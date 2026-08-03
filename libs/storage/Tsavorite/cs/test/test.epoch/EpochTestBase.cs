// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Base for every fixture here: the running-test tracking from <see cref="Garnet.test.TestBase"/>,
    /// plus the timeouts and join helpers these tests share.
    /// </summary>
    public abstract class EpochTestBase : Garnet.test.TestBase
    {
        /// <summary>Long enough that only a genuine hang trips it, not a slow machine.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long to wait before concluding that something which must *not* happen has not.
        /// Paid in full on every passing run, unlike <see cref="Timeout"/>.
        /// </summary>
        internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

        /// <summary>Wait for every thread, failing the test if any of them does not finish in time.</summary>
        protected static void JoinAll(IEnumerable<Thread> threads, string message = "a thread did not finish") => JoinAll(threads, Timeout, message);

        /// <inheritdoc cref="JoinAll(IEnumerable{Thread}, string)"/>
        protected static void JoinAll(IEnumerable<Thread> threads, TimeSpan timeout, string message = "a thread did not finish")
        {
            foreach (var thread in threads)
                Assert.That(thread.Join(timeout), Is.True, message);
        }

        /// <summary>Wait for every thread without asserting, so cleanup cannot mask the real failure.</summary>
        protected static void TryJoinAll(IEnumerable<Thread> threads)
        {
            foreach (var thread in threads)
                _ = thread.Join(Timeout);
        }
    }

    /// <summary>
    /// Base for the fixtures that test a single epoch instance, which is most of them.
    /// <see cref="InstanceTests"/> derives from <see cref="EpochTestBase"/> directly because it
    /// asserts exact <see cref="LightEpoch.ActiveInstanceCount"/> deltas.
    /// </summary>
    public abstract class SingleEpochTestBase : EpochTestBase
    {
        protected LightEpoch epoch;

        [SetUp]
        public virtual void CreateEpoch() => epoch = new LightEpoch();

        [TearDown]
        public virtual void DisposeEpoch()
        {
            epoch?.Dispose();
            epoch = null;
        }
    }
}