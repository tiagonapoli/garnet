// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Linq;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    [TestFixture]
    public class RangeIndexFlushFileRetentionTests : TestBase
    {
        private const string TreeA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TreeB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private string testDir;

        [SetUp]
        public void Setup()
        {
            testDir = Path.Combine(TestUtils.MethodTestDir, "ri-flush-retention-test");
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
            Directory.CreateDirectory(testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
            TestUtils.OnTearDown();
        }

        [Test]
        public void SupersededFlushFilesBelowOldestCheckpointAreDeleted()
        {
            using var manager = new RangeIndexManager(testDir);
            string[] files = [FlushFile(TreeA, 0x100), FlushFile(TreeA, 0x200), FlushFile(TreeA, 0x300), FlushFile(TreeB, 0x50)];
            foreach (var file in files)
                File.WriteAllBytes(Path.Combine(testDir, file), [1]);

            const long checkpointEnd = 0x250;
            var replicatedBefore = ReplicatedFiles(manager, checkpointEnd);

            manager.DeleteSupersededFlushFiles(checkpointEnd);

            var remaining = Directory.GetFiles(testDir, "*.flush.bftree").Select(Path.GetFileName).Order().ToArray();
            CollectionAssert.AreEqual(new[] { FlushFile(TreeA, 0x200), FlushFile(TreeA, 0x300), FlushFile(TreeB, 0x50) }.Order().ToArray(), remaining);
            CollectionAssert.AreEqual(replicatedBefore, ReplicatedFiles(manager, checkpointEnd), "the checkpoint must still replicate the same flush files");
        }

        private static string FlushFile(string hash, long address) => $"{hash}.{address:x16}.flush.bftree";

        private static string[] ReplicatedFiles(RangeIndexManager manager, long checkpointEnd)
            => manager.EnumerateFilesForReplication(Guid.NewGuid(), 0, checkpointEnd).Select(e => Path.GetFileName(e.Path)).Order().ToArray();
    }
}
