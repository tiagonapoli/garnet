// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Unit tests for <see cref="VectorSetCleanupWorkSet{TValue}"/>.
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupWorkSetTests
    {
        private static byte[] Key(string s) => Encoding.UTF8.GetBytes(s);

        [Test]
        public void AddAndCompleteTrackTheEntry()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var workSet = new VectorSetCleanupWorkSet<int>(counter);

            ClassicAssert.IsTrue(workSet.TryAdd(Key("a"), 1));
            ClassicAssert.IsTrue(workSet.Contains(Key("a")));
            ClassicAssert.AreEqual(1, counter.Inflight);

            ClassicAssert.IsTrue(workSet.TryComplete(Key("a")));
            ClassicAssert.IsFalse(workSet.Contains(Key("a")));
            ClassicAssert.AreEqual(0, counter.Inflight);

            ClassicAssert.IsFalse(workSet.TryComplete(Key("a")));
        }

        [Test]
        public void DuplicateAddRegistersNothing()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var workSet = new VectorSetCleanupWorkSet<int>(counter);

            ClassicAssert.IsTrue(workSet.TryAdd(Key("a"), 1));
            ClassicAssert.IsFalse(workSet.TryAdd(Key("a"), 2));
            ClassicAssert.AreEqual(1, counter.Inflight);
        }

        [Test]
        public void EntriesCanBeEnumerated()
        {
            var workSet = new VectorSetCleanupWorkSet<int>(new VectorSetCleanupWorkCounter());

            _ = workSet.TryAdd(Key("a"), 1);
            _ = workSet.TryAdd(Key("b"), 2);

            var values = new List<int>();
            foreach (var (_, value) in workSet)
            {
                values.Add(value);
            }

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
        }

        [Test]
        public async Task WaitForCompletionBlocksUntilTheEntryIsCompleted()
        {
            var workSet = new VectorSetCleanupWorkSet<int>(new VectorSetCleanupWorkCounter());

            _ = workSet.TryAdd(Key("a"), 1);

            var waiter = Task.Run(() => workSet.WaitForCompletion(Key("a")));
            ClassicAssert.IsFalse(waiter.IsCompleted);

            _ = workSet.TryComplete(Key("a"));
            await waiter;
        }
    }
}