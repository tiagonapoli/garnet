// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Base for every fixture here: the running-test tracking that <c>Garnet.test.TestBase</c>
    /// provides, plus the timeouts and join helpers these tests share.
    ///
    /// It is a copy rather than a reference because <c>Garnet.test.TestBase</c> lives in the
    /// <c>Garnet.test</c> assembly, and this project exists precisely so the epoch can be tested
    /// without dragging in the rest of the server.
    /// </summary>
    public abstract class EpochTestBase
    {
        /// <summary>Tests currently executing, dumped by the unhandled-exception handler.</summary>
        public static readonly ConcurrentDictionary<string, bool> RunningTests = new();

        /// <summary>Long enough that only a genuine hang trips it, not a slow machine.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long to wait before concluding that something which must *not* happen has not
        /// happened. Every use is a negative assertion, so unlike <see cref="Timeout"/> this is
        /// paid in full on every passing run -- long enough to be meaningful, short enough not to
        /// dominate the suite.
        /// </summary>
        internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

        [SetUp]
        public void TrackRunningTest()
        {
            RunningTests[TestContext.CurrentContext.Test.Name] = true;

            if (TestContext.CurrentContext.CurrentRepeatCount > 0)
                Debug.WriteLine($"*** Current test iteration {TestContext.CurrentContext.CurrentRepeatCount + 1}: {TestContext.CurrentContext.Test.Name} ***");
        }

        [TearDown]
        public void RemoveRunningTest()
        {
            Assert.That(RunningTests.TryRemove(TestContext.CurrentContext.Test.Name, out _), Is.True, $"Could not find running test {TestContext.CurrentContext.Test.Name}");
        }

        /// <summary>Wait for every thread, failing the test if any of them does not finish in time.</summary>
        protected static void JoinAll(IEnumerable<Thread> threads, string message = "a thread did not finish") => JoinAll(threads, Timeout, message);

        /// <inheritdoc cref="JoinAll(IEnumerable{Thread}, string)"/>
        protected static void JoinAll(IEnumerable<Thread> threads, TimeSpan timeout, string message = "a thread did not finish")
        {
            foreach (var thread in threads)
                Assert.That(thread.Join(timeout), Is.True, message);
        }

        /// <summary>
        /// Wait for every thread without asserting. For cleanup paths that must not mask the failure
        /// that got them there.
        /// </summary>
        protected static void TryJoinAll(IEnumerable<Thread> threads)
        {
            foreach (var thread in threads)
                _ = thread.Join(Timeout);
        }
    }

    /// <summary>
    /// Base for the fixtures that test a single epoch instance, which is most of them.
    ///
    /// <see cref="InstanceTests"/> derives from <see cref="EpochTestBase"/> directly instead: it
    /// asserts exact <see cref="LightEpoch.ActiveInstanceCount"/> deltas, so a fixture-owned
    /// instance would show up in its results.
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