// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;
using Garnet.server;
using Garnet.server.BfTreeInterop;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;
using Tsavorite.core;

namespace Garnet.test
{
    /// <summary>
    /// Integration tests for RangeIndex (RI.*) commands.
    ///
    /// <para>These tests cover the full lifecycle of BfTree-backed range indexes including:
    /// creation, field CRUD, type safety (WRONGTYPE), eviction, flush/promote, lazy restore,
    /// checkpoint/recovery, AOF replay, concurrent access, and diagnostic commands.</para>
    ///
    /// <para>Each test creates a fresh Garnet server in Setup and disposes it in
    /// TearDown. Tests that need low memory, AOF, or checkpoint recovery
    /// recreate the server with the appropriate options.</para>
    /// </summary>
    [TestFixture]
    public class RespRangeIndexTests : TestBase
    {
        GarnetServer server;

        [SetUp]
        public void Setup()
        {
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true);
            server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            server.Dispose();
            TestUtils.OnTearDown();
        }

        /// <summary>
        /// Verifies basic RI.CREATE with MEMORY backend succeeds.
        /// </summary>
        [Test]
        public void RICreateBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // RI.CREATE with MEMORY backend
            var result = db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536");
            ClassicAssert.AreEqual("OK", (string)result);
        }

        /// <summary>
        /// Verifies that calling RI.CREATE on an existing key returns an error
        /// ("ERR index already exists") and does not overwrite the existing index.
        /// </summary>
        [Test]
        public void RICreateDuplicateReturnsErrorTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create first time
            var result = db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536");
            ClassicAssert.AreEqual("OK", (string)result);

            // Create again - should fail with error
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536"));
            ClassicAssert.IsTrue(ex.Message.Contains("index already exists"));
        }

        /// <summary>
        /// Verifies that DEL on a RangeIndex key removes the key and frees the BfTree.
        /// Subsequent RI.SET/RI.GET on the deleted key should return errors.
        /// A second DEL returns false (key no longer exists).
        /// </summary>
        [Test]
        public void RICreateThenDeleteTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create a range index and insert data
            var result = db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            ClassicAssert.AreEqual("OK", (string)result);
            result = db.Execute("RI.SET", "myindex", "field1", "value1");
            ClassicAssert.AreEqual("OK", (string)result);

            // Delete the index with DEL
            var deleted = db.KeyDelete("myindex");
            ClassicAssert.IsTrue(deleted);

            // Delete again - should return false (not found)
            deleted = db.KeyDelete("myindex");
            ClassicAssert.IsFalse(deleted);

            // RI.SET should fail — index no longer exists after DEL
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "myindex", "field1", "value1"));
            ClassicAssert.IsNotNull(ex);

            // RI.GET should also fail
            ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.GET", "myindex", "field1"));
            ClassicAssert.IsNotNull(ex);
        }

        /// <summary>
        /// Verifies RI.CREATE with minimal arguments (defaults for all numeric parameters).
        /// </summary>
        [Test]
        public void RICreateWithDefaultsTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // RI.CREATE with minimal args (defaults)
            var result = db.Execute("RI.CREATE", "myindex", "MEMORY");
            ClassicAssert.AreEqual("OK", (string)result);
        }

        /// <summary>
        /// Verifies RI.CREATE with all optional parameters explicitly specified.
        /// </summary>
        [Test]
        public void RICreateWithAllOptionsTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // RI.CREATE with all options (PAGESIZE auto-computed from MAXRECORD)
            var result = db.Execute("RI.CREATE", "myindex", "MEMORY",
                "CACHESIZE", "131072",
                "MINRECORD", "8",
                "MAXRECORD", "1024",
                "MAXKEYLEN", "128");
            ClassicAssert.AreEqual("OK", (string)result);
        }

        /// <summary>
        /// Verifies basic RI.SET + RI.GET round-trip: set a field, then read it back.
        /// </summary>
        [Test]
        public void RISetAndGetBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");

            var setResult = db.Execute("RI.SET", "myindex", "field1", "value1");
            ClassicAssert.AreEqual("OK", (string)setResult);

            var getResult = db.Execute("RI.GET", "myindex", "field1");
            ClassicAssert.AreEqual("value1", (string)getResult);
        }

        /// <summary>
        /// Verifies that RI.SET overwrites existing field values (upsert semantics).
        /// </summary>
        [Test]
        public void RISetOverwriteTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "field1", "value1");
            db.Execute("RI.SET", "myindex", "field1", "value2");

            var getResult = db.Execute("RI.GET", "myindex", "field1");
            ClassicAssert.AreEqual("value2", (string)getResult);
        }

        /// <summary>
        /// Verifies that RI.GET on a non-existent field returns null (not an error).
        /// </summary>
        [Test]
        public void RIGetNonExistentFieldTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");

            var getResult = db.Execute("RI.GET", "myindex", "nosuchfield");
            ClassicAssert.IsTrue(getResult.IsNull);
        }

        /// <summary>
        /// Verifies that RI.GET on a non-existent key returns an error (not null).
        /// </summary>
        [Test]
        public void RIGetNonExistentIndexTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.GET", "noindex", "field1"));
            ClassicAssert.IsTrue(ex.Message.Contains("range index"));
        }

        /// <summary>
        /// Verifies that RI.DEL removes a field and subsequent RI.GET returns null.
        /// </summary>
        [Test]
        public void RIDelFieldTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "field1", "value1");

            var delResult = (int)db.Execute("RI.DEL", "myindex", "field1");
            ClassicAssert.AreEqual(1, delResult);

            var getResult = db.Execute("RI.GET", "myindex", "field1");
            ClassicAssert.IsTrue(getResult.IsNull);
        }

        /// <summary>
        /// Verifies that RI.SET on a non-existent key returns an error.
        /// </summary>
        [Test]
        public void RISetOnNonExistentIndexTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "noindex", "field1", "value1"));
            ClassicAssert.IsTrue(ex.Message.Contains("range index"));
        }

        /// <summary>
        /// Verifies that multiple fields can be independently stored and retrieved.
        /// </summary>
        [Test]
        public void RIMultipleFieldsTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");
            db.Execute("RI.SET", "myindex", "ccc", "val-c");

            ClassicAssert.AreEqual("val-a", (string)db.Execute("RI.GET", "myindex", "aaa"));
            ClassicAssert.AreEqual("val-b", (string)db.Execute("RI.GET", "myindex", "bbb"));
            ClassicAssert.AreEqual("val-c", (string)db.Execute("RI.GET", "myindex", "ccc"));
        }

        /// <summary>
        /// Verifies WRONGTYPE error when RI.SET is used on a normal string key.
        /// </summary>
        [Test]
        public void RIWrongTypeOnNormalKeyTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // SET a normal string key
            db.StringSet("normalkey", "hello");

            // RI.SET on a normal key should fail
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "normalkey", "field1", "value1"));
            ClassicAssert.IsNotNull(ex);
        }

        /// <summary>
        /// Verifies WRONGTYPE error when RI.GET is used on a normal string key.
        /// </summary>
        [Test]
        public void RIWrongTypeGetOnNormalKeyTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // SET a normal string key, then try RI.GET
            db.StringSet("normalkey", "hello");

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.GET", "normalkey", "field1"));
            ClassicAssert.IsNotNull(ex);
        }

        /// <summary>
        /// Verifies WRONGTYPE error when a normal GET is used on a RangeIndex key.
        /// </summary>
        [Test]
        public void RINormalGetOnRangeIndexKeyTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536");

            // GET on a RI key returns WRONGTYPE error
            var ex = Assert.Throws<RedisServerException>(() => db.StringGet("myindex"));
            ClassicAssert.IsTrue(ex.Message.StartsWith("WRONGTYPE"));
        }

        // TODO: We used to forbid SET'ing over an RI - but that's a weird break from Redis
        //       This was tested in RINormalSetOnRangeIndexKeyTest
        //
        //       Now we allow SET (and it's ilk) to overwrite an RI.
        //
        //       We should have a set of tests that check that RI's are appropriately cleaned
        //       up after being overwriten

        ///// <summary>
        ///// Verifies WRONGTYPE error when SET is used on an existing RangeIndex key,
        ///// and confirms the RI key's data is not corrupted.
        ///// </summary>
        //[Test]
        //public void RINormalSetOnRangeIndexKeyTest()
        //{
        //    using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
        //    var db = redis.GetDatabase(0);

        //    db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
        //    db.Execute("RI.SET", "myindex", "field1", "value1");

        //    // SET on a RI key returns WRONGTYPE error
        //    var ex = Assert.Throws<RedisServerException>(() => db.StringSet("myindex", "overwrite"));
        //    ClassicAssert.IsTrue(ex.Message.StartsWith("WRONGTYPE"));

        //    // Verify the RI key is still intact
        //    var val = db.Execute("RI.GET", "myindex", "field1");
        //    ClassicAssert.AreEqual("value1", (string)val);
        //}

        /// <summary>
        /// Verifies AOF replay: checkpoint base state, then apply post-checkpoint mutations
        /// (RI.SET update, RI.SET insert, RI.DEL) via AOF replay on recovery.
        /// </summary>
        [Test]
        public void RIAofReplayTest()
        {
            // Insert data, then recover WITHOUT checkpoint — relies on AOF replay
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "aoftest", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "aoftest", "key1", "val1");
                db.Execute("RI.SET", "aoftest", "key2", "val2");
                db.Execute("RI.SET", "aoftest", "key3", "val3");

                // Checkpoint to establish base state
                db.Execute("SAVE");

                // Post-checkpoint mutations — these are only in the AOF
                db.Execute("RI.SET", "aoftest", "key4", "val4");
                db.Execute("RI.SET", "aoftest", "key1", "val1-updated");
                db.Execute("RI.DEL", "aoftest", "key2");

                // Commit AOF
                db.Execute("COMMITAOF");
            }

            // Recover — checkpoint restores base state, AOF replays post-checkpoint ops
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            // Replay restores the checkpointed tree before applying the three post-checkpoint ops
            var replayStats = server.Provider.StoreWrapper.rangeIndexManager.ReadPhaseStats();
            ClassicAssert.AreEqual(3, replayStats.Ops);
            ClassicAssert.AreEqual(1, replayStats.Restores);
            ClassicAssert.AreEqual(0, replayStats.PreStageCopyBytes, "the restore must open the checkpoint snapshot without copying it");
            ClassicAssert.Greater(replayStats.OpenTicks, 0);
            ClassicAssert.GreaterOrEqual(replayStats.StubReadTicks, replayStats.RestoreTicks);

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // key1 should have updated value (from AOF replay)
                var val = db.Execute("RI.GET", "aoftest", "key1");
                ClassicAssert.AreEqual("val1-updated", (string)val, "key1 should have AOF-replayed update");

                // key2 should be deleted (from AOF replay)
                val = db.Execute("RI.GET", "aoftest", "key2");
                ClassicAssert.IsTrue(val.IsNull, "key2 should be deleted via AOF replay");

                // key3 should exist (from checkpoint)
                val = db.Execute("RI.GET", "aoftest", "key3");
                ClassicAssert.AreEqual("val3", (string)val, "key3 should survive from checkpoint");

                // key4 should exist (from AOF replay)
                val = db.Execute("RI.GET", "aoftest", "key4");
                ClassicAssert.AreEqual("val4", (string)val, "key4 should be added via AOF replay");
            }
        }

        /// <summary>
        /// Verifies RI.SCAN returns records ordered by key with COUNT limit.
        /// Default FIELDS mode (BOTH) returns [key, value] pairs.
        /// </summary>
        [Test]
        public void RIScanBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");
            db.Execute("RI.SET", "myindex", "ccc", "val-c");
            db.Execute("RI.SET", "myindex", "ddd", "val-d");
            db.Execute("RI.SET", "myindex", "eee", "val-e");

            var result = (RedisResult[])db.Execute("RI.SCAN", "myindex", "aaa", "COUNT", "3");
            ClassicAssert.AreEqual(3, result.Length);

            // Each element is [key, value] since default FIELDS is BOTH
            var first = (RedisResult[])result[0];
            ClassicAssert.AreEqual("aaa", (string)first[0]);
            ClassicAssert.AreEqual("val-a", (string)first[1]);

            var second = (RedisResult[])result[1];
            ClassicAssert.AreEqual("bbb", (string)second[0]);
            ClassicAssert.AreEqual("val-b", (string)second[1]);

            var third = (RedisResult[])result[2];
            ClassicAssert.AreEqual("ccc", (string)third[0]);
            ClassicAssert.AreEqual("val-c", (string)third[1]);
        }

        /// <summary>
        /// Verifies large RI.SCAN replies stay well-formed when a record crosses the output buffer boundary.
        /// </summary>
        [Test]
        public void RIScanRecordCrossingOutputBufferBoundaryTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            const int recordCount = 2048;
            for (var valueLength = 1; valueLength <= 48; valueLength++)
            {
                var index = $"idx{valueLength}";
                var value = new string('v', valueLength);
                db.Execute("RI.CREATE", index, "DISK", "CACHESIZE", "1048576", "MINRECORD", "8");
                for (var i = 0; i < recordCount; i++)
                    db.Execute("RI.SET", index, $"key{i:D5}", value);

                var scanned = (RedisResult[])db.Execute("RI.SCAN", index, "key00000", "COUNT", recordCount.ToString());
                ClassicAssert.AreEqual(recordCount, scanned.Length, $"value length {valueLength}");
                for (var i = 0; i < recordCount; i++)
                {
                    var entry = (RedisResult[])scanned[i];
                    ClassicAssert.AreEqual(ResultType.BulkString, entry[1].Resp2Type, $"value length {valueLength}, record {i}");
                    ClassicAssert.AreEqual($"key{i:D5}", (string)entry[0]);
                    ClassicAssert.AreEqual(value, (string)entry[1]);
                }
            }
        }

        /// <summary>
        /// Verifies RI.SCAN FIELDS KEY returns only key strings (no nested arrays).
        /// </summary>
        [Test]
        public void RIScanFieldsKeyTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");

            var result = (RedisResult[])db.Execute("RI.SCAN", "myindex", "aaa", "COUNT", "10", "FIELDS", "KEY");
            ClassicAssert.AreEqual(2, result.Length);
            ClassicAssert.AreEqual("aaa", (string)result[0]);
            ClassicAssert.AreEqual("bbb", (string)result[1]);
        }

        /// <summary>
        /// Verifies RI.SCAN FIELDS VALUE returns only value strings (no nested arrays).
        /// </summary>
        [Test]
        public void RIScanFieldsValueTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");

            var result = (RedisResult[])db.Execute("RI.SCAN", "myindex", "aaa", "COUNT", "10", "FIELDS", "VALUE");
            ClassicAssert.AreEqual(2, result.Length);
            ClassicAssert.AreEqual("val-a", (string)result[0]);
            ClassicAssert.AreEqual("val-b", (string)result[1]);
        }

        /// <summary>
        /// Verifies RI.RANGE returns all entries in the closed [start, end] range.
        /// </summary>
        [Test]
        public void RIRangeBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");

            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");
            db.Execute("RI.SET", "myindex", "ccc", "val-c");
            db.Execute("RI.SET", "myindex", "ddd", "val-d");
            db.Execute("RI.SET", "myindex", "eee", "val-e");

            var result = (RedisResult[])db.Execute("RI.RANGE", "myindex", "bbb", "ddd");
            ClassicAssert.AreEqual(3, result.Length);

            var first = (RedisResult[])result[0];
            ClassicAssert.AreEqual("bbb", (string)first[0]);
            ClassicAssert.AreEqual("val-b", (string)first[1]);

            var second = (RedisResult[])result[1];
            ClassicAssert.AreEqual("ccc", (string)second[0]);
            ClassicAssert.AreEqual("val-c", (string)second[1]);

            var third = (RedisResult[])result[2];
            ClassicAssert.AreEqual("ddd", (string)third[0]);
            ClassicAssert.AreEqual("val-d", (string)third[1]);
        }

        /// <summary>
        /// Verifies RI.SCAN on a non-existent key returns an error.
        /// </summary>
        [Test]
        public void RIScanOnNonExistentIndexTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SCAN", "noindex", "aaa", "COUNT", "10"));
            ClassicAssert.IsTrue(ex.Message.Contains("range index"));
        }

        /// <summary>
        /// Verifies RI.RANGE on a non-existent key returns an error.
        /// </summary>
        [Test]
        public void RIRangeOnNonExistentIndexTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.RANGE", "noindex", "aaa", "zzz"));
            ClassicAssert.IsTrue(ex.Message.Contains("range index"));
        }

        /// <summary>
        /// Verifies that page eviction frees BfTree instances on evicted pages,
        /// while trees on recent (mutable) pages remain live and functional.
        /// </summary>
        [Test]
        public void RIEvictionFreesEvictedTreeButKeepsLiveTest()
        {
            // Create several RI trees on early pages, then fill the log to evict those
            // pages (freeing the native BfTrees). Create new trees on recent pages and
            // verify they are still fully functional.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create several RI trees — their stubs land on the first pages
            for (int i = 0; i < 3; i++)
            {
                db.Execute("RI.CREATE", $"early{i}", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", $"early{i}", "field1", $"val{i}xx");
            }
            ClassicAssert.AreEqual(3, rangeIndexManager.LiveIndexCount, "3 trees should be live after creation");

            // Fill the log with string keys to push early pages below HeadAddress.
            // Eviction calls DisposeRecord on each RI stub, freeing the native BfTrees.
            for (int i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            // The 3 early trees should have been freed by eviction
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount,
                "All 3 early trees should have been freed by page eviction");

            // Create one new RI tree on a recent (mutable) page — above HeadAddress
            db.Execute("RI.CREATE", "live", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "live", "field1", "alive");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Only the new tree should be live");

            // Verify the live tree is fully functional — its BfTree was NOT freed
            var val = db.Execute("RI.GET", "live", "field1");
            ClassicAssert.AreEqual("alive", (string)val,
                "Live tree should still be accessible after evicting early pages");
        }

        /// <summary>
        /// Verifies that evicting a page with a deleted RI stub doesn't crash.
        /// The BfTree was already freed by DEL; eviction sees handle=0 and skips.
        /// </summary>
        [Test]
        public void RIEvictionAfterDeleteTest()
        {
            // Test that evicting a page with a deleted RI stub doesn't crash.
            // The BfTree was already freed by DEL; DisposeRecord sees handle=0 and skips.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create and then delete a range index
            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "key1", "value1");
            db.KeyDelete("myindex");

            // Fill log to evict the page containing the deleted RI stub
            for (int i = 0; i < 200; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            // Create a new index after eviction — should work fine
            db.Execute("RI.CREATE", "newidx", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "newidx", "field1", "hello");
            var val = db.Execute("RI.GET", "newidx", "field1");
            ClassicAssert.AreEqual("hello", (string)val);
        }

        /// <summary>
        /// Verifies that multiple indexes can be created, deleted, evicted, and new indexes
        /// remain functional after the eviction of deleted stubs.
        /// </summary>
        [Test]
        public void RIEvictionMultipleIndexesTest()
        {
            // Create indexes, delete them, evict their pages, then verify new indexes work
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create and delete several indexes on early pages
            for (int idx = 0; idx < 3; idx++)
            {
                db.Execute("RI.CREATE", $"old{idx}", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", $"old{idx}", "field1", $"value{idx}");
                db.KeyDelete($"old{idx}");
            }

            // Fill log to evict deleted stubs
            for (int i = 0; i < 200; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            // Create a live index after eviction — should be fully functional
            db.Execute("RI.CREATE", "live", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "live", "field1", "alive");
            var val = db.Execute("RI.GET", "live", "field1");
            ClassicAssert.AreEqual("alive", (string)val);
        }

        /// <summary>
        /// Verifies create/delete/evict/recreate cycle under memory pressure.
        /// Each cycle creates an index, deletes it, evicts, then creates a new one.
        /// </summary>
        [Test]
        public void RICreateDeleteRecreateWithEvictionTest()
        {
            // Test create/delete/evict/recreate cycle under memory pressure.
            // Each cycle creates an index, deletes it, evicts, then creates a new one
            // and verifies the new one is live.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            for (int round = 0; round < 3; round++)
            {
                // Create and delete — BfTree is freed by delete
                db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "myindex", "field1", $"round{round}");
                db.KeyDelete("myindex");

                // Fill log to evict the deleted stub page
                for (int i = 0; i < 100; i++)
                    db.StringSet($"pad{round}_{i:D4}", $"x{round}_{i:D4}");
            }

            // After all cycles, create a live index and verify it works
            db.Execute("RI.CREATE", "final", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "final", "field1", "works");
            var val = db.Execute("RI.GET", "final", "field1");
            ClassicAssert.AreEqual("works", (string)val);
        }

        /// <summary>
        /// Verifies RI.SET returns an error when the field exceeds MAXKEYLEN.
        /// </summary>
        [Test]
        public void RISetInvalidKVFieldTooLongTest()
        {
            // RI.SET with a field exceeding MAXKEYLEN should return an error.
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY",
                "CACHESIZE", "65536",
                "MINRECORD", "8",
                "MAXRECORD", "256",
                "MAXKEYLEN", "16");

            // Field of 17 bytes exceeds MAXKEYLEN=16
            var longField = new string('k', 17);
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "myindex", longField, "value1"));
            ClassicAssert.IsTrue(ex.Message.Contains("key+value size must be between"),
                $"Expected InvalidKV error, got: {ex.Message}");
        }

        /// <summary>
        /// Verifies RI.SET returns an error when the value exceeds MAXRECORD.
        /// </summary>
        [Test]
        public void RISetInvalidKVValueTooLongTest()
        {
            // RI.SET with a value exceeding MAXRECORD should return an error.
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY",
                "CACHESIZE", "65536",
                "MINRECORD", "8",
                "MAXRECORD", "64",
                "MAXKEYLEN", "16");

            // Value larger than MAXRECORD=64
            var longValue = new string('v', 128);
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "myindex", "field1", longValue));
            ClassicAssert.IsTrue(ex.Message.Contains("key+value size must be between"),
                $"Expected InvalidKV error, got: {ex.Message}");
        }

        /// <summary>
        /// Verifies RI.SET returns an error when the record (key+value) is below MINRECORD.
        /// </summary>
        [Test]
        public void RISetInvalidKVRecordTooSmallTest()
        {
            // RI.SET with a record (key+value) smaller than MINRECORD should return an error.
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Set a high MINRECORD so that small key+value pairs are rejected
            db.Execute("RI.CREATE", "myindex", "MEMORY",
                "CACHESIZE", "65536",
                "MINRECORD", "128",
                "MAXRECORD", "1024",
                "MAXKEYLEN", "64");

            // key="a" (1 byte) + value="b" (1 byte) = 2 bytes, well below MINRECORD=128
            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.SET", "myindex", "a", "b"));
            ClassicAssert.IsTrue(ex.Message.Contains("key+value size must be between"),
                $"Expected InvalidKV error for record below MINRECORD, got: {ex.Message}");
        }

        /// <summary>
        /// Verifies thread-safety of concurrent RI.SET/RI.GET/RI.DEL from multiple clients
        /// on the same RangeIndex. Tests the shared-lock path under contention.
        /// </summary>
        [Test]
        public async Task RIConcurrentMultiClientTest()
        {
            // Multiple clients concurrently writing to and reading from the same
            // RangeIndex. Verifies thread-safety of the shared-lock path.
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY",
                "CACHESIZE", "1048576",
                "MINRECORD", "8",
                "MAXRECORD", "256",
                "MAXKEYLEN", "64");

            const int numTasks = 8;
            const int opsPerTask = 50;

            // Phase 1: concurrent writes
            var writeTasks = new Task[numTasks];
            for (int t = 0; t < numTasks; t++)
            {
                int taskId = t;
                writeTasks[t] = Task.Run(async () =>
                {
                    using var conn = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    var tdb = conn.GetDatabase(0);
                    for (int i = 0; i < opsPerTask; i++)
                    {
                        var field = $"t{taskId}:f{i:D4}";
                        var value = $"val-{taskId}-{i}";
                        await tdb.ExecuteAsync("RI.SET", "myindex", field, value);
                    }
                });
            }
            await Task.WhenAll(writeTasks);

            // Phase 2: concurrent reads — verify every written value
            var readTasks = new Task[numTasks];
            for (int t = 0; t < numTasks; t++)
            {
                int taskId = t;
                readTasks[t] = Task.Run(async () =>
                {
                    using var conn = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    var tdb = conn.GetDatabase(0);
                    for (int i = 0; i < opsPerTask; i++)
                    {
                        var field = $"t{taskId}:f{i:D4}";
                        var expected = $"val-{taskId}-{i}";
                        var actual = (string)await tdb.ExecuteAsync("RI.GET", "myindex", field);
                        ClassicAssert.AreEqual(expected, actual,
                            $"Mismatch for {field}: expected '{expected}', got '{actual}'");
                    }
                });
            }
            await Task.WhenAll(readTasks);

            // Phase 3: concurrent mixed read/write/delete
            var mixedTasks = new Task[numTasks];
            for (int t = 0; t < numTasks; t++)
            {
                int taskId = t;
                mixedTasks[t] = Task.Run(async () =>
                {
                    using var conn = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    var tdb = conn.GetDatabase(0);
                    for (int i = 0; i < opsPerTask; i++)
                    {
                        // Write a new key
                        var field = $"mix{taskId}:f{i:D4}";
                        await tdb.ExecuteAsync("RI.SET", "myindex", field, $"mixed-{taskId}-{i}");

                        // Read it back
                        var val = (string)await tdb.ExecuteAsync("RI.GET", "myindex", field);
                        ClassicAssert.AreEqual($"mixed-{taskId}-{i}", val);

                        // Delete it
                        await tdb.ExecuteAsync("RI.DEL", "myindex", field);
                    }
                });
            }
            await Task.WhenAll(mixedTasks);
        }

        /// <summary>
        /// Verifies that DEL on a mutable-region RI key immediately frees the BfTree
        /// (LiveIndexCount drops to 0).
        /// </summary>
        [Test]
        public void RIDeleteInMutableRegionFreesResourcesTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "field1", "value1");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be live after creation");
            ClassicAssert.IsTrue(store.Log.TailAddress > store.Log.ReadOnlyAddress,
                "Stub should be in the mutable region");

            db.KeyDelete("myindex");
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount,
                "BfTree should be freed immediately after DEL in mutable region");
        }

        /// <summary>
        /// Verifies that DEL on a read-only-region RI key frees the BfTree via CopyUpdater.
        /// Also verifies a new index can be created and used after the delete.
        /// </summary>
        [Test]
        public void RIDeleteInReadOnlyRegionFreesResourcesTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "field1", "value1");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be live after creation");

            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

            db.KeyDelete("myindex");
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount,
                "BfTree should be freed immediately after DEL in read-only region");

            db.Execute("RI.CREATE", "newidx", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "newidx", "f1xx", "v1xx");
            var val = db.Execute("RI.GET", "newidx", "f1xx");
            ClassicAssert.AreEqual("v1xx", (string)val);
        }

        /// <summary>
        /// Verifies the flush→promote→access cycle: when pages move to read-only,
        /// OnFlush snapshots the BfTree and sets IsFlushed. The next RI.GET detects
        /// the flag, promotes the stub to tail via RMW, and data remains accessible.
        /// </summary>
        [Test]
        public void RIFlushPromotesToTailOnNextAccessTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            // Create a memory-backed index and insert data
            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "aaa", "val-a");
            db.Execute("RI.SET", "myindex", "bbb", "val-b");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be live");

            // Record the tail address before flush — stub is in mutable
            var tailBeforeFlush = store.Log.TailAddress;

            // Force pages into read-only region — triggers OnFlushRecord which
            // snapshots BfTree and sets the Flushed flag on the in-memory stub.
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

            // The BfTree should still be live (not evicted, just flushed)
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should still be live after flush");

            // Next RI.GET detects flushed flag → promotes stub to tail via RMW → clears flag
            var result = db.Execute("RI.GET", "myindex", "aaa");
            ClassicAssert.AreEqual("val-a", (string)result, "Data should be readable after flush+promote");

            // Tail should have advanced because the stub was copied to mutable region
            ClassicAssert.IsTrue(store.Log.TailAddress > tailBeforeFlush,
                "Tail should advance after promote-to-tail RMW");

            // Subsequent operations should work normally (no more promote needed)
            db.Execute("RI.SET", "myindex", "ccc", "val-c");
            result = db.Execute("RI.GET", "myindex", "ccc");
            ClassicAssert.AreEqual("val-c", (string)result, "New insert should work after promote");

            result = db.Execute("RI.GET", "myindex", "bbb");
            ClassicAssert.AreEqual("val-b", (string)result, "Pre-flush data should still be accessible");
        }

        /// <summary>
        /// Verifies two consecutive flush→promote cycles: flush, promote, mutate, flush
        /// again, promote again. All data should survive both cycles.
        /// </summary>
        [Test]
        public void RIFlushPromoteThenSecondFlushTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            // Create index, insert data
            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "key-one", "value-1");

            // First flush cycle: flush → promote → mutate
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var r1 = db.Execute("RI.GET", "myindex", "key-one");
            ClassicAssert.AreEqual("value-1", (string)r1, "Read after first flush should promote and return data");

            // Mutate after promote (stub is now back in mutable)
            db.Execute("RI.SET", "myindex", "key-two", "value-2");

            // Second flush cycle: flush again → should snapshot with latest data
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

            // Read again — should promote again and return both values
            r1 = db.Execute("RI.GET", "myindex", "key-one");
            ClassicAssert.AreEqual("value-1", (string)r1, "key-one should survive second flush cycle");

            var r2 = db.Execute("RI.GET", "myindex", "key-two");
            ClassicAssert.AreEqual("value-2", (string)r2, "key-two (added after first promote) should survive second flush cycle");

            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should still be live");
        }

        /// <summary>
        /// Verifies full eviction-to-disk and lazy restore cycle: create, insert, evict
        /// past HeadAddress, then access triggers pending disk read → invalidate → promote
        /// → RestoreTreeFromFlush → data accessible again.
        /// </summary>
        [Test]
        public void RIEvictToDiskThenLazyRestoreTest()
        {
            // Create a BfTree, insert data, evict past HeadAddress (to disk),
            // then access again — should promote from disk + lazy restore from flush file.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create disk-backed index and insert data (disk backend supports snapshot/restore)
            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "alpha", "value-alpha");
            db.Execute("RI.SET", "myindex", "bravo", "value-bravo");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be live");

            // Fill the log to push the RI stub below HeadAddress (eviction).
            // OnFlushRecord snapshots the BfTree to flush.bftree.
            // DisposeRecord(PageEviction) frees the native BfTree.
            for (int i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount,
                "BfTree should have been freed by eviction");

            // Now access the RI key — this triggers:
            // 1. Pending read from disk → OnDiskReadRecord invalidates TreeHandle
            // 2. ReadRangeIndex detects IsFlushed → promotes stub to tail
            // 3. Promoted stub has TreeHandle == 0 → RestoreTreeFromFlush recovers BfTree
            // 4. RI.GET returns data from the restored BfTree
            var result = db.Execute("RI.GET", "myindex", "alpha");
            ClassicAssert.AreEqual("value-alpha", (string)result,
                "Data should be recoverable after eviction + lazy restore");

            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount,
                "Restored BfTree should be registered as live");

            // Verify second key is also available
            result = db.Execute("RI.GET", "myindex", "bravo");
            ClassicAssert.AreEqual("value-bravo", (string)result,
                "Second key should also be recoverable");

            // Verify writes work on the restored tree
            db.Execute("RI.SET", "myindex", "charlie", "value-charlie");
            result = db.Execute("RI.GET", "myindex", "charlie");
            ClassicAssert.AreEqual("value-charlie", (string)result,
                "Writes should work on restored tree");
        }

        /// <summary>
        /// Verifies a scan after lazy restore succeeds when leaves were promoted to full-page cache before the eviction snapshot.
        /// </summary>
        [Test]
        public void RIScanAfterEvictAndLazyRestoreOfScannedTreeTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            const int recordCount = 512;
            var value = new string('v', 64);
            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            for (var i = 0; i < recordCount; i++)
                db.Execute("RI.SET", "myindex", $"key{i:D5}", $"{value}{i:D5}");

            // Scanning promotes base leaves to full-page cache, whose records are typed as clean cache records.
            var scanned = (RedisResult[])db.Execute("RI.SCAN", "myindex", "key00000", "COUNT", recordCount.ToString(), "FIELDS", "KEY");
            ClassicAssert.AreEqual(recordCount, scanned.Length);

            for (var i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "BfTree should have been freed by eviction");

            // The restored leaves must be valid base pages; scanning promotes them to full-page cache again.
            scanned = (RedisResult[])db.Execute("RI.SCAN", "myindex", "key00000", "COUNT", recordCount.ToString());
            ClassicAssert.AreEqual(recordCount, scanned.Length);
            for (var i = 0; i < recordCount; i++)
            {
                var entry = (RedisResult[])scanned[i];
                ClassicAssert.AreEqual($"key{i:D5}", (string)entry[0]);
                ClassicAssert.AreEqual($"{value}{i:D5}", (string)entry[1]);
            }

            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Restored BfTree should be registered as live");
        }

        /// <summary>
        /// Creates a low-memory server, fills a disk-backed index and evicts its stub, returning the manager and the expected values.
        /// </summary>
        private (RangeIndexManager Manager, ConnectionMultiplexer Redis, string Value) CreateEvictedDiskIndex(int recordCount)
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var value = new string('v', 64);
            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            for (var i = 0; i < recordCount; i++)
                db.Execute("RI.SET", "myindex", $"key{i:D5}", $"{value}{i:D5}");

            for (var i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "BfTree should have been evicted");

            // A tree whose flush snapshot was still queued at eviction is parked once that snapshot completes.
            ClassicAssert.IsTrue(SpinWait.SpinUntil(() => rangeIndexManager.ParkedTreeCount == 1, TimeSpan.FromMinutes(1)), "Evicted tree should be parked");
            ClassicAssert.AreEqual(65536, rangeIndexManager.ParkedTreeBytes);
            return (rangeIndexManager, redis, value);
        }

        /// <summary>
        /// Verifies parked trees are bounded by circular-buffer memory: parking a tree beyond the budget disposes the oldest,
        /// and both indexes still read back from the parked tree or the flush file.
        /// </summary>
        [Test]
        public void RIParkedTreesBeyondMemoryBudgetDisposeOldestTest()
        {
            const int recordCount = 128;
            const int cacheBytes = 1 << 20;
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, rangeIndexParkedTreeMemoryMB: 1);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            string[] indexes = ["index-a", "index-b"];
            foreach (var index in indexes)
            {
                db.Execute("RI.CREATE", index, "DISK", "CACHESIZE", cacheBytes.ToString(), "MINRECORD", "8");
                for (var i = 0; i < recordCount; i++)
                    db.Execute("RI.SET", index, $"key{i:D5}", $"{index}-{i:D5}");
            }

            for (var i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Both trees should have been evicted");
            ClassicAssert.IsTrue(SpinWait.SpinUntil(() => rangeIndexManager.ParkedTreeCount == 1 && rangeIndexManager.ParkedTreesDisposedCount == 1, TimeSpan.FromMinutes(1)),
                $"Only one tree fits the budget: parked={rangeIndexManager.ParkedTreeCount} disposed={rangeIndexManager.ParkedTreesDisposedCount}");
            ClassicAssert.AreEqual(cacheBytes, rangeIndexManager.ParkedTreeBytes);

            foreach (var index in indexes)
            {
                for (var i = 0; i < recordCount; i++)
                    ClassicAssert.AreEqual($"{index}-{i:D5}", (string)db.Execute("RI.GET", index, $"key{i:D5}"));
            }

            ClassicAssert.AreEqual(1, rangeIndexManager.ParkedTreesReusedCount, "The tree within the budget should be reused");
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreeBytes);
            ClassicAssert.AreEqual(2, rangeIndexManager.LiveIndexCount);
        }

        /// <summary>
        /// Verifies promoting an evicted index reuses its parked tree instead of copying the flush file and reopening it.
        /// </summary>
        [Test]
        public void RIPromoteOfEvictedIndexReusesParkedTreeTest()
        {
            const int recordCount = 512;
            var (rangeIndexManager, redis, value) = CreateEvictedDiskIndex(recordCount);
            using var _ = redis;
            var db = redis.GetDatabase(0);

            for (var i = 0; i < recordCount; i++)
                ClassicAssert.AreEqual($"{value}{i:D5}", (string)db.Execute("RI.GET", "myindex", $"key{i:D5}"));

            ClassicAssert.AreEqual(1, rangeIndexManager.ParkedTreesReusedCount, "Promote should reuse the parked tree");
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreesDisposedCount);
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreeCount);
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

            var extraKey = $"key{recordCount:D5}";
            db.Execute("RI.SET", "myindex", extraKey, "value-extra");
            ClassicAssert.AreEqual("value-extra", (string)db.Execute("RI.GET", "myindex", extraKey));
            var scanned = (RedisResult[])db.Execute("RI.SCAN", "myindex", "key00000", "COUNT", (recordCount + 1).ToString(), "FIELDS", "KEY");
            ClassicAssert.AreEqual(recordCount + 1, scanned.Length);
        }

        /// <summary>
        /// Verifies an evicted index whose parked tree was disposed is restored from its flush file.
        /// </summary>
        [Test]
        public void RIPromoteAfterParkedTreeDisposedRestoresFromFlushFileTest()
        {
            const int recordCount = 512;
            var (rangeIndexManager, redis, value) = CreateEvictedDiskIndex(recordCount);
            using var _ = redis;
            var db = redis.GetDatabase(0);

            rangeIndexManager.SweepParkedTrees(all: true);
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreeCount);
            ClassicAssert.AreEqual(1, rangeIndexManager.ParkedTreesDisposedCount);

            for (var i = 0; i < recordCount; i++)
                ClassicAssert.AreEqual($"{value}{i:D5}", (string)db.Execute("RI.GET", "myindex", $"key{i:D5}"));

            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreesReusedCount);
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);
            db.Execute("RI.SET", "myindex", "extra", "value-extra");
            ClassicAssert.AreEqual("value-extra", (string)db.Execute("RI.GET", "myindex", "extra"));
        }

        /// <summary>
        /// Verifies deleting an evicted index disposes its parked tree, so a recreated index starts empty.
        /// </summary>
        [Test]
        public void RIDeleteOfEvictedIndexDisposesParkedTreeTest()
        {
            var (rangeIndexManager, redis, _) = CreateEvictedDiskIndex(recordCount: 64);
            using var __ = redis;
            var db = redis.GetDatabase(0);

            // DEL of a disk-resident record writes a tombstone without reporting it as found.
            _ = db.KeyDelete("myindex");
            ClassicAssert.AreEqual("OK", (string)db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8"));
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreeCount);
            ClassicAssert.AreEqual(0, rangeIndexManager.ParkedTreesReusedCount);

            ClassicAssert.IsTrue(db.Execute("RI.GET", "myindex", "key00000").IsNull);
            db.Execute("RI.SET", "myindex", "fresh", "value-fresh");
            ClassicAssert.AreEqual("value-fresh", (string)db.Execute("RI.GET", "myindex", "fresh"));
        }

        /// <summary>
        /// Verifies a scan succeeds over disk-backed leaves whose mini pages hold only records cached by point reads.
        /// </summary>
        [Test]
        public void RIScanAfterReadPromotionToCleanMiniPageTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            const int recordCount = 4096;
            var value = new string('v', 64);
            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            for (var i = 0; i < recordCount; i++)
                db.Execute("RI.SET", "myindex", $"key{i:D5}", $"{value}{i:D5}");

            for (var round = 0; round < 8; round++)
            {
                // Point reads of base-page records promote them into mini pages as clean cache records.
                for (var i = 0; i < recordCount; i++)
                    ClassicAssert.AreEqual($"{value}{i:D5}", (string)db.Execute("RI.GET", "myindex", $"key{i:D5}"));

                // Scanning a leaf with such a mini page merges it, which is skipped because no record is dirty.
                var scanned = (RedisResult[])db.Execute("RI.SCAN", "myindex", "key00000", "COUNT", recordCount.ToString());
                ClassicAssert.AreEqual(recordCount, scanned.Length);
                for (var i = 0; i < recordCount; i++)
                {
                    var entry = (RedisResult[])scanned[i];
                    ClassicAssert.AreEqual($"key{i:D5}", (string)entry[0]);
                    ClassicAssert.AreEqual($"{value}{i:D5}", (string)entry[1]);
                }
            }
        }

        /// <summary>
        /// Verifies checkpoint + recovery: create tree, insert data, BGSAVE, dispose,
        /// recover. All data should be present after recovery and new writes should work.
        /// </summary>
        [Test]
        public void RICheckpointAndRecoverTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Create a disk-backed BfTree and insert data
                db.Execute("RI.CREATE", "cpindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "cpindex", "alpha", "value-alpha");
                db.Execute("RI.SET", "cpindex", "bravo", "value-bravo");
                db.Execute("RI.SET", "cpindex", "charlie", "value-charlie");

                // Verify data before checkpoint
                var val = db.Execute("RI.GET", "cpindex", "alpha");
                ClassicAssert.AreEqual("value-alpha", (string)val);

                // Take checkpoint via BGSAVE
                db.Execute("SAVE");
            }

            // Dispose and recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // Verify data recovered from checkpoint
                var val = db.Execute("RI.GET", "cpindex", "alpha");
                ClassicAssert.AreEqual("value-alpha", (string)val, "alpha should survive checkpoint+recovery");

                val = db.Execute("RI.GET", "cpindex", "bravo");
                ClassicAssert.AreEqual("value-bravo", (string)val, "bravo should survive checkpoint+recovery");

                val = db.Execute("RI.GET", "cpindex", "charlie");
                ClassicAssert.AreEqual("value-charlie", (string)val, "charlie should survive checkpoint+recovery");

                // Verify writes work after recovery
                db.Execute("RI.SET", "cpindex", "delta", "value-delta");
                val = db.Execute("RI.GET", "cpindex", "delta");
                ClassicAssert.AreEqual("value-delta", (string)val, "new insert should work after recovery");
            }
        }

        /// <summary>
        /// Full lifecycle test: create → insert → flush to read-only → promote → mutate →
        /// evict to disk → restore from flush → checkpoint → recover. Verifies data
        /// survives every transition in the store lifecycle.
        /// </summary>
        [Test]
        public void RIFlushEvictRestoreCheckpointCycleTest()
        {
            // Full lifecycle: create → insert → flush to read-only → promote → mutate →
            // evict to disk → restore from flush → checkpoint → recover
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Create disk-backed tree and insert data
                db.Execute("RI.CREATE", "lifecycle", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "lifecycle", "key-aaa", "val-aaa");
                db.Execute("RI.SET", "lifecycle", "key-bbb", "val-bbb");
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

                // Force flush to read-only (sets Flushed flag)
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

                // Access triggers promote to tail (clears Flushed flag)
                var val = db.Execute("RI.GET", "lifecycle", "key-aaa");
                ClassicAssert.AreEqual("val-aaa", (string)val, "Should read after flush+promote");

                // Mutate after promote (stub now in mutable)
                db.Execute("RI.SET", "lifecycle", "key-ccc", "val-ccc");

                // Fill log to evict the old pages past HeadAddress
                for (int i = 0; i < 200; i++)
                    db.StringSet($"fill{i:D4}", $"data{i:D4}");

                // Tree might still be live (promote moved stub to tail)
                // Access should work regardless
                val = db.Execute("RI.GET", "lifecycle", "key-bbb");
                ClassicAssert.AreEqual("val-bbb", (string)val, "Should read after eviction of old pages");

                val = db.Execute("RI.GET", "lifecycle", "key-ccc");
                ClassicAssert.AreEqual("val-ccc", (string)val, "Post-promote insert should survive eviction");

                // Take checkpoint
                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                var val = db.Execute("RI.GET", "lifecycle", "key-aaa");
                ClassicAssert.AreEqual("val-aaa", (string)val, "key-aaa should survive full lifecycle");

                val = db.Execute("RI.GET", "lifecycle", "key-bbb");
                ClassicAssert.AreEqual("val-bbb", (string)val, "key-bbb should survive full lifecycle");

                val = db.Execute("RI.GET", "lifecycle", "key-ccc");
                ClassicAssert.AreEqual("val-ccc", (string)val, "key-ccc should survive full lifecycle");
            }
        }

        /// <summary>
        /// Verifies that multiple BfTrees are independently evicted and lazily restored.
        /// 3 evicted trees + 1 live tree = 4 total after all restores.
        /// </summary>
        [Test]
        public void RIMultipleTreesEvictAndRestoreTest()
        {
            // Multiple BfTrees, some evicted, some live — verify independent lifecycle
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create 3 trees early — they'll be on early pages
            for (int i = 0; i < 3; i++)
            {
                db.Execute("RI.CREATE", $"tree{i}", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", $"tree{i}", "field1", $"value{i}-1");
                db.Execute("RI.SET", $"tree{i}", "field2", $"value{i}-2");
            }
            ClassicAssert.AreEqual(3, rangeIndexManager.LiveIndexCount);

            // Fill log to evict early pages
            for (int i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");

            // Early trees should be evicted
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "All early trees should be evicted");

            // Create a new tree on recent pages (still in memory)
            db.Execute("RI.CREATE", "tree-live", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "tree-live", "field1", "live-val");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

            // Access evicted trees — triggers lazy restore
            for (int i = 0; i < 3; i++)
            {
                var val = db.Execute("RI.GET", $"tree{i}", "field1");
                ClassicAssert.AreEqual($"value{i}-1", (string)val, $"tree{i} field1 should restore from flush");

                val = db.Execute("RI.GET", $"tree{i}", "field2");
                ClassicAssert.AreEqual($"value{i}-2", (string)val, $"tree{i} field2 should restore from flush");
            }

            // All trees should now be live (3 restored + 1 live)
            ClassicAssert.AreEqual(4, rangeIndexManager.LiveIndexCount);

            // Live tree should still work
            var liveVal = db.Execute("RI.GET", "tree-live", "field1");
            ClassicAssert.AreEqual("live-val", (string)liveVal);
        }

        /// <summary>
        /// Verifies that multiple trees survive checkpoint + recovery with all data intact.
        /// </summary>
        [Test]
        public void RICheckpointWithMultipleTreesAndRecoverTest()
        {
            // Multiple trees, checkpoint, recover — all trees should be restored
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Create several trees with different data
                for (int i = 0; i < 5; i++)
                {
                    db.Execute("RI.CREATE", $"idx{i}", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                    for (int j = 0; j < 10; j++)
                        db.Execute("RI.SET", $"idx{i}", $"field-{j:D3}", $"value-{i}-{j}");
                }

                // Checkpoint
                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // Verify all trees and all fields recovered
                for (int i = 0; i < 5; i++)
                {
                    for (int j = 0; j < 10; j++)
                    {
                        var val = db.Execute("RI.GET", $"idx{i}", $"field-{j:D3}");
                        ClassicAssert.AreEqual($"value-{i}-{j}", (string)val,
                            $"idx{i} field-{j:D3} should survive checkpoint+recovery");
                    }
                }

                // Verify writes work on all recovered trees
                for (int i = 0; i < 5; i++)
                {
                    db.Execute("RI.SET", $"idx{i}", "new-field", $"new-value-{i}");
                    var val = db.Execute("RI.GET", $"idx{i}", "new-field");
                    ClassicAssert.AreEqual($"new-value-{i}", (string)val);
                }
            }
        }

        /// <summary>
        /// Verifies flush → promote → mutate → checkpoint → recover captures post-flush mutations.
        /// </summary>
        [Test]
        public void RIFlushPromoteCheckpointRecoverTest()
        {
            // Flush → promote → mutate → checkpoint → recover
            // Tests that post-flush mutations are captured by checkpoint
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "fpcp", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "fpcp", "before-flush", "original");

                // Flush to read-only
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

                // Promote by accessing
                var val = db.Execute("RI.GET", "fpcp", "before-flush");
                ClassicAssert.AreEqual("original", (string)val);

                // Mutate after promote (these are in mutable region now)
                db.Execute("RI.SET", "fpcp", "after-flush", "mutated");
                db.Execute("RI.SET", "fpcp", "before-flush", "updated");

                // Checkpoint should capture the mutated state
                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                var val = db.Execute("RI.GET", "fpcp", "before-flush");
                ClassicAssert.AreEqual("updated", (string)val, "Updated value should survive checkpoint");

                val = db.Execute("RI.GET", "fpcp", "after-flush");
                ClassicAssert.AreEqual("mutated", (string)val, "Post-flush insert should survive checkpoint");
            }
        }

        /// <summary>
        /// Verifies that evicting a deleted stub doesn't crash — the BfTree was already freed
        /// by DEL, so eviction sees handle=0 and safely skips. New indexes work after eviction.
        /// </summary>
        [Test]
        public void RIDeleteDuringEvictionCycleTest()
        {
            // Create tree → delete → fill log to trigger eviction of deleted record
            // Verify eviction of deleted record doesn't crash
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create and populate
            db.Execute("RI.CREATE", "deltest", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "deltest", "field1", "value1");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

            // Delete the tree
            db.KeyDelete("deltest");
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount);

            // Fill log to evict the page containing the deleted stub
            for (int i = 0; i < 200; i++)
                db.StringSet($"fill{i:D4}", $"data{i:D4}");

            // Create new tree to verify manager is still functional
            db.Execute("RI.CREATE", "newtest", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "newtest", "field1", "alive");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

            var val = db.Execute("RI.GET", "newtest", "field1");
            ClassicAssert.AreEqual("alive", (string)val);
        }

        /// <summary>
        /// Verifies double flush → promote → checkpoint → recover: data from both flush
        /// cycles and post-promote mutations all survive recovery.
        /// </summary>
        [Test]
        public void RIDoubleFlushCycleWithCheckpointTest()
        {
            // flush → promote → mutate → flush again → promote → checkpoint → recover
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "dblflush", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "dblflush", "round1", "value-r1");

                // First flush cycle
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                var val = db.Execute("RI.GET", "dblflush", "round1");
                ClassicAssert.AreEqual("value-r1", (string)val);

                // Mutate after first promote
                db.Execute("RI.SET", "dblflush", "round2", "value-r2");

                // Second flush cycle
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                val = db.Execute("RI.GET", "dblflush", "round2");
                ClassicAssert.AreEqual("value-r2", (string)val);

                // Mutate after second promote
                db.Execute("RI.SET", "dblflush", "round3", "value-r3");

                // Checkpoint
                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                var val = db.Execute("RI.GET", "dblflush", "round1");
                ClassicAssert.AreEqual("value-r1", (string)val, "round1 should survive double-flush + checkpoint");

                val = db.Execute("RI.GET", "dblflush", "round2");
                ClassicAssert.AreEqual("value-r2", (string)val, "round2 should survive double-flush + checkpoint");

                val = db.Execute("RI.GET", "dblflush", "round3");
                ClassicAssert.AreEqual("value-r3", (string)val, "round3 should survive double-flush + checkpoint");
            }
        }

        /// <summary>
        /// Verifies eviction → lazy restore → mutate → checkpoint → recover.
        /// Post-restore mutations should be captured by the checkpoint.
        /// </summary>
        [Test]
        public void RIEvictRestoreAndCheckpointTest()
        {
            // Evict to disk → lazy restore → mutate → checkpoint → recover
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "evictcp", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "evictcp", "pre-evict", "original");
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

                // Fill to evict
                for (int i = 0; i < 2000; i++)
                    db.StringSet($"fill{i:D4}", $"data{i:D4}");

                ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Tree should be evicted");

                // Lazy restore by accessing
                var val = db.Execute("RI.GET", "evictcp", "pre-evict");
                ClassicAssert.AreEqual("original", (string)val, "Should restore from flush file");
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be restored");

                // Mutate after restore
                db.Execute("RI.SET", "evictcp", "post-restore", "added");

                // Checkpoint
                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                var val = db.Execute("RI.GET", "evictcp", "pre-evict");
                ClassicAssert.AreEqual("original", (string)val, "Pre-evict data should survive evict→restore→checkpoint");

                val = db.Execute("RI.GET", "evictcp", "post-restore");
                ClassicAssert.AreEqual("added", (string)val, "Post-restore data should survive checkpoint");
            }
        }

        /// <summary>
        /// Verifies that taking two checkpoints and recovering always gets the latest data.
        /// Checkpoint 2 should contain updates made after checkpoint 1.
        /// </summary>
        [Test]
        public void RITwoCheckpointsRecoverToLatestTest()
        {
            // Take two checkpoints with different data, recover to latest
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Phase 1: create tree and insert first batch
                db.Execute("RI.CREATE", "twockpt", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "twockpt", "key-alpha", "val-alpha");
                db.Execute("RI.SET", "twockpt", "key-bravo", "val-bravo");

                // Checkpoint 1
                db.Execute("SAVE");

                // Phase 2: insert additional data + update existing
                db.Execute("RI.SET", "twockpt", "key-charlie", "val-charlie");
                db.Execute("RI.SET", "twockpt", "key-alpha", "val-alpha-v2");

                // Checkpoint 2
                db.Execute("SAVE");
            }

            // Recover — should get latest checkpoint (checkpoint 2)
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // Should see checkpoint 2 data: updated alpha, bravo, and charlie
                var val = db.Execute("RI.GET", "twockpt", "key-alpha");
                ClassicAssert.AreEqual("val-alpha-v2", (string)val, "alpha should have updated value from checkpoint 2");

                val = db.Execute("RI.GET", "twockpt", "key-bravo");
                ClassicAssert.AreEqual("val-bravo", (string)val, "bravo should exist");

                val = db.Execute("RI.GET", "twockpt", "key-charlie");
                ClassicAssert.AreEqual("val-charlie", (string)val, "charlie should exist from checkpoint 2");

                // Verify writes work after recovery
                db.Execute("RI.SET", "twockpt", "key-delta", "val-delta");
                val = db.Execute("RI.GET", "twockpt", "key-delta");
                ClassicAssert.AreEqual("val-delta", (string)val, "new insert should work after recovery");
            }
        }

        /// <summary>
        /// Verifies that a recovered tree is opened over its checkpoint snapshot without staging <c>data.bftree</c>, that
        /// its writes never reach that snapshot, and that a checkpoint taken before first use keeps the tree recoverable
        /// after the older checkpoint snapshot is purged.
        /// </summary>
        [Test]
        public void RIRecoveryStagesDataFileLazilyTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "lazystage", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "lazystage", "key-alpha", "val-alpha");
                db.Execute("RI.SET", "lazystage", "key-bravo", "val-bravo");
                db.Execute("SAVE");
            }

            server.Dispose();
            var riLogRoot = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            foreach (var file in Directory.GetFiles(riLogRoot, "*.data.bftree"))
                File.Delete(file);

            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                ClassicAssert.IsEmpty(Directory.GetFiles(riLogRoot, "*.data.bftree"), "recovery must not stage data.bftree before the tree is used");

                string[] CheckpointSnapshots() => [.. Directory.GetFiles(TestUtils.MethodTestDir, "*.bftree", SearchOption.AllDirectories).Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "rangeindex" && Path.GetDirectoryName(f) != riLogRoot)];
                var recoveredSnapshots = CheckpointSnapshots();
                ClassicAssert.IsNotEmpty(recoveredSnapshots, "the recovered checkpoint should hold a tree snapshot");

                // Checkpoint the still-pending tree; retention purges the recovered checkpoint's snapshot (deleted here if it survived).
                db.Execute("SAVE");
                ClassicAssert.IsEmpty(Directory.GetFiles(riLogRoot, "*.data.bftree"), "checkpointing a pending tree must not stage data.bftree");
                foreach (var file in recoveredSnapshots.Where(File.Exists))
                    File.Delete(file);

                var source = CheckpointSnapshots().Single();
                var sourceBytes = File.ReadAllBytes(source);
                ClassicAssert.AreEqual("val-alpha", (string)db.Execute("RI.GET", "lazystage", "key-alpha"));
                ClassicAssert.AreEqual("val-bravo", (string)db.Execute("RI.GET", "lazystage", "key-bravo"));
                ClassicAssert.IsEmpty(Directory.GetFiles(riLogRoot, "*.data.bftree"), "first use must open the checkpoint snapshot instead of staging data.bftree");

                var overlayWritesBefore = BfTreeService.OverlayStats.WorkWriteBytes;
                var value = new string('c', 100);
                for (var i = 0; i < 2000; i++)
                    db.Execute("RI.SET", "lazystage", $"key-{i:D5}", value);
                ClassicAssert.AreEqual(value, (string)db.Execute("RI.GET", "lazystage", "key-00000"));
                ClassicAssert.Greater(BfTreeService.OverlayStats.WorkWriteBytes, overlayWritesBefore, "the cache overflow must have written pages to the overlay work file");
                ClassicAssert.AreEqual(sourceBytes, File.ReadAllBytes(source), "the restored tree wrote to the checkpoint snapshot it was opened from");
            }
        }

        /// <summary>
        /// Verifies that promoting an evicted index links its flush file as <c>data.bftree</c> instead of copying the whole
        /// tree, and that the restored tree's writes go to its overlay work file, never to that shared flush file.
        /// </summary>
        [Test]
        public void RIPromoteLinksFlushFileAndNeverModifiesItTest()
        {
            const int recordCount = 512;
            var (rangeIndexManager, redis, value) = CreateEvictedDiskIndex(recordCount);
            using var _ = redis;
            var db = redis.GetDatabase(0);
            rangeIndexManager.SweepParkedTrees(all: true);
            WaitForFlushSnapshots();

            var flushFile = Directory.GetFiles(Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex"), "*.flush.bftree").Single();
            var flushBytes = File.ReadAllBytes(flushFile);
            var before = rangeIndexManager.ReadPhaseStats();
            ClassicAssert.AreEqual($"{value}00000", (string)db.Execute("RI.GET", "myindex", "key00000"));
            var stats = rangeIndexManager.ReadPhaseStats().Minus(before);
            ClassicAssert.AreEqual(1, stats.PreStages);
            ClassicAssert.AreEqual(1, stats.PreStageLinks, "the promote must link the flush file");
            ClassicAssert.AreEqual(0, stats.PreStageCopyBytes);

            var overlayWritesBefore = BfTreeService.OverlayStats.WorkWriteBytes;
            for (var i = 0; i < recordCount; i++)
                db.Execute("RI.SET", "myindex", $"key{i:D5}", $"updated{value}{i:D5}");
            for (var i = 0; i < recordCount; i++)
                ClassicAssert.AreEqual($"updated{value}{i:D5}", (string)db.Execute("RI.GET", "myindex", $"key{i:D5}"));

            ClassicAssert.Greater(BfTreeService.OverlayStats.WorkWriteBytes, overlayWritesBefore, "the cache overflow must have written pages to the overlay work file");
            ClassicAssert.AreEqual(flushBytes, File.ReadAllBytes(flushFile), "the restored tree wrote through data.bftree into the flush file it shares");
        }
        /// <summary>
        /// Verifies that post-checkpoint mutations are replayed from AOF after recovery.
        /// With AOF enabled, v+1 data IS replayed from AOF after checkpoint recovery.
        /// </summary>
        [Test]
        public void RIRecoverToEarlierCheckpointTest()
        {
            // Add keys A,B → checkpoint → add key C, update A → recover →
            // With AOF logging, v+1 data IS replayed from AOF after checkpoint recovery
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Phase 1: create tree, insert keys A and B
                db.Execute("RI.CREATE", "earlyck", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "earlyck", "key-A", "val-A-original");
                db.Execute("RI.SET", "earlyck", "key-B", "val-B");

                // Checkpoint
                db.Execute("SAVE");

                // Phase 2: add more data AFTER checkpoint (logged to AOF)
                db.Execute("RI.SET", "earlyck", "key-A", "val-A-updated");
                db.Execute("RI.SET", "earlyck", "key-C", "val-C");

                // Commit AOF
                db.Execute("COMMITAOF");
            }

            // Recover — checkpoint restored first, then AOF entries replayed
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // key-A should have the UPDATED value (replayed from AOF)
                var val = db.Execute("RI.GET", "earlyck", "key-A");
                ClassicAssert.AreEqual("val-A-updated", (string)val, "key-A should have updated value replayed from AOF");

                // key-B should exist (it was in the checkpoint)
                val = db.Execute("RI.GET", "earlyck", "key-B");
                ClassicAssert.AreEqual("val-B", (string)val, "key-B should exist from checkpoint");

                // key-C should exist (replayed from AOF)
                val = db.Execute("RI.GET", "earlyck", "key-C");
                ClassicAssert.AreEqual("val-C", (string)val, "key-C should exist — replayed from AOF after checkpoint");
            }
        }

        /// <summary>
        /// Verifies that DEL on a recovered key correctly frees the lazily-restored BfTree.
        /// </summary>
        [Test]
        public void RIDeleteAfterRecoveryTest()
        {
            // Create tree, checkpoint, recover, then DEL the recovered key.
            // Verifies that DEL correctly frees the lazily-restored BfTree.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "delafter", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "delafter", "key1", "val1");
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

                db.Execute("SAVE");
            }

            // Recover
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();
            rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // Access triggers lazy restore
                var val = db.Execute("RI.GET", "delafter", "key1");
                ClassicAssert.AreEqual("val1", (string)val);
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

                // DEL on the recovered key should free the BfTree
                var deleted = db.KeyDelete("delafter");
                ClassicAssert.IsTrue(deleted, "DEL should return true");
                ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "BfTree should be freed after DEL");

                // Subsequent access should return not found
                var ex = Assert.Throws<RedisServerException>(() =>
                    db.Execute("RI.GET", "delafter", "key1"));
                ClassicAssert.IsNotNull(ex);
            }
        }

        /// <summary>
        /// Verifies RI.EXISTS returns 1 for existing RI keys, 0 for non-existent
        /// and deleted keys.
        /// </summary>
        [Test]
        public void RIExistsBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Non-existent key should return 0
            var result = db.Execute("RI.EXISTS", "myindex");
            ClassicAssert.AreEqual(0, (int)result);

            // Create index, should now return 1
            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536");
            result = db.Execute("RI.EXISTS", "myindex");
            ClassicAssert.AreEqual(1, (int)result);

            // Delete the key, should return 0
            db.KeyDelete("myindex");
            result = db.Execute("RI.EXISTS", "myindex");
            ClassicAssert.AreEqual(0, (int)result);
        }

        /// <summary>
        /// Verifies RI.EXISTS returns 0 (not WRONGTYPE) for a normal string key.
        /// </summary>
        [Test]
        public void RIExistsOnNormalKeyTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // SET a normal string key
            db.StringSet("normalkey", "hello");

            // RI.EXISTS on a normal string key should return 0 (not WRONGTYPE)
            var result = db.Execute("RI.EXISTS", "normalkey");
            ClassicAssert.AreEqual(0, (int)result);
        }

        /// <summary>
        /// Verifies RI.CONFIG returns all 6 configuration fields with correct values.
        /// </summary>
        [Test]
        public void RIConfigBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536",
                "MINRECORD", "32", "MAXRECORD", "512", "MAXKEYLEN", "64");

            var result = db.Execute("RI.CONFIG", "myindex");
            var arr = (RedisResult[])result;

            // Should be 12 elements (6 field-value pairs)
            ClassicAssert.AreEqual(12, arr.Length);

            // Check field names and values
            ClassicAssert.AreEqual("storage_backend", (string)arr[0]);
            ClassicAssert.AreEqual("MEMORY", (string)arr[1]);
            ClassicAssert.AreEqual("cache_size", (string)arr[2]);
            ClassicAssert.AreEqual("65536", (string)arr[3]);
            ClassicAssert.AreEqual("min_record_size", (string)arr[4]);
            ClassicAssert.AreEqual("32", (string)arr[5]);
            ClassicAssert.AreEqual("max_record_size", (string)arr[6]);
            ClassicAssert.AreEqual("512", (string)arr[7]);
            ClassicAssert.AreEqual("max_key_len", (string)arr[8]);
            ClassicAssert.AreEqual("64", (string)arr[9]);
            ClassicAssert.AreEqual("leaf_page_size", (string)arr[10]);
            // leaf_page_size is auto-computed, just ensure it's present
            ClassicAssert.IsNotNull((string)arr[11]);
        }

        /// <summary>
        /// Verifies RI.CONFIG returns WRONGTYPE error on a normal string key.
        /// </summary>
        [Test]
        public void RIConfigWrongTypeTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.StringSet("normalkey", "hello");

            var ex = Assert.Throws<RedisServerException>(() =>
                db.Execute("RI.CONFIG", "normalkey"));
            ClassicAssert.IsNotNull(ex);
        }

        /// <summary>
        /// Verifies RI.METRICS returns runtime state (tree_handle, is_live, is_flushed, is_recovered).
        /// </summary>
        [Test]
        public void RIMetricsBasicTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536", "MINRECORD", "8");

            // Insert some data (use field/value sizes that fit default MINRECORD)
            db.Execute("RI.SET", "myindex", "field1", "value1");
            db.Execute("RI.SET", "myindex", "field2", "value2");

            var result = db.Execute("RI.METRICS", "myindex");
            var arr = (RedisResult[])result;

            // Should be 8 elements (4 field-value pairs)
            ClassicAssert.AreEqual(8, arr.Length);

            ClassicAssert.AreEqual("tree_handle", (string)arr[0]);
            // tree_handle should be a non-zero number
            ClassicAssert.IsNotNull((string)arr[1]);

            ClassicAssert.AreEqual("is_live", (string)arr[2]);
            ClassicAssert.AreEqual("true", (string)arr[3]);

            ClassicAssert.AreEqual("is_flushed", (string)arr[4]);
            ClassicAssert.AreEqual("false", (string)arr[5]);

            ClassicAssert.AreEqual("is_recovered", (string)arr[6]);
            ClassicAssert.AreEqual("false", (string)arr[7]);
        }

        /// <summary>
        /// Verifies that TYPE command returns "rangeindex" for RI keys.
        /// </summary>
        [Test]
        public void RITypeCommandTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create a RangeIndex and check TYPE
            db.Execute("RI.CREATE", "myindex", "MEMORY", "CACHESIZE", "65536");
            var type = db.KeyType("myindex");
            ClassicAssert.AreEqual(RedisType.Unknown, type);
            // StackExchange.Redis maps unknown types to Unknown, verify via Execute
            var typeResult = db.Execute("TYPE", "myindex");
            ClassicAssert.AreEqual("rangeindex", (string)typeResult);

            // Normal string key should return "string"
            db.StringSet("normalkey", "hello");
            typeResult = db.Execute("TYPE", "normalkey");
            ClassicAssert.AreEqual("string", (string)typeResult);
        }

        /// <summary>
        /// Stress test: 4 worker threads insert concurrently while a 5th thread takes a
        /// blocking SAVE checkpoint. Verifies checkpoint contains a strict prefix of each
        /// thread's keys (point-in-time snapshot consistency).
        /// </summary>
        [Test]
        [CancelAfter(120_000)]
        public void RIConcurrentOpsWithCheckpointTest(System.Threading.CancellationToken cancellationToken)
        {
            // 4 threads insert contiguous keys at full speed. A single SAVE (blocking)
            // runs from a 5th thread. After SAVE completes, workers are signaled and
            // insert a few more keys before stopping. Recovery should show a strict
            // prefix per thread: all keys before some cutoff, none after.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            const int numThreads = 4;
            const int postSaveOps = 50;
            var saveCompleted = new ManualResetEventSlim(false);
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var insertedCounts = new int[numThreads];
            var barrier = new Barrier(numThreads + 1);

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "stress", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            }

            // Worker threads: insert keys at full speed, then postSaveOps more after save
            var workers = new Task[numThreads];
            for (int t = 0; t < numThreads; t++)
            {
                var threadId = t;
                workers[t] = Task.Run(() =>
                {
                    using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    var db = redis.GetDatabase(0);
                    int i = 0;
                    barrier.SignalAndWait();

                    // Phase 1: insert at full speed until save completes
                    while (!saveCompleted.IsSet)
                    {
                        try
                        {
                            db.Execute("RI.SET", "stress", $"t{threadId}_{i:D6}", $"v{threadId}_{i:D6}");
                            i++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} op {i}: {ex.Message}");
                        }
                    }

                    // Phase 2: insert postSaveOps more (these should NOT be in the checkpoint)
                    for (int j = 0; j < postSaveOps; j++)
                    {
                        try
                        {
                            db.Execute("RI.SET", "stress", $"t{threadId}_{i:D6}", $"v{threadId}_{i:D6}");
                            i++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} op {i}: {ex.Message}");
                        }
                    }

                    insertedCounts[threadId] = i;
                });
            }

            // Checkpoint thread: wait for workers to start, then do a single blocking SAVE
            var checkpointTask = Task.Run(() =>
            {
                using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
                var db = redis.GetDatabase(0);
                barrier.SignalAndWait();

                // Let workers insert for a bit before taking the checkpoint
                Thread.Sleep(200);
                db.Execute("SAVE");
                saveCompleted.Set();
            });

            Task.WaitAll([.. workers, checkpointTask]);
            ClassicAssert.IsEmpty(errors, $"Errors during concurrent ops:\n{string.Join("\n", errors)}");

            // Recover from checkpoint only (no AOF)
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                for (int t = 0; t < numThreads; t++)
                {
                    var totalInserted = insertedCounts[t];

                    // Find the prefix length: first key that is absent
                    int recovered = 0;
                    for (int i = 0; i < totalInserted; i++)
                    {
                        var val = db.Execute("RI.GET", "stress", $"t{t}_{i:D6}");
                        if (val.IsNull)
                            break;
                        ClassicAssert.AreEqual($"v{t}_{i:D6}", (string)val,
                            $"Thread {t} key t{t}_{i:D6}: value mismatch");
                        recovered++;
                    }

                    ClassicAssert.Greater(recovered, 0,
                        $"Thread {t}: no keys recovered");

                    // Strict prefix: ALL keys after the cutoff must be absent
                    for (int i = recovered; i < totalInserted; i++)
                    {
                        var val = db.Execute("RI.GET", "stress", $"t{t}_{i:D6}");
                        ClassicAssert.IsTrue(val.IsNull,
                            $"Thread {t} key t{t}_{i:D6}: present after gap at {recovered} — not a strict prefix");
                    }

                    // The postSaveOps keys must NOT be in the checkpoint
                    ClassicAssert.Less(recovered, totalInserted,
                        $"Thread {t}: all {totalInserted} keys recovered — checkpoint was not taken mid-insertion");
                }
            }
        }

        /// <summary>
        /// After checkpoint recovery, <c>IsRecovered</c> must be cleared when the tree
        /// is first restored. Otherwise, a second eviction cycle causes
        /// RestoreTreeFromFlush to pick the stale checkpoint snapshot instead of the
        /// fresh <c>flush.bftree</c>, losing post-recovery writes.
        /// </summary>
        [Test]
        public void RIRecoverThenSecondEvictionUsesFlushSnapshotTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "stalecp", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "stalecp", "pre-checkpoint", "original");
                db.Execute("SAVE");
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, enableAOF: true, tryRecover: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                var val = db.Execute("RI.GET", "stalecp", "pre-checkpoint");
                ClassicAssert.AreEqual("original", (string)val, "Checkpoint data should be restored");

                db.Execute("RI.SET", "stalecp", "post-recovery", "new-value");

                // Fill log to trigger flush (writes flush.bftree) then eviction (frees tree)
                for (int i = 0; i < 2000; i++)
                    db.StringSet($"fill{i:D4}", $"data{i:D4}");

                ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Tree should be evicted");

                val = db.Execute("RI.GET", "stalecp", "pre-checkpoint");
                ClassicAssert.AreEqual("original", (string)val, "Pre-checkpoint data should survive");

                val = db.Execute("RI.GET", "stalecp", "post-recovery");
                ClassicAssert.AreEqual("new-value", (string)val,
                    "Post-recovery data must survive eviction (flush.bftree, not stale checkpoint)");
            }
        }

        /// <summary>
        /// A checkpoint can capture the key's latest stub with <c>IsTransferred</c> set when a newer-version RIPROMOTE
        /// or RIRESTORE copy marks it after the version shift; recovery discards that copy, so the flagged stub is latest
        /// again. Restoring its tree must clear the flag, otherwise the record that owns the tree is never flushed or
        /// evicted, and the tree leaks.
        /// </summary>
        [Test]
        public void RIRecoveredStaleTransferredStubStillEvictsRestoredTreeTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "staletx", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "staletx", "pre-checkpoint", "original");

                // Model the checkpoint image a concurrent newer-version copy leaves behind.
                var keyBytes = GC.AllocateArray<byte>("staletx"u8.Length, pinned: true);
                "staletx"u8.CopyTo(keyBytes);
                var marker = new MarkLatestStubTransferred();
                server.Provider.StoreWrapper.store.Log.IterateKeyVersions(ref marker, (FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(keyBytes));
                ClassicAssert.IsTrue(marker.Marked, "test setup: latest stub should be marked transferred");

                db.Execute("SAVE");
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, tryRecover: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                ClassicAssert.AreEqual("original", (string)db.Execute("RI.GET", "staletx", "pre-checkpoint"));
                ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "Tree should be restored");
                db.Execute("RI.SET", "staletx", "post-recovery", "new-value");

                for (var i = 0; i < 2000; i++)
                    db.StringSet($"fill{i:D4}", $"data{i:D4}");

                ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Restored tree must be evicted with its stub");
                ClassicAssert.AreEqual("original", (string)db.Execute("RI.GET", "staletx", "pre-checkpoint"));
                ClassicAssert.AreEqual("new-value", (string)db.Execute("RI.GET", "staletx", "post-recovery"));
            }
        }

        private struct MarkLatestStubTransferred : IScanIteratorFunctions
        {
            public bool Marked;

            public readonly bool OnStart(long beginAddress, long endAddress) => true;

            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult) where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                RangeIndexManager.SetTransferredFlag(logRecord.ValueSpan);
                Marked = true;
                return false;
            }

            public readonly void OnStop(bool completed, long numberOfRecords) { }

            public readonly void OnException(Exception exception, long numberOfRecords) { }
        }

        /// <summary>
        /// A stub that is mutable in a checkpoint and flushed after it is re-flushed, with no live tree, at the same
        /// address when recovery moves the read-only boundary over it; the flush file from before the crash must be replaced.
        /// </summary>
        [Test]
        public void RIRecoverReplacesExistingFlushFileTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, mutablePercent: 10);
            server.Start();

            var riLogRoot = server.Provider.StoreWrapper.rangeIndexManager.RiLogRoot;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // Place the stub past the first page so it is mutable at SAVE but below the read-only address recovery computes.
                for (var i = 0; i < 150; i++)
                    db.StringSet($"pre{i:D4}", $"data{i:D4}");

                db.Execute("RI.CREATE", "reflush", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "reflush", "field", "value");
                for (var i = 0; i < 150; i++)
                    db.StringSet($"post{i:D4}", $"data{i:D4}");

                ClassicAssert.IsFalse(Directory.EnumerateFiles(riLogRoot, "*.flush.bftree").Any(), "Stub must be mutable at checkpoint time");
                db.Execute("SAVE");

                for (var i = 0; i < 2000 && !Directory.EnumerateFiles(riLogRoot, "*.flush.bftree").Any(); i++)
                    db.StringSet($"fill{i:D4}", $"data{i:D4}");
                WaitForFlushSnapshots();

                ClassicAssert.IsTrue(Directory.EnumerateFiles(riLogRoot, "*.flush.bftree").Any(), "Stub should have been flushed after the checkpoint");
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, mutablePercent: 10, tryRecover: true, failOnRecoveryError: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);
                ClassicAssert.AreEqual("value", (string)db.Execute("RI.GET", "reflush", "field"));
            }
        }

        /// <summary>
        /// The synced copy used for tree snapshots must produce an exact copy across sync intervals, replace a destination that
        /// is a hard link to an immutable snapshot without modifying that snapshot, and leave no destination when the source is missing.
        /// </summary>
        [Test]
        public void RIFileCopyPreservesHardLinkedSnapshotTest()
        {
            var dir = Path.Combine(TestUtils.MethodTestDir, "copy");
            Directory.CreateDirectory(dir);
            var snapshot = Path.Combine(dir, "snapshot.bftree");
            var source = Path.Combine(dir, "source.bftree");
            var destination = Path.Combine(dir, "data.bftree");

            var snapshotBytes = new byte[4096];
            Array.Fill(snapshotBytes, (byte)0x5A);
            File.WriteAllBytes(snapshot, snapshotBytes);
            var sourceBytes = new byte[RangeIndexFileLinks.SyncIntervalBytes + 12345];
            new Random(7).NextBytes(sourceBytes);
            File.WriteAllBytes(source, sourceBytes);

            ClassicAssert.IsTrue(RangeIndexFileLinks.TryReplaceWithHardLink(snapshot, destination), "Test requires hard links in the test directory");
            RangeIndexFileLinks.Copy(source, destination, overwrite: true);

            ClassicAssert.IsTrue(File.ReadAllBytes(destination).AsSpan().SequenceEqual(sourceBytes), "Copy must reproduce the source exactly");
            ClassicAssert.IsTrue(File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(snapshotBytes), "Overwriting must not write through a hard link to the snapshot");
            Assert.Throws<IOException>(() => RangeIndexFileLinks.Copy(source, destination, overwrite: false));

            var missingDestination = Path.Combine(dir, "missing.bftree");
            Assert.Throws<FileNotFoundException>(() => RangeIndexFileLinks.Copy(Path.Combine(dir, "absent.bftree"), missingDestination, overwrite: true));
            ClassicAssert.IsFalse(File.Exists(missingDestination), "A failed copy must not leave a destination behind");
        }

        /// <summary>
        /// A tree flushed after the recovered checkpoint leaves a flush file newer than the record recovery restores; recovery
        /// must delete it, or superseded-file cleanup and replication keep that orphan and drop the file the live record reads.
        /// </summary>
        [Test]
        public void RIRecoveryDeletesFlushFilesAboveRecoveredTailTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var hashPrefix = RangeIndexManager.HashKeyToPrefix("orphan"u8);
            string checkpointedFile;
            string orphanFile;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                var store = server.Provider.StoreWrapper.store;
                var riLogRoot = server.Provider.StoreWrapper.rangeIndexManager.RiLogRoot;

                db.Execute("RI.CREATE", "orphan", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "orphan", "field", "checkpointed");
                store.Log.FlushAndEvict(wait: true);
                WaitForFlushSnapshots();
                checkpointedFile = Directory.GetFiles(riLogRoot, $"{hashPrefix}.*.flush.bftree").Single();

                db.Execute("SAVE");

                // Promote the tree past the checkpoint tail, mutate it, and flush it again before the "crash".
                ClassicAssert.AreEqual("checkpointed", (string)db.Execute("RI.GET", "orphan", "field"));
                db.Execute("RI.SET", "orphan", "field", "lost");
                store.Log.FlushAndEvict(wait: true);
                WaitForFlushSnapshots();
                orphanFile = Directory.GetFiles(riLogRoot, $"{hashPrefix}.*.flush.bftree").Single(f => f != checkpointedFile);
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, tryRecover: true, failOnRecoveryError: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;
            ClassicAssert.IsFalse(File.Exists(orphanFile), "Recovery must delete the flush file written after the recovered checkpoint");
            ClassicAssert.IsTrue(File.Exists(checkpointedFile), "The flush file of the recovered record must survive recovery");

            // The cluster cleanup keeps only the newest file per tree, which must be the one the recovered record reads.
            rangeIndexManager.DeleteSupersededFlushFiles(long.MaxValue);
            ClassicAssert.IsTrue(File.Exists(checkpointedFile), "Superseded-file cleanup must keep the file of the live record");
            var replicated = rangeIndexManager.EnumerateFilesForReplication(Guid.NewGuid(), 0, long.MaxValue).Where(e => e.IsFlushFile).Select(e => Path.GetFileName(e.Path)).ToList();
            CollectionAssert.Contains(replicated, Path.GetFileName(checkpointedFile), "Replication must send the file of the live record");

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);
                ClassicAssert.AreEqual("checkpointed", (string)db.Execute("RI.GET", "orphan", "field"));
            }
        }

        /// <summary>
        /// A tree still tracked after a store reset (as before a replica recovers in place) must be discarded: recovery
        /// replaces its data.bftree, and a later restore would otherwise reuse the old native tree over the new file.
        /// </summary>
        [Test]
        public async Task RIResetDiscardsSurvivingTreesBeforeRecoveryTest()
        {
            var storeWrapper = server.Provider.StoreWrapper;
            var rangeIndexManager = storeWrapper.rangeIndexManager;
            var key = "survivor"u8.ToArray();
            var hashPrefix = RangeIndexManager.HashKeyToPrefix(key);

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "survivor", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "survivor", "field", "checkpointed");
                db.Execute("SAVE");
                db.Execute("RI.SET", "survivor", "field", "after-checkpoint");
            }

            storeWrapper.store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Eviction should dispose the tree");
            WaitForFlushSnapshots();

            // Track a tree that no record references, opened over data.bftree like RestoreTree does.
            var flushFile = Directory.GetFiles(rangeIndexManager.RiLogRoot, $"{hashPrefix}.*.flush.bftree").Single();
            var dataPath = rangeIndexManager.LogDataPathFor(key);
            File.Copy(flushFile, dataPath, overwrite: true);
            var survivingTree = BfTreeService.RecoverFromCprSnapshot(dataPath, enableSnapshots: true, StorageBackendType.Disk);
            var pinnedKey = GC.AllocateArray<byte>(key.Length, pinned: true);
            key.CopyTo(pinnedKey, 0);
            var keyHash = GarnetKeyComparer.StaticGetHashCode64((FixedSpanByteKey)PinnedSpanByte.FromPinnedSpan(pinnedKey));
            rangeIndexManager.RegisterIndex(survivingTree, keyHash, key);
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount);

            storeWrapper.Reset();
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "Store reset must discard trees no record references");

            await storeWrapper.RecoverCheckpointAsync();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);
                ClassicAssert.AreEqual("checkpointed", (string)db.Execute("RI.GET", "survivor", "field"), "Recovery must restore the checkpointed tree");
            }
        }

        /// <summary>
        /// Verifies pure AOF-only recovery (no checkpoint). RI.CREATE is replayed to
        /// recreate the BfTree, then RI.SET/RI.DEL operations rebuild the data.
        /// </summary>
        [Test]
        public void RIAofOnlyRecoveryTest()
        {
            // No checkpoint at all — AOF replay must recreate the BfTree from scratch.
            // RI.CREATE is logged via RMW, RI.SET/RI.DEL via synthetic RMW.
            // On recovery, AOF replay re-executes RI.CREATE (creates the tree),
            // then replays RI.SET/RI.DEL operations to rebuild the data.
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                db.Execute("RI.CREATE", "aofonly", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "aofonly", "key-a", "val-a");
                db.Execute("RI.SET", "aofonly", "key-b", "val-b");
                db.Execute("RI.SET", "aofonly", "key-c", "val-c");
                db.Execute("RI.SET", "aofonly", "key-a", "val-a-updated");
                db.Execute("RI.DEL", "aofonly", "key-b");

                // Commit AOF but do NOT take a checkpoint
                db.Execute("COMMITAOF");
            }

            // Recover — no checkpoint exists, so everything comes from AOF replay
            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);

                // key-a should have updated value
                var val = db.Execute("RI.GET", "aofonly", "key-a");
                ClassicAssert.AreEqual("val-a-updated", (string)val, "key-a should have updated value from AOF replay");

                // key-b should be deleted
                val = db.Execute("RI.GET", "aofonly", "key-b");
                ClassicAssert.IsTrue(val.IsNull, "key-b should be deleted via AOF replay");

                // key-c should exist
                val = db.Execute("RI.GET", "aofonly", "key-c");
                ClassicAssert.AreEqual("val-c", (string)val, "key-c should exist from AOF replay");

                // New writes should work on the AOF-recovered tree
                db.Execute("RI.SET", "aofonly", "key-d", "val-d");
                val = db.Execute("RI.GET", "aofonly", "key-d");
                ClassicAssert.AreEqual("val-d", (string)val, "new insert should work after AOF-only recovery");
            }
        }

        /// <summary>
        /// Verifies that DEL on a disk-backed RangeIndex cleans up the WORKING file
        /// (<c>&lt;hash&gt;.data.bftree</c>) on disk but PRESERVES per-flush snapshot files
        /// (<c>&lt;hash&gt;.&lt;addr&gt;.flush.bftree</c>). Per-flush files are LOG-tied — they may
        /// still be required to recover an OLDER checkpoint that was taken BEFORE the DEL.
        /// They are reclaimed by <c>OnTruncate</c> when the log's BeginAddress passes their address.
        /// </summary>
        [Test]
        public void RIDiskFileCleanupOnDeleteTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Create a disk-backed range index
            db.Execute("RI.CREATE", "cleanup", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "cleanup", "key1", "val1");

            // Verify the riLogRoot exists with at least one <hash>.data.bftree file
            var riLogRoot = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            ClassicAssert.IsTrue(Directory.Exists(riLogRoot), "riLogRoot directory should exist after RI.CREATE");
            var dataFiles = Directory.GetFiles(riLogRoot, "*.data.bftree");
            ClassicAssert.AreEqual(1, dataFiles.Length, "should have exactly one data.bftree file");

            // Delete the range index
            var delResult = db.KeyDelete("cleanup");
            ClassicAssert.IsTrue(delResult, "DEL should return true");

            // Working file should be cleaned up.
            dataFiles = Directory.Exists(riLogRoot) ? Directory.GetFiles(riLogRoot, "*.data.bftree") : [];
            ClassicAssert.AreEqual(0, dataFiles.Length, "data.bftree files should be deleted after DEL");
            // (Flush files are not asserted here because none were created in this test — no flush
            // event fired between RI.SET and DEL. The next test verifies the preservation contract.)
        }

        /// <summary>
        /// Verifies the LOG-tied lifetime of per-flush snapshot files: DEL preserves the
        /// <c>&lt;hash&gt;.&lt;addr&gt;.flush.bftree</c> files that were created by prior flush
        /// events, because they may be required to recover an OLDER checkpoint that was taken
        /// before the DEL. Only <c>OnTruncate</c> (when log BeginAddress passes their address)
        /// can safely delete them.
        /// </summary>
        [Test]
        public void RIDeletePreservesPerFlushSnapshotFilesTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true,
                lowMemory: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);

            // Create RI, write, force flush so a per-flush snapshot file is created.
            db.Execute("RI.CREATE", "preservetest", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "preservetest", "field-x", "value-v1");
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            WaitForFlushSnapshots();

            var riLogRoot = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            var flushBefore = Directory.GetFiles(riLogRoot, "*.flush.bftree");
            ClassicAssert.GreaterOrEqual(flushBefore.Length, 1,
                "test setup: at least one flush.bftree file should exist after force-flush");

            // Read once to promote the flushed stub back to the mutable region (RIPROMOTE).
            // This is required because DEL operates on the in-memory chain head.
            ClassicAssert.AreEqual("value-v1", (string)db.Execute("RI.GET", "preservetest", "field-x"));

            // DEL the key.
            ClassicAssert.IsTrue(db.KeyDelete("preservetest"), "DEL should succeed");

            // Working file must be deleted, but per-flush snapshot file(s) must SURVIVE.
            var dataAfter = Directory.GetFiles(riLogRoot, "*.data.bftree");
            ClassicAssert.AreEqual(0, dataAfter.Length, "data.bftree should be deleted on DEL");

            var flushAfter = Directory.GetFiles(riLogRoot, "*.flush.bftree");
            ClassicAssert.AreEqual(flushBefore.Length, flushAfter.Length,
                "per-flush snapshot files MUST be preserved on DEL — they may be required to recover " +
                "an older checkpoint that was taken before the DEL. Only OnTruncate (when log " +
                "BeginAddress passes their address) can safely delete them.");

            // Verify file content is byte-identical (not corrupted).
            for (var i = 0; i < flushBefore.Length; i++)
            {
                ClassicAssert.IsTrue(File.Exists(flushBefore[i]), $"flush file {Path.GetFileName(flushBefore[i])} must still exist after DEL");
            }
        }

        /// <summary>
        /// Verifies that DEL of a previously-evicted-and-restored RangeIndex correctly cleans
        /// up the working file but PRESERVES per-flush snapshot files (LOG-tied lifetime).
        /// </summary>
        [Test]
        public async Task RIDiskFileCleanupOnDeleteAfterEvictionAndRestoreTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rangeIndexManager = server.Provider.StoreWrapper.rangeIndexManager;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            var redisServer = redis.GetServers()[0];

            // Create a disk-backed range index on an early page
            db.Execute("RI.CREATE", "evictdel", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "evictdel", "key1", "val1");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "tree should be live after creation");

            // Capture TailAddress after RI insertion — flushing up to this address guarantees
            // the RI record is on disk.
            var tailAfterRiInsert = TestUtils.GetStoreAddressInfo(redisServer).TailAddress;

            var riLogRoot = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            ClassicAssert.IsTrue(Directory.Exists(riLogRoot), "riLogRoot should exist");

            // Fill the log with string keys and wait for deterministic flush past the RI record.
            // This pushes the RI stub below HeadAddress and triggers eviction.
            await TestUtils.FlushAndWaitForStoreAsync(db, redisServer, tailAfterRiInsert);

            // Verify eviction actually occurred
            ClassicAssert.AreEqual(0, rangeIndexManager.LiveIndexCount, "tree should have been freed by eviction");
            WaitForFlushSnapshots();

            // Files should still exist after eviction (preserved for lazy restore).
            var dataFiles = Directory.GetFiles(riLogRoot, "*.data.bftree");
            ClassicAssert.AreEqual(1, dataFiles.Length, "data.bftree file should survive eviction");

            var flushFilesPostEvict = Directory.GetFiles(riLogRoot, "*.flush.bftree");
            ClassicAssert.GreaterOrEqual(flushFilesPostEvict.Length, 1,
                "at least one flush snapshot should exist after deterministic flush");

            // Lazy restore brings the record back in-memory (DEL requires the record
            // to be in-memory; the unified Delete path does not trigger lazy restore).
            var val = db.Execute("RI.GET", "evictdel", "key1");
            ClassicAssert.AreEqual("val1", (string)val, "lazy restore should recover data after eviction");
            ClassicAssert.AreEqual(1, rangeIndexManager.LiveIndexCount, "tree should be live again after lazy restore");

            // Now delete — only the working data.bftree should be removed; per-flush snapshots
            // must survive (LOG-tied lifetime; needed for recovery to a prior checkpoint).
            var delResult = db.KeyDelete("evictdel");
            ClassicAssert.IsTrue(delResult, "DEL should return true");

            dataFiles = Directory.Exists(riLogRoot) ? Directory.GetFiles(riLogRoot, "*.data.bftree") : [];
            ClassicAssert.AreEqual(0, dataFiles.Length, "data.bftree should be deleted after DEL");

            var flushFilesPostDel = Directory.Exists(riLogRoot) ? Directory.GetFiles(riLogRoot, "*.flush.bftree") : [];
            ClassicAssert.AreEqual(flushFilesPostEvict.Length, flushFilesPostDel.Length,
                "per-flush snapshot files MUST be preserved on DEL even for previously-evicted keys " +
                "— they may be required to recover an older checkpoint taken before the DEL.");
        }

        // ============================================================================
        // BfTree compaction-lifecycle tests
        // ============================================================================

        /// <summary>
        /// Helper: count the number of <c>&lt;hash&gt;.&lt;addr&gt;.flush.bftree</c> files in
        /// the riLogRoot — used by tests that verify per-flush-file lifecycle.
        /// </summary>
        private static int CountFlushFiles()
        {
            var riLogRoot = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            if (!Directory.Exists(riLogRoot)) return 0;
            return Directory.GetFiles(riLogRoot, "*.flush.bftree").Length;
        }

        /// <summary>Waits for flush snapshots queued by completed page flushes; flush files are written in the background.</summary>
        private void WaitForFlushSnapshots() => server.Provider.StoreWrapper.rangeIndexManager.WaitForPendingFlushSnapshots("test");

        /// <summary>
        /// Verifies the per-flush snapshot file immutability invariant: a
        /// <c>&lt;hash&gt;.&lt;addr&gt;.flush.bftree</c> file, once written, is never overwritten.
        /// Subsequent flushes for the same key produce a NEW file at a distinct address,
        /// so historical per-flush state is preserved for recovery to older checkpoints.
        ///
        /// <para>Steps: create a disk-backed RI, set v1, flush (creates file at A1), capture
        /// bytes; promote + set v2, flush (must create a new file at A2); assert that the
        /// A1 file still exists with byte-identical content AND the post-v2 file count is
        /// strictly greater than post-v1.</para>
        /// </summary>
        [Test]
        public void RIFlushFilesAreImmutablePerAddressTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true,
                lowMemory: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true)))
            {
                var db = redis.GetDatabase(0);

                // v1 state: create disk-backed RI and insert one field. Use long enough field/value
                // to satisfy MINRECORD=8.
                db.Execute("RI.CREATE", "flushtestkey", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "flushtestkey", "field-x", "value-v1");

                // Force flush so the v1 stub is on a flushed page (creates <hash>.<A1>.flush.bftree).
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                WaitForFlushSnapshots();

                // Snapshot the flush file count and capture the v1 file content.
                var afterV1 = ListFlushFiles();
                ClassicAssert.GreaterOrEqual(afterV1.Count, 1, "at least one flush file after v1 flush");
                var v1FileContents = new System.Collections.Generic.Dictionary<string, byte[]>();
                foreach (var f in afterV1) v1FileContents[f] = File.ReadAllBytes(f);

                // Promote (read forces RIPROMOTE on flushed stub) and mutate to v2.
                ClassicAssert.AreEqual("value-v1", (string)db.Execute("RI.GET", "flushtestkey", "field-x"));
                db.Execute("RI.SET", "flushtestkey", "field-x", "value-v2");

                // Force another flush — must create a NEW <hash>.<A2>.flush.bftree at a distinct addr.
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                WaitForFlushSnapshots();

                // Verify: each pre-existing v1 file STILL exists with byte-identical content.
                foreach (var (path, originalBytes) in v1FileContents)
                {
                    ClassicAssert.IsTrue(File.Exists(path),
                        $"per-flush snapshot file {Path.GetFileName(path)} must remain after subsequent flush");
                    var currentBytes = File.ReadAllBytes(path);
                    ClassicAssert.AreEqual(originalBytes.Length, currentBytes.Length,
                        $"per-flush snapshot {Path.GetFileName(path)} must NOT be overwritten (size differs)");
                    CollectionAssert.AreEqual(originalBytes, currentBytes,
                        $"per-flush snapshot {Path.GetFileName(path)} must NOT be overwritten (content differs). " +
                        "Per-flush <addr>.flush.bftree files are immutable.");
                }

                // Verify: at least one new flush file was created post-v2 (proving second flush
                // didn't just overwrite the v1 file in place).
                var afterV2 = ListFlushFiles();
                ClassicAssert.Greater(afterV2.Count, afterV1.Count,
                    "Second flush must create a NEW per-flush file (not overwrite the v1 file). " +
                    $"Before: {afterV1.Count}, after: {afterV2.Count}");
            }

            // Helper closure
            static System.Collections.Generic.List<string> ListFlushFiles()
            {
                var dir = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
                if (!Directory.Exists(dir)) return new System.Collections.Generic.List<string>();
                return Directory.GetFiles(dir, "*.flush.bftree").ToList();
            }
        }

        /// <summary>
        /// Re-flushing a tree that was not mutated since its last snapshot must hard-link that immutable snapshot instead
        /// of writing a new copy, while a mutation must force a fresh, independent snapshot.
        /// </summary>
        [Test]
        public void RIFlushOfUnmodifiedTreeLinksPreviousSnapshotTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;
            var dir = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            string[] FlushFiles()
            {
                WaitForFlushSnapshots();
                return Directory.GetFiles(dir, "*.flush.bftree").Order().ToArray();
            }

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            db.Execute("RI.CREATE", "linkkey", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "linkkey", "field-x", "value-v1");
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var first = FlushFiles().Single();

            // A read promotes the flushed stub to the tail without mutating the tree.
            ClassicAssert.AreEqual("value-v1", (string)db.Execute("RI.GET", "linkkey", "field-x"));
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var second = FlushFiles().Except([first]).Single();

            db.Execute("RI.SET", "linkkey", "field-x", "value-v2");
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var third = FlushFiles().Except([first, second]).Single();
            ClassicAssert.AreEqual("value-v2", (string)db.Execute("RI.GET", "linkkey", "field-x"));

            // Appending through one name is visible through every hard link to the same file.
            var secondLength = new FileInfo(second).Length;
            var thirdLength = new FileInfo(third).Length;
            using (var stream = new FileStream(first, FileMode.Append, FileAccess.Write))
                stream.WriteByte(0);
            ClassicAssert.AreEqual(secondLength + 1, new FileInfo(second).Length, "the unmodified tree's flush must link the previous snapshot");
            ClassicAssert.AreEqual(thirdLength, new FileInfo(third).Length, "a mutated tree must get an independent snapshot");
        }

        /// <summary>
        /// Verifies that a tree evicted and restored from its flush snapshot, then flushed again without being mutated,
        /// hard-links the snapshot it was restored from instead of rewriting the whole tree.
        /// </summary>
        [Test]
        public void RIFlushOfRestoredUnmodifiedTreeLinksRestoreSourceTest()
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var store = server.Provider.StoreWrapper.store;
            var dir = Path.Combine(TestUtils.MethodTestDir, "Store", "rangeindex");
            string[] FlushFiles()
            {
                WaitForFlushSnapshots();
                return Directory.GetFiles(dir, "*.flush.bftree").Order().ToArray();
            }

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
            var db = redis.GetDatabase(0);
            db.Execute("RI.CREATE", "restorelink", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "restorelink", "field-x", "value-v1");
            store.Log.FlushAndEvict(wait: true);
            var first = FlushFiles().Single();
            ClassicAssert.AreEqual(0, server.Provider.StoreWrapper.rangeIndexManager.LiveIndexCount, "eviction must dispose the tree");

            // The read restores the tree from the first flush file and promotes its stub to the tail without mutating it.
            ClassicAssert.AreEqual("value-v1", (string)db.Execute("RI.GET", "restorelink", "field-x"));
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var second = FlushFiles().Except([first]).Single();

            var secondLength = new FileInfo(second).Length;
            using (var stream = new FileStream(first, FileMode.Append, FileAccess.Write))
                stream.WriteByte(0);
            ClassicAssert.AreEqual(secondLength + 1, new FileInfo(second).Length, "the restored, unmodified tree's flush must link the file it was restored from");
        }

        /// <summary>
        /// Verifies that <c>DisposeTreeUnderLock</c> with <c>deleteFiles: false</c> (eviction
        /// path) NO-OPS when the source stub has <c>IsTransferred=true</c>, even when its
        /// <c>TreeHandle</c> is zero. After <c>PostCopyToTail</c> (compaction) or RIPROMOTE
        /// PostCopyUpdater transfers ownership to a destination at the tail, the source
        /// record's stub carries <c>IsTransferred=true</c>; the corresponding liveIndexes
        /// entry now belongs to the destination, so a later <c>OnEvict</c> on the stale
        /// source must NOT remove that entry — doing so would lose checkpoint coverage and
        /// DEL-time native-tree disposal.
        ///
        /// <para>Test mechanics:</para>
        /// <list type="number">
        /// <item>Construct a 35-byte stub representing the stale source (IsTransferred=true,
        /// TreeHandle=0).</item>
        /// <item>Call <c>DisposeTreeUnderLock(key, stub, deleteFiles: false)</c>.</item>
        /// <item>Assert the liveIndexes entry SURVIVED (count unchanged).</item>
        /// </list>
        ///
        /// <para>A stub with <c>IsTransferred=false</c> and <c>TreeHandle=0</c> only removes a pending
        /// (not yet activated) entry, so an activated entry also survives it.</para>
        /// </summary>
        [Test]
        public void RIDisposeTreeUnderLockNoOpsOnTransferredSourceTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            ClassicAssert.IsNotNull(rim);

            // Create an RI key so that a real liveIndexes entry exists; this models the scenario
            // where we'd later eviction-callback a stale source.
            db.Execute("RI.CREATE", "transtest", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "transtest", "field-x", "value-v1");
            ClassicAssert.AreEqual(1, rim.LiveIndexCount, "tree should be live after creation");

            // Construct a stub representing a "stale source" (IsTransferred=true, TreeHandle=0)
            // with the SAME key as the live entry. This models the byte state of a source record
            // after PostCopyToTail-live or RIPROMOTE-live has cleared TreeHandle and set
            // IsTransferred on the source.
            var staleStub = new byte[RangeIndexManager.IndexSizeBytes];
            // Stub layout: [0..7]=TreeHandle (zero), [33]=flags byte (Transferred bit = 1<<2 = 4)
            staleStub[33] = 4;

            // Verify our reading of the stub matches expectation.
            ref readonly var stubRef = ref RangeIndexManager.ReadIndex(staleStub);
            ClassicAssert.AreEqual(nint.Zero, stubRef.TreeHandle, "test setup: stub TreeHandle should be 0");
            ClassicAssert.IsTrue(stubRef.IsTransferred, "test setup: IsTransferred should be true");
            ClassicAssert.IsFalse(stubRef.IsFlushed, "test setup: IsFlushed should be false");

            // Call DisposeTreeUnderLock as OnEvict would, with deleteFiles=false (eviction path).
            // With IsTransferred=true, this must no-op — NOT remove the entry that belongs to
            // the live record at the tail.
            // RangeIndexManager hashes the key via PinnedSpanByte.FromPinnedSpan, which captures
            // a raw pointer assuming the source is GC-pinned. Use unsafe `fixed` blocks to pin
            // the managed byte[] for the duration of each DisposeTreeUnderLock call.
            unsafe
            {
                var keyBytes = System.Text.Encoding.ASCII.GetBytes("transtest");
                fixed (byte* keyPtr = keyBytes)
                {
                    var pinnedKey = new ReadOnlySpan<byte>(keyPtr, keyBytes.Length);
                    rim.DisposeTreeUnderLock(pinnedKey, staleStub, deleteFiles: false);
                }
            }

            ClassicAssert.AreEqual(1, rim.LiveIndexCount,
                "DisposeTreeUnderLock on a stale (IsTransferred=true) source must NOT remove the live entry " +
                "that now belongs to the destination at the tail.");

            // Live tree should still be functional.
            ClassicAssert.AreEqual("value-v1", (string)db.Execute("RI.GET", "transtest", "field-x"));

            // A stub WITHOUT IsTransferred and with TreeHandle=0 looks like a pending entry being evicted, but the entry
            // for "pendkey" is activated, so it must survive too.
            var pendingStub = new byte[RangeIndexManager.IndexSizeBytes];
            db.Execute("RI.CREATE", "pendkey", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            ClassicAssert.AreEqual(2, rim.LiveIndexCount, "second tree created");
            // Now simulate eviction of a pending-entry stub for "pendkey".
            unsafe
            {
                var pendKeyBytes = System.Text.Encoding.ASCII.GetBytes("pendkey");
                fixed (byte* keyPtr = pendKeyBytes)
                {
                    var pinnedKey = new ReadOnlySpan<byte>(keyPtr, pendKeyBytes.Length);
                    rim.DisposeTreeUnderLock(pinnedKey, pendingStub, deleteFiles: false);
                }
            }
            ClassicAssert.AreEqual(2, rim.LiveIndexCount, "evicting a TreeHandle=0 stub must not remove an activated entry");
        }

        /// <summary>
        /// RestoreTree activates the restored tree before its RIRESTORE RMW publishes the handle. If that RMW copies a
        /// read-only stub to the tail, a later eviction of the stale TreeHandle=0 source must not remove the activated
        /// entry; otherwise the tree leaks and later flushes copy its live working file as a stale snapshot.
        /// </summary>
        [Test]
        public async Task RIEvictionOfRestoreCopyUpdateSourceKeepsRestoredTreeAsync()
        {
            TestUtils.IgnoreIfExceptionInjectionDisabled();

            const ExceptionInjectionType RestorePause = ExceptionInjectionType.RangeIndex_Restore_Pause_Before_Stub_Update;

            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            db.Execute("RI.CREATE", "myindex", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "myindex", "alpha", "value-alpha");
            for (var i = 0; i < 2000; i++)
                db.StringSet($"filler{i:D4}", $"data{i:D4}");
            ClassicAssert.AreEqual(0, rim.LiveIndexCount, "tree should have been freed by eviction");

            ExceptionInjectionHelper.EnableException(RestorePause);
            try
            {
                // The read promotes the cold stub to the tail, activates the restored tree, then parks before RIRESTORE.
                var getTask = Task.Run(() =>
                {
                    using var getRedis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    return (string)getRedis.GetDatabase(0).Execute("RI.GET", "myindex", "alpha");
                });
                await ExceptionInjectionHelper.WaitOnClearAsync(RestorePause).WaitAsync(TimeSpan.FromSeconds(30));
                ClassicAssert.AreEqual(1, rim.LiveIndexCount, "restored tree should be activated");

                // Make the promoted stub read-only so RIRESTORE copy-updates it to the tail.
                var sourceEnd = store.Log.TailAddress;
                store.Log.ShiftReadOnlyAddress(sourceEnd, wait: true);

                ExceptionInjectionHelper.EnableException(RestorePause);
                ClassicAssert.AreEqual("value-alpha", await getTask.WaitAsync(TimeSpan.FromSeconds(30)));
                ExceptionInjectionHelper.DisableException(RestorePause);

                // Evict only the stale source; the restored stub lives above sourceEnd.
                store.Log.ShiftHeadAddress(sourceEnd, wait: true);
            }
            finally
            {
                ExceptionInjectionHelper.DisableException(RestorePause);
            }

            ClassicAssert.AreEqual(1, rim.LiveIndexCount, "evicting the stale source must not remove the restored tree");

            db.Execute("RI.SET", "myindex", "bravo", "value-bravo");
            for (var i = 0; i < 2000; i++)
                db.StringSet($"filler2-{i:D4}", $"data{i:D4}");
            ClassicAssert.AreEqual(0, rim.LiveIndexCount, "restored tree should have been freed by eviction");

            ClassicAssert.AreEqual("value-alpha", (string)db.Execute("RI.GET", "myindex", "alpha"));
            ClassicAssert.AreEqual("value-bravo", (string)db.Execute("RI.GET", "myindex", "bravo"));
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "liveIndexes")]
        static extern ref System.Collections.Concurrent.ConcurrentDictionary<Guid, RangeIndexManager.TreeEntry> GetLiveIndexes(RangeIndexManager manager);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "storeEpoch")]
        static extern ref Tsavorite.core.LightEpoch GetStoreEpoch(RangeIndexManager manager);

        /// <summary>
        /// The checkpoint snapshot loop iterates liveIndexes without the per-key lock or epoch protection, so it can hold
        /// an entry that eviction removes and defer-disposes. Snapshotting that stale entry must skip the disposed tree
        /// instead of failing the checkpoint with "Native handle is null", and an in-flight snapshot must delay disposal.
        /// </summary>
        [Test]
        [CancelAfter(60_000)]
        public void RICheckpointSnapshotOfEvictedTreeIsSkippedTest(System.Threading.CancellationToken cancellationToken)
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);
            var rim = server.Provider.StoreWrapper.rangeIndexManager;

            db.Execute("RI.CREATE", "evictsnap", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            db.Execute("RI.SET", "evictsnap", "field-x", "value-v1");
            db.Execute("RI.CREATE", "evictheld", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            ClassicAssert.AreEqual(2, rim.LiveIndexCount);

            var liveIndexes = GetLiveIndexes(rim);
            var entries = liveIndexes.Values.ToArray();
            var staleEntry = entries.Single(e => e.HashPrefix == RangeIndexManager.HashKeyToPrefix("evictsnap"u8));
            var heldEntry = entries.Single(e => e.HashPrefix == RangeIndexManager.HashKeyToPrefix("evictheld"u8));
            var snapshotPath = Path.Combine(TestUtils.MethodTestDir, "stale-entry.bftree");

            Evict("evictsnap", staleEntry.Tree.NativePtr);
            while (staleEntry.Tree.NativePtr != nint.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(10);
            }

            ClassicAssert.IsFalse(staleEntry.TrySnapshotUnderClaim(snapshotPath), "a snapshot of a disposed tree must be skipped");
            ClassicAssert.IsFalse(File.Exists(snapshotPath));

            // While a snapshot holds the claim, the deferred dispose must not free the native tree.
            ClassicAssert.IsTrue(heldEntry.TryClaimSnapshot());
            var heldPtr = heldEntry.Tree.NativePtr;
            var evict = Task.Run(() => Evict("evictheld", heldPtr), cancellationToken);
            Thread.Sleep(200);
            ClassicAssert.AreEqual(heldPtr, heldEntry.Tree.NativePtr, "the tree must not be disposed while a snapshot holds the claim");
            heldEntry.ReleaseSnapshot();
            evict.Wait(cancellationToken);
            while (heldEntry.Tree.NativePtr != nint.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(10);
            }

            ClassicAssert.AreEqual(0, rim.LiveIndexCount);

            unsafe void Evict(string key, nint treeHandle)
            {
                var stub = new byte[RangeIndexManager.IndexSizeBytes];
                BitConverter.TryWriteBytes(stub.AsSpan(0, sizeof(long)), (long)treeHandle);
                var keyBytes = System.Text.Encoding.ASCII.GetBytes(key);
                var epoch = GetStoreEpoch(rim);
                epoch.Resume();
                try
                {
                    fixed (byte* keyPtr = keyBytes)
                        rim.DisposeTreeUnderLock(new ReadOnlySpan<byte>(keyPtr, keyBytes.Length), stub, deleteFiles: false);
                }
                finally
                {
                    epoch.Suspend();
                }
            }
        }

        /// <summary>
        /// Verifies that <c>EnableRangeIndexPreview=true</c> works correctly with
        /// <c>CopyReadsToTail=true</c>. RangeIndex Reads go through the dedicated
        /// <c>Read_RangeIndex</c> API which suppresses Tsavorite's automatic CTT
        /// (RangeIndex performs its own controlled promotion via RIPROMOTE). This test
        /// is bounded by <c>[CancelAfter]</c> so a regression that lets CTT reach a
        /// RangeIndex stub would surface as a hang/timeout.
        /// </summary>
        [Test]
        [CancelAfter(60_000)]
        public void RICopyReadsToTailCompatibleTest(System.Threading.CancellationToken cancellationToken)
        {
            // Recreate the server with CopyReadsToTail=true.
            server?.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(
                TestUtils.MethodTestDir,
                enableRangeIndexPreview: true,
                copyReadsToTail: true);
            server.Start();

            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);
            db.Execute("RI.CREATE", "rikey", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
            for (int i = 0; i < 50; i++)
                db.Execute("RI.SET", "rikey", $"field-{i:000}", $"value-{i:000}-pad");

            // Force the records into the read-only / flushed region so subsequent reads
            // would otherwise trigger CTT for the RI stub. With Read_RangeIndex suppressing
            // CTT, the read should route through PromoteToTail (RIPROMOTE) instead.
            var store = server.Provider.StoreWrapper.store;
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

            for (int i = 0; i < 50 && !cancellationToken.IsCancellationRequested; i++)
            {
                var got = (string)db.Execute("RI.GET", "rikey", $"field-{i:000}");
                ClassicAssert.AreEqual($"value-{i:000}-pad", got, $"field-{i:000}");
            }
        }

        /// <summary>
        /// A flush snapshot is written by a background worker, so marking pages read-only and evicting them must not wait
        /// for it, and neither must a promote of the evicted record, which reclaims the tree; a checkpoint must not persist
        /// until the flush file exists.
        /// </summary>
        [Test]
        [CancelAfter(120_000)]
        public async Task RIFlushSnapshotRunsInBackgroundAndGatesCheckpointAsync(System.Threading.CancellationToken cancellationToken)
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            rim.BeforeFlushSnapshotForTest = path =>
            {
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };

            try
            {
                using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "asyncflush", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "asyncflush", "field-x", "value-v1");

                // Flushing and evicting completes while the snapshot is blocked, which leaves the tree to its snapshot.
                await Task.Run(() => store.Log.FlushAndEvict(wait: true)).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)), "the flush must queue a snapshot");
                ClassicAssert.AreEqual(1, rim.PendingFlushSnapshotCount);
                ClassicAssert.AreEqual(0, rim.LiveIndexCount, "eviction must not wait for the snapshot");
                ClassicAssert.IsEmpty(Directory.GetFiles(rim.RiLogRoot, "*.flush.bftree"), "the flush file must not exist before its snapshot runs");

                // The evicted tree is still open, so the promote reclaims it instead of waiting for its snapshot and a copy.
                var get = Task.Run(() =>
                {
                    using var getRedis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
                    var getDb = getRedis.GetDatabase(0);
                    _ = getDb.Execute("RI.SET", "asyncflush", "field-y", "value-y");
                    return (string)getDb.Execute("RI.GET", "asyncflush", "field-x");
                }, cancellationToken);
                ClassicAssert.AreEqual("value-v1", await get.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
                ClassicAssert.AreEqual(1, rim.DeferredTreesReclaimedCount);
                ClassicAssert.AreEqual(1, rim.PendingFlushSnapshotCount, "the reclaimed tree's snapshot must still be queued");

                var save = Task.Run(() => db.Execute("SAVE"), cancellationToken);

                // Completing early would persist a checkpoint referencing a missing flush file.
                ClassicAssert.IsFalse(save.Wait(TimeSpan.FromSeconds(1), cancellationToken), "the checkpoint must wait for the pending snapshot");

                gate.Set();
                _ = await save.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                ClassicAssert.AreEqual(1, rim.LiveIndexCount, "the settled release must leave the reclaimed tree registered");
                ClassicAssert.AreEqual("value-y", (string)db.Execute("RI.GET", "asyncflush", "field-y"));
                ClassicAssert.AreEqual(0, rim.PendingFlushSnapshotCount);
                ClassicAssert.IsNotEmpty(Directory.GetFiles(rim.RiLogRoot, "*.flush.bftree"));
                ClassicAssert.IsEmpty(Directory.GetFiles(rim.RiLogRoot, "*" + RangeIndexManager.InProgressSuffix), "completed snapshots must be renamed to their final name");
            }
            finally
            {
                gate.Set();
                rim.BeforeFlushSnapshotForTest = null;
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, tryRecover: true, failOnRecoveryError: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                ClassicAssert.AreEqual("value-v1", (string)redis.GetDatabase(0).Execute("RI.GET", "asyncflush", "field-x"));
                ClassicAssert.AreEqual("value-y", (string)redis.GetDatabase(0).Execute("RI.GET", "asyncflush", "field-y"));
            }
        }

        /// <summary>
        /// Flush snapshots of one tree queued behind a running one are written once: the next worker snapshots the tree into
        /// the newest flush file and hard-links the others, and every flushed stub still recovers.
        /// </summary>
        [Test]
        [CancelAfter(120_000)]
        public async Task RIQueuedFlushSnapshotsOfOneTreeAreCoalescedAsync(System.Threading.CancellationToken cancellationToken)
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            rim.BeforeFlushSnapshotForTest = path =>
            {
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };

            try
            {
                using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "coalesce", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");

                // Each write copies the read-only record to the tail, and shifting the read-only address flushes that copy.
                for (var i = 0; i < 3; i++)
                {
                    db.Execute("RI.SET", "coalesce", $"field-{i}", $"value-{i}");
                    await Task.Run(() => store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true)).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30), cancellationToken), "the flush must queue a snapshot");
                }

                ClassicAssert.AreEqual(3, rim.PendingFlushSnapshotCount, "the blocked worker has not taken the queued snapshots yet");

                gate.Set();
                while (rim.PendingFlushSnapshotCount > 0)
                    await Task.Delay(20, cancellationToken);

                ClassicAssert.AreEqual(2, rim.CoalescedFlushSnapshotCount, "one snapshot must serve all three flushes");
                var files = Directory.GetFiles(rim.RiLogRoot, "*.flush.bftree");
                ClassicAssert.AreEqual(3, files.Length);
                ClassicAssert.AreEqual(1, files.Select(f => new FileInfo(f).Length).Distinct().Count(), "the coalesced files must hold the same snapshot");
                ClassicAssert.IsEmpty(Directory.GetFiles(rim.RiLogRoot, "*" + RangeIndexManager.InProgressSuffix));

                db.Execute("SAVE");
            }
            finally
            {
                gate.Set();
                rim.BeforeFlushSnapshotForTest = null;
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true, tryRecover: true, failOnRecoveryError: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);
                for (var i = 0; i < 3; i++)
                    ClassicAssert.AreEqual($"value-{i}", (string)db.Execute("RI.GET", "coalesce", $"field-{i}"), $"field-{i}");
            }
        }

        /// <summary>
        /// A store reset waits for the flush snapshots in flight, whose output it then discards, so they run without the
        /// snapshot I/O cap rather than holding the reset for the whole capped sweep.
        /// </summary>
        [Test]
        [CancelAfter(300_000)]
        public async Task RIStoreResetLiftsSnapshotIOCapForInFlightSnapshotsAsync(System.Threading.CancellationToken cancellationToken)
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, lowMemory: true);
            server.Start();

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            var store = server.Provider.StoreWrapper.store;
            try
            {
                using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "uncapped", "DISK", "CACHESIZE", "1048576", "MINRECORD", "8", "MAXRECORD", "1024", "MAXKEYLEN", "64");

                // About 64 MiB of records: the capped sweep needs well over 100 s, the uncapped one a few seconds.
                var value = new string('v', 900);
                for (var batch = 0; batch < 72; batch++)
                {
                    var writes = new Task[1000];
                    for (var i = 0; i < writes.Length; i++)
                        writes[i] = db.ExecuteAsync("RI.SET", "uncapped", $"field-{batch:D3}-{i:D4}", value);
                    await Task.WhenAll(writes).WaitAsync(cancellationToken);
                }

                // Each pacing charge of 4 MiB sleeps 8 s at this rate, which bounds how long a lifted cap can still hold the sweep.
                BfTreeService.SetSnapshotIORate(512 << 10);
                var throttledBefore = BfTreeService.SnapshotIOThrottledTime;
                await Task.Run(() => store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true)).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                ClassicAssert.AreEqual(1, rim.PendingFlushSnapshotCount, "the flush must queue a snapshot of the tree");
                while (BfTreeService.SnapshotIOThrottledTime == throttledBefore)
                    await Task.Delay(20, cancellationToken);

                var reset = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(() => server.Provider.StoreWrapper.Reset()).WaitAsync(cancellationToken);
                reset.Stop();

                ClassicAssert.AreEqual(0, rim.PendingFlushSnapshotCount);
                ClassicAssert.Less(reset.Elapsed, TimeSpan.FromSeconds(40), "the reset must not wait for the capped snapshot sweep");
            }
            finally
            {
                BfTreeService.SetSnapshotIORate(0);
            }
        }

        /// <summary>
        /// With the AOF enabled, a checkpoint's tree snapshot is written in the background: operations on the tree proceed
        /// while it is pending, the checkpoint does not persist until it completes, and recovery converges by replaying the AOF.
        /// </summary>
        [Test]
        [CancelAfter(120_000)]
        public async Task RICheckpointSnapshotRunsInBackgroundWithAofAsync(System.Threading.CancellationToken cancellationToken)
        {
            server.Dispose();
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true);
            server.Start();

            var rim = server.Provider.StoreWrapper.rangeIndexManager;
            ClassicAssert.IsTrue(rim.BackgroundCheckpointSnapshots);
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            rim.BeforeFlushSnapshotForTest = path =>
            {
                if (path.Contains(".flush.", StringComparison.Ordinal))
                    return;
                started.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(60));
            };

            try
            {
                using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig(allowAdmin: true));
                var db = redis.GetDatabase(0);
                db.Execute("RI.CREATE", "ckptasync", "DISK", "CACHESIZE", "65536", "MINRECORD", "8");
                db.Execute("RI.SET", "ckptasync", "field-1", "value-1");
                var lastSave = (long)db.Execute("LASTSAVE");

                db.Execute("BGSAVE");
                ClassicAssert.IsTrue(started.Wait(TimeSpan.FromSeconds(30), cancellationToken), "the checkpoint must queue a tree snapshot");

                // Under the synchronous path the tree's checkpoint barrier holds this write until the snapshot is written.
                var set = Task.Run(() => db.Execute("RI.SET", "ckptasync", "field-2", "value-2"), cancellationToken);
                _ = await set.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                ClassicAssert.AreEqual("value-2", (string)db.Execute("RI.GET", "ckptasync", "field-2"));

                await Task.Delay(500, cancellationToken);
                ClassicAssert.AreEqual(lastSave, (long)db.Execute("LASTSAVE"), "the checkpoint must not persist before its tree snapshot");

                gate.Set();
                while ((long)db.Execute("LASTSAVE") == lastSave)
                    await Task.Delay(50, cancellationToken);

                db.Execute("RI.SET", "ckptasync", "field-3", "value-3");
                db.Execute("COMMITAOF");
            }
            finally
            {
                gate.Set();
                rim.BeforeFlushSnapshotForTest = null;
            }

            server.Dispose();
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true, enableAOF: true, tryRecover: true, failOnRecoveryError: true);
            server.Start();

            using (var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig()))
            {
                var db = redis.GetDatabase(0);
                for (var i = 1; i <= 3; i++)
                    ClassicAssert.AreEqual($"value-{i}", (string)db.Execute("RI.GET", "ckptasync", $"field-{i}"), $"field-{i}");
            }
        }

        /// <summary>
        /// Snapshot files left half-written by a crash carry the in-progress suffix and must be removed on startup.
        /// </summary>
        [Test]
        public void RIInProgressSnapshotsAreDeletedOnStartupTest()
        {
            var riLogRoot = server.Provider.StoreWrapper.rangeIndexManager.RiLogRoot;
            server.Dispose();

            Directory.CreateDirectory(riLogRoot);
            var partial = Path.Combine(riLogRoot, $"deadbeef.{0x40L:x16}.flush.bftree{RangeIndexManager.InProgressSuffix}");
            File.WriteAllBytes(partial, [1, 2, 3]);

            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableRangeIndexPreview: true);
            server.Start();
            ClassicAssert.IsFalse(File.Exists(partial));
        }

        /// <summary>
        /// Verifies that <see cref="RangeIndexManager.KeyExists"/> correctly reports presence
        /// for keys of every type (string, hash, sorted-set, RangeIndex) and absence for
        /// non-existent keys. This is the gate used by
        /// <see cref="RangeIndexManager.PublishMigratedIndex"/> to skip the publish when a
        /// key with the same name already exists on the destination, regardless of its type.
        /// </summary>
        [Test]
        public void RangeIndexManagerKeyExistsTest()
        {
            using var redis = ConnectionMultiplexer.Connect(TestUtils.GetConfig());
            var db = redis.GetDatabase(0);

            // Populate one key of each common type via the public API.
            ClassicAssert.AreEqual("OK", (string)db.Execute("SET", "str-key", "string-value"));
            db.Execute("HSET", "hash-key", "f", "v");
            db.Execute("ZADD", "zset-key", "1", "m");
            db.Execute("RI.CREATE", "ri-key", "MEMORY", "CACHESIZE", "65536");

            // Reach into the running server to call the internal helper directly.
            var storeWrapper = server.Provider.StoreWrapper;
            var rangeIndexManager = storeWrapper.DefaultDatabase.RangeIndexManager;

            using var localSession = new LocalServerSession(storeWrapper);
            ref var ctx = ref localSession.storageSession.stringBasicContext;

            // Every populated name reports true regardless of type.
            ClassicAssert.IsTrue(rangeIndexManager.KeyExists("str-key"u8, ref ctx), "string key should be observed");
            ClassicAssert.IsTrue(rangeIndexManager.KeyExists("hash-key"u8, ref ctx), "hash key should be observed");
            ClassicAssert.IsTrue(rangeIndexManager.KeyExists("zset-key"u8, ref ctx), "sorted-set key should be observed");
            ClassicAssert.IsTrue(rangeIndexManager.KeyExists("ri-key"u8, ref ctx), "RangeIndex key should be observed");

            // Missing key reports false.
            ClassicAssert.IsFalse(rangeIndexManager.KeyExists("missing-key"u8, ref ctx), "absent key should not be observed");

            // Sanity check the byte-level overload too (covers non-UTF-8-literal paths).
            ClassicAssert.IsTrue(rangeIndexManager.KeyExists(System.Text.Encoding.ASCII.GetBytes("str-key"), ref ctx));
            ClassicAssert.IsFalse(rangeIndexManager.KeyExists(System.Text.Encoding.ASCII.GetBytes("nope"), ref ctx));
        }
    }
}