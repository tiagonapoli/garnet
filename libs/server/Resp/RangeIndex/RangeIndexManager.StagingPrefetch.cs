// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Staging prefetch. A tree recovered from a checkpoint stays pending until first touched, and its first touch copies
    /// the whole checkpoint snapshot into <c>data.bftree</c> under the key's exclusive lock. AOF replay touches trees one at
    /// a time on the replay thread, so those copies run serially and stall recovery. Before replay, pending trees are
    /// copied to temporary files by background workers that take no locks; the first touch then renames the copy into
    /// place, waiting for it only if it is in flight.
    ///
    /// <para>Ownership of a prefetched file follows <see cref="TreeEntry.Prefetch"/>: whoever exchanges the field to null
    /// either renames the file into place or deletes it.</para>
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>Concurrent staging-prefetch copies.</summary>
        internal const int StagingPrefetchConcurrency = 2;

        /// <summary>Suffix of a prefetched copy of a pending tree's staging source.</summary>
        internal const string StagingPrefetchSuffix = ".prefetch";

        private readonly object stagingPrefetchLock = new();
        private readonly List<StagingPrefetchBatch> stagingPrefetchBatches = [];

        private int stagingPrefetchConsumed;
        private int stagingPrefetchDiscarded;
        private long stagingPrefetchSequence;

        /// <summary>Gets the number of prefetched copies renamed into place.</summary>
        internal int StagingPrefetchConsumedCount => Volatile.Read(ref stagingPrefetchConsumed);

        /// <summary>Gets the number of prefetches cancelled, failed, or discarded as stale.</summary>
        internal int StagingPrefetchDiscardedCount => Volatile.Read(ref stagingPrefetchDiscarded);

        /// <summary>Test hook run by a prefetch worker before it copies a tree; null in production.</summary>
        internal Action<string> BeforeStagingPrefetchCopyForTest;

        /// <summary>A background copy of one pending tree's staging source.</summary>
        internal sealed class StagingPrefetch(TreeEntry entry, string source, string tempPath)
        {
            public const int Queued = 0, Running = 1, Cancelled = 2;

            public readonly TreeEntry Entry = entry;
            public readonly string Source = source;
            public readonly string TempPath = tempPath;
            public readonly long QueuedAt = Stopwatch.GetTimestamp();
            public int State;

            private readonly ManualResetEventSlim done = new();
            private bool copied;

            /// <summary>Waits for the copy to finish or be skipped; returns whether the temporary file holds the source.</summary>
            public bool WaitCopied()
            {
                done.Wait();
                return Volatile.Read(ref copied);
            }

            public void Complete(bool copied)
            {
                Volatile.Write(ref this.copied, copied);
                done.Set();
            }
        }

        private sealed class StagingPrefetchBatch
        {
            public readonly ConcurrentQueue<StagingPrefetch> Queue = new();
            public readonly List<StagingPrefetch> All = [];
            public readonly List<Thread> Workers = [];
            public readonly long StartedAt = Stopwatch.GetTimestamp();
            public volatile bool Stopped;
            public long Bytes;
            public int Copied, Failed, Skipped;
        }

        /// <summary>
        /// Starts copying the staging source of every pending tree to a temporary file in the background, so AOF replay
        /// does not stage them one by one on first touch.
        /// </summary>
        /// <returns>The number of trees queued.</returns>
        internal int BeginStagingPrefetch(string reason)
        {
            var batch = new StagingPrefetchBatch();
            foreach (var entry in liveIndexes.Values)
            {
                if (entry.Tree != null || entry.IsRetired || Volatile.Read(ref entry.StagingSource) is not { } source)
                    continue;

                var prefetch = new StagingPrefetch(entry, source, $"{LogDataPath(entry.HashPrefix)}.{Interlocked.Increment(ref stagingPrefetchSequence)}{StagingPrefetchSuffix}");
                if (Interlocked.CompareExchange(ref entry.Prefetch, prefetch, null) != null)
                    continue;

                batch.All.Add(prefetch);
                batch.Queue.Enqueue(prefetch);
            }

            if (batch.All.Count == 0)
                return 0;

            lock (stagingPrefetchLock)
            {
                stagingPrefetchBatches.Add(batch);
                var workers = Math.Min(StagingPrefetchConcurrency, batch.All.Count);
                var remaining = workers;
                for (var i = 0; i < workers; i++)
                {
                    var worker = new Thread(() =>
                    {
                        RunStagingPrefetchWorker(batch);
                        if (Interlocked.Decrement(ref remaining) == 0)
                            FinishStagingPrefetchBatch(batch, reason);
                    })
                    { IsBackground = true, Name = $"RangeIndexStagingPrefetch-{i}" };
                    batch.Workers.Add(worker);
                }

                foreach (var worker in batch.Workers)
                    worker.Start();
            }

            logger?.LogInformation("[RangeIndexStagingPrefetchBegin] reason={Reason} trees={Trees} workers={Workers}", reason, batch.All.Count, batch.Workers.Count);
            return batch.All.Count;
        }

        private void RunStagingPrefetchWorker(StagingPrefetchBatch batch)
        {
            while (!batch.Stopped && batch.Queue.TryDequeue(out var prefetch))
            {
                if (Interlocked.CompareExchange(ref prefetch.State, StagingPrefetch.Running, StagingPrefetch.Queued) != StagingPrefetch.Queued)
                {
                    _ = Interlocked.Increment(ref batch.Skipped);
                    prefetch.Complete(false);
                    continue;
                }

                var startedAt = Stopwatch.GetTimestamp();
                var copied = false;
                try
                {
                    BeforeStagingPrefetchCopyForTest?.Invoke(prefetch.Entry.HashPrefix);
                    RangeIndexFileLinks.Copy(prefetch.Source, prefetch.TempPath, overwrite: true);
                    copied = true;
                    var bytes = new FileInfo(prefetch.TempPath).Length;
                    _ = Interlocked.Add(ref batch.Bytes, bytes);
                    _ = Interlocked.Increment(ref batch.Copied);
                    logger?.LogInformation("[RangeIndexStagingPrefetch] hash={Hash} bytes={Bytes} queuedMs={QueuedMs:F0} copyMs={CopyMs:F0}",
                        prefetch.Entry.HashPrefix, bytes, Stopwatch.GetElapsedTime(prefetch.QueuedAt, startedAt).TotalMilliseconds, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    // A checkpoint can re-point the staging source and purge the old one; first touch then copies itself.
                    _ = Interlocked.Increment(ref batch.Failed);
                    logger?.LogWarning("[RangeIndexStagingPrefetch] hash={Hash} failed after {Ms:F0} ms: {Error}", prefetch.Entry.HashPrefix, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, ex.Message);
                }
                finally
                {
                    prefetch.Complete(copied);
                }
            }

            // Prefetches left queued by a stop never start; complete them so no consumer waits.
            while (batch.Queue.TryDequeue(out var abandoned))
                abandoned.Complete(false);
        }

        private void FinishStagingPrefetchBatch(StagingPrefetchBatch batch, string reason)
        {
            // Copies whose tree went away or was staged from a different source are never consumed.
            var stale = 0;
            foreach (var prefetch in batch.All)
            {
                var entry = prefetch.Entry;
                if ((entry.IsRetired || !string.Equals(Volatile.Read(ref entry.StagingSource), prefetch.Source, StringComparison.Ordinal)) && TryReleaseStagingPrefetch(prefetch))
                    stale++;
            }

            logger?.LogInformation("[RangeIndexStagingPrefetchSummary] reason={Reason} trees={Trees} copied={Copied} failed={Failed} skipped={Skipped} stale={Stale} bytes={Bytes} ms={Ms:F0} stopped={Stopped}",
                reason, batch.All.Count, batch.Copied, batch.Failed, batch.Skipped, stale, Interlocked.Read(ref batch.Bytes), Stopwatch.GetElapsedTime(batch.StartedAt).TotalMilliseconds, batch.Stopped);
        }

        /// <summary>
        /// Moves <paramref name="entry"/>'s prefetched copy of <paramref name="source"/> to <paramref name="dataPath"/>,
        /// waiting for it if in flight. The caller must hold the per-key exclusive lock.
        /// </summary>
        /// <returns><c>false</c> if there is no usable prefetch; the caller then copies the source itself.</returns>
        internal bool TryTakeStagingPrefetch(TreeEntry entry, string source, string dataPath)
        {
            var prefetch = Interlocked.Exchange(ref entry.Prefetch, null);
            if (prefetch == null)
                return false;

            // Waiting behind other queued copies is slower than copying now; an unstarted prefetch is cancelled instead.
            if (Interlocked.CompareExchange(ref prefetch.State, StagingPrefetch.Cancelled, StagingPrefetch.Queued) == StagingPrefetch.Queued)
            {
                _ = Interlocked.Increment(ref stagingPrefetchDiscarded);
                return false;
            }

            // The worker takes no locks, so waiting for it under the key lock cannot deadlock.
            if (prefetch.WaitCopied() && string.Equals(prefetch.Source, source, StringComparison.Ordinal))
            {
                try
                {
                    File.Move(prefetch.TempPath, dataPath, overwrite: true);
                    _ = Interlocked.Increment(ref stagingPrefetchConsumed);
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[RangeIndexStagingPrefetch] hash={Hash} failed to move the prefetched copy into place", entry.HashPrefix);
                }
            }

            _ = Interlocked.Increment(ref stagingPrefetchDiscarded);
            DeleteStagingPrefetchFile(prefetch.TempPath);
            return false;
        }

        /// <summary>Waits for every staging-prefetch worker to finish; for tests.</summary>
        internal bool WaitForStagingPrefetchForTest(TimeSpan timeout)
        {
            StagingPrefetchBatch[] batches;
            lock (stagingPrefetchLock)
                batches = [.. stagingPrefetchBatches];

            foreach (var batch in batches)
            {
                foreach (var worker in batch.Workers)
                {
                    if (!worker.Join(timeout))
                        return false;
                }
            }

            return true;
        }

        /// <summary>Stops staging prefetch, waits for in-flight copies, and deletes every unconsumed copy.</summary>
        private void StopStagingPrefetch(string reason)
        {
            StagingPrefetchBatch[] batches;
            lock (stagingPrefetchLock)
            {
                batches = [.. stagingPrefetchBatches];
                stagingPrefetchBatches.Clear();
                foreach (var batch in batches)
                    batch.Stopped = true;
            }

            if (batches.Length == 0)
                return;

            var released = 0;
            foreach (var batch in batches)
            {
                foreach (var worker in batch.Workers)
                {
                    if (worker != Thread.CurrentThread)
                        worker.Join();
                }

                foreach (var prefetch in batch.All)
                {
                    if (TryReleaseStagingPrefetch(prefetch))
                        released++;
                }
            }

            logger?.LogInformation("[RangeIndexStagingPrefetchStop] reason={Reason} batches={Batches} released={Released}", reason, batches.Length, released);
        }

        /// <summary>Takes ownership of an unconsumed, finished prefetch and deletes its copy.</summary>
        private bool TryReleaseStagingPrefetch(StagingPrefetch prefetch)
        {
            if (Interlocked.CompareExchange(ref prefetch.Entry.Prefetch, null, prefetch) != prefetch)
                return false;

            _ = Interlocked.Increment(ref stagingPrefetchDiscarded);
            DeleteStagingPrefetchFile(prefetch.TempPath);
            return true;
        }

        /// <summary>Discards the prefetch of an entry leaving <see cref="liveIndexes"/>; an in-flight copy is deleted once it finishes.</summary>
        private void DiscardStagingPrefetch(TreeEntry entry)
        {
            if (Interlocked.Exchange(ref entry.Prefetch, null) is not { } prefetch)
                return;

            _ = Interlocked.Increment(ref stagingPrefetchDiscarded);
            if (Interlocked.CompareExchange(ref prefetch.State, StagingPrefetch.Cancelled, StagingPrefetch.Queued) == StagingPrefetch.Queued)
                return;

            // Callers may run in an eviction drain, so never wait here for a multi-gigabyte copy.
            _ = Task.Run(() =>
            {
                _ = prefetch.WaitCopied();
                DeleteStagingPrefetchFile(prefetch.TempPath);
            });
        }

        private void DeleteStagingPrefetchFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[RangeIndexStagingPrefetch] failed to delete {Path}", path);
            }
        }
    }
}
