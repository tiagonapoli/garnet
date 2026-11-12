// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Garnet.test.cluster
{
    [TestFixture]
    [NonParallelizable]
    public class ClusterReplicationAofCheckpointReplayTests : TestBase
    {
        ClusterTestContext context;

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test]
        [Category("REPLICATION")]
        public async Task AofLimitCheckpointBlocksReplicaReplayUntilSnapshotCompletes()
        {
            const int primaryIndex = 0, replicaIndex = 1;
            const int valueSize = 64 * 1024, writeBatch = 256;
            const string aofSizeLimit = "1g";
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: 180);
            context.CreateInstances(2, disableObjects: true, lowMemory: true, memorySize: "8g", pageSize: "16m", enableAOF: true, CommitFrequencyMs: 1000, FastAofTruncate: false, useAofNullDevice: false, OnDemandCheckpoint: true, enableDisklessSync: true, DisableStorageTier: true, compactionType: LogCompactionType.None, AofMemorySize: "128m", AofPageSize: "32m", aofSizeLimit: aofSizeLimit, checkpointThrottleFlushDelayMs: 0);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);
            var db = context.clusterTestUtils.GetDatabase();
            db.StringSet("{mg}hot", new string('v', valueSize));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            var replica = context.GetStoreWrapper(replicaIndex);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            replica.DefaultDatabase.StateMachineDriver.UnsafeRegisterCallback(new PauseCheckpointOnce(entered, release, context.cts.Token));
            var replicaEndpoint = context.clusterTestUtils.GetEndPoint(replicaIndex);
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(replicaEndpoint, "READONLY", []));

            try
            {
                var writes = (int)(ServerOptions.ParseSize(aofSizeLimit, out _) / valueSize) + writeBatch;
                for (var i = 0; i < writes; i += writeBatch)
                {
                    context.cts.Token.ThrowIfCancellationRequested();
                    Task.WaitAll([.. Enumerable.Range(i, writeBatch).Select(n => db.StringSetAsync("{mg}hot", n.ToString().PadRight(valueSize, 'h')))]);
                }

                entered.Wait(context.cts.Token);
                var blockedOffset = context.clusterTestUtils.GetReplicationOffset(replicaIndex);
                db.StringSet("{mg}after-checkpoint", "committed");
                ClassicAssert.AreEqual("AOF file committed", context.clusterTestUtils.GetServer(primaryIndex).Execute("COMMITAOF").ToString());
                var primaryOffset = context.clusterTestUtils.GetReplicationOffset(primaryIndex);

                ClassicAssert.IsTrue(primaryOffset.AnyGreater(blockedOffset), "The primary must advance while the replica checkpoint is held");
                ClassicAssert.AreEqual(blockedOffset, context.clusterTestUtils.GetReplicationOffset(replicaIndex), "Replica replay must remain at the checkpoint");
                ClassicAssert.IsNull((string)context.clusterTestUtils.Execute(replicaEndpoint, "GET", ["{mg}after-checkpoint"]), "The write after the checkpoint marker must not be applied yet");
                TestContext.Progress.WriteLine($"Replica snapshot held: replay offset {blockedOffset}, primary offset {primaryOffset}; committed sentinel is absent on replica");
            }
            finally
            {
                release.Set();
                await replica.DefaultDatabase.StateMachineDriver.CompleteAsync(context.cts.Token);
            }

            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ClassicAssert.AreEqual("committed", (string)context.clusterTestUtils.Execute(replicaEndpoint, "GET", ["{mg}after-checkpoint"]));
            TestContext.Progress.WriteLine("Replica snapshot released: replay caught up and the committed sentinel is present");
        }

        // Hold snapshot completion, not the replay thread: replay is free to progress if it does not await the checkpoint.
        sealed class PauseCheckpointOnce(ManualResetEventSlim entered, ManualResetEventSlim release, CancellationToken token) : IStateMachineCallback
        {
            int paused;

            public void BeforeEnteringState(SystemState next)
            {
                if (next.Phase != Phase.WAIT_FLUSH || Interlocked.Exchange(ref paused, 1) != 0)
                    return;

                entered.Set();
                release.Wait(token);
            }
        }
    }
}
