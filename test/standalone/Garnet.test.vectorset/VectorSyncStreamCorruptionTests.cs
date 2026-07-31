// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Garnet.common;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// Deterministic corruption proof for the cleanup-drain barrier awaited on the replica full-sync
    /// paths. A diskless full sync empties the store (primary-driven CLUSTER FLUSHALL) and then streams
    /// records back in <b>as-is</b> — bypassing both guards that protect the normal command path:
    /// context reservation (which skips <c>cleaningUp</c> contexts) and the <c>WaitForDiskANNIndexDrop</c>
    /// recreate spin. So a streamed vector element can land in a namespace that still has an in-flight
    /// cleanup scan queued against it.
    ///
    /// This test reproduces exactly that: it parks the cleanup scan (via
    /// <see cref="ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan"/>) after its <c>needCleanup</c>
    /// snapshot is built but before the delete-scan runs, streams a fresh element into that same
    /// namespace, then releases the scan. Without draining first, the scan deletes the just-streamed
    /// element — silent data loss. Draining before streaming (what the sync path does) prevents it.
    ///
    /// Exception injection is DEBUG-only, so the tests self-ignore in Release via
    /// <see cref="TestUtils.IgnoreIfExceptionInjectionDisabled"/>.
    /// </summary>
    [TestFixture]
    public class VectorSyncStreamCorruptionTests : TestBase
    {
        private global::Garnet.GarnetServer server;

        [SetUp]
        public void Setup()
        {
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableAOF: true, enableVectorSetPreview: true);
            server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            // Never leave the injection armed — it would park the next test's cleanup scan.
            ExceptionInjectionHelper.DisableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            try { server.Dispose(); } catch { }
            TestUtils.OnTearDown();
        }

        private static void PopulateVectorSet(IDatabase db, string key, int elements, int seed)
        {
            var elem = new byte[4];
            var data = new byte[75];
            var rand = new Random(seed);
            for (var i = 0; i < elements; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(elem, i);
                rand.NextBytes(data);
                _ = db.Execute("VADD", [key, "XB8", data, elem, "XPREQ8"]);
            }
        }

        private static void SpinUntil(Func<bool> condition, TimeSpan timeout, string message)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > timeout)
                    Assert.Fail(message);
                Thread.Yield();
            }
        }

        /// <summary>
        /// WITHOUT a drain: park the cleanup scan mid-flight, stream a record into the namespace it is
        /// about to clean, release it — the scan deletes the streamed record. This is the data loss a
        /// diskless full sync would suffer if it streamed records before draining the cleanup pipeline.
        /// </summary>
        [Test]
        public void InFlightCleanupScanDeletesStreamedElement_WithoutDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

            PopulateVectorSet(db, "{vs}a", 5, seed: 2026_09_01);
            var reserved = vectorManager.GetReservedContexts();
            ClassicAssert.AreEqual(1, reserved.Count, "the populated Vector Set should reserve exactly one context");
            var context = reserved[0];

            // Arm the mid-scan pause, then drop the set. Deleting queues a cleanup scan for this context;
            // the scan builds needCleanup={context} then parks before deleting.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            _ = db.KeyDelete("{vs}a");

            SpinUntil(() => !ExceptionInjectionHelper.IsEnabled(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan),
                TimeSpan.FromSeconds(30), "the cleanup scan never reached the in-flight pause after the set was dropped");

            // The scan is parked with the namespace queued for cleanup. Stream a fresh element straight
            // into that namespace — mimicking diskless as-is streaming (no reservation, no drop guard).
            var streamedKey = Encoding.ASCII.GetBytes("streamed-element");
            var value = new byte[75];
            new Random(0xBEEF).NextBytes(value);
            vectorManager.TestOnlyStreamElementIntoContext(context, streamedKey, value);

            ClassicAssert.Greater(vectorManager.TestOnlyCountRecordsInContext(context), 0,
                "the streamed element must be present in the namespace before the scan resumes");

            // Release the scan and let the whole pipeline finish.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            vectorManager.WaitForCleanupComplete();

            // The in-flight scan deleted the streamed element: crossing the boundary without a drain
            // corrupts (loses) the streamed data.
            ClassicAssert.AreEqual(0, vectorManager.TestOnlyCountRecordsInContext(context),
                "the in-flight cleanup scan deleted the streamed element — the silent data loss the sync-path drain prevents");
        }

        /// <summary>
        /// WITH a drain: draining the cleanup pipeline before streaming (exactly what the full-sync path
        /// does) means the namespace is no longer queued for cleanup when the record is streamed, so it
        /// survives. This is the fixed behavior.
        /// </summary>
        [Test]
        public void DrainBeforeStreamPreservesElement_WithDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

            PopulateVectorSet(db, "{vs}a", 5, seed: 2026_09_02);
            var context = vectorManager.GetReservedContexts()[0];

            // Drop the set and DRAIN fully first — the sync-path ordering (empty store, then drain,
            // then stream). The cleanup scan runs to completion here, removing the set's own elements.
            _ = db.KeyDelete("{vs}a");
            vectorManager.WaitForCleanupComplete();
            ClassicAssert.AreEqual(0, vectorManager.TestOnlyCountRecordsInContext(context),
                "the drain should have removed the dropped set's elements");

            // Now stream a record into that namespace — no in-flight cleanup remains to delete it.
            var streamedKey = Encoding.ASCII.GetBytes("streamed-element");
            var value = new byte[75];
            new Random(0xF00D).NextBytes(value);
            vectorManager.TestOnlyStreamElementIntoContext(context, streamedKey, value);

            ClassicAssert.AreEqual(1, vectorManager.TestOnlyCountRecordsInContext(context),
                "the streamed element must survive when the pipeline was drained before streaming");

            // A subsequent drain must not touch it — nothing is queued against this namespace.
            vectorManager.WaitForCleanupComplete();
            ClassicAssert.AreEqual(1, vectorManager.TestOnlyCountRecordsInContext(context),
                "the streamed element must remain after a subsequent drain (nothing queued against its namespace)");
        }
    }
}