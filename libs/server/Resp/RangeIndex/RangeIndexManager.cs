// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Manages the lifecycle of RangeIndex (BfTree) instances stored as fixed-size stubs
    /// in Garnet's main store.
    ///
    /// <para>Architecture: Each RangeIndex key in the store holds a <see cref="RangeIndexStub"/>
    /// containing BfTree configuration metadata and a native pointer to the live BfTree instance.
    /// The manager tracks all live BfTree instances (keyed by Guid = XxHash128 of the key bytes —
    /// same scheme as the file-name prefix), coordinates checkpoint/flush/eviction snapshots,
    /// and handles lazy restore from disk.</para>
    ///
    /// <para>File layout (flat under two roots):</para>
    /// <list type="bullet">
    /// <item><b>Log root</b> (<c>{LogDir ?? CheckpointDir ?? cwd}/Store/rangeindex/</c>) — working
    /// files (<c>&lt;hash&gt;.data.bftree</c>) and immutable per-flush snapshots
    /// (<c>&lt;hash&gt;.&lt;addr:x16&gt;.flush.bftree</c>).</item>
    /// <item><b>Checkpoint root</b> (<c>{CheckpointDir}/Store/checkpoints[_dbId]/cpr-checkpoints/&lt;token&gt;/rangeindex/</c>) —
    /// per-checkpoint snapshots (<c>&lt;hash&gt;.bftree</c>); deleted automatically when
    /// Tsavorite removes the parent token directory.</item>
    /// </list>
    ///
    /// <para>Concurrency: Data operations (RI.SET, RI.GET, RI.DEL) acquire a shared lock
    /// via <see cref="ReadRangeIndex"/>; lifecycle operations (DEL key, eviction, checkpoint)
    /// acquire an exclusive lock. See <c>RangeIndexManager.Locking.cs</c> for details.</para>
    ///
    /// <para>liveIndexes access discipline: <see cref="liveIndexes"/> is consulted on the hot
    /// path ONLY for checkpoint coordination (<see cref="WaitForTreeCheckpoint"/>). All other
    /// hot-path code uses the stub's <c>TreeHandle</c> directly.</para>
    /// </summary>
    public sealed partial class RangeIndexManager : IDisposable
    {
        /// <summary>
        /// RecordType discriminator for RangeIndex records in the unified store.
        /// Stored in <c>RecordDataHeader.RecordType</c> to distinguish RI stubs
        /// from normal strings (0) and VectorSet stubs.
        /// </summary>
        public const byte RangeIndexRecordType = 2;

        /// <summary>Size of the RangeIndex stub in bytes.</summary>
        internal const int IndexSizeBytes = RangeIndexStub.Size;

        // Flush filename layout: <hash:32>.<addr:16>.flush.bftree
        private const int HashPrefixLength = 32;
        private const int AddrHexLength = 16;
        private const string FlushSuffix = ".flush.bftree";
        private const int FlushFileNameLength = HashPrefixLength + 1 + AddrHexLength + 13; // ".flush.bftree".Length == 13
        private const int AddrStartIndex = HashPrefixLength + 1;

        /// <summary>Gets the number of live (registered) BfTree indexes (activated + pending).</summary>
        internal int LiveIndexCount => liveIndexes.Count;

        /// <summary>Gets the log-tied root directory for RI files. Used by the cluster replication layer
        /// to determine the target directory for received flush files on the replica side.</summary>
        public string RiLogRoot => riLogRoot;

        /// <summary>
        /// Tracks BfTree entries (activated + pending), keyed by <see cref="KeyId"/> = a Guid
        /// derived from the key bytes via <c>XxHash128</c>. Same hash scheme used to derive the
        /// on-disk filename prefix; collision risk is the same as for filename collisions
        /// (cryptographically negligible).
        /// </summary>
        private readonly ConcurrentDictionary<Guid, TreeEntry> liveIndexes = new();

        /// <summary>Activated entries by native tree pointer, so writers holding only a stub can count mutations.</summary>
        private readonly ConcurrentDictionary<nint, TreeEntry> entriesByTree = new();

        private readonly ILogger logger;

        /// <summary>
        /// Tsavorite store epoch, used for deferred BfTree disposal via
        /// <c>storeEpoch.BumpCurrentEpoch(...)</c>. Ensures concurrent readers using a
        /// TreeHandle are not affected by a concurrent DEL — the actual dispose runs
        /// only after all current epoch holders move past.
        /// </summary>
        private readonly LightEpoch storeEpoch;

        /// <summary>
        /// Log-tied root directory (without trailing separator). Holds the working file
        /// (<c>&lt;hash&gt;.data.bftree</c>) and immutable per-flush snapshots
        /// (<c>&lt;hash&gt;.&lt;addr:x16&gt;.flush.bftree</c>).
        /// </summary>
        private readonly string riLogRoot;

        /// <summary>
        /// Temporary directory for in-progress migration files.
        /// Created once in the constructor; cleaned up on startup to remove partial artifacts from prior crashes.
        /// </summary>
        private readonly string migrationTempDir;

        /// <summary>
        /// Checkpoint-tied parent directory (the Tsavorite <c>cpr-checkpoints/</c> directory).
        /// Per-checkpoint snapshots live under <c>{cprDir}/&lt;token&gt;/rangeindex/&lt;hash&gt;.bftree</c>.
        /// May be null if checkpointing is not enabled.
        /// </summary>
        private readonly string cprDir;

        /// <summary>
        /// Global checkpoint barrier. When non-zero, a checkpoint is snapshotting trees.
        /// RI operations check this first (one volatile read on hot path); if set, they
        /// look up by <see cref="KeyId"/> and check the per-tree
        /// <see cref="TreeEntry.SnapshotPending"/> flag.
        /// </summary>
        private volatile bool checkpointInProgress;

        /// <summary>
        /// Checkpoint token from the last recovery. Used by
        /// <see cref="RebuildFromSnapshotIfPending"/> to locate the correct checkpoint
        /// snapshot file for above-FUA stubs recovered from snapshot.
        /// </summary>
        private Guid recoveredCheckpointToken;

        /// <summary>
        /// Per-tree entry in <see cref="liveIndexes"/>. Class (not struct) so the
        /// <see cref="SnapshotPending"/> field can be updated in-place via <c>Volatile.Write</c>.
        /// </summary>
        internal sealed class TreeEntry
        {
            /// <summary>The managed BfTree wrapper owning the native tree pointer.
            /// <c>null</c> for a "pending" entry — data.bftree (or <see cref="StagingSource"/>) on disk has correct content
            /// but no native BfTree has been opened yet (awaiting <c>RestoreTree</c> activation).</summary>
            public BfTreeService Tree;

            /// <summary>Hash of the Garnet key, used for lock striping.</summary>
            public readonly long KeyHash;

            /// <summary>32-character lowercase-hex prefix derived from the key (XxHash128 → Guid("N")).
            /// Used to construct file paths under both roots.</summary>
            public readonly string HashPrefix;

            /// <summary>Immutable checkpoint snapshot holding a pending entry's contents until <c>data.bftree</c> is staged from it; null once staged.</summary>
            public string StagingSource;

            /// <summary>Immutable snapshot that <c>data.bftree</c> was last staged from for this pending entry; null when unknown.</summary>
            public StagedCopy StagedFrom;

            /// <summary>Background copy of <see cref="StagingSource"/>; owned by whoever clears this field.</summary>
            public StagingPrefetch Prefetch;

            /// <summary>Address of the record whose flush last snapshotted <see cref="Tree"/>; 0 when none.</summary>
            public long LastFlushAddress;

            /// <summary>Gets whether the entry was removed from <see cref="liveIndexes"/>.</summary>
            public bool IsRetired => Volatile.Read(ref retired) != 0;

            /// <summary>
            /// Activates a pending entry with <paramref name="tree"/>, opened from an unmodified copy of
            /// <paramref name="baselineSnapshot"/> when it is non-null, so an unchanged tree's next snapshot can link that file.
            /// </summary>
            /// <returns><c>false</c> if the entry already has a tree.</returns>
            public bool TryActivate(BfTreeService tree, string baselineSnapshot)
            {
                while (!TryClaimSnapshot())
                    Thread.Yield();
                try
                {
                    if (Volatile.Read(ref Tree) != null)
                        return false;

                    lastSnapshotPath = baselineSnapshot;
                    lastSnapshotMutations = Volatile.Read(ref MutationCount);
                    StagedFrom = null;
                    Volatile.Write(ref Tree, tree);
                    return true;
                }
                finally
                {
                    ReleaseSnapshot();
                }
            }

            /// <summary>
            /// 1 while this tree is being snapshotted for checkpoint, 0 otherwise.
            /// Set at <see cref="CheckpointTrigger.VersionShift"/> time, cleared after snapshot completes.
            /// RI data operations spin-wait on this flag when <see cref="checkpointInProgress"/> is set.
            /// </summary>
            public int SnapshotPending;

            /// <summary>
            /// Per-tree snapshot serialization atomic. 0 = idle; 1 = a snapshot is in flight.
            /// Both <see cref="GarnetRecordTriggers.OnFlush"/> and
            /// <see cref="SnapshotAllTreesForCheckpoint"/> claim this before calling
            /// <c>cpr_snapshot</c> so the two callers do not race for bftree's internal
            /// <c>snapshot_in_progress</c> flag (which would no-op one of them silently).
            /// </summary>
            public int SnapshotInProgress;

            /// <summary>Try to claim the per-tree snapshot atomic. Returns true if claimed.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryClaimSnapshot()
                => Interlocked.CompareExchange(ref SnapshotInProgress, 1, 0) == 0;

            /// <summary>Release the per-tree snapshot atomic.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ReleaseSnapshot()
                => Volatile.Write(ref SnapshotInProgress, 0);

            /// <summary>
            /// Spin to claim the per-tree snapshot atomic, take a CPR snapshot of the live tree
            /// directly into <paramref name="destinationPath"/>, then release the claim. The
            /// claim serializes against concurrent flush / checkpoint / migration snapshots on
            /// the same tree — bftree silently no-ops a <c>cpr_snapshot</c> that races its
            /// internal <c>snapshot_in_progress</c> flag, so each caller must hold the claim
            /// while it snapshots. bftree takes the destination path as a <c>cpr_snapshot</c>
            /// argument and writes a fresh self-contained file, so we snapshot straight to
            /// <paramref name="destinationPath"/>.
            /// </summary>
            /// <returns><c>false</c> without snapshotting if the entry was retired by <see cref="Retire"/>.</returns>
            public bool TrySnapshotUnderClaim(string destinationPath) => TrySnapshotUnderClaim(destinationPath, allowLink: false, out _);

            /// <summary>
            /// Like <see cref="TrySnapshotUnderClaim(string)"/>, but when <paramref name="allowLink"/> is set and the tree has not
            /// been mutated since its last snapshot, hard-links that immutable snapshot instead of writing a new one, or copies it
            /// when it is on another file system.
            /// </summary>
            public bool TrySnapshotUnderClaim(string destinationPath, bool allowLink, out TreeSnapshotKind kind) => TrySnapshotUnderClaim(destinationPath, allowLink, atomic: false, out kind);

            /// <summary>
            /// Like <see cref="TrySnapshotUnderClaim(string, bool, out TreeSnapshotKind)"/>; when <paramref name="atomic"/> is set the
            /// snapshot is written under a temporary name and renamed, so <paramref name="destinationPath"/> never holds a partial file.
            /// </summary>
            public bool TrySnapshotUnderClaim(string destinationPath, bool allowLink, bool atomic, out TreeSnapshotKind kind)
            {
                kind = TreeSnapshotKind.Written;
                while (!TryClaimSnapshot())
                    Thread.Yield();
                try
                {
                    if (Volatile.Read(ref retired) != 0)
                        return false;

                    var target = atomic ? destinationPath + InProgressSuffix : destinationPath;

                    // Writers count a mutation only after it completes, so a snapshot taken after this read reflects every counted one.
                    var mutations = Volatile.Read(ref MutationCount);
                    var unchanged = allowLink && lastSnapshotPath != null && mutations == lastSnapshotMutations;
                    if (unchanged && RangeIndexFileLinks.TryReplaceWithHardLink(lastSnapshotPath, target))
                    {
                        kind = TreeSnapshotKind.Linked;
                    }
                    else if (unchanged && RangeIndexFileLinks.TryReplaceWithCopy(lastSnapshotPath, target))
                    {
                        kind = TreeSnapshotKind.Copied;
                    }
                    else
                    {
                        // The destination may be a hard link to another snapshot, which writing in place would corrupt.
                        RangeIndexFileLinks.DeleteIfExists(target);
                        BfTreeService.CprSnapshotByPtr(Tree.NativePtr, target);
                    }

                    // Renaming replaces only the directory entry, so a destination hard-linked to another snapshot is unaffected.
                    if (atomic)
                        File.Move(target, destinationPath, overwrite: true);

                    if (allowLink)
                    {
                        lastSnapshotPath = destinationPath;
                        lastSnapshotMutations = mutations;
                    }

                    return true;
                }
                finally
                {
                    ReleaseSnapshot();
                }
            }

            /// <summary>Completed native inserts and deletes on <see cref="Tree"/>.</summary>
            public long MutationCount;

            // Guarded by the snapshot claim.
            string lastSnapshotPath;
            long lastSnapshotMutations;

            /// <summary>
            /// Mark the entry retired under the snapshot claim; must precede disposing <see cref="Tree"/>. Callers holding a
            /// stale reference (e.g. a checkpoint iterating <see cref="liveIndexes"/> without the per-key lock) then skip
            /// it, and a snapshot already in flight completes before the native tree is freed.
            /// </summary>
            public void Retire()
            {
                while (!TryClaimSnapshot())
                    Thread.Yield();
                Volatile.Write(ref retired, 1);
                ReleaseSnapshot();
            }

            int retired;

            // Background flush snapshots of this tree, chained so the tail completes after all of them.
            readonly object flushChainLock = new();
            Task flushChain = Task.CompletedTask;
            bool flushChainClosed;

            /// <summary>Queues <paramref name="work"/> after this tree's earlier flush snapshots.</summary>
            /// <returns><c>false</c> if <see cref="CloseFlushChainAsync"/> was called because the tree is being released.</returns>
            public bool TryAppendFlush(Action work, TaskScheduler scheduler)
            {
                lock (flushChainLock)
                {
                    if (flushChainClosed)
                        return false;
                    flushChain = flushChain.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, scheduler);
                    return true;
                }
            }

            /// <summary>Rejects further flush snapshots and returns a task completing after the queued ones.</summary>
            public Task CloseFlushChainAsync()
            {
                lock (flushChainLock)
                {
                    flushChainClosed = true;
                    return flushChain;
                }
            }

            /// <summary>Accepts flush snapshots again after <see cref="CloseFlushChainAsync"/>, for a released tree that is reclaimed.</summary>
            public void ReopenFlushChain()
            {
                lock (flushChainLock)
                    flushChainClosed = false;
            }

            public TreeEntry(BfTreeService tree, long keyHash, string hashPrefix)
            {
                Tree = tree;
                KeyHash = keyHash;
                HashPrefix = hashPrefix;
            }
        }

        /// <summary>How a tree snapshot file was produced.</summary>
        internal enum TreeSnapshotKind
        {
            /// <summary>Written from the live tree.</summary>
            Written,
            /// <summary>Hard-linked to the unchanged tree's previous snapshot.</summary>
            Linked,
            /// <summary>Copied from the unchanged tree's previous snapshot, which is on another file system.</summary>
            Copied,
        }

        /// <summary>Log mode for a tree snapshot: <paramref name="written"/>, "linked", or "copied" (with a "cold-" prefix kept).</summary>
        private static string SnapshotMode(string written, TreeSnapshotKind kind) => kind switch
        {
            TreeSnapshotKind.Linked => written.StartsWith("cold-", StringComparison.Ordinal) ? "cold-linked" : "linked",
            TreeSnapshotKind.Copied => written.StartsWith("cold-", StringComparison.Ordinal) ? "cold-copied" : "copied",
            _ => written,
        };

        /// <summary>A copy of an immutable snapshot into <c>data.bftree</c>, identified by the copy's length and write time.</summary>
        internal sealed record StagedCopy(string Source, long Length, DateTime LastWriteTimeUtc)
        {
            public static StagedCopy Describe(string source, string dataPath)
            {
                var info = new FileInfo(dataPath);
                return new StagedCopy(source, info.Length, info.LastWriteTimeUtc);
            }

            /// <summary>Whether <paramref name="dataPath"/> is still this copy, unwritten since staging.</summary>
            public bool Matches(string dataPath)
            {
                var info = new FileInfo(dataPath);
                return info.Exists && info.Length == Length && info.LastWriteTimeUtc == LastWriteTimeUtc;
            }
        }

        /// <summary>
        /// Creates a new <see cref="RangeIndexManager"/>. Constructed only when range index
        /// is enabled in server options; <c>GarnetServer</c> passes <c>null</c> in place of
        /// a manager when the feature is disabled, so this constructor never runs in the
        /// disabled case.
        /// </summary>
        /// <param name="riLogRoot">Log-tied root directory for working/flush files (e.g.
        /// <c>{LogDir ?? CheckpointDir ?? cwd}/Store/rangeindex</c>). MUST be a non-empty path
        /// and the directory MUST be creatable — the constructor throws otherwise so
        /// misconfiguration (missing permissions, bad path, etc.) surfaces at server startup
        /// rather than at first use.</param>
        /// <param name="cprDir">Tsavorite <c>cpr-checkpoints/</c> directory; per-checkpoint snapshots
        /// live under <c>{cprDir}/&lt;token&gt;/rangeindex/</c>. May be null if no checkpointing.</param>
        /// <param name="storeEpoch">The store's <see cref="LightEpoch"/>; used to defer native
        /// <c>BfTree.Dispose</c> + file deletion past any in-flight reader observing the
        /// stub's <c>TreeHandle</c>. May be null in unit-test scenarios with no concurrent
        /// readers; in that case disposal is performed synchronously.</param>
        /// <param name="logger">Optional logger.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="riLogRoot"/> is
        /// null or empty.</exception>
        /// <exception cref="IOException">Thrown when the riLogRoot directory cannot be
        /// created (e.g., insufficient permissions). Wraps the underlying exception.</exception>
        public RangeIndexManager(string riLogRoot, string cprDir = null,
            LightEpoch storeEpoch = null, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(riLogRoot))
                throw new ArgumentException(
                    "RangeIndexManager: riLogRoot is required.",
                    nameof(riLogRoot));

            this.riLogRoot = riLogRoot;
            this.cprDir = cprDir;
            this.storeEpoch = storeEpoch;
            this.logger = logger;
            rangeIndexLocks = new ReadOptimizedLock(Environment.ProcessorCount);

            try
            {
                Directory.CreateDirectory(riLogRoot);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "RangeIndexManager: failed to create riLogRoot {Path}", riLogRoot);
                throw new IOException(
                    $"RangeIndexManager: failed to create riLogRoot '{riLogRoot}'. " +
                    "Check that the parent directory exists and the process has write permissions.",
                    ex);
            }

            DeleteInProgressSnapshots();

            // Clean up partial migration artifacts from prior crashes, then ensure directory exists
            migrationTempDir = Path.Combine(riLogRoot, "migration-tmp");
            if (Directory.Exists(migrationTempDir))
            {
                try
                {
                    Directory.Delete(migrationTempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "RangeIndexManager: failed to clean up migration temp directory {Path}", migrationTempDir);
                    throw new Exception($"RangeIndexManager: failed to clean up migration temp directory '{migrationTempDir}'. Check for locked files or insufficient permissions.", ex);
                }
            }

            try
            {
                Directory.CreateDirectory(migrationTempDir);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "RangeIndexManager: failed to create migration temp directory {Path}", migrationTempDir);
                throw new Exception($"RangeIndexManager: failed to create migration temp directory '{migrationTempDir}'.", ex);
            }
        }

        /// <summary>
        /// Compute the unambiguous identity of a RangeIndex key as a 128-bit Guid.
        /// Same scheme used to derive the on-disk filename prefix
        /// (<see cref="HashKeyToPrefix"/>). The cryptographically negligible collision risk
        /// is the same risk we accept for filename collisions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Guid KeyId(ReadOnlySpan<byte> keyBytes) => new(XxHash128.Hash(keyBytes));

        /// <summary>
        /// Hash key bytes to a 32-character lowercase-hex filename prefix using XxHash128.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string HashKeyToPrefix(ReadOnlySpan<byte> keyBytes)
            => new Guid(XxHash128.Hash(keyBytes)).ToString("N");

        // -- Path helpers --

        /// <summary>{logRoot}/&lt;hash&gt;.data.bftree</summary>
        internal string LogDataPath(string hashPrefix)
            => Path.Combine(riLogRoot ?? string.Empty, hashPrefix + ".data.bftree");

        /// <summary>{logRoot}/&lt;hash&gt;.&lt;addr:x16&gt;.flush.bftree</summary>
        public string LogFlushPath(string hashPrefix, long logicalAddress)
            => Path.Combine(riLogRoot ?? string.Empty, $"{hashPrefix}.{logicalAddress:x16}{FlushSuffix}");

        /// <summary>{cprDir}/&lt;token&gt;/rangeindex/&lt;hash&gt;.bftree</summary>
        public string CheckpointSnapshotPath(string hashPrefix, Guid checkpointToken)
            => Path.Combine(cprDir ?? string.Empty, checkpointToken.ToString(), "rangeindex", hashPrefix + ".bftree");

        /// <summary>The directory holding per-checkpoint RI snapshots for a given token.</summary>
        internal string CheckpointSnapshotDir(Guid checkpointToken)
            => Path.Combine(cprDir ?? string.Empty, checkpointToken.ToString(), "rangeindex");

        // -- Convenience helpers used outside this class (RangeIndexOps via raw key) --
        internal string LogDataPathFor(ReadOnlySpan<byte> keyBytes) => LogDataPath(HashKeyToPrefix(keyBytes));

        /// <summary>
        /// Creates a new BfTree instance via the native interop layer.
        /// For disk-backed trees, derives the file path deterministically from the key bytes.
        /// </summary>
        internal BfTreeService CreateBfTree(
            StorageBackendType storageBackend,
            ReadOnlySpan<byte> keyBytes,
            ulong cacheSize,
            uint minRecordSize,
            uint maxRecordSize,
            uint maxKeyLen,
            uint leafPageSize)
        {
            var hashPrefix = HashKeyToPrefix(keyBytes);
            string filePath = null;

            if (storageBackend == StorageBackendType.Disk)
            {
                filePath = LogDataPath(hashPrefix);
                DiscardParkedTree(keyBytes, "create");
            }

            // Enable CPR snapshots whenever a log root is configured. The destination path is
            // supplied per call by OnFlush / SnapshotAllTreesForCheckpoint / migration, so only
            // the on/off toggle is needed here. Leave disabled only if riLogRoot is unset (test
            // scenarios). riLogRoot itself was already created in the constructor.
            bool enableSnapshots = !string.IsNullOrEmpty(riLogRoot);

            return new BfTreeService(
                storageBackend: storageBackend,
                filePath: filePath,
                enableSnapshots: enableSnapshots,
                cbSizeByte: cacheSize,
                cbMinRecordSize: minRecordSize,
                cbMaxRecordSize: maxRecordSize,
                cbMaxKeyLen: maxKeyLen,
                leafPageSize: leafPageSize);
        }

        /// <summary>
        /// Compute the leaf page size from the max record size when not explicitly specified.
        /// </summary>
        /// <param name="maxRecordSize">The configured maximum record size in bytes.</param>
        /// <returns>
        /// A power-of-two page size:
        /// <list type="bullet">
        /// <item>For <paramref name="maxRecordSize"/> ≤ 2 KB → 4 KB</item>
        /// <item>For larger values → 2.5× record size rounded to next power of 2, capped at 32 KB</item>
        /// </list>
        /// </returns>
        internal static uint ComputeLeafPageSize(uint maxRecordSize)
        {
            if (maxRecordSize <= 2048)
                return 4096;

            // 2.5x, capped at 32KB
            var target = (uint)(maxRecordSize * 2.5);
            if (target > 32768)
                target = 32768;

            // Round up to next power of 2
            return RoundUpToPowerOf2(target);
        }

        /// <summary>
        /// Rounds up to the next power of 2 using the standard bit-manipulation algorithm.
        /// </summary>
        private static uint RoundUpToPowerOf2(uint v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }

        /// <summary>
        /// Register a BfTreeService in the live index dictionary after successful creation or restore.
        /// Cold path — called once per RI.CREATE or activation (RestoreTree).
        /// </summary>
        internal void RegisterIndex(BfTreeService bfTree, long keyHash, ReadOnlySpan<byte> keyBytes, string baselineSnapshot = null)
        {
            var hashPrefix = HashKeyToPrefix(keyBytes);
            var keyId = KeyId(keyBytes);
            var newEntry = new TreeEntry(bfTree, keyHash, hashPrefix);

            // Map the pointer before snapshots can see the entry, so every write after its first snapshot is counted.
            entriesByTree[bfTree.NativePtr] = newEntry;
            if (liveIndexes.TryAdd(keyId, newEntry))
                return; // First registration for this key — done.

            // A prior entry exists. Per caller invariants:
            //   - RICREATE: invoked only when the underlying RMW reports Record.Created=true,
            //     so a prior entry cannot exist for the same key on the create path.
            //   - RestoreTree: holds the per-key rangeIndexLocks X-lock for the duration of
            //     RegisterIndex, so concurrent RegisterIndex on the same key is impossible.
            //     The only legitimate prior entry is a pending one (Tree==null) registered
            //     earlier by PreStageAndRegisterPending; we activate it in place.
            // CompareExchange makes the activation atomic so even a future caller-invariant
            // violation cannot result in two threads both believing they activated the entry
            // (one would observe a non-null prior in CompareExchange and dispose its duplicate).
            if (liveIndexes.TryGetValue(keyId, out var existing))
            {
                entriesByTree[bfTree.NativePtr] = existing;
                if (existing.TryActivate(bfTree, baselineSnapshot))
                    return; // We activated the pending entry.
            }

            _ = entriesByTree.TryRemove(bfTree.NativePtr, out _);

            logger?.LogError(
                "RegisterIndex: liveIndexes entry for {Hash} is unexpectedly already activated. " +
                "Caller invariant violated (RICREATE should fire only on Record.Created=true; " +
                "RestoreTree must hold the per-key rangeIndexLocks X-lock). Disposing duplicate.",
                hashPrefix);
            DisposeBfTreeDeferred(bfTree, "duplicate-register");
        }

        /// <summary>
        /// Defer-dispose a BfTree past any reader that may still be using its TreeHandle.
        /// Falls back to synchronous dispose when no <see cref="storeEpoch"/> is wired
        /// (unit-test scenarios with no concurrent readers).
        /// </summary>
        private void DisposeBfTreeDeferred(BfTreeService bfTree, string reason)
        {
            if (storeEpoch != null)
            {
                var loser = bfTree;
                storeEpoch.BumpCurrentEpoch(() =>
                {
                    try { loser.Dispose(); }
                    catch (Exception ex) { logger?.LogWarning(ex, "Deferred dispose failed: {Reason}", reason); }
                });
            }
            else
            {
                try { bfTree.Dispose(); }
                catch (Exception ex) { logger?.LogWarning(ex, "Synchronous dispose failed: {Reason}", reason); }
            }
        }

        /// <summary>
        /// Register a "pending" entry: data.bftree on disk has correct content (just pre-staged) but
        /// no native BfTree has been opened yet. Activated by a later <see cref="RegisterIndex"/> call
        /// from <c>RestoreTree</c>.
        /// </summary>
        internal void RegisterPending(ReadOnlySpan<byte> keyBytes, long keyHash)
        {
            var hashPrefix = HashKeyToPrefix(keyBytes);
            var keyId = KeyId(keyBytes);
            var pending = new TreeEntry(tree: null, keyHash, hashPrefix);
            // Add only if no entry exists; if one exists (activated or pending), leave it alone.
            _ = liveIndexes.TryAdd(keyId, pending);
        }

        /// <summary>
        /// Unregister the entry for a key. Disposes the native tree if present and the entry
        /// is owned by this manager. The caller must already hold the exclusive lock for this key.
        /// </summary>
        /// <returns><c>true</c> if an entry was found and removed.</returns>
        internal bool UnregisterIndex(ReadOnlySpan<byte> keyBytes)
        {
            var keyId = KeyId(keyBytes);
            DiscardParkedTreeLocked(keyId, "unregister");
            if (liveIndexes.TryRemove(keyId, out var entry))
            {
                DrainEntryFlushSnapshots(entry, "unregister");
                DiscardStagingPrefetch(entry);
                entry.Retire();
                ForgetTree(entry.Tree);
                try { entry.Tree?.Dispose(); }
                catch (Exception ex) { logger?.LogWarning(ex, "Failed to dispose BfTree on unregister"); }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Dispose every tracked tree after the store was reset. No record references them any longer, and recovery
        /// rewrites their <c>data.bftree</c> files, so a surviving tree would later be reused over replaced files.
        /// </summary>
        internal void DiscardTreesAfterStoreReset()
        {
            // The discarded log's flush files are deleted next; snapshots still queued for it are pointless.
            StopFlushSnapshots("store-reset");
            StopStagingPrefetch("store-reset");
            DiscardAllParkedTrees();
            try
            {
                DiscardTrees();
            }
            finally
            {
                // The reset log restarts its addresses, so the old truncation point no longer applies.
                Volatile.Write(ref flushFilesTruncatedBelow, 0);
                flushSnapshotsStopped = false;
            }
        }

        private void DiscardTrees()
        {
            foreach (var kvp in liveIndexes)
            {
                var entry = kvp.Value;
                rangeIndexLocks.AcquireExclusiveLock(entry.KeyHash, out var lockToken);
                try
                {
                    if (!liveIndexes.TryRemove(kvp))
                        continue;

                    if (entry.Tree != null)
                        logger?.LogWarning("RangeIndex tree {Hash} (tree={Tree}) outlived the store reset; disposing it", entry.HashPrefix, entry.Tree.NativePtr);

                    // Dispose synchronously: recovery overwrites data.bftree next, so the native tree must release it first.
                    // Readers call into the native tree only under the shared lock held off by our exclusive lock.
                    entry.Retire();
                    ForgetTree(entry.Tree);
                    try { entry.Tree?.Dispose(); }
                    catch (Exception ex) { logger?.LogWarning(ex, "Failed to dispose BfTree {Hash} after store reset", entry.HashPrefix); }
                }
                finally
                {
                    rangeIndexLocks.ReleaseLock(lockToken);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Drop any in-progress AOF-stream reassembly
            DisposeIncompleteStreamReassembly();

            StopFlushSnapshots("dispose");
            StopStagingPrefetch("dispose");
            DisposeParkedTreeSweeper();
            DiscardAllParkedTrees();
            DisposeFlushScheduler();

            foreach (var kvp in liveIndexes)
            {
                kvp.Value.Retire();
                try { kvp.Value.Tree?.Dispose(); }
                catch (Exception ex) { logger?.LogWarning(ex, "Failed to dispose BfTree {Hash}", kvp.Value.HashPrefix); }
            }
            liveIndexes.Clear();
            entriesByTree.Clear();
        }

        /// <summary>Stops tracking mutations of <paramref name="tree"/>; must precede its disposal so a reused native pointer is never confused with it.</summary>
        private void ForgetTree(BfTreeService tree)
        {
            if (tree != null)
                _ = entriesByTree.TryRemove(tree.NativePtr, out _);
        }

        /// <summary>Records a completed insert or delete on <paramref name="treePtr"/>, invalidating snapshot reuse for its tree.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void NoteTreeMutated(nint treePtr)
        {
            if (entriesByTree.TryGetValue(treePtr, out var entry))
                _ = Interlocked.Increment(ref entry.MutationCount);
        }

        /// <summary>
        /// Atomically pre-stage <c>data.bftree</c> from <c>&lt;srcAddr:x16&gt;.flush.bftree</c>
        /// and register a pending entry in <see cref="liveIndexes"/> so a subsequent checkpoint
        /// will capture it via <see cref="SnapshotAllTreesForCheckpoint"/>.
        ///
        /// <para>Called from RIPROMOTE PostCopyUpdater (cold case: src.TreeHandle == 0) and
        /// from <c>PostCopyToTail</c> (compaction with disk source).</para>
        ///
        /// <para>Concurrency: takes the per-key EXCLUSIVE rangeIndex lock for the duration of the
        /// file copy. This is required because <c>CASRecordIntoChain</c> unseals dst immediately
        /// on CAS-success (in <c>Helpers.cs.CASRecordIntoChain</c>), so by the time this trigger
        /// fires, concurrent readers can already observe dst with <c>TreeHandle == 0</c> and
        /// invoke <c>RestoreTree</c>, which opens <c>data.bftree</c> under its own per-key
        /// exclusive lock. Holding the exclusive lock here blocks <c>RestoreTree</c> until the
        /// file is fully written, preventing it from observing a partial <c>data.bftree</c>.</para>
        ///
        /// <para>A direct copy over <c>data.bftree</c> is sufficient under this lock — the
        /// exclusive lock serializes against any reader that would open <c>data.bftree</c>, and
        /// against other concurrent <c>PreStageAndRegisterPending</c> calls for the same key.
        /// A crash mid-copy is self-healing: post-recovery either <c>OnRecoverySnapshotRead</c>
        /// (above-FUA stub) or the next RIPROMOTE-cold (IsFlushed=true stub) re-pre-stages and
        /// overwrites any partial file before <c>RestoreTree</c> can observe it.</para>
        /// </summary>
        internal void PreStageAndRegisterPending(ReadOnlySpan<byte> keyBytes, long srcFlushAddress, bool reuseParkedTree = false)
        {
            if (string.IsNullOrEmpty(riLogRoot))
                return;

            var hashPrefix = HashKeyToPrefix(keyBytes);
            var snapshotPath = LogFlushPath(hashPrefix, srcFlushAddress);
            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(keyBytes));

            // An evicted tree whose flush snapshots are still queued holds this record's state; only a promote reclaims it,
            // because its retrying read publishes the tree into the new record at once.
            if (reuseParkedTree && TryReclaimDeferredTree(keyHash, KeyId(keyBytes), srcFlushAddress))
                return;

            WaitForFlushFile(KeyId(keyBytes), snapshotPath, "prestage");

            // The tree evicted from this record may still be open, holding the flush file's contents without a copy. Only a
            // promote reuses it: its retrying read publishes the tree into the new record at once.
            if (reuseParkedTree && TryReuseParkedTree(keyBytes, keyHash, KeyId(keyBytes), srcFlushAddress, snapshotPath))
                return;

            if (!File.Exists(snapshotPath))
            {
                // Per Invariant 2 (Per-flush snapshot invariant), the per-flush file must exist
                // for any IsFlushed=true stub at addr >= BeginAddress. If it's missing here, the
                // invariant has been violated (likely a race with a concurrent OnTruncate, or
                // external file deletion). Log loudly as ERROR — recovering from any other source
                // file would risk restoring the wrong tree version. Leave the destination record
                // with TreeHandle=0 and no pending entry; the next RestoreTree will return NOTFOUND
                // for the affected key, surfacing the data loss explicitly rather than silently
                // restoring incorrect data.
                logger?.LogError("PreStageAndRegisterPending: invariant violation — source flush file missing: {Path}. " +
                    "The destination record at the tail will have no pending entry; subsequent RestoreTree " +
                    "will return NOTFOUND for the affected key. NOT falling back to any other flush file " +
                    "to avoid restoring an incorrect tree version.", snapshotPath);
                DiscardStaleDataFile(keyBytes, keyHash, hashPrefix);
                return;
            }

            var dataPath = LogDataPath(hashPrefix);

            // Acquire the per-key exclusive lock for the duration of the file copy AND the
            // pending-entry registration. This blocks any concurrent RestoreTree (which also
            // takes the exclusive lock) from observing a partial data.bftree.
            // riLogRoot was created in the constructor.
            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var lockToken);
            try
            {
                DiscardParkedTreeLocked(KeyId(keyBytes), "staged");
                var copyStart = Stopwatch.GetTimestamp();
                RangeIndexFileLinks.Copy(snapshotPath, dataPath, overwrite: true);
                logger?.LogInformation("RangeIndexSoakPreStaged hash={Hash} bytes={Bytes} copyMs={Ms:F0}", hashPrefix, new FileInfo(dataPath).Length, Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds);

                var keyId = KeyId(keyBytes);
                var entry = liveIndexes.GetOrAdd(keyId, _ => new TreeEntry(tree: null, keyHash, hashPrefix));
                if (entry.Tree == null)
                {
                    Volatile.Write(ref entry.StagingSource, null);
                    entry.StagedFrom = StagedCopy.Describe(snapshotPath, dataPath);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "PreStageAndRegisterPending: copy/register failed for {Hash}; " +
                    "destination record will have no pending entry; subsequent RestoreTree will return NOTFOUND",
                    hashPrefix);
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
        }

        /// <summary>
        /// Deletes the working file of a tree whose source flush file is missing, unless a tree of the key is open or staged,
        /// so that <c>RestoreTree</c> reports the key as not found instead of opening whatever an earlier tree left there.
        /// </summary>
        private void DiscardStaleDataFile(ReadOnlySpan<byte> keyBytes, long keyHash, string hashPrefix)
        {
            rangeIndexLocks.AcquireExclusiveLock(keyHash, out var lockToken);
            try
            {
                if (liveIndexes.TryGetValue(KeyId(keyBytes), out var entry) && entry != null)
                    return;

                DiscardParkedTreeLocked(KeyId(keyBytes), "source-missing");
                var dataPath = LogDataPath(hashPrefix);
                if (File.Exists(dataPath))
                {
                    File.Delete(dataPath);
                    logger?.LogError("PreStageAndRegisterPending: deleted stale {Path} so the key is reported as not found", dataPath);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "PreStageAndRegisterPending: failed to delete the stale working file of {Hash}", hashPrefix);
            }
            finally
            {
                rangeIndexLocks.ReleaseLock(lockToken);
            }
        }

        /// <summary>
        /// Register a pending entry whose <c>data.bftree</c> is staged lazily from
        /// <c>cpr-checkpoints/&lt;recoveredCheckpointToken&gt;/rangeindex/&lt;hash&gt;.bftree</c> on first use.
        /// Called from <c>OnRecoverySnapshotRead</c> for above-FUA-at-checkpoint stubs.
        ///
        /// <para>The recovered checkpoint is retained until a newer checkpoint commits, and that checkpoint's
        /// pending-entry snapshot re-points <see cref="TreeEntry.StagingSource"/> at itself, so the source
        /// outlives every entry that still references it.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when this method is called without
        /// the recovery state required to locate checkpoint snapshots
        /// (<see cref="cprDir"/> is empty or <see cref="recoveredCheckpointToken"/> is Guid.Empty).
        /// This indicates that <c>OnRecoverySnapshotRead</c> fired without
        /// <c>OnRecovery(token)</c> having captured the recovered checkpoint token first —
        /// a wiring bug that would otherwise silently lose the recovered tree.</exception>
        internal void RebuildFromSnapshotIfPending(ReadOnlySpan<byte> keyBytes)
        {
            if (string.IsNullOrEmpty(cprDir) || recoveredCheckpointToken == Guid.Empty)
                throw new InvalidOperationException(
                    "RebuildFromSnapshotIfPending: recovery state missing " +
                    $"(cprDir empty: {string.IsNullOrEmpty(cprDir)}, recoveredCheckpointToken empty: {recoveredCheckpointToken == Guid.Empty}). " +
                    "This indicates OnRecoverySnapshotRead fired without OnRecovery(token) " +
                    "having captured the recovered checkpoint token. The recovered RangeIndex tree would " +
                    "otherwise be silently lost.");

            var hashPrefix = HashKeyToPrefix(keyBytes);
            var snapshotPath = CheckpointSnapshotPath(hashPrefix, recoveredCheckpointToken);
            if (!File.Exists(snapshotPath))
            {
                // Below-FUA stubs have no checkpoint snapshot; RIPROMOTE PostCopyUpdater handles
                // them lazily via the per-flush snapshot file on first access. NOT an error.
                logger?.LogDebug("OnRecoverySnapshotRead: snapshot absent for {Hash} — RIPROMOTE will handle lazily", hashPrefix);
                return;
            }

            // Stage data.bftree lazily: a node that is fully resynced, or that never touches this
            // tree, must not pay a full-tree copy for it during recovery.
            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(keyBytes));
            var keyId = KeyId(keyBytes);
            var entry = liveIndexes.GetOrAdd(keyId, _ => new TreeEntry(tree: null, keyHash, hashPrefix));
            if (entry.Tree == null)
                Volatile.Write(ref entry.StagingSource, snapshotPath);
        }

        /// <summary>
        /// Stage <c>data.bftree</c> from a pending entry's deferred <see cref="TreeEntry.StagingSource"/>, if any.
        /// The caller must hold the per-key exclusive lock.
        /// </summary>
        internal void StageDeferredData(TreeEntry entry, string dataPath)
        {
            while (Volatile.Read(ref entry.StagingSource) is { } source)
            {
                var copyStart = Stopwatch.GetTimestamp();
                if (TryTakeStagingPrefetch(entry, source, dataPath))
                {
                    Volatile.Write(ref entry.StagingSource, null);
                    entry.StagedFrom = StagedCopy.Describe(source, dataPath);
                    logger?.LogInformation("RangeIndexSoakLazyStaged hash={Hash} bytes={Bytes} prefetched=true waitMs={Ms:F0}", entry.HashPrefix, new FileInfo(dataPath).Length, Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds);
                    continue;
                }

                try
                {
                    RangeIndexFileLinks.Copy(source, dataPath, overwrite: true);
                }
                catch (FileNotFoundException) when (!ReferenceEquals(source, Volatile.Read(ref entry.StagingSource)))
                {
                    // A checkpoint moved the source to its own snapshot before deleting the old one.
                    continue;
                }

                Volatile.Write(ref entry.StagingSource, null);
                entry.StagedFrom = StagedCopy.Describe(source, dataPath);
                logger?.LogInformation("RangeIndexSoakLazyStaged hash={Hash} bytes={Bytes} copyMs={Ms:F0}", entry.HashPrefix, new FileInfo(dataPath).Length, Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds);
            }
        }

        /// <summary>
        /// Copy the contents of a tree with no live native instance to <paramref name="destinationPath"/>, reading
        /// the deferred staging source of <paramref name="entry"/> when present and <paramref name="dataPath"/> otherwise.
        /// </summary>
        /// <returns><c>false</c> if no source file exists.</returns>
        private static bool CopyPendingContents(TreeEntry entry, string dataPath, string destinationPath, bool overwrite)
        {
            while (true)
            {
                var source = (entry == null ? null : Volatile.Read(ref entry.StagingSource)) ?? dataPath;
                try
                {
                    RangeIndexFileLinks.Copy(source, destinationPath, overwrite);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    // Retry only if a checkpoint moved the staging source while we were copying.
                    var current = (entry == null ? null : Volatile.Read(ref entry.StagingSource)) ?? dataPath;
                    if (ReferenceEquals(source, current))
                        return false;
                }
            }
        }

        /// <summary>
        /// Store the recovered checkpoint token for use by
        /// <see cref="RebuildFromSnapshotIfPending"/>.
        /// </summary>
        internal void SetRecoveredCheckpointToken(Guid token) => recoveredCheckpointToken = token;

        /// <summary>
        /// Log an OnFlush invariant violation: <see cref="RangeIndexStub.TreeHandle"/> was zero
        /// (no live tree) but the working file <c>data.bftree</c> was missing. Per Invariant 5
        /// (Pending entry invariant), every above-FUA stub with TreeHandle=0 must have a pending
        /// entry in <see cref="liveIndexes"/> and a pre-staged <c>data.bftree</c> on disk; if
        /// <c>data.bftree</c> is missing, something has corrupted that invariant (e.g. a prior
        /// pre-stage failure, or external file deletion). The caller (<see cref="GarnetRecordTriggers.OnFlush"/>)
        /// must NOT set <see cref="RangeIndexStub.IsFlushed"/> in this case — that would put the
        /// record into an unrestorable state where RIPROMOTE-cold would also fail and the key
        /// would be permanently broken.
        /// </summary>
        internal void LogOnFlushInvariantViolation(string hashPrefix, long logicalAddress)
        {
            logger?.LogError("OnFlush: invariant violation — TreeHandle=0 but data.bftree missing for {Hash} at addr 0x{Addr:x16}; skipping IsFlushed (record will route through RestoreTree which will return NOTFOUND for the affected key)",
                hashPrefix, logicalAddress);
        }

        /// <summary>
        /// Snapshot a BfTree's current contents to its per-flush snapshot file. Called from
        /// <see cref="GarnetRecordTriggers.OnFlush"/> when a page transitions to read-only.
        ///
        /// <para><b>Live case</b> (<c>stub.TreeHandle != 0</c>): take a CPR snapshot via the
        /// native handle. CPR is concurrent-safe with workers (no per-key X-lock needed).
        /// Per-tree atomic <see cref="TreeEntry.SnapshotInProgress"/> serializes against
        /// concurrent <see cref="SnapshotAllTreesForCheckpoint"/> for the same tree (otherwise
        /// bftree's internal <c>snapshot_in_progress</c> would no-op one of them).</para>
        ///
        /// <para><b>Cold case</b> (<c>stub.TreeHandle == 0</c>): the stub was just CAS'd at the
        /// tail by PostCopyToTail-cold or RIPROMOTE-PostCopyUpdater-cold; PreStage already
        /// copied <c>&lt;srcAddr&gt;.flush.bftree → data.bftree</c> but RestoreTree hasn't
        /// activated a live tree yet. ANOTHER stub for the same key may have a live tree in
        /// <see cref="liveIndexes"/> (RestoreTree ran against a different addr's stub) — workers
        /// using that tree would write to <c>data.bftree</c> concurrently, making
        /// copying <c>data.bftree</c> unsafe. So:
        /// <list type="bullet">
        /// <item>Acquire per-key SHARED RI lock — blocks RestoreTree's X-lock from registering
        /// a new tree during our copy. Deadlock-free: S-vs-S compatible with hot path; no path
        /// holds an RI X-lock across a Tsavorite op that fires deferred OnFlush.</item>
        /// <item>If <see cref="liveIndexes"/> has a live tree under another stub → use
        /// CPR snapshot (concurrent-safe with workers).</item>
        /// <item>Else → safe to copy <c>data.bftree → flushPath</c>.</item>
        /// </list></para>
        ///
        /// <para>Sets <see cref="RangeIndexStub.IsFlushed"/> on the in-memory stub on success so
        /// the next data operation routes through <see cref="GarnetRecordTriggers.PostCopyToTail"/>
        /// or RIPROMOTE PostCopyUpdater to re-anchor the tree at the tail.</para>
        /// </summary>
        /// <param name="key">The raw key bytes (used for hash prefix + lock acquisition).</param>
        /// <param name="valueSpan">The store value span containing the stub.</param>
        /// <param name="logicalAddress">The logical address of the record being flushed.</param>
        internal void SnapshotTreeForFlush(ReadOnlySpan<byte> key, Span<byte> valueSpan, long logicalAddress)
        {
            ref readonly var stub = ref ReadIndex(valueSpan);

            // Stale source whose ownership was transferred to a newer record at the tail: no-op.
            if (stub.IsTransferred)
                return;

            // Need riLogRoot for any disk artifact (both backends use it as staging directory).
            if (string.IsNullOrEmpty(riLogRoot))
                return;

            var hashPrefix = HashKeyToPrefix(key);
            var dataPath = LogDataPath(hashPrefix);
            var flushPath = LogFlushPath(hashPrefix, logicalAddress);
            var keyId = KeyId(key);
            var start = Stopwatch.GetTimestamp();
            string mode;

            if (stub.TreeHandle != nint.Zero)
            {
                // Live case: stub directly references a live tree. CPR snapshot via the handle.
                if (!liveIndexes.TryGetValue(keyId, out var entry) || entry?.Tree == null)
                {
                    // Edge case: stub.TreeHandle points at a tree no longer in liveIndexes
                    // (DEL deferred-disposed it but stub bytes weren't updated). Treat as cold.
                    mode = SnapshotForFlushCold(key, hashPrefix, dataPath, flushPath, valueSpan, logicalAddress);
                }
                else if (TryEnqueueFlushSnapshot(entry, logicalAddress, flushPath))
                {
                    // The worker logs the snapshot; only the time this flush thread spent is logged here.
                    Volatile.Write(ref entry.LastFlushAddress, logicalAddress);
                    SetFlushedFlag(valueSpan);
                    var enqueueMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (enqueueMs >= 10)
                        logger?.LogInformation("[RangeIndexFlushEnqueueSlow] hash={Hash} addr=0x{Addr:x16} ms={Ms:F0}", hashPrefix, logicalAddress, enqueueMs);
                    return;
                }
                else if (!entry.TrySnapshotUnderClaim(flushPath, allowLink: true, out var kind))
                {
                    mode = SnapshotForFlushCold(key, hashPrefix, dataPath, flushPath, valueSpan, logicalAddress);
                }
                else
                {
                    Volatile.Write(ref entry.LastFlushAddress, logicalAddress);
                    SetFlushedFlag(valueSpan);
                    mode = SnapshotMode("live", kind);
                }
            }
            else
            {
                mode = SnapshotForFlushCold(key, hashPrefix, dataPath, flushPath, valueSpan, logicalAddress);
            }

            LogFlushSnapshot(hashPrefix, logicalAddress, flushPath, mode, start);
        }

        /// <summary>Logs one flush snapshot with its cost and the flush files retained afterwards.</summary>
        /// <param name="queueMs">Time the snapshot waited for a background writer, or null when taken synchronously.</param>
        private void LogFlushSnapshot(string hashPrefix, long logicalAddress, string flushPath, string mode, long start, double? queueMs = null)
        {
            if (logger is null || !logger.IsEnabled(LogLevel.Information))
                return;

            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var bytes = mode is null ? 0 : TryGetFileLength(flushPath);
            var (retained, retainedBytes) = MeasureFlushFiles();
            logger.LogInformation("[RangeIndexFlushSnapshot] hash={Hash} addr=0x{Addr:x16} mode={Mode} async={Async} bytes={Bytes} ms={Ms:F0} queueMs={QueueMs:F0} pending={Pending} retainedFiles={Retained} retainedBytes={RetainedBytes} throttledTotalMs={ThrottledMs:F0}",
                hashPrefix, logicalAddress, mode ?? "failed", queueMs.HasValue, bytes, ms, queueMs ?? 0, pendingFlushSnapshots.Count, retained, retainedBytes, BfTreeService.SnapshotWriteThrottledTime.TotalMilliseconds);
        }

        private static long TryGetFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return -1; }
        }

        /// <summary>Counts the flush files under the log root and their total size.</summary>
        private (int Files, long Bytes) MeasureFlushFiles()
        {
            var files = 0;
            long bytes = 0;
            try
            {
                foreach (var (path, _, _) in EnumerateFlushFiles())
                {
                    var length = TryGetFileLength(path);
                    if (length < 0)
                        continue;
                    files++;
                    bytes += length;
                }
            }
            catch (IOException)
            {
            }

            return (files, bytes);
        }

        /// <summary>
        /// OnFlush cold-case: stub.TreeHandle == 0. The pre-staged <c>data.bftree</c> is the
        /// only candidate source for capturing this flush. We must serialize against any
        /// concurrent RestoreTree (X-lock) that could activate a tree mid-copy and start
        /// writing to data.bftree from a worker thread. Use SHARED RI lock — deadlock-free
        /// because no firing-thread holds an X-lock across a Tsavorite op that fires deferred
        /// OnFlush (RestoreTree releases X before its RMW; PreStage / DisposeTreeUnderLock
        /// don't issue Tsavorite ops while holding X; OnFlush itself doesn't take X).
        /// </summary>
        /// <returns>How the snapshot was taken, or <c>null</c> if no source existed.</returns>
        private string SnapshotForFlushCold(ReadOnlySpan<byte> key, string hashPrefix, string dataPath, string flushPath, Span<byte> valueSpan, long logicalAddress)
        {
            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(key));
            var lockIndex = rangeIndexLocks.CalculateIndexWithHint(keyHash);

            bool releaseLock;
            ReadOptimizedLock.LockToken sharedLockToken;
            if (SharedLockHeldForKeyHash.Value.Held && SharedLockHeldForKeyHash.Value.Index == lockIndex)
            {
                // Do not acquire shared lock if already held for this index
                releaseLock = false;
                sharedLockToken = default;
            }
            else
            {
                releaseLock = true;
                rangeIndexLocks.AcquireSharedLock(keyHash, out sharedLockToken);
            }

            try
            {
                // Re-check: a tree may have become live under a different stub for this key
                // (RestoreTree completed before we acquired the shared lock).
                if (liveIndexes.TryGetValue(KeyId(key), out var entry) && entry?.Tree != null && entry.TrySnapshotUnderClaim(flushPath, allowLink: true, out var kind))
                {
                    Volatile.Write(ref entry.LastFlushAddress, logicalAddress);
                    SetFlushedFlag(valueSpan);
                    return SnapshotMode("cold-live", kind);
                }

                // No live tree exists for this key (S-lock blocks RestoreTree from activating
                // one mid-copy). data.bftree or the deferred staging source is stable — no concurrent writer.
                // Recovery can re-flush a record at an address whose flush file survives from before the crash.
                if (!CopyPendingContents(entry, dataPath, flushPath, overwrite: true))
                {
                    LogOnFlushInvariantViolation(hashPrefix, logicalAddress);
                    return null; // do NOT set IsFlushed
                }

                SetFlushedFlag(valueSpan);
                return "cold-copy";
            }
            finally
            {
                if (releaseLock)
                {
                    rangeIndexLocks.ReleaseLock(sharedLockToken);
                }
            }
        }

        /// <summary>
        /// Set the checkpoint barrier. Called at version shift (PREPARE → IN_PROGRESS).
        /// Marks all entries (activated + pending) as snapshot-pending, then sets the global flag.
        /// </summary>
        internal void SetCheckpointBarrier(Guid checkpointToken)
        {
            foreach (var kvp in liveIndexes)
                Volatile.Write(ref kvp.Value.SnapshotPending, 1);
            checkpointInProgress = true;
        }

        /// <summary>
        /// Clear the checkpoint barrier. Called after snapshot completes at WAIT_FLUSH.
        /// </summary>
        internal void ClearCheckpointBarrier()
        {
            checkpointInProgress = false;
            foreach (var kvp in liveIndexes)
                Volatile.Write(ref kvp.Value.SnapshotPending, 0);
        }

        /// <summary>
        /// Snapshot all live BfTrees for a checkpoint. Called at FlushBegin.
        ///
        /// <para>For each entry with <see cref="TreeEntry.SnapshotPending"/> set:
        /// <list type="bullet">
        /// <item>Activated entries (Tree != null) → take a CPR snapshot via the tree handle
        /// straight to the checkpoint destination (<see cref="TreeEntry.TrySnapshotUnderClaim(string)"/>); entries retired by a concurrent deferred dispose are skipped.
        /// With <see cref="BackgroundCheckpointSnapshots"/> the snapshot is queued instead and the tree's barrier is released at once.</item>
        /// <item>Pending entries (Tree == null) → copy the deferred staging source, or <c>data.bftree</c>
        /// once staged (no live tree to snapshot from).</item>
        /// </list></para>
        ///
        /// <para>Uses the per-tree atomic <see cref="TreeEntry.SnapshotInProgress"/> to serialize
        /// against concurrent <see cref="SnapshotTreeForFlush"/> for the same tree. Per-key
        /// X-lock is NOT taken here — that lock would deadlock if any deferred OnFlush fired
        /// on the checkpoint thread while it held S-locks on hot-path readers' shards.</para>
        ///
        /// <para>Memory-backed trees are also captured via CPR snapshot (bftree supports
        /// CPR for memory-backed trees uniformly with disk-backed).</para>
        ///
        /// <para>Failure is fatal — the exception propagates to the state machine driver.</para>
        /// </summary>
        internal void SnapshotAllTreesForCheckpoint(Guid checkpointToken)
        {
            try
            {
                if (string.IsNullOrEmpty(cprDir))
                    return;

                var snapshotDir = CheckpointSnapshotDir(checkpointToken);
                var allStart = Stopwatch.GetTimestamp();
                var trees = 0;
                var queued = 0;
                long totalBytes = 0;

                foreach (var kvp in liveIndexes)
                {
                    var entry = kvp.Value;

                    if (Volatile.Read(ref entry.SnapshotPending) == 0)
                        continue;

                    try
                    {
                        Directory.CreateDirectory(snapshotDir);
                        var checkpointPath = CheckpointSnapshotPath(entry.HashPrefix, checkpointToken);
                        var treeStart = Stopwatch.GetTimestamp();
                        string mode = null;

                        if (BackgroundCheckpointSnapshots && entry.Tree is not null)
                        {
                            if (!TryEnqueueCheckpointSnapshot(entry, checkpointToken, checkpointPath))
                                logger?.LogWarning("SnapshotAllTreesForCheckpoint: skipped {Hash}; tree is being released", entry.HashPrefix);
                            else
                                queued++;
                        }
                        else if (entry.Tree is not null)
                        {
                            // Snapshot straight to the token-tagged checkpoint destination under
                            // the per-tree claim (serializes against concurrent OnFlush / migration
                            // on the same tree).
                            if (!entry.TrySnapshotUnderClaim(checkpointPath, allowLink: true, out var kind))
                                logger?.LogWarning("SnapshotAllTreesForCheckpoint: skipped {Hash}; tree was disposed after removal from liveIndexes", entry.HashPrefix);
                            else
                                mode = SnapshotMode("live", kind);
                        }
                        else
                        {
                            // Pending entry: its contents are in the deferred staging source, or in
                            // data.bftree once staged. No live tree means no concurrent writer.
                            var dataPath = LogDataPath(entry.HashPrefix);
                            var source = Volatile.Read(ref entry.StagingSource);
                            if (!CopyPendingContents(entry, dataPath, checkpointPath, overwrite: true))
                                logger?.LogWarning("SnapshotAllTreesForCheckpoint: data.bftree missing for pending {Hash}", entry.HashPrefix);
                            else
                            {
                                mode = "pending-copy";
                                if (source != null)
                                {
                                    // Re-point staging at this checkpoint so purging the older one cannot strand the entry.
                                    _ = Interlocked.CompareExchange(ref entry.StagingSource, checkpointPath, source);
                                }
                            }
                        }

                        if (mode != null)
                        {
                            var bytes = TryGetFileLength(checkpointPath);
                            trees++;
                            totalBytes += Math.Max(bytes, 0);
                            logger?.LogInformation("[RangeIndexCheckpointSnapshot] token={Token} hash={Hash} mode={Mode} bytes={Bytes} ms={Ms:F0}",
                                checkpointToken, entry.HashPrefix, mode, bytes, Stopwatch.GetElapsedTime(treeStart).TotalMilliseconds);
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref entry.SnapshotPending, 0);
                    }
                }

                logger?.LogInformation("[RangeIndexCheckpointSnapshotSummary] token={Token} trees={Trees} queued={Queued} bytes={Bytes} ms={Ms:F0}",
                    checkpointToken, trees, queued, totalBytes, Stopwatch.GetElapsedTime(allStart).TotalMilliseconds);
            }
            finally
            {
                ClearCheckpointBarrier();
            }
        }

        /// <summary>
        /// On log truncation, delete per-flush snapshot files in the log root whose address is
        /// strictly less than <paramref name="newBeginAddress"/>.
        /// Per-checkpoint snapshots are NOT touched here — Tsavorite's checkpoint manager
        /// removes them when it deletes the parent token directory.
        ///
        /// <para>Per-flush files are LOG-tied (their lifetime tracks log addresses), not
        /// checkpoint-tied — they are safe to delete once Tsavorite's BeginAddress passes their
        /// address. This cleanup is unconditional and independent of cluster mode or any
        /// checkpoint-retention policy.</para>
        /// </summary>
        internal void OnTruncateImpl(long newBeginAddress)
        {
            if (string.IsNullOrEmpty(riLogRoot))
                return;

            // Published before enumerating, so a queued snapshot below it either sees it or is enumerated here.
            long truncated;
            while ((truncated = Volatile.Read(ref flushFilesTruncatedBelow)) < newBeginAddress
                && Interlocked.CompareExchange(ref flushFilesTruncatedBelow, newBeginAddress, truncated) != truncated)
            {
            }

            if (!Directory.Exists(riLogRoot))
                return;

            try
            {
                var start = Stopwatch.GetTimestamp();
                var deleted = 0;
                long deletedBytes = 0;
                foreach (var (path, _, addr) in EnumerateFlushFiles())
                {
                    if (addr < newBeginAddress && TryDeleteFlushFile(path, out var bytes))
                    {
                        deleted++;
                        deletedBytes += bytes;
                    }
                }

                LogFlushFileDeletion("truncated", newBeginAddress, deleted, deletedBytes, start);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "OnTruncate: enumeration failed under {Root}", riLogRoot);
            }
        }

        /// <summary>
        /// Deletes flush files that no retained checkpoint and no live record can reference: for each tree, every flush
        /// file below <paramref name="oldestCheckpointEndAddress"/> except the newest one.
        ///
        /// <para>A flush file is read only through the latest log record of its tree (RIPROMOTE, copy-to-tail), and
        /// recovering or replicating a checkpoint uses the newest file below that checkpoint's end address. When a newer
        /// file of the same tree exists below the end address of the oldest retained checkpoint, the older file is
        /// therefore unreachable, even though log truncation would keep it until <c>BeginAddress</c> passes it. This
        /// holds because recovery deletes the files of records it discards (<see cref="DeleteDiscardedFlushFile"/>,
        /// <see cref="DeleteFlushFilesAboveRecoveredTail"/>), so no file is newer than the live record of its tree.</para>
        /// </summary>
        /// <param name="oldestCheckpointEndAddress">The hybrid log end address of the oldest retained checkpoint.</param>
        public void DeleteSupersededFlushFiles(long oldestCheckpointEndAddress)
        {
            if (string.IsNullOrEmpty(riLogRoot) || !Directory.Exists(riLogRoot))
                return;

            try
            {
                var start = Stopwatch.GetTimestamp();
                var newest = new Dictionary<string, long>();
                var candidates = new List<(string Path, string Hash, long Address)>();
                foreach (var (path, name, addr) in EnumerateFlushFiles())
                {
                    if (addr >= oldestCheckpointEndAddress)
                        continue;
                    var hash = name[..HashPrefixLength];
                    candidates.Add((path, hash, addr));
                    if (!newest.TryGetValue(hash, out var newestAddr) || addr > newestAddr)
                        newest[hash] = addr;
                }

                var deleted = 0;
                long deletedBytes = 0;
                foreach (var (path, hash, addr) in candidates)
                {
                    if (addr < newest[hash] && TryDeleteFlushFile(path, out var bytes))
                    {
                        deleted++;
                        deletedBytes += bytes;
                    }
                }

                LogFlushFileDeletion("superseded", oldestCheckpointEndAddress, deleted, deletedBytes, start);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DeleteSupersededFlushFiles: enumeration failed under {Root}", riLogRoot);
            }
        }

        /// <summary>Deletes the flush file of a record that recovery invalidated, so it can never be taken for the newest file of its tree.</summary>
        /// <param name="key">The key of the invalidated record.</param>
        /// <param name="logicalAddress">The address of the invalidated record.</param>
        internal void DeleteDiscardedFlushFile(ReadOnlySpan<byte> key, long logicalAddress)
        {
            if (string.IsNullOrEmpty(riLogRoot))
                return;

            var hashPrefix = HashKeyToPrefix(key);
            var path = LogFlushPath(hashPrefix, logicalAddress);
            if (File.Exists(path) && TryDeleteFlushFile(path, out var bytes))
                logger?.LogInformation("[RangeIndexFlushFilesDeleted] reason=recovery-undo hash={Hash} address=0x{Addr:x16} deleted=1 deletedBytes={Bytes}", hashPrefix, logicalAddress, bytes);
        }

        /// <summary>
        /// Deletes every flush file at or above <paramref name="recoveredTailAddress"/>. Their records were written after the
        /// recovered checkpoint and are not part of the recovered log, so the files are unreachable; left in place, a file
        /// newer than the live record of its tree would be taken for that tree's newest file.
        /// </summary>
        /// <param name="recoveredTailAddress">The tail address of the hybrid log right after checkpoint recovery.</param>
        public void DeleteFlushFilesAboveRecoveredTail(long recoveredTailAddress)
        {
            if (string.IsNullOrEmpty(riLogRoot) || !Directory.Exists(riLogRoot))
                return;

            try
            {
                var start = Stopwatch.GetTimestamp();
                var deleted = 0;
                long deletedBytes = 0;
                foreach (var (path, _, addr) in new List<(string, string, long)>(EnumerateFlushFiles()))
                {
                    if (addr >= recoveredTailAddress && TryDeleteFlushFile(path, out var bytes))
                    {
                        deleted++;
                        deletedBytes += bytes;
                    }
                }

                LogFlushFileDeletion("recovery-tail", recoveredTailAddress, deleted, deletedBytes, start);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DeleteFlushFilesAboveRecoveredTail: enumeration failed under {Root}", riLogRoot);
            }
        }

        /// <summary>Deletes every flush file under the log root, for when the hybrid log they belong to is discarded.</summary>
        /// <param name="reason">Why the files are discarded; logged.</param>
        public void DeleteAllFlushFiles(string reason)
        {
            // The discarded log's queued snapshots are skipped; waiting lets those in flight finish before their files are deleted.
            flushSnapshotsStopped = true;
            try
            {
                WaitForPendingFlushSnapshots("delete-all:" + reason);
            }
            finally
            {
                flushSnapshotsStopped = false;
            }

            try
            {
                var start = Stopwatch.GetTimestamp();
                var deleted = 0;
                long deletedBytes = 0;
                foreach (var (path, _, _) in new List<(string, string, long)>(EnumerateFlushFiles()))
                {
                    if (TryDeleteFlushFile(path, out var bytes))
                    {
                        deleted++;
                        deletedBytes += bytes;
                    }
                }

                LogFlushFileDeletion(reason, 0, deleted, deletedBytes, start);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DeleteAllFlushFiles: enumeration failed under {Root}", riLogRoot);
            }
        }

        private bool TryDeleteFlushFile(string path, out long bytes)
        {
            bytes = TryGetFileLength(path);
            try
            {
                File.Delete(path);
                return bytes >= 0;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to delete flush file {Path}", path);
                return false;
            }
        }

        private void LogFlushFileDeletion(string reason, long address, int deleted, long deletedBytes, long start)
        {
            if (logger is null || !logger.IsEnabled(LogLevel.Information))
                return;

            var (retained, retainedBytes) = MeasureFlushFiles();
            logger.LogInformation("[RangeIndexFlushFilesDeleted] reason={Reason} address=0x{Addr:x16} deleted={Deleted} deletedBytes={DeletedBytes} retainedFiles={Retained} retainedBytes={RetainedBytes} ms={Ms:F0}",
                reason, address, deleted, deletedBytes, retained, retainedBytes, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        // ---------------------------------------------------------------
        // Shared flush-file enumeration helper
        // ---------------------------------------------------------------

        /// <summary>
        /// Enumerates all <c>*.flush.bftree</c> files under <c>riLogRoot</c>, parsing and
        /// yielding the embedded logical address from each valid filename.
        /// </summary>
        /// <returns>Tuples of (fullPath, fileName, logicalAddress). Skips files that don't
        /// match the expected naming pattern. Returns empty if riLogRoot is not configured
        /// or does not exist.</returns>
        private IEnumerable<(string Path, string Name, long Address)> EnumerateFlushFiles()
        {
            if (string.IsNullOrEmpty(riLogRoot) || !Directory.Exists(riLogRoot))
            {
                logger?.LogInformation("Root directory invalid {riLogRoot}", riLogRoot);
                yield break;
            }

            foreach (var path in Directory.EnumerateFiles(riLogRoot, $"*{FlushSuffix}"))
            {
                var name = Path.GetFileName(path);

                // Pattern: <hash:32>.<addr:16>.flush.bftree
                if (name.Length != FlushFileNameLength)
                    continue;

                var addrSegment = name.AsSpan(AddrStartIndex, AddrHexLength);
                if (!long.TryParse(addrSegment, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var addr))
                    continue;

                yield return (path, name, addr);
            }
        }

        // ---------------------------------------------------------------
        // Replication: file enumeration for checkpoint transfer
        // ---------------------------------------------------------------

        /// <summary>
        /// Represents a single RangeIndex file that must be shipped during replication.
        /// </summary>
        public readonly struct RangeIndexFileEntry
        {
            /// <summary>Full path to the file on the primary.</summary>
            public readonly string Path;

            /// <summary>The 32-character key hash prefix identifying the RangeIndex tree.</summary>
            public readonly string KeyHash;

            /// <summary>The hlog logical address (for flush files); 0 for checkpoint snapshots.</summary>
            public readonly long Address;

            /// <summary>Whether this is a per-flush file (true) or a per-checkpoint snapshot (false).</summary>
            public readonly bool IsFlushFile;

            public RangeIndexFileEntry(string path, string keyHash, long address, bool isFlushFile)
            {
                Path = path;
                KeyHash = keyHash;
                Address = address;
                IsFlushFile = isFlushFile;
            }
        }

        /// <summary>
        /// Enumerates all RangeIndex files that must be sent during disk-based replication
        /// for a given checkpoint token and hlog address range.
        ///
        /// <para>Two categories of files are collected:</para>
        /// <list type="number">
        /// <item><b>Per-flush files</b> in <c>riLogRoot</c> whose embedded address falls within
        /// <c>[hlogStartAddress, hlogEndAddress)</c>. These correspond to RI stubs in the hlog
        /// segments being transferred.</item>
        /// <item><b>Per-checkpoint snapshot files</b> under
        /// <c>cpr-checkpoints/&lt;token&gt;/rangeindex/</c>. These correspond to RI stubs in
        /// the snapshot region at checkpoint time.</item>
        /// </list>
        ///
        /// <para>Only the highest-address flush file per key hash within the range is included. Older flush files belong to
        /// superseded stub records, which are never dereferenced: <see cref="PreStageAndRegisterPending"/> is only invoked
        /// with the address of the live record (RIPROMOTE copy-update and compaction copy of live records), and recovery deletes
        /// the files of the records it discards, so no file is newer than the live record of its tree.</para>
        /// </summary>
        /// <param name="checkpointToken">The checkpoint token (storeHlogToken) to locate
        /// per-checkpoint snapshots.</param>
        /// <param name="hlogStartAddress">The <c>hybridLogFileStartAddress</c> from
        /// <c>LogFileInfo</c> — inclusive lower bound for flush file filtering.</param>
        /// <param name="hlogEndAddress">The <c>hybridLogFileEndAddress</c> from
        /// <c>LogFileInfo</c> — exclusive upper bound for flush file filtering.</param>
        /// <returns>An enumerable of <see cref="RangeIndexFileEntry"/> describing files to
        /// transfer. Empty if RangeIndex is not configured or no files match.</returns>
        public IEnumerable<RangeIndexFileEntry> EnumerateFilesForReplication(
            Guid checkpointToken, long hlogStartAddress, long hlogEndAddress)
        {
            WaitForPendingFlushSnapshots("replication", addr => addr >= hlogStartAddress && addr < hlogEndAddress);

            // 1. Per-flush files: <hash>.<addr:x16>.flush.bftree where addr ∈ [start, end), latest per hash only
            var latestFlushFiles = new Dictionary<string, (string Path, long Address)>();
            foreach (var (path, name, addr) in EnumerateFlushFiles())
            {
                if (addr >= hlogStartAddress && addr < hlogEndAddress)
                {
                    var keyHash = name[..HashPrefixLength];
                    if (!latestFlushFiles.TryGetValue(keyHash, out var latest) || addr > latest.Address)
                        latestFlushFiles[keyHash] = (path, addr);
                }
            }

            foreach (var (keyHash, latest) in latestFlushFiles)
                yield return new RangeIndexFileEntry(latest.Path, keyHash, latest.Address, isFlushFile: true);

            // 2. Per-checkpoint snapshot files: cpr-checkpoints/<token>/rangeindex/<hash>.bftree
            if (!string.IsNullOrEmpty(cprDir))
            {
                var snapshotDir = CheckpointSnapshotDir(checkpointToken);
                if (Directory.Exists(snapshotDir))
                {
                    foreach (var path in Directory.EnumerateFiles(snapshotDir, "*.bftree"))
                    {
                        var name = Path.GetFileName(path);

                        // Skip files that don't match the expected format: <32-hex-hash>.bftree
                        if (name.Length != HashPrefixLength + ".bftree".Length)
                            continue;

                        var keyHash = name[..HashPrefixLength];
                        yield return new RangeIndexFileEntry(path, keyHash, address: 0, isFlushFile: false);
                    }
                }
            }
        }
    }
}