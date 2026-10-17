// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Garnet.cluster;
using Garnet.common;
using Garnet.networking;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test.cluster
{
    [TestFixture]
    public class ClusterBulkTransferBufferTests : TestBase
    {
        GarnetServer server;
        ClusterProvider provider;
        ReceiveCheckpointHandler checkpointReceiver;

        [TearDown]
        public void TearDown()
        {
            if (checkpointReceiver != null)
            {
                provider.replicationManager.recvCheckpointHandler = null;
                checkpointReceiver.Dispose();
                checkpointReceiver = null;
            }

            server?.Dispose();
            server = null;
            TestUtils.OnTearDown();
        }

        [TestCase("SYNC", true)]
        [TestCase("SNAPSHOT_DATA", true)]
        [TestCase("MIGRATE", true)]
        [TestCase("SLOTS", false)]
        [TestCase("SET", false)]
        public async Task OnlyInternalBulkCommandsMarkTheConnection(string command, bool expectedBulkTransfer)
        {
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            var endpoint = new IPEndPoint(IPAddress.Loopback, ClusterTestContext.Port);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, endpoints: [endpoint], enableCluster: true, enableAOF: true, enableTLS: true);
            provider = (ClusterProvider)server.Provider.StoreWrapper.clusterProvider;
            if (command == "SNAPSHOT_DATA")
            {
                checkpointReceiver = new ReceiveCheckpointHandler(provider);
                provider.replicationManager.recvCheckpointHandler = checkpointReceiver;
            }

            server.Start();
            using var connection = await ConnectionMultiplexer.ConnectAsync(TestUtils.GetConfig(endpoints: [endpoint], allowAdmin: true, useTLS: true));
            var database = connection.GetDatabase();
            var id = (long)await database.ExecuteAsync("CLIENT", "ID");
            var session = server.Provider.StoreWrapper.Servers.Cast<GarnetServerBase>().SelectMany(listener => listener.ActiveConsumers()).OfType<RespServerSession>().Single(candidate => candidate.Id == id);
            ClassicAssert.IsFalse(session.clusterSession.IsBulkTransferSession);

            if (command == "SET")
            {
                _ = await database.ExecuteAsync("CLUSTER", "ADDSLOTSRANGE", "0", "16383");
                var value = new string('x', 2 * 1024 * 1024);
                ClassicAssert.IsTrue(await database.StringSetAsync("buffer-policy", value));
                ClassicAssert.AreEqual(value, (string)await database.StringGetAsync("buffer-policy"));
            }
            else
            {
                object[] arguments = command switch
                {
                    "SYNC" => [command, new string('0', 40), new byte[sizeof(int)]],
                    "MIGRATE" => [command, new string('0', 40), "F", "F", new byte[sizeof(int)]],
                    "SNAPSHOT_DATA" => [command, Guid.NewGuid().ToByteArray(), "1", "0", Array.Empty<byte>()],
                    "SLOTS" => [command],
                    _ => throw new ArgumentOutOfRangeException(nameof(command))
                };
                var result = await database.ExecuteAsync("CLUSTER", arguments);
                if (expectedBulkTransfer)
                    ClassicAssert.AreEqual("OK", result.ToString());
            }

            ClassicAssert.AreEqual(expectedBulkTransfer, session.clusterSession.IsBulkTransferSession);
            var sender = typeof(ServerSessionBase).GetField("networkSender", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
            var retention = typeof(NetworkHandler<GarnetServerTcp, GarnetTcpNetworkSender>).GetProperty("RetainReceiveBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
            ClassicAssert.AreEqual(expectedBulkTransfer, retention.GetValue(sender), "the actual server handler must select the matching receive policy");
        }
    }
}