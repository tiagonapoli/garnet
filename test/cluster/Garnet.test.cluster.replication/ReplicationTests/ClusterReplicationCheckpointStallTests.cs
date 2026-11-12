// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Replication lag while a replica takes the checkpoint that a primary checkpoint asks it to take.
    /// </summary>
    /// <remarks>
    /// A replica takes its own checkpoint when replay reaches the primary's CheckpointEndCommit, and it does so on the
    /// replay thread (<c>AofProcessor</c>, "Must block here"). No AOF record is replayed until the replica's checkpoint
    /// has been flushed, so every write the primary acknowledges meanwhile becomes visible on the replica only after the
    /// whole flush. A checkpoint of several GB on a slow disk stalls replication for minutes.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class ClusterReplicationCheckpointStallTests : TestBase
    {
        ClusterTestContext context;

        const int PrimaryIndex = 0;
        const int ReplicaIndex = 1;

        // Each flushed checkpoint page sleeps this long, standing in for a slow disk: ~256 pages take over 10 s.
        const int CheckpointThrottleFlushDelayMs = 50;

        // Generous for a loaded CI machine, yet far below the stall the throttled checkpoint produces.
        static readonly TimeSpan MaxAcceptableReplicationLag = TimeSpan.FromSeconds(1);

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
        public void ReplicaCheckpointDoesNotStallReplication()
        {
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, pageSize: "32k", memorySize: "64m", checkpointThrottleFlushDelayMs: CheckpointThrottleFlushDelayMs);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // 8 MB of in-memory records, so a snapshot checkpoint flushes hundreds of throttled pages.
            var db = context.clusterTestUtils.GetDatabase();
            var largeValue = new string('v', 4096);
            var writes = new Task[2048];
            for (var i = 0; i < writes.Length; i++)
                writes[i] = db.StringSetAsync($"{{stall}}key{i}", largeValue);
            Task.WaitAll(writes);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);

            var replicaLastSave = context.clusterTestUtils.LastSave(ReplicaIndex, logger: context.logger);
            var primaryLastSave = context.clusterTestUtils.LastSave(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(PrimaryIndex, primaryLastSave);
            context.clusterTestUtils.WaitUntilNextSecond(ReplicaIndex, replicaLastSave);

            var checkpointStopwatch = Stopwatch.StartNew();
            context.clusterTestUtils.GetServer(PrimaryIndex).Save(SaveType.BackgroundSave);

            // Write probes until the replica has finished its checkpoint, timing each probe from the primary's
            // acknowledgement to its first read on the replica.
            var maxLag = TimeSpan.Zero;
            var maxLagProbe = -1;
            var probes = 0;
            var replicaCheckpointDone = false;
            while (!replicaCheckpointDone)
            {
                ClassicAssert.Less(checkpointStopwatch.Elapsed, TimeSpan.FromSeconds(120), "replica never completed its checkpoint");
                replicaCheckpointDone = context.clusterTestUtils.LastSave(ReplicaIndex, logger: context.logger) > replicaLastSave;

                var key = Encoding.ASCII.GetBytes($"{{stall}}probe{probes}");
                var value = Encoding.ASCII.GetBytes(probes.ToString());
                ClassicAssert.AreEqual(ResponseState.OK, context.clusterTestUtils.SetKey(PrimaryIndex, key, value, out _, out _, logger: context.logger));

                var lag = Stopwatch.StartNew();
                while (context.clusterTestUtils.GetKey(ReplicaIndex, key, out _, out _, out _, logger: context.logger) != probes.ToString())
                {
                    ClassicAssert.Less(lag.Elapsed, TimeSpan.FromSeconds(120), $"probe {probes} never reached the replica");
                    Thread.Sleep(1);
                }

                if (lag.Elapsed > maxLag)
                    (maxLag, maxLagProbe) = (lag.Elapsed, probes);
                probes++;
            }

            context.clusterTestUtils.WaitCheckpoint(PrimaryIndex, primaryLastSave, logger: context.logger);

            ClassicAssert.Less(maxLag, MaxAcceptableReplicationLag, $"replication stalled for {maxLag.TotalMilliseconds:F0} ms (probe {maxLagProbe} of {probes}) while the replica took its checkpoint; the replica's checkpoint finished {checkpointStopwatch.Elapsed.TotalMilliseconds:F0} ms after the primary's BGSAVE");
        }
    }
}
