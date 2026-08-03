// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.epoch.litmus
{
    /// <summary>Outcome of a <see cref="QuarantineLitmus{TEpoch}"/> run.</summary>
    internal sealed class QuarantineLitmusResult
    {
        /// <summary>Rounds in which the reader dereferenced a page it had been poisoned under.</summary>
        internal long Violations { get; init; }

        /// <summary>
        /// Rounds in which the reader captured a live page pointer. If this is 0 the race
        /// window was never sampled and a clean result says nothing.
        /// </summary>
        internal long SampledRounds { get; init; }

        /// <summary>Rounds the reclaimer completed.</summary>
        internal long Rounds { get; init; }

        /// <summary>Pages retired via <see cref="ILitmusEpoch.BumpCurrentEpoch(Action)"/>.</summary>
        internal long Drains { get; init; }

        /// <summary>Pages the epoch actually decided were safe to recycle.</summary>
        internal long Quarantines { get; init; }

        internal TimeSpan Elapsed { get; init; }

        public override string ToString()
            => $"violations={Violations:N0} sampledRounds={SampledRounds:N0} rounds={Rounds:N0} "
             + $"drains={Drains:N0} quarantined={Quarantines:N0} elapsed={Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Store-Buffer litmus over one epoch instance, detecting a use-after-free logically rather
    /// than by hardware fault. <typeparamref name="TEpoch"/> selects which epoch is under test:
    /// <see cref="FixedEpoch"/> is expected to pass, <see cref="BuggyEpoch"/> to fail.
    ///
    /// The reader announces its epoch then loads a shared page pointer; the reclaimer unlinks that
    /// pointer and retires the page. If the reclaimer's scan misses the announce, the epoch
    /// authorises the free while the reader is inside the page. "Freeing" stamps a poison sentinel,
    /// so a reader observing poison in a page it was protecting is a use-after-free by the
    /// algorithm's own definition.
    ///
    /// Nothing is unmapped, which is what makes this work on x86-64: an unmap sends a TLB shootdown
    /// IPI, and taking an interrupt drains the interrupted core's store buffer, fencing the reader
    /// on every round. Pages and drain callbacks are pre-allocated so no round allocates.
    /// </summary>
    internal sealed unsafe class QuarantineLitmus<TEpoch> where TEpoch : struct, ILitmusEpoch
    {
        const nuint PageSize = 4096;
        const int PoolPages = 1024;
        const int WordsPerPage = (int)(PageSize / sizeof(long));
        const long Poison = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);

        readonly TEpoch epoch;
        readonly LitmusRendezvous rendezvous = new();
        readonly TimeSpan duration;
        readonly int deref;
        readonly LitmusCores cores;
        readonly bool selfTest;

        // One cached delegate per pool slot. BumpCurrentEpoch defers the callback until the retired
        // epoch is safe, so it must stay bound to the page that was retired; building them once
        // keeps that binding without allocating in the race loop.
        readonly Action[] drainCallbacks = new Action[PoolPages];

        byte* pool;
        byte* counters;

        // Reader and reclaimer counters get separate lines, and curPage -- the reclaimer-to-reader
        // channel the reader loads every round -- gets a third, so no RFO lands on the loop this
        // harness is timing.
        const nuint CounterLine = 128;

        // One extra page for the counters, page-aligned and outside every page the reclaimer poisons.
        const nuint MappedBytes = (PageSize * PoolPages) + PageSize;

        ref long ObservedPages => ref *(long*)counters;
        ref long Sink => ref *(long*)(counters + 8);
        ref long Violations => ref *(long*)(counters + 16);

        ref long CurPage => ref *(long*)(counters + CounterLine);

        ref long Drains => ref *(long*)(counters + (2 * CounterLine));
        ref long Quarantines => ref *(long*)(counters + (2 * CounterLine) + 8);

        internal QuarantineLitmus(TEpoch epoch, TimeSpan duration, int deref, LitmusCores cores, bool selfTest = false)
        {
            this.epoch = epoch;
            this.duration = duration;
            this.deref = deref;
            this.cores = cores;
            this.selfTest = selfTest;
        }

        internal QuarantineLitmusResult Run()
        {
            pool = LitmusNative.MapPage(MappedBytes);
            counters = pool + (PageSize * PoolPages);
            try
            {
                for (var slot = 0; slot < PoolPages; slot++)
                {
                    var page = (long)(pool + ((nuint)slot * PageSize));
                    drainCallbacks[slot] = () => Quarantine(page);
                }

                var reader = new Thread(ReaderLoop) { IsBackground = true, Name = "litmus-reader", Priority = ThreadPriority.Highest };
                reader.Start();

                // Disturbers only read the epoch table, so they cannot influence any epoch decision.
                // They keep its cache lines shared rather than exclusively owned, so an announce
                // must first win an RFO -- and since x86 store buffers commit in order, that pins
                // the announce in the buffer long enough for a missing StoreLoad fence to show.
                var disturbers = new Thread[cores.DisturberCores.Length];
                for (var i = 0; i < disturbers.Length; i++)
                {
                    var core = cores.DisturberCores[i];
                    disturbers[i] = new Thread(() => DisturberLoop(core)) { IsBackground = true, Name = $"litmus-disturber{core}" };
                    disturbers[i].Start();
                }

                _ = LitmusNative.TryPin(cores.ReclaimerCore);

                var stopwatch = Stopwatch.StartNew();
                var rounds = ReclaimerLoop();
                stopwatch.Stop();

                _ = reader.Join(5000);
                foreach (var disturber in disturbers)
                    _ = disturber.Join(5000);

                return new QuarantineLitmusResult
                {
                    Violations = Volatile.Read(ref Violations),
                    SampledRounds = Volatile.Read(ref ObservedPages),
                    Rounds = rounds,
                    Drains = Volatile.Read(ref Drains),
                    Quarantines = Volatile.Read(ref Quarantines),
                    Elapsed = stopwatch.Elapsed
                };
            }
            finally
            {
                LitmusNative.Unmap(pool, MappedBytes);
                epoch.Dispose();
            }
        }

        void DisturberLoop(int core)
        {
            _ = LitmusNative.TryPin(core);

            long local = 0;
            while (!rendezvous.Stop)
            {
                for (var i = 1; i <= epoch.EntryCount; i++)
                    local += epoch.TestHookAnnouncedEpochAt(i);
            }

            _ = Interlocked.Add(ref Sink, local);
        }

        void ReaderLoop()
        {
            _ = LitmusNative.TryPin(cores.ReaderCore);

            while (true)
            {
                rendezvous.StartBarrier();

                // Nothing may sit between the barrier and Resume(). The window is a few instructions
                // wide, and arriving late drains the announce out of the store buffer before the
                // reclaimer scans, so no run length produces a violation -- a single extra volatile
                // load here was measured to be the difference between catching the unfixed epoch
                // and catching nothing. Hence the shutdown check lives after EndBarrier.
                //
                // Resume-then-Refresh mirrors a normal Tsavorite BasicContext operation:
                // ClientSession.UnsafeResumeThread calls Resume and then InternalRefresh, which
                // begins with ProtectAndDrain.
                epoch.Resume();
                epoch.ProtectAndDrain();

                ReadAndCheck();

                epoch.Suspend();
                rendezvous.EndBarrier();

                // After EndBarrier so it stays out of the window above. Depart() because the
                // reclaimer's Shutdown may already be waiting in a pass this thread will not enter.
                if (rendezvous.Stop)
                {
                    rendezvous.Depart();
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReadAndCheck()
        {
            var pageAddress = CurPage;
            if (pageAddress == 0)
                return;

            ObservedPages++;

            var page = (long*)pageAddress;
            long accumulator = 0;
            var poisoned = false;
            for (var index = 0; index < deref; index++)
            {
                var value = page[index & (WordsPerPage - 1)];
                poisoned |= value == Poison;
                accumulator += value;
            }

            Sink += accumulator;

            if (poisoned)
                _ = Interlocked.Increment(ref Violations);
        }

        /// <summary>
        /// BumpCurrentEpoch asserts ThisInstanceProtected(), so the retiring thread holds and
        /// refreshes an epoch every round, the way Tsavorite drives it in production. Retiring
        /// unprotected would widen the race window past anything real code can produce.
        /// </summary>
        long ReclaimerLoop()
        {
            epoch.Resume();

            var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
            long round = 0;
            while (Environment.TickCount64 < deadline)
            {
                var page = pool + ((nuint)(round % PoolPages) * PageSize);
                var words = (long*)page;
                for (var index = 0; index < WordsPerPage; index++)
                    words[index] = index;
                Volatile.Write(ref CurPage, (long)page);

                rendezvous.StartBarrier();

                CurPage = 0;

                // Self-test: poison unconditionally, as if the epoch had wrongly cleared the page
                // every round, proving the detection path can fire on this machine.
                if (selfTest)
                    Quarantine((long)page);

                epoch.BumpCurrentEpoch(drainCallbacks[round % PoolPages]);
                epoch.ProtectAndDrain();
                Drains++;
                rendezvous.EndBarrier();
                round++;
            }

            rendezvous.Shutdown();
            epoch.Suspend();
            return round;
        }

        /// <summary>
        /// Stands in for the unmap: the epoch says this page is recyclable, so stamping it destroys
        /// any value a still-protected reader could legitimately see.
        /// </summary>
        void Quarantine(long page)
        {
            _ = Interlocked.Increment(ref Quarantines);
            var words = (long*)page;
            for (var index = 0; index < WordsPerPage; index++)
                words[index] = Poison;
        }
    }
}