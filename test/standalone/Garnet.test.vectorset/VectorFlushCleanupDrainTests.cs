// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// Tests that <c>VectorManager.WaitForCleanupComplete</c> fully drains the background cleanup
    /// pipeline after a FLUSHDB, and that FLUSHDB itself leaves the reservation state coherent.
    ///
    /// A FLUSHDB empties the main store, which queues eviction-driven native DiskANN drops and can
    /// leave in-flight cleanup, while <c>FlushGuard</c> wipes the in-memory context reservation. This
    /// exercises multiple Vector Sets (distinct contexts) at once and asserts that once the drain
    /// barrier returns, nothing is outstanding: no reserved contexts, no dirty context metadata, and
    /// no pending native drops.
    /// </summary>
    [TestFixture]
    public class VectorFlushCleanupDrainTests : TestBase
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

        /// <summary>
        /// Create several Vector Sets, each in its own context, then FLUSHDB and drain. After the
        /// barrier returns the reservation bitmap, the dirty-metadata set, and the pending-drop set
        /// must all be empty, and no records may remain in any namespace.
        /// </summary>
        [Test]
        public void FlushDbDrainsAllVectorSetCleanup()
        {
            const int Sets = 5;
            const int ElementsPerSet = 200;

            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

#if DEBUG
            var preCreateCalls = vectorManager.Service.CreateIndexCalls;
            var preDropCalls = vectorManager.Service.DropIndexCalls;
#endif

            for (var s = 0; s < Sets; s++)
            {
                PopulateVectorSet(db, $"{{vs}}set-{s}", ElementsPerSet, seed: 2026_08_01 + s);
            }

            ClassicAssert.AreEqual(Sets, vectorManager.GetReservedContextCount(), "each populated Vector Set should reserve exactly one context");

            adminServer.FlushDatabase(0);

            // Drain the whole cleanup pipeline: request-cleanup marking, checkpoint discovery, the
            // cleanup scan, and native DiskANN drops queued by emptying the store.
            vectorManager.WaitForCleanupComplete();

            ClassicAssert.AreEqual(0, vectorManager.GetReservedContextCount(), "FLUSHDB must leave no reserved Vector Set context");
            ClassicAssert.AreEqual(0, vectorManager.GetDirtyContextMetadataCount(), "FLUSHDB must leave no dirty context metadata");
            ClassicAssert.AreEqual(0, vectorManager.GetPendingDropCount(), "the drain must complete every pending native index drop");

            var dbSize = (long)db.Execute("DBSIZE");
            ClassicAssert.AreEqual(0, dbSize, "FLUSHDB must remove every record across all namespaces");

#if DEBUG
            var finalCreateCalls = vectorManager.Service.CreateIndexCalls;
            var finalDropCalls = vectorManager.Service.DropIndexCalls;

            // Every native index that was created must have been dropped by the drain.
            ClassicAssert.Greater(finalCreateCalls, preCreateCalls, "populating the Vector Sets should have created native indexes");
            ClassicAssert.AreEqual(finalCreateCalls - preCreateCalls, finalDropCalls - preDropCalls, "every created native index must be dropped by the drain");
#endif
        }

        /// <summary>
        /// After a FLUSHDB drains the pipeline, creating a brand-new Vector Set must succeed. A stale
        /// dirty-context-metadata entry left by the flush would fault the first UpdateContextMetadata,
        /// so this is a regression guard for the FLUSHDB reset coherence.
        /// </summary>
        [Test]
        public void FreshVectorSetAfterFlushDbSucceeds()
        {
            var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
            ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var adminServer = redis.GetServers()[0];

            for (var s = 0; s < 3; s++)
            {
                PopulateVectorSet(db, $"{{vs}}pre-{s}", 100, seed: 2026_08_10 + s);
            }

            adminServer.FlushDatabase(0);
            vectorManager.WaitForCleanupComplete();

            ClassicAssert.AreEqual(0, vectorManager.GetReservedContextCount());
            ClassicAssert.AreEqual(0, vectorManager.GetDirtyContextMetadataCount());

            // A fresh Vector Set must create cleanly on top of the reset reservation state.
            PopulateVectorSet(db, "{vs}fresh", 100, seed: 2026_08_20);

            ClassicAssert.AreEqual(1, vectorManager.GetReservedContextCount(), "the fresh Vector Set should reserve exactly one context after the reset");
            ClassicAssert.IsTrue(db.KeyExists("{vs}fresh"), "the fresh Vector Set must persist after being created post-FLUSHDB");
        }
    }
}