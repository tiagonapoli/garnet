// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// The reclamation frontier. <see cref="LightEpoch.SafeToReclaimEpoch"/> is the value the whole
    /// structure exists to compute: everything at or below it may be freed. Every test here is a
    /// statement about it never overtaking a thread that is still inside a protected region.
    /// </summary>
    [TestFixture]
    public class LightEpochReclamationTests
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
        public void AnEmptyTableAnnouncesNothing()
        {
            Assert.That(epoch.MinAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));

            for (var entry = 1; entry <= epoch.EntryCount; entry++)
            {
                Assert.That(epoch.AnnouncedEpochAt(entry), Is.Zero);
                Assert.That(epoch.ThreadIdAt(entry), Is.Zero);
            }
        }

        [Test]
        public void ProtectingLowersTheAnnouncedMinimum()
        {
            using var reader = new ParkedReader(epoch);
            Assert.That(epoch.MinAnnouncedEpoch(), Is.EqualTo(reader.AnnouncedEpoch));

            epoch.Resume();
            try
            {
                for (var i = 0; i < 8; i++)
                    _ = epoch.BumpCurrentEpoch();

                Assert.That(epoch.CurrentEpoch, Is.GreaterThan(reader.AnnouncedEpoch));
                Assert.That(epoch.MinAnnouncedEpoch(), Is.EqualTo(reader.AnnouncedEpoch),
                    "the parked reader is the oldest announcement and must define the minimum");
            }
            finally
            {
                epoch.Suspend();
            }
        }

        /// <summary>
        /// The core safety property: an epoch a live thread has announced is never declared safe to
        /// reclaim, no matter how far the global epoch runs ahead of it.
        /// </summary>
        [Test]
        public void SafeToReclaimNeverReachesALiveAnnouncedEpoch()
        {
            using var reader = new ParkedReader(epoch);

            epoch.Resume();
            try
            {
                for (var i = 0; i < 64; i++)
                {
                    _ = epoch.BumpCurrentEpoch();
                    epoch.ProtectAndDrain();

                    Assert.That(epoch.SafeToReclaimEpoch, Is.LessThan(reader.AnnouncedEpoch),
                        "an epoch that a live reader had announced was declared safe to reclaim");
                    Assert.That(epoch.SafeToReclaimEpoch, Is.LessThan(epoch.MinAnnouncedEpoch()),
                        "SafeToReclaimEpoch overtook the oldest announcement in the table");
                }
            }
            finally
            {
                epoch.Suspend();
            }

            reader.LeaveAndJoin();

            epoch.Resume();
            try
            {
                _ = epoch.BumpCurrentEpoch();
                Assert.That(epoch.SafeToReclaimEpoch, Is.GreaterThanOrEqualTo(reader.AnnouncedEpoch),
                    "once the reader left, the epoch it held must become reclaimable");
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void SafeToReclaimAdvancesWhenNobodyIsProtected()
        {
            epoch.Resume();
            long announced;
            try
            {
                announced = epoch.ThisThreadAnnouncedEpoch();
                _ = epoch.BumpCurrentEpoch();
            }
            finally
            {
                epoch.Suspend();
            }

            // The only announcement was this thread's own, so everything before it is reclaimable.
            Assert.That(epoch.SafeToReclaimEpoch, Is.EqualTo(announced - 1));

            epoch.Resume();
            try
            {
                _ = epoch.BumpCurrentEpoch();
                Assert.That(epoch.SafeToReclaimEpoch, Is.GreaterThanOrEqualTo(announced),
                    "the previously announced epoch stayed unreclaimable after the thread suspended");
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void TheOldestOfManyReadersDefinesTheFrontier()
        {
            const int ReaderCount = 8;
            var readers = new ParkedReader[ReaderCount];
            try
            {
                for (var i = 0; i < ReaderCount; i++)
                {
                    readers[i] = new ParkedReader(epoch);

                    epoch.Resume();
                    try
                    {
                        _ = epoch.BumpCurrentEpoch();
                    }
                    finally
                    {
                        epoch.Suspend();
                    }
                }

                // Each reader entered one epoch later than the last, so the first one is the oldest.
                var oldest = readers[0].AnnouncedEpoch;
                for (var i = 1; i < ReaderCount; i++)
                    Assert.That(readers[i].AnnouncedEpoch, Is.GreaterThan(oldest));

                Assert.That(epoch.MinAnnouncedEpoch(), Is.EqualTo(oldest));

                epoch.Resume();
                try
                {
                    _ = epoch.BumpCurrentEpoch();
                    Assert.That(epoch.SafeToReclaimEpoch, Is.EqualTo(oldest - 1));
                }
                finally
                {
                    epoch.Suspend();
                }

                // Retiring the oldest reader must move the frontier up to the next oldest, and no further.
                readers[0].LeaveAndJoin();

                epoch.Resume();
                try
                {
                    _ = epoch.BumpCurrentEpoch();
                    Assert.That(epoch.SafeToReclaimEpoch, Is.EqualTo(readers[1].AnnouncedEpoch - 1));
                }
                finally
                {
                    epoch.Suspend();
                }
            }
            finally
            {
                foreach (var reader in readers)
                    reader?.Dispose();
            }
        }

        /// <summary>
        /// Under contention the frontier must never overtake the announcement of a thread that stays
        /// protected: its slot is non-zero for the whole round, so every scan of the table sees it and
        /// clamps the minimum. Checker threads therefore hold their region for the whole run while
        /// churn threads hammer the table with acquire/release traffic.
        /// <para>
        /// The check is deliberately not made across <see cref="LightEpoch.Resume"/>: Acquire samples
        /// CurrentEpoch before spinning for a free slot, so a thread that loses the race can announce
        /// an epoch that has already been reclaimed. That is conservative rather than unsafe -- it only
        /// drags the frontier back -- but it means the strict inequality is not a global invariant.
        /// </para>
        /// </summary>
        [Test]
        public void SafeToReclaimStaysBehindEveryLiveReaderUnderContention()
        {
            const int CheckerCount = 4;
            const int ChurnCount = 4;
            const int Rounds = 20_000;

            var violations = 0;
            var stopChurn = false;
            using var start = new ManualResetEventSlim();

            var checkers = new Thread[CheckerCount];
            for (var t = 0; t < CheckerCount; t++)
            {
                checkers[t] = new Thread(() =>
                {
                    start.Wait();
                    epoch.Resume();
                    try
                    {
                        for (var r = 0; r < Rounds; r++)
                        {
                            if ((r & 63) == 0)
                                _ = epoch.BumpCurrentEpoch();
                            else
                                epoch.ProtectAndDrain();

                            var announced = epoch.ThisThreadAnnouncedEpoch();
                            if (Volatile.Read(ref epoch.SafeToReclaimEpoch) >= announced)
                                _ = Interlocked.Increment(ref violations);
                        }
                    }
                    finally
                    {
                        epoch.Suspend();
                    }
                })
                { IsBackground = true };
                checkers[t].Start();
            }

            var churn = new Thread[ChurnCount];
            for (var t = 0; t < ChurnCount; t++)
            {
                churn[t] = new Thread(() =>
                {
                    start.Wait();
                    while (!Volatile.Read(ref stopChurn))
                    {
                        epoch.Resume();
                        epoch.ProtectAndDrain();
                        epoch.Suspend();
                    }
                })
                { IsBackground = true };
                churn[t].Start();
            }

            start.Set();
            foreach (var thread in checkers)
                Assert.That(thread.Join(TimeSpan.FromMinutes(2)), Is.True, "checker did not finish");

            Volatile.Write(ref stopChurn, true);
            foreach (var thread in churn)
                Assert.That(thread.Join(TimeSpan.FromMinutes(1)), Is.True, "churn thread did not finish");

            Assert.That(Volatile.Read(ref violations), Is.Zero,
                "SafeToReclaimEpoch reached an epoch a thread was still announcing");
        }
    }
}
