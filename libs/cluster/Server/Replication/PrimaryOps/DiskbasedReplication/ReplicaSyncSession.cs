// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Garnet.client;
using Garnet.common;
using Garnet.server;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.cluster
{
    internal sealed partial class ReplicaSyncSession(
        StoreWrapper storeWrapper,
        ClusterProvider clusterProvider,
        AofAddress replicaAofBeginAddress,
        AofAddress replicaAofTailAddress,
        SyncMetadata replicaSyncMetadata = null,
        CancellationToken token = default,
        string replicaNodeId = null,
        string replicaAssignedPrimaryId = null,
        CheckpointEntry replicaCheckpointEntry = null,
        ILogger logger = null) : IDisposable
    {
        readonly StoreWrapper storeWrapper = storeWrapper;
        readonly ClusterProvider clusterProvider = clusterProvider;
        public readonly SyncMetadata replicaSyncMetadata = replicaSyncMetadata;
        readonly CancellationToken token = token;
        readonly CancellationTokenSource cts = new();
        readonly SemaphoreSlim signalCompletion = new(0);

        public readonly string replicaNodeId = replicaNodeId;
        public readonly string replicaAssignedPrimaryId = replicaAssignedPrimaryId;
        private readonly AofAddress replicaAofBeginAddress = replicaAofBeginAddress;
        private readonly AofAddress replicaAofTailAddress = replicaAofTailAddress;

        private readonly CheckpointEntry replicaCheckpointEntry = replicaCheckpointEntry;

        private readonly ILogger logger = logger;

        public string errorMsg = default;

        const int validateMetadataMaxRetryCount = 10;

        public void Dispose()
        {
            AofSyncDriver?.DisposeClient();
            AofSyncDriver = null;
            cts.Cancel();
            cts.Dispose();
            signalCompletion?.Dispose();
        }

        private bool ValidateMetadata(
            CheckpointEntry localEntry,
            out long index_size,
            out LogFileInfo hlog_size,
            out bool skipLocalMainStoreCheckpoint,
            out bool reuseNewerReplicaCheckpoint)
        {
            hlog_size = default;
            index_size = -1L;

            // Local and remote checkpoints are of same history if both of the following hold
            // 1. There is a checkpoint available at remote node
            // 2. Remote and local checkpoints contain the same PrimaryReplId
            var sameMainStoreCheckpointHistory = !string.IsNullOrEmpty(replicaCheckpointEntry.metadata.storePrimaryReplId) && replicaCheckpointEntry.metadata.storePrimaryReplId.Equals(localEntry.metadata.storePrimaryReplId);
            // We will not send the latest local checkpoint if any of the following hold
            // 1. Local node does not have any checkpoints
            // 2. Local checkpoint is of same version and history as the remote checkpoint
            // 3. Remote checkpoint is newer than the local one and was built from AOF this node still holds
            skipLocalMainStoreCheckpoint = localEntry.metadata.storeHlogToken == default || (sameMainStoreCheckpointHistory && localEntry.metadata.storeVersion == replicaCheckpointEntry.metadata.storeVersion);
            var reuseReason = "version-mismatch";
            reuseNewerReplicaCheckpoint = !skipLocalMainStoreCheckpoint && sameMainStoreCheckpointHistory && CanReuseNewerReplicaCheckpoint(localEntry, out reuseReason);
            skipLocalMainStoreCheckpoint |= reuseNewerReplicaCheckpoint;

            var reason = skipLocalMainStoreCheckpoint && !reuseNewerReplicaCheckpoint
                ? (localEntry.metadata.storeHlogToken == default ? "no-local-checkpoint" : "same-history-and-version")
                : (!sameMainStoreCheckpointHistory ? "history-mismatch" : reuseReason);
            logger?.LogInformation("[ResyncDecision] sendCheckpoint={send} reason={reason} replicaReplId={replicaReplId} localReplId={localReplId} replicaVersion={replicaVersion} localVersion={localVersion} replicaToken={replicaToken} localToken={localToken} replicaCoveredAof={replicaCoveredAof} localCoveredAof={localCoveredAof}",
                !skipLocalMainStoreCheckpoint, reason, replicaCheckpointEntry.metadata.storePrimaryReplId, localEntry.metadata.storePrimaryReplId, replicaCheckpointEntry.metadata.storeVersion, localEntry.metadata.storeVersion, replicaCheckpointEntry.metadata.storeHlogToken, localEntry.metadata.storeHlogToken, replicaCheckpointEntry.metadata.storeCheckpointCoveredAofAddress, localEntry.metadata.storeCheckpointCoveredAofAddress);
            // Acquire metadata for main store
            // If failed then this checkpoint is not usable because it is corrupted
            if (!skipLocalMainStoreCheckpoint && !clusterProvider.replicationManager.TryAcquireSettledMetadataForMainStore(localEntry, out hlog_size, out index_size))
                return false;

            return true;
        }

        /// <summary>
        /// Returns the highest AOF address whose records this node holds unchanged since any replica received them.
        /// </summary>
        private AofAddress GetStableAofTailAddress()
        {
            var stableTail = storeWrapper.appendOnlyFile.Log.CommittedUntilAddress;
            var recoveredTail = clusterProvider.replicationManager.RecoveredAofTailAddress;
            if (recoveredTail.Length == stableTail.Length)
                stableTail.MinExchange(recoveredTail);
            return stableTail;
        }

        /// <summary>
        /// Whether the replica can recover its own checkpoint although it is newer than the local one, which happens when
        /// this node crashed during a checkpoint that the replica had already completed and recovered an older one.
        /// </summary>
        private bool CanReuseNewerReplicaCheckpoint(CheckpointEntry localEntry, out string reason)
        {
            var replicaMetadata = replicaCheckpointEntry.metadata;
            var replicaCovered = replicaCheckpointEntry.GetMinAofCoveredAddress();
            reason = "version-mismatch";
            if (replicaMetadata.storeHlogToken == default || replicaMetadata.storeVersion <= localEntry.metadata.storeVersion)
                return false;

            // Signed under an earlier history, the replica checkpoint may contain AOF that diverged at a failover
            reason = "replica-checkpoint-newer-other-history";
            if (!replicaMetadata.storePrimaryReplId.Equals(clusterProvider.replicationManager.PrimaryReplId))
                return false;

            reason = "replica-checkpoint-newer-not-covering-local";
            if (replicaCovered.AnyLesser(localEntry.GetMinAofCoveredAddress()))
                return false;

            // AOF past the stable tail may have been lost by this node's crash, so a checkpoint covering it can diverge
            reason = "replica-checkpoint-newer-beyond-stable-aof";
            if (replicaCovered.AnyGreater(GetStableAofTailAddress()))
                return false;

            reason = "replica-checkpoint-newer-aof-missing";
            if (replicaAofTailAddress.AnyLesser(replicaCovered))
                return false;

            // Records written after the sync must carry a version the recovered replica does not skip as already checkpointed
            reason = "replica-checkpoint-newer-version-ahead";
            if (storeWrapper.store.CurrentVersion < replicaMetadata.storeVersion)
                return false;

            reason = "replica-checkpoint-newer";
            return true;
        }

        /// <summary>
        /// Start sending the latest checkpoint to replica
        /// </summary>
        public async Task<bool> SendCheckpointAsync()
        {
            errorMsg = default;
            var current = clusterProvider.clusterManager.CurrentConfig;
            var (address, port) = current.GetWorkerAddressFromNodeId(replicaNodeId);

            if (address == null || port == -1)
            {
                errorMsg = $"PRIMARY-ERR don't know about replicaId: {replicaNodeId}";
                logger?.LogError("{errorMsg}", errorMsg);
                return false;
            }

            GarnetClientSession gcs = new(
                new IPEndPoint(IPAddress.Parse(address), port),
                clusterProvider.replicationManager.GetRSSNetworkBufferSettings,
                clusterProvider.replicationManager.GetNetworkPool,
                tlsOptions: clusterProvider.serverOptions.TlsOptions?.TlsClientOptions,
                authUsername: clusterProvider.ClusterUsername,
                authPassword: clusterProvider.ClusterPassword,
                clientName: nameof(ReplicaSyncSession.SendCheckpointAsync),
                logger: logger);
            CheckpointEntry localEntry = default;
            AofSyncDriver aofSyncDriver = null;
            var totalWatch = Stopwatch.StartNew();
            var phaseWatch = Stopwatch.StartNew();
            long acquireMs = 0, connectMs = 0, validateMs = 0, transferMs = 0, recoverMs = 0;

            try
            {
                logger?.LogInformation("Replica replicaId:{replicaId} requesting checkpoint replicaStoreVersion:{replicaStoreVersion}",
                    replicaNodeId, replicaCheckpointEntry.metadata.storeVersion);

                logger?.LogInformation("Attempting to acquire checkpoint");

                (localEntry, aofSyncDriver) = await AcquireCheckpointEntryAsync().ConfigureAwait(false);
                logger?.LogInformation("Checkpoint search completed");
                acquireMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                await gcs.ConnectAsync((int)storeWrapper.serverOptions.ReplicaSyncTimeout.TotalMilliseconds, cts.Token).ConfigureAwait(false);
                connectMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                var index_size = -1L;
                var hlog_size = default(LogFileInfo);
                var skipLocalMainStoreCheckpoint = false;
                var reuseNewerReplicaCheckpoint = false;
                var retryCount = validateMetadataMaxRetryCount;
                while (!ValidateMetadata(localEntry, out index_size, out hlog_size, out skipLocalMainStoreCheckpoint, out reuseNewerReplicaCheckpoint))
                {
                    logger?.LogError("Failed to validate metadata. Retrying....");
                    await Task.Yield();
                    if (retryCount-- <= 0)
                        throw new GarnetException("Failed to validate metadata!");
                }

                validateMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                #region sendStoresSnapshotData
                if (!skipLocalMainStoreCheckpoint)
                {
                    logger?.LogInformation("Sending main store checkpoint {version} {storeHlogToken} {storeIndexToken} to replica", localEntry.metadata.storeVersion, localEntry.metadata.storeHlogToken, localEntry.metadata.storeIndexToken);

                    using var checkpointTransmissionDriver = new SnapshotTransmissionDriver(gcs, storeWrapper.serverOptions.ReplicaSyncTimeout, logger);

                    // RangeIndex files go before the Tsavorite checkpoint, whose metadata commits the checkpoint at the replica;
                    // otherwise an interrupted transfer leaves a committed checkpoint that later attaches reuse without these files.
                    if (storeWrapper.serverOptions.EnableRangeIndexPreview)
                    {
                        checkpointTransmissionDriver.AddReader(new RangeIndexSnapshotReader(
                            clusterProvider.rangeIndexManager,
                            localEntry.metadata.storeHlogToken,
                            hlog_size.hybridLogFileStartAddress,
                            hlog_size.hybridLogFileEndAddress,
                            storeWrapper.serverOptions.ReplicaSyncCompressRangeIndex,
                            logger));
                    }

                    checkpointTransmissionDriver.AddReader(new TsavoriteSnapshotReader(clusterProvider, localEntry, hlog_size, index_size, storeWrapper.serverOptions.ReplicaSyncTimeout, logger));
                    await checkpointTransmissionDriver.SendCheckpointAsync(cts.Token).ConfigureAwait(false);
                }
                #endregion

                transferMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                #region startAofSync
                var recoverFromRemote = !skipLocalMainStoreCheckpoint;
                // A reused newer replica checkpoint already holds the AOF between the local checkpoint and its own;
                // streaming from the local checkpoint would resend that AOF for the replica to skip.
                var checkpointAofBeginAddress = reuseNewerReplicaCheckpoint ? replicaCheckpointEntry.GetMinAofCoveredAddress() : localEntry.GetMinAofCoveredAddress();
                var beginAddress = checkpointAofBeginAddress;
                var sameHistory2 = !string.IsNullOrEmpty(clusterProvider.replicationManager.PrimaryReplId2) && clusterProvider.replicationManager.PrimaryReplId2.Equals(replicaAssignedPrimaryId);

                // Calculate replay AOF range
                var sameMainStoreCheckpointHistory = !string.IsNullOrEmpty(replicaCheckpointEntry.metadata.storePrimaryReplId) && replicaCheckpointEntry.metadata.storePrimaryReplId.Equals(localEntry.metadata.storePrimaryReplId);
                var replayAOFMap = clusterProvider.storeWrapper.appendOnlyFile.ComputeAofSyncReplayAddress(
                    recoverFromRemote,
                    sameMainStoreCheckpointHistory,
                    sameHistory2,
                    clusterProvider.replicationManager.ReplicationOffset2,
                    replicaAofBeginAddress,
                    replicaAofTailAddress,
                    beginAddress,
                    reuseNewerReplicaCheckpoint ? GetStableAofTailAddress() : storeWrapper.appendOnlyFile.Log.CommittedUntilAddress,
                    ref checkpointAofBeginAddress);

                // Signal replica to recover from local/remote checkpoint
                // Make replica replayAOF if needed and replay from provided beginAddress to RecoveredReplication Address
                // Recovery time scales with checkpoint size, so it is bounded by the attach timeout rather than the per-message sync timeout.
                var resp = await gcs.ExecuteClusterBeginReplicaRecover(
                    !skipLocalMainStoreCheckpoint,
                    replayAOFMap,
                    clusterProvider.replicationManager.PrimaryReplId,
                    localEntry.ToByteArray(),
                    beginAddress.Span,
                    checkpointAofBeginAddress.Span).WaitAsync(storeWrapper.runtimeConfig.GetTimeSpan(ServerConfigType.REPL_ATTACH_TIMEOUT), cts.Token).ConfigureAwait(false);
                var syncFromAofAddress = AofAddress.FromString(resp);
                recoverMs = phaseWatch.ElapsedMilliseconds;
                phaseWatch.Restart();

                // Assert that AOF address the replica will be requesting can be served, except in case of:
                // Possible AOF data loss: { using null AOF device } OR { main memory replication AND no on-demand checkpoints }
                var possibleAofDataLoss = clusterProvider.serverOptions.UseAofNullDevice ||
                    (clusterProvider.serverOptions.FastAofTruncate && !clusterProvider.serverOptions.OnDemandCheckpoint);
                clusterProvider.storeWrapper.appendOnlyFile.DataLossCheck(possibleAofDataLoss, syncFromAofAddress, logger);

                // Check what happens if we fail after recovery and start AOF stream
                ExceptionInjectionHelper.TriggerException(ExceptionInjectionType.Replication_Fail_Before_Background_AOF_Stream_Task_Start);

                // We have already added the iterator for the covered address above but replica might request an address
                // that is ahead of the covered address so we should start streaming from that address in order not to
                // introduce duplicate insertions.
                if (!clusterProvider.replicationManager.AofSyncDriverStore.TryAddReplicationDriver(replicaNodeId, ref syncFromAofAddress, out aofSyncDriver))
                    throw new GarnetException("Failed trying to try update replication task");
                if (!clusterProvider.replicationManager.TryConnectToReplica(replicaNodeId, ref syncFromAofAddress, aofSyncDriver, out _))
                    throw new GarnetException("Failed connecting to replica for aofSync");

                var primaryTail = clusterProvider.storeWrapper.appendOnlyFile.Log.TailAddress;
                logger?.LogInformation("[ResyncTiming] side=primary replica={replicaId} fullCheckpoint={fullCheckpoint} replayAofMap={replayAofMap} acquireMs={acquireMs} connectMs={connectMs} validateMs={validateMs} transferMs={transferMs} replicaRecoverMs={recoverMs} aofStartMs={aofStartMs} totalMs={totalMs} syncFrom={syncFrom} primaryTail={primaryTail} aofLagBytes={aofLagBytes}",
                    replicaNodeId, !skipLocalMainStoreCheckpoint, replayAOFMap, acquireMs, connectMs, validateMs, transferMs, recoverMs, phaseWatch.ElapsedMilliseconds, totalWatch.ElapsedMilliseconds, syncFromAofAddress, primaryTail, primaryTail[0] - syncFromAofAddress[0]);
                #endregion
            }
            catch (Exception ex)
            {
                if (localEntry != null)
                    logger?.LogCheckpointEntry(LogLevel.Error, "Error at attaching", localEntry);
                else
                    logger?.LogError("Error at attaching: {ex}", ex.Message);

                if (aofSyncDriver != null)
                    _ = clusterProvider.replicationManager.AofSyncDriverStore.TryRemove(aofSyncDriver);
                errorMsg = ex.Message;// this is error sent to remote client
                return false;
            }
            finally
            {
                // At this point the replica has received the most recent checkpoint data
                // and recovered from it so primary can release and delete it safely
                localEntry?.RemoveReader();
                gcs.Dispose();
            }
            return true;
        }

        private async Task<(CheckpointEntry, AofSyncDriver)> AcquireCheckpointEntryAsync()
        {
            AofSyncDriver aofSyncDriver;
            CheckpointEntry cEntry;

            // This loop tries to provide the following two guarantees
            // 1. Retrieve latest checkpoint and lock it to prevent deletion before it is send to the replica
            // 2. Guard against truncation of AOF in between the retrieval of the checkpoint metadata and start of the aofSyncTask
            var iteration = 0;
            var numOdcAttempts = 0;
            const int maxOdcAttempts = 2;

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                logger?.LogInformation("AcquireCheckpointEntry iteration {iteration}", iteration);
                iteration++;

                aofSyncDriver = null;
                cEntry = default;

                // Acquire startSaveTime to identify if an external task might have taken the checkpoint for us
                // This is only useful for MainMemoryReplication where we might have multiple replicas attaching
                // We want to share the on-demand checkpoint and ensure that only one replica should succeed when calling TakeOnDemandCheckpoint
                var lastSaveTime = storeWrapper.lastSaveTime;

                var exceptionInjected = ExceptionInjectionHelper.TriggerCondition(ExceptionInjectionType.Replication_Acquire_Checkpoint_Entry_Fail_Condition);

                // Retrieve latest checkpoint and lock it from deletion operations
                var addedReader = !exceptionInjected && clusterProvider.replicationManager.TryGetLatestCheckpointEntryFromMemory(out cEntry);

                if (!addedReader)
                {
                    // Fail to acquire lock, could mean that a writer might be trying to delete
                    logger?.LogWarning("Could not acquire lock for existing checkpoint, retrying.");

                    // Go back to re-acquire the latest checkpoint
                    await Task.Yield();
                    continue;
                }

#if DEBUG
                // Only on Debug mode
                await ExceptionInjectionHelper.ResetAndWaitAsync(ExceptionInjectionType.Replication_Wait_After_Checkpoint_Acquisition).ConfigureAwait(false);
#endif

                // Calculate the minimum start address covered by this checkpoint
                var startAofAddress = cEntry.GetMinAofCoveredAddress();

                // If there is possible AOF data loss and we need to take an on-demand checkpoint,
                // then we should take the checkpoint before we register the sync task, because
                // TryAddReplicationTask is guaranteed to return true in this scenario.
                var validMetadata = ValidateMetadata(cEntry, out _, out _, out _, out _);
                if (clusterProvider.serverOptions.OnDemandCheckpoint &&
                    (startAofAddress.AnyLesser(clusterProvider.replicationManager.AofSyncDriverStore.TruncatedUntil) || !validMetadata))
                {
                    if (numOdcAttempts >= maxOdcAttempts && clusterProvider.AllowDataLoss)
                    {
                        logger?.LogWarning("Failed to acquire checkpoint after {numOdcAttempts} on-demand checkpoint attempts. Possible data loss, startAofAddress:{startAofAddress} < truncatedUntil:{truncatedUntil}.", numOdcAttempts, startAofAddress, clusterProvider.replicationManager.AofSyncDriverStore.TruncatedUntil);
                    }
                    else
                    {
                        cEntry.RemoveReader();
                        numOdcAttempts++;
                        logger?.LogInformation("Taking on-demand checkpoint, attempt {numOdcAttempts}.", numOdcAttempts);
                        await storeWrapper.TakeOnDemandCheckpointAsync(lastSaveTime).ConfigureAwait(false);
                        await Task.Yield();
                        continue;
                    }
                }

                // Validate that AofSyncDriver has been terminated
                clusterProvider.replicationManager.AofSyncDriverStore.AssertDoesNotExist(replicaNodeId);

                // Enqueue AOF sync task with startAofAddress to prevent future AOF truncations
                // and check if truncation has happened in between retrieving the latest checkpoint and enqueuing the aofSyncTask
                if (clusterProvider.replicationManager.AofSyncDriverStore.TryAddReplicationDriver(replicaNodeId, ref startAofAddress, out aofSyncDriver))
                    break;

                // Unlock last checkpoint because associated startAofAddress is no longer available
                cEntry.RemoveReader();

                // Go back to re-acquire checkpoint
                await Task.Yield();
            }

            return (cEntry, aofSyncDriver);
        }
    }
}