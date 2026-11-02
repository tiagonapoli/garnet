// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
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

        /// <summary>Waits for the flush snapshots a node queued for completed page flushes; flush files are written in the background.</summary>
        void WaitForFlushSnapshots(int nodeIndex) => context.nodes[nodeIndex].Provider.StoreWrapper.rangeIndexManager.WaitForPendingFlushSnapshots("test");

        /// <summary>
        /// A replica checkpoint driven by the primary's checkpoint marker persists in the background: while its tree snapshot
        /// is pending, the replica keeps replaying new primary writes, and the checkpoint completes once the snapshot does.
        /// </summary>
        [Test, Order(0)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexReplicaCheckpointDoesNotStallReplay()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            PopulateRangeIndex(primaryIndex, "idx1", 10);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            var replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            replicaRim.BeforeFlushSnapshotForTest = path =>
            {
                if (path.Contains(".flush.", StringComparison.Ordinal))
                    return;
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };

            try
            {
                var replicaLastSave = (long)context.clusterTestUtils.Execute(replicaEndpoint, "LASTSAVE", [], logger: context.logger);
                ClassicAssert.AreEqual("Background saving started", (string)context.clusterTestUtils.Execute(primaryEndpoint, "BGSAVE", [], logger: context.logger));
                ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)), "the replica checkpoint must queue a tree snapshot");

                // Replay continues past the checkpoint marker while the replica checkpoint is still persisting.
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", "field-new", "value-new"], logger: context.logger));
                var deadline = DateTime.UtcNow.AddSeconds(15);
                string replicated = null;
                while (DateTime.UtcNow < deadline && (replicated = (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", "field-new"], logger: context.logger)) != "value-new")
                    await Task.Delay(50);
                ClassicAssert.AreEqual("value-new", replicated, "replica replay must not wait for its checkpoint to persist");
                ClassicAssert.AreEqual(replicaLastSave, (long)context.clusterTestUtils.Execute(replicaEndpoint, "LASTSAVE", [], logger: context.logger), "the replica checkpoint must not persist before its tree snapshot");

                gate.Set();
                deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && (long)context.clusterTestUtils.Execute(replicaEndpoint, "LASTSAVE", [], logger: context.logger) == replicaLastSave)
                    await Task.Delay(50);
                ClassicAssert.AreNotEqual(replicaLastSave, (long)context.clusterTestUtils.Execute(replicaEndpoint, "LASTSAVE", [], logger: context.logger), "the replica checkpoint must complete");
            }
            finally
            {
                gate.Set();
                replicaRim.BeforeFlushSnapshotForTest = null;
            }

            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ValidateRangeIndex(replicaIndex, "idx1", 10);
        }

        /// <summary>
        /// A replica evicting a tree whose flush snapshots are still queued must keep applying replicated writes to it.
        /// </summary>
        [Test, Order(0)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexReplicaAppliesWritesWhileEvictedTreeAwaitsFlushSnapshots()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.CREATE", ["idx1", "DISK", "CACHESIZE", "65536", "MINRECORD", "8"], logger: context.logger));
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", "field-0", "value-0"], logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            var replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
            var replicaStore = context.nodes[replicaIndex].Provider.StoreWrapper.store;
            using var gate = new ManualResetEventSlim();
            replicaRim.BeforeFlushSnapshotForTest = path => _ = gate.Wait(TimeSpan.FromSeconds(60));

            const int cycles = 3;
            try
            {
                for (var cycle = 1; cycle <= cycles; cycle++)
                {
                    await Task.Run(() => replicaStore.Log.FlushAndEvict(wait: true)).WaitAsync(TimeSpan.FromSeconds(30));
                    ClassicAssert.AreEqual(0, replicaRim.LiveIndexCount, $"cycle {cycle}: the replica must evict the tree while its snapshot is queued");

                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", $"field-{cycle}", $"value-{cycle}"], logger: context.logger));
                    context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
                    ClassicAssert.AreEqual($"value-{cycle}", (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", $"field-{cycle}"], logger: context.logger), $"cycle {cycle}: the replica must apply the write replayed into the deferred window");
                }

                ClassicAssert.AreEqual(cycles, replicaRim.DeferredTreesReclaimedCount);
            }
            finally
            {
                gate.Set();
                replicaRim.BeforeFlushSnapshotForTest = null;
            }

            replicaRim.WaitForPendingFlushSnapshots("test");
            for (var i = 0; i <= cycles; i++)
                ClassicAssert.AreEqual($"value-{i}", (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", $"field-{i}"], logger: context.logger), $"field-{i}");
        }

        /// <summary>
        /// Replicated tree writes interleaved with log writes that flush and evict the tree's record during replay must all be
        /// applied on the replica, even while its flush snapshots lag behind.
        /// </summary>
        [Test, Order(0)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexReplicaKeepsWritesInterleavedWithLogPressure([Values(0, 20, 300)] int snapshotDelayMs)
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.CREATE", ["idx1", "DISK", "CACHESIZE", "65536", "MINRECORD", "8"], logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            var replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
            if (snapshotDelayMs > 0)
                replicaRim.BeforeFlushSnapshotForTest = _ => Thread.Sleep(snapshotDelayMs);

            const int writes = 400;
            var pad = new string('p', 2048);
            try
            {
                for (var i = 0; i < writes; i++)
                {
                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", $"field-{i:D4}", $"value-{i:D4}"], logger: context.logger));
                    for (var j = 0; j < 4; j++)
                        ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "SET", [$"pad-{i}-{j}", pad], logger: context.logger));
                }

                context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            }
            finally
            {
                replicaRim.BeforeFlushSnapshotForTest = null;
            }

            TestContext.Progress.WriteLine($"reclaimed={replicaRim.DeferredTreesReclaimedCount} parkedReused={replicaRim.ParkedTreesReusedCount} parkedDisposed={replicaRim.ParkedTreesDisposedCount} live={replicaRim.LiveIndexCount}");
            var missing = new List<int>();
            for (var i = 0; i < writes; i++)
            {
                ClassicAssert.AreEqual($"value-{i:D4}", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.GET", ["idx1", $"field-{i:D4}"], logger: context.logger), $"primary field-{i:D4}");
                if ((string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", $"field-{i:D4}"], logger: context.logger) != $"value-{i:D4}")
                    missing.Add(i);
            }

            ClassicAssert.IsEmpty(missing, $"replica lost {missing.Count} writes: {string.Join(",", missing.Take(40))}; reclaimed={replicaRim.DeferredTreesReclaimedCount} parkedReused={replicaRim.ParkedTreesReusedCount}");
        }

        /// <summary>
        /// A replica compacts its log for the checkpoint it takes at the primary's checkpoint marker; replay must keep
        /// applying new primary writes while that compaction runs.
        /// </summary>
        [Test, Order(0)]
        [Category("REPLICATION")]
        public async Task ClusterReplicaCheckpointCompactionDoesNotStallReplay()
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, enableRangeIndexPreview: true);
            context.CreateConnection();
            var (_, _) = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            PopulateRangeIndex(primaryIndex, "idx1", 10);
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            var replicaStoreWrapper = context.nodes[replicaIndex].Provider.StoreWrapper;
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            replicaStoreWrapper.BeforeCheckpointCompactionForTest = () =>
            {
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };

            try
            {
                ClassicAssert.AreEqual("Background saving started", (string)context.clusterTestUtils.Execute(primaryEndpoint, "BGSAVE", [], logger: context.logger));
                ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)), "the replica checkpoint must compact its log");

                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", "field-new", "value-new"], logger: context.logger));
                var deadline = DateTime.UtcNow.AddSeconds(15);
                string replicated = null;
                while (DateTime.UtcNow < deadline && (replicated = (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", "field-new"], logger: context.logger)) != "value-new")
                    await Task.Delay(50);
                ClassicAssert.AreEqual("value-new", replicated, "replica replay must not wait for checkpoint compaction");
            }
            finally
            {
                gate.Set();
                replicaStoreWrapper.BeforeCheckpointCompactionForTest = null;
            }

            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            ValidateRangeIndex(replicaIndex, "idx1", 10);
        }
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
            WaitForFlushSnapshots(primaryIndex);

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
            WaitForFlushSnapshots(primaryIndex);
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
            WaitForFlushSnapshots(primaryIndex);

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
                string[] expected = [files[^1]];
                Assert.That(replicaFiles.TryGetValue(keyHash, out var received) ? received : [], Is.EqualTo(expected), $"only the latest flush file of {keyHash} must be shipped");
            }

            foreach (var index in new[] { "idx1", "idx2" })
            {
                ValidateRangeIndex(replicaIndex, index, 10);
                ClassicAssert.AreEqual("value0010", (string)context.clusterTestUtils.Execute((IPEndPoint)context.endpoints[replicaIndex], "RI.GET", [index, "field0010"], logger: context.logger));
            }
        }

        /// <summary>
        /// Flush files a replica wrote in its own hybrid log address space are meaningless once it adopts the primary's
        /// checkpoint. A leftover file at a higher address would otherwise be taken as the newest flush of its tree.
        /// </summary>
        [Test, Order(11)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexFullSyncPurgesReplicaLocalFlushFiles()
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
            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, long.Parse(context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger)));

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            var primaryFlushFile = Path.GetFileName(Directory.GetFiles(GetRiLogRoot(primaryIndex), "*.flush.bftree").Single());
            var replicaRoot = GetRiLogRoot(replicaIndex);
            Directory.CreateDirectory(replicaRoot);
            var staleFile = Path.Combine(replicaRoot, $"{primaryFlushFile.Split('.')[0]}.{long.MaxValue - 1:x16}.flush.bftree");
            File.WriteAllBytes(staleFile, [1, 2, 3]);

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            string[] expected = [primaryFlushFile];
            Assert.That(Directory.GetFiles(replicaRoot, "*.flush.bftree").Select(Path.GetFileName), Is.EqualTo(expected), "the replica must hold exactly the flush files of the adopted checkpoint");
            ValidateRangeIndex(replicaIndex, "idx1", 10);
        }

        /// <summary>
        /// A full sync replays the primary's AOF tail after the checkpoint; tree writes in that tail interleaved with log
        /// writes that flush and evict the tree records during replay must all be applied on the replica.
        /// </summary>
        [Test, Order(11)]
        [Category("REPLICATION")]
        public void ClusterRangeIndexFullSyncReplaysTailWritesInterleavedWithLogPressure([Values(1, 4)] int treeCount, [Values(0, 50)] int snapshotDelayMs)
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            for (var t = 0; t < treeCount; t++)
            {
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.CREATE", [$"idx{t}", "DISK", "CACHESIZE", "65536", "MINRECORD", "8"], logger: context.logger));
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", [$"idx{t}", "seed", "seed"], logger: context.logger));
            }

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            const int rounds = 300;
            var pad = new string('p', 2048);
            for (var i = 0; i < rounds; i++)
            {
                for (var t = 0; t < treeCount; t++)
                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", [$"idx{t}", $"field-{i:D4}", $"value-{t}-{i:D4}"], logger: context.logger));
                for (var j = 0; j < 4; j++)
                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "SET", [$"pad-{i}-{j}", pad], logger: context.logger));
            }

            var replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
            if (snapshotDelayMs > 0)
                replicaRim.BeforeFlushSnapshotForTest = _ => Thread.Sleep(snapshotDelayMs);
            try
            {
                ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));
                context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            }
            finally
            {
                replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
                replicaRim.BeforeFlushSnapshotForTest = null;
            }

            TestContext.Progress.WriteLine($"reclaimed={replicaRim.DeferredTreesReclaimedCount} parkedReused={replicaRim.ParkedTreesReusedCount} parkedDisposed={replicaRim.ParkedTreesDisposedCount} live={replicaRim.LiveIndexCount}");
            var missing = new List<string>();
            for (var t = 0; t < treeCount; t++)
            {
                for (var i = 0; i < rounds; i++)
                {
                    var expected = $"value-{t}-{i:D4}";
                    ClassicAssert.AreEqual(expected, (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.GET", [$"idx{t}", $"field-{i:D4}"], logger: context.logger), $"primary idx{t} field-{i:D4}");
                    if ((string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", [$"idx{t}", $"field-{i:D4}"], logger: context.logger) != expected)
                        missing.Add($"t{t}r{i}");
                }
            }

            ClassicAssert.IsEmpty(missing, $"replica lost {missing.Count} writes: {string.Join(",", missing.Take(40))}");
        }

        /// <summary>
        /// A full sync from a checkpoint whose tree snapshots were cut while writes ran must apply every tail write on the replica,
        /// even while the restored trees are evicted, reclaimed, and read concurrently during replay.
        /// </summary>
        [Test, Order(11)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexFullSyncFromLiveCheckpointSnapshotsKeepsTailWrites([Values(2, 4)] int treeCount, [Values(0, 50)] int snapshotDelayMs)
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            context.CreateInstances(2, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            const int fields = 64;
            var expected = new Dictionary<(int, int), string>();
            void Write(int t, int f, string v)
            {
                if (v == null)
                    _ = context.clusterTestUtils.Execute(primaryEndpoint, "RI.DEL", [$"idx{t}", $"field-{f:D3}"], logger: context.logger);
                else
                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", [$"idx{t}", $"field-{f:D3}", v], logger: context.logger));
                expected[(t, f)] = v;
            }

            for (var t = 0; t < treeCount; t++)
            {
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.CREATE", [$"idx{t}", "DISK", "CACHESIZE", "65536", "MINRECORD", "8"], logger: context.logger));
                for (var f = 0; f < fields; f++)
                    Write(t, f, $"load-{t}-{f}");
            }

            var pad = new string('p', 2048);
            var round = 0;
            void Round()
            {
                round++;
                for (var t = 0; t < treeCount; t++)
                {
                    // Like the soak: re-set the field deleted last round, set a few more, then delete one.
                    Write(t, (round - 1) * 7 % fields, $"r{round}-{t}-a");
                    for (var k = 1; k <= 4; k++)
                        Write(t, (round * 13 + k * 5) % fields, $"r{round}-{t}-{k}");
                    Write(t, round * 7 % fields, null);
                }

                for (var j = 0; j < 4; j++)
                    ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "SET", [$"pad-{round}-{j}", pad], logger: context.logger));
            }

            // Hold the primary's checkpoint snapshots so they are cut while writes keep landing on the trees.
            var primaryRim = context.nodes[primaryIndex].Provider.StoreWrapper.rangeIndexManager;
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            primaryRim.BeforeFlushSnapshotForTest = path =>
            {
                if (path.Contains(".flush.", StringComparison.Ordinal))
                    return;
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };
            try
            {
                var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
                context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
                ClassicAssert.AreEqual("Background saving started", (string)context.clusterTestUtils.Execute(primaryEndpoint, "BGSAVE", [], logger: context.logger));
                ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)), "the checkpoint must queue a tree snapshot");
                for (var i = 0; i < 40; i++)
                    Round();
                gate.Set();
                context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);
            }
            finally
            {
                gate.Set();
                primaryRim.BeforeFlushSnapshotForTest = null;
            }

            for (var i = 0; i < 200; i++)
                Round();

            var replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
            if (snapshotDelayMs > 0)
                replicaRim.BeforeFlushSnapshotForTest = _ => Thread.Sleep(snapshotDelayMs);

            using var stopReads = new CancellationTokenSource();
            var reads = Task.Run(async () =>
            {
                var rng = new Random(7);
                while (!stopReads.IsCancellationRequested)
                {
                    try
                    {
                        _ = context.clusterTestUtils.Execute(replicaEndpoint, "READONLY", [], logger: context.logger);
                        _ = context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", [$"idx{rng.Next(treeCount)}", $"field-{rng.Next(fields):D3}"], logger: context.logger);
                    }
                    catch
                    {
                        // The replica rejects reads until it has synced.
                    }

                    await Task.Delay(5);
                }
            });

            try
            {
                ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));
                for (var i = 0; i < 200; i++)
                    Round();
                context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);
            }
            finally
            {
                stopReads.Cancel();
                await reads;
                replicaRim = context.nodes[replicaIndex].Provider.StoreWrapper.rangeIndexManager;
                replicaRim.BeforeFlushSnapshotForTest = null;
            }

            TestContext.Progress.WriteLine($"reclaimed={replicaRim.DeferredTreesReclaimedCount} parkedReused={replicaRim.ParkedTreesReusedCount} parkedDisposed={replicaRim.ParkedTreesDisposedCount} live={replicaRim.LiveIndexCount}");
            var missing = new List<string>();
            foreach (var ((t, f), value) in expected)
            {
                ClassicAssert.AreEqual(value, (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.GET", [$"idx{t}", $"field-{f:D3}"], logger: context.logger), $"primary idx{t} field-{f:D3}");
                var actual = (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", [$"idx{t}", $"field-{f:D3}"], logger: context.logger);
                if (actual != value)
                    missing.Add($"t{t}f{f}={actual ?? "nil"}!={value ?? "nil"}");
            }

            ClassicAssert.IsEmpty(missing, $"replica diverged on {missing.Count} fields: {string.Join(",", missing.Take(40))}");
            ClassicAssert.Greater(replicaRim.ReplayWriteCheckCount, 0, "the replica must re-read its replayed writes");
            ClassicAssert.AreEqual(0, replicaRim.ReplayWriteLossCount, "the replica must not lose a replayed write");
        }

        /// <summary>
        /// A full sync streams RangeIndex files spanning many more chunks than the in-flight window, and the replica
        /// must receive them byte for byte, with or without wire compression.
        /// </summary>
        [Test, Order(10)]
        [Category("REPLICATION")]
        public async Task ClusterRangeIndexFullSyncTransfersMultiChunkFiles([Values] bool compress)
        {
            var primaryIndex = 0;
            var replicaIndex = 1;
            var nodesCount = 2;
            var fieldCount = 4000;
            var value = string.Concat(Enumerable.Range(0, 15).Select(i => Guid.NewGuid().ToString("N")));

            context.CreateInstances(nodesCount, disableObjects: true, enableAOF: true, lowMemory: true, enableRangeIndexPreview: true, replicaSyncCompressRangeIndex: compress);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(primaryIndex, [(0, 16383)], true, context.logger));
            context.clusterTestUtils.SetConfigEpoch(primaryIndex, primaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(replicaIndex, replicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(primaryIndex, replicaIndex, logger: context.logger);
            context.clusterTestUtils.WaitAll(context.logger);

            var primaryEndpoint = (IPEndPoint)context.endpoints[primaryIndex];
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.CREATE", ["idx1", "DISK", "CACHESIZE", "1048576", "MAXKEYLEN", "64"], logger: context.logger));
            for (var i = 0; i < fieldCount; i++)
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.Execute(primaryEndpoint, "RI.SET", ["idx1", $"field{i:D4}", $"{value}{i:D4}"], logger: context.logger));

            await context.clusterTestUtils.FlushAndWaitForStoreAsync(primaryIndex, long.Parse(context.clusterTestUtils.GetInfo(primaryIndex, "STORE", "Log.TailAddress", logger: context.logger)));

            var primaryLastSaveTime = context.clusterTestUtils.LastSave(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(primaryIndex, primaryLastSaveTime);
            context.clusterTestUtils.Checkpoint(primaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(primaryIndex, primaryLastSaveTime, logger: context.logger);

            var primaryFlushFiles = Directory.GetFiles(GetRiLogRoot(primaryIndex), "*.flush.bftree");
            Assert.That(primaryFlushFiles.Select(f => new FileInfo(f).Length).Max(), Is.GreaterThan(16L << 17), "a flush file must span many chunks for this scenario to be under test");

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: replicaIndex, primaryNodeIndex: primaryIndex, failEx: false, logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(primaryIndex, replicaIndex, context.logger);

            foreach (var primaryFile in primaryFlushFiles)
            {
                var replicaFile = Path.Combine(GetRiLogRoot(replicaIndex), Path.GetFileName(primaryFile));
                Assert.That(File.ReadAllBytes(replicaFile), Is.EqualTo(File.ReadAllBytes(primaryFile)), $"{Path.GetFileName(primaryFile)} must be transferred intact");
            }

            var replicaEndpoint = (IPEndPoint)context.endpoints[replicaIndex];
            for (var i = 0; i < fieldCount; i += 97)
                ClassicAssert.AreEqual($"{value}{i:D4}", (string)context.clusterTestUtils.Execute(replicaEndpoint, "RI.GET", ["idx1", $"field{i:D4}"], logger: context.logger));
        }
    }
}