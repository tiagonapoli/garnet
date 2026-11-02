// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    public sealed partial class RangeIndexManager
    {
        /// <summary>Recent replayed fields remembered per index for <see cref="VerifyReplayedWrites"/>.</summary>
        internal const int ReplayWriteCheckCapacity = 128;

        /// <summary>Minimum time between two periodic re-reads of an index's remembered fields; a tree change re-reads at once.</summary>
        /// <remarks>Each re-read costs <see cref="ReplayWriteCheckCapacity"/> tree reads, so a per-op cadence slows replica apply.</remarks>
        internal static readonly TimeSpan ReplayWriteCheckInterval = TimeSpan.FromSeconds(1);

        private readonly ConcurrentDictionary<string, ReplayWriteTracker> replayWriteTrackers = new();
        private long replayWriteLossCount;
        private long replayWriteChecks;

        /// <summary>Gets the number of replayed writes found missing from their tree after they were applied.</summary>
        internal long ReplayWriteLossCount => Interlocked.Read(ref replayWriteLossCount);

        /// <summary>Gets the number of re-reads of remembered replayed fields.</summary>
        internal long ReplayWriteCheckCount => Interlocked.Read(ref replayWriteChecks);

        /// <summary>Fields last written by replay to one index, with the value each must still read back; null means deleted.</summary>
        private sealed class ReplayWriteTracker
        {
            public readonly Dictionary<string, (byte[] Field, byte[] Value, long At)> Expected = [];
            public readonly Queue<string> Order = new();
            public nint LastCheckedTree;
            public long LastCheckAt;
        }

        /// <summary>
        /// Remembers a replayed write of <paramref name="field"/> on index <paramref name="key"/> and periodically re-reads every
        /// remembered field from <paramref name="treePtr"/>, logging <c>[RangeIndexReplayWriteLoss]</c> for any that no longer
        /// reads back. Replay is the only writer on a replica and during recovery, so a mismatch is a lost write.
        /// </summary>
        /// <param name="value">The written value, or empty for a delete.</param>
        private void TrackReplayedWrite(ReadOnlySpan<byte> key, nint treePtr, ReadOnlySpan<byte> field, ReadOnlySpan<byte> value, bool isDelete)
        {
            var tracker = replayWriteTrackers.GetOrAdd(Convert.ToHexString(key), static _ => new ReplayWriteTracker());
            lock (tracker)
            {
                var fieldId = Convert.ToHexString(field);
                if (!tracker.Expected.TryGetValue(fieldId, out var entry))
                {
                    tracker.Order.Enqueue(fieldId);
                    while (tracker.Order.Count > ReplayWriteCheckCapacity)
                        _ = tracker.Expected.Remove(tracker.Order.Dequeue());

                    entry.Field = GC.AllocateArray<byte>(field.Length, pinned: true);
                    field.CopyTo(entry.Field);
                }

                tracker.Expected[fieldId] = (entry.Field, isDelete ? null : value.ToArray(), Stopwatch.GetTimestamp());

                // A different tree means the index was evicted and restored or promoted since the last check: re-read right away.
                if (treePtr == tracker.LastCheckedTree && Stopwatch.GetElapsedTime(tracker.LastCheckAt) < ReplayWriteCheckInterval)
                    return;

                VerifyReplayedWrites(key, tracker, treePtr);
            }
        }

        private void VerifyReplayedWrites(ReadOnlySpan<byte> key, ReplayWriteTracker tracker, nint treePtr)
        {
            var previousTree = tracker.LastCheckedTree;
            var sinceCheckMs = tracker.LastCheckAt == 0 ? 0 : Stopwatch.GetElapsedTime(tracker.LastCheckAt).TotalMilliseconds;
            tracker.LastCheckedTree = treePtr;
            tracker.LastCheckAt = Stopwatch.GetTimestamp();
            _ = Interlocked.Increment(ref replayWriteChecks);

            List<string> lost = null;
            foreach (var (fieldId, (field, expected, at)) in tracker.Expected)
            {
                var result = BfTreeService.ReadByPtr(treePtr, PinnedSpanByte.FromPinnedSpan(field), out var actual);
                var matches = expected is null ? result != BfTreeReadResult.Found : result == BfTreeReadResult.Found && actual.AsSpan().SequenceEqual(expected);
                if (matches)
                    continue;

                var count = Interlocked.Increment(ref replayWriteLossCount);
                if (count <= 200)
                {
                    logger?.LogCritical("[RangeIndexReplayWriteLoss] count={Count} key={Key} field={Field} expected={Expected} result={Result} actualLen={ActualLen} writtenAgoMs={AgoMs:F0} treePtr={Tree} previousTreePtr={Previous} sinceCheckMs={SinceMs:F0} tracked={Tracked}",
                        count, Convert.ToHexString(key), fieldId, expected is null ? "deleted" : $"len={expected.Length}", result, actual.Length,
                        Stopwatch.GetElapsedTime(at).TotalMilliseconds, treePtr, previousTree, sinceCheckMs, tracker.Expected.Count);
                }

                (lost ??= []).Add(fieldId);
            }

            // Forget lost fields so one loss is reported once rather than at every later check.
            if (lost != null)
            {
                foreach (var fieldId in lost)
                    _ = tracker.Expected.Remove(fieldId);
            }
        }

        /// <summary>Forgets remembered replayed writes of <paramref name="key"/>, whose index is being recreated.</summary>
        private void ForgetReplayedWrites(ReadOnlySpan<byte> key) => _ = replayWriteTrackers.TryRemove(Convert.ToHexString(key), out _);

        /// <summary>Forgets every remembered replayed write, after the store was reset and will be reloaded.</summary>
        private void ForgetAllReplayedWrites() => replayWriteTrackers.Clear();
    }
}