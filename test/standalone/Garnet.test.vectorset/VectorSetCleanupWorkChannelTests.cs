// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Unit tests for <see cref="VectorSetCleanupWorkChannel{T}"/>.
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupWorkChannelTests
    {
        [Test]
        public async Task PublishedItemsAreReadBack()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var channel = new VectorSetCleanupWorkChannel<int>(counter);

            ClassicAssert.IsFalse(channel.HasPending);
            ClassicAssert.IsTrue(channel.TryPublish(1));
            ClassicAssert.IsTrue(channel.TryPublish(2));
            ClassicAssert.IsTrue(channel.HasPending);

            ClassicAssert.IsTrue(await channel.WaitToReadAsync());

            using var batch = channel.ReadAllAvailable();
            CollectionAssert.AreEqual(new[] { 1, 2 }, batch.Items);
        }

        [Test]
        public void PublishRegistersAndTheBatchReleasesEveryItem()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var channel = new VectorSetCleanupWorkChannel<int>(counter);

            _ = channel.TryPublish(1);
            _ = channel.TryPublish(2);
            ClassicAssert.AreEqual(2, counter.Inflight);

            var batch = channel.ReadAllAvailable();
            ClassicAssert.AreEqual(2, counter.Inflight);

            batch.Dispose();
            ClassicAssert.AreEqual(0, counter.Inflight);
        }

        [Test]
        public void PublishRegistersAndTheLeaseReleasesOneItem()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var channel = new VectorSetCleanupWorkChannel<int>(counter);

            _ = channel.TryPublish(1);
            _ = channel.TryPublish(2);

            ClassicAssert.IsTrue(channel.TryAcquire(out var lease));
            ClassicAssert.AreEqual(1, lease.Item);
            ClassicAssert.AreEqual(2, counter.Inflight);

            lease.Dispose();
            ClassicAssert.AreEqual(1, counter.Inflight);
        }

        [Test]
        public void TryAcquireIsFalseWhenEmpty()
        {
            var channel = new VectorSetCleanupWorkChannel<int>(new VectorSetCleanupWorkCounter());

            ClassicAssert.IsFalse(channel.TryAcquire(out _));
        }

        [Test]
        public async Task CompletedChannelRejectsPublishesAndRegistersNothing()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var channel = new VectorSetCleanupWorkChannel<int>(counter);
            channel.CompleteAndWaitForConsumerTask(Task.CompletedTask);

            ClassicAssert.IsFalse(channel.TryPublish(1));
            ClassicAssert.AreEqual(0, counter.Inflight);
            ClassicAssert.IsFalse(await channel.WaitToReadAsync());
        }

        [Test]
        public void CompleteAndWaitForConsumerTaskDrainsBeforeReturning()
        {
            var counter = new VectorSetCleanupWorkCounter();
            var channel = new VectorSetCleanupWorkChannel<int>(counter);

            var consumer = Task.Run(async () =>
            {
                while (await channel.WaitToReadAsync())
                {
                    channel.ReadAllAvailable().Dispose();
                }
            });

            _ = channel.TryPublish(1);
            channel.CompleteAndWaitForConsumerTask(consumer);

            ClassicAssert.IsFalse(channel.HasPending);
            ClassicAssert.AreEqual(0, counter.Inflight);
        }
    }
}