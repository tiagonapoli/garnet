// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// The epoch table itself: slot handout, recycling, and the semaphore slow path taken when every
    /// slot is occupied.
    /// </summary>
    [TestFixture]
    public class LightEpochEntryTableTests
    {
        static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        LightEpoch epoch;

        [SetUp]
        public void Setup() => epoch = new LightEpoch();

        [TearDown]
        public void TearDown()
        {
            epoch?.Dispose();
            epoch = null;
        }

        [Test]
        public void EntryCountIsAtLeastTheMinimumTableSize()
        {
            Assert.That(epoch.EntryCount, Is.GreaterThanOrEqualTo(128));
            Assert.That(epoch.EntryCount, Is.GreaterThanOrEqualTo(Environment.ProcessorCount));
        }

        [Test]
        public void ConcurrentThreadsGetDistinctSlots()
        {
            const int ThreadCount = 32;

            var entries = new int[ThreadCount];
            using var allIn = new CountdownEvent(ThreadCount);
            using var release = new ManualResetEventSlim();

            var threads = new Thread[ThreadCount];
            for (var t = 0; t < ThreadCount; t++)
            {
                var index = t;
                threads[t] = new Thread(() =>
                {
                    epoch.Resume();
                    entries[index] = epoch.ThisThreadEntry();
                    _ = allIn.Signal();
                    release.Wait();
                    epoch.Suspend();
                })
                { IsBackground = true };
                threads[t].Start();
            }

            Assert.That(allIn.Wait(Generous), Is.True, "threads did not all acquire a slot");
            Assert.That(entries, Is.Unique, "two concurrently protected threads shared a slot");
            Assert.That(entries, Is.All.GreaterThan(0));

            release.Set();
            foreach (var thread in threads)
                Assert.That(thread.Join(Generous), Is.True);

            for (var entry = 1; entry <= epoch.EntryCount; entry++)
                Assert.That(epoch.AnnouncedEpochAt(entry), Is.Zero, $"slot {entry} was left announced");
        }

        /// <summary>
        /// With every slot taken, the next thread must park on the semaphore rather than spin or fail,
        /// and must be woken by the first release.
        /// </summary>
        [Test]
        public void AFullTableParksTheNextThreadUntilASlotIsFreed()
        {
            var capacity = epoch.EntryCount;

            using var allIn = new CountdownEvent(capacity);
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseRest = new ManualResetEventSlim();

            var holders = new Thread[capacity];
            for (var i = 0; i < capacity; i++)
            {
                var isFirst = i == 0;
                holders[i] = new Thread(() =>
                {
                    epoch.Resume();
                    _ = allIn.Signal();
                    (isFirst ? releaseFirst : releaseRest).Wait();
                    epoch.Suspend();
                })
                { IsBackground = true };
                holders[i].Start();
            }

            try
            {
                Assert.That(allIn.Wait(Generous), Is.True, "could not fill the epoch table");

                using var acquired = new ManualResetEventSlim();
                var latecomer = new Thread(() =>
                {
                    epoch.Resume();
                    acquired.Set();
                    epoch.Suspend();
                })
                { IsBackground = true };
                latecomer.Start();

                Assert.That(acquired.Wait(TimeSpan.FromMilliseconds(500)), Is.False,
                    "a thread acquired a slot from a completely full table");

                _ = SpinWait.SpinUntil(() => epoch.WaiterCount > 0, Generous);
                Assert.That(epoch.WaiterCount, Is.GreaterThan(0),
                    "the blocked thread never parked on the waiter semaphore");

                releaseFirst.Set();

                Assert.That(acquired.Wait(Generous), Is.True, "freeing a slot did not wake the waiter");
                Assert.That(latecomer.Join(Generous), Is.True);
            }
            finally
            {
                releaseFirst.Set();
                releaseRest.Set();
                foreach (var holder in holders)
                    _ = holder.Join(Generous);
            }

            Assert.That(epoch.WaiterCount, Is.Zero);
        }

        /// <summary>
        /// Disposing while a thread is parked waiting for a slot must unblock it with
        /// <see cref="ObjectDisposedException"/> rather than leave it stuck on the semaphore.
        /// </summary>
        [Test]
        public void DisposeUnblocksWaitersWithObjectDisposedException()
        {
            var disposable = new LightEpoch();
            var capacity = disposable.EntryCount;

            using var allIn = new CountdownEvent(capacity);
            using var release = new ManualResetEventSlim();

            var holders = new Thread[capacity];
            for (var i = 0; i < capacity; i++)
            {
                holders[i] = new Thread(() =>
                {
                    disposable.Resume();
                    _ = allIn.Signal();
                    release.Wait();
                    disposable.Suspend();
                })
                { IsBackground = true };
                holders[i].Start();
            }

            try
            {
                Assert.That(allIn.Wait(Generous), Is.True, "could not fill the epoch table");

                Exception caught = null;
                var waiter = new Thread(() =>
                {
                    try
                    {
                        disposable.Resume();
                        disposable.Suspend();
                    }
                    catch (Exception e)
                    {
                        caught = e;
                    }
                })
                { IsBackground = true };
                waiter.Start();

                _ = SpinWait.SpinUntil(() => disposable.WaiterCount > 0, Generous);
                Assert.That(disposable.WaiterCount, Is.GreaterThan(0), "the thread never parked");

                disposable.Dispose();

                Assert.That(waiter.Join(Generous), Is.True, "the parked thread was never released by Dispose");
                Assert.That(caught, Is.TypeOf<ObjectDisposedException>(),
                    $"expected ObjectDisposedException, got {caught?.GetType().Name ?? "no exception"}");
            }
            finally
            {
                release.Set();
                foreach (var holder in holders)
                    _ = holder.Join(Generous);
            }
        }
    }
}
