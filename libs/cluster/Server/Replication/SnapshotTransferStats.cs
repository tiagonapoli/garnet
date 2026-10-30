// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Garnet.cluster
{
    /// <summary>
    /// Diagnostic timing, GC and thread-pool counters for one snapshot file transfer (sender or receiver side).
    /// </summary>
    internal sealed class SnapshotTransferStats
    {
        readonly long startTimestamp = Stopwatch.GetTimestamp();
        readonly int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        readonly long allocatedBytes = GC.GetTotalAllocatedBytes(false);
        readonly TimeSpan pauseDuration = GC.GetTotalPauseDuration();

        long readTicks, sendTicks, writeTicks, gapTicks, maxSendTicks, maxGapTicks, lastChunkEnd;

        public long Chunks { get; private set; }
        public long Bytes { get; private set; }

        public void AddRead(long elapsedTicks) => readTicks += elapsedTicks;

        public void AddSend(long elapsedTicks, int bytes)
        {
            sendTicks += elapsedTicks;
            maxSendTicks = Math.Max(maxSendTicks, elapsedTicks);
            Chunks++;
            Bytes += bytes;
        }

        /// <summary>Records a received chunk; the gap is the idle time since the previous chunk was processed (network plus sender time).</summary>
        public void AddReceive(long chunkStart, long chunkEnd, int bytes)
        {
            if (lastChunkEnd != 0)
            {
                var gap = chunkStart - lastChunkEnd;
                gapTicks += gap;
                maxGapTicks = Math.Max(maxGapTicks, gap);
            }

            writeTicks += chunkEnd - chunkStart;
            lastChunkEnd = chunkEnd;
            Chunks++;
            Bytes += bytes;
        }

        public void Log(ILogger logger, string side, CheckpointFileType type, string name)
        {
            if (logger == null)
                return;

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var gcInfo = GC.GetGCMemoryInfo();
            ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);

            logger.LogInformation(
                "[SnapshotTransferStats] side={side} type={type} file={name} bytes={bytes} chunks={chunks} elapsedMs={elapsedMs:F0} MBps={mbps:F1} " +
                "readMs={readMs:F0} sendWaitMs={sendMs:F0} avgRttMs={avgRtt:F3} maxRttMs={maxRtt:F1} writeMs={writeMs:F0} gapMs={gapMs:F0} avgGapMs={avgGap:F3} maxGapMs={maxGap:F1} " +
                "gc0={gc0} gc1={gc1} gc2={gc2} allocMB={allocMB:F1} gcPauseMs={pauseMs:F0} heapMB={heapMB:F0} committedMB={committedMB:F0} " +
                "tpThreads={tpThreads} tpBusy={tpBusy} tpQueued={tpQueued}",
                side, type, name, Bytes, Chunks, elapsedMs, Bytes / 1e6 / Math.Max(elapsedMs / 1000, 1e-9),
                Ms(readTicks), Ms(sendTicks), Chunks > 0 ? Ms(sendTicks) / Chunks : 0, Ms(maxSendTicks), Ms(writeTicks), Ms(gapTicks), Chunks > 1 ? Ms(gapTicks) / (Chunks - 1) : 0, Ms(maxGapTicks),
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, (GC.GetTotalAllocatedBytes(false) - allocatedBytes) / 1e6,
                (GC.GetTotalPauseDuration() - pauseDuration).TotalMilliseconds, gcInfo.HeapSizeBytes / 1e6, gcInfo.TotalCommittedBytes / 1e6,
                ThreadPool.ThreadCount, maxWorkers - availableWorkers, ThreadPool.PendingWorkItemCount);
        }

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
