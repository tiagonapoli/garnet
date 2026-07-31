// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// Deterministic proof that the cleanup-drain barrier awaited on the replica full-sync paths
    /// (<c>ReplicaDisklessSync.TryReplicaDisklessRecovery</c> and <c>ReplicaDiskbasedSync</c>) is
    /// necessary. Both paths empty the store (primary-driven CLUSTER FLUSHALL / <c>storeWrapper.Reset</c>)
    /// and then call <c>VectorManager.WaitForCleanupComplete</c> before advertising the offset and
    /// serving. Emptying the store queues eviction-driven native DiskANN drops on a background task; if
    /// the sync proceeds without draining them, the boundary is crossed while a namespace's native
    /// index is still pending drop.
    ///
    /// These tests use <see cref="ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop"/> to hold
    /// a drop in flight across the store-empty boundary, making the race deterministic: without the
    /// drain the pending-drop set is non-empty at the boundary; the drain is exactly what blocks until
    /// it is empty. Exception injection is only compiled into DEBUG builds, so the tests self-ignore in
    /// Release via <see cref="TestUtils.IgnoreIfExceptionInjectionDisabled"/>.
    /// </summary>
    [TestFixture]
    public class VectorSyncDrainNecessityTests : TestBase
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
            // Never leave the injection armed — it would park the next test's drop task.
            ExceptionInjectionHelper.DisableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
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
        /// Arm the drop-pause injection, flush, and wait for the background drop task to park with the
        /// drop still pending. This is the exact state a full sync would proceed into if it did NOT
        /// drain — the pending-drop set is non-empty at the store-empty boundary.
        /// </summary>
        [Test]
        public void StoreEmptyBoundaryHasPendingNativeDropsWithoutDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

            PopulateVectorSet(db, "{vs}pending", 100, seed: 2026_08_30);
            ClassicAssert.AreEqual(1, vectorManager.GetReservedContextCount(), "the populated Vector Set should reserve one context");

            // Arm the pause BEFORE emptying the store so the eviction-driven drop parks in flight.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

            adminServer.FlushDatabase(0);

            // The drop task clears the flag when it reaches the pause — that is our "arrived, parked" signal.
            SpinUntil(() => !ExceptionInjectionHelper.IsEnabled(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop),
                TimeSpan.FromSeconds(30), "the background drop task never reached the in-flight pause after FLUSHDB");

            // Boundary crossed without a drain: a native index drop is still outstanding.
            ClassicAssert.Greater(vectorManager.GetPendingDropCount(), 0,
                "emptying the store must leave a native index drop pending — the hazard the sync-path drain guards against");

            // Release and drain so the fixture tears down cleanly.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
            vectorManager.WaitForCleanupComplete();
            ClassicAssert.AreEqual(0, vectorManager.GetPendingDropCount());
        }

        /// <summary>
        /// With a drop held in flight at the boundary, <c>WaitForCleanupComplete</c> must NOT return
        /// until that drop finishes. Removing the drain call from the sync path would let the caller
        /// proceed immediately (as proven by <see cref="StoreEmptyBoundaryHasPendingNativeDropsWithoutDrain"/>);
        /// this proves the drain is exactly what waits for the pipeline to quiesce.
        /// </summary>
        [Test]
        public void WaitForCleanupCompleteBlocksUntilNativeDropsFinish()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

            PopulateVectorSet(db, "{vs}block", 100, seed: 2026_08_31);

            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
            adminServer.FlushDatabase(0);

            SpinUntil(() => !ExceptionInjectionHelper.IsEnabled(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop),
                TimeSpan.FromSeconds(30), "the background drop task never reached the in-flight pause after FLUSHDB");
            ClassicAssert.Greater(vectorManager.GetPendingDropCount(), 0, "a native drop should be parked in flight");

            var drain = Task.Run(() => vectorManager.WaitForCleanupComplete());

            // While the drop is parked, the drain must stay blocked.
            ClassicAssert.IsFalse(drain.Wait(TimeSpan.FromMilliseconds(500)),
                "WaitForCleanupComplete returned while a native index drop was still in flight");

            // Release the parked drop; the drain must now complete and leave nothing outstanding.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

            ClassicAssert.IsTrue(drain.Wait(TimeSpan.FromSeconds(30)),
                "WaitForCleanupComplete did not return after the in-flight drop was released");
            ClassicAssert.AreEqual(0, vectorManager.GetPendingDropCount(), "the drain must complete every pending native index drop");
            ClassicAssert.AreEqual(0, vectorManager.GetReservedContextCount(), "no Vector Set context may remain reserved after the drain");
        }
    }
}