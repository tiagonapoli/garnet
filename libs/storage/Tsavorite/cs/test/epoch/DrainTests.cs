// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// The drain list: deferred actions attached to an epoch, which must run once the epoch they were
    /// registered against becomes safe to reclaim, and must not run one moment sooner.
    /// </summary>
    [TestFixture]
    public class DrainTests
    {
        LightEpoch epoch;

        [SetUp]
        public void Setup() => epoch = new LightEpoch();

        [TearDown]
        public void TearDown()
        {
            epoch.Dispose();
            epoch = null;
        }

        [Test]
        public void ActionRunsImmediatelyWhenNobodyElseIsProtected()
        {
            var ran = 0;

            epoch.Resume();
            try
            {
                epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref ran));
                Assert.That(Volatile.Read(ref ran), Is.EqualTo(1), "with no other thread protected the action's epoch is immediately reclaimable");
            }
            finally
            {
                epoch.Suspend();
            }
        }

        /// <summary>
        /// <see cref="LightEpoch.Suspend"/> drains on the way out when it is the last thread to leave,
        /// so an action registered while others were protected still runs without anyone calling
        /// <see cref="LightEpoch.ProtectAndDrain"/> afterwards.
        /// </summary>
        [Test]
        public void TheLastThreadToSuspendRunsPendingActions()
        {
            var drained = 0;
            using var reader = new ParkedReader(epoch);

            epoch.Resume();
            try
            {
                epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref drained));
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(Volatile.Read(ref drained), Is.Zero, "the action ran while a reader was still protected");

            reader.LeaveAndJoin();

            Assert.That(Volatile.Read(ref drained), Is.EqualTo(1), "the last thread to suspend must drain the pending action itself");
        }

        [Test]
        public void EveryActionRunsExactlyOnceWhenTheDrainListFills()
        {
            var capacity = LightEpoch.DrainListCapacity;
            var counts = new int[capacity];

            using var reader = new ParkedReader(epoch);

            epoch.Resume();
            try
            {
                for (var i = 0; i < capacity; i++)
                {
                    var index = i;
                    epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref counts[index]));
                }
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(counts, Is.All.Zero, "actions ran while a reader was still protected");

            reader.LeaveAndJoin();

            Assert.That(counts, Is.All.EqualTo(1), $"every registered action must run exactly once; got [{string.Join(",", counts)}]");
        }

        /// <summary>
        /// The drain list is finite. When it is full and nothing can be reclaimed, registering another
        /// action must block rather than drop it, and must complete once the blocker leaves.
        /// </summary>
        [Test]
        public void RegisteringBlocksWhileTheDrainListIsFullAndCompletesAfterwards()
        {
            var capacity = LightEpoch.DrainListCapacity;
            var counts = new int[capacity];
            var extraRan = 0;

            using var reader = new ParkedReader(epoch);

            epoch.Resume();
            try
            {
                for (var i = 0; i < capacity; i++)
                {
                    var index = i;
                    epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref counts[index]));
                }

                using var registered = new ManualResetEventSlim();
                var latecomer = new Thread(() =>
                {
                    epoch.Resume();
                    try
                    {
                        epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref extraRan));
                        registered.Set();
                    }
                    finally
                    {
                        epoch.Suspend();
                    }
                })
                { IsBackground = true };
                latecomer.Start();

                Assert.That(registered.Wait(TimeSpan.FromMilliseconds(500)), Is.False, "registered an action into a full drain list while nothing was reclaimable");

                reader.LeaveAndJoin();

                Assert.That(registered.Wait(TimeSpan.FromSeconds(30)), Is.True, "the blocked registration never completed after the drain list could be emptied");
                Assert.That(latecomer.Join(TimeSpan.FromSeconds(30)), Is.True);

                Assert.That(counts, Is.All.EqualTo(1), "the backlog did not drain exactly once each");
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void ActionsRunInEpochOrder()
        {
            const int ActionCount = 8;
            var order = new System.Collections.Concurrent.ConcurrentQueue<int>();

            using var reader = new ParkedReader(epoch);

            epoch.Resume();
            try
            {
                for (var i = 0; i < ActionCount; i++)
                {
                    var index = i;
                    epoch.BumpCurrentEpoch(() => order.Enqueue(index));
                }
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(order, Is.Empty);

            reader.LeaveAndJoin();

            Assert.That(order.ToArray(), Is.EqualTo(Enumerable.Range(0, ActionCount).ToArray()), "actions registered against increasing epochs must drain in that order");
        }

        [Test]
        public void ManyThreadsRegisteringActionsAllRunExactlyOnce()
        {
            const int ThreadCount = 8;
            const int PerThread = 200;

            var counts = new int[ThreadCount * PerThread];
            var threads = new Thread[ThreadCount];
            using var start = new ManualResetEventSlim();

            for (var t = 0; t < ThreadCount; t++)
            {
                var threadIndex = t;
                threads[t] = new Thread(() =>
                {
                    start.Wait();
                    for (var i = 0; i < PerThread; i++)
                    {
                        var index = (threadIndex * PerThread) + i;
                        epoch.Resume();
                        try
                        {
                            epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref counts[index]));
                        }
                        finally
                        {
                            epoch.Suspend();
                        }
                    }
                })
                { IsBackground = true };
                threads[t].Start();
            }

            start.Set();
            foreach (var thread in threads)
                Assert.That(thread.Join(TimeSpan.FromMinutes(2)), Is.True, "worker did not finish");

            // Anything still pending drains on the next quiescent pass.
            epoch.Resume();
            try
            {
                _ = epoch.BumpCurrentEpoch();
                epoch.ProtectAndDrain();
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(counts, Is.All.EqualTo(1), $"{counts.Count(c => c == 0)} actions never ran and {counts.Count(c => c > 1)} ran more than once");
        }

        [Test]
        public void ActionDoesNotRunWhileAnotherThreadIsProtected()
        {
            using var protectedThreadEntered = new ManualResetEventSlim();
            using var releaseProtectedThread = new ManualResetEventSlim();

            var drained = 0;

            var reader = new Thread(() =>
            {
                epoch.Resume();
                protectedThreadEntered.Set();
                releaseProtectedThread.Wait();
                epoch.Suspend();
            })
            { IsBackground = true };
            reader.Start();
            Assert.That(protectedThreadEntered.Wait(TimeSpan.FromSeconds(30)), Is.True);

            epoch.Resume();
            epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref drained));
            epoch.Suspend();

            Assert.That(Volatile.Read(ref drained), Is.Zero, "action drained while a thread was still protected");

            releaseProtectedThread.Set();
            Assert.That(reader.Join(TimeSpan.FromSeconds(30)), Is.True);

            epoch.Resume();
            epoch.ProtectAndDrain();
            epoch.Suspend();

            Assert.That(Volatile.Read(ref drained), Is.EqualTo(1));
        }
    }
}
