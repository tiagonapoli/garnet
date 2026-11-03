// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Garnet.common;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Locking methods for RangeIndex operations.
    ///
    /// <para>Locking protocol:</para>
    /// <list type="bullet">
    /// <item><b>Shared locks</b> are acquired for data operations (RI.SET, RI.GET, RI.DEL field)
    /// via <see cref="ReadRangeIndex"/>. Multiple sessions can operate on the same BfTree
    /// concurrently — the native BfTree is internally thread-safe for point operations.</item>
    /// <item><b>Exclusive locks</b> are acquired for lifecycle operations (DEL key, eviction,
    /// checkpoint snapshot, lazy restore) via <see cref="AcquireExclusiveForDelete"/>.
    /// These prevent concurrent data operations from accessing a BfTree that is being
    /// freed, snapshotted, or restored.</item>
    /// </list>
    ///
    /// <para>Locks are striped by key hash via <see cref="ReadOptimizedLock"/> for scalability.</para>
    ///
    /// <para><b>Tree ownership lock</b> (<see cref="LockTreeOwnership"/>): guards which tree, parked tree, or deferred release
    /// a key owns, and the key's <c>data.bftree</c>. Callbacks that run inside a Tsavorite operation (RIPROMOTE
    /// PostCopyUpdater, PostCopyToTail, OnFlush) hold the record's bucket latch, and readers hold the RangeIndex lock across
    /// Tsavorite operations that wait for that latch, so such callbacks take only this lock. Its holders never issue
    /// Tsavorite operations nor wait for a RangeIndex lock; a RangeIndex lock, when also needed, is taken first.</para>
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>
        /// RAII holder for a shared lock on a RangeIndex key.
        /// Disposing releases the shared lock and completes the replica consistent read, if one was started.
        /// </summary>
        internal readonly ref struct ReadRangeIndexLock : IDisposable
        {
            private readonly ref readonly ReadOptimizedLock lockRef;
            private readonly ReadOptimizedLock.LockToken lockToken;
            private readonly ReadSessionState readSessionState;

            internal ReadRangeIndexLock(ref readonly ReadOptimizedLock lockRef, ReadOptimizedLock.LockToken lockToken, ReadSessionState readSessionState)
            {
                this.lockToken = lockToken;
                this.lockRef = ref lockRef;
                this.readSessionState = readSessionState;
            }

            internal ReadRangeIndexLock(ReadSessionState readSessionState) => this.readSessionState = readSessionState;

            /// <inheritdoc/>
            public void Dispose()
            {
                if (!Unsafe.IsNullRef(in lockRef))
                    lockRef.ReleaseLock(lockToken);

                // Runs after the tree operation so the session's sequence number covers every replayed write it observed.
                readSessionState?.PostSingleKeyConsistentReadCallback();
            }
        }

        /// <summary>
        /// RAII holder for an exclusive lock on a RangeIndex key.
        /// Disposing releases the exclusive lock.
        /// </summary>
        internal readonly ref struct ExclusiveRangeIndexLock : IDisposable
        {
            private readonly ref readonly ReadOptimizedLock lockRef;
            private readonly ReadOptimizedLock.LockToken lockToken;

            internal ExclusiveRangeIndexLock(ref readonly ReadOptimizedLock lockRef, ReadOptimizedLock.LockToken lockToken)
            {
                this.lockToken = lockToken;
                this.lockRef = ref lockRef;
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                if (Unsafe.IsNullRef(in lockRef))
                    return;

                lockRef.ReleaseLock(lockToken);
            }
        }

        /// <summary>RAII holder for a tree ownership lock (<see cref="LockTreeOwnership"/>).</summary>
        internal readonly ref struct TreeOwnershipLock : IDisposable
        {
            private readonly object gate;

            internal TreeOwnershipLock(object gate) => this.gate = gate;

            /// <inheritdoc/>
            public void Dispose() => Monitor.Exit(gate);
        }

        private const int TreeOwnershipLockCount = 1024;

        private readonly object[] treeOwnershipLocks = CreateTreeOwnershipLocks();

        private long treeOwnershipLockWaits;
        private long treeOwnershipLockWaitTicks;

        private readonly ReadOptimizedLock rangeIndexLocks;

        private static object[] CreateTreeOwnershipLocks()
        {
            var locks = new object[TreeOwnershipLockCount];
            for (var i = 0; i < locks.Length; i++)
                locks[i] = new object();
            return locks;
        }

        /// <summary>Gets how often a tree ownership lock was contended and the total time spent waiting for it.</summary>
        internal (long Waits, TimeSpan WaitTime) TreeOwnershipLockContention
            => (Interlocked.Read(ref treeOwnershipLockWaits), Stopwatch.GetElapsedTime(0, Interlocked.Read(ref treeOwnershipLockWaitTicks)));

        /// <summary>Acquires the key's tree ownership lock; it is reentrant.</summary>
        /// <param name="reason">Names the caller in the slow-wait log.</param>
        private TreeOwnershipLock LockTreeOwnership(long keyHash, string reason)
        {
            var gate = treeOwnershipLocks[(int)((ulong)keyHash & (TreeOwnershipLockCount - 1))];
            if (!Monitor.TryEnter(gate))
            {
                var start = Stopwatch.GetTimestamp();
                Monitor.Enter(gate);
                var ticks = Stopwatch.GetTimestamp() - start;
                _ = Interlocked.Increment(ref treeOwnershipLockWaits);
                _ = Interlocked.Add(ref treeOwnershipLockWaitTicks, ticks);
                var ms = ticks * 1000.0 / Stopwatch.Frequency;
                if (ms >= 100)
                    logger?.LogInformation("[RangeIndexOwnershipLockWait] reason={Reason} ms={Ms:F0}", reason, ms);
            }

            return new TreeOwnershipLock(gate);
        }

        /// <summary>
        /// Read the RangeIndex stub under a shared lock.
        /// Used by RI.SET, RI.GET, RI.DEL (field-level operations).
        /// Returns an RAII lock holder; caller operates on the BfTree while the lock is held.
        /// </summary>
        /// <remarks>
        /// This method handles several edge cases transparently:
        /// <list type="bullet">
        /// <item><b>Checkpoint in progress</b>: If the tree is being snapshotted, releases
        /// the lock, waits for snapshot completion, and retries.</item>
        /// <item><b>Flushed stub</b>: If the stub has been flushed to the read-only region
        /// (IsFlushed set), promotes the stub to the tail via RMW and retries.</item>
        /// <item><b>Null TreeHandle</b>: If the tree was evicted to disk (TreeHandle == 0),
        /// triggers lazy restore from the flush/checkpoint snapshot file and retries.</item>
        /// </list>
        /// </remarks>
        /// <param name="session">The storage session for store reads.</param>
        /// <param name="key">The pinned Garnet key.</param>
        /// <param name="input">The StringInput for the read operation (used for WRONGTYPE checks).</param>
        /// <param name="indexSpan">Caller-provided span to receive the stub bytes (must be ≥ <see cref="IndexSizeBytes"/>).</param>
        /// <param name="status">On return, the read status (OK, NOTFOUND, or WRONGTYPE).</param>
        /// <returns>An RAII lock holder. Dispose to release the shared lock.</returns>
        internal ReadRangeIndexLock ReadRangeIndex(
            StorageSession session,
            PinnedSpanByte key,
            ref StringInput input,
            scoped Span<byte> indexSpan,
            out GarnetStatus status)
        {
            GarnetAsserts.Assert(indexSpan.Length >= IndexSizeBytes, "Insufficient space for index");

            var keyHash = session.stringBasicContext.GetKeyHash((FixedSpanByteKey)key);

            // Tree reads bypass the consistent-read store context, so replica sessions wait for this key's replay
            // explicitly, before taking a lock replay may need.
            var readSessionState = session.readSessionState;
            readSessionState?.PreSingleKeyConsistentRead(keyHash);

        Retry:
            var output = StringOutput.FromPinnedSpan(indexSpan);
            rangeIndexLocks.AcquireSharedLock(keyHash, out var sharedLockToken);

            GarnetStatus readRes;
            try
            {
                readRes = session.Read_RangeIndex(key.ReadOnlySpan, ref input, ref output, ref session.stringBasicContext);
            }
            catch
            {
                rangeIndexLocks.ReleaseLock(sharedLockToken);
                throw;
            }

            if (readRes != GarnetStatus.OK)
            {
                status = readRes;
                rangeIndexLocks.ReleaseLock(sharedLockToken);
                return new(readSessionState);
            }

            var outputSpan = output.SpanByteAndMemory.IsSpanByte
                ? output.SpanByteAndMemory.SpanByte.ReadOnlySpan
                : output.SpanByteAndMemory.MemorySpan;

            if (outputSpan.Length != IndexSizeBytes)
            {
                rangeIndexLocks.ReleaseLock(sharedLockToken);
                throw new GarnetException($"Unexpected stub size {outputSpan.Length} for RangeIndex read, expected {IndexSizeBytes}");
            }

            ref readonly var stub = ref ReadIndex(outputSpan);

            // Per-tree checkpoint barrier: one volatile read on hot path (no checkpoint = skipped).
            if (checkpointInProgress
                && WaitForTreeCheckpoint(key.ReadOnlySpan, ref output, indexSpan, ref sharedLockToken))
            {
                goto Retry;
            }

            if (stub.IsFlushed)
            {
                rangeIndexLocks.ReleaseLock(sharedLockToken);
                PromoteToTail(session, key);
                goto Retry;
            }

            if (stub.TreeHandle == nint.Zero)
            {
                rangeIndexLocks.ReleaseLock(sharedLockToken);

                // Restore under exclusive lock to prevent concurrent restores.
                // Pre-staging of data.bftree always happened earlier (PostCopyToTail-cold,
                // RIPROMOTE PostCopyUpdater-cold, or OnRecoverySnapshotRead) so RestoreTree
                // just opens data.bftree directly.
                if (!RestoreTree(session, key, keyHash, ref input, indexSpan))
                {
                    status = GarnetStatus.NOTFOUND;
                    return new(readSessionState);
                }
                goto Retry;
            }

            status = GarnetStatus.OK;
            return new(in rangeIndexLocks, sharedLockToken, readSessionState);
        }

        /// <summary>
        /// Acquire an exclusive lock for the given key hash.
        /// Used by <c>TryDeleteRangeIndex</c> to prevent concurrent data operations
        /// while a BfTree is being freed.
        /// </summary>
        /// <param name="keyHash">The key hash for lock striping.</param>
        /// <returns>An RAII lock holder. Dispose to release the exclusive lock.</returns>
        internal ExclusiveRangeIndexLock AcquireExclusiveForDelete(long keyHash)
        {
            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var exclusiveLockToken);
            return new(in rangeIndexLocks, exclusiveLockToken);
        }

        /// <summary>
        /// Issue an RMW with RIPROMOTE to copy the stub from read-only to the mutable region.
        /// CopyUpdater copies the stub bytes and clears the flushed flag.
        /// Multiple threads may race here — only one wins the CAS; others see the new record.
        /// </summary>
        private void PromoteToTail(StorageSession session, PinnedSpanByte key)
        {
            var start = Stopwatch.GetTimestamp();
            session.PromoteRangeIndexToTail(key);
            var ticks = Stopwatch.GetTimestamp() - start;
            _ = Interlocked.Increment(ref phaseStats.Promotes);
            _ = Interlocked.Add(ref phaseStats.PromoteTicks, ticks);
            var ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms >= 100)
                logger?.LogInformation("RangeIndexSoakPromoted hash={Hash} elapsedMs={Ms:F0}", HashKeyToPrefix(key.ReadOnlySpan), ms);
        }

        /// <summary>
        /// Cold path: checks if this key's tree (activated or pending) is currently being snapshotted
        /// for a checkpoint. If so, releases the shared lock, spin-waits for the snapshot to complete,
        /// and signals the caller to retry the entire read operation.
        ///
        /// <para>Lookup is by <see cref="KeyId"/> (XxHash128 → Guid) so it works uniformly for activated
        /// (TreeHandle != 0) and pending (TreeHandle == 0) entries. Hot-path Guid derivation is gated
        /// by the <see cref="checkpointInProgress"/> short-circuit so steady-state cost is one volatile
        /// bool read.</para>
        /// </summary>
        /// <param name="key">The raw key bytes.</param>
        /// <param name="output">The StringOutput from the read (passed for lifetime management).</param>
        /// <param name="indexSpan">The stub span (passed for lifetime management).</param>
        /// <param name="sharedLockToken">The shared lock token; released before waiting.</param>
        /// <returns><c>true</c> if caller should retry the read; <c>false</c> if no wait was needed.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool WaitForTreeCheckpoint(ReadOnlySpan<byte> key, ref StringOutput output,
            Span<byte> indexSpan, ref ReadOptimizedLock.LockToken sharedLockToken)
        {
            var keyId = KeyId(key);
            if (!liveIndexes.TryGetValue(keyId, out var treeEntry)
                || Volatile.Read(ref treeEntry.SnapshotPending) == 0)
                return false;

            rangeIndexLocks.ReleaseLock(sharedLockToken);
            var start = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref treeEntry.SnapshotPending) != 0)
                Thread.Yield();

            _ = Interlocked.Increment(ref phaseStats.CheckpointWaits);
            _ = Interlocked.Add(ref phaseStats.CheckpointWaitTicks, Stopwatch.GetTimestamp() - start);
            return true;
        }

        /// <summary>
        /// Restore a BfTree from its pre-staged CPR snapshot (<c>data.bftree</c>) and publish
        /// the resulting native pointer into the stub via a RIRESTORE RMW.
        /// </summary>
        /// <remarks>
        /// <para><b>Split design</b> (essential for deadlock safety): the per-key X-lock is
        /// held ONLY for the recovery + register step, then RELEASED before issuing the RMW.
        /// Holding an RI X-lock across a Tsavorite RMW would risk firing a deferred OnFlush on
        /// this thread (e.g., from the RMW's allocator drain), which would attempt the per-key
        /// shared lock for the cold case and self-deadlock with the X-lock we still hold.</para>
        ///
        /// <para>Race between releasing X and issuing RIRESTORE RMW: a concurrent DEL could
        /// fire, taking the X-lock, removing our entry from <see cref="liveIndexes"/>, and
        /// queuing the bfTree for deferred disposal via <c>storeEpoch.BumpCurrentEpoch</c>. The
        /// deferred dispose CANNOT execute until our thread's epoch advances — i.e., until the
        /// RMW's Tsavorite session-suspend point. By that time the RMW has either:
        /// <list type="bullet">
        /// <item>Succeeded — our nativePtr is now in the stub bytes; subsequent readers see
        /// it and route through ReadRangeIndex's hot path which acquires the shared RI lock
        /// before calling the native handle. The DEL's deferred dispose fires only after all
        /// such readers complete (epoch barrier).</item>
        /// <item>Returned NOTFOUND on a tombstoned key (RIRESTORE.NeedInitialUpdate=false).
        /// Caller treats this as "key was deleted concurrently" → no re-restore loop.</item>
        /// </list></para>
        ///
        /// <para>Pre-staging of <c>data.bftree</c> always happened earlier, or is deferred to this method:
        /// <list type="bullet">
        /// <item><c>PostCopyToTail</c>-cold (compaction/CopyReadsToTail with disk source).</item>
        /// <item>RIPROMOTE <c>PostCopyUpdater</c>-cold (post-eviction or post-recovery first promote).</item>
        /// <item><c>OnRecoverySnapshotRead</c> (above-FUA-at-checkpoint stubs) stages nothing; the restore
        /// opens the checkpoint snapshot itself (<see cref="TreeEntry.StagingSource"/>).</item>
        /// </list></para>
        /// </remarks>
        private bool RestoreTree(
            StorageSession session,
            PinnedSpanByte key,
            long keyHash,
            ref StringInput input,
            Span<byte> indexSpan)
        {
            var start = Stopwatch.GetTimestamp();
            _ = Interlocked.Increment(ref phaseStats.Restores);
            try
            {
                return RestoreTreeCore(session, key, keyHash, ref input, indexSpan);
            }
            finally
            {
                _ = Interlocked.Add(ref phaseStats.RestoreTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        /// <summary>
        /// Opens a pending entry's tree over its immutable contents — the checkpoint staging source, else <paramref name="dataPath"/> —
        /// through a copy-on-write overlay, so activation never copies the tree and never writes to a file other trees or
        /// snapshots share. The caller must hold the per-key exclusive lock.
        /// </summary>
        /// <returns><c>false</c> if no source file exists.</returns>
        private bool TryOpenOverlay(TreeEntry entry, string dataPath, string hashPrefix, StorageBackendType storageBackend, out BfTreeService tree, out string baseSnapshot, out string baselineSnapshot, out BfTreeRecoveryStats stats)
        {
            while (true)
            {
                var staging = entry == null ? null : Volatile.Read(ref entry.StagingSource);
                baseSnapshot = staging ?? dataPath;
                baselineSnapshot = staging ?? (entry?.StagedFrom is { } staged && staged.Matches(dataPath) ? staged.Source : null);
                if (File.Exists(baseSnapshot))
                {
                    try
                    {
                        tree = BfTreeService.RecoverFromCprSnapshotOverlay(baseSnapshot, LogOverlayWorkPath(hashPrefix), enableSnapshots: true, storageBackend, out stats);
                        return true;
                    }
                    catch (InvalidOperationException) when (staging != null && !ReferenceEquals(staging, Volatile.Read(ref entry.StagingSource)))
                    {
                        // A checkpoint moved the staging source to its own snapshot before deleting the old one.
                        continue;
                    }
                }
                else if (staging != null && !ReferenceEquals(staging, Volatile.Read(ref entry.StagingSource)))
                {
                    continue;
                }

                tree = null;
                stats = default;
                return false;
            }
        }

        private bool RestoreTreeCore(
            StorageSession session,
            PinnedSpanByte key,
            long keyHash,
            ref StringInput input,
            Span<byte> indexSpan)
        {
            nint nativePtr;
            var lockStart = Stopwatch.GetTimestamp();
            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var exclusiveLockToken);
            var lockWaitMs = Stopwatch.GetElapsedTime(lockStart).TotalMilliseconds;
            try
            {
                // Re-read stub under exclusive lock to check if another thread already restored.
                var output = StringOutput.FromPinnedSpan(indexSpan);
                var readRes = session.Read_RangeIndex(key.ReadOnlySpan, ref input, ref output, ref session.stringBasicContext);
                if (readRes != GarnetStatus.OK)
                    return false;

                var outputSpan = output.SpanByteAndMemory.IsSpanByte
                    ? output.SpanByteAndMemory.SpanByte.ReadOnlySpan
                    : output.SpanByteAndMemory.MemorySpan;
                if (outputSpan.Length < IndexSizeBytes)
                    return false;

                ref readonly var stub = ref ReadIndex(outputSpan);
                if (stub.TreeHandle != nint.Zero)
                    return true; // Another thread already restored

                var keySpan = key.ReadOnlySpan;
                var keyId = KeyId(keySpan);
                using var ownership = LockTreeOwnership(keyHash, "restore");

                // Race-resolved path: another stub for this key may already have a live tree
                // (RestoreTree ran via a different addr). Reuse it instead of opening another.
                if (liveIndexes.TryGetValue(keyId, out var existing) && existing?.Tree != null)
                {
                    nativePtr = existing.Tree.NativePtr;
                }
                else
                {
                    var hashPrefix = HashKeyToPrefix(keySpan);
                    var dataPath = LogDataPath(hashPrefix);
                    var discardStart = Stopwatch.GetTimestamp();
                    DiscardParkedTreeLocked(keyId, "restore");

                    var restoreStart = Stopwatch.GetTimestamp();
                    var overlayBefore = BfTreeService.OverlayStats;
                    if (!TryOpenOverlay(existing, dataPath, hashPrefix, (StorageBackendType)stub.StorageBackend, out var bfTree, out var baseSnapshot, out var baselineSnapshot, out var recoveryStats))
                    {
                        // Should not happen in normal flow — pre-staging guarantees data.bftree (or a checkpoint staging source)
                        // exists for any stub above FUA whose TreeHandle is 0. Assert in Debug; LogWarning + return false in
                        // Release so the affected key surfaces NOTFOUND rather than crashing the process.
                        GarnetAsserts.Assert(false, $"RestoreTree: data.bftree missing for {hashPrefix} — pre-stage invariant violated");
                        logger?.LogWarning("RestoreTree: data.bftree missing for {Hash} — pre-stage invariant violated", hashPrefix);
                        return false;
                    }

                    RegisterIndex(bfTree, keyHash, keySpan, baselineSnapshot);
                    _ = Interlocked.Add(ref phaseStats.OpenTicks, Stopwatch.GetTimestamp() - restoreStart);

                    // Re-look-up to use the WINNER's pointer (handles concurrent restorer race).
                    if (!liveIndexes.TryGetValue(keyId, out var winner) || winner?.Tree == null)
                    {
                        // We disposed our duplicate via RegisterIndex's loser-disposal path AND
                        // someone removed the winner before we observed it — extremely rare race
                        // (concurrent DEL between RegisterIndex and TryGetValue). Bail out; the
                        // next reader will retry.
                        return false;
                    }
                    nativePtr = winner.Tree.NativePtr;
                    var overlayAfter = BfTreeService.OverlayStats;
                    logger?.LogInformation("RangeIndexSoakRestored hash={Hash} tree={Tree} base={Base} lockWaitMs={LockWaitMs:F0} discardMs={DiscardMs:F0} openMs={OpenMs:F0} baseline={Baseline} overlayBaseReadMiB={BaseReadMiB:F1} native=[{NativeStats}]", hashPrefix, nativePtr, baseSnapshot, lockWaitMs, Stopwatch.GetElapsedTime(discardStart, restoreStart).TotalMilliseconds, Stopwatch.GetElapsedTime(restoreStart).TotalMilliseconds, baselineSnapshot != null, (overlayAfter.BaseReadBytes - overlayBefore.BaseReadBytes) / 1048576.0, recoveryStats);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to restore BfTree from data.bftree");
                return false;
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(exclusiveLockToken);
            }

            // RIRESTORE RMW WITHOUT the X-lock. Safe because OnFlush no longer takes any RI
            // X-lock from any code path that may fire as a deferred epoch action.
            // RIRESTORE.NeedInitialUpdate=false → if the key was concurrently DEL'd (tombstone),
            // the RMW returns NOTFOUND and the caller does not loop.
            try
            {
                ExceptionInjectionHelper.ResetAndWait(ExceptionInjectionType.RangeIndex_Restore_Pause_Before_Stub_Update);
                var updateStart = Stopwatch.GetTimestamp();
                session.RestoreRangeIndexStub(key, nativePtr);
                _ = Interlocked.Add(ref phaseStats.StubUpdateTicks, Stopwatch.GetTimestamp() - updateStart);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "RestoreRangeIndexStub RMW failed");
                return false;
            }
        }
    }
}