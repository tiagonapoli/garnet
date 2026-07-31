// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// Relies on the DEBUG-only VectorManager test hooks (VectorManager.TestHooks.cs).
#if DEBUG

using System;
using System.Text;
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
    /// necessary, and that it is sufficient.
    ///
    /// Both paths empty the store (primary-driven CLUSTER FLUSHALL / <c>storeWrapper.Reset</c>) and then
    /// call <c>VectorManager.WaitForCleanupComplete</c> before advertising the offset and serving. Two
    /// independent hazards are covered:
    ///
    /// <list type="bullet">
    /// <item>Emptying the store queues eviction-driven native DiskANN drops on a background task, so the
    /// boundary can be crossed while a native index is still pending drop.</item>
    /// <item>A diskless sync streams records back in <b>as-is</b>, bypassing both guards that protect the
    /// normal command path — context reservation (which skips <c>cleaningUp</c> contexts) and the
    /// <c>WaitForDiskANNIndexDrop</c> recreate spin — so a streamed element can land in a namespace with
    /// an in-flight cleanup scan queued against it and be silently deleted.</item>
    /// </list>
    ///
    /// The injection seams <see cref="ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop"/> and
    /// <see cref="ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan"/> hold the respective pipeline
    /// stage in flight across the boundary, making both races deterministic. Exception injection is only
    /// compiled into DEBUG builds, so the tests self-ignore in Release via
    /// <see cref="TestUtils.IgnoreIfExceptionInjectionDisabled"/>.
    /// </summary>
    [TestFixture]
    public class VectorSetReplicaSyncTests : VectorSetCleanupTestBase
    {
        /// <summary>
        /// Arm the drop-pause injection, flush, and wait for the background drop task to park with the
        /// drop still pending. This is the exact state a full sync would proceed into if it did NOT
        /// drain — the pending-drop set is non-empty at the store-empty boundary.
        /// </summary>
        [Test]
        public void StoreEmptyBoundaryHasPendingNativeDropsWithoutDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = VectorManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

            PopulateVectorSet(db, "{vs}pending", 100, seed: 2026_08_30);
            ClassicAssert.AreEqual(1, vectorManager.GetReservedContextCount(), "the populated Vector Set should reserve one context");

            // Arm the pause BEFORE emptying the store so the eviction-driven drop parks in flight.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

            adminServer.FlushDatabase(0);

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop,
                "the background drop task never reached the in-flight pause after FLUSHDB");

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

            var vectorManager = VectorManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

            PopulateVectorSet(db, "{vs}block", 100, seed: 2026_08_31);

            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
            adminServer.FlushDatabase(0);

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop,
                "the background drop task never reached the in-flight pause after FLUSHDB");
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

        /// <summary>
        /// WITHOUT a drain: park the cleanup scan mid-flight, stream a record into the namespace it is
        /// about to clean, release it — the scan deletes the streamed record. This is the data loss a
        /// diskless full sync would suffer if it streamed records before draining the cleanup pipeline.
        /// </summary>
        [Test]
        public void InFlightCleanupScanDeletesStreamedElement_WithoutDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = VectorManager;

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

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan,
                "the cleanup scan never reached the in-flight pause after the set was dropped");

            // The scan is parked with the namespace queued for cleanup. Stream a fresh element straight
            // into that namespace — mimicking diskless as-is streaming (no reservation, no drop guard).
            var streamedKey = Encoding.ASCII.GetBytes("streamed-element");
            var value = new byte[VectorDimensions];
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

            var vectorManager = VectorManager;

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
            var value = new byte[VectorDimensions];
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

#endif
