// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Garnet.common;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Measures replica AOF growth when a diskless full sync is interrupted after importing records.
    /// These tests write many GiB to disk, so they run only when selected explicitly.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ClusterDisklessSyncReplicaAofGrowthTests : TestBase
    {
        private const int PrimaryIndex = 0;
        private const int ReplicaIndex = 1;
        private const long OneGiB = 1L << 30;
        private const int OneMiB = 1 << 20;

        ClusterTestContext context;

        [SetUp]
        public void Setup()
        {
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: (int)TimeSpan.FromMinutes(60).TotalSeconds);
        }

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test, Explicit("Writes over 4 GiB of AOF data")]
        public void InterruptedDisklessFullSyncExceeds4GiBWithOneGiBAofSizeLimit()
            => VerifyReplicaAofGrowthDuringDisklessSync(targetGiB: 4, keyCount: 18432, valueSize: 256 << 10, aofSizeLimit: "1g");

        [Test, Explicit("Writes over 16 GiB of AOF data")]
        public void InterruptedDisklessFullSyncExceeds16GiBWithOneGiBAofSizeLimit()
            => VerifyReplicaAofGrowthDuringDisklessSync(targetGiB: 16, keyCount: 69632, valueSize: 256 << 10, aofSizeLimit: "1g");

        private void VerifyReplicaAofGrowthDuringDisklessSync(int targetGiB, int keyCount, int valueSize, string aofSizeLimit)
        {
            var targetBytes = targetGiB * OneGiB;

            context.CreateInstances(2, enableAOF: true, enableDisklessSync: true, replicaDisklessSyncFullSyncAofThreshold: "1k",
                replicaSyncTimeout: 3600, timeout: 600, aofSizeLimit: aofSizeLimit);
            context.MeetAndAssignSlotsAllNodes(2);

            // Keep the primary's size-limit checkpoint task from competing with bulk data preparation.
            ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.GetServer(PrimaryIndex).Execute("CONFIG", "SET", "aof-size-limit-enforce-frequency", "3600"));

            var database = context.clusterTestUtils.GetDatabase();
            var value = new byte[valueSize];
            Random.Shared.NextBytes(value);
            for (var i = 0; i < keyCount; i++)
                ClassicAssert.IsTrue(database.StringSet($"{{aof}}{i}", value));

            var log = GetStoreWrapper(context.nodes[ReplicaIndex]).appendOnlyFile.Log;
            var aofDirectory = context.nodeOptions[ReplicaIndex].GetAppendOnlyFileDirectory(0);
            var primaryAddress = GetStoreWrapper(context.nodes[PrimaryIndex]).appendOnlyFile.Log.TailAddress[0];

            context.clusterTestUtils.ClusterReplicate(ReplicaIndex, PrimaryIndex, async: true, logger: context.logger);

            var stopwatch = Stopwatch.StartNew();
            while (log.TailAddress[0] - log.BeginAddress[0] < targetBytes)
            {
                ClassicAssert.Less(stopwatch.Elapsed, TimeSpan.FromMinutes(45), $"diskless import did not write {targetGiB} GiB to the replica AOF");
                Thread.Sleep(10);
            }

            using (ExceptionInjectionHelper.EnabledScope(ExceptionInjectionType.Replication_Diskless_Sync_After_Record_Import))
            {
                Thread.Sleep(500);
                log.CommitAsync().GetAwaiter().GetResult();

                var committedBytes = log.CommittedUntilAddress[0] - log.BeginAddress[0];
                var segmentSizes = Directory.GetFiles(aofDirectory, "aof.log.*").Select(path => new FileInfo(path).Length).ToArray();
                var fileBytes = segmentSizes.Sum();
                TestContext.Progress.WriteLine($"Interrupted diskless sync: replica AOF committed {committedBytes / OneMiB:N0} MiB across {segmentSizes.Length} segments ({fileBytes / OneMiB:N0} MiB of files) with aof-size-limit {aofSizeLimit}.");

                ClassicAssert.GreaterOrEqual(committedBytes, targetBytes, $"the replica must have committed at least {targetGiB} GiB of imported records before full sync completes");
                ClassicAssert.GreaterOrEqual(fileBytes, targetBytes, $"the replica's physical AOF segment files must span at least {targetGiB} GiB");
                ClassicAssert.Less(log.BeginAddress[0], primaryAddress, "the replica AOF should not have been reinitialized at the primary offset");
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "storeWrapper")]
        private static extern ref StoreWrapper GetStoreWrapper(GarnetServer server);
    }
}
#endif