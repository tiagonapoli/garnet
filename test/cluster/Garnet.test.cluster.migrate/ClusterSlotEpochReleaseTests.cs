// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

#if DEBUG
using System;
using System.Text;
using System.Threading.Tasks;
using Garnet.common;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    [TestFixture, NonParallelizable]
    public class ClusterSlotEpochReleaseTests : TestBase
    {
        ClusterTestContext context;

        [SetUp]
        public void Setup()
        {
            context = new ClusterTestContext();
            context.Setup([]);
        }

        [TearDown]
        public void TearDown() => context.TearDown();

        [TestCase("GET"), TestCase("SET"), CancelAfter(60_000)]
        public async Task SlotOwnershipChangeDuringReleasedEpochDoesNotServeOldOwner(string commandName)
        {
            const int sourceIndex = 1;
            const int targetIndex = 2;
            const string key = "{abc}a";
            const string value = "old-owner-value";
            const ExceptionInjectionType pause = ExceptionInjectionType.Cluster_Slot_Verification_Epoch_Released;

            context.CreateInstances(3);
            context.CreateConnection();
            _ = context.clusterTestUtils.SimpleSetupCluster(logger: context.logger);

            var slot = ClusterTestUtils.HashSlot(Encoding.ASCII.GetBytes(key));
            var sourceId = context.clusterTestUtils.GetNodeIdFromNode(sourceIndex, context.logger);
            var targetId = context.clusterTestUtils.GetNodeIdFromNode(targetIndex, context.logger);
            ClassicAssert.AreEqual(ResponseState.OK, context.clusterTestUtils.SetKey(sourceIndex,
                Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(value), out _, out _, logger: context.logger));
            ClassicAssert.AreEqual("OK", context.clusterTestUtils.SetSlot(targetIndex, slot, "IMPORTING", sourceId, logger: context.logger));
            ClassicAssert.AreEqual("OK", context.clusterTestUtils.SetSlot(sourceIndex, slot, "MIGRATING", targetId, logger: context.logger));

            var client = context.clusterTestUtils.GetGarnetClientSession(sourceIndex);
            ClassicAssert.AreEqual(value, await client.ExecuteAsync("GET", key));

            try
            {
                var command = commandName switch
                {
                    "GET" => new[] { "GET", key },
                    "SET" => new[] { "SET", key, value },
                    _ => throw new ArgumentOutOfRangeException(nameof(commandName))
                };

                ExceptionInjectionHelper.EnableException(pause);
                var pending = client.ExecuteAsync(command);
                await ExceptionInjectionHelper.WaitOnClearAsync(pause).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(pending.IsCompleted, Is.False);

                ClassicAssert.AreEqual("OK", context.clusterTestUtils.SetSlot(sourceIndex, slot, "NODE", targetId, logger: context.logger));
                ExceptionInjectionHelper.EnableException(pause);

                var error = Assert.ThrowsAsync<Exception>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.That(error.Message, Does.StartWith("TRYAGAIN"));
            }
            finally
            {
                ExceptionInjectionHelper.SuspendParking();
                ExceptionInjectionHelper.DisableException(pause);
                ExceptionInjectionHelper.ResumeParking();
            }
        }
    }
}
#endif
