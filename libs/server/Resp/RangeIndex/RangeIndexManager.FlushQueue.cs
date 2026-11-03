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

        private int evictionsWithReaderDrainDeferred;

        /// <summary>Gets the number of evictions that found the key's lock held and deferred draining the tree's readers.</summary>
        internal int EvictionsWithReaderDrainDeferredCount => Volatile.Read(ref evictionsWithReaderDrainDeferred);

        /// <summary>An evicted tree waiting for its flush snapshots; <see cref="Settled"/> and <see cref="Reclaimed"/> are guarded by the key's tree ownership lock.</summary>
        private sealed class DeferredTreeRelease(TreeEntry entry, BfTreeService tree, ulong cacheBytes, bool drainReaders)
        {
            public readonly TreeEntry Entry = entry;
            public readonly BfTreeService Tree = tree;
            public readonly ulong CacheBytes = cacheBytes;
            /// <summary>The eviction could not take the exclusive lock, so readers of the tree must be drained before it is released.</summary>
            public readonly bool DrainReaders = drainReaders;
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

        /// <summary>Gets the number of flush files linked to a snapshot written for a later flush of the same tree.</summary>
        internal long CoalescedFlushSnapshotCount => Interlocked.Read(ref coalescedFlushSnapshots);

        private long coalescedFlushSnapshots;

        /// <summary>Test hook run by a flush worker before it snapshots; null in production.</summary>
        internal Action<string> BeforeFlushSnapshotForTest;

        /// <summary>Occupies every flush worker with <paramref name="work"/>, as other trees' long snapshots do.</summary>
        internal Task[] OccupyFlushWorkersForTest(Action work)
        {
            var tasks = new Task[FlushSnapshotConcurrency];
            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, FlushScheduler);

            return tasks;
        }

        /// <summary>
        /// Whether checkpoint snapshots of live trees are written in the background rather than at
        /// <see cref="Tsavorite.core.CheckpointTrigger.FlushBegin"/> with the tree's operations held. Requires the AOF: the snapshot cut lands
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

        internal sealed class PendingFlushSnapshot(long address)
        {
            public readonly long Address = address;
            private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Completion => done.Task;

            public void Complete() => _ = done.TrySetResult();

            private Action inlineWork;
            private int claimed;

            /// <summary>Sets work that only the first of the worker and a waiter runs, so a waiter need not queue behind other trees.</summary>
            public void SetClaimableWork(Action work) => inlineWork = work;

            /// <summary>Runs the claimable work on the calling thread unless a worker has already claimed it.</summary>
            /// <returns><c>true</c> if this call ran the work.</returns>
            public bool TryRunClaimed()
            {
                var work = Volatile.Read(ref inlineWork);
                if (work == null || Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                    return false;

                work();
                return true;
            }
        }

        private long flushSnapshotsRunInline;

        /// <summary>Gets the number of flush snapshots a waiting restore wrote itself instead of waiting for a worker.</summary>
        internal long FlushSnapshotsRunInlineCount => Interlocked.Read(ref flushSnapshotsRunInline);

        private FlushSnapshotScheduler FlushScheduler => LazyInitializer.EnsureInitialized(ref flushSnapshotScheduler, () => new FlushSnapshotScheduler(FlushSnapshotConcurrency));

        /// <summary>Removes the half-written snapshots and overlay work files a crash left behind.</summary>
        private void DeleteInProgressSnapshots()
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(riLogRoot, "*" + InProgressSuffix))
                    File.Delete(path);
                foreach (var path in Directory.EnumerateFiles(riLogRoot, "*" + OverlayWorkSuffix))
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
            if (entry.TryAppendFlushSnapshot(new QueuedFlushSnapshot(flushPath, pending, enqueuedAt), () => RunFlushSnapshot(entry, flushPath), FlushScheduler))
                return true;

            _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(flushPath, pending));
            pending.Complete();
            return false;
        }

        /// <summary>Mode returned by a cold flush whose copy was queued; the worker logs the snapshot.</summary>
        private const string ColdFlushQueuedMode = "cold-queued";

        /// <summary>Gets the number of cold flushes, and how many of them queued their copy instead of copying on the flush thread.</summary>
        internal (long Total, long Queued) ColdFlushCounts => (Interlocked.Read(ref phaseStats.ColdFlushes), Interlocked.Read(ref phaseStats.ColdFlushesQueued));

        /// <summary>
        /// Queues a copy of the immutable checkpoint snapshot <paramref name="source"/> holding pending <paramref name="entry"/>'s
        /// contents to <paramref name="flushPath"/>. Copying it inline stalls the flush thread (the AOF replay thread during recovery)
        /// for seconds per tree when the checkpoint and data directories cannot share hard links.
        /// </summary>
        /// <returns><c>false</c> if the entry no longer accepts snapshots because its tree is being released.</returns>
        private bool TryEnqueueColdFlushCopy(TreeEntry entry, string source, string dataPath, string flushPath, long logicalAddress)
        {
            var enqueuedAt = Stopwatch.GetTimestamp();
            var pending = new PendingFlushSnapshot(logicalAddress);

            // A copy reads only the immutable source, so a waiting restore may run it rather than queue behind other trees.
            pending.SetClaimableWork(() => RunColdFlushCopy(entry, source, dataPath, flushPath, pending, enqueuedAt));

            // Registered first so a waiter never misses a snapshot that is already running.
            pendingFlushSnapshots[flushPath] = pending;
            if (entry.TryAppendFlush(() => _ = pending.TryRunClaimed(), FlushScheduler))
                return true;

            _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(flushPath, pending));
            pending.Complete();
            return false;
        }

        /// <summary>
        /// Writes a queued cold flush file. The source needs no lock: it is never written, and a restore opens a copy-on-write overlay
        /// over it. If a newer checkpoint retired the source meanwhile, the entry's current contents are snapshotted instead, which is
        /// valid for the flush because AOF replay converges over a later cut.
        /// </summary>
        private void RunColdFlushCopy(TreeEntry entry, string source, string dataPath, string flushPath, PendingFlushSnapshot pending, long enqueuedAt)
        {
            var start = Stopwatch.GetTimestamp();
            string mode = null;
            try
            {
                BeforeFlushSnapshotForTest?.Invoke(flushPath);

                var target = flushPath + InProgressSuffix;
                if (flushSnapshotsStopped)
                {
                    mode = "skipped-stopped";
                }
                else if (pending.Address < Volatile.Read(ref flushFilesTruncatedBelow))
                {
                    mode = "skipped-truncated";
                }
                else
                {
                    try
                    {
                        RangeIndexFileLinks.Copy(source, target, overwrite: true);
                        mode = "cold-copy";
                    }
                    catch (FileNotFoundException)
                    {
                        using var ownership = LockTreeOwnership(entry.KeyHash, "flush-cold-retry");
                        if (entry.Tree != null && entry.TrySnapshotUnderClaim(target, allowLink: false, out _))
                            mode = "cold-live";
                        else if (entry.Tree == null && CopyPendingContents(entry, dataPath, target, overwrite: true))
                            mode = "cold-copy-retry";
                    }

                    if (mode != null)
                    {
                        File.Move(target, flushPath, overwrite: true);

                        // A truncation that enumerated the directory before the rename cannot have seen this file.
                        if (pending.Address < Volatile.Read(ref flushFilesTruncatedBelow))
                            _ = TryDeleteFlushFile(flushPath, out _);
                    }
                    else
                    {
                        logger?.LogCritical("[RangeIndexFlushSnapshotFailed] hash={Hash} addr=0x{Addr:x16} reason=no-source; the flushed stub has no flush file", entry.HashPrefix, pending.Address);
                    }
                }
            }
            catch (Exception ex)
            {
                mode = null;
                logger?.LogCritical(ex, "[RangeIndexFlushSnapshotFailed] hash={Hash} addr=0x{Addr:x16}; the flushed stub may have no flush file", entry.HashPrefix, pending.Address);
            }
            finally
            {
                _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(flushPath, pending));
                pending.Complete();
                LogFlushSnapshot(entry.HashPrefix, pending.Address, flushPath, mode, start, Stopwatch.GetElapsedTime(enqueuedAt, start).TotalMilliseconds);
            }
        }

        /// <summary>A flush snapshot queued on its tree and not yet taken by a worker.</summary>
        internal sealed class QueuedFlushSnapshot(string path, PendingFlushSnapshot pending, long enqueuedAt)
        {
            public readonly string Path = path;
            public readonly PendingFlushSnapshot Pending = pending;
            public readonly long EnqueuedAt = enqueuedAt;

            /// <summary>Log mode once handled; null while unhandled or if it failed.</summary>
            public string Mode;
        }

        /// <summary>
        /// Writes one snapshot for every flush of <paramref name="entry"/> queued so far: the tree is snapshotted once, into the
        /// highest-address flush file, and the other files are hard links to it. A snapshot taken after a flush is valid for that
        /// flush (AOF replay converges over a late snapshot), so one written after the last queued flush serves all of them, and a
        /// backlog of one tree's flushes costs one snapshot instead of one each. Runs that find the queue empty were coalesced.
        /// </summary>
        private void RunFlushSnapshot(TreeEntry entry, string flushPath)
        {
            BeforeFlushSnapshotForTest?.Invoke(flushPath);

            var batch = entry.TakeQueuedFlushSnapshots();
            if (batch.Count == 0)
                return;

            var start = Stopwatch.GetTimestamp();
            QueuedFlushSnapshot written = null;
            try
            {
                var truncatedBelow = Volatile.Read(ref flushFilesTruncatedBelow);
                foreach (var item in batch)
                {
                    if (flushSnapshotsStopped)
                        item.Mode = "skipped-stopped";
                    else if (item.Pending.Address < truncatedBelow)
                        item.Mode = "skipped-truncated";
                    else if (written is null || item.Pending.Address > written.Pending.Address)
                        written = item;
                }

                if (written is null)
                    return;

                if (!entry.TrySnapshotUnderClaim(written.Path, allowLink: true, atomic: true, out var kind))
                {
                    logger?.LogCritical("[RangeIndexFlushSnapshotFailed] hash={Hash} addr=0x{Addr:x16} files={Files} reason=retired; the flushed stubs have no flush file",
                        entry.HashPrefix, written.Pending.Address, batch.Count);
                    return;
                }

                written.Mode = SnapshotMode("live", kind);
                foreach (var item in batch)
                {
                    if (item.Mode != null)
                        continue;
                    item.Mode = LinkFlushFile(entry, written.Path, item.Path);
                    if (item.Mode == "coalesced")
                        _ = Interlocked.Increment(ref coalescedFlushSnapshots);
                }

                // A truncation that enumerated the directory before the renames cannot have seen these files.
                truncatedBelow = Volatile.Read(ref flushFilesTruncatedBelow);
                foreach (var item in batch)
                {
                    if (item.Mode != null && !item.Mode.StartsWith("skipped", StringComparison.Ordinal) && item.Pending.Address < truncatedBelow)
                        _ = TryDeleteFlushFile(item.Path, out _);
                }
            }
            catch (Exception ex)
            {
                logger?.LogCritical(ex, "[RangeIndexFlushSnapshotFailed] hash={Hash} files={Files}; flushed stubs may have no flush file", entry.HashPrefix, batch.Count);
            }
            finally
            {
                foreach (var item in batch)
                {
                    _ = pendingFlushSnapshots.TryRemove(new KeyValuePair<string, PendingFlushSnapshot>(item.Path, item.Pending));
                    item.Pending.Complete();
                    LogFlushSnapshot(entry.HashPrefix, item.Pending.Address, item.Path, item.Mode, start, Stopwatch.GetElapsedTime(item.EnqueuedAt, start).TotalMilliseconds, batch.Count);
                }
            }
        }

        /// <summary>Atomically gives <paramref name="flushPath"/> the contents of the just-written flush file <paramref name="sourcePath"/>.</summary>
        /// <returns>The log mode, or <c>null</c> if the file could not be produced.</returns>
        private string LinkFlushFile(TreeEntry entry, string sourcePath, string flushPath)
        {
            var target = flushPath + InProgressSuffix;
            if (RangeIndexFileLinks.TryReplaceWithHardLink(sourcePath, target))
            {
                File.Move(target, flushPath, overwrite: true);
                return "coalesced";
            }

            // The source may already have been truncated; snapshot the tree again rather than leave the stub without a file.
            return entry.TrySnapshotUnderClaim(flushPath, allowLink: true, atomic: true, out var kind) ? SnapshotMode("live", kind) : null;
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

            // ToArray is a point-in-time copy; enumerating the live dictionary would also await snapshots queued during the
            // wait, which under sustained page flushes never ends and starves checkpoints (783 s seen in the soak).
            foreach (var kvp in pendingFlushSnapshots.ToArray())
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

                // The workers may be busy with other trees' snapshots for minutes; this file is needed now.
                var inlineStart = Stopwatch.GetTimestamp();
                if (pending.TryRunClaimed())
                {
                    _ = Interlocked.Increment(ref flushSnapshotsRunInline);
                    logger?.LogInformation("[RangeIndexFlushSnapshotInline] reason={Reason} path={Path} completed={Completed} ms={Ms:F0}",
                        reason, Path.GetFileName(flushPath), pending.Completion.IsCompleted, Stopwatch.GetElapsedTime(inlineStart).TotalMilliseconds);
                }
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
        /// <param name="pendingSource">For a pending entry, the immutable snapshot holding its contents, copied instead of snapshotting a live tree.</param>
        /// <returns><c>false</c> if the entry no longer accepts snapshots because its tree is being released.</returns>
        private bool TryEnqueueCheckpointSnapshot(TreeEntry entry, Guid checkpointToken, string checkpointPath, string pendingSource = null)
        {
            var enqueuedAt = Stopwatch.GetTimestamp();
            var pending = pendingCheckpointSnapshots.GetOrAdd(checkpointToken, static _ => new PendingCheckpointSnapshots());
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Registered first so the persistence wait never misses a snapshot that is already running.
            pending.Snapshots.Add(done.Task);
            if (entry.TryAppendFlush(() => RunCheckpointSnapshot(entry, checkpointToken, checkpointPath, pendingSource, pending, done, enqueuedAt), FlushScheduler))
                return true;

            _ = done.TrySetResult();
            return false;
        }

        private void RunCheckpointSnapshot(TreeEntry entry, Guid checkpointToken, string checkpointPath, string pendingSource, PendingCheckpointSnapshots pending, TaskCompletionSource done, long enqueuedAt)
        {
            var start = Stopwatch.GetTimestamp();
            string mode = null;
            try
            {
                BeforeFlushSnapshotForTest?.Invoke(checkpointPath);

                if (flushSnapshotsStopped)
                    mode = "skipped-stopped";
                else if (pendingSource != null)
                    mode = CopyPendingForCheckpoint(entry, pendingSource, checkpointPath);
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

        /// <summary>
        /// Copies pending <paramref name="entry"/>'s immutable snapshot <paramref name="source"/> to <paramref name="checkpointPath"/>
        /// and re-points the entry's staging source at it. If the source was deleted meanwhile, the entry's current contents are
        /// captured under its ownership lock instead; a later cut is valid because AOF replay from the checkpoint converges over it.
        /// </summary>
        /// <returns>The log mode, or <c>null</c> if no contents could be captured.</returns>
        private string CopyPendingForCheckpoint(TreeEntry entry, string source, string checkpointPath)
        {
            var target = checkpointPath + InProgressSuffix;
            var staging = Volatile.Read(ref entry.StagingSource);
            string mode = "pending-copy";
            try
            {
                RangeIndexFileLinks.Copy(source, target, overwrite: true);
            }
            catch (FileNotFoundException)
            {
                using var ownership = LockTreeOwnership(entry.KeyHash, "checkpoint-pending-retry");
                if (entry.IsRetired)
                    return "skipped-retired";

                staging = Volatile.Read(ref entry.StagingSource);
                if (entry.Tree != null)
                {
                    if (!entry.TrySnapshotUnderClaim(target, allowLink: false, out _))
                        return "skipped-retired";
                    mode = "pending-live";
                }
                else if (CopyPendingContents(entry, LogDataPath(entry.HashPrefix), target, overwrite: true))
                {
                    mode = "pending-copy-retry";
                }
                else
                {
                    return null;
                }
            }

            File.Move(target, checkpointPath, overwrite: true);

            // Re-point staging at this checkpoint so purging the older one cannot strand the entry.
            if (staging != null)
                _ = Interlocked.CompareExchange(ref entry.StagingSource, checkpointPath, staging);
            return mode;
        }

        /// <summary>Stops writing queued flush snapshots and waits for those in flight; used before trees are discarded.</summary>
        private void StopFlushSnapshots(string reason)
        {
            flushSnapshotsStopped = true;
            var start = Stopwatch.GetTimestamp();
            var flushPending = pendingFlushSnapshots.Count;
            var checkpointPending = 0;
            var releasesPending = pendingTreeReleases.Count;

            // The caller blocks on the snapshots still in flight and discards their trees next, so the I/O cap would only delay it.
            using (BfTreeService.SuspendSnapshotIORate())
            {
                WaitForPendingFlushSnapshots(reason);
                foreach (var kvp in pendingCheckpointSnapshots)
                {
                    foreach (var snapshot in kvp.Value.Snapshots)
                    {
                        checkpointPending++;
                        AsyncUtils.BlockingWait(snapshot);
                    }
                }
                foreach (var kvp in pendingTreeReleases)
                    AsyncUtils.BlockingWait(kvp.Value.Completion);
            }

            logger?.LogInformation("[RangeIndexStopFlushSnapshots] reason={Reason} flushPending={Flush} checkpointPending={Checkpoint} releasesPending={Releases} ms={Ms:F0}",
                reason, flushPending, checkpointPending, releasesPending, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        /// <summary>
        /// Hands an evicted tree with queued flush snapshots to its last snapshot, which disposes or parks it when done unless
        /// a promote reclaims it first (<see cref="TryReclaimDeferredTree"/>). The caller has removed the entry, and new readers
        /// of the evicted record promote or restore it. Readers already using the tree were drained by the caller's exclusive lock,
        /// or, with <paramref name="drainReaders"/>, are drained before the release settles.
        /// </summary>
        /// <returns><c>false</c> if no snapshot is pending and no drain is needed, so the caller disposes the tree itself.</returns>
        private bool TryDeferReleaseToFlushSnapshots(Guid keyId, TreeEntry entry, BfTreeService tree, ulong cacheBytes, bool drainReaders = false)
        {
            var chain = entry.CloseFlushChainAsync();
            if (chain.IsCompleted && !drainReaders)
                return false;

            var release = new DeferredTreeRelease(entry, tree, cacheBytes, drainReaders);
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
                // A barrier on the exclusive lock waits out readers that took the tree before its eviction.
                if (release.DrainReaders && !release.Reclaimed)
                {
                    var drainStart = Stopwatch.GetTimestamp();
                    rangeIndexLocks.AcquireExclusiveLock(entry.KeyHash, out var drainToken);
                    rangeIndexLocks.ReleaseLock(drainToken);
                    var drainMs = Stopwatch.GetElapsedTime(drainStart).TotalMilliseconds;
                    if (drainMs >= 100)
                        logger?.LogInformation("[RangeIndexEvictReaderDrain] hash={Hash} ms={Ms:F0}", entry.HashPrefix, drainMs);
                }

                // Only ownership bookkeeping happens here: the tree is unreachable to readers since its eviction.
                using (LockTreeOwnership(entry.KeyHash, "release-settle"))
                {
                    release.Settled = true;
                    if (release.Reclaimed)
                        outcome = "reclaimed";
                    // A registration since the eviction may have staged data.bftree over the evicted tree's file.
                    else if (EvictedTreeGrace > TimeSpan.Zero && !liveIndexes.ContainsKey(keyId) && TryParkEvictedTreeLocked(keyId, entry, release.Tree, release.CacheBytes))
                        outcome = "parked";
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

            using var ownership = LockTreeOwnership(keyHash, "reclaim");
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

            // After Dispose a rejected task would fault and synchronously schedule its successor, recursing down a long
            // per-tree chain until the stack overflows; such stragglers run on the thread pool and skip their work instead.
            protected override void QueueTask(Task task)
            {
                if (!tasks.IsAddingCompleted)
                {
                    try
                    {
                        tasks.Add(task);
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                _ = ThreadPool.UnsafeQueueUserWorkItem(t => _ = TryExecuteTask(t), task, preferLocal: false);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

            protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();

            public void Dispose() => tasks.CompleteAdding();
        }
    }
}