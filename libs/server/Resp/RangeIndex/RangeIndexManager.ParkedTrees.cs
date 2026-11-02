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
    /// disposed under the key's exclusive lock, which also guards those writers.</para>
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>How long an evicted tree stays open for reuse; <see cref="TimeSpan.Zero"/> disposes evicted trees at once.</summary>
        public TimeSpan EvictedTreeGrace { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Most evicted trees kept open at once; the oldest beyond it are disposed.</summary>
        internal const int MaxParkedTrees = 16;

        private readonly ConcurrentDictionary<Guid, ParkedTree> parkedTrees = new();
        private Timer parkedTreeSweeper;
        private int parkedTreeSweeping;
        private int parkedTreesReused;
        private int parkedTreesDisposed;

        /// <summary>Gets the number of evicted trees currently parked.</summary>
        internal int ParkedTreeCount => parkedTrees.Count;

        /// <summary>Gets the number of promotes that reused a parked tree.</summary>
        internal int ParkedTreesReusedCount => Volatile.Read(ref parkedTreesReused);

        /// <summary>Gets the number of parked trees disposed without reuse.</summary>
        internal int ParkedTreesDisposedCount => Volatile.Read(ref parkedTreesDisposed);

        private sealed class ParkedTree(BfTreeService tree, long keyHash, string hashPrefix, long flushAddress)
        {
            public readonly BfTreeService Tree = tree;
            public readonly long KeyHash = keyHash;
            public readonly string HashPrefix = hashPrefix;
            public readonly long FlushAddress = flushAddress;
            public readonly long ParkedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Parks <paramref name="tree"/>, evicted from <paramref name="entry"/>, for reuse by a promote of its last flushed record.
        /// The caller holds the key's exclusive lock and has removed <paramref name="entry"/> from <see cref="liveIndexes"/>.
        /// </summary>
        /// <returns><c>false</c> if the tree cannot be parked and must be disposed.</returns>
        private bool TryParkEvictedTreeLocked(Guid keyId, TreeEntry entry, BfTreeService tree)
        {
            var flushAddress = Volatile.Read(ref entry.LastFlushAddress);
            if (EvictedTreeGrace <= TimeSpan.Zero || flushSnapshotsStopped || flushAddress == 0)
                return false;

            DiscardParkedTreeLocked(keyId, "replaced");
            entry.Retire();
            ForgetTree(tree);
            parkedTrees[keyId] = new ParkedTree(tree, entry.KeyHash, entry.HashPrefix, flushAddress);
            logger?.LogInformation("[RangeIndexTreeParked] hash={Hash} addr=0x{Addr:x16} parked={Parked}", entry.HashPrefix, flushAddress, parkedTrees.Count);

            var sweeper = LazyInitializer.EnsureInitialized(ref parkedTreeSweeper, () =>
            {
                var period = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, EvictedTreeGrace.Ticks / 4));
                return new Timer(_ => SweepParkedTrees(), null, period, period);
            });
            if (parkedTrees.Count > MaxParkedTrees)
                _ = sweeper.Change(TimeSpan.Zero, TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, EvictedTreeGrace.Ticks / 4)));
            return true;
        }

        /// <summary>
        /// Parks a tree whose disposal waited for its flush snapshots, unless the key was registered again meanwhile.
        /// </summary>
        /// <returns><c>false</c> if the tree was not parked and must be disposed.</returns>
        private bool TryParkReleasedTree(Guid keyId, TreeEntry entry, BfTreeService tree)
        {
            if (EvictedTreeGrace <= TimeSpan.Zero)
                return false;

            rangeIndexLocks.AcquireExclusiveLock(entry.KeyHash, out var lockToken);
            try
            {
                // A registration since the eviction may have staged data.bftree over the evicted tree's file.
                return !liveIndexes.ContainsKey(keyId) && TryParkEvictedTreeLocked(keyId, entry, tree);
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
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

            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var lockToken);
            try
            {
                if (!parkedTrees.TryRemove(keyId, out var parked))
                    return false;

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
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
        }

        /// <summary>Disposes the key's parked tree, if any; the caller holds the key's exclusive lock.</summary>
        private void DiscardParkedTreeLocked(Guid keyId, string reason)
        {
            if (!parkedTrees.IsEmpty && parkedTrees.TryRemove(keyId, out var parked))
                DisposeParkedTree(parked, reason);
        }

        /// <summary>Disposes the key's parked tree, if any, before its <c>data.bftree</c> is rewritten.</summary>
        private void DiscardParkedTree(ReadOnlySpan<byte> keyBytes, string reason)
        {
            if (parkedTrees.IsEmpty)
                return;

            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(keyBytes));
            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var lockToken);
            try
            {
                DiscardParkedTreeLocked(KeyId(keyBytes), reason);
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
        }

        /// <summary>Disposes parked trees older than <see cref="EvictedTreeGrace"/>, and the oldest beyond <see cref="MaxParkedTrees"/>.</summary>
        internal void SweepParkedTrees(bool all = false)
        {
            if (parkedTrees.IsEmpty || Interlocked.Exchange(ref parkedTreeSweeping, 1) != 0)
                return;

            try
            {
                var ordered = parkedTrees.ToArray().OrderBy(static kvp => kvp.Value.ParkedAt).ToArray();
                var excess = ordered.Length - MaxParkedTrees;
                foreach (var kvp in ordered)
                {
                    var expired = all || Stopwatch.GetElapsedTime(kvp.Value.ParkedAt) >= EvictedTreeGrace;
                    if (!expired && excess <= 0)
                        break;

                    rangeIndexLocks.AcquireExclusiveLock(kvp.Value.KeyHash, out var lockToken);
                    try
                    {
                        if (parkedTrees.TryRemove(kvp))
                        {
                            excess--;
                            DisposeParkedTree(kvp.Value, all ? "discard-all" : expired ? "expired" : "over-capacity");
                        }
                    }
                    finally
                    {
                        rangeIndexLocks.ReleaseLock(lockToken);
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
            logger?.LogInformation("[RangeIndexParkedTreeDisposed] hash={Hash} addr=0x{Addr:x16} reason={Reason} parkedMs={ParkedMs:F0} disposeMs={DisposeMs:F0} parked={Parked}",
                parked.HashPrefix, parked.FlushAddress, reason, Stopwatch.GetElapsedTime(parked.ParkedAt, start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, parkedTrees.Count);
        }

        private void DisposeParkedTreeSweeper()
        {
            var sweeper = Interlocked.Exchange(ref parkedTreeSweeper, null);
            sweeper?.Dispose();
        }
    }
}
