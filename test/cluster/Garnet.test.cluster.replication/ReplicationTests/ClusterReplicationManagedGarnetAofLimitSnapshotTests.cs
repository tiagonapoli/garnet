// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

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

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test]
        [Category("REPLICATION")]
        [TestCase(256, "16m", 180)]
        [TestCase(4096, "1g", 3600, Explicit = true, Reason = "Production scale: writes ~4 GB of records and two ~4 GB snapshots per node")]
        public void SingleKeyWritesPastAofSizeLimitRewriteWholeStore(int storeMB, string aofSizeLimit, int timeoutSeconds)
        {
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: timeoutSeconds);
            context.CreateInstances(2, lowMemory: true, memorySize: "8g", pageSize: "16m", enableAOF: true, CommitFrequencyMs: 1000, FastAofTruncate: false, useAofNullDevice: false, OnDemandCheckpoint: true, enableDisklessSync: true, DisableStorageTier: true, compactionType: LogCompactionType.None, AofMemorySize: "128m", AofPageSize: "32m", aofSizeLimit: aofSizeLimit, checkpointThrottleFlushDelayMs: 0);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            var db = context.clusterTestUtils.GetDatabase();
            var keyCount = (int)(storeMB * MB / ValueSize);
            var value = new string('v', ValueSize);
            for (var i = 0; i < keyCount; i += WriteBatch)
                Task.WaitAll([.. Enumerable.Range(i, Math.Min(WriteBatch, keyCount - i)).Select(k => db.StringSetAsync($"{{mg}}key{k}", value))]);

            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);
            var fillSave = context.clusterTestUtils.LastSave(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(PrimaryIndex, fillSave);
            context.clusterTestUtils.GetServer(PrimaryIndex).Save(SaveType.BackgroundSave);
            WaitLastSaveAfter(fillSave);
            var lastSave = context.clusterTestUtils.LastSave(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(PrimaryIndex, lastSave);

            // Overwrite one key in place until the AOF passes AofSizeLimit; the in-memory log does not grow.
            var aofLimitBytes = ServerOptions.ParseSize(aofSizeLimit, out _);
            var writes = (int)(aofLimitBytes / ValueSize) + WriteBatch;
            for (var i = 0; i < writes; i += WriteBatch)
                Task.WaitAll([.. Enumerable.Range(i, WriteBatch).Select(n => db.StringSetAsync("{mg}hot", n.ToString().PadRight(ValueSize, 'h')))]);
            var aofBytes = (long)writes * ValueSize;

            var sw = Stopwatch.StartNew();
            WaitLastSaveAfter(lastSave);
            var primaryCheckpointSeconds = sw.Elapsed.TotalSeconds;
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);

            var primarySnapshotBytes = NewestSnapshotBytes(PrimaryIndex);
            var replicaSnapshotBytes = NewestSnapshotBytes(ReplicaIndex);
            ClassicAssert.LessOrEqual(primarySnapshotBytes, 2 * aofBytes, $"overwriting 1 key past AofSizeLimit={aofSizeLimit} ({aofBytes / MB} MB of AOF) over a {storeMB} MB store wrote a {primarySnapshotBytes / MB} MB snapshot on the primary ({(double)primarySnapshotBytes / aofBytes:F1}x the AOF, observed within {primaryCheckpointSeconds:F0}s of the last write) and a {replicaSnapshotBytes / MB} MB snapshot on the replica");
        }

        // ClusterTestUtils.WaitCheckpoint gives up after 30s, shorter than a multi-GB snapshot.
        void WaitLastSaveAfter(DateTime previous)
        {
            var server = context.clusterTestUtils.GetServer(PrimaryIndex);
            while (server.LastSave() <= previous)
            {
                context.cts.Token.ThrowIfCancellationRequested();
                Thread.Sleep(500);
            }
        }

        long NewestSnapshotBytes(int nodeIndex)
        {
            var port = context.clusterTestUtils.GetEndPoint(nodeIndex).Port.ToString();
            var nodeDir = Directory.EnumerateDirectories(context.TestFolder, port, SearchOption.AllDirectories).First();
            var snapshots = Directory.EnumerateDirectories(nodeDir, "cpr-checkpoints", SearchOption.AllDirectories).Single();
            var newest = new DirectoryInfo(snapshots).GetDirectories().OrderBy(d => d.CreationTimeUtc).Last();
            return newest.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
    }
}
