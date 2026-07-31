// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Tsavorite.core;

namespace Tsavorite.epoch.litmus
{
    /// <summary>Outcome of a <see cref="QuarantineLitmus"/> run.</summary>
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

        /// <summary>Pages retired via <see cref="LightEpoch.BumpCurrentEpoch(Action)"/>.</summary>
        internal long Drains { get; init; }

        /// <summary>Pages the epoch actually decided were safe to recycle.</summary>
        internal long Quarantines { get; init; }

        internal TimeSpan Elapsed { get; init; }

        public override string ToString()
            => $"violations={Violations:N0} sampledRounds={SampledRounds:N0} rounds={Rounds:N0} "
             + $"drains={Drains:N0} quarantined={Quarantines:N0} elapsed={Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Store-Buffer litmus over one <see cref="LightEpoch"/> instance, detecting a
    /// use-after-free logically rather than by hardware fault.
    ///
    /// The reader announces its epoch and then loads a shared page pointer; the reclaimer
    /// unlinks that pointer and asks the epoch to retire the page. If the reclaimer's scan
    /// misses the reader's announce, the epoch authorises the free while the reader is
    /// inside the page. "Freeing" stamps a poison sentinel over the page, so a reader that
    /// observes poison in a page it was protecting is a use-after-free by the algorithm's
    /// own definition.
    ///
    /// This detects the race without unmapping anything, which is what makes it work on x86-64:
    /// an unmap would send a TLB shootdown IPI, and taking an interrupt drains the interrupted
    /// core's store buffer, so the OS would fence the reader on every round and the race could
    /// never appear. Pages come from a pool allocated once and the drain callbacks are pre-built,
    /// so nothing allocates per round.
    /// </summary>
    internal sealed unsafe class QuarantineLitmus
    {
        const nuint PageSize = 4096;
        const int PoolPages = 1024;
        const int WordsPerPage = (int)(PageSize / sizeof(long));
        const long Poison = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);

        readonly LightEpoch epoch = new();
        readonly LitmusRendezvous rendezvous = new();
        readonly TimeSpan duration;
        readonly int deref;
        readonly LitmusCores cores;
        readonly bool selfTest;

        // One cached delegate per pool slot. BumpCurrentEpoch defers the drain callback until
        // the epoch decides the retired epoch is safe, which can be several rounds later, so
        // the callback must be bound to the page that was actually retired. Building them once
        // keeps that binding without allocating inside the race loop.
        readonly Action[] drainCallbacks = new Action[PoolPages];

        byte* pool;
        byte* counters;

        // The reader and the reclaimer each increment their own counters every round, so sharing a
        // cache line between them would put an RFO on the critical path of the very loop whose
        // timing this harness measures. Each side gets its own line out of a dedicated page, and
        // curPage -- the reclaimer-to-reader channel, which the reader loads every round -- gets a
        // third, so reading it does not drag either side's counters along with it.
        const nuint CounterLine = 128;

        // The mapping carries one extra page for the counters, so it is page-aligned and outside
        // every pool page the reclaimer poisons.
        const nuint MappedBytes = (PageSize * PoolPages) + PageSize;

        ref long ObservedPages => ref *(long*)counters;
        ref long Sink => ref *(long*)(counters + 8);
        ref long Violations => ref *(long*)(counters + 16);

        ref long CurPage => ref *(long*)(counters + CounterLine);

        ref long Drains => ref *(long*)(counters + (2 * CounterLine));
        ref long Quarantines => ref *(long*)(counters + (2 * CounterLine) + 8);

        internal QuarantineLitmus(TimeSpan duration, int deref, LitmusCores cores, bool selfTest = false)
        {
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

                // Disturber threads only read the epoch table, so they cannot influence any epoch
                // decision. Their job is to keep the table's cache lines shared rather than
                // exclusively owned by the announcing thread: an announce into a shared line must
                // first win an RFO, and since x86 store buffers commit in order, that pins the
                // announce in the buffer long enough for a missing StoreLoad fence to show.
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
                    local += epoch.AnnouncedEpochAt(i);
            }

            _ = Interlocked.Add(ref Sink, local);
        }

        void ReaderLoop()
        {
            _ = LitmusNative.TryPin(cores.ReaderCore);

            while (true)
            {
                rendezvous.StartBarrier();

                // Nothing may sit between the barrier and Resume(). The window this test samples
                // is a few instructions wide, and the reader's position in it decides everything:
                // arriving even slightly late drains the announce out of the store buffer before
                // the reclaimer scans, so the scan always sees it and no run length produces a
                // violation. A single extra volatile load here was measured to be the difference
                // between catching the unfixed epoch and catching nothing, which is why the
                // shutdown check lives after EndBarrier instead.
                //
                // Resume-then-Refresh mirrors a normal Tsavorite BasicContext operation:
                // ClientSession.UnsafeResumeThread calls Resume and then InternalRefresh, which
                // begins with ProtectAndDrain.
                epoch.Resume();
                epoch.ProtectAndDrain();

                ReadAndCheck();

                epoch.Suspend();
                rendezvous.EndBarrier();

                // The shutdown check goes after EndBarrier so it stays out of the window above.
                // The reclaimer's Shutdown does one final barrier pass, so this thread is never
                // left waiting on a partner that has gone.
                if (rendezvous.Stop)
                    return;
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
        /// BumpCurrentEpoch asserts ThisInstanceProtected(), so the retiring thread holds an
        /// epoch and refreshes it every round, the way Tsavorite drives the epoch in production.
        /// Retiring from an unprotected thread would leave it out of the safe-epoch scan and
        /// widen the race window past anything real code can produce.
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

                // Self-test: poison unconditionally, as if the epoch had wrongly decided the page
                // was reclaimable on every round. Any reader that captured the pointer must then
                // observe poison, so this proves the detection path can fire on this machine.
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
        /// Stands in for the unmap: the epoch has decided this page is safe to recycle, so
        /// stamping it destroys any value a still-protected reader could legitimately see.
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