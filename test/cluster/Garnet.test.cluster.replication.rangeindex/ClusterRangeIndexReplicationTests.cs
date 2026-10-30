// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Garnet.common;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    [TestFixture]
    [NonParallelizable]
    public class ClusterRangeIndexReplicationTests : TestBase
    {
        ClusterTestContext context;

        readonly int timeout = 60;
        readonly int testTimeout = (int)TimeSpan.FromSeconds(120).TotalSeconds;

        public Dictionary<string, LogLevel> monitorTests = new()
        {
            { "ClusterRangeIndexCheckpointSync", LogLevel.Trace },
            { "ClusterRangeIndexReplicationFailover", LogLevel.Trace },
        };

        [SetUp]
        public virtual void Setup()
        {
            context = new ClusterTestContext();
            context.Setup(monitorTests, testTimeoutSeconds: testTimeout);
        }

        [TearDown]
        public virtual void TearDown()
        {
            context?.TearDown();
        }

        void PopulateRangeIndex(int nodeIndex, string indexName, int fieldCount)
        {
            var endpoint = (IPEndPoint)context.endpoints[nodeIndex];
            var result = context.clusterTestUtils.Execute(endpoint, "RI.CREATE",
                [indexName, "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8"],
                logger: context.logger);
            ClassicAssert.AreEqual("OK", (string)result);

            for (var i = 0; i < fieldCount; i++)
            {
                result = context.clusterTestUtils.Execute(endpoint, "RI.SET",
                    [indexName, $"field{i:D4}", $"value{i:D4}"],
                    logger: context.logger);
                ClassicAssert.AreEqual("OK", (string)result);
            }
        }

        void ValidateRangeIndex(int nodeIndex, string indexName, int fieldCount)
        {
            var endpoint = (IPEndPoint)context.endpoints[nodeIndex];
            for (var i = 0; i < fieldCount; i++)
            {
                var result = context.clusterTestUtils.Execute(endpoint, "RI.GET",
                    [indexName, $"field{i:D4}"],
                    logger: context.logger);
                ClassicAssert.AreEqual($"value{i:D4}", (string)result,
                    $"Mismatch at field{i:D4} on node {nodeIndex}");
            }
        }

        string GetRiLogRoot(int nodeIndex)
        {
            var port = ((IPEndPoint)context.endpoints[nodeIndex]).Port;
            return Path.Combine(context.TestFolder, port.ToString(), "Store", "rangeindex");
        }

        /// <summary>
        /// Create RI keys on primary and allow them to flow to the replica through AOF.
        /// Verify RI.GET returns correct values on replica.
        /// </summary>
        [Test, Order(1)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexCheckpointSync()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true,
                enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // Populate range index on primary
            PopulateRangeIndex(primaryIndex, "idx1", 10);

            // Wait for replica to sync via AOF
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Validate on replica
            ValidateRangeIndex(replicaIndex, "idx1", 10);
        }

        /// <summary>
        /// Multiple RI keys with MEMORY backend, checkpoint, replicate.
        /// Verify all keys and their fields are accessible on replica.
        /// </summary>
        [Test, Order(2)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexCheckpointSyncMultipleKeys()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true,
                enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // Populate multiple range indexes on primary
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            PopulateRangeIndex(primaryIndex, "idx2", 20);
            PopulateRangeIndex(primaryIndex, "idx3", 5);

            // Take checkpoint
            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            // Wait for replica to sync
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Validate all indexes on replica
            ValidateRangeIndex(replicaIndex, "idx1", 10);
            ValidateRangeIndex(replicaIndex, "idx2", 20);
            ValidateRangeIndex(replicaIndex, "idx3", 5);
        }

        /// <summary>
        /// Low memory forces RI key eviction (flush.bftree files created).
        /// Take checkpoint, attach replica. Verify flush snapshot files are transferred
        /// and RI data is restored on replica.
        /// </summary>
        [Test, Order(3)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexCheckpointSyncWithEviction()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true,
                lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // Populate range indexes
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            PopulateRangeIndex(primaryIndex, "idx2", 10);

            // Capture TailAddress after RI population — flushing up to this address guarantees
            // the RI records are on disk.
            var tailAfterRiInsert = long.Parse(
                context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger));

            // Insert fillers and poll until FlushedUntilAddress reaches the captured tail
            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, tailAfterRiInsert);

            // Verify flush.bftree files were created on the primary (confirms flush path was triggered).
            var primaryRiLogRoot = GetRiLogRoot(primaryIndex);
            var primaryFlushFiles = Directory.GetFiles(primaryRiLogRoot, "*.flush.bftree");
            ClassicAssert.IsTrue(primaryFlushFiles.Length > 0,
                "flush.bftree files should exist on primary after deterministic flush");

            // Take checkpoint
            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            // Wait for replica to sync
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Validate on replica — even evicted keys should be accessible
            ValidateRangeIndex(replicaIndex, "idx1", 10);
            ValidateRangeIndex(replicaIndex, "idx2", 10);

            // Verify flush.bftree files were transferred to the replica
            var replicaRiLogRoot = GetRiLogRoot(replicaIndex);
            var replicaFlushFiles = Directory.GetFiles(replicaRiLogRoot, "*.flush.bftree");
            ClassicAssert.IsTrue(replicaFlushFiles.Length > 0,
                "flush.bftree files should exist on replica after replication sync");
            ClassicAssert.AreEqual(primaryFlushFiles.Length, replicaFlushFiles.Length,
                "replica should have the same number of flush.bftree files as the primary");
        }

        /// <summary>
        /// Populate RI → checkpoint → more RI.SET → attach replica.
        /// Verify both checkpointed data and AOF-replayed data arrive.
        /// </summary>
        [Test, Order(4)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexAddReplicaAfterCheckpoint()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, tryRecover: true, disableObjects: true,
                enableAOF: true, enableRangeIndexPreview: true);
            context.CreateConnection();

            // Setup primary only — do not attach replica yet
            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(0,
                new List<(int, int)> { (0, 16383) }, true, context.logger));
            context.clusterTestUtils.BumpEpoch(0, logger: context.logger);

            // Populate and checkpoint
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            // Add more data after checkpoint (will be in AOF)
            var endpoint = (IPEndPoint)context.endpoints[primaryIndex];
            for (var i = 10; i < 20; i++)
            {
                context.clusterTestUtils.Execute(endpoint, "RI.SET",
                    ["idx1", $"field{i:D4}", $"value{i:D4}"],
                    logger: context.logger);
            }

            // Now attach replica
            var primaryId = context.clusterTestUtils.GetNodeIdFromNode(0, context.logger);
            context.clusterTestUtils.Meet(1, 0, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);
            _ = context.clusterTestUtils.ClusterReplicate(1, primaryId, async: false, logger: context.logger);
            context.clusterTestUtils.BumpEpoch(1, logger: context.logger);

            // Wait for sync
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Validate all 20 fields — 10 from checkpoint, 10 from AOF
            ValidateRangeIndex(replicaIndex, "idx1", 20);
        }

        /// <summary>
        /// Checkpoint sync → shutdown replica → more RI.SET on primary → restart replica.
        /// Verify replica catches up.
        /// </summary>
        [Test, Order(5)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexReplicationRestartReplica()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true,
                enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // Populate and sync
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ValidateRangeIndex(replicaIndex, "idx1", 10);

            // Take checkpoint so replica can recover
            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            var replicaLastSaveTime = context.clusterTestUtils.LastSave(replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(replicaIndex, replicaLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(replicaIndex, replicaLastSaveTime, logger: context.logger);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Shutdown replica
            context.nodes[replicaIndex].Dispose(false);
            context.clusterTestUtils.WaitForAofSyncDriverDipose(primaryIndex);

            // Add more data while replica is down
            var endpoint = (IPEndPoint)context.endpoints[primaryIndex];
            for (var i = 10; i < 20; i++)
            {
                context.clusterTestUtils.Execute(endpoint, "RI.SET",
                    ["idx1", $"field{i:D4}", $"value{i:D4}"],
                    logger: context.logger);
            }

            // Restart replica
            context.nodes[replicaIndex] = context.CreateInstance(
                context.clusterTestUtils.GetEndPoint(replicaIndex),
                disableObjects: true,
                tryRecover: true,
                enableAOF: true,
                timeout: timeout,
                cleanClusterConfig: false,
                enableRangeIndexPreview: true);
            context.nodes[replicaIndex].Start();
            context.CreateConnection();

            // Wait for sync
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Validate all 20 fields
            ValidateRangeIndex(replicaIndex, "idx1", 20);
        }

        /// <summary>
        /// Populate RI on primary → replicate via checkpoint → failover replica to primary.
        /// Verify RI data accessible on new primary.
        /// </summary>
        [Test, Order(6)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexReplicationFailover()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true,
                enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            // Populate and sync
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ValidateRangeIndex(replicaIndex, "idx1", 10);

            // Take checkpoint
            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            var replicaLastSaveTime = context.clusterTestUtils.LastSave(replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(replicaIndex, replicaLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(replicaIndex, replicaLastSaveTime, logger: context.logger);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            // Failover
            _ = context.clusterTestUtils.ClusterFailover(replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitForNoFailover(replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitForFailoverCompleted(replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitForReplicaRecovery(primaryIndex, logger: context.logger);

            // Validate RI data on new primary (was replicaIndex)
            ValidateRangeIndex(replicaIndex, "idx1", 10);

            // Verify we can write to new primary
            var newPrimaryEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            var result = context.clusterTestUtils.Execute(newPrimaryEndpoint, "RI.SET",
                ["idx1", "field0010", "value0010"],
                logger: context.logger);
            ClassicAssert.AreEqual("OK", (string)result);
        }

        /// <summary>
        /// A full sync interrupted while RangeIndex files are in flight must not leave the replica with a checkpoint
        /// that a later attach reuses, since the primary then skips resending the RangeIndex files it never received.
        /// </summary>
        [Test, Order(7)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexResyncAfterInterruptedRangeIndexTransfer()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            PopulateRangeIndex(primaryIndex, "idx1", 10);
            PopulateRangeIndex(primaryIndex, "idx2", 10);

            var tailAfterRiInsert = long.Parse(context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger));
            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, tailAfterRiInsert);
            ClassicAssert.IsNotEmpty(Directory.GetFiles(GetRiLogRoot(primaryIndex), "*.flush.bftree"), "the checkpoint must carry RangeIndex files for this scenario to be under test");

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            try
            {
                ExceptionInjectionHelper.EnableException(ExceptionInjectionType.Replication_Fail_Replica_RangeIndex_File_Receive);
                var resp = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger);
                Assert.That(resp, Does.Contain(nameof(ExceptionInjectionType.Replication_Fail_Replica_RangeIndex_File_Receive)), "the first sync must fail during the RangeIndex file transfer");
            }
            finally
            {
                ExceptionInjectionHelper.DisableException(ExceptionInjectionType.Replication_Fail_Replica_RangeIndex_File_Receive);
            }

            var retry = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger);
            ClassicAssert.AreEqual("OK", retry, "the retry must resend the checkpoint instead of reusing the partially received one");
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            ValidateRangeIndex(replicaIndex, "idx1", 10);
            ValidateRangeIndex(replicaIndex, "idx2", 10);
        }

        /// <summary>
        /// Replica recovery after a full sync can outlast the per-message sync timeout, so the primary must keep
        /// waiting for it instead of failing the attach and forcing an endless cycle of full resyncs.
        /// </summary>
        [Test, Order(8)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexFullSyncRecoveryLongerThanSyncTimeout()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;
            var replicaSyncTimeoutSeconds = 1;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true, enableRangeIndexPreview: true, replicaSyncTimeout: replicaSyncTimeoutSeconds);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            PopulateRangeIndex(primaryIndex, "idx1", 10);

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            string resp;
            try
            {
                ExceptionInjectionHelper.EnableException(ExceptionInjectionType.Replication_Wait_Replica_Before_Checkpoint_Recovery);
                var replicate = Task.Run(() => context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));

                // The replica clears the condition when it parks before recovery; hold it there well past the sync timeout.
                await ExceptionInjectionHelper.WaitOnClearAsync(ExceptionInjectionType.Replication_Wait_Replica_Before_Checkpoint_Recovery).WaitAsync(TimeSpan.FromSeconds(timeout));
                await Task.Delay(TimeSpan.FromSeconds(3 * replicaSyncTimeoutSeconds));

                ExceptionInjectionHelper.EnableException(ExceptionInjectionType.Replication_Wait_Replica_Before_Checkpoint_Recovery);
                resp = await replicate.WaitAsync(TimeSpan.FromSeconds(timeout));
            }
            finally
            {
                ExceptionInjectionHelper.EnableException(ExceptionInjectionType.Replication_Wait_Replica_Before_Checkpoint_Recovery);
                ExceptionInjectionHelper.DisableException(ExceptionInjectionType.Replication_Wait_Replica_Before_Checkpoint_Recovery);
            }

            ClassicAssert.AreEqual("OK", resp, "the attach must survive a replica recovery that outlasts the sync timeout");
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ValidateRangeIndex(replicaIndex, "idx1", 10);
        }

        /// <summary>
        /// A key flushed, promoted by a write and flushed again leaves a superseded flush file on the primary. A full
        /// sync must ship only the latest flush file per key, since superseded stubs are never dereferenced.
        /// </summary>
        [Test, Order(9)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexFullSyncSendsOnlyLatestFlushFilePerKey()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            PopulateRangeIndex(primaryIndex, "idx1", 10);
            PopulateRangeIndex(primaryIndex, "idx2", 10);
            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, long.Parse(context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger)));

            // Writing to the flushed keys copies their stubs to the tail; flushing again writes a second flush file per key.
            foreach (var index in new[] { "idx1", "idx2" })
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", [index, "field0010", "value0010"], logger: context.logger));
            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, long.Parse(context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger)));

            static Dictionary<string, string[]> FlushFilesByKey(string root) => Directory.GetFiles(root, "*.flush.bftree").Select(Path.GetFileName).GroupBy(name => name.Split('.')[0]).ToDictionary(g => g.Key, g => g.Order().ToArray());

            var primaryFiles = FlushFilesByKey(GetRiLogRoot(primaryIndex));
            Assert.That(primaryFiles.Values, Has.Some.Matches<string[]>(files => files.Length > 1), "the primary must hold superseded flush files for this scenario to be under test");

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            var replicaFiles = FlushFilesByKey(GetRiLogRoot(replicaIndex));
            foreach (var (keyHash, files) in primaryFiles)
            {
                // Zero-padded hex addresses sort lexicographically, so the last name is the latest flush.
                Assert.That(replicaFiles.TryGetValue(keyHash, out var received) ? received : [], Is.EqualTo(new[] { files[^1] }), $"only the latest flush file of {keyHash} must be shipped");
            }

            foreach (var index in new[] { "idx1", "idx2" })
            {
                ValidateRangeIndex(replicaIndex, index, 10);
                ClassicAssert.AreEqual("value0010", (string)context.clusterTestUtils.Execute((IPEndPoint)context.endpoints[replicaIndex], "RI.GET", [index, "field0010"], logger: context.logger));
            }
        }
    }
}