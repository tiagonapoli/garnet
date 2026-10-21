// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// A synchronized replay operation (FLUSHALL, FLUSHDB, checkpoint, transaction barriers) is broadcast to every
    /// physical sublog and executed once, by the first sublog to reach it, after every sublog has arrived. Sublogs replay
    /// independently, so one sublog can reach the operation more than ReplicaSyncTimeout after another.
    /// </summary>
    [TestFixture]
    public class AofSynchronizedReplayTimeoutTests : TestBase
    {
        const string KeyWrittenAfterFlush = "written-after-flushall";

        GarnetServer server;

        [SetUp]
        public void Setup()
        {
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableAOF: true, aofPhysicalSublogCount: 2);
            server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            server?.Dispose();
            TestUtils.OnTearDown();
        }

        [Test]
        public void LateSublogDoesNotReexecuteFlushAllAfterBarrierTimeout()
        {
            var storeWrapper = server.Provider.StoreWrapper;

            // ReplicaSyncTimeout bounds how long the first sublog waits for the others; shorten it so the test does not
            // spend the default 5 seconds per wait. The sublog lag itself is produced by ordering, not by timing.
            storeWrapper.serverOptions.ReplicaSyncTimeout = TimeSpan.FromSeconds(1);

            var processor = new AofProcessor(storeWrapper, recordToAof: false);
            try
            {
                ReplayFlushAllWithLateSublog(processor);
            }
            finally
            {
                processor.Dispose();
            }
        }

        static void ReplayFlushAllWithLateSublog(AofProcessor processor)
        {
            var flushAll = CreateBroadcastFlushAllRecord(sequenceNumber: 1_000_000, participantCount: 2);

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Sublog 0 reaches FLUSHALL while sublog 1 is still more than ReplicaSyncTimeout behind.
            var sublog0Failed = TryReplay(processor, virtualSublogIdx: 0, flushAll, out var sublog0Error);

            // Sublog 0 continues replaying the writes that follow FLUSHALL on the primary.
            ClassicAssert.IsTrue(db.StringSet(KeyWrittenAfterFlush, "v"));

            // Sublog 1 finally reaches the same FLUSHALL record.
            var sublog1Failed = TryReplay(processor, virtualSublogIdx: 1, flushAll, out var sublog1Error);

            if (sublog0Failed || sublog1Failed)
            {
                // Surfacing the timeout as a replay failure is acceptable: the replica re-synchronizes instead of diverging.
                ClassicAssert.IsTrue(db.KeyExists(KeyWrittenAfterFlush), $"FLUSHALL wiped a later write although replay failed: {sublog0Error ?? sublog1Error}");
                return;
            }

            ClassicAssert.IsTrue(db.KeyExists(KeyWrittenAfterFlush),
                "Both sublogs completed FLUSHALL replay without an error, but the late sublog executed FLUSHALL a second time and wiped a write the primary performed after FLUSHALL");
        }

        static bool TryReplay(AofProcessor processor, int virtualSublogIdx, byte[] record, out Exception error)
        {
            error = null;
            try
            {
                unsafe
                {
                    fixed (byte* ptr = record)
                        processor.ProcessAofRecordInternal(virtualSublogIdx, ptr, record.Length, asReplica: true, out _);
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex;
                return true;
            }
        }

        /// <summary>Encodes FLUSHALL as GarnetLog.EnqueueBroadcastEntry writes it to every physical sublog.</summary>
        static unsafe byte[] CreateBroadcastFlushAllRecord(long sequenceNumber, short participantCount)
        {
            var basicHeader = new AofHeader { opType = AofEntryType.FlushAll, storeVersion = 0, sessionID = -1, databaseId = 0 };
            basicHeader.HeaderType = AofHeaderType.ShardedLogTransactionHeader;
            var header = new AofShardedLogTransactionHeader
            {
                shardedHeader = new AofShardedHeader { basicHeader = basicHeader, sequenceNumber = sequenceNumber },
                participantCount = participantCount
            };
            new Span<byte>(header.replayTaskAccessVector, AofShardedLogTransactionHeader.ReplayTaskAccessVectorBytes).Fill(0xFF);

            var record = new byte[AofShardedLogTransactionHeader.TotalSize];
            fixed (byte* ptr = record)
                *(AofShardedLogTransactionHeader*)ptr = header;
            return record;
        }
    }
}