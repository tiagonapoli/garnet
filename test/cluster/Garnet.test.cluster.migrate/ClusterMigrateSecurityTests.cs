// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Garnet.client;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Regression tests for the CLUSTER MIGRATE out-of-bounds read: a migrated inline log record whose
    /// declared value length exceeds the framed record must be rejected instead of copying adjacent heap
    /// memory into the store (which the attacker could then read back).
    /// </summary>
    [TestFixture(false), NonParallelizable]
    public class ClusterMigrateSecurityTests(bool UseTLS)
    {
        public enum MalformedPayload
        {
            TruncatedKeyCount,
            NegativeKeyCount,
            MissingRecordKind,
            MissingVectorSetKind,
            MissingRecordLength,
            MissingVectorSetRecordLength,
            MissingChunkLength,
            OversizedChunk
        }

        const int DeclaredValueLength = 1024;   // what the malformed header claims
        const int ProvidedValueLength = 8;      // what actually follows in the framed record

        ClusterTestContext context;
        readonly int defaultShards = 3;

        [SetUp]
        public void Setup()
        {
            context = new ClusterTestContext();
            context.Setup([]);
        }

        [TearDown]
        public void TearDown()
        {
            context?.TearDown();
        }

        [Test, Order(1)]
        [Category("CLUSTER")]
        public void ClusterMigrateRejectsMalformedInlineLogRecord()
        {
            context.CreateInstances(defaultShards, useTLS: UseTLS);
            context.CreateConnection(useTLS: UseTLS);
            _ = context.clusterTestUtils.SimpleSetupCluster(logger: context.logger);

            var sourceNodeIndex = 1;
            var targetNodeIndex = 2;
            var sourceNodeId = context.clusterTestUtils.GetNodeIdFromNode(sourceNodeIndex, context.logger);

            // The malformed record's key is "{abc}" (hash tag "abc"); it hashes into a slot owned by the source node.
            var keyBytes = Encoding.ASCII.GetBytes("{abc}");
            var slot = ClusterTestUtils.HashSlot(keyBytes);
            ClassicAssert.AreEqual(7638, slot);

            // Put the slot into IMPORTING state on the target so the receiver reaches the SET that would store the leak.
            var respImport = context.clusterTestUtils.SetSlot(targetNodeIndex, slot, "IMPORTING", sourceNodeId, logger: context.logger);
            ClassicAssert.AreEqual("OK", respImport);

            var migrateCommand = BuildMalformedMigrateCommand(sourceNodeId, keyBytes);

            // Send the crafted CLUSTER MIGRATE directly to the importing node; a malformed record must not receive an OK reply.
            var targetEndpoint = context.clusterTestUtils.GetEndPoint(targetNodeIndex);
            SendRaw(targetEndpoint, migrateCommand);

            // With the fix the malformed record is rejected before it can be stored, so an ASKING GET on the importing
            // node finds nothing. Without the fix the out-of-bounds read stores ~1 KB of adjacent heap under the key,
            // which this read-back returns (the declared value length far exceeds the bytes actually sent).
            var readBack = context.clusterTestUtils.GetKey(targetNodeIndex, keyBytes, out _, out _, out _, asking: true, logger: context.logger);
            ClassicAssert.IsNull(readBack, "malformed migrated record was stored (out-of-bounds read leaked into the store)");

            // The server rejected a single malformed command without crashing; it still serves other requests.
            var pong = context.clusterTestUtils.GetMultiplexer().GetServer(targetEndpoint).Execute("PING");
            ClassicAssert.AreEqual("PONG", pong.ToString());
        }

        [TestCase(RecordInfo.Size)]
        [TestCase(Constants.FixedHeaderSize)]
        [Category("CLUSTER")]
        public void ClusterMigrateRejectsNullLogRecord(int recordLength)
        {
            context.CreateInstances(defaultShards, useTLS: UseTLS);
            context.CreateConnection(useTLS: UseTLS);
            _ = context.clusterTestUtils.SimpleSetupCluster(logger: context.logger);

            var sourceNodeId = context.clusterTestUtils.GetNodeIdFromNode(1, context.logger);
            var targetEndpoint = context.clusterTestUtils.GetEndPoint(2);
            SendRaw(targetEndpoint, BuildMigrateCommand(sourceNodeId, new byte[recordLength]));

            var pong = context.clusterTestUtils.GetMultiplexer().GetServer(targetEndpoint).Execute("PING");
            ClassicAssert.AreEqual("PONG", pong.ToString());
        }

        [TestCase(MalformedPayload.TruncatedKeyCount)]
        [TestCase(MalformedPayload.NegativeKeyCount)]
        [TestCase(MalformedPayload.MissingRecordKind)]
        [TestCase(MalformedPayload.MissingVectorSetKind)]
        [TestCase(MalformedPayload.MissingRecordLength)]
        [TestCase(MalformedPayload.MissingVectorSetRecordLength)]
        [TestCase(MalformedPayload.MissingChunkLength)]
        [TestCase(MalformedPayload.OversizedChunk)]
        [Category("CLUSTER")]
        public void ClusterMigrateRejectsMalformedPayload(MalformedPayload malformed)
        {
            context.CreateInstances(defaultShards, useTLS: UseTLS);
            context.CreateConnection(useTLS: UseTLS);
            _ = context.clusterTestUtils.SimpleSetupCluster(logger: context.logger);

            var sourceNodeId = context.clusterTestUtils.GetNodeIdFromNode(1, context.logger);
            var targetEndpoint = context.clusterTestUtils.GetEndPoint(2);
            byte[] payload = malformed switch
            {
                MalformedPayload.TruncatedKeyCount => [1, 0, 0],
                MalformedPayload.NegativeKeyCount => [255, 255, 255, 255],
                MalformedPayload.MissingRecordKind or MalformedPayload.MissingVectorSetKind => [1, 0, 0, 0],
                MalformedPayload.MissingRecordLength => [1, 0, 0, 0, (byte)MigrationRecordSpanType.LogRecord],
                MalformedPayload.MissingVectorSetRecordLength => [1, 0, 0, 0, (byte)MigrationRecordSpanType.VectorSetIndex],
                MalformedPayload.MissingChunkLength => [1, 0, 0, 0, (byte)MigrationRecordSpanType.ChunkedLogRecord],
                MalformedPayload.OversizedChunk => [1, 0, 0, 0, (byte)MigrationRecordSpanType.ChunkedLogRecord, 20, 0, 0, 0],
                _ => throw new ArgumentOutOfRangeException(nameof(malformed))
            };

            var vectorSet = malformed is MalformedPayload.MissingVectorSetKind or MalformedPayload.MissingVectorSetRecordLength;
            SendRaw(targetEndpoint, BuildMigratePayloadCommand(sourceNodeId, payload, vectorSet));

            var pong = context.clusterTestUtils.GetMultiplexer().GetServer(targetEndpoint).Execute("PING");
            ClassicAssert.AreEqual("PONG", pong.ToString());
        }

        /// <summary>
        /// Builds a CLUSTER MIGRATE command carrying one inline log record whose header declares a value far larger
        /// than the bytes actually present in the framed record.
        /// </summary>
        static unsafe byte[] BuildMalformedMigrateCommand(string sourceNodeId, byte[] keyBytes)
        {
            var recordLength = RecordInfo.Size + RecordDataHeader.Size + keyBytes.Length + ProvidedValueLength;
            var record = new byte[recordLength];
            fixed (byte* rp = record)
            {
                *(RecordInfo*)rp = new RecordInfo();
                ref var header = ref *(RecordDataHeader*)(rp + RecordInfo.Size);
                header.SetKeyAndValueInline();
                header.KeyLength = keyBytes.Length;
                header.ValueLength = DeclaredValueLength;   // lie: only ProvidedValueLength value bytes follow

                var keyOffset = Constants.FixedHeaderSize;
                for (var i = 0; i < keyBytes.Length; i++)
                    rp[keyOffset + i] = keyBytes[i];
                for (var i = 0; i < ProvidedValueLength; i++)
                    rp[keyOffset + keyBytes.Length + i] = 0x41;
            }

            return BuildMigrateCommand(sourceNodeId, record);
        }

        static unsafe byte[] BuildMigrateCommand(string sourceNodeId, byte[] record)
        {
            // Payload = [int keyCount][byte MigrationRecordSpanType][int recordLength][record bytes]
            var payload = new byte[sizeof(int) + 1 + sizeof(int) + record.Length];
            fixed (byte* pp = payload)
            {
                *(int*)pp = 1;
                pp[sizeof(int)] = (byte)MigrationRecordSpanType.LogRecord;
                *(int*)(pp + sizeof(int) + 1) = record.Length;
            }
            Array.Copy(record, 0, payload, sizeof(int) + 1 + sizeof(int), record.Length);

            return BuildMigratePayloadCommand(sourceNodeId, payload);
        }

        static byte[] BuildMigratePayloadCommand(string sourceNodeId, byte[] payload, bool vectorSet = false)
        {
            return BuildRespArray(
                "CLUSTER"u8.ToArray(),
                "MIGRATE"u8.ToArray(),
                Encoding.ASCII.GetBytes(sourceNodeId),
                "F"u8.ToArray(),
                vectorSet ? "T"u8.ToArray() : "F"u8.ToArray(),
                payload);
        }

        static byte[] BuildRespArray(params byte[][] args)
        {
            using var ms = new MemoryStream();
            void WriteAscii(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                ms.Write(b, 0, b.Length);
            }

            WriteAscii($"*{args.Length}\r\n");
            foreach (var arg in args)
            {
                WriteAscii($"${arg.Length}\r\n");
                ms.Write(arg, 0, arg.Length);
                WriteAscii("\r\n");
            }
            return ms.ToArray();
        }

        static void SendRaw(IPEndPoint endpoint, byte[] command)
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(endpoint);
            socket.Send(command);
            socket.ReceiveTimeout = 2000;
            var response = new byte[256];
            try
            {
                var received = socket.Receive(response);
                ClassicAssert.AreNotEqual("+OK\r\n", Encoding.ASCII.GetString(response, 0, received));
            }
            catch (SocketException) { /* fix path tears down the connection without replying */ }
        }
    }
}
