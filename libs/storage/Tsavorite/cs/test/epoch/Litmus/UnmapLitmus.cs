// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>Outcome of an <see cref="UnmapLitmus"/> run.</summary>
    internal sealed class UnmapLitmusResult
    {
        /// <summary>
        /// Rounds in which the reclaimer, at the instant it freed a page, saw a reader
        /// recorded as being inside that same page. This is the use-after-free condition
        /// itself, rather than a fault that merely implies it.
        /// </summary>
        internal long TripwireHits { get; init; }

        /// <summary>
        /// Hits from the self-test, which samples the same condition at retire time (when
        /// nothing has been freed yet). A self-test reporting 0 means the detector is blind
        /// on this machine and every "0 hits" verdict is void.
        /// </summary>
        internal long SelfTestHits { get; init; }

        /// <summary>Pages the epoch actually decided were safe to unmap.</summary>
        internal long FreedPages { get; init; }

        /// <summary>Rounds the reclaimer completed.</summary>
        internal long Rounds { get; init; }

        internal long FirstHitReaderEpoch { get; init; }
        internal long FirstHitTriggerEpoch { get; init; }
        internal long FirstHitSafeToReclaimEpoch { get; init; }

        internal TimeSpan Elapsed { get; init; }

        public override string ToString()
            => $"tripwireHits={TripwireHits:N0} selfTestHits={SelfTestHits:N0} freedPages={FreedPages:N0} "
             + $"rounds={Rounds:N0} elapsed={Elapsed.TotalSeconds:F1}s"
             + (TripwireHits == 0 ? string.Empty
                 : $" firstHit(readerEpoch={FirstHitReaderEpoch} trigger={FirstHitTriggerEpoch} safeToReclaim={FirstHitSafeToReclaimEpoch})");
    }

    /// <summary>
    /// Two-sided Store-Buffer litmus over one <see cref="LightEpoch"/> instance, where a
    /// reclaimed page is genuinely unmapped so a use-after-free becomes a hardware access
    /// violation.
    ///
    /// This is the sensitive mode on ARM64, which broadcasts TLB maintenance in hardware
    /// (TLBI ... IS) so no core is interrupted. On x86-64 there is no architectural broadcast
    /// invalidation: the kernel sends a shootdown IPI to every core holding the mapping, and
    /// taking an interrupt on x86 drains that core's store buffer, so the reader is fenced by
    /// the OS on every round and the race cannot be observed. A clean result on x86 is
    /// therefore weak evidence — use <see cref="QuarantineLitmus"/> there.
    ///
    /// Detection is by tripwire rather than by waiting for the fault. A crash is weak
    /// evidence: it can be missed entirely when the address stays mapped, it says nothing
    /// about why the free was authorised, and in a test host it takes the whole process down.
    /// The tripwire records which page the reader is inside and has the reclaimer sample that
    /// at the instant it frees, capturing the epoch state alongside — so a hit distinguishes a
    /// genuine epoch-protocol violation from a defect in this harness. The loads and stores
    /// are deliberately plain, since making them interlocked would add the very ordering under
    /// test; misses are expected and acceptable, a hit is proof.
    /// </summary>
    internal sealed unsafe class UnmapLitmus
    {
        const nuint PageSize = 4096;
        const int WordsPerPage = (int)(PageSize / sizeof(long));

        readonly LightEpoch epoch = new();
        readonly LitmusRendezvous rendezvous = new();
        readonly TimeSpan duration;
        readonly int deref;
        readonly LitmusCores cores;
        readonly bool selfTest;

        long curPage;
        long sink;
        long frees;
        long readerActivePage;
        long readerActiveEpoch;
        long tripwireHits;
        long selfTestHits;
        long firstHitTrigger = -1;
        long firstHitSafeToReclaim = -1;
        long firstHitReaderEpoch = -1;

        internal UnmapLitmus(TimeSpan duration, int deref, LitmusCores cores, bool selfTest = false)
        {
            this.duration = duration;
            this.deref = deref;
            this.cores = cores;
            this.selfTest = selfTest;
        }

        internal UnmapLitmusResult Run()
        {
            try
            {
                var reader = new Thread(ReaderLoop) { IsBackground = true, Name = "unmap-litmus-reader", Priority = ThreadPriority.Highest };
                reader.Start();

                _ = LitmusNative.TryPin(cores.ReclaimerCore);

                var stopwatch = Stopwatch.StartNew();
                var rounds = ReclaimerLoop();
                stopwatch.Stop();

                _ = reader.Join(5000);

                return new UnmapLitmusResult
                {
                    TripwireHits = Volatile.Read(ref tripwireHits),
                    SelfTestHits = Volatile.Read(ref selfTestHits),
                    FreedPages = Volatile.Read(ref frees),
                    Rounds = rounds,
                    FirstHitReaderEpoch = firstHitReaderEpoch,
                    FirstHitTriggerEpoch = firstHitTrigger,
                    FirstHitSafeToReclaimEpoch = firstHitSafeToReclaim,
                    Elapsed = stopwatch.Elapsed
                };
            }
            finally
            {
                epoch.Dispose();
            }
        }

        void ReaderLoop()
        {
            _ = LitmusNative.TryPin(cores.ReaderCore);

            while (true)
            {
                rendezvous.StartBarrier();
                if (rendezvous.Stop)
                {
                    rendezvous.EndBarrier();
                    return;
                }

                epoch.Resume();
                epoch.ProtectAndDrain();

                ReadAndDeref();

                epoch.Suspend();
                rendezvous.EndBarrier();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReadAndDeref()
        {
            var pageAddress = curPage;
            if (pageAddress == 0)
                return;

            readerActiveEpoch = epoch.ThisThreadAnnouncedEpoch();
            readerActivePage = pageAddress;

            var page = (long*)pageAddress;
            long accumulator = 0;
            for (var index = 0; index < deref; index++)
                accumulator += page[index & (WordsPerPage - 1)];
            sink += accumulator;

            readerActivePage = 0;
        }

        long ReclaimerLoop()
        {
            epoch.Resume();

            var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
            long round = 0;
            while (Environment.TickCount64 < deadline)
            {
                var page = LitmusNative.MapPage(PageSize);
                var words = (long*)page;
                for (var index = 0; index < WordsPerPage; index++)
                    words[index] = index;
                Volatile.Write(ref curPage, (long)page);

                rendezvous.StartBarrier();

                curPage = 0;
                var pageAddress = (long)page;
                var triggerEpoch = epoch.CurrentEpoch;

                // Detector sensitivity control: sample the tripwire condition at the instant the
                // page is retired rather than the instant it is reclaimed. Nothing is freed
                // early, so this measures only whether the detector can fire at all.
                if (selfTest && Volatile.Read(ref readerActivePage) == pageAddress)
                    _ = Interlocked.Increment(ref selfTestHits);

                epoch.BumpCurrentEpoch(() =>
                {
                    if (Volatile.Read(ref readerActivePage) == pageAddress && Interlocked.Increment(ref tripwireHits) == 1)
                    {
                        firstHitTrigger = triggerEpoch;
                        firstHitSafeToReclaim = epoch.SafeToReclaimEpoch;
                        firstHitReaderEpoch = Volatile.Read(ref readerActiveEpoch);
                    }

                    LitmusNative.Unmap((byte*)pageAddress, PageSize);
                    _ = Interlocked.Increment(ref frees);
                });
                epoch.ProtectAndDrain();

                rendezvous.EndBarrier();
                round++;
            }

            rendezvous.Shutdown();
            epoch.Suspend();
            return round;
        }
    }
}
