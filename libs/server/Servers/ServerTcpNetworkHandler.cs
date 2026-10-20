// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Net.Sockets;
using Garnet.common;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    internal sealed class ServerTcpNetworkHandler : TcpNetworkHandler<GarnetServerTcp>
    {
        protected override bool RetainReceiveBuffers => Session is RespServerSession { clusterSession.IsBulkTransferSession: true };

        public ServerTcpNetworkHandler(GarnetServerTcp serverHook, Socket socket, NetworkBufferSettings networkBufferSettings, LimitedFixedBufferPool networkPool, bool useTLS, int networkSendThrottleMax = 8, ILogger logger = null)
            : base(serverHook, socket, networkBufferSettings, networkPool, useTLS, null, networkSendThrottleMax: networkSendThrottleMax, logger: logger)
        {
        }
    }
}