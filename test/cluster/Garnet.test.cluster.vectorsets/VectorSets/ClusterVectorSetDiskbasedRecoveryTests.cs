// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Disk-based (checkpoint) full-sync coverage for a populated Vector Set.
    ///
    /// A replica that attaches to a primary holding data it has never seen recovers the primary's
    /// checkpoint through <c>RecoverCheckpointAsync(replicaRecover: true)</c>, so the Vector Set index
    /// and element records arrive through Tsavorite's recovery page scan rather than through streamed
    /// AOF records. With <c>FastAofTruncate</c> the AOF is truncated at the checkpoint, so AOF replay
    /// cannot paper over a checkpoint that failed to recover.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ClusterVectorSetDiskbasedRecoveryTests
    {
        /// <summary>Mirrors the server log so the test can assert on what recovery reported.</summary>
        private sealed class CaptureLogWriter(TextWriter passThrough) : TextWriter
        {
            private readonly StringBuilder buffer = new();

            public override Encoding Encoding => passThrough.Encoding;

            public override void Write(string value)
            {
                passThrough.Write(value);

                lock (buffer)
                {
                    _ = buffer.Append(value);
                }
            }

            public string Captured()
            {
                lock (buffer)
                {
                    return buffer.ToString();
                }
            }
        }

        private const int PrimaryIndex = 0;
        private const int ReplicaIndex = 1;

        private const int VectorDimensions = 64;
        private const int Elements = 200;

        /// <summary>The message <c>RecoverCheckpointAsync</c> logs when it swallows a recovery fault.</summary>
        private const string RecoveryErrorMessage = "Error during recovery of store";

        private ClusterTestContext context;
        private CaptureLogWriter captureLogWriter;

        private readonly int timeout = (int)TimeSpan.FromSeconds(30).TotalSeconds;

        [SetUp]
        public void Setup()
        {
            captureLogWriter = new(TestContext.Progress);

            context = new ClusterTestContext { logTextWriter = captureLogWriter };

            // RecoverCheckpointAsync reports a failed recovery at Information and then carries on.
            context.Setup(new Dictionary<string, LogLevel> { [TestContext.CurrentContext.Test.MethodName] = LogLevel.Information });
        }

        [TearDown]
        public void TearDown()
        {
            context?.TearDown();
        }

        /// <summary>
        /// A Vector Set that exists only in the primary's checkpoint must be fully readable on a
        /// replica that attaches afterwards.
        /// </summary>
        [Test]
        [Category("REPLICATION")]
        public async Task PopulatedVectorSetSurvivesDiskbasedFullSyncAsync()
        {
            const string Key = "{vsdisk}populated";

            context.CreateInstances(
                2,
                enableAOF: true,
                FastAofTruncate: true,
                CommitFrequencyMs: -1,
                OnDemandCheckpoint: true,
                timeout: timeout);
            context.CreateConnection();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.AddDelSlotsRange(PrimaryIndex, [(0, 16383)], addslot: true, logger: context.logger));
            context.clusterTestUtils.SetConfigEpoch(PrimaryIndex, PrimaryIndex + 1, logger: context.logger);
            context.clusterTestUtils.SetConfigEpoch(ReplicaIndex, ReplicaIndex + 1, logger: context.logger);
            context.clusterTestUtils.Meet(PrimaryIndex, ReplicaIndex, logger: context.logger);

            var primaryId = context.clusterTestUtils.ClusterMyId(PrimaryIndex, logger: context.logger);
            await context.clusterTestUtils.WaitUntilNodeIdIsKnownAsync(ReplicaIndex, primaryId, logger: context.logger).ConfigureAwait(false);

            // Written while the primary is alone, so the elements only ever reach the replica through
            // the checkpoint that the following CHECKPOINT call takes.
            var elements = PopulateVectorSet(PrimaryIndex, Key, Elements, seed: 2026_07_30_00);
            CheckpointPrimary();

            ClassicAssert.AreEqual("OK", context.clusterTestUtils.ClusterReplicate(ReplicaIndex, PrimaryIndex, logger: context.logger));
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, logger: context.logger);

            // RecoverCheckpointAsync only rethrows under FailOnRecoveryError, so a fault raised while
            // Tsavorite scans the checkpoint leaves the replica attached and serving a store that was
            // only partially recovered. The log is the sole evidence that it happened.
            var log = captureLogWriter.Captured();
            ClassicAssert.IsFalse(
                log.Contains(RecoveryErrorMessage, StringComparison.Ordinal),
                $"the replica failed to recover the primary's checkpoint but still reported the attach as successful; " +
                $"VectorManager's recovery hooks throw NullReferenceException because recoveredIndexes/recoveredMetadata " +
                $"were already released by ResumePostRecovery at startup, and this recovery is a runtime one:" +
                $"{Environment.NewLine}{RecoveryErrorExcerpt(log)}");

            var readOnly = (string)context.clusterTestUtils.Execute(context.clusterTestUtils.GetEndPoint(ReplicaIndex), "READONLY", [], logger: context.logger);
            ClassicAssert.AreEqual("OK", readOnly);

            ClassicAssert.AreEqual(Elements, VectorSetSize(PrimaryIndex, Key), $"the primary lost elements of '{Key}'; the test is asserting nothing");
            ClassicAssert.AreEqual(
                Elements,
                VectorSetSize(ReplicaIndex, Key),
                $"the replica holds a different number of elements for '{Key}' than the primary after a disk-based full sync");

            var missing = new List<int>();
            for (var i = 0; i < elements.Count; i++)
            {
                if (ElementEmbedding(ReplicaIndex, Key, elements[i]).Length == 0)
                    missing.Add(i);
            }

            ClassicAssert.IsEmpty(
                missing,
                $"{missing.Count} of {elements.Count} elements of '{Key}' are missing on the replica after a disk-based full sync (first few: {string.Join(", ", missing.Take(10))})");
        }

        /// <summary>Trims the captured log down to the recovery failure and the stack that caused it.</summary>
        private static string RecoveryErrorExcerpt(string log)
        {
            var start = log.IndexOf(RecoveryErrorMessage, StringComparison.Ordinal);
            if (start < 0)
                return log;

            return log.Substring(start, Math.Min(2_000, log.Length - start));
        }

        /// <summary>Takes a checkpoint on the primary and waits for it to land.</summary>
        private void CheckpointPrimary()
        {
            var lastSave = context.clusterTestUtils.LastSave(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitUntilNextSecond(PrimaryIndex, lastSave, logger: context.logger);
            context.clusterTestUtils.Checkpoint(PrimaryIndex, logger: context.logger);
            context.clusterTestUtils.WaitCheckpoint(PrimaryIndex, lastSave, logger: context.logger);
        }

        private List<byte[]> PopulateVectorSet(int nodeIndex, string key, int count, int seed)
        {
            var endpoint = context.clusterTestUtils.GetEndPoint(nodeIndex);
            var r = new Random(seed);
            var elements = new List<byte[]>(count);

            for (var i = 0; i < count; i++)
            {
                var vector = new byte[VectorDimensions];
                r.NextBytes(vector);

                var element = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(element, i);

                var added = (int)context.clusterTestUtils.Execute(endpoint, "VADD", [key, "XB8", vector, element, "XPREQ8"], skipLogging: true);
                ClassicAssert.AreEqual(1, added, $"VADD of element {i} into '{key}' should have inserted a new element");

                elements.Add(element);
            }

            return elements;
        }

        /// <summary>Reads the cardinality of a Vector Set from VINFO.</summary>
        private long VectorSetSize(int nodeIndex, string key)
        {
            var reply = context.clusterTestUtils.Execute(context.clusterTestUtils.GetEndPoint(nodeIndex), "VINFO", [key], logger: context.logger);

            // Execute surfaces errors as bulk strings, so a non-array reply means the read failed.
            if (reply.Resp2Type != ResultType.Array)
                Assert.Fail($"VINFO on '{key}' at node {nodeIndex} did not return an array, got {reply.Resp2Type}: {reply}");

            var fields = (RedisValue[])reply;
            if (fields is null)
                Assert.Fail($"VINFO on '{key}' at node {nodeIndex} returned a nil array, so that node does not hold the Vector Set at all");

            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                if (((string)fields[i]).Equals("size", StringComparison.OrdinalIgnoreCase))
                    return (long)fields[i + 1];
            }

            Assert.Fail($"VINFO reply for '{key}' had no 'size' field: [{string.Join(", ", fields.Select(static f => (string)f))}]");
            return -1;
        }

        /// <summary>Returns the stored embedding for an element, or an empty array when absent.</summary>
        private string[] ElementEmbedding(int nodeIndex, string key, byte[] element)
        {
            var reply = context.clusterTestUtils.Execute(context.clusterTestUtils.GetEndPoint(nodeIndex), "VEMB", [key, element], skipLogging: true);
            if (reply.Resp2Type != ResultType.Array)
                Assert.Fail($"VEMB on '{key}' at node {nodeIndex} did not return an array, got {reply.Resp2Type}: {reply}");

            return (string[])reply;
        }
    }
}
