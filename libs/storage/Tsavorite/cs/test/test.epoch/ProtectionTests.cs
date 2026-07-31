// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// The protection lifecycle: what <see cref="LightEpoch.Resume"/>, <see cref="LightEpoch.Suspend"/>
    /// and the refresh path leave behind in the epoch table.
    /// </summary>
    [TestFixture]
    public class ProtectionTests
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
        public void UnprotectedThreadHoldsNoSlot()
        {
            Assert.That(epoch.ThisInstanceProtected(), Is.False);
            Assert.That(epoch.ThisThreadEntry(), Is.Zero);
            Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.Zero);
            Assert.That(epoch.TrySuspend(), Is.False);
        }

        [Test]
        public void SlotIndexIsWithinTheTable()
        {
            epoch.Resume();
            try
            {
                var entry = epoch.ThisThreadEntry();
                Assert.That(entry, Is.GreaterThan(0));
                Assert.That(entry, Is.LessThanOrEqualTo(epoch.EntryCount));
                Assert.That(epoch.ThreadIdAt(entry), Is.EqualTo(Environment.CurrentManagedThreadId));
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void SuspendLeavesTheSlotCompletelyFree()
        {
            epoch.Resume();
            var entry = epoch.ThisThreadEntry();
            epoch.Suspend();

            Assert.That(epoch.AnnouncedEpochAt(entry), Is.Zero, "the announced epoch was left behind");
            Assert.That(epoch.ThreadIdAt(entry), Is.Zero, "the thread id was left behind");
            Assert.That(epoch.ThisThreadEntry(), Is.Zero);
        }

        /// <summary>
        /// The claim CAS uses 0 as "slot free", so a protected thread announcing 0 would make a live
        /// slot look reclaimable and claimable. CurrentEpoch starts at 1 and only ever increases.
        /// </summary>
        [Test]
        public void AProtectedThreadNeverAnnouncesEpochZero()
        {
            Assert.That(epoch.CurrentEpoch, Is.GreaterThan(0));

            for (var i = 0; i < 32; i++)
            {
                epoch.Resume();
                try
                {
                    Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    _ = epoch.BumpCurrentEpoch();
                    Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    epoch.ProtectAndDrain();
                    Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    Assert.That(epoch.CurrentEpoch, Is.GreaterThan(0));
                }
                finally
                {
                    epoch.Suspend();
                }
            }
        }

        [Test]
        public void SuspendResumeKeepsTheThreadProtected()
        {
            epoch.Resume();
            try
            {
                epoch.SuspendResume();

                Assert.That(epoch.ThisInstanceProtected(), Is.True);
                Assert.That(epoch.ThisThreadEntry(), Is.GreaterThan(0));
                Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void RefreshRepublishesTheLatestEpochEveryTime()
        {
            epoch.Resume();
            try
            {
                for (var i = 0; i < 16; i++)
                {
                    _ = epoch.BumpCurrentEpoch();
                    epoch.ProtectAndDrain();
                    Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
                }
            }
            finally
            {
                epoch.Suspend();
            }
        }

        [Test]
        public void ProtectionSurvivesRepeatedResumeSuspendCycles()
        {
            for (var i = 0; i < 1_000; i++)
            {
                epoch.Resume();
                Assert.That(epoch.ThisInstanceProtected(), Is.True);
                epoch.Suspend();
                Assert.That(epoch.ThisInstanceProtected(), Is.False);
            }
        }

        [Test]
        public void ToStringReportsProtectedThreads()
        {
            epoch.Resume();
            try
            {
                var description = epoch.ToString();
                Assert.That(description, Does.Contain("CurrentEpoch"));
                Assert.That(description, Does.Contain($"tid={Environment.CurrentManagedThreadId}"));
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(epoch.ToString(), Does.Contain("none]"));
        }

        [Test]
        public void ResumeAndSuspendTrackProtectionState()
        {
            Assert.That(epoch.ThisInstanceProtected(), Is.False);

            epoch.Resume();
            Assert.That(epoch.ThisInstanceProtected(), Is.True);

            epoch.Suspend();
            Assert.That(epoch.ThisInstanceProtected(), Is.False);
        }

        [Test]
        public void ResumeIfNotProtectedIsIdempotent()
        {
            Assert.That(epoch.ResumeIfNotProtected(), Is.True);
            Assert.That(epoch.ResumeIfNotProtected(), Is.False);
            Assert.That(epoch.TrySuspend(), Is.True);
            Assert.That(epoch.TrySuspend(), Is.False);
        }

        [Test]
        public void AcquireAnnouncesCurrentEpoch()
        {
            Bump();
            Bump();

            epoch.Resume();
            try
            {
                Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
            }
            finally
            {
                epoch.Suspend();
            }

            Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.EqualTo(0));
        }

        void Bump()
        {
            epoch.Resume();
            _ = epoch.BumpCurrentEpoch();
            epoch.Suspend();
        }
    }
}
