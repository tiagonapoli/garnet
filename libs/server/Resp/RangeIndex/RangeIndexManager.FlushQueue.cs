// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Background flush snapshots. <see cref="SnapshotTreeForFlush"/> runs inside the Tsavorite epoch drain that marks pages
    /// read-only — on a replica that is the AOF replay thread — so writing a multi-gigabyte tree snapshot there stalls
    /// replication. Live trees are instead snapshotted by dedicated workers while the stub is marked flushed immediately.
    ///
    /// <para>The snapshot cut lands after the stub became read-only. That is safe because every tree mutation (RI.SET,
    /// RI.DEL) is idempotent and last-writer-wins, so replaying the AOF over a later cut converges to the same tree, and
    /// later writes promote the stub to the tail first, so the flush file is never the newest state of a reachable record.</para>
    ///
    /// <para>Every consumer of a flush file waits for its pending snapshot: checkpoint persistence
    /// (<see cref="WaitForFlushSnapshotsBeforeCheckpointPersist"/>), replica sync enumeration, restoring an evicted stub
    /// (<see cref="PreStageAndRegisterPending"/>), and file deletion. Files are written under a temporary name and renamed,
    /// so enumerations never observe a partial snapshot.</para>
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>Concurrent flush snapshot writers; snapshots of different trees proceed in parallel.</summary>
        internal const int FlushSnapshotConcurrency = 4;

        /// <summary>Suffix of a snapshot being written; renamed to its final name when complete.</summary>
        internal const string InProgressSuffix = ".inprogress";

        private FlushSnapshotScheduler flushSnapshotScheduler;

        /// <summary>Flush snapshots not yet written, by final path.</summary>
        private readonly ConcurrentDictionary<string, PendingFlushSnapshot> pendingFlushSnapshots = new();

        /// <summary>Evicted or deleted trees whose disposal waits for their pending flush snapshots, by key id.</summary>
        private readonly ConcurrentDictionary<Guid, DeferredTreeRelease> pendingTreeReleases = new();

        private int deferredTreesReclaimed;

        /// <summary>Gets the number of promotes that reclaimed an evicted tree before its flush snapshots finished.</summary>
        internal int DeferredTreesReclaimedCount => Volatile.Read(ref deferredTreesReclaimed);

        /// <summary>An evicted tree waiting for its flush snapshots; <see cref="Settled"/> and <see cref="Reclaimed"/> are guarded by the key's lock.</summary>
        private sealed class DeferredTreeRelease(TreeEntry entry, BfTreeService tree, ulong cacheBytes)
        {
            public readonly TreeEntry Entry = entry;
            public readonly BfTreeService Tree = tree;
            public readonly ulong CacheBytes = cacheBytes;
            public readonly long QueuedAt = Stopwatch.GetTimestamp();
            private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Settled;
            public bool Reclaimed;

            public Task Completion => done.Task;

            public void Complete() => _ = done.TrySetResult();
        }

        /// <summary>Flush files below this address are truncated; a snapshot finishing below it deletes its file.</summary>
        private long flushFilesTruncatedBelow;

        /// <summary>Set on dispose and store reset; queued snapshots are skipped.</summary>
        private volatile bool flushSnapshotsStopped;

        /// <summary>Gets the number of flush snapshots queued or being written.</summary>
        internal int PendingFlushSnapshotCount => pendingFlushSnapshots.Count;

        /// <summary>Test hook run by a flush worker before it snapshots; null in production.</summary>
        internal Action<string> BeforeFlushSnapshotForTest;

        /// <summary>
        /// Whether checkpoint snapshots of live trees are written in the background rather than at
        /// <see cref="CheckpointTrigger.FlushBegin"/> with the tree's operations held. Requires the AOF: the snapshot cut lands
        /// after the version shift, and replaying the AOF from the checkpoint over it converges for the same reason as a
        /// late flush snapshot.
        /// </summary>
        public bool BackgroundCheckpointSnapshots { get; init; }

        /// <summary>Checkpoint snapshots queued or being written, by checkpoint token.</summary>
        private readonly ConcurrentDictionary<Guid, PendingCheckpointSnapshots> pendingCheckpointSnapshots = new();

        private sealed class PendingCheckpointSnapshots
        {
            public readonly ConcurrentBag<Task> Snapshots = [];
            public int Failed;
        }

        private sealed class PendingFlushSnapshot(long address)
        {
            public readonly long Address = address;
            private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Completion => done.Task;

            public void Complete() => _ = done.TrySetResult();
        }

        private FlushSnapshotScheduler FlushScheduler => LazyInitializer.EnsureInitialized(ref flushSnapshotScheduler, () => new FlushSnapshotScheduler(FlushSnapshotConcurrency));

        /// <summary>Removes snapshot and staging-prefetch files a crash left half-written or unused.</summary>
        private void DeleteInProgressSnapshots()
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(riLogRoot, "*" + InProgressSuffix))
                    File.Delete(path);
                foreach (var path in Directory.EnumerateFiles(riLogRoot, "*" + StagingPrefetchSuffix))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "RangeIndexManager: failed to delete in-progress snapshots under {Path}", riLogRoot);
            }
        }

        /// <summary>
        /// Queues a flush snapshot of <paramref name="entry"/>'s live tree to <paramref name="flushPath"/>.
        /// </summary>
        /// <returns><c>false</c> if the entry no longer accepts snapshots because its tree is being released.</returns>
        private bool TryEnqueueFlushSnapshot(TreeEntry entry, long logicalAddress, string flushPath)
        {
            var enqueuedAt = Stopwatch.GetTimestamp();
            var pending = new PendingFlushSnapshot(logicalAddress);

            // Registered first so a waiter never misses a snapshot that is already running.
            pendingFlushSnapshots[flushPath] = pending;
            if (entry.TryAppendFlush(() => RunFlushSnapshot(entry, pending, flushPath, enqueuedAt), FlushScheduler))
                return true;

            _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(flushPath, pending));
            pending.Complete();
            return false;
        }

        private void RunFlushSnapshot(TreeEntry entry, PendingFlushSnapshot pending, string flushPath, long enqueuedAt)
        {
            var logicalAddress = pending.Address;
            var start = Stopwatch.GetTimestamp();
            string mode = null;
            try
            {
                BeforeFlushSnapshotForTest?.Invoke(flushPath);

                if (flushSnapshotsStopped)
                    mode = "skipped-stopped";
                else if (logicalAddress < Volatile.Read(ref flushFilesTruncatedBelow))
                    mode = "skipped-truncated";
                else if (entry.TrySnapshotUnderClaim(flushPath, allowLink: true, atomic: true, out var kind))
                {
                    mode = SnapshotMode("live", kind);

                    // A truncation that enumerated the directory before the rename cannot have seen this file.
                    if (logicalAddress < Volatile.Read(ref flushFilesTruncatedBelow))
                        _ = TryDeleteFlushFile(flushPath, out _);
                }
                else
                {
                    logger?.LogCritical("[RangeIndexFlushSnapshotFailed] hash={Hash} addr=0x{Addr:x16} reason=retired; the flushed stub has no flush file",
                        entry.HashPrefix, logicalAddress);
                }
            }
            catch (Exception ex)
            {
                logger?.LogCritical(ex, "[RangeIndexFlushSnapshotFailed] hash={Hash} addr=0x{Addr:x16}; the flushed stub has no flush file", entry.HashPrefix, logicalAddress);
            }
            finally
            {
                _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(flushPath, pending));
                pending.Complete();
                LogFlushSnapshot(entry.HashPrefix, logicalAddress, flushPath, mode, start, Stopwatch.GetElapsedTime(enqueuedAt, start).TotalMilliseconds);
            }
        }

        /// <summary>
        /// Waits for queued flush snapshots whose address satisfies <paramref name="filter"/>, or all of them when null.
        /// Only snapshots queued before the call are awaited.
        /// </summary>
        /// <param name="reason">Why the caller needs the files; logged with the wait.</param>
        /// <param name="filter">Selects snapshots by logical address.</param>
        internal void WaitForPendingFlushSnapshots(string reason, Func<long, bool> filter = null)
        {
            if (pendingFlushSnapshots.IsEmpty)
                return;

            var start = Stopwatch.GetTimestamp();
            var waited = 0;
            foreach (var kvp in pendingFlushSnapshots)
            {
                if (filter != null && !filter(kvp.Value.Address))
                    continue;
                if (kvp.Value.Completion.IsCompleted)
                    continue;
                waited++;
                AsyncUtils.BlockingWait(kvp.Value.Completion);
            }

            LogFlushDrainWait(reason, waited, start);
        }

        /// <summary>Waits for the flush file at <paramref name="flushPath"/> and for the release of the key's previous tree.</summary>
        private void WaitForFlushFile(Guid keyId, string flushPath, string reason)
        {
            var start = Stopwatch.GetTimestamp();
            var waited = 0;
            if (pendingFlushSnapshots.TryGetValue(flushPath, out var pending) && !pending.Completion.IsCompleted)
            {
                waited++;
                AsyncUtils.BlockingWait(pending.Completion);
            }

            // The evicted tree may still have data.bftree open; staging over it must wait for its disposal.
            if (pendingTreeReleases.TryGetValue(keyId, out var release) && !release.Completion.IsCompleted)
            {
                waited++;
                AsyncUtils.BlockingWait(release.Completion);
            }

            LogFlushDrainWait(reason, waited, start);
        }

        /// <summary>
        /// Called before Tsavorite persists checkpoint metadata. Records below the checkpoint's flushed address may be
        /// flushed stubs whose recovery reads their flush files, and their OnFlush calls all preceded the page writes the
        /// checkpoint waited for, so every such snapshot is already queued. The checkpoint's own tree snapshots were queued
        /// at FlushBegin; if any failed, the checkpoint fails rather than persisting without them.
        /// </summary>
        internal void WaitForFlushSnapshotsBeforeCheckpointPersist(Guid checkpointToken)
        {
            WaitForPendingFlushSnapshots($"checkpoint:{checkpointToken}");

            if (!pendingCheckpointSnapshots.TryRemove(checkpointToken, out var pending))
                return;

            var start = Stopwatch.GetTimestamp();
            var waited = 0;
            foreach (var snapshot in pending.Snapshots)
            {
                if (snapshot.IsCompleted)
                    continue;
                waited++;
                AsyncUtils.BlockingWait(snapshot);
            }

            if (waited > 0)
            {
                logger?.LogInformation("[RangeIndexCheckpointSnapshotWait] token={Token} snapshots={Waited} ms={Ms:F0}",
                    checkpointToken, waited, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            if (Volatile.Read(ref pending.Failed) != 0)
                throw new GarnetException($"RangeIndex: {pending.Failed} checkpoint tree snapshot(s) failed for checkpoint {checkpointToken}");
        }

        /// <summary>Stops tracking the background snapshots of a failed checkpoint; they finish on their own.</summary>
        internal void ForgetCheckpointSnapshots(Guid checkpointToken) => _ = pendingCheckpointSnapshots.TryRemove(checkpointToken, out _);

        /// <summary>
        /// Queues a checkpoint snapshot of <paramref name="entry"/>'s live tree to <paramref name="checkpointPath"/>, after the
        /// tree's earlier flush snapshots.
        /// </summary>
        /// <returns><c>false</c> if the entry no longer accepts snapshots because its tree is being released.</returns>
        private bool TryEnqueueCheckpointSnapshot(TreeEntry entry, Guid checkpointToken, string checkpointPath)
        {
            var enqueuedAt = Stopwatch.GetTimestamp();
            var pending = pendingCheckpointSnapshots.GetOrAdd(checkpointToken, static _ => new PendingCheckpointSnapshots());
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Registered first so the persistence wait never misses a snapshot that is already running.
            pending.Snapshots.Add(done.Task);
            if (entry.TryAppendFlush(() => RunCheckpointSnapshot(entry, checkpointToken, checkpointPath, pending, done, enqueuedAt), FlushScheduler))
                return true;

            _ = done.TrySetResult();
            return false;
        }

        private void RunCheckpointSnapshot(TreeEntry entry, Guid checkpointToken, string checkpointPath, PendingCheckpointSnapshots pending, TaskCompletionSource done, long enqueuedAt)
        {
            var start = Stopwatch.GetTimestamp();
            string mode = null;
            try
            {
                BeforeFlushSnapshotForTest?.Invoke(checkpointPath);

                if (flushSnapshotsStopped)
                    mode = "skipped-stopped";
                else if (entry.TrySnapshotUnderClaim(checkpointPath, allowLink: true, atomic: true, out var kind))
                    mode = SnapshotMode("live", kind);
                else
                    mode = "skipped-retired";
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[RangeIndexCheckpointSnapshotFailed] token={Token} hash={Hash}", checkpointToken, entry.HashPrefix);
            }
            finally
            {
                // A tree retired after the version shift was evicted (its flush file covers the stub) or deleted, so skipping it is
                // not a failure; anything else leaves the checkpoint without the tree.
                if (mode is null or "skipped-stopped")
                    _ = Interlocked.Increment(ref pending.Failed);

                _ = done.TrySetResult();
                logger?.LogInformation("[RangeIndexCheckpointSnapshot] token={Token} hash={Hash} mode={Mode} async=true bytes={Bytes} ms={Ms:F0} queueMs={QueueMs:F0}",
                    checkpointToken, entry.HashPrefix, mode ?? "failed", mode is null ? -1 : TryGetFileLength(checkpointPath),
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds, Stopwatch.GetElapsedTime(enqueuedAt, start).TotalMilliseconds);
            }
        }

        /// <summary>Stops writing queued flush snapshots and waits for those in flight; used before trees are discarded.</summary>
        private void StopFlushSnapshots(string reason)
        {
            flushSnapshotsStopped = true;
            WaitForPendingFlushSnapshots(reason);
            foreach (var kvp in pendingCheckpointSnapshots)
            {
                foreach (var snapshot in kvp.Value.Snapshots)
                    AsyncUtils.BlockingWait(snapshot);
            }
            foreach (var kvp in pendingTreeReleases)
                AsyncUtils.BlockingWait(kvp.Value.Completion);
        }

        /// <summary>
        /// Hands an evicted tree with queued flush snapshots to its last snapshot, which disposes or parks it when done unless
        /// a promote reclaims it first (<see cref="TryReclaimDeferredTree"/>). The caller has removed the entry under the
        /// per-key exclusive lock, which drained every reader of the evicted record, and readers of the evicted record now
        /// promote or restore it, so no reader can still use the tree.
        /// </summary>
        /// <returns><c>false</c> if no snapshot is pending, so the caller disposes the tree itself.</returns>
        private bool TryDeferReleaseToFlushSnapshots(Guid keyId, TreeEntry entry, BfTreeService tree, ulong cacheBytes)
        {
            var chain = entry.CloseFlushChainAsync();
            if (chain.IsCompleted)
                return false;

            var release = new DeferredTreeRelease(entry, tree, cacheBytes);
            pendingTreeReleases[keyId] = release;
            _ = chain.ContinueWith(_ => SettleDeferredRelease(keyId, release), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }

        /// <summary>Parks or disposes a deferred tree once its flush snapshots are written, unless a promote reclaimed it.</summary>
        private void SettleDeferredRelease(Guid keyId, DeferredTreeRelease release)
        {
            var entry = release.Entry;
            var outcome = "disposed";
            try
            {
                rangeIndexLocks.AcquireExclusiveLock(entry.KeyHash, out var lockToken);
                try
                {
                    release.Settled = true;
                    if (release.Reclaimed)
                        outcome = "reclaimed";
                    // A registration since the eviction may have staged data.bftree over the evicted tree's file.
                    else if (EvictedTreeGrace > TimeSpan.Zero && !liveIndexes.ContainsKey(keyId) && TryParkEvictedTreeLocked(keyId, entry, release.Tree, release.CacheBytes))
                        outcome = "parked";
                }
                finally
                {
                    rangeIndexLocks.ReleaseLock(lockToken);
                }

                if (outcome == "disposed")
                    DisposeAndDeleteFilesDeferred(entry, release.Tree, hashPrefix: null);
            }
            finally
            {
                _ = pendingTreeReleases.TryRemove(new KeyValuePair<Guid, DeferredTreeRelease>(keyId, release));
                release.Complete();
                logger?.LogInformation("[RangeIndexFlushReleaseDeferred] hash={Hash} waitMs={Ms:F0} outcome={Outcome}",
                    entry.HashPrefix, Stopwatch.GetElapsedTime(release.QueuedAt).TotalMilliseconds, outcome);
            }
        }

        /// <summary>
        /// Re-registers the evicted tree of the key while its flush snapshots are still queued, when it was evicted from the
        /// record flushed at <paramref name="flushAddress"/>, so that record's promote waits for neither the snapshot nor a copy.
        /// The same <see cref="TreeEntry"/> returns, so its snapshot claim keeps serializing the queued snapshots with later ones,
        /// and a snapshot cut after the promote is safe for the reasons a late flush snapshot is.
        /// </summary>
        /// <returns><c>true</c> if the tree is live again.</returns>
        private bool TryReclaimDeferredTree(long keyHash, Guid keyId, long flushAddress)
        {
            if (pendingTreeReleases.IsEmpty || !pendingTreeReleases.TryGetValue(keyId, out var release))
                return false;

            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var lockToken);
            try
            {
                var entry = release.Entry;
                if (release.Settled || release.Reclaimed || Volatile.Read(ref entry.LastFlushAddress) != flushAddress || liveIndexes.ContainsKey(keyId))
                    return false;

                DiscardParkedTreeLocked(keyId, "reclaimed");
                entry.ReopenFlushChain();
                if (!liveIndexes.TryAdd(keyId, entry))
                    return false;

                release.Reclaimed = true;
                _ = pendingTreeReleases.TryRemove(new KeyValuePair<Guid, DeferredTreeRelease>(keyId, release));
                _ = Interlocked.Increment(ref deferredTreesReclaimed);
                logger?.LogInformation("[RangeIndexDeferredTreeReclaimed] hash={Hash} addr=0x{Addr:x16} deferredMs={Ms:F0} pendingSnapshots={Pending}",
                    entry.HashPrefix, flushAddress, Stopwatch.GetElapsedTime(release.QueuedAt).TotalMilliseconds, pendingFlushSnapshots.Count);
                return true;
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
        }

        /// <summary>Closes <paramref name="entry"/> to new flush snapshots and waits for the queued ones.</summary>
        private void DrainEntryFlushSnapshots(TreeEntry entry, string reason)
        {
            var chain = entry.CloseFlushChainAsync();
            if (chain.IsCompleted)
                return;

            var start = Stopwatch.GetTimestamp();
            AsyncUtils.BlockingWait(chain);
            LogFlushDrainWait(reason, 1, start);
        }

        private void LogFlushDrainWait(string reason, int waited, long start)
        {
            if (waited == 0)
                return;

            logger?.LogInformation("[RangeIndexFlushDrainWait] reason={Reason} snapshots={Waited} pending={Pending} ms={Ms:F0}",
                reason, waited, pendingFlushSnapshots.Count, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        private void DisposeFlushScheduler() => flushSnapshotScheduler?.Dispose();

        /// <summary>Runs flush snapshots on dedicated threads, keeping long blocking writes off the thread pool.</summary>
        private sealed class FlushSnapshotScheduler : TaskScheduler, IDisposable
        {
            private readonly BlockingCollection<Task> tasks = new();
            private readonly Thread[] threads;

            public FlushSnapshotScheduler(int concurrency)
            {
                threads = new Thread[concurrency];
                for (var i = 0; i < concurrency; i++)
                {
                    threads[i] = new Thread(Run) { IsBackground = true, Name = $"RangeIndexFlush-{i}" };
                    threads[i].Start();
                }
            }

            public override int MaximumConcurrencyLevel => threads.Length;

            private void Run()
            {
                foreach (var task in tasks.GetConsumingEnumerable())
                    _ = TryExecuteTask(task);
            }

            protected override void QueueTask(Task task) => tasks.Add(task);

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

            protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();

            public void Dispose() => tasks.CompleteAdding();
        }
    }
}