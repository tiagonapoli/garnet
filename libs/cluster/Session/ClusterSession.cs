// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.networking;
using Garnet.server;
using Garnet.server.ACL;
using Garnet.server.Auth;
using Microsoft.Extensions.Logging;

namespace Garnet.cluster
{
    internal sealed partial class ClusterSession : IClusterSession
    {
        readonly ClusterProvider clusterProvider;
        readonly TransactionManager txnManager;
        readonly GarnetSessionMetrics sessionMetrics;
        BasicGarnetApi basicGarnetApi;
        readonly INetworkSender networkSender;
        readonly ILogger logger;
        ClusterSlotVerificationInput csvi;

        // Authenticator used to validate permissions for cluster commands
        readonly IGarnetAuthenticator authenticator;

        // User currently authenticated in this session
        UserHandle userHandle;

        SessionParseState parseState;
        unsafe byte* dcurr, dend;
        long _localCurrentEpoch = 0;

        /// <summary>
        /// The Garnet epoch this session announced when it entered its current batch, or 0 if it is between batches.
        /// Read by <see cref="ClusterProvider.BumpAndWaitForEpochTransitionAsync"/> from another thread, in a spin
        /// loop, so it must not be cached across iterations.
        /// </summary>
        public long LocalCurrentEpoch => Volatile.Read(ref _localCurrentEpoch);

        /// <summary>
        /// Indicates if this is a session that allows for reads and writes
        /// </summary>
        bool readWriteSession = false;

        public bool ReadWriteSession => clusterProvider.clusterManager.CurrentConfig.IsPrimary || readWriteSession;

        public void SetReadOnlySession() => readWriteSession = false;
        public void SetReadWriteSession() => readWriteSession = true;

        /// <inheritdoc/>
        public bool IsReplicating { get; private set; }

        /// <inheritdoc/>
        public IGarnetServer Server { get; set; }

        private StringBasicContext stringBasicContext;
        private VectorBasicContext vectorBasicContext;

        /// <summary>
        /// Streaming state for receiving inbound RangeIndex migration data.
        /// Unlike regular keys or VectorSet keys (which are each sent as a single record),
        /// a RangeIndex BfTree snapshot can be large and is transmitted as a sequence of
        /// chunked <c>SerializedRangeIndexStream</c> records
        /// spread across multiple <c>CLUSTER MIGRATE</c> commands on this connection.
        /// The sender serializes all chunks for one key sequentially before moving to the next,
        /// so per-connection state is sufficient to reassemble them.
        /// </summary>
        private readonly RangeIndexMigrationReceiveState rangeIndexMigrationState;

        public ClusterSession(ClusterProvider clusterProvider, TransactionManager txnManager, IGarnetAuthenticator authenticator, UserHandle userHandle, GarnetSessionMetrics sessionMetrics, BasicGarnetApi basicGarnetApi, StringBasicContext stringBasicContext, VectorBasicContext vectorBasicContext, INetworkSender networkSender, ILogger logger = null)
        {
            this.clusterProvider = clusterProvider;
            this.authenticator = authenticator;
            this.userHandle = userHandle;
            this.txnManager = txnManager;
            this.sessionMetrics = sessionMetrics;
            this.basicGarnetApi = basicGarnetApi;
            this.stringBasicContext = stringBasicContext;
            this.vectorBasicContext = vectorBasicContext;
            this.networkSender = networkSender;
            this.logger = logger;

            if (clusterProvider.serverOptions.EnableRangeIndexPreview)
                rangeIndexMigrationState = new RangeIndexMigrationReceiveState(clusterProvider.storeWrapper.DefaultDatabase.RangeIndexManager, clusterProvider.storeWrapper.appendOnlyFile, logger);
        }

        public unsafe void ProcessClusterCommands(RespCommand command, VectorManager vectorManager, ref SessionParseState parseState, ref byte* dcurr, ref byte* dend)
        {
            this.dcurr = dcurr;
            this.dend = dend;
            this.parseState = parseState;
            var invalidParameters = false;

            try
            {
                if (command.IsClusterSubCommand())
                {
                    if (RespCommandsInfo.TryGetSimpleRespCommandInfo(command, out var cmdInfo) && cmdInfo.KeySpecs?.Length > 0)
                    {
                        csvi.keySpecs = cmdInfo.KeySpecs;
                        csvi.isSubCommand = cmdInfo.IsSubCommand;
                        csvi.readOnly = command.IsReadOnly();
                        if (NetworkMultiKeySlotVerifyNoResponse(ref parseState, ref csvi, ref this.dcurr, ref this.dend))
                            return;
                    }

                    ProcessClusterCommands(command, vectorManager, out invalidParameters);
                }
                else
                {
                    _ = command switch
                    {
                        RespCommand.MIGRATE => NetworkTryMIGRATE(out invalidParameters),
                        RespCommand.FAILOVER => TryFAILOVER(),
                        RespCommand.SECONDARYOF or RespCommand.REPLICAOF => NetworkTryREPLICAOF(out invalidParameters),
                        _ => false
                    };
                }

                if (invalidParameters)
                {
                    var cmdName = RespCommandsInfo.GetRespCommandName(command);
                    var errorMessage = string.Format(CmdStrings.GenericErrWrongNumArgs, cmdName.ToLowerInvariant());
                    while (!RespWriteUtils.TryWriteError(errorMessage, ref this.dcurr, this.dend))
                        SendAndReset();
                }
            }
            finally
            {
                dcurr = this.dcurr;
                dend = this.dend;
            }
        }

        unsafe void SendAndReset()
        {
            byte* d = networkSender.GetResponseObjectHead();
            if ((int)(dcurr - d) > 0)
            {
                Send(d);
                networkSender.GetResponseObject();
                dcurr = networkSender.GetResponseObjectHead();
                dend = networkSender.GetResponseObjectTail();
            }
        }

        unsafe void SendAndReset(ref byte* dcurr, ref byte* dend)
        {
            byte* d = networkSender.GetResponseObjectHead();
            if ((int)(dcurr - d) > 0)
            {
                Send(d);
                networkSender.GetResponseObject();
                dcurr = networkSender.GetResponseObjectHead();
                dend = networkSender.GetResponseObjectTail();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void Send(byte* d)
        {
            // #if DEBUG
            // logger?.LogTrace("SEND: [{send}]", Encoding.UTF8.GetString(new Span<byte>(d, (int)(dcurr - d))).Replace("\n", "|").Replace("\r", ""));
            // #endif

            if ((int)(dcurr - d) > 0)
            {
                // Debug.WriteLine("SEND: [" + Encoding.UTF8.GetString(new Span<byte>(d, (int)(dcurr - d))).Replace("\n", "|").Replace("\r", "!") + "]");
                if (clusterProvider.storeWrapper.appendOnlyFile != null && clusterProvider.storeWrapper.serverOptions.WaitForCommit)
                {
                    var task = clusterProvider.storeWrapper.appendOnlyFile.Log.WaitForCommitAsync();
                    if (!task.IsCompletedSuccessfully) AsyncUtils.BlockingWait(task);
                }
                int sendBytes = (int)(dcurr - d);
                networkSender.SendResponse((int)(d - networkSender.GetResponseObjectHead()), sendBytes);
                sessionMetrics?.incr_total_net_output_bytes((ulong)sendBytes);
            }
        }

        /// <summary>
        /// Updates the user currently authenticated in the session.
        /// </summary>
        /// <param name="userHandle"><see cref="UserHandle"/> to set as authenticated user.</param>
        public void SetUserHandle(UserHandle userHandle)
        {
            this.userHandle = userHandle;
        }

        /// <summary>
        /// Announce this session as active at the current Garnet epoch.
        /// </summary>
        /// <remarks>
        /// The announce is a locked RMW rather than a plain store because it forms one half of a store-buffer
        /// (SB) pattern with <see cref="ClusterProvider.BumpAndWaitForEpochTransitionAsync"/>:
        /// <code>
        ///   session : STORE _localCurrentEpoch ; LOAD  currentConfig
        ///   changer : STORE currentConfig      ; LOAD  _localCurrentEpoch
        /// </code>
        /// x86-TSO forbids both sides reading the stale value only when both fence between their store and their
        /// load. The changer already fences (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> when
        /// publishing the config, <see cref="Interlocked.Increment(ref long)"/> when bumping). With a plain store
        /// here the announce could sit in this core's store buffer while the scan reads the slot as 0, takes this
        /// session for idle, and reports the config transition complete — after which the session would go on to
        /// serve commands from the pre-transition <see cref="ClusterConfig"/> snapshot it already loaded.
        ///
        /// On the batch path that hazard was masked rather than absent: <c>TryConsumeMessages</c> calls
        /// <c>networkSender.EnterAndGetResponseObject</c> immediately after announcing, and that takes a
        /// <see cref="System.Threading.SpinLock"/>, whose <see cref="Interlocked"/> acquire drained the announce
        /// before any config read. Fencing here makes the ordering a property of the epoch protocol instead of a
        /// side effect of how an unrelated component happens to lock.
        ///
        /// Reading <see cref="ClusterProvider.GarnetCurrentEpoch"/> before the exchange is safe: a stale (lower)
        /// epoch only makes the scan wait for this session for longer.
        /// </remarks>
        public void AcquireCurrentEpoch() => Interlocked.Exchange(ref _localCurrentEpoch, clusterProvider.GarnetCurrentEpoch);

        /// <summary>
        /// Announce this session as idle. A release store, so the config reads this batch performed cannot sink
        /// past it and be attributed to a session the scan has already cleared.
        /// </summary>
        public void ReleaseCurrentEpoch() => Volatile.Write(ref _localCurrentEpoch, 0);

        /// <summary>
        /// Release epoch, wait for config transition and re-acquire the epoch
        /// </summary>
        public async Task UnsafeBumpAndWaitForEpochTransitionAsync()
        {
            ReleaseCurrentEpoch();
            _ = await clusterProvider.BumpAndWaitForEpochTransitionAsync().ConfigureAwait(false);
            AcquireCurrentEpoch();
        }

        /// <summary>
        /// NOTE: Unsafe! DO NOT USE, other than benchmarking
        /// </summary>
        /// <param name="replicaOf"></param>
        public void UnsafeSetConfig(string replicaOf = null)
        {
            var config = clusterProvider.clusterManager.CurrentConfig;
            config = config.MakeReplicaOf(replicaOf);
            clusterProvider.clusterManager.UnsafeSetConfig(config);

            if (replicaOf != null)
                clusterProvider.replicationManager.ResetReplicaReplayDriverStore();
        }

        public void Dispose()
        {
            rangeIndexMigrationState?.Dispose();

            // Call dispose on ref of this session if this session is a replication task
            if (IsReplicating)
                replicaReplayDriverStore?.Dispose();
        }
    }
}