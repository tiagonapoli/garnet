// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Bytes written by the checkpoint that the AOF size limit triggers, relative to the AOF that triggered it.
    /// </summary>
    /// <remarks>
    /// Exceeding <c>AofSizeLimit</c> starts a snapshot checkpoint of the whole unflushed log, regardless of how little of
    /// it changed since the previous checkpoint. A small AOF over a large in-memory log therefore writes a snapshot many
    /// times the AOF size, and every replica repeats it while its AOF replay is blocked.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class ClusterReplicationAofSizeLimitCheckpointTests : TestBase
    {
        ClusterTestContext context;

        const int PrimaryIndex = 0;
        const int ReplicaIndex = 1;
        const int ValueSize = 4096;
        const int KeyCount = 4096;
        const int UpdateCount = 640;
        const string AofSizeLimit = "2m";

        [SetUp]
        public void Setup()
        {
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: 180);
        }

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test]
        [Category("REPLICATION")]
        public void AofSizeLimitCheckpointWritesSnapshotProportionalToAof()
        {
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, pageSize: "32k", memorySize: "64m", aofSizeLimit: AofSizeLimit);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // 16 MB of in-memory records, then a checkpoint so the AOF starts empty.
            var db = context.clusterTestUtils.GetDatabase();
            var value = new string('v', ValueSize);
            Task.WaitAll([.. Enumerable.Range(0, KeyCount).Select(i => db.StringSetAsync($"{{amp}}key{i}", value))]);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);
            context.clusterTestUtils.GetServer(PrimaryIndex).Save(StackExchange.Redis.SaveType.ForegroundSave);

            var lastSave = context.clusterTestUtils.LastSave(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(PrimaryIndex, lastSave);

            // Just over AofSizeLimit of in-place updates: the in-memory log does not grow, only the AOF does.
            var updated = new string('u', ValueSize);
            Task.WaitAll([.. Enumerable.Range(0, UpdateCount).Select(i => db.StringSetAsync($"{{amp}}key{i}", updated))]);
            var aofBytes = (long)UpdateCount * ValueSize;

            context.clusterTestUtils.WaitCheckpoint(PrimaryIndex, lastSave, logger: context.logger);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);

            var primarySnapshotBytes = NewestSnapshotBytes(PrimaryIndex);
            var replicaSnapshotBytes = NewestSnapshotBytes(ReplicaIndex);

            ClassicAssert.LessOrEqual(primarySnapshotBytes, 2 * aofBytes, $"exceeding AofSizeLimit={AofSizeLimit} with {aofBytes} AOF bytes wrote a {primarySnapshotBytes}-byte snapshot on the primary ({(double)primarySnapshotBytes / aofBytes:F1}x) and a {replicaSnapshotBytes}-byte snapshot on the replica");
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
