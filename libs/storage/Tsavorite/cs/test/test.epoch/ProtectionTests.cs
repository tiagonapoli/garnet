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
    public class ProtectionTests : SingleEpochTestBase
    {
        [Test]
        public void UnprotectedThreadHoldsNoSlot()
        {
            Assert.That(epoch.ThisInstanceProtected(), Is.False);
            Assert.That(epoch.TestHookThisThreadEntry(), Is.Zero);
            Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.Zero);
            Assert.That(epoch.TrySuspend(), Is.False);
        }

        [Test]
        public void SlotIndexIsWithinTheTable()
        {
            using (epoch.ProtectedScope())
            {
                var entry = epoch.TestHookThisThreadEntry();
                Assert.That(entry, Is.GreaterThan(0));
                Assert.That(entry, Is.LessThanOrEqualTo(epoch.EntryCount));
                Assert.That(epoch.TestHookThreadIdAt(entry), Is.EqualTo(Environment.CurrentManagedThreadId));
            }
        }

        [Test]
        public void SuspendLeavesTheSlotCompletelyFree()
        {
            epoch.Resume();
            var entry = epoch.TestHookThisThreadEntry();
            epoch.Suspend();

            Assert.That(epoch.TestHookAnnouncedEpochAt(entry), Is.Zero, "the announced epoch was left behind");
            Assert.That(epoch.TestHookThreadIdAt(entry), Is.Zero, "the thread id was left behind");
            Assert.That(epoch.TestHookThisThreadEntry(), Is.Zero);
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
                using (epoch.ProtectedScope())
                {
                    Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    _ = epoch.BumpCurrentEpoch();
                    Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    epoch.ProtectAndDrain();
                    Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.Not.Zero);
                    Assert.That(epoch.CurrentEpoch, Is.GreaterThan(0));
                }
            }
        }

        [Test]
        public void SuspendResumeKeepsTheThreadProtected()
        {
            using (epoch.ProtectedScope())
            {
                epoch.SuspendResume();

                Assert.That(epoch.ThisInstanceProtected(), Is.True);
                Assert.That(epoch.TestHookThisThreadEntry(), Is.GreaterThan(0));
                Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
            }
        }

        [Test]
        public void RefreshRepublishesTheLatestEpochEveryTime()
        {
            using (epoch.ProtectedScope())
            {
                for (var i = 0; i < 16; i++)
                {
                    _ = epoch.BumpCurrentEpoch();
                    epoch.ProtectAndDrain();
                    Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
                }
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
            using (epoch.ProtectedScope())
            {
                var description = epoch.ToString();
                Assert.That(description, Does.Contain("CurrentEpoch"));
                Assert.That(description, Does.Contain($"tid={Environment.CurrentManagedThreadId}"));
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

            using (epoch.ProtectedScope())
            {
                Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
            }

            Assert.That(epoch.TestHookThisThreadAnnouncedEpoch(), Is.EqualTo(0));
        }

        void Bump()
        {
            epoch.Resume();
            _ = epoch.BumpCurrentEpoch();
            epoch.Suspend();
        }
    }
}
