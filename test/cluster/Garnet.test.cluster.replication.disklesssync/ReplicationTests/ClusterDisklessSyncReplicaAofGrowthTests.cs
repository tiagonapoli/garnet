// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Garnet.client;
using Garnet.common;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Verifies the replica AOF written while importing records during a diskless full sync: its growth and what a replica restart recovers from it.
    /// The growth tests write many GiB to disk, so they run only when selected explicitly.
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

        [TestCase("string")]
        [TestCase("hash")]
        [TestCase("list")]
        [TestCase("set")]
        [TestCase("zset")]
        public void InterruptedDisklessFullSyncAofReplayLosesExpiration(string type)
        {
            context.CreateInstances(2, enableAOF: true, enableDisklessSync: true, replicaDisklessSyncFullSyncAofThreshold: "1k", timeout: 60);
            context.MeetAndAssignSlotsAllNodes(2);

            var key = $"{{obj}}{type}";
            var database = context.clusterTestUtils.GetDatabase();
            PopulateKey(database, key, type);
            ClassicAssert.IsTrue(database.KeyExpire(key, TimeSpan.FromHours(1)));
            ClassicAssert.IsNotNull(database.KeyTimeToLive(key));

            var log = GetStoreWrapper(context.nodes[ReplicaIndex]).appendOnlyFile.Log;
            var tailBeforeSync = log.TailAddress[0];
            var primaryId = context.clusterTestUtils.GetNodeIdFromNode(PrimaryIndex, context.logger);

            using (ExceptionInjectionHelper.EnabledScope(ExceptionInjectionType.Replication_Diskless_Sync_After_Record_Import))
            {
                context.clusterTestUtils.ClusterReplicate(ReplicaIndex, PrimaryIndex, async: true, logger: context.logger);

                var stopwatch = Stopwatch.StartNew();
                while (log.TailAddress[0] == tailBeforeSync)
                {
                    ClassicAssert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(30), "the replica did not import the record");
                    Thread.Sleep(10);
                }

                log.CommitAsync().GetAwaiter().GetResult();
                TestContext.Progress.WriteLine($"Interrupted diskless sync: replica AOF [{log.BeginAddress[0]}, {log.TailAddress[0]}) after importing {key}.");

                // Stop the primary so the restarted replica can only use its own AOF.
                context.nodes[PrimaryIndex].Dispose(false);
                context.nodes[PrimaryIndex] = null;
                context.nodes[ReplicaIndex].Dispose(false);
            }

            using var session = RestartReplicaWithoutPrimary();

            // The failed sync left the replica as a primary without slots; let it serve the replayed keys.
            ClassicAssert.AreEqual("OK", session.ExecuteAsync("CLUSTER", "FORGET", primaryId).GetAwaiter().GetResult());
            ClassicAssert.AreEqual("OK", session.ExecuteAsync("CLUSTER", "ADDSLOTSRANGE", "0", "16383").GetAwaiter().GetResult());

            var recoveredType = session.ExecuteAsync("TYPE", key).GetAwaiter().GetResult();
            var recoveredTtl = session.ExecuteAsync("TTL", key).GetAwaiter().GetResult();
            TestContext.Progress.WriteLine($"Replica AOF replay recovered {key}: TYPE={recoveredType} TTL={recoveredTtl}");

            ClassicAssert.AreEqual(type, recoveredType, "AOF replay should recover the imported value");
            ClassicAssert.AreEqual("-1", recoveredTtl, "the imported record's AOF entry has no expiration, so replay recovers the key without its TTL");
        }

        [Test]
        public void CompletedDisklessFullSyncAofDoesNotRecoverImportedRecords()
        {
            const int keyCount = 100;

            context.CreateInstances(2, enableAOF: true, enableDisklessSync: true, replicaDisklessSyncFullSyncAofThreshold: "1k", timeout: 60);
            context.MeetAndAssignSlotsAllNodes(2);

            var database = context.clusterTestUtils.GetDatabase();
            for (var i = 0; i < keyCount; i++)
                PopulateKey(database, $"{{obj}}{i}", (i % 5) switch { 0 => "string", 1 => "hash", 2 => "list", 3 => "set", _ => "zset" });

            var log = GetStoreWrapper(context.nodes[ReplicaIndex]).appendOnlyFile.Log;
            var primaryLog = GetStoreWrapper(context.nodes[PrimaryIndex]).appendOnlyFile.Log;

            context.clusterTestUtils.ClusterReplicate(ReplicaIndex, PrimaryIndex, async: false, logger: context.logger);
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);
            ClassicAssert.AreEqual(keyCount, (long)context.clusterTestUtils.Execute(context.clusterTestUtils.GetEndPoint(ReplicaIndex), "DBSIZE", []));

            log.CommitAsync().GetAwaiter().GetResult();
            TestContext.Progress.WriteLine($"Completed diskless sync: replica AOF [{log.BeginAddress[0]}, {log.TailAddress[0]}), primary AOF [{primaryLog.BeginAddress[0]}, {primaryLog.TailAddress[0]}).");

            context.nodes[PrimaryIndex].Dispose(false);
            context.nodes[PrimaryIndex] = null;
            context.nodes[ReplicaIndex].Dispose(false);

            using var session = RestartReplicaWithoutPrimary();
            var recoveredKeys = session.ExecuteAsync("DBSIZE").GetAwaiter().GetResult();
            TestContext.Progress.WriteLine($"Replica restart without primary recovered {recoveredKeys} of {keyCount} keys.");

            ClassicAssert.AreEqual("0", recoveredKeys, "the replica takes no checkpoint and keeps no imported records in its AOF, so it recovers nothing locally");
        }

        private GarnetClientSession RestartReplicaWithoutPrimary()
        {
            context.nodes[ReplicaIndex] = context.CreateInstance(context.clusterTestUtils.GetEndPoint(ReplicaIndex), cleanClusterConfig: false, tryRecover: true, enableAOF: true, timeout: 60);
            context.nodes[ReplicaIndex].Start();

            var session = context.clusterTestUtils.CreateGarnetClientSession(ReplicaIndex);
            session.Connect();
            ClassicAssert.AreEqual("OK", session.ExecuteAsync("READONLY").GetAwaiter().GetResult());
            return session;
        }

        private static void PopulateKey(IDatabase database, string key, string type)
        {
            var added = type switch
            {
                "string" => database.StringSet(key, "value"),
                "hash" => database.HashSet(key, "field", "value"),
                "list" => database.ListRightPush(key, "value") == 1,
                "set" => database.SetAdd(key, "value"),
                "zset" => database.SortedSetAdd(key, "value", 1),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            ClassicAssert.IsTrue(added);
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "storeWrapper")]
        private static extern ref StoreWrapper GetStoreWrapper(GarnetServer server);
    }
}
#endif