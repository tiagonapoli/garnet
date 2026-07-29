// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Tests for <see cref="LightEpoch"/>, focused on the CAS-carried announce: the slot claim
    /// and the epoch announce are a single locked RMW, and the refresh announce reads
    /// CurrentEpoch with acquire semantics.
    /// </summary>
    [TestFixture]
    public class LightEpochTests
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
            Bump(epoch);
            Bump(epoch);

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

        [Test]
        public void RefreshPicksUpAdvancedEpoch()
        {
            epoch.Resume();
            try
            {
                var announced = epoch.ThisThreadAnnouncedEpoch();
                _ = epoch.BumpCurrentEpoch();
                epoch.ProtectAndDrain();
                Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.GreaterThan(announced));
                Assert.That(epoch.ThisThreadAnnouncedEpoch(), Is.EqualTo(epoch.CurrentEpoch));
            }
            finally
            {
                epoch.Suspend();
            }
        }

        static void Bump(LightEpoch epoch)
        {
            epoch.Resume();
            _ = epoch.BumpCurrentEpoch();
            epoch.Suspend();
        }

        [Test]
        public void DrainActionDoesNotRunWhileAThreadIsProtected()
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
            });
            reader.Start();
            protectedThreadEntered.Wait();

            epoch.Resume();
            epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref drained));
            epoch.Suspend();

            Assert.That(Volatile.Read(ref drained), Is.EqualTo(0), "action drained while a thread was still protected");

            releaseProtectedThread.Set();
            reader.Join();

            epoch.Resume();
            epoch.ProtectAndDrain();
            epoch.Suspend();

            Assert.That(Volatile.Read(ref drained), Is.EqualTo(1));
        }

        [Test]
        public void SlotsAreReleasedAndReusedAcrossThreads()
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < 64; i++)
            {
                var entry = -1;
                var t = new Thread(() =>
                {
                    epoch.Resume();
                    entry = epoch.ThisThreadEntry();
                    epoch.Suspend();
                });
                t.Start();
                t.Join();

                Assert.That(entry, Is.GreaterThan(0));
                _ = seen.Add(entry);
            }

            // Sequential threads must be able to recycle slots; if nothing was ever
            // released the table would hand out 64 distinct entries.
            Assert.That(seen.Count, Is.LessThan(64), "no epoch table slot was ever reused");

            // Every slot must be back to unannounced.
            for (var i = 1; i <= epoch.EntryCount; i++)
                Assert.That(epoch.AnnouncedEpochAt(i), Is.EqualTo(0), $"slot {i} left announced");
        }

        [Test]
        public void ConcurrentAcquireReleaseNeverDoubleClaimsASlot()
        {
            const int ThreadCount = 16;
            const int Rounds = 20_000;

            var conflicts = 0;
            var threads = new Thread[ThreadCount];
            using var start = new ManualResetEventSlim();

            for (var t = 0; t < ThreadCount; t++)
            {
                threads[t] = new Thread(() =>
                {
                    var myId = Environment.CurrentManagedThreadId;
                    start.Wait();
                    for (var r = 0; r < Rounds; r++)
                    {
                        epoch.Resume();

                        if (epoch.ThreadIdAt(epoch.ThisThreadEntry()) != myId)
                            _ = Interlocked.Increment(ref conflicts);
                        if (epoch.ThisThreadAnnouncedEpoch() == 0)
                            _ = Interlocked.Increment(ref conflicts);

                        epoch.ProtectAndDrain();
                        epoch.Suspend();
                    }
                });
                threads[t].Start();
            }

            start.Set();
            foreach (var thread in threads)
                thread.Join();

            Assert.That(Volatile.Read(ref conflicts), Is.EqualTo(0), "a slot was claimed by two threads at once");
        }

        /// <summary>
        /// The property the fix exists for: an object retired at epoch E must not be freed while
        /// any thread is still inside a protected region that could dereference it. A reader that
        /// observes the object as live must never see it freed.
        /// </summary>
        [Test]
        public void RetiredObjectIsNeverFreedUnderALiveReader()
        {
            const int ReaderCount = 8;
            var duration = TimeSpan.FromSeconds(5);

            var page = new Page();
            var violations = 0;
            var freed = 0L;
            var stop = false;

            var readers = new Thread[ReaderCount];
            for (var i = 0; i < ReaderCount; i++)
            {
                readers[i] = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        epoch.Resume();

                        // Refresh first, then load the published pointer: anything observed as
                        // live after the announce cannot be retired before this thread suspends.
                        epoch.ProtectAndDrain();
                        var observed = Volatile.Read(ref page);

                        if (observed is not null && Volatile.Read(ref observed.freed))
                            _ = Interlocked.Increment(ref violations);

                        epoch.Suspend();
                    }
                });
                readers[i].Start();
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration && Volatile.Read(ref violations) == 0)
            {
                var retired = Volatile.Read(ref page);
                Volatile.Write(ref page, new Page());

                epoch.Resume();
                epoch.BumpCurrentEpoch(() =>
                {
                    Volatile.Write(ref retired.freed, true);
                    _ = Interlocked.Increment(ref freed);
                });
                epoch.Suspend();
            }

            Volatile.Write(ref stop, true);
            foreach (var reader in readers)
                reader.Join();

            Assert.That(Volatile.Read(ref freed), Is.GreaterThan(0), "the reclaimer never freed anything, so the test proved nothing");
            Assert.That(Volatile.Read(ref violations), Is.EqualTo(0), "a reader dereferenced a page that had already been freed");
        }

        [Test]
        public void UserWordTracksTheMinimumAcrossThreads()
        {
            var word = epoch.AllocateUserWord(long.MaxValue);
            try
            {
                using var bothAnnounced = new CountdownEvent(2);
                using var release = new ManualResetEventSlim();

                var threads = new Thread[2];
                for (var i = 0; i < 2; i++)
                {
                    var value = 100L + i;
                    threads[i] = new Thread(() =>
                    {
                        epoch.Resume();
                        epoch.ThisThreadUserWord(word) = value;
                        _ = bothAnnounced.Signal();
                        release.Wait();
                        epoch.Suspend();
                    });
                    threads[i].Start();
                }

                bothAnnounced.Wait();
                Assert.That(epoch.GetMinUserWord(word), Is.EqualTo(100L));

                release.Set();
                foreach (var thread in threads)
                    thread.Join();
            }
            finally
            {
                epoch.ReleaseUserWord(word);
            }
        }

        sealed class Page
        {
            internal bool freed;
        }
    }
}
