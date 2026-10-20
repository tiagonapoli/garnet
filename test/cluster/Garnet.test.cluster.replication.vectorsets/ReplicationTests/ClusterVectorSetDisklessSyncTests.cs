// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    [TestFixture]
    [NonParallelizable]
    public class ClusterVectorSetDisklessSyncTests : VectorSetReplicationTestBase
    {
        private const int PrimaryIndex = 0;
        private const int ReplicaIndex = 1;

        /// <summary>Creates a diskless-sync cluster with a low threshold so re-attach takes full sync.</summary>
        private void SetupDisklessCluster(int nodeCount, int commitFrequencyMs = 0, bool asyncReplay = false)
        {
            // Once the replica's AOF gap exceeds this, the primary stops replaying and full syncs instead
            context.CreateInstances(
                nodeCount,
                enableAOF: true,
                CommitFrequencyMs: commitFrequencyMs,
                asyncReplay: asyncReplay,
                enableDisklessSync: true,
                replicaDisklessSyncFullSyncAofThreshold: "1k",
                timeout: timeout);

            context.MeetAndAssignSlotsAllNodes(nodeCount);
        }

        /// <summary>Detach, move past incremental replay, then re-attach so the sync must be a full one.</summary>
        private void ForceDisklessFullSync(int replicaIndex = ReplicaIndex)
        {
            context.clusterTestUtils.ResetReplica(replicaIndex, PrimaryIndex, context.logger);
            context.clusterTestUtils.AdvancePrimaryPastReplicaAofWindow(PrimaryIndex);
            context.clusterTestUtils.AttachReplicaToPrimary(replicaIndex, PrimaryIndex, logger: context.logger);
        }

        /// <summary>
        /// Populated sets travel by diskless full sync, then the replica reads them. This catches
        /// foreign IndexPtr dereferences that used to surface at NativeDiskANNMethods.card. Multiple
        /// sets of differing sizes also expose cross-set substitution, where a foreign handle points
        /// at the wrong live index instead of faulting.
        /// </summary>
        [Test]
        public void VectorSetReadableOnReplicaAfterDisklessFullSync()
        {
            // Same hash slot, so both keys live on the single primary in this topology.
            const string SmallKey = "{vsdisk}small";
            const string LargeKey = "{vsdisk}large";
            const int SmallElements = 10;
            const int LargeElements = 500;

            SetupDisklessCluster(2);
            context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger);

            // Attached writes arrive as VADD replay, so the replica builds its own index.
            PopulateVectorSet(PrimaryIndex, LargeKey, LargeElements);
            PopulateVectorSet(PrimaryIndex, SmallKey, SmallElements);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, logger: context.logger);

            ClassicAssert.AreEqual(LargeElements, VectorSetSize(PrimaryIndex, LargeKey));
            ClassicAssert.AreEqual(SmallElements, VectorSetSize(PrimaryIndex, SmallKey));

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, SmallKey);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, LargeKey);

            ForceDisklessFullSync();

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, SmallKey);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, LargeKey);

            // Neither key may report the other's cardinality.
            ClassicAssert.AreEqual(SmallElements, VectorSetSize(ReplicaIndex, SmallKey), "small set must not report elements belonging to another Vector Set");
            ClassicAssert.AreEqual(LargeElements, VectorSetSize(ReplicaIndex, LargeKey), "large set must report exactly its own elements");
        }

        /// <summary>
        /// Control: without full sync, replicated VADD payloads build the replica's local index,
        /// so this should pass even if full-sync cases fail.
        /// </summary>
        [Test]
        public void VectorSetReadableOnReplicaWithoutFullSync()
        {
            const string Key = "{vsdisk}control";
            const int Elements = 500;

            SetupDisklessCluster(2);
            context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger);

            PopulateVectorSet(PrimaryIndex, Key, Elements);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, logger: context.logger);

            context.clusterTestUtils.ReadOnly(ReplicaIndex);

            // Without full sync, VADD replay builds a local index and isolates full sync as the cause.
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, Key);
        }

        [Test]
        public void VectorSetReplicatesAfterStartupRecovery()
        {
            const string VectorSetKey = "vs-startup-recovery";

            SetupDisklessCluster(2);

            context.nodeOptions[ReplicaIndex].Recover = true;
            context.RestartNode(ReplicaIndex, ensureAofFlush: true);
            context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger);

            PopulateVectorSet(PrimaryIndex, VectorSetKey, count: 1);
            ClassicAssert.AreEqual(1, VectorSetSize(PrimaryIndex, VectorSetKey));

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, VectorSetKey);
        }

        /// <summary>
        /// Every replica takes its own diskless full sync from the same populated primary.
        /// Each must rebuild its own index, not share per-primary state.
        /// </summary>
        [Test]
        public void VectorSetReplicatedToEveryReplicaByDisklessFullSync()
        {
            const string Key = "{vsdisk}fanout";
            const int Elements = 200;
            const int ReplicaCount = 3;

            SetupDisklessCluster(1 + ReplicaCount);

            // Populated before any replica exists, so each attach carries the index record.
            PopulateVectorSet(PrimaryIndex, Key, Elements);

            // Each replica starts with its own replication id, so every attach takes a full sync.
            for (var replica = 1; replica <= ReplicaCount; replica++)
            {
                context.clusterTestUtils.AttachReplicaToPrimary(replica, PrimaryIndex, logger: context.logger);
            }

            for (var replica = 1; replica <= ReplicaCount; replica++)
            {
                context.clusterTestUtils.ReadOnly(replica);
                AssertFullyReplicated(PrimaryIndex, replica, Key);
            }

            // No two nodes may share a handle.
            for (var a = 0; a <= ReplicaCount; a++)
            {
                for (var b = a + 1; b <= ReplicaCount; b++)
                {
                    AssertOwnsItsIndex(b, a, Key);
                }
            }
        }

        /// <summary>
        /// Promotes a replica after diskless full sync, then syncs back to the old primary.
        /// The record crosses both directions and both nodes must own their indexes.
        /// </summary>
        [Test]
        public void VectorSetSurvivesFailoverAfterDisklessFullSync()
        {
            const string Key = "{vsdisk}failover";
            const int Elements = 200;

            SetupDisklessCluster(2);

            PopulateVectorSet(PrimaryIndex, Key, Elements);

            // The replica has its own replication id, so the attach takes a full sync.
            context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger);

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, Key);

            context.FailoverTo(ReplicaIndex, PrimaryIndex);

            context.clusterTestUtils.ReadOnly(PrimaryIndex);
            AssertFullyReplicated(ReplicaIndex, PrimaryIndex, Key);

            // Inherited sets must still accept writes and replicate them back.
            PopulateVectorSet(ReplicaIndex, Key, count: 50);
            context.clusterTestUtils.WaitForReplicaAofSync(ReplicaIndex, PrimaryIndex, logger: context.logger);

            context.clusterTestUtils.ReadOnly(PrimaryIndex);
            AssertFullyReplicated(ReplicaIndex, PrimaryIndex, Key);
        }

        /// <summary>
        /// A brand-new Vector Set created on a node that received a set by diskless full sync must
        /// not be handed the streamed set's context. The stream-in path installs the index record
        /// but never reserves its context in the receiver's allocator, so the next allocation can
        /// collide with it. Promoting the receiver makes it writable so the fresh create runs there.
        /// </summary>
        [Test]
        public void FreshVectorSetDoesNotReuseStreamedContextAfterDisklessFullSync()
        {
            const string StreamedKey = "{vsctx}streamed";
            const string FreshKey = "{vsctx}fresh";
            const int Elements = 200;

            SetupDisklessCluster(2);

            PopulateVectorSet(PrimaryIndex, StreamedKey, Elements);

            // The replica has its own replication id, so the attach takes a full sync that carries
            // the streamed set's index record (and its context) verbatim.
            context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger);

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, StreamedKey);

            // Promote the receiver so the fresh create allocates a context on the very node that
            // took the diskless full sync.
            context.FailoverTo(ReplicaIndex, PrimaryIndex);

            var streamedContext = ReadPersistedContext(ReplicaIndex, StreamedKey);

            PopulateVectorSet(ReplicaIndex, FreshKey, count: 50);
            var freshContext = ReadPersistedContext(ReplicaIndex, FreshKey);

            ClassicAssert.AreNotEqual(streamedContext, freshContext, $"a fresh Vector Set '{FreshKey}' was handed the streamed set '{StreamedKey}' context ({streamedContext}); the diskless full-sync receiver never reserved the streamed context, so the allocator reissued it");
        }

        /// <summary>
        /// A diskless full sync that dies after records were streamed but before ATTACH_SYNC never reaches
        /// ReconcileRecoveredState, so the recovery bookkeeping it accumulated is still pending. The retry
        /// re-streams the same ContextMetadata records, so unless the intervening flush discards that
        /// bookkeeping RecoveredContextMetadata rejects the duplicate index and every later attempt fails too.
        /// </summary>
        [Test]
        public void VectorSetDisklessFullSyncRecoversAfterAbortedAttempt()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            const string Key = "{vsretry}aborted";
            const int Elements = 200;

            SetupDisklessCluster(2);

            PopulateVectorSet(PrimaryIndex, Key, Elements);

            using (ExceptionInjectionHelper.EnabledScope(ExceptionInjectionType.Replication_Diskless_Sync_Reset_Cts))
            {
                var failed = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: ReplicaIndex, primaryNodeIndex: PrimaryIndex, failEx: false, logger: context.logger);
                ClassicAssert.AreEqual($"Exception injection triggered {ExceptionInjectionType.Replication_Diskless_Sync_Reset_Cts}", failed);
            }

            var resp = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: ReplicaIndex, primaryNodeIndex: PrimaryIndex, logger: context.logger);
            ClassicAssert.AreEqual("OK", resp, "the retried diskless full sync must not inherit recovery state from the aborted attempt");

            context.clusterTestUtils.WaitForReplicasConnected(PrimaryIndex, 1, logger: context.logger);

            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, Key);
        }

        /// <summary>
        /// An aborted diskless full sync leaves raw namespaced records on the replica: ContextMetadata in
        /// <see cref="VectorManager.MetadataNamespace"/> and element data under the streamed set's own
        /// namespaces. Those arrive as raw bytes, so nothing reserves their contexts in the replica's
        /// VectorManager, and the attempt dies before ReconcileRecoveredState can mark the unreferenced ones
        /// for cleanup. The only thing that can discard them is the CLUSTER FLUSHALL the primary issues at the
        /// start of the next full sync.
        ///
        /// Deleting the set on the primary between the two attempts makes the leak observable: after the
        /// retry the replica must hold nothing at all for the doomed context, because it is no longer part
        /// of the synced state and no cleanup pass on the replica will ever target it.
        /// </summary>
        [Test]
        public void AbortedDisklessFullSyncLeavesNoOrphanNamespacedRecords()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            const string DoomedKey = "{vsorphan}doomed";
            const string KeptKey = "{vsorphan}kept";
            const int DoomedElements = 200;
            const int KeptElements = 100;

            SetupDisklessCluster(2);

            PopulateVectorSet(PrimaryIndex, DoomedKey, DoomedElements);
            PopulateVectorSet(PrimaryIndex, KeptKey, KeptElements);

            var doomedContext = ReadPersistedContext(PrimaryIndex, DoomedKey);
            var keptContext = ReadPersistedContext(PrimaryIndex, KeptKey);
            ClassicAssert.AreNotEqual(doomedContext, keptContext, "the two Vector Sets must not share a context or the test cannot distinguish their records");

            using (ExceptionInjectionHelper.EnabledScope(ExceptionInjectionType.Replication_Diskless_Sync_Reset_Cts))
            {
                var failed = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: ReplicaIndex, primaryNodeIndex: PrimaryIndex, failEx: false, logger: context.logger);
                ClassicAssert.AreEqual($"Exception injection triggered {ExceptionInjectionType.Replication_Diskless_Sync_Reset_Cts}", failed);
            }

            // The injection fires a full second into streaming, well after CLUSTER FLUSHALL and after these
            // small sets have been sent, so the replica is holding streamed records that no retry needs.
            var strandedOnReplica = RecordsHeldForContext(ReplicaIndex, doomedContext);
            ClassicAssert.Greater(strandedOnReplica, 0, $"the aborted attempt streamed nothing for context {doomedContext}, so this test would not exercise the flush that has to discard it");

            // Drop the set on the primary, so after the retry the doomed context is not part of the
            // replicated state and anything the replica still holds for it is unreachable garbage.
            _ = context.clusterTestUtils.Execute(context.clusterTestUtils.GetEndPoint(PrimaryIndex), "DEL", [DoomedKey], logger: context.logger);
            WaitForContextDrained(PrimaryIndex, doomedContext, "the primary never finished cleaning up the deleted Vector Set, so the test cannot tell orphaned replica records from replicated ones");

            var resp = context.clusterTestUtils.ClusterReplicate(replicaNodeIndex: ReplicaIndex, primaryNodeIndex: PrimaryIndex, logger: context.logger);
            ClassicAssert.AreEqual("OK", resp, "the retried diskless full sync must not inherit recovery state from the aborted attempt");

            context.clusterTestUtils.WaitForReplicasConnected(PrimaryIndex, 1, logger: context.logger);
            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            AssertFullyReplicated(PrimaryIndex, ReplicaIndex, KeptKey);

            // Guards the checks below: if the census saw nothing at all they would pass vacuously.
            var keptRecords = RecordsHeldForContext(ReplicaIndex, keptContext);
            ClassicAssert.Greater(keptRecords, 0, $"the census found no records for the surviving set's context {keptContext} on node {ReplicaIndex}, so it is not observing replicated Vector Set data and the orphan checks below prove nothing");

            // Element data in the doomed set's namespaces must be gone, not merely unreferenced.
            var orphanedRecords = RecordsHeldForContext(ReplicaIndex, doomedContext);
            ClassicAssert.AreEqual(0, orphanedRecords, $"node {ReplicaIndex} still holds {orphanedRecords} namespaced records under the deleted set's context {doomedContext} (it held {strandedOnReplica} right after the aborted attempt); the full sync that followed did not discard what the aborted attempt streamed, and no cleanup pass will ever target that context because nothing on the replica marked it as needing cleanup");

            // ContextMetadata records must describe the synced state and nothing else.
            var primaryCensus = CensusNamespacedRecords(PrimaryIndex);
            var replicaCensus = CensusNamespacedRecords(ReplicaIndex);

            var orphanNamespaces = replicaCensus.Keys.Where(ns => !primaryCensus.ContainsKey(ns)).OrderBy(static ns => ns).ToList();
            ClassicAssert.IsEmpty(orphanNamespaces, $"node {ReplicaIndex} holds records under namespaces node {PrimaryIndex} does not have at all: [{string.Join(", ", orphanNamespaces)}]");

            ClassicAssert.AreEqual(
                primaryCensus.GetValueOrDefault(VectorManager.MetadataNamespace),
                replicaCensus.GetValueOrDefault(VectorManager.MetadataNamespace),
                $"node {ReplicaIndex} disagrees with node {PrimaryIndex} on the number of live ContextMetadata records, so the aborted attempt's metadata survived the flush");

            foreach (var (ns, expected) in primaryCensus.Where(static kv => kv.Key != VectorManager.MetadataNamespace))
            {
                ClassicAssert.AreEqual(expected, replicaCensus.GetValueOrDefault(ns), $"node {ReplicaIndex} holds {replicaCensus.GetValueOrDefault(ns)} records in namespace {ns} but node {PrimaryIndex} holds {expected}");
            }
        }

        /// <summary>
        /// A Vector Set deleted and recreated under the same key after the diskless snapshot's version shift. The checkpoint has
        /// frozen the deleted index record, so the primary must still clean up the deleted set and match the replica.
        /// </summary>
        [Test]
        public void VectorSetDeletedAndRecreatedBeforeFinalDisklessScanReplicatesNewSetOnly()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            const string Key = "{vsdisk}vs-recreate-new-version";
            const int DeletedElements = 8;

            SetupDisklessCluster(2);

            PopulateVectorSet(PrimaryIndex, Key, DeletedElements);

            // The primary deletes the set while the checkpoint has frozen its index record.
            AttachReplicaWhilePaused(ExceptionInjectionType.Replication_Pause_Before_Diskless_Snapshot_Final_Scan, () =>
            {
                DeleteOnPrimary(Key);
                PopulateVectorSet(PrimaryIndex, Key, count: 4);
            });

            AssertNoDeletedElementsResurrected(Key, DeletedElements);
        }

        private void DeleteOnPrimary(string key)
        {
            var primary = context.clusterTestUtils.GetEndPoint(PrimaryIndex);
            var deleted = (int)context.clusterTestUtils.Execute(primary, "DEL", [key], logger: context.logger);
            ClassicAssert.AreEqual(0, (int)context.clusterTestUtils.Execute(primary, "EXISTS", [key], logger: context.logger), $"DEL of '{key}' (reply {deleted}) did not remove it from node {PrimaryIndex}");
        }

        /// <summary>Attaches the replica, runs <paramref name="whilePaused"/> while the sync is held at <paramref name="pause"/>, then lets it finish.</summary>
        private void AttachReplicaWhilePaused(ExceptionInjectionType pause, Action whilePaused)
        {
            using (ExceptionInjectionHelper.EnabledScope(pause))
            {
                var attach = Task.Run(() => context.clusterTestUtils.AttachReplicaToPrimary(ReplicaIndex, PrimaryIndex, logger: context.logger));
                ExceptionInjectionHelper.WaitOnClearAsync(pause).WaitAsync(TimeSpan.FromSeconds(timeout)).GetAwaiter().GetResult();

                whilePaused();

                ExceptionInjectionHelper.EnableException(pause);
                attach.WaitAsync(TimeSpan.FromSeconds(timeout)).GetAwaiter().GetResult();
            }
        }

        /// <summary>Asserts the replica holds exactly the primary's current set, with none of the first <paramref name="deletedElements"/> elements written under the key.</summary>
        private void AssertNoDeletedElementsResurrected(string key, int deletedElements)
        {
            context.clusterTestUtils.ReadOnly(ReplicaIndex);
            WaitForReplication(PrimaryIndex, ReplicaIndex);

            var primary = context.clusterTestUtils.GetEndPoint(PrimaryIndex);
            var replica = context.clusterTestUtils.GetEndPoint(ReplicaIndex);

            var resurrected = new List<int>();
            for (var i = 0; i < deletedElements; i++)
            {
                var element = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(element, i);

                ClassicAssert.IsEmpty((string[])context.clusterTestUtils.Execute(primary, "VEMB", [key, element], skipLogging: true), $"element {i} was deleted with its Vector Set but node {PrimaryIndex} still returns it");

                if (((string[])context.clusterTestUtils.Execute(replica, "VEMB", [key, element], skipLogging: true)).Length != 0)
                    resurrected.Add(i);
            }

            ClassicAssert.IsEmpty(resurrected, $"node {ReplicaIndex} returns {resurrected.Count} of {deletedElements} elements of the deleted Vector Set from the recreated '{key}': [{string.Join(", ", resurrected)}]");
            ClassicAssert.AreEqual(VectorSetSize(PrimaryIndex, key), VectorSetSize(ReplicaIndex, key), $"node {ReplicaIndex} disagrees with node {PrimaryIndex} on the cardinality of the recreated '{key}'");

            // Contexts are allocated per node, so compare the set's own records and the store-wide total rather than namespaces.
            GetStoreWrapper(context.nodes[PrimaryIndex]).DefaultDatabase.VectorManager.WaitForQuiescence();
            GetStoreWrapper(context.nodes[ReplicaIndex]).DefaultDatabase.VectorManager.WaitForQuiescence();
            ClassicAssert.AreEqual(RecordsHeldForContext(PrimaryIndex, ReadPersistedContext(PrimaryIndex, key)), RecordsHeldForContext(ReplicaIndex, ReadPersistedContext(ReplicaIndex, key)), $"node {ReplicaIndex} and node {PrimaryIndex} hold a different number of element records for the recreated '{key}'");

            static int TotalElementRecords(Dictionary<ulong, int> census) => census.Where(static kv => kv.Key != VectorManager.MetadataNamespace).Sum(static kv => kv.Value);
            var primaryTotal = TotalElementRecords(CensusNamespacedRecords(PrimaryIndex));
            var replicaTotal = TotalElementRecords(CensusNamespacedRecords(ReplicaIndex));
            ClassicAssert.AreEqual(primaryTotal, replicaTotal, $"node {ReplicaIndex} holds {replicaTotal} Vector Set element records but node {PrimaryIndex} holds {primaryTotal}; one of them retains data of the deleted set");
        }
    }
}