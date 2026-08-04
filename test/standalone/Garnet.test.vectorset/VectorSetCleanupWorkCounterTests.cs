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
    /// Unit tests for <see cref="VectorSetCleanupWorkCounter"/>.
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupWorkCounterTests
    {
        [Test]
        public void WaitCompletesOnlyWhenTheCountReachesZero()
        {
            var counter = new VectorSetCleanupWorkCounter();

            ClassicAssert.IsTrue(counter.WaitAllCleanupsAsync().IsCompleted);

            counter.RegisterCleanup();
            counter.RegisterCleanup();

            var wait = counter.WaitAllCleanupsAsync();
            ClassicAssert.IsFalse(wait.IsCompleted);

            counter.OnCleanupComplete();
            ClassicAssert.IsFalse(wait.IsCompleted);

            counter.OnCleanupComplete();
            ClassicAssert.IsTrue(wait.Wait(TimeSpan.FromSeconds(5)));
            ClassicAssert.AreEqual(0, counter.Inflight);
        }

#if !DEBUG
        /// <summary>
        /// Release-build only: an unbalanced completion is a Debug.Fail, and this covers the fallback that
        /// keeps it from hanging waiters in production.
        /// </summary>
        [Test]
        public void CompletingWithoutARegistrationIsCountedAndDoesNotGoNegative()
        {
            var counter = new VectorSetCleanupWorkCounter();

            counter.OnCleanupComplete();

            ClassicAssert.AreEqual(0, counter.Inflight);
            ClassicAssert.AreEqual(1, counter.UnbalancedCompletions);
            ClassicAssert.IsTrue(counter.WaitAllCleanupsAsync().IsCompleted);
        }
#endif

        [Test]
        public async Task RunTrackedTaskReleasesOnSuccessAndOnFailure()
        {
            var counter = new VectorSetCleanupWorkCounter();

            await counter.RunCountedTaskAsync(0, static _ => { });
            ClassicAssert.AreEqual(0, counter.Inflight);

            var faulted = counter.RunCountedTaskAsync(0, static _ => throw new InvalidOperationException());
            _ = Assert.ThrowsAsync<InvalidOperationException>(async () => await faulted);
            ClassicAssert.AreEqual(0, counter.Inflight);
        }
    }
}