// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using CloseOwnershipAllocator = ObjectAllocator<StoreFunctions<TestObjectKey.Comparer, AllocatorCloseOwnershipTests.FaultyEvictTriggers>>;
    using CloseOwnershipStoreFunctions = StoreFunctions<TestObjectKey.Comparer, AllocatorCloseOwnershipTests.FaultyEvictTriggers>;

    /// <summary>
    /// <para>
    /// <c>AllocatorBase.OngoingCloseUntilAddress</c> is a work-ownership token for the page-close/eviction
    /// pipeline. <see cref="AllocatorBase{TStoreFunctions, TAllocator}"/>.<c>OnPagesClosed</c> reads it and,
    /// when it is non-zero, returns immediately on the assumption that some other thread is inside
    /// <c>OnPagesClosedWorker</c> and will close the requested range. The whole close pipeline therefore rests
    /// on one invariant:
    /// </para>
    /// <para>
    ///   <b>OngoingCloseUntilAddress != 0  =&gt;  a thread is actively running OnPagesClosedWorker.</b>
    /// </para>
    /// <para>
    /// The token is cleared in exactly one place: the <c>Interlocked.CompareExchange(ref OngoingCloseUntilAddress, 0, ...)</c>
    /// at the bottom of <c>OnPagesClosedWorker</c>. Any path that leaves the worker without reaching that CAS
    /// permanently strands the token, and every subsequent <c>OnPagesClosed</c> defers to a worker that no
    /// longer exists — so <c>ClosedUntilAddress</c> can never advance again.
    /// </para>
    /// <para>
    /// The consequence is not a deadlock but a <b>livelock that burns a full core forever</b>, because both
    /// <c>ResetCore</c> phase 2 and <c>ShiftAddressesWithWait</c> wait for <c>ClosedUntilAddress</c> with a bare
    /// <c>while (...) Thread.Yield();</c> spin and no timeout, cancellation, or progress check.
    /// </para>
    /// <para>
    /// These tests reproduce the two independent ways the token is stranded:
    /// <list type="number">
    /// <item>an exception escaping <c>OnPagesClosedWorker</c> (an application <c>OnEvict</c> throw here; in
    ///   production it was a <see cref="NullReferenceException"/> inside <c>EvictRecordsInRange</c>), and</item>
    /// <item><c>Initialize()</c> rewinding <c>ClosedUntilAddress</c> back to <c>firstValidAddress</c> while
    ///   leaving <c>OngoingCloseUntilAddress</c> at its stale pre-rewind value.</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    internal class AllocatorCloseOwnershipTests : TestBase
    {
        /// <summary>How long a Reset()/FlushAndEvict() may take before we call it wedged.</summary>
        private static readonly TimeSpan WedgeTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Shared, mutable fault-injection state for <see cref="FaultyEvictTriggers"/>.</summary>
        internal class EvictFaultInjector
        {
            /// <summary>When set, the next <c>OnEvict</c> callback throws, unwinding OnPagesClosedWorker.</summary>
            public volatile bool ThrowOnEvict;

            public int EvictCalls;
            public int ThrownCount;
        }

        /// <summary>
        /// Record triggers whose <c>OnEvict</c> throws on demand. <c>OnEvict</c> is invoked from
        /// <c>ObjectAllocatorImpl.EvictRecordsInRange</c>, which is called by <c>OnPagesClosedWorker</c> —
        /// i.e. exactly where the production NullReferenceException surfaced.
        /// </summary>
        internal struct FaultyEvictTriggers : IRecordTriggers
        {
            private readonly EvictFaultInjector injector;

            public FaultyEvictTriggers(EvictFaultInjector injector) => this.injector = injector;

            public readonly bool CallOnEvict => true;

            public readonly void OnEvict(ref LogRecord logRecord, EvictionSource source)
            {
                if (injector is null)
                    return;

                _ = Interlocked.Increment(ref injector.EvictCalls);
                if (injector.ThrowOnEvict)
                {
                    _ = Interlocked.Increment(ref injector.ThrownCount);
                    throw new InvalidOperationException("Injected OnEvict failure inside OnPagesClosedWorker.");
                }
            }
        }

        private static readonly FieldInfo OngoingCloseUntilAddressField =
            typeof(AllocatorBase<CloseOwnershipStoreFunctions, CloseOwnershipAllocator>)
                .GetField("OngoingCloseUntilAddress", BindingFlags.NonPublic | BindingFlags.Instance);

        private TsavoriteKV<CloseOwnershipStoreFunctions, CloseOwnershipAllocator> store;
        private IDevice log, objlog;
        private EvictFaultInjector injector;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "AllocatorCloseOwnershipTests.log"), deleteOnClose: true);
            objlog = Devices.CreateLogDevice(Path.Join(MethodTestDir, "AllocatorCloseOwnershipTests.obj.log"), deleteOnClose: true);
            injector = new EvictFaultInjector();
            store = new(new()
            {
                IndexSize = 1L << 13,
                LogDevice = log,
                ObjectLogDevice = objlog,
                MutableFraction = 0.1,
                LogMemorySize = 1L << 15,
                PageSize = MinKvLogPageSize
            }, StoreFunctions.Create(new TestObjectKey.Comparer(), () => new TestObjectValue.Serializer(),
                    new FaultyEvictTriggers(injector))
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
        }

        [TearDown]
        public void TearDown()
        {
            // NOTE: a wedged test leaves its probe thread spinning in Thread.Yield(). The probe threads are
            // background threads so they cannot block process exit, but store.Dispose() is deliberately not
            // forced here if a probe is still running inside the allocator.
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            objlog?.Dispose(); objlog = null;
            OnTearDown();
        }

        private long OngoingCloseUntilAddress => (long)OngoingCloseUntilAddressField.GetValue(store.hlogBase);

        private void SetOngoingCloseUntilAddress(long value) => OngoingCloseUntilAddressField.SetValue(store.hlogBase, value);

        private void Upsert(int key, int value)
        {
            using var s = store.NewSession<TestObjectKey, TestObjectInput, TestObjectOutput, int, TestObjectFunctionsDelete>(new TestObjectFunctionsDelete());
            _ = s.BasicContext.Upsert(new TestObjectKey { key = key }, new TestObjectValue { value = value }, 0);
        }

        private void Fill(int from, int count)
        {
            for (var i = from; i < from + count; i++)
                Upsert(i, i);
        }

        /// <summary>
        /// Runs <paramref name="action"/> on a background thread and reports whether it finished within
        /// <see cref="WedgeTimeout"/>. A wedged allocator spins in <c>Thread.Yield()</c> forever, so the
        /// probe thread is marked background and simply abandoned when it does not complete.
        /// </summary>
        private static bool RunWithTimeout(Action action, out Exception error)
        {
            Exception captured = null;
            using var done = new ManualResetEventSlim(false);
            var probe = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = "close-ownership-probe" };

            probe.Start();
            var completed = done.Wait(WedgeTimeout);
            error = captured;
            return completed;
        }

        /// <summary>
        /// Drives a healthy fill + eviction cycle so the close pipeline has run normally at least once and
        /// the ownership token is back at 0. This is the baseline the fault cases deviate from.
        /// </summary>
        private void FillAndEvictCleanly(int from, int count)
        {
            injector.ThrowOnEvict = false;
            Fill(from, count);
            store.Log.FlushAndEvict(wait: true);
        }

        /// <summary>
        /// Forces an eviction whose <c>OnEvict</c> throws, unwinding <c>OnPagesClosedWorker</c> before it can
        /// release the ownership token. The exception surfaces on whichever thread drained the epoch; it is
        /// swallowed here because the point of the test is the state left behind, not the throw itself.
        /// </summary>
        private void EvictWithInjectedFailure()
        {
            injector.ThrowOnEvict = true;
            try
            {
                store.Log.FlushAndEvict(wait: true);
            }
            catch (Exception)
            {
                // Expected: the injected OnEvict failure propagates out through LightEpoch.Drain.
            }
            finally
            {
                injector.ThrowOnEvict = false;
            }
        }

        /// <summary>
        /// The leak itself: an exception must not carry the close-ownership token away with it. If
        /// <c>OnPagesClosedWorker</c> unwinds while still holding the token, the pipeline is left with an owner
        /// that no longer exists and <c>ClosedUntilAddress</c> can never advance again. Everything else in this
        /// fixture is a consequence of that state.
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void EvictFailureReleasesCloseOwnershipToken()
        {
            FillAndEvictCleanly(0, 128);
            ClassicAssert.AreEqual(0, OngoingCloseUntilAddress,
                "Precondition: a clean close cycle must leave OngoingCloseUntilAddress at 0.");

            Fill(1_000, 128);
            EvictWithInjectedFailure();

            ClassicAssert.Greater(injector.ThrownCount, 0,
                "Precondition: the injected OnEvict failure never fired, so the close worker was never unwound.");

            ClassicAssert.AreEqual(0, OngoingCloseUntilAddress,
                "OnPagesClosedWorker unwound without releasing OngoingCloseUntilAddress. The close pipeline now has"
                + " a phantom owner: every later OnPagesClosed defers to it, so ClosedUntilAddress can never advance"
                + " and every waiter on it spins in Thread.Yield() forever.");
        }

        /// <summary>
        /// <para>
        /// The production hang, reproduced end to end. This mirrors the wedged thread captured in the memory
        /// dump of a Garnet replica, whose stack was:
        /// </para>
        /// <code>
        /// AllocatorBase.&lt;ResetCore&gt;b__1()      AllocatorBase.cs  (while (ClosedUntilAddress &lt; newBeginAddress) Thread.Yield();)
        /// LightEpoch.Drain(Int64)
        /// LightEpoch.BumpCurrentEpoch(Action)
        /// AllocatorBase.ResetCore()
        /// AllocatorBase.Reset()
        /// TsavoriteKV.Reset()
        /// </code>
        /// <para>
        /// That thread had burned 109 hours of CPU — 87% of the whole process — spinning on a
        /// <c>ClosedUntilAddress</c> that nothing would ever advance.
        /// </para>
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void ResetDoesNotWedgeAfterEvictFailure()
        {
            FillAndEvictCleanly(0, 128);
            Fill(1_000, 128);
            EvictWithInjectedFailure();

            ClassicAssert.Greater(injector.ThrownCount, 0, "Precondition: the injected OnEvict failure never fired.");

            var completed = RunWithTimeout(() => store.Reset(), out var error);

            ClassicAssert.IsNull(error, $"Reset() threw instead of completing: {error}");
            ClassicAssert.IsTrue(completed,
                $"TsavoriteKV.Reset() did not complete within {WedgeTimeout.TotalSeconds}s. ResetCore phase 2 is"
                + " spinning in 'while (ClosedUntilAddress < newBeginAddress) Thread.Yield();' waiting for a"
                + " page-close worker that already unwound. This is the captured production livelock.");
        }

        /// <summary>
        /// The same wedge reached through <c>ShiftAddressesWithWait</c> rather than <c>Reset</c>. This is the
        /// more common entry point — every ordinary <c>FlushAndEvict(wait: true)</c> takes it — which is why a
        /// single stranded token takes the whole store down, not just the reset path.
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void FlushAndEvictDoesNotWedgeAfterEvictFailure()
        {
            FillAndEvictCleanly(0, 128);
            Fill(1_000, 128);
            EvictWithInjectedFailure();

            ClassicAssert.Greater(injector.ThrownCount, 0, "Precondition: the injected OnEvict failure never fired.");

            Fill(2_000, 128);
            var completed = RunWithTimeout(() => store.Log.FlushAndEvict(wait: true), out var error);

            ClassicAssert.IsNull(error, $"FlushAndEvict() threw instead of completing: {error}");
            ClassicAssert.IsTrue(completed,
                $"Log.FlushAndEvict(wait: true) did not complete within {WedgeTimeout.TotalSeconds}s;"
                + " ShiftAddressesWithWait is spinning on ClosedUntilAddress behind a phantom close owner.");
        }

        /// <summary>
        /// The invariant in isolation, with no exception involved: a non-zero ownership token with no running
        /// worker is by itself enough to wedge <c>Reset()</c> forever. This is what makes the
        /// <c>Initialize()</c> gap below fatal rather than merely untidy.
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void StrandedCloseOwnershipTokenDoesNotWedgeReset()
        {
            FillAndEvictCleanly(0, 128);
            Fill(1_000, 128);

            ClassicAssert.Less(store.hlogBase.ClosedUntilAddress, store.Log.TailAddress,
                "Precondition: there must be unclosed log for the phase-2 wait to block on.");

            // Strand the token exactly as an unwound worker would have left it, without throwing anything.
            SetOngoingCloseUntilAddress(store.hlogBase.ClosedUntilAddress + 1);

            var completed = RunWithTimeout(() => store.Reset(), out var error);

            ClassicAssert.IsNull(error, $"Reset() threw instead of completing: {error}");
            ClassicAssert.IsTrue(completed,
                $"TsavoriteKV.Reset() did not complete within {WedgeTimeout.TotalSeconds}s. A non-zero"
                + " OngoingCloseUntilAddress with no running worker makes OnPagesClosed defer to a phantom"
                + " owner, so ClosedUntilAddress never advances and ResetCore spins forever.");
        }

        /// <summary>
        /// <para>
        /// The second, independent way the token is stranded — and the one that needs no exception at all.
        /// </para>
        /// <para>
        /// <c>Initialize()</c> rewinds seven log addresses back to <c>firstValidAddress</c> —
        /// <c>ReadOnlyAddress</c>, <c>SafeReadOnlyAddress</c>, <c>HeadAddress</c>, <c>SafeHeadAddress</c>,
        /// <c>ClosedUntilAddress</c>, <c>FlushedUntilAddress</c> and <c>BeginAddress</c> — but does not clear
        /// <c>OngoingCloseUntilAddress</c>. A token left over from the pre-rewind log therefore survives into
        /// the new generation, where it is larger than every address the fresh log will request, so
        /// <c>OnPagesClosed</c> takes its early-out on every single call and no page is ever closed again.
        /// </para>
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void ResetClearsCloseOwnershipToken()
        {
            FillAndEvictCleanly(0, 128);
            Fill(1_000, 128);

            // Strand a token belonging to the pre-rewind log generation. Initialize() must not carry it over.
            SetOngoingCloseUntilAddress(store.hlogBase.ClosedUntilAddress + 1);

            var completed = RunWithTimeout(() => store.Reset(), out var error);
            ClassicAssert.IsNull(error, $"Reset() threw instead of completing: {error}");
            ClassicAssert.IsTrue(completed, $"Reset() did not complete within {WedgeTimeout.TotalSeconds}s.");

            ClassicAssert.AreEqual(0, OngoingCloseUntilAddress,
                "Reset()/Initialize() rewound ClosedUntilAddress but left OngoingCloseUntilAddress set. The stale"
                + " token outlives the log generation it belonged to and silently disables page closing for the"
                + " life of the process.");
        }

        /// <summary>
        /// End-to-end proof that the store is still usable after a failed eviction: the close pipeline must
        /// recover and keep advancing <c>ClosedUntilAddress</c> once the fault is removed. Without ownership
        /// release this never progresses, which is what turned a single transient eviction error into a
        /// permanently stalled replica in production.
        /// </summary>
        [Test, Category("TsavoriteKV")]
        public void ClosePipelineRecoversAfterEvictFailure()
        {
            FillAndEvictCleanly(0, 128);
            Fill(1_000, 128);
            EvictWithInjectedFailure();

            var closedBefore = store.hlogBase.ClosedUntilAddress;

            Fill(2_000, 256);
            var completed = RunWithTimeout(() => store.Log.FlushAndEvict(wait: true), out var error);

            ClassicAssert.IsNull(error, $"FlushAndEvict() threw instead of completing: {error}");
            ClassicAssert.IsTrue(completed,
                $"Close pipeline never recovered within {WedgeTimeout.TotalSeconds}s after a transient OnEvict failure.");
            ClassicAssert.Greater(store.hlogBase.ClosedUntilAddress, closedBefore,
                "ClosedUntilAddress must resume advancing once the eviction fault is removed.");
        }
    }
}
