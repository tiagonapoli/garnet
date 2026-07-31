// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// The per-thread user-word API, which lends subsystems the spare 48 bytes of the epoch table's
    /// cache line. Slots are claimed by bitmask CAS, and the values are owned by the application.
    /// </summary>
    [TestFixture]
    public class UserWordTests
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
        public void AllocationHandsOutEverySlotOnceThenThrows()
        {
            var words = new List<int>();
            try
            {
                for (var i = 0; i < LightEpoch.MaxUserWords; i++)
                    words.Add(epoch.AllocateUserWord(0));

                Assert.That(words, Is.Unique);
                Assert.That(words, Is.EquivalentTo(Enumerable.Range(0, LightEpoch.MaxUserWords)));
                _ = Assert.Throws<InvalidOperationException>(() => epoch.AllocateUserWord(0));
            }
            finally
            {
                foreach (var word in words)
                    epoch.ReleaseUserWord(word);
            }
        }

        [Test]
        public void AReleasedSlotIsHandedOutAgain()
        {
            var first = epoch.AllocateUserWord(0);
            epoch.ReleaseUserWord(first);

            var second = epoch.AllocateUserWord(0);
            try
            {
                Assert.That(second, Is.EqualTo(first));
            }
            finally
            {
                epoch.ReleaseUserWord(second);
            }
        }

        [Test]
        public void ReleaseRejectsAnIndexOutsideTheTable()
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => epoch.ReleaseUserWord(-1));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => epoch.ReleaseUserWord(LightEpoch.MaxUserWords));
        }

        [Test]
        public void AllocationInitializesTheWholeColumn()
        {
            var word = epoch.AllocateUserWord(1234);
            try
            {
                Assert.That(epoch.GetMinUserWord(word), Is.EqualTo(1234), "every entry's slot must carry the initial value, or the minimum is meaningless");
            }
            finally
            {
                epoch.ReleaseUserWord(word);
            }
        }

        [Test]
        public void SlotsDoNotInterfereWithEachOther()
        {
            var first = epoch.AllocateUserWord(10);
            var second = epoch.AllocateUserWord(20);
            try
            {
                using (epoch.Protected())
                {
                    epoch.ThisThreadUserWord(first) = 5;

                    Assert.That(epoch.GetMinUserWord(first), Is.EqualTo(5));
                    Assert.That(epoch.GetMinUserWord(second), Is.EqualTo(20), "a write bled into the neighbouring slot");
                }
            }
            finally
            {
                epoch.ReleaseUserWord(first);
                epoch.ReleaseUserWord(second);
            }
        }

        [Test]
        public void EverySlotIndexIsAddressableIndependently()
        {
            var words = new List<int>();
            try
            {
                for (var i = 0; i < LightEpoch.MaxUserWords; i++)
                    words.Add(epoch.AllocateUserWord(long.MaxValue));

                using (epoch.Protected())
                {
                    for (var i = 0; i < words.Count; i++)
                        epoch.ThisThreadUserWord(words[i]) = 100 + i;

                    for (var i = 0; i < words.Count; i++)
                        Assert.That(epoch.GetMinUserWord(words[i]), Is.EqualTo(100 + i), $"slot {words[i]} did not hold its own value");
                }
            }
            finally
            {
                foreach (var word in words)
                    epoch.ReleaseUserWord(word);
            }
        }

        /// <summary>
        /// Documented contract: the application owns the slot contents, so LightEpoch must not clear
        /// them on Suspend. A subsystem tracking a per-thread watermark relies on this.
        /// </summary>
        [Test]
        public void AWordSurvivesSuspend()
        {
            var word = epoch.AllocateUserWord(long.MaxValue);
            try
            {
                epoch.Resume();
                epoch.ThisThreadUserWord(word) = 42;
                epoch.Suspend();

                Assert.That(epoch.GetMinUserWord(word), Is.EqualTo(42), "LightEpoch must not reset user words when the thread suspends");
            }
            finally
            {
                epoch.ReleaseUserWord(word);
            }
        }

        [Test]
        public void MinimumTracksTheLowestValueAcrossThreads()
        {
            const int ThreadCount = 8;

            var word = epoch.AllocateUserWord(long.MaxValue);
            try
            {
                using var allWritten = new CountdownEvent(ThreadCount);
                using var release = new ManualResetEventSlim();

                var threads = new Thread[ThreadCount];
                for (var t = 0; t < ThreadCount; t++)
                {
                    var value = 100L + t;
                    threads[t] = new Thread(() =>
                    {
                        epoch.Resume();
                        epoch.ThisThreadUserWord(word) = value;
                        _ = allWritten.Signal();
                        release.Wait();
                        epoch.Suspend();
                    })
                    { IsBackground = true };
                    threads[t].Start();
                }

                Assert.That(allWritten.Wait(TimeSpan.FromSeconds(30)), Is.True);
                Assert.That(epoch.GetMinUserWord(word), Is.EqualTo(100L));

                release.Set();
                foreach (var thread in threads)
                    Assert.That(thread.Join(TimeSpan.FromSeconds(30)), Is.True);
            }
            finally
            {
                epoch.ReleaseUserWord(word);
            }
        }

        [Test]
        public void ConcurrentAllocationNeverHandsOutTheSameSlotTwice()
        {
            const int Rounds = 500;

            var duplicates = 0;
            var threads = new Thread[LightEpoch.MaxUserWords];
            using var start = new ManualResetEventSlim();
            var live = new int[LightEpoch.MaxUserWords];

            for (var t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    start.Wait();
                    for (var r = 0; r < Rounds; r++)
                    {
                        var word = epoch.AllocateUserWord(0);
                        if (Interlocked.Increment(ref live[word]) != 1)
                            _ = Interlocked.Increment(ref duplicates);

                        _ = Interlocked.Decrement(ref live[word]);
                        epoch.ReleaseUserWord(word);
                    }
                })
                { IsBackground = true };
                threads[t].Start();
            }

            start.Set();
            foreach (var thread in threads)
                Assert.That(thread.Join(TimeSpan.FromMinutes(1)), Is.True);

            Assert.That(Volatile.Read(ref duplicates), Is.Zero, "the same user-word slot was allocated twice at once");
        }
    }
}