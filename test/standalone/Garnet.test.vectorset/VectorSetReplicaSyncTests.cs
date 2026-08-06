// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    /// Proof that the cleanup drain awaited on the replica full-sync paths is both necessary and
    /// sufficient. Both paths empty the store and then drain before advertising the offset, guarding
    /// two hazards: emptying the store queues eviction-driven native DiskANN drops on a background
    /// task, and a diskless sync streams records back in as-is, so a streamed element can land in a
    /// namespace with a cleanup scan already queued against it.
    /// </summary>
    [TestFixture]
    public class VectorSetReplicaSyncTests : VectorSetCleanupTestBase
    {
        /// <summary>
        /// Arm the drop-pause injection, flush, and wait for the background drop task to park with the
        /// drop still pending. This is the exact state a full sync would proceed into if it did NOT
        /// drain - the pending-drop set is non-empty at the store-empty boundary.
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
            ClassicAssert.AreEqual(1, vectorManager.TestHookGetReservedContexts().Count, "the populated Vector Set should reserve one context");

            // Arm the pause BEFORE emptying the store so the eviction-driven drop parks in flight.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

            adminServer.FlushDatabase(0);

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop, "the background drop task never reached the in-flight pause after FLUSHDB");

            // Boundary crossed without a drain: a native index drop is still outstanding.
            ClassicAssert.Greater(vectorManager.TestHookGetPendingDropCount(), 0, "emptying the store must leave a native index drop pending - the hazard the sync-path drain guards against");

            // Release and drain so the fixture tears down cleanly.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
            DrainCleanup(vectorManager);
            ClassicAssert.AreEqual(0, vectorManager.TestHookGetPendingDropCount());
        }

        /// <summary>
        /// With a drop held in flight at the boundary, the drain must NOT return until that drop
        /// finishes. Removing the drain call from the sync path would let the caller proceed immediately,
        /// as proven by <see cref="StoreEmptyBoundaryHasPendingNativeDropsWithoutDrain"/>.
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

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop, "the background drop task never reached the in-flight pause after FLUSHDB");
            ClassicAssert.Greater(vectorManager.TestHookGetPendingDropCount(), 0, "a native drop should be parked in flight");

            var drain = Task.Run(() => DrainCleanup(vectorManager));

            // While the drop is parked, the drain must stay blocked.
            ClassicAssert.IsFalse(drain.Wait(TimeSpan.FromMilliseconds(500)), "WaitForCleanupComplete returned while a native index drop was still in flight");

            // Release the parked drop; the drain must now complete and leave nothing outstanding.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

            ClassicAssert.IsTrue(drain.Wait(TimeSpan.FromSeconds(30)), "WaitForCleanupComplete did not return after the in-flight drop was released");
            ClassicAssert.AreEqual(0, vectorManager.TestHookGetPendingDropCount(), "the drain must complete every pending native index drop");
            ClassicAssert.AreEqual(0, vectorManager.TestHookGetReservedContexts().Count, "no Vector Set context may remain reserved after the drain");
        }

        /// <summary>
        /// WITHOUT a drain: park the cleanup scan mid-flight, stream a record into the namespace it is
        /// about to clean, release it - the scan deletes the streamed record. This is the data loss a
        /// diskless full sync would suffer if it streamed records before draining the cleanup pipeline.
        /// </summary>
        [Test]
        public void InFlightCleanupScanDeletesStreamedElementWithoutDrain()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = VectorManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

            PopulateVectorSet(db, "{vs}a", 5, seed: 2026_09_01);
            var reserved = vectorManager.TestHookGetReservedContexts();
            ClassicAssert.AreEqual(1, reserved.Count, "the populated Vector Set should reserve exactly one context");
            var context = reserved[0];

            // Arm the mid-scan pause, then drop the set. Deleting queues a cleanup scan for this context;
            // the scan builds needCleanup={context} then parks before deleting.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            _ = db.KeyDelete("{vs}a");

            WaitUntilParked(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan, "the cleanup scan never reached the in-flight pause after the set was dropped");

            // The scan is parked with the namespace queued for cleanup. Stream a fresh element straight
            // into that namespace - mimicking diskless as-is streaming (no reservation, no drop guard).
            var streamedKey = Encoding.ASCII.GetBytes("streamed-element");
            var value = new byte[VectorDimensions];
            new Random(0xBEEF).NextBytes(value);
            vectorManager.TestHookStreamElementIntoContext(context, streamedKey, value);

            ClassicAssert.Greater(vectorManager.TestHookCountRecordsInContext(context), 0, "the streamed element must be present in the namespace before the scan resumes");

            // Release the scan and let the whole pipeline finish.
            ExceptionInjectionHelper.EnableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            DrainCleanup(vectorManager);

            // The in-flight scan deleted the streamed element: crossing the boundary without a drain
            // loses the streamed data.
            ClassicAssert.AreEqual(0, vectorManager.TestHookCountRecordsInContext(context), "the in-flight cleanup scan deleted the streamed element - the silent data loss the sync-path drain prevents");
        }

        /// <summary>
        /// WITH a drain: draining before streaming (exactly what the full-sync path does) means the
        /// namespace is no longer queued for cleanup when the record is streamed, so it survives.
        /// </summary>
        [Test]
        public void DrainBeforeStreamPreservesElement()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            var vectorManager = VectorManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

            PopulateVectorSet(db, "{vs}a", 5, seed: 2026_09_02);
            var context = vectorManager.TestHookGetReservedContexts()[0];

            // Drop the set and DRAIN fully first - the sync-path ordering (empty store, then drain,
            // then stream). The cleanup scan runs to completion here, removing the set's own elements.
            _ = db.KeyDelete("{vs}a");
            DrainCleanup(vectorManager);
            ClassicAssert.AreEqual(0, vectorManager.TestHookCountRecordsInContext(context), "the drain should have removed the dropped set's elements");

            // Now stream a record into that namespace - no in-flight cleanup remains to delete it.
            var streamedKey = Encoding.ASCII.GetBytes("streamed-element");
            var value = new byte[VectorDimensions];
            new Random(0xF00D).NextBytes(value);
            vectorManager.TestHookStreamElementIntoContext(context, streamedKey, value);

            ClassicAssert.AreEqual(1, vectorManager.TestHookCountRecordsInContext(context), "the streamed element must survive when the pipeline was drained before streaming");

            // A subsequent drain must not touch it - nothing is queued against this namespace.
            DrainCleanup(vectorManager);
            ClassicAssert.AreEqual(1, vectorManager.TestHookCountRecordsInContext(context), "the streamed element must remain after a subsequent drain (nothing queued against its namespace)");
        }
    }
}