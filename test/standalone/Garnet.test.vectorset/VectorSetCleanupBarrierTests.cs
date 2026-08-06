// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// Tests for <c>VectorManager.WaitForCleanupCompleteAsync</c>, the barrier that blocks until the
    /// background cleanup pipeline is quiescent.
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupBarrierTests : VectorSetCleanupTestBase
    {
        [Test]
        public void BarrierReturnsImmediatelyWhenIdle()
        {
            DrainCleanup(VectorManager);
        }

        /// <summary>
        /// Deleting a Vector Set queues marking, a keyspace scan and a native index drop. After the
        /// barrier returns none of that may still be outstanding, so every element key must be gone.
        /// </summary>
        [Test]
        public void BarrierDrainsCleanupQueuedByDelete()
        {
            const int Sets = 3;
            const int ElementsPerSet = 200;

            var vectorManager = VectorManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

#if DEBUG
            var preDropCalls = vectorManager.Service.DropIndexCalls;
#endif

            for (var s = 0; s < Sets; s++)
            {
                PopulateVectorSet(db, $"{{vs}}set-{s}", ElementsPerSet, seed: 2026_08_01 + s);
            }

            for (var s = 0; s < Sets; s++)
            {
                _ = db.KeyDelete($"{{vs}}set-{s}");
            }

            DrainCleanup(vectorManager);

            ClassicAssert.AreEqual(0, (long)db.Execute("DBSIZE"), "no element keys may survive the drain");
#if DEBUG
            ClassicAssert.AreEqual(Sets, vectorManager.Service.DropIndexCalls - preDropCalls, "the drain must complete every native index drop queued by the deletes");
#endif
        }
    }
}