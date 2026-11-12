// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    /// <summary>
    /// SafeTailAddress publishes each producer's in-flight lower bound in its LightEpoch table entry. These tests
    /// cover allocation retries that suspend and resume the epoch between the publish and the allocation.
    /// </summary>
    [TestFixture]
    internal class LogSafeTailEpochEntryMigrationTests : TestBase
    {
        const int PageSizeBits = 12;
        const int PageSize = 1 << PageSizeBits;
        const int FillerLength = 100;
        const int SmallLength = 16;
        const int LargeLength = 3000;
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        sealed class BlockingEntry(int length, byte value) : ILogEnqueueEntry
        {
            public readonly ManualResetEventSlim Serializing = new(false);
            public readonly ManualResetEventSlim Release = new(false);

            public int SerializedLength => length;

            public void SerializeTo(Span<byte> dest)
            {
                Serializing.Set();
                Assert.That(Release.Wait(Timeout), Is.True, "blocked producer was never released");
                dest[..length].Fill(value);
            }
        }

        LightEpoch epoch;
        IDevice device;
        TsavoriteLog log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            epoch = new LightEpoch();
            device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "safetail.log"), deleteOnClose: true);
            log = new TsavoriteLog(new TsavoriteLogSettings
            {
                LogDevice = device,
                PageSizeBits = PageSizeBits,
                MemorySizeBits = PageSizeBits + 1,
                SegmentSizeBits = PageSizeBits + 4,
                LogCommitDir = MethodTestDir,
                TryRecoverLatest = false,
                Epoch = epoch,
            });
        }

        [TearDown]
        public void TearDown()
        {
            log?.Dispose();
            log = null;
            device?.Dispose();
            device = null;
            epoch?.Dispose();
            epoch = null;
            OnTearDown();
        }

        /// <summary>
        /// A producer publishes its in-flight lower bound, then WaitToRetryNow suspends its epoch while the next page
        /// cannot be closed. Another producer takes the freed table entry and clears the bound with its own
        /// EndInflightEnqueue; when the first producer resumes and allocates, no entry covers its unwritten slot.
        /// A scanUncommitted iterator must still return every record in order.
        /// </summary>
        [Test]
        [Category("TsavoriteLog")]
        public void UncommittedScanDoesNotSkipRecordWhoseProducerSuspendedDuringAllocationRetry()
        {
            // Leave too little room on the current page for the large record so its allocation must turn the page,
            // which in a two-page buffer requires closing the oldest page.
            var filler = new byte[FillerLength];
            while (log.TailAddress < 2L * PageSize || PageSize - (log.TailAddress & (PageSize - 1)) >= 1000)
                _ = log.Enqueue(filler);
            log.Commit(spinWait: true);

            using var iter = log.Scan(log.TailAddress, long.MaxValue, scanUncommitted: true);

            // Hold every epoch table entry but one, so the page close below cannot drain and the large producer
            // can only lose its entry to the small producer waiting for a free one.
            using var releaseHolders = new ManualResetEventSlim(false);
            var holders = new List<Thread>();
            using var holderAcquired = new SemaphoreSlim(0);
            for (var i = 0; i < epoch.EntryCount - 1; i++)
            {
                var holder = new Thread(() =>
                {
                    epoch.Resume();
                    _ = holderAcquired.Release();
                    _ = releaseHolders.Wait(Timeout);
                    epoch.Suspend();
                }) { IsBackground = true };
                holder.Start();
                holders.Add(holder);
                Assert.That(holderAcquired.Wait(Timeout), Is.True, "epoch holder did not acquire an entry");
            }

            var large = new BlockingEntry(LargeLength, 0xAB);
            var epochBeforeLarge = Volatile.Read(ref epoch.CurrentEpoch);
            Exception largeError = null;
            var largeProducer = new Thread(() =>
            {
                try
                {
                    Assert.That(log.TryEnqueue(large, out _), Is.True);
                }
                catch (Exception ex)
                {
                    largeError = ex;
                }
            }) { IsBackground = true };
            largeProducer.Start();

            // The page turn's ShiftHeadAddress bumps the epoch from inside the large producer's allocation retry loop.
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref epoch.CurrentEpoch) > epochBeforeLarge, Timeout), Is.True, "large producer never attempted the page turn");

            var small = new BlockingEntry(SmallLength, 0xCD);
            small.Release.Set();
            var smallProducer = new Thread(() => Assert.That(log.TryEnqueue(small, out _), Is.True)) { IsBackground = true };
            smallProducer.Start();
            Assert.That(smallProducer.Join(Timeout), Is.True, "small producer never obtained an epoch entry");

            releaseHolders.Set();
            foreach (var holder in holders)
                Assert.That(holder.Join(Timeout), Is.True);

            Assert.That(large.Serializing.Wait(Timeout), Is.True, "large producer never allocated its slot");
            var safeTailWhileLargeInFlight = log.RefreshSafeTailAddress();
            var tailWhileLargeInFlight = log.TailAddress;
            var seen = new List<int>();
            var scanError = DrainAvailable(iter, seen);

            large.Release.Set();
            Assert.That(largeProducer.Join(Timeout), Is.True);
            Assert.That(largeError, Is.Null, largeError?.ToString());

            var marker = new byte[LargeLength];
            Array.Fill(marker, (byte)0xEF);
            _ = log.Enqueue(marker);

            var deadline = DateTime.UtcNow + Timeout;
            while (scanError is null && !seen.Contains(0xEF) && DateTime.UtcNow < deadline)
            {
                _ = log.RefreshSafeTailAddress();
                scanError = DrainAvailable(iter, seen);
                if (!seen.Contains(0xEF))
                    Thread.Yield();
            }

            var context = $"while the large record was being written SafeTailAddress was {safeTailWhileLargeInFlight} and TailAddress was {tailWhileLargeInFlight}";
            Assert.That(scanError, Is.Null, $"uncommitted scan failed ({context}): {scanError}");
            Assert.That(seen, Is.EqualTo(new[] { 0xCD, 0xAB, 0xEF }), $"uncommitted scan returned [{string.Join(", ", seen.Select(v => $"0x{v:X2}"))}] ({context})");
        }

        static TsavoriteException DrainAvailable(TsavoriteLogScanIterator iter, List<int> seen)
        {
            try
            {
                while (iter.GetNext(out var entry, out var length, out _))
                    seen.Add(length == 0 ? -1 : entry.Distinct().Count() == 1 ? entry[0] : -2);
            }
            catch (TsavoriteException ex)
            {
                return ex;
            }

            return null;
        }
    }
}
