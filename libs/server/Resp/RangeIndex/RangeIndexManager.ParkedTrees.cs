// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Garnet.common;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Evicted trees kept open for reuse. Restoring an evicted stub copies its whole flush file over <c>data.bftree</c> and
    /// reopens the tree, inside the promoting operation — on a replica, the AOF replay thread — so a burst of log writes
    /// that evicts hot stubs stalls replication for seconds per tree. An evicted tree is frozen once its stub is flushed
    /// (later writes promote the stub first, transferring the tree), so its flush file holds exactly the tree's state.
    /// The tree is therefore parked open after its last flush snapshot, and a promote of that flushed record re-registers
    /// it instead of copying the file.
    ///
    /// <para>A parked tree still owns <c>data.bftree</c>: every writer of that file discards it first, and parked trees are
    /// disposed under the key's tree ownership lock, which also guards those writers.</para>
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>
        /// How long an evicted tree stays open for reuse; <see cref="TimeSpan.Zero"/> disposes evicted trees at once. It must
        /// outlast the time from parking to the next touch of the record, which spans queued flush snapshots and checkpoints
        /// when a log flush evicts many trees at once; memory is bounded by <see cref="MaxParkedTreeBytes"/> instead.
        /// </summary>
        public TimeSpan EvictedTreeGrace { get; init; } = TimeSpan.FromMinutes(10);

        /// <summary>Most circular-buffer bytes held by parked trees; the oldest beyond it are disposed.</summary>
        public long MaxParkedTreeBytes { get; init; } = 1L << 30;

        /// <summary>Most evicted trees kept open at once; the oldest beyond it are disposed.</summary>
        internal const int MaxParkedTrees = 16;

        private readonly ConcurrentDictionary<Guid, ParkedTree> parkedTrees = new();
        private Timer parkedTreeSweeper;
        private int parkedTreeSweeping;
        private int parkedTreesReused;
        private int parkedTreesDisposed;
        private long parkedTreeBytes;

        /// <summary>Gets the number of evicted trees currently parked.</summary>
        internal int ParkedTreeCount => parkedTrees.Count;

        /// <summary>Gets the circular-buffer bytes held by parked trees.</summary>
        internal long ParkedTreeBytes => Interlocked.Read(ref parkedTreeBytes);

        /// <summary>Gets the number of promotes that reused a parked tree.</summary>
        internal int ParkedTreesReusedCount => Volatile.Read(ref parkedTreesReused);

        /// <summary>Gets the number of parked trees disposed without reuse.</summary>
        internal int ParkedTreesDisposedCount => Volatile.Read(ref parkedTreesDisposed);

        private sealed class ParkedTree(BfTreeService tree, long keyHash, string hashPrefix, long flushAddress, long cacheBytes)
        {
            public readonly BfTreeService Tree = tree;
            public readonly long KeyHash = keyHash;
            public readonly string HashPrefix = hashPrefix;
            public readonly long FlushAddress = flushAddress;
            public readonly long CacheBytes = cacheBytes;
            public readonly long ParkedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Parks <paramref name="tree"/>, evicted from <paramref name="entry"/>, for reuse by a promote of its last flushed record.
        /// The caller holds the key's tree ownership lock and has removed <paramref name="entry"/> from <see cref="liveIndexes"/>.
        /// </summary>
        /// <param name="cacheBytes">Circular-buffer size of the tree, counted against <see cref="MaxParkedTreeBytes"/>.</param>
        /// <returns><c>false</c> if the tree cannot be parked and must be disposed.</returns>
        private bool TryParkEvictedTreeLocked(Guid keyId, TreeEntry entry, BfTreeService tree, ulong cacheBytes)
        {
            var flushAddress = Volatile.Read(ref entry.LastFlushAddress);
            if (EvictedTreeGrace <= TimeSpan.Zero || flushSnapshotsStopped || flushAddress == 0 || cacheBytes > (ulong)MaxParkedTreeBytes)
                return false;

            DiscardParkedTreeLocked(keyId, "replaced");
            entry.Retire();
            ForgetTree(tree);
            parkedTrees[keyId] = new ParkedTree(tree, entry.KeyHash, entry.HashPrefix, flushAddress, (long)cacheBytes);
            var totalBytes = Interlocked.Add(ref parkedTreeBytes, (long)cacheBytes);
            logger?.LogInformation("[RangeIndexTreeParked] hash={Hash} addr=0x{Addr:x16} cacheBytes={CacheBytes} parked={Parked} parkedBytes={ParkedBytes}",
                entry.HashPrefix, flushAddress, cacheBytes, parkedTrees.Count, totalBytes);

            var period = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, EvictedTreeGrace.Ticks / 4));
            var sweeper = LazyInitializer.EnsureInitialized(ref parkedTreeSweeper, () => new Timer(_ => SweepParkedTrees(), null, period, period));
            if (parkedTrees.Count > MaxParkedTrees || totalBytes > MaxParkedTreeBytes)
                _ = sweeper.Change(TimeSpan.Zero, period);
            return true;
        }

        /// <summary>
        /// Re-registers the parked tree of the key when it was evicted from the record flushed at <paramref name="flushAddress"/>,
        /// so that record's promote need not stage <paramref name="flushPath"/>. Any other parked tree of the key is disposed.
        /// </summary>
        /// <returns><c>true</c> if the parked tree is live again.</returns>
        private bool TryReuseParkedTree(ReadOnlySpan<byte> keyBytes, long keyHash, Guid keyId, long flushAddress, string flushPath)
        {
            if (parkedTrees.IsEmpty)
                return false;

            using (LockTreeOwnership(keyHash, "parked-reuse"))
            {
                if (!parkedTrees.TryRemove(keyId, out var parked))
                    return false;

                _ = Interlocked.Add(ref parkedTreeBytes, -parked.CacheBytes);
                if (parked.FlushAddress != flushAddress || liveIndexes.ContainsKey(keyId))
                {
                    DisposeParkedTree(parked, parked.FlushAddress != flushAddress ? "address-mismatch" : "registered");
                    return false;
                }

                // The tree equals the flush file, so its next unchanged snapshot can link that file.
                RegisterPending(keyBytes, keyHash);
                RegisterIndex(parked.Tree, keyHash, keyBytes, File.Exists(flushPath) ? flushPath : null);
                _ = Interlocked.Increment(ref parkedTreesReused);
                logger?.LogInformation("[RangeIndexParkedTreeReused] hash={Hash} addr=0x{Addr:x16} parkedMs={Ms:F0} parked={Parked}",
                    parked.HashPrefix, flushAddress, Stopwatch.GetElapsedTime(parked.ParkedAt).TotalMilliseconds, parkedTrees.Count);
                return true;
            }
        }

        /// <summary>Disposes the key's parked tree, if any; the caller holds the key's tree ownership lock.</summary>
        private void DiscardParkedTreeLocked(Guid keyId, string reason)
        {
            if (!parkedTrees.IsEmpty && parkedTrees.TryRemove(keyId, out var parked))
            {
                _ = Interlocked.Add(ref parkedTreeBytes, -parked.CacheBytes);
                DisposeParkedTree(parked, reason);
            }
        }

        /// <summary>Disposes the key's parked tree, if any, before its <c>data.bftree</c> is rewritten.</summary>
        private void DiscardParkedTree(ReadOnlySpan<byte> keyBytes, string reason)
        {
            if (parkedTrees.IsEmpty)
                return;

            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(keyBytes));
            using (LockTreeOwnership(keyHash, "parked-discard"))
                DiscardParkedTreeLocked(KeyId(keyBytes), reason);
        }

        /// <summary>
        /// Disposes parked trees older than <see cref="EvictedTreeGrace"/>, and the oldest beyond <see cref="MaxParkedTrees"/>
        /// or <see cref="MaxParkedTreeBytes"/>.
        /// </summary>
        internal void SweepParkedTrees(bool all = false)
        {
            if (parkedTrees.IsEmpty || Interlocked.Exchange(ref parkedTreeSweeping, 1) != 0)
                return;

            try
            {
                var ordered = parkedTrees.ToArray().OrderBy(static kvp => kvp.Value.ParkedAt).ToArray();
                var excess = ordered.Length - MaxParkedTrees;
                var excessBytes = ParkedTreeBytes - MaxParkedTreeBytes;
                foreach (var kvp in ordered)
                {
                    var expired = all || Stopwatch.GetElapsedTime(kvp.Value.ParkedAt) >= EvictedTreeGrace;
                    var overCapacity = excess > 0 || excessBytes > 0;
                    if (!expired && !overCapacity)
                        break;

                    using (LockTreeOwnership(kvp.Value.KeyHash, "parked-sweep"))
                    {
                        if (parkedTrees.TryRemove(kvp))
                        {
                            excess--;
                            excessBytes -= kvp.Value.CacheBytes;
                            _ = Interlocked.Add(ref parkedTreeBytes, -kvp.Value.CacheBytes);
                            DisposeParkedTree(kvp.Value, all ? "discard-all" : expired ? "expired" : excess >= 0 ? "over-capacity" : "over-memory");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to sweep parked RangeIndex trees");
            }
            finally
            {
                Volatile.Write(ref parkedTreeSweeping, 0);
            }
        }

        /// <summary>Disposes every parked tree, waiting for a concurrent sweep to finish.</summary>
        private void DiscardAllParkedTrees()
        {
            while (!parkedTrees.IsEmpty)
            {
                SweepParkedTrees(all: true);
                if (!parkedTrees.IsEmpty)
                    Thread.Yield();
            }
        }

        private void DisposeParkedTree(ParkedTree parked, string reason)
        {
            var start = Stopwatch.GetTimestamp();
            try { parked.Tree.Dispose(); }
            catch (Exception ex) { logger?.LogWarning(ex, "Failed to dispose parked BfTree {Hash}", parked.HashPrefix); }
            _ = Interlocked.Increment(ref parkedTreesDisposed);
            logger?.LogInformation("[RangeIndexParkedTreeDisposed] hash={Hash} addr=0x{Addr:x16} reason={Reason} parkedMs={ParkedMs:F0} disposeMs={DisposeMs:F0} parked={Parked} parkedBytes={ParkedBytes}",
                parked.HashPrefix, parked.FlushAddress, reason, Stopwatch.GetElapsedTime(parked.ParkedAt, start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, parkedTrees.Count, ParkedTreeBytes);
        }

        private void DisposeParkedTreeSweeper()
        {
            var sweeper = Interlocked.Exchange(ref parkedTreeSweeper, null);
            sweeper?.Dispose();
        }
    }
}