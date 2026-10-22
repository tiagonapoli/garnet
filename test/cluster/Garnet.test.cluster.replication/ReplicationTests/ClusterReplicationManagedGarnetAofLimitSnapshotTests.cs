// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;
using Tsavorite.core;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Snapshot written when repeated writes to a single key exceed <c>AofSizeLimit</c> under Managed Garnet's persistent, untiered configuration.
    /// </summary>
    /// <remarks>
    /// Managed Garnet renders this configuration for <c>PersistenceMode=AofAndRdb</c> nodes without a storage tier: AofSizeLimit=1g,
    /// CommitFrequencyMs=1000, FastAofTruncate=false, UseAofNullDevice=false, OnDemandCheckpoint=true, ReplicaDisklessSync=true,
    /// EnableStorageTier=false, CompactionType=None, AofMemorySize=128m, Garnet default page sizes and a SKU-derived LogMemorySize (8g on a 12 GB SKU).
    /// Without a storage tier the snapshot starts at HeadAddress, so every AOF-limit checkpoint rewrites the whole in-memory log.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class ClusterReplicationManagedGarnetAofLimitSnapshotTests : TestBase
    {
        ClusterTestContext context;

        const int PrimaryIndex = 0;
        const int ReplicaIndex = 1;
        const int ValueSize = 64 * 1024;
        const int WriteBatch = 256;
        const long MB = 1L << 20;
        const string AofSizeLimit = "1g";

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test]
        [Category("REPLICATION")]
        public async Task SingleKeyWritesPastAofSizeLimitRewriteWholeStore()
        {
            const int storeMB = 4096;
            SetupCluster();
            var primary = context.GetStoreWrapper(PrimaryIndex);
            var replica = context.GetStoreWrapper(ReplicaIndex);
            var primaryCheckpoints = new CheckpointObserver(primary, context);
            var replicaCheckpoints = new CheckpointObserver(replica, context);
            primary.DefaultDatabase.StateMachineDriver.UnsafeRegisterCallback(primaryCheckpoints);
            replica.DefaultDatabase.StateMachineDriver.UnsafeRegisterCallback(replicaCheckpoints);
            var db = context.clusterTestUtils.GetDatabase();
            var keyCount = (int)(storeMB * MB / ValueSize);
            var value = new string('v', ValueSize);
            db.StringSet("{mg}hot", value);
            for (var i = 0; i < keyCount; i += WriteBatch)
            {
                context.cts.Token.ThrowIfCancellationRequested();
                Task.WaitAll([.. Enumerable.Range(i, Math.Min(WriteBatch, keyCount - i)).Select(k => db.StringSetAsync($"{{mg}}key{k}", value))]);
            }

            // Let AOF-triggered checkpoints drain population; no SAVE/BGSAVE resets the AOF.
            while (primary.AofSize() > ServerOptions.ParseSize(AofSizeLimit, out _) || primary.store.SystemState.Phase != Phase.REST)
                ClusterTestUtils.BackOff(cancellationToken: context.cts.Token);
            await primary.DefaultDatabase.StateMachineDriver.CompleteAsync(context.cts.Token);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);
            var measurementStart = primary.appendOnlyFile.Log.TailAddress[0];

            var aofLimitBytes = ServerOptions.ParseSize(AofSizeLimit, out _);
            var writes = (int)(aofLimitBytes / ValueSize) + WriteBatch;
            WriteHotKey(db, writes);
            var aofBytes = (long)writes * ValueSize;

            var sw = Stopwatch.StartNew();
            var primaryCheckpoint = primaryCheckpoints.WaitFor(c => c.CoveredAofAddress > measurementStart && c.AofBytesAtStart > aofLimitBytes);
            var primaryCheckpointSeconds = sw.Elapsed.TotalSeconds;
            var replicaCheckpoint = replicaCheckpoints.WaitFor(c => c.Version == primaryCheckpoint.Version);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);

            // The primary captures its safe tail before emitting the start marker; concurrent writes can separate them.
            ClassicAssert.GreaterOrEqual(replicaCheckpoint.CoveredAofAddress, primaryCheckpoint.CoveredAofAddress, "The replica must cover at least the primary checkpoint's safe AOF address");
            ClassicAssert.AreNotEqual(primaryCheckpoint.Token, replicaCheckpoint.Token, "The replica generates its own snapshot");
            var primarySnapshotBytes = primaryCheckpoint.SnapshotBytes;
            var replicaSnapshotBytes = replicaCheckpoint.SnapshotBytes;
            TestContext.Progress.WriteLine($"AOF-limit checkpoint version {primaryCheckpoint.Version}: primary token {primaryCheckpoint.Token}, replica token {replicaCheckpoint.Token}, covered AOF address {primaryCheckpoint.CoveredAofAddress}, AOF at trigger {primaryCheckpoint.AofBytesAtStart / MB} MB");
            ClassicAssert.LessOrEqual(primarySnapshotBytes, 2 * aofBytes, $"overwriting 1 key past AofSizeLimit={AofSizeLimit} ({aofBytes / MB} MB of new value payload; {primaryCheckpoint.AofBytesAtStart / MB} MB of outstanding AOF at checkpoint start) over a {storeMB} MB store wrote a {primarySnapshotBytes / MB} MB snapshot on the primary ({(double)primarySnapshotBytes / aofBytes:F1}x the new payload, observed within {primaryCheckpointSeconds:F0}s of the last write) and a {replicaSnapshotBytes / MB} MB snapshot on the replica");
        }

        void SetupCluster()
        {
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: 3600);
            context.CreateInstances(2, disableObjects: true, lowMemory: true, memorySize: "8g", pageSize: "16m", enableAOF: true, CommitFrequencyMs: 1000, FastAofTruncate: false, useAofNullDevice: false, OnDemandCheckpoint: true, enableDisklessSync: true, DisableStorageTier: true, compactionType: LogCompactionType.None, AofMemorySize: "128m", AofPageSize: "32m", aofSizeLimit: AofSizeLimit, checkpointThrottleFlushDelayMs: 0);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);
        }

        void WriteHotKey(IDatabase db, int writes)
        {
            for (var i = 0; i < writes; i += WriteBatch)
            {
                context.cts.Token.ThrowIfCancellationRequested();
                Task.WaitAll([.. Enumerable.Range(i, Math.Min(WriteBatch, writes - i)).Select(n => db.StringSetAsync("{mg}hot", n.ToString().PadRight(ValueSize, 'h')))]);
            }
        }

        sealed record Snapshot(Guid Token, long Version, long CoveredAofAddress, long AofBytesAtStart, long SnapshotBytes);

        sealed class CheckpointObserver(StoreWrapper store, ClusterTestContext context) : IStateMachineCallback
        {
            readonly ConcurrentQueue<Snapshot> completed = new();
            long aofBytesAtStart;
            long coveredAofAddress;

            public void BeforeEnteringState(SystemState next)
            {
                if (next.Phase == Phase.PREPARE)
                {
                    aofBytesAtStart = store.AofSize();
                    coveredAofAddress = store.StoreCheckpointManager.CurrentSafeAofAddress[0];
                }
                else if (next.Phase == Phase.REST)
                {
                    store.store.GetLatestCheckpointTokens(out var token, out _, out var version);
                    ClassicAssert.AreEqual(next.Version, version, "Persisted metadata must identify this completed checkpoint");
                    var snapshots = Directory.EnumerateDirectories(store.serverOptions.CheckpointDir, token.ToString(), SearchOption.AllDirectories).Where(d => Directory.GetParent(d).Name == "cpr-checkpoints").Single();
                    var bytes = Directory.EnumerateFiles(snapshots, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                    completed.Enqueue(new Snapshot(token, version, coveredAofAddress, aofBytesAtStart, bytes));
                }
            }

            public Snapshot WaitFor(Func<Snapshot, bool> predicate)
            {
                while (true)
                {
                    var checkpoint = completed.FirstOrDefault(predicate);
                    if (checkpoint != null)
                        return checkpoint;

                    ClusterTestUtils.BackOff(cancellationToken: context.cts.Token);
                }
            }
        }
    }
}
