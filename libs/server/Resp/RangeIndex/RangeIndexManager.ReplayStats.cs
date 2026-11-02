// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Where the time of RangeIndex AOF replay goes. Replay handlers time the stub read (which includes every cold path of
    /// <c>ReadRangeIndex</c>) and the native operation; the cold paths count their own share wherever they run, so
    /// non-replay readers that hit one during replay also show up in the cold-path totals.
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>Interval of the periodic <c>[RangeIndexReplayProgress]</c> line.</summary>
        internal static readonly long ReplayProgressIntervalTicks = Stopwatch.Frequency * 10;

        /// <summary>Cumulative time counters of RangeIndex replay and of the cold paths of <c>ReadRangeIndex</c>.</summary>
        internal struct PhaseStats
        {
            public long Ops, StubReadTicks, NativeTicks, SlowOps;
            public long CheckpointWaits, CheckpointWaitTicks;
            public long Promotes, PromoteTicks, PreStageCopies, PreStageCopyTicks, PreStageCopyBytes;
            public long Restores, RestoreTicks, StageCopies, StageCopyTicks, StageCopyBytes, PrefetchTakes, PrefetchWaitTicks, OpenTicks, StubUpdateTicks;

            public readonly PhaseStats Minus(in PhaseStats other) => new()
            {
                Ops = Ops - other.Ops,
                StubReadTicks = StubReadTicks - other.StubReadTicks,
                NativeTicks = NativeTicks - other.NativeTicks,
                SlowOps = SlowOps - other.SlowOps,
                CheckpointWaits = CheckpointWaits - other.CheckpointWaits,
                CheckpointWaitTicks = CheckpointWaitTicks - other.CheckpointWaitTicks,
                Promotes = Promotes - other.Promotes,
                PromoteTicks = PromoteTicks - other.PromoteTicks,
                PreStageCopies = PreStageCopies - other.PreStageCopies,
                PreStageCopyTicks = PreStageCopyTicks - other.PreStageCopyTicks,
                PreStageCopyBytes = PreStageCopyBytes - other.PreStageCopyBytes,
                Restores = Restores - other.Restores,
                RestoreTicks = RestoreTicks - other.RestoreTicks,
                StageCopies = StageCopies - other.StageCopies,
                StageCopyTicks = StageCopyTicks - other.StageCopyTicks,
                StageCopyBytes = StageCopyBytes - other.StageCopyBytes,
                PrefetchTakes = PrefetchTakes - other.PrefetchTakes,
                PrefetchWaitTicks = PrefetchWaitTicks - other.PrefetchWaitTicks,
                OpenTicks = OpenTicks - other.OpenTicks,
                StubUpdateTicks = StubUpdateTicks - other.StubUpdateTicks,
            };
        }

        /// <summary>A replay op at least this slow is counted as slow.</summary>
        internal static readonly long SlowReplayOpTicks = Stopwatch.Frequency / 5;

        private PhaseStats phaseStats;
        private PhaseStats replayStatsBaseline;
        private PhaseStats replayProgressBaseline;
        private long replayStatsStartTicks;
        private long replayProgressTicks;

        /// <summary>Reads a consistent-enough copy of the counters; each field is read atomically.</summary>
        internal PhaseStats ReadPhaseStats() => new()
        {
            Ops = Interlocked.Read(ref phaseStats.Ops),
            StubReadTicks = Interlocked.Read(ref phaseStats.StubReadTicks),
            NativeTicks = Interlocked.Read(ref phaseStats.NativeTicks),
            SlowOps = Interlocked.Read(ref phaseStats.SlowOps),
            CheckpointWaits = Interlocked.Read(ref phaseStats.CheckpointWaits),
            CheckpointWaitTicks = Interlocked.Read(ref phaseStats.CheckpointWaitTicks),
            Promotes = Interlocked.Read(ref phaseStats.Promotes),
            PromoteTicks = Interlocked.Read(ref phaseStats.PromoteTicks),
            PreStageCopies = Interlocked.Read(ref phaseStats.PreStageCopies),
            PreStageCopyTicks = Interlocked.Read(ref phaseStats.PreStageCopyTicks),
            PreStageCopyBytes = Interlocked.Read(ref phaseStats.PreStageCopyBytes),
            Restores = Interlocked.Read(ref phaseStats.Restores),
            RestoreTicks = Interlocked.Read(ref phaseStats.RestoreTicks),
            StageCopies = Interlocked.Read(ref phaseStats.StageCopies),
            StageCopyTicks = Interlocked.Read(ref phaseStats.StageCopyTicks),
            StageCopyBytes = Interlocked.Read(ref phaseStats.StageCopyBytes),
            PrefetchTakes = Interlocked.Read(ref phaseStats.PrefetchTakes),
            PrefetchWaitTicks = Interlocked.Read(ref phaseStats.PrefetchWaitTicks),
            OpenTicks = Interlocked.Read(ref phaseStats.OpenTicks),
            StubUpdateTicks = Interlocked.Read(ref phaseStats.StubUpdateTicks),
        };

        /// <summary>Starts a replay measurement window, reported by <see cref="LogReplayStats"/>.</summary>
        internal void BeginReplayStats()
        {
            var now = ReadPhaseStats();
            replayStatsBaseline = now;
            replayProgressBaseline = now;
            var ticks = Stopwatch.GetTimestamp();
            Volatile.Write(ref replayStatsStartTicks, ticks);
            Volatile.Write(ref replayProgressTicks, ticks);
        }

        /// <summary>Logs the replay phase totals since <see cref="BeginReplayStats"/>.</summary>
        internal void LogReplayStats(string reason)
        {
            var elapsed = Stopwatch.GetTimestamp() - Volatile.Read(ref replayStatsStartTicks);
            LogPhaseStats("[RangeIndexReplayStats]", reason, ReadPhaseStats().Minus(replayStatsBaseline), elapsed);
        }

        private void RecordReplayOp(long stubReadTicks, long nativeTicks)
        {
            _ = Interlocked.Increment(ref phaseStats.Ops);
            _ = Interlocked.Add(ref phaseStats.StubReadTicks, stubReadTicks);
            _ = Interlocked.Add(ref phaseStats.NativeTicks, nativeTicks);
            if (stubReadTicks + nativeTicks >= SlowReplayOpTicks)
                _ = Interlocked.Increment(ref phaseStats.SlowOps);

            var now = Stopwatch.GetTimestamp();
            var last = Volatile.Read(ref replayProgressTicks);
            if (now - last < ReplayProgressIntervalTicks || Interlocked.CompareExchange(ref replayProgressTicks, now, last) != last)
                return;

            var current = ReadPhaseStats();
            var delta = current.Minus(replayProgressBaseline);
            replayProgressBaseline = current;
            LogPhaseStats("[RangeIndexReplayProgress]", "interval", delta, now - last);
        }

        private void LogPhaseStats(string tag, string reason, in PhaseStats s, long elapsedTicks)
        {
            static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var cold = s.CheckpointWaitTicks + s.PromoteTicks + s.RestoreTicks;
            logger?.LogInformation("{Tag} reason={Reason} elapsedMs={Elapsed:F0} ops={Ops} slowOps={Slow} stubReadMs={StubRead:F0} hotReadMs={HotRead:F0} nativeMs={Native:F0} " +
                "checkpointWaits={CpWaits} checkpointWaitMs={CpWaitMs:F0} promotes={Promotes} promoteMs={PromoteMs:F0} preStageCopies={PreStageCopies} preStageCopyMs={PreStageCopyMs:F0} preStageCopyMB={PreStageCopyMB:F0} " +
                "restores={Restores} restoreMs={RestoreMs:F0} stageCopies={StageCopies} stageCopyMs={StageCopyMs:F0} stageCopyMB={StageCopyMB:F0} prefetchTakes={PrefetchTakes} prefetchWaitMs={PrefetchWaitMs:F0} openMs={OpenMs:F0} stubUpdateMs={StubUpdateMs:F0}",
                tag, reason, Ms(elapsedTicks), s.Ops, s.SlowOps, Ms(s.StubReadTicks), Ms(Math.Max(0, s.StubReadTicks - cold)), Ms(s.NativeTicks),
                s.CheckpointWaits, Ms(s.CheckpointWaitTicks), s.Promotes, Ms(s.PromoteTicks), s.PreStageCopies, Ms(s.PreStageCopyTicks), s.PreStageCopyBytes / 1048576.0,
                s.Restores, Ms(s.RestoreTicks), s.StageCopies, Ms(s.StageCopyTicks), s.StageCopyBytes / 1048576.0, s.PrefetchTakes, Ms(s.PrefetchWaitTicks), Ms(s.OpenTicks), Ms(s.StubUpdateTicks));
        }
    }
}