// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        /// <summary>Pages retired via <see cref="IEpochUnderTest.BumpCurrentEpoch(Action)"/>.</summary>
        internal long Drains { get; init; }

        /// <summary>Pages the epoch actually decided were safe to recycle.</summary>
        internal long Quarantines { get; init; }

        internal TimeSpan Elapsed { get; init; }

        public override string ToString()
            => $"violations={Violations:N0} sampledRounds={SampledRounds:N0} rounds={Rounds:N0} "
             + $"drains={Drains:N0} quarantined={Quarantines:N0} elapsed={Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Shared counters. Reader and reclaimer counters get separate cache lines, and
    /// <see cref="CurPage"/> -- which the reader loads every round -- gets a third.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * Line)]
    internal struct Counters
    {
        const int Line = 128;

        [FieldOffset(0)] internal long ObservedPages;
        [FieldOffset(8)] internal long Sink;
        [FieldOffset(16)] internal long Violations;

        [FieldOffset(Line)] internal long CurPage;

        [FieldOffset(2 * Line)] internal long Drains;
        [FieldOffset((2 * Line) + 8)] internal long Quarantines;
    }

    /// <summary>
    /// Store-buffer litmus over one epoch instance, detecting a use-after-free logically rather
    /// than by hardware fault. <typeparamref name="TEpoch"/> selects the epoch under test:
    /// <see cref="FixedEpoch"/> is expected to pass, <see cref="BuggyEpoch"/> to fail.
    ///
    /// The reader announces its epoch then loads a shared page pointer; the reclaimer unlinks that
    /// pointer and retires the page. If the reclaimer's scan misses the announce, the epoch
    /// authorises the free while the reader is inside the page. "Freeing" stamps a poison sentinel,
    /// so a reader that observes poison in a page it was protecting is a use-after-free.
    ///
    /// Pages are pooled and never unmapped, so no round allocates or enters the kernel.
    /// </summary>
    internal sealed unsafe class QuarantineLitmus<TEpoch> where TEpoch : struct, IEpochUnderTest
    {
        const nuint PageSize = 4096;
        const int PoolPages = 1024;
        const int WordsPerPage = (int)(PageSize / sizeof(long));
        const long Poison = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);

        private readonly TEpoch epoch;
        private readonly TwoThreadBarrier barrier = new();
        private readonly TimeSpan duration;
        private readonly int deref;
        private readonly CoreLayout cores;
        private readonly bool selfTest;

        // BumpCurrentEpoch defers the callback, so it must stay bound to the page that was retired.
        // Building them once keeps that binding without allocating in the race loop.
        private readonly Action[] drainCallbacks = new Action[PoolPages];

        private byte* pool;
        private Counters* counters;

        // One extra page for the counters, page-aligned and outside every page the reclaimer poisons.
        const nuint MappedBytes = (PageSize * PoolPages) + PageSize;

        private ref long ObservedPages => ref counters->ObservedPages;
        private ref long Sink => ref counters->Sink;
        private ref long Violations => ref counters->Violations;
        private ref long CurPage => ref counters->CurPage;
        private ref long Drains => ref counters->Drains;
        private ref long Quarantines => ref counters->Quarantines;

        internal QuarantineLitmus(TEpoch epoch, TimeSpan duration, int deref, CoreLayout cores, bool selfTest = false)
        {
            this.epoch = epoch;
            this.duration = duration;
            this.deref = deref;
            this.cores = cores;
            this.selfTest = selfTest;
        }

        internal QuarantineLitmusResult Run()
        {
            pool = Platform.MapPage(MappedBytes);
            counters = (Counters*)(pool + (PageSize * PoolPages));
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
                // They keep its cache lines shared, so an announce must first win an RFO -- and
                // since x86 store buffers commit in order, that pins the announce in the buffer
                // long enough for a missing StoreLoad fence to show.
                var disturbers = new Thread[cores.DisturberCores.Length];
                for (var i = 0; i < disturbers.Length; i++)
                {
                    var core = cores.DisturberCores[i];
                    disturbers[i] = new Thread(() => DisturberLoop(core)) { IsBackground = true, Name = $"litmus-disturber{core}" };
                    disturbers[i].Start();
                }

                _ = Platform.TryPin(cores.ReclaimerCore);

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
                Platform.Unmap(pool, MappedBytes);
                epoch.Dispose();
            }
        }

        void DisturberLoop(int core)
        {
            _ = Platform.TryPin(core);

            long local = 0;
            while (!barrier.Stop)
            {
                for (var i = 1; i <= epoch.EntryCount; i++)
                    local += epoch.TestHookAnnouncedEpochAt(i);
            }

            _ = Interlocked.Add(ref Sink, local);
        }

        void ReaderLoop()
        {
            _ = Platform.TryPin(cores.ReaderCore);

            while (true)
            {
                barrier.WaitAtStart();

                // Nothing may sit between the barrier and Resume(). The window is a few instructions
                // wide, and arriving late drains the announce out of the store buffer before the
                // reclaimer scans -- a single extra volatile load here was measured to be the
                // difference between catching the unfixed epoch and catching nothing. Hence the
                // shutdown check lives after WaitAtEnd.
                //
                // Resume-then-ProtectAndDrain mirrors ClientSession.UnsafeResumeThread, which calls
                // Resume and then InternalRefresh.
                epoch.Resume();
                epoch.ProtectAndDrain();

                ReadAndCheck();

                epoch.Suspend();
                barrier.WaitAtEnd();

                // After WaitAtEnd so it stays out of the window above. Depart() because the
                // reclaimer's Shutdown may already be waiting in a pass this thread will not enter.
                if (barrier.Stop)
                {
                    barrier.Depart();
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
        /// refreshes an epoch every round, the way Tsavorite drives it in production.
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

                barrier.WaitAtStart();

                CurPage = 0;

                // Poison unconditionally, as if the epoch had wrongly cleared the page every round.
                if (selfTest)
                    Quarantine((long)page);

                epoch.BumpCurrentEpoch(drainCallbacks[round % PoolPages]);
                epoch.ProtectAndDrain();
                Drains++;
                barrier.WaitAtEnd();
                round++;
            }

            barrier.Shutdown();
            epoch.Suspend();
            return round;
        }

        /// <summary>Stands in for the unmap: stamping the page destroys any value a still-protected reader could legitimately see.</summary>
        void Quarantine(long page)
        {
            _ = Interlocked.Increment(ref Quarantines);
            var words = (long*)page;
            for (var index = 0; index < WordsPerPage; index++)
                words[index] = Poison;
        }
    }
}