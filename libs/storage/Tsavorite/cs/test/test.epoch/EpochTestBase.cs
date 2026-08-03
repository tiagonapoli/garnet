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
    /// Fixture plumbing shared by the epoch tests: one <see cref="LightEpoch"/> per test, and the join
    /// helpers used by the many tests that fan work out over threads.
    /// </summary>
    public abstract class EpochTestBase
    {
        /// <summary>Long enough that only a genuine hang trips it, not a slow machine.</summary>
        protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>Long enough for the tests that deliberately run millions of rounds.</summary>
        protected static readonly TimeSpan SoakTimeout = TimeSpan.FromMinutes(2);

        protected LightEpoch epoch;

        [SetUp]
        public virtual void Setup() => epoch = new LightEpoch();

        [TearDown]
        public virtual void TearDown()
        {
            epoch?.Dispose();
            epoch = null;
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
}