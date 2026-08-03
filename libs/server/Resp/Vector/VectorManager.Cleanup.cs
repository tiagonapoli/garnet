// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.networking;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Methods related to cleaning up data after a Vector Set is deleted.
    /// </summary>
    public sealed partial class VectorManager
    {
        /// <summary>
        /// Used as part of scanning post-index-delete to cleanup abandoned data.
        /// </summary>
        private sealed class PostDropCleanupFunctions : IScanIteratorFunctions
        {
            private readonly StorageSession storageSession;
            private readonly FrozenSet<ulong> contexts;

            public PostDropCleanupFunctions(StorageSession storageSession, HashSet<ulong> contexts)
            {
                this.contexts = contexts.ToFrozenSet();
                this.storageSession = storageSession;
            }

            public void OnException(Exception exception, long numberOfRecords) { }
            public bool OnStart(long beginAddress, long endAddress) => true;
            public void OnStop(bool completed, long numberOfRecords) { }

            /// <inheritdoc/>
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                if (!logRecord.HasNamespace)
                {
                    // Not Vector Set, ignore
                    cursorRecordResult = CursorRecordResult.Skip;
                    return true;
                }

                var namespaceBytes = logRecord.NamespaceBytes;
                if (namespaceBytes.Length is not (sizeof(byte) or sizeof(uint)))
                {
                    // Not Vector Set, ignore
                    cursorRecordResult = CursorRecordResult.Skip;
                    return true;
                }

                var ns = ExtractContextFromNamespaces(namespaceBytes);

                // We only store the _first_ context in a batch of related contexts to delete
                // so mask it down to just the first context
                var pairedContext = ns & ~(ContextStep - 1);
                if (!contexts.Contains(pairedContext))
                {
                    // Not a target vector set, ignore
                    cursorRecordResult = CursorRecordResult.Skip;
                    return true;
                }

                VectorElementKey toDeleteKey = new(namespaceBytes, logRecord.KeyBytes);

                // Delete it
                var status = storageSession.vectorBasicContext.Delete(toDeleteKey, 0);
                if (status.IsPending)
                {
                    VectorOutput ignored = new();
                    CompletePending(ref status, ref ignored, ref storageSession.vectorBasicContext);
                }

                Debug.Assert(status.IsCompletedSuccessfully, "Nothing else should be deleting namespaced keys");

                cursorRecordResult = CursorRecordResult.Accept;
                return true;
            }
        }

        private readonly VectorSetCleanupTracker cleanupTracker = new();
        private readonly VectorSetCleanupChannel<object> cleanupTaskChannel;
        private readonly VectorSetCleanupChannel<(ulong Context, TaskCompletionSource MarkCompleted)> requestCleanupTaskChannel;

        // Pure nudge: the drop work itself lives in requestedDrops, whose entries carry the tracker
        // registrations, so a wake here has no obligation attached and needs no lease.
        private readonly Channel<object> requestDropTaskChannel;
        private readonly VectorSetCleanupWorkSet<(ulong Context, nint IndexPtr)> requestedDrops;
        private readonly ConcurrentDictionary<ulong, byte[]> potentiallyDeleted;
        private readonly Task cleanupTask;
        private readonly Task requestCleanupTask;
        private readonly Task requestDropTask;
        private readonly Func<IMessageConsumer> getTempSession;

        private bool requestCleanupTaskRunning;
        private int postCheckpointTasksRunning;

        // Pause / resume coordination for the cleanup task vs concurrent Reset.
        //
        // Cluster re-attach paths (ReplicaDisklessSync / ReplicaDiskbasedSync) call
        // storeWrapper.Reset() which tears down and rebuilds the main-store allocator.
        // The cleanup task's iterator path is safe (Tsavorite's Initializing flag causes
        // it to terminate cleanly). However the cleanup task ALSO does post-iterate RMWs
        // on metadata records (ClearDeleteInProgress / UpdateContextMetadata) — those
        // RMWs are NOT Reset-resilient and can dereference freed pagePointers and AVE.
        //
        // The pause/resume API serializes the entire cleanup-iteration (iterate + RMWs)
        // with Reset by holding cleanupGate around the whole loop body, restoring Reset's
        // documented "store is quiesced" contract.
        //
        // SemaphoreSlim used as an async-friendly mutex (initialCount=1, maxCount=1):
        // the cleanup loop takes it around each iteration; PauseCleanupAsync takes it
        // and holds until ResumeCleanup releases. Drops still enqueue items into
        // cleanupTaskChannel during a pause — the cleanup task wakes, awaits the gate
        // until the pause is lifted, then processes the backlog.
        //
        // Contract: PauseCleanupAsync callers MUST balance every successful invocation
        // with ResumeCleanup, ideally in a finally block. A held pause at Dispose time
        // would deadlock shutdown.
        private readonly SemaphoreSlim cleanupGate = new(initialCount: 1, maxCount: 1);

        /// <summary>
        /// Separate task that handles requests to drop the DiskANN side of indexes.
        /// 
        /// This needs to be in the background because we can't drop DiskANN indexes while
        /// they are in use, which means we can't drop them in response to <see cref="GarnetRecordTriggers"/>.
        /// 
        /// An additional subtlety is that indexes which are requested to be dropped cannot be recreated
        /// until that drop is processed.
        /// </summary>
        private async Task RunRequestDropTaskAsync()
        {
            while (await requestDropTaskChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Wakes are only nudges — the work itself is tracked by requestedDrops, whose entries
                // are registered with cleanupTracker before they are added and completed as they are
                // removed. So a wake carries no obligation and can simply be drained.
                while (requestDropTaskChannel.Reader.TryRead(out _))
                {
                }

                try
                {
                    // TODO: this doesn't work with non-RESP impls... which maybe we don't care about?
                    using var dropSession = (RespServerSession)getTempSession();
                    if (dropSession.activeDbId != dbId && !dropSession.TrySwitchActiveDatabaseSession(dbId))
                    {
                        throw new GarnetException($"Could not switch VectorManager cleanup session to {dbId}, initialization failed");
                    }

                    ActiveThreadSession = dropSession.storageSession;

                    await TestHookPauseInNativeDropAsync().ConfigureAwait(false);

                    // Process all pending drops
                    foreach (var (k, (context, indexPtr)) in requestedDrops)
                    {
                        long keyHash;
                        unsafe
                        {
                            fixed (byte* keyPtr = k)
                            {
                                keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedPointer(keyPtr, k.Length));
                            }
                        }

                        vectorSetLocks.AcquireExclusiveLock(keyHash, out var lockToken);

                        try
                        {
                            Service.DropIndex(context, indexPtr);
                        }
                        finally
                        {
                            vectorSetLocks.ReleaseLock(lockToken);
                            if (!requestedDrops.TryComplete(k, out _))
                            {
                                logger?.LogCritical("Drop for {key} raced with some other cleanup, this should never happen", SpanByte.ToShortString(k));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Failure during background drop of Vector Set indexes, implies storage leak");
                }
                finally
                {
                    ActiveThreadSession = null;
                }
            }
        }

        /// <summary>
        /// Separate task that allows for marking Vector Sets contexts as needing cleanup.
        /// 
        /// Cleanup is actually done by the <see cref="RunCleanupTaskAsync"/>.
        /// 
        /// Separating the two states allows for durable deletion logic, as we can block
        /// deletion of Vector Sets until the context is marked as needing deletion.
        /// </summary>
        private async Task RunRequestCleanupTaskAsync()
        {
            while (await requestCleanupTaskChannel.WaitToReadAsync().ConfigureAwait(false))
            {
                Volatile.Write(ref requestCleanupTaskRunning, true);

                // We do not need to take the cleanupGate here because we block in an OnDispose callback 
                // for this task to make progress.
                //
                // The fact that we're in an OnDispose means Reset() isn't running.

                // Disposed after the try/finally below, so the successor queued in the finally is always
                // registered before this pass releases the registrations it owns.
                using var batch = requestCleanupTaskChannel.ReadAllAvailable();

                try
                {
                    ExceptionInjectionHelper.TriggerException(ExceptionInjectionType.VectorSet_Interrupt_Delete_4);

                    // TODO: this doesn't work with non-RESP impls... which maybe we don't care about?
                    using var cleanupSession = (RespServerSession)getTempSession();
                    if (cleanupSession.activeDbId != dbId && !cleanupSession.TrySwitchActiveDatabaseSession(dbId))
                    {
                        throw new GarnetException($"Could not switch VectorManager cleanup session to {dbId}, initialization failed");
                    }

                    ref var delCtx = ref cleanupSession.storageSession.vectorBasicContext;

                    var needsUpdate = false;
                    lock (this)
                    {
                        foreach (var t in batch.Items)
                        {
                            var (contextIndex, contextValue) = ContextMetadata.DecomposeContext(t.Context);
                            if (!contextMetadatas[contextIndex].IsCleaningUp(contextIndex != 0, contextValue))
                            {
                                contextMetadatas[contextIndex].MarkCleaningUp(contextIndex != 0, contextValue);

                                _ = dirtyContextMetadatas.Add(contextIndex);

                                needsUpdate = true;
                            }
                        }
                    }

                    if (needsUpdate)
                    {
                        UpdateContextMetadata(ref delCtx);
                    }

                    ExceptionInjectionHelper.TriggerException(ExceptionInjectionType.VectorSet_Interrupt_Delete_3);

                    // Only queued on the success path. A pass that throws after marking contexts leaves them
                    // cleaningUp with no scan queued, which is what keeps the Vector Set recoverable via a
                    // re-executed DEL; WaitForCleanupCompleteAsync schedules the scan those marks still owe.
                    _ = cleanupTaskChannel.TryPublish(null);

                    CompleteMarkRequests(batch.Items, error: null);
                }
                catch (Exception e)
                {
                    CompleteMarkRequests(batch.Items, e);
                }
                finally
                {
                    Volatile.Write(ref requestCleanupTaskRunning, false);
                }
            }
        }

        /// <summary>
        /// Settle every <c>VectorSetDeleted</c> waiter in a marking pass, driven off the items the pass owns
        /// rather than off what it managed to process. A waiter blocks a RESP thread, so a pass that throws
        /// before reaching a given item must still fault it or that thread hangs forever.
        /// </summary>
        private void CompleteMarkRequests(List<(ulong Context, TaskCompletionSource MarkCompleted)> items, Exception error)
        {
            foreach (var t in items)
            {
                if (t.MarkCompleted == null)
                {
                    continue;
                }

                try
                {
                    // Idempotent, so an item settled before the throw keeps its original outcome
                    _ = error == null
                        ? t.MarkCompleted.TrySetResult()
                        : t.MarkCompleted.TrySetException(error);
                }
                catch (Exception e)
                {
                    // Best effort
                    logger?.LogError(e, "While completing Vector Set cleanup request");
                }
            }
        }

        /// <summary>
        /// Every context currently flagged as needing cleanup, or null if there is none.
        /// </summary>
        private HashSet<ulong> SnapshotContextsNeedingCleanup()
        {
            HashSet<ulong> needCleanup = null;

            lock (this)
            {
                for (var i = 0; i < contextMetadatas.Length; i++)
                {
                    var subCleanup = contextMetadatas[i].GetNeedCleanup();
                    if (subCleanup != null)
                    {
                        var offset = ContextMetadata.OffsetForContextMetadata(i);

                        needCleanup ??= [];
                        foreach (var item in subCleanup)
                        {
                            _ = needCleanup.Add(offset + item);
                        }
                    }
                }
            }

            return needCleanup;
        }

        /// <summary>
        /// Perform cleanup of deleted Vector Set element keys.
        /// 
        /// What needs cleanup is tracked as part of <see cref="ContextMetadata"/>.
        /// </summary>
        private async Task RunCleanupTaskAsync()
        {
            while (await cleanupTaskChannel.WaitToReadAsync().ConfigureAwait(false))
            {
                // Each queued item is one outstanding scan; the lease completes it on every exit path.
                while (cleanupTaskChannel.TryRead(out var work))
                {
                    using (work)
                    {
                        await cleanupGate.WaitAsync().ConfigureAwait(false);

                        try
                        {
                            // TODO: this doesn't work with non-RESP impls... which maybe we don't care about?
                            using var cleanupSession = (RespServerSession)getTempSession();
                            if (cleanupSession.activeDbId != dbId && !cleanupSession.TrySwitchActiveDatabaseSession(dbId))
                            {
                                throw new GarnetException($"Could not switch VectorManager cleanup session to {dbId}, initialization failed");
                            }

                            // Scan context needs to know how to handle objects and all callbacks, while VectorSessionFunctions is intentionally kept svelte
                            //
                            // So we use to different contexts, one to scan (strings) and one to delete (vectors)
                            // The ref locals are (re)taken after the pause seam below so they don't cross an await.

                            ExceptionInjectionHelper.TriggerException(ExceptionInjectionType.VectorSet_Interrupt_Delete_1);

                            var needCleanup = SnapshotContextsNeedingCleanup();

                            if (needCleanup == null)
                            {
                                // Previous run already got here, so bail
                                continue;
                            }

                            // Test seam: park with the needCleanup snapshot built but before the delete-scan,
                            // so a test can stream a record into one of those namespaces and prove the scan deletes it.
                            await ExceptionInjectionHelper.ResetAndWaitAsync(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan).ConfigureAwait(false);

                            // Take the scan/delete contexts after the pause so no ref local crosses the await.
                            ref var scanCtx = ref cleanupSession.storageSession.stringBasicContext;

                            PostDropCleanupFunctions callbacks = new(cleanupSession.storageSession, needCleanup);

                            // Scan whole keyspace and remove any associated data using a snapshot
                            // lookup-based push iterator. This avoids building a parallel tempKv (which
                            // would cost memory proportional to the keyspace) — IterateLookupSnapshot
                            // walks the log and uses hash-chain liveness checks bounded to the snapshot's
                            // TailAddress, so concurrent RCUs don't drop records.
                            _ = scanCtx.Session.IterateLookupSnapshot(ref callbacks);

                            ExceptionInjectionHelper.TriggerException(ExceptionInjectionType.VectorSet_Interrupt_Delete_2);

                            lock (this)
                            {
                                foreach (var cleanedUp in needCleanup)
                                {
                                    var (contextIndex, contextValue) = ContextMetadata.DecomposeContext(cleanedUp);
                                    contextMetadatas[contextIndex].FinishedCleaningUp(contextIndex != 0, contextValue);

                                    _ = dirtyContextMetadatas.Add(contextIndex);
                                }
                            }

                            ref var delCtx = ref cleanupSession.storageSession.vectorBasicContext;
                            UpdateContextMetadata(ref delCtx);
                        }
                        catch (Exception e)
                        {
                            logger?.LogError(e, "Failure during background cleanup of deleted vector sets, implies storage leak");

                            // The contexts this pass was going to clear are still marked cleaningUp, and
                            // nothing else re-queues them. Requeue before the lease is released, or the
                            // count reaches zero with the scan still owed and a drain returns early.
                            _ = cleanupTaskChannel.TryPublish(null);
                        }
                        finally
                        {
                            _ = cleanupGate.Release();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Block any new cleanup-task iteration from starting and wait for the current one
        /// (if any) to finish. Callers (e.g., cluster re-attach paths) MUST balance every
        /// invocation with <see cref="ResumeCleanup"/>, ideally in a finally block.
        ///
        /// While paused, drops still enqueue items into <see cref="cleanupTaskChannel"/>;
        /// the cleanup task wakes, awaits the gate until the pause is lifted, then
        /// processes the backlog — so no work is lost.
        ///
        /// Use this before invoking <see cref="StoreWrapper.Reset"/> on a running store, to
        /// avoid the cleanup-task scan iterator racing with the allocator teardown.
        ///
        /// The optional <paramref name="cancellationToken"/> aborts the wait if the cleanup
        /// task is mid-iteration over a large keyspace and the caller (e.g., cluster
        /// re-attach) needs to give up. If cancellation throws <see cref="OperationCanceledException"/>,
        /// the gate was NOT acquired and the caller MUST NOT call <see cref="ResumeCleanup"/>.
        /// </summary>
        public Task PauseCleanupAsync(CancellationToken cancellationToken = default)
            => cleanupGate.WaitAsync(cancellationToken);

        /// <summary>
        /// Lift the pause acquired by <see cref="PauseCleanupAsync"/>. Queued cleanup
        /// events resume processing immediately. Must be called exactly once per
        /// successful PauseCleanupAsync — typically from a finally block.
        /// </summary>
        public void ResumeCleanup() => cleanupGate.Release();

        /// <summary>
        /// True if a pending request to drop the DiskANN index behind this _specific_ key exists.
        /// </summary>
        public bool DropRequested(ReadOnlySpan<byte> key) => requestedDrops.Contains(key);

        /// <summary>
        /// Block until <see cref="DropRequested(ReadOnlySpan{byte})"/> would return false.
        /// 
        /// Do not call this while holding any Vector Set related locks, we will deadlock.
        /// </summary>
        public void WaitForDiskANNIndexDrop(ReadOnlySpan<byte> key) => requestedDrops.WaitForCompletion(key);

        /// <summary>
        /// For testing purposes, block until all cleanup requests are processed.
        /// </summary>
        internal void WaitForCleanupRequests()
        {
            while (!potentiallyDeleted.IsEmpty || requestCleanupTaskChannel.HasPending || Volatile.Read(ref requestCleanupTaskRunning) || Interlocked.CompareExchange(ref postCheckpointTasksRunning, 0, 0) != 0)
            {
                _ = Thread.Yield();
            }
        }

        /// <summary>
        /// Block until the whole cleanup pipeline has finished. Call at store-emptying boundaries (FLUSH,
        /// replica full sync) AFTER the store is emptied, and never while cleanup is paused.
        /// </summary>
        public async Task WaitForCleanupCompleteAsync()
        {
            // A marking pass that faults after durably marking its contexts leaves those marks with no scan
            // queued, which is what keeps the Vector Set recoverable via a re-executed DEL. The caller has
            // already emptied the store, so scheduling a scan here retires those stranded marks without any
            // live key to lose. Looping covers a pass that was still in flight when the barrier started and
            // marked its contexts after the first scan was queued.
            HashSet<ulong> previouslyOwed = null;

            while (true)
            {
                _ = cleanupTaskChannel.TryPublish(null);

                await cleanupTracker.WaitAllCleanupsAsync().ConfigureAwait(false);

                var stillOwed = SnapshotContextsNeedingCleanup();
                if (stillOwed == null)
                {
                    return;
                }

                if (previouslyOwed != null && previouslyOwed.SetEquals(stillOwed))
                {
                    // No progress across a full pass. Returning leaves trash behind, but blocking here
                    // would hang the flush or sync that is waiting on this barrier.
                    logger?.LogError("Vector Set cleanup could not retire {count} context(s) during drain", stillOwed.Count);
                    return;
                }

                previouslyOwed = stillOwed;
            }
        }

        /// <summary>
        /// Synchronous wrapper over <see cref="WaitForCleanupCompleteAsync"/>.
        /// </summary>
        public void WaitForCleanupComplete() => AsyncUtils.BlockingWait(WaitForCleanupCompleteAsync());

        /// <summary>
        /// Called when a Vector Set is discovered (typically via compaction) to _potentially_ be deleted.
        /// 
        /// Contexts and keys are retained for a final liveliness check when checkpointing completes.
        /// </summary>
        public void VectorSetPotentiallyDeleted(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            if (value.Length != Index.Size)
            {
                logger?.LogError("Unexpected index size on Vector Set during compaction, {actual} != {expected}", value.Length, Index.Size);
                return;
            }

            ReadIndex(value, out var context, out _, out _, out _, out _, out _, out _, out var flags, out _);

            // Record _may_ be dead, but does not imply anything about the Vector Set if it is
            if (flags.HasFlag(VectorSetFlags.SuppressCleanup))
            {
                return;
            }

            potentiallyDeleted[context] = key.ToArray();
        }

        /// <summary>
        /// Called when a checkpoint completes, signalling that Vector Sets passed to <see cref="VectorSetPotentiallyDeleted(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> should be processed.
        /// </summary>
        public unsafe void CheckpointCompleted()
        {
            _ = Interlocked.Increment(ref postCheckpointTasksRunning);

            // The discovery pass itself is cleanup work: it can still be enumerating potentiallyDeleted
            // and feeding the request-cleanup channel, so it must be visible to a drain until it ends.
            _ = cleanupTracker.RunTrackedTaskAsync(this, static self =>
            {
                try
                {
                    using var session = (RespServerSession)self.getTempSession();

                    // Just need a Vector Set command, which one doesn't matter
                    StringInput input = new(RespCommand.VINFO);
                    input.parseState.Initialize(1);

                    Span<byte> indexSpan = stackalloc byte[Index.Size];
                    var indexMem = SpanByteAndMemory.FromPinnedSpan(indexSpan);
                    StringOutput output = new(indexMem);

                    while (!self.potentiallyDeleted.IsEmpty)
                    {
                        foreach (var (context, key) in self.potentiallyDeleted)
                        {
                            if (self.potentiallyDeleted.TryRemove(context, out _))
                            {
                                bool needsDelete;

                                fixed (byte* keyPtr = key)
                                {
                                    ReadOnlySpan<byte> keySpan = new(keyPtr, key.Length);
                                    input.parseState.SetArgument(0, PinnedSpanByte.FromPinnedSpan(keySpan));

                                    var status = session.storageSession.Read_MainStore(key, ref input, ref output, ref session.storageSession.stringBasicContext);

                                    if (status != GarnetStatus.OK || !output.SpanByteAndMemory.IsSpanByte || output.SpanByteAndMemory.Length != Index.Size)
                                    {
                                        // WRONGTYPE or missing means the index is not longer live, and a wrong-sized value means we're corrupted somehow
                                        needsDelete = true;
                                    }
                                    else
                                    {
                                        // If the _context_ on this record has changed, that also means the old Vector Set is dead
                                        ReadIndex(output.SpanByteAndMemory.Span, out var liveContext, out _, out _, out _, out _, out _, out _, out _, out _);
                                        needsDelete = liveContext != context;
                                    }
                                }

                                if (needsDelete)
                                {
                                    // No need to wait for marking, since the record is already "deleted"
                                    if (!self.requestCleanupTaskChannel.TryPublish((context, null)))
                                    {
                                        self.logger?.LogWarning("Could not request delete of abandoned Vector Set {key}", SpanByte.ToShortString(key));
                                    }
                                }

                                if (!output.SpanByteAndMemory.IsSpanByte || output.SpanByteAndMemory.Length != Index.Size)
                                {
                                    output.SpanByteAndMemory.Dispose();
                                    output = new(indexMem);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _ = Interlocked.Decrement(ref self.postCheckpointTasksRunning);
                }
            });
        }
    }
}