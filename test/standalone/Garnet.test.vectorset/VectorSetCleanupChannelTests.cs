// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Unit tests for <see cref="VectorSetCleanupChannel{T}"/>.
    ///
    /// These cover the corner cases CleanupChannel.tla identifies: that publishing is registered
    /// before the item is visible, that a lease releases its registration on every exit path
    /// (including the abnormal ones that hand-written accounting tends to miss), and that a batch
    /// holds its registrations until it is disposed so a hand-off cannot dip through zero.
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupChannelTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static VectorSetCleanupChannel<object> NewChannel(out VectorSetCleanupTracker tracker)
        {
            tracker = new VectorSetCleanupTracker();
            return new VectorSetCleanupChannel<object>(tracker, singleWriter: false);
        }

        [Test]
        public void PublishRegistersBeforeTheItemIsVisible()
        {
            var channel = NewChannel(out var tracker);

            ClassicAssert.AreEqual(0, tracker.InFlight);
            ClassicAssert.IsTrue(channel.TryPublish(null));

            // The registration is already in place at the instant the item became readable, which is
            // what stops a drain from observing an empty pipeline before the consumer wakes.
            ClassicAssert.AreEqual(1, tracker.InFlight);
            ClassicAssert.IsTrue(channel.HasPending);
            ClassicAssert.IsFalse(tracker.WaitAllCleanupsAsync().IsCompleted);
        }

        [Test]
        public void ReadingDoesNotCompleteUntilTheLeaseIsDisposed()
        {
            var channel = NewChannel(out var tracker);
            _ = channel.TryPublish(null);

            ClassicAssert.IsTrue(channel.TryRead(out var lease));
            ClassicAssert.AreEqual(1, tracker.InFlight, "Dequeuing is not completing - the consumer now owns the work");

            lease.Dispose();
            ClassicAssert.AreEqual(0, tracker.InFlight);
        }

        [Test]
        public void LeaseReleasesOnEarlyContinue()
        {
            var channel = NewChannel(out var tracker);
            _ = channel.TryPublish(null);
            _ = channel.TryPublish(null);

            var seen = 0;
            while (channel.TryRead(out var work))
            {
                using (work)
                {
                    seen++;
                    continue;
                }
            }

            ClassicAssert.AreEqual(2, seen);
            ClassicAssert.AreEqual(0, tracker.InFlight, "An early continue must not leak a registration");
        }

        [Test]
        public void LeaseReleasesOnThrow()
        {
            var channel = NewChannel(out var tracker);
            _ = channel.TryPublish(null);

            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                ClassicAssert.IsTrue(channel.TryRead(out var work));
                using (work)
                {
                    throw new InvalidOperationException("scan failed");
                }
            });

            // This is the case CleanupChannel.tla flags as unrecoverable: a leak here can never be
            // undone, so every later WaitAllCleanupsAsync would block forever.
            ClassicAssert.AreEqual(0, tracker.InFlight, "A throw must not leak a registration");
            ClassicAssert.IsTrue(tracker.WaitAllCleanupsAsync().IsCompleted);
        }

        [Test]
        public void BatchHoldsEveryRegistrationUntilDisposed()
        {
            var channel = NewChannel(out var tracker);
            _ = channel.TryPublish(null);
            _ = channel.TryPublish(null);
            _ = channel.TryPublish(null);

            ClassicAssert.AreEqual(3, tracker.InFlight);

            using (var batch = channel.ReadAllAvailable())
            {
                ClassicAssert.AreEqual(3, batch.Items.Count);
                ClassicAssert.AreEqual(3, tracker.InFlight, "Draining the channel must not complete anything");
                ClassicAssert.IsFalse(channel.HasPending);
            }

            ClassicAssert.AreEqual(0, tracker.InFlight);
        }

        [Test]
        public void EmptyBatchIsHarmless()
        {
            var channel = NewChannel(out var tracker);

            using (var batch = channel.ReadAllAvailable())
            {
                ClassicAssert.AreEqual(0, batch.Items.Count);
            }

            ClassicAssert.AreEqual(0, tracker.InFlight);
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
        }

        [Test]
        public void HandOffPublishedBeforeBatchDisposalNeverDipsToZero()
        {
            var tracker = new VectorSetCleanupTracker();
            var upstream = new VectorSetCleanupChannel<object>(tracker, singleWriter: false);
            var downstream = new VectorSetCleanupChannel<object>(tracker, singleWriter: false);

            _ = upstream.TryPublish(null);

            var wait = tracker.WaitAllCleanupsAsync();

            using (var batch = upstream.ReadAllAvailable())
            {
                ClassicAssert.AreEqual(1, batch.Items.Count);

                // The successor is published while the batch is still held, exactly as the marking
                // stage does in its finally.
                _ = downstream.TryPublish(null);
                ClassicAssert.AreEqual(2, tracker.InFlight);
            }

            ClassicAssert.AreEqual(1, tracker.InFlight);
            ClassicAssert.IsFalse(wait.IsCompleted, "The waiter must not be released across a hand-off");

            ClassicAssert.IsTrue(downstream.TryRead(out var work));
            work.Dispose();

            ClassicAssert.IsTrue(wait.Wait(Timeout));
        }

        [Test]
        public void HandOffPublishedAfterBatchDisposalReleasesAWaiterEarly()
        {
            var tracker = new VectorSetCleanupTracker();
            var upstream = new VectorSetCleanupChannel<object>(tracker, singleWriter: false);
            var downstream = new VectorSetCleanupChannel<object>(tracker, singleWriter: false);

            _ = upstream.TryPublish(null);

            var wait = tracker.WaitAllCleanupsAsync();

            var batch = upstream.ReadAllAvailable();
            ClassicAssert.AreEqual(1, batch.Items.Count);
            batch.Dispose();

            // Demonstrates why the marking stage declares its batch outside the try: disposing before
            // the successor is published lets a drain through with the scan still owed.
            ClassicAssert.IsTrue(wait.Wait(Timeout), "This is the hazard being guarded against");

            _ = downstream.TryPublish(null);
            ClassicAssert.AreEqual(1, tracker.InFlight);
        }

        [Test]
        public void PublishAfterCompletionRegistersNothing()
        {
            var channel = NewChannel(out var tracker);

            channel.CompleteWriter();

            ClassicAssert.IsFalse(channel.TryPublish(null));
            ClassicAssert.AreEqual(0, tracker.InFlight, "A rejected publish must roll its registration back");
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
            ClassicAssert.IsTrue(tracker.WaitAllCleanupsAsync().IsCompleted);
        }

        [Test]
        public void RequeueingFromInsideALeaseKeepsWorkOutstanding()
        {
            var channel = NewChannel(out var tracker);
            _ = channel.TryPublish(null);

            var wait = tracker.WaitAllCleanupsAsync();

            ClassicAssert.IsTrue(channel.TryRead(out var work));
            using (work)
            {
                // What the scan's catch block does: the contexts are still dirty, so requeue before
                // this lease releases or the count reaches zero with the scan still owed.
                _ = channel.TryPublish(null);
            }

            ClassicAssert.AreEqual(1, tracker.InFlight);
            ClassicAssert.IsFalse(wait.IsCompleted);

            ClassicAssert.IsTrue(channel.TryRead(out var retry));
            retry.Dispose();

            ClassicAssert.IsTrue(wait.Wait(Timeout));
        }

        [Test]
        public async Task WaitToReadAsyncCompletesOnPublishAndOnWriterCompletion()
        {
            var channel = NewChannel(out _);

            var pending = channel.WaitToReadAsync();
            ClassicAssert.IsFalse(pending.IsCompleted);

            _ = channel.TryPublish(null);
            ClassicAssert.IsTrue(await pending);

            ClassicAssert.IsTrue(channel.TryRead(out var work));
            work.Dispose();

            channel.CompleteWriter();
            ClassicAssert.IsFalse(await channel.WaitToReadAsync());
            ClassicAssert.IsTrue(channel.Completion.Wait(Timeout));
        }

        [Test]
        public void ProducersAndConsumerStayBalancedUnderLoad()
        {
            const int Producers = 4;
            const int PerProducer = 2000;

            var channel = NewChannel(out var tracker);
            var consumed = 0;

            var consumer = Task.Run(async () =>
            {
                while (await channel.WaitToReadAsync())
                {
                    while (channel.TryRead(out var work))
                    {
                        using (work)
                        {
                            consumed++;

                            // Exercise the abnormal exit path on a slice of the load.
                            if (consumed % 7 == 0)
                            {
                                continue;
                            }
                        }
                    }
                }
            });

            var producers = new Task[Producers];
            for (var i = 0; i < Producers; i++)
            {
                producers[i] = Task.Run(() =>
                {
                    for (var j = 0; j < PerProducer; j++)
                    {
                        _ = channel.TryPublish(null);
                    }
                });
            }

            ClassicAssert.IsTrue(Task.WaitAll(producers, Timeout));
            ClassicAssert.IsTrue(tracker.WaitAllCleanupsAsync().Wait(Timeout), "Drain must observe quiescence");

            channel.CompleteWriter();
            ClassicAssert.IsTrue(consumer.Wait(Timeout));

            ClassicAssert.AreEqual(Producers * PerProducer, consumed);
            ClassicAssert.AreEqual(0, tracker.InFlight);
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
        }
    }
}