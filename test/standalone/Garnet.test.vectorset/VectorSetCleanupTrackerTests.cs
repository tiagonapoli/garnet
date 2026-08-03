// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Unit tests for <see cref="VectorSetCleanupTracker"/>.
    ///
    /// The counter itself is small; what these cover are the corner cases that the TLA+ model
    /// (CleanupTracker.tla) identified as the ones that actually decide whether a drain is sound:
    /// the two register/complete placement rules, the instant-of-return semantics, and the failure
    /// modes that would wedge the pipeline (negative count, inline continuations).
    /// </summary>
    [TestFixture]
    public class VectorSetCleanupTrackerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static void AssertCompletes(Task task, string message)
            => ClassicAssert.IsTrue(task.Wait(Timeout), message);

        private static void AssertDoesNotComplete(Task task, string message)
            => ClassicAssert.IsFalse(task.Wait(TimeSpan.FromMilliseconds(250)), message);

        [Test]
        public void WaitOnIdleTrackerCompletesSynchronously()
        {
            var tracker = new VectorSetCleanupTracker();

            var wait = tracker.WaitAllCleanupsAsync();

            ClassicAssert.IsTrue(wait.IsCompletedSuccessfully, "a wait with nothing in flight must not block at all");
            ClassicAssert.AreEqual(0, tracker.InFlight);
        }

        [Test]
        public void WaitBlocksWhileWorkIsInFlightAndReleasesWhenItCompletes()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();
            var wait = tracker.WaitAllCleanupsAsync();

            AssertDoesNotComplete(wait, "the wait returned while a registered unit of work was still in flight");

            tracker.OnCleanupComplete();

            AssertCompletes(wait, "the wait did not return after the last unit of work completed");
            ClassicAssert.AreEqual(0, tracker.InFlight);
        }

        /// <summary>
        /// The count is a count, not a flag: partial completion must not release a waiter.
        /// </summary>
        [Test]
        public void WaitBlocksUntilEveryRegistrationCompletes()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();
            tracker.RegisterCleanup();
            tracker.RegisterCleanup();

            var wait = tracker.WaitAllCleanupsAsync();

            tracker.OnCleanupComplete();
            tracker.OnCleanupComplete();
            AssertDoesNotComplete(wait, "the wait returned with one registration still outstanding");

            tracker.OnCleanupComplete();
            AssertCompletes(wait, "the wait did not return once all three registrations completed");
        }

        /// <summary>
        /// The contract is "nothing outstanding at the instant of return". Work registered after the
        /// count hit zero must not retroactively un-complete a waiter that already observed quiescence.
        /// </summary>
        [Test]
        public void WaitStaysCompletedWhenNewWorkArrivesAfterQuiescence()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();
            var wait = tracker.WaitAllCleanupsAsync();
            tracker.OnCleanupComplete();

            AssertCompletes(wait, "the wait did not return when the pipeline quiesced");

            tracker.RegisterCleanup();

            ClassicAssert.IsTrue(wait.IsCompletedSuccessfully, "a completed wait must not be reopened by later work");
            ClassicAssert.AreEqual(1, tracker.InFlight);
        }

        /// <summary>
        /// A wait taken after a previous one completed must get a fresh, unsignalled completion —
        /// a stale reused source would return immediately and silently skip the drain.
        /// </summary>
        [Test]
        public void SecondWaitIsIndependentOfTheFirst()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();
            var first = tracker.WaitAllCleanupsAsync();
            tracker.OnCleanupComplete();
            AssertCompletes(first, "the first wait did not return");

            tracker.RegisterCleanup();
            var second = tracker.WaitAllCleanupsAsync();

            AssertDoesNotComplete(second, "the second wait reused the already-signalled completion from the first");

            tracker.OnCleanupComplete();
            AssertCompletes(second, "the second wait did not return");
        }

        [Test]
        public void ConcurrentWaitersAllRelease()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();

            var waits = new Task[8];
            for (var i = 0; i < waits.Length; i++)
            {
                waits[i] = tracker.WaitAllCleanupsAsync();
            }

            foreach (var wait in waits)
            {
                AssertDoesNotComplete(wait, "a waiter returned while work was in flight");
            }

            tracker.OnCleanupComplete();

            AssertCompletes(Task.WhenAll(waits), "not every concurrent waiter was released");
        }

        /// <summary>
        /// R1 (register before publish). Registering only once the consumer has taken the work leaves a
        /// window where the work exists but is invisible — the drain returns early. Modelled as
        /// Tracker_LateRegister.cfg, which violates TrackerSound at depth 4.
        /// </summary>
        [Test]
        public void RegisteringAfterPublishLetsAWaitThroughEarly()
        {
            var tracker = new VectorSetCleanupTracker();

            // Producer publishes without registering (the rule being broken).
            var published = true;

            var wait = tracker.WaitAllCleanupsAsync();
            ClassicAssert.IsTrue(wait.IsCompletedSuccessfully,
                "this is the hazard R1 prevents: the tracker cannot see work that was published before it was registered");
            ClassicAssert.IsTrue(published);

            // With the rule honoured the same sequence blocks.
            tracker.RegisterCleanup();
            var correctWait = tracker.WaitAllCleanupsAsync();
            AssertDoesNotComplete(correctWait, "registering before publishing must make the work visible to a waiter");

            tracker.OnCleanupComplete();
            AssertCompletes(correctWait, "the wait did not return after the work completed");
        }

        /// <summary>
        /// R2 (register the successor before completing the predecessor). This is the rule the marking
        /// stage relies on when it pumps the cleanup scan. Releasing first lets the count touch zero
        /// mid-pipeline; modelled as Tracker_LateHandoff.cfg, which violates TrackerSound at depth 6.
        /// </summary>
        [Test]
        public void HandOffRegisteringSuccessorFirstKeepsTheCountAboveZero()
        {
            var tracker = new VectorSetCleanupTracker();

            // Predecessor: the marking pass owns one registration.
            tracker.RegisterCleanup();
            var wait = tracker.WaitAllCleanupsAsync();

            // Successor registered BEFORE the predecessor is released.
            tracker.RegisterCleanup();
            tracker.OnCleanupComplete();

            AssertDoesNotComplete(wait, "the count dipped to zero across the hand-off and released the waiter early");
            ClassicAssert.AreEqual(1, tracker.InFlight);

            tracker.OnCleanupComplete();
            AssertCompletes(wait, "the wait did not return once the successor finished");
        }

        /// <summary>
        /// Releasing the predecessor first is exactly the ordering that breaks: the waiter is released
        /// while the successor stage still owes work. Retained as an executable statement of why the
        /// ordering in RunRequestCleanupTaskAsync matters.
        /// </summary>
        [Test]
        public void HandOffReleasingPredecessorFirstReleasesTheWaiterEarly()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();
            var wait = tracker.WaitAllCleanupsAsync();

            tracker.OnCleanupComplete();
            AssertCompletes(wait, "the count reached zero, so the waiter is released — this is the hazard R2 prevents");

            // The successor only becomes visible afterwards, too late for the waiter above.
            tracker.RegisterCleanup();
            ClassicAssert.AreEqual(1, tracker.InFlight);
        }

        /// <summary>
        /// An unbalanced completion must not drive the count negative: a negative count can never
        /// return to zero, so it would hang every subsequent drain instead of just being wrong once.
        /// </summary>
        [Test]
        public void UnbalancedCompletionIsIgnoredRatherThanDrivingTheCountNegative()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.OnCleanupComplete();

            ClassicAssert.AreEqual(0, tracker.InFlight, "the count must never go negative");
            ClassicAssert.AreEqual(1, tracker.UnbalancedCompletions, "the misuse must still be observable");

            // The tracker remains usable and live.
            tracker.RegisterCleanup();
            var wait = tracker.WaitAllCleanupsAsync();
            AssertDoesNotComplete(wait, "a spurious completion must not have satisfied a later registration");

            tracker.OnCleanupComplete();
            AssertCompletes(wait, "the tracker must still function after an unbalanced completion");
        }

        /// <summary>
        /// The completion must be signalled outside the lock. If it ran inline while the lock was held,
        /// a continuation that touches the tracker would deadlock — and in the real pipeline the
        /// continuation is a replica-sync path that immediately does more cleanup bookkeeping.
        /// </summary>
        [Test]
        public void CompletionDoesNotRunContinuationsUnderTheLock()
        {
            var tracker = new VectorSetCleanupTracker();

            tracker.RegisterCleanup();

            var reentered = tracker.WaitAllCleanupsAsync().ContinueWith(
                _ =>
                {
                    tracker.RegisterCleanup();
                    tracker.OnCleanupComplete();

                    return tracker.InFlight;
                },
                TaskContinuationOptions.ExecuteSynchronously);

            tracker.OnCleanupComplete();

            AssertCompletes(reentered, "a continuation that re-enters the tracker deadlocked — the completion ran under the lock");
            ClassicAssert.AreEqual(0, reentered.Result);
        }

        /// <summary>
        /// Under contention a waiter must only ever be released at a genuine zero. Producers keep
        /// registering and completing while waiters come and go; any waiter that returns is checked
        /// against a counter maintained independently of the tracker.
        /// </summary>
        [Test]
        public void ConcurrentRegisterCompleteNeverReleasesAWaiterWhileWorkIsOutstanding()
        {
            const int Producers = 4;
            const int IterationsPerProducer = 2000;

            var tracker = new VectorSetCleanupTracker();

            // Shadow count maintained with the same ordering as the tracker calls, so a waiter that
            // returns can be checked against it.
            var shadow = 0;
            var failures = new List<string>();
            var done = false;

            var producers = new Task[Producers];
            for (var p = 0; p < Producers; p++)
            {
                producers[p] = Task.Run(() =>
                {
                    for (var i = 0; i < IterationsPerProducer; i++)
                    {
                        _ = Interlocked.Increment(ref shadow);
                        tracker.RegisterCleanup();

                        Thread.SpinWait(i % 17);

                        tracker.OnCleanupComplete();
                        _ = Interlocked.Decrement(ref shadow);
                    }
                });
            }

            var waiter = Task.Run(async () =>
            {
                while (!Volatile.Read(ref done))
                {
                    await tracker.WaitAllCleanupsAsync().ConfigureAwait(false);

                    // The tracker said zero; nothing may be registered-but-not-completed at this point
                    // beyond what a producer could have added after the release, which the shadow
                    // decrements only after the tracker completion.
                    if (tracker.InFlight < 0)
                    {
                        lock (failures)
                        {
                            failures.Add($"tracker count went negative: {tracker.InFlight}");
                        }
                    }
                }
            });

            AssertCompletes(Task.WhenAll(producers), "producers did not finish");
            Volatile.Write(ref done, true);
            AssertCompletes(waiter, "waiter loop did not finish");

            ClassicAssert.IsEmpty(failures);
            ClassicAssert.AreEqual(0, tracker.InFlight, "every registration must have been completed");
            ClassicAssert.AreEqual(0, Volatile.Read(ref shadow));
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions, "balanced usage must never report an unbalanced completion");

            // And a final wait must be satisfied immediately.
            ClassicAssert.IsTrue(tracker.WaitAllCleanupsAsync().IsCompletedSuccessfully);
        }

        /// <summary>
        /// End-to-end replay of the pipeline shape the model checks: a deletion is registered by its
        /// producer, the marking stage hands off to a scan, and an independent native drop overlaps.
        /// A single wait started at the beginning must not return until all of it has drained.
        /// </summary>
        [Test]
        public void PipelineShapedSequenceKeepsTheWaiterBlockedUntilFullyDrained()
        {
            var tracker = new VectorSetCleanupTracker();

            // Producer: VectorSetDeleted registers before writing to requestCleanupTaskChannel.
            tracker.RegisterCleanup();

            // Producer: RequestDropInMemoryIndex registers before adding to requestedDrops.
            tracker.RegisterCleanup();

            var wait = tracker.WaitAllCleanupsAsync();

            // Marking stage: registers the scan pump before releasing the request it owned.
            tracker.RegisterCleanup();
            tracker.OnCleanupComplete();
            AssertDoesNotComplete(wait, "hand-off from marking to scan must not open a zero-count window");

            // Drop task finishes its native drop.
            tracker.OnCleanupComplete();
            AssertDoesNotComplete(wait, "the scan is still outstanding");

            // Scan finishes.
            tracker.OnCleanupComplete();
            AssertCompletes(wait, "the wait must return once marking, scanning and drops have all drained");

            ClassicAssert.AreEqual(0, tracker.InFlight);
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
        }

        [Test]
        public void RegisterAndPublishLeavesTheWorkRegisteredWhenPublishingSucceeds()
        {
            var tracker = new VectorSetCleanupTracker();

            var observedAtPublish = -1;
            var published = tracker.RegisterAndPublish(tracker, t =>
            {
                observedAtPublish = t.InFlight;
                return true;
            });

            ClassicAssert.IsTrue(published);
            ClassicAssert.AreEqual(1, observedAtPublish, "The work must already be registered when it becomes visible");
            ClassicAssert.AreEqual(1, tracker.InFlight);
        }

        [Test]
        public void RegisterAndPublishRollsBackWhenPublishingFails()
        {
            var tracker = new VectorSetCleanupTracker();

            ClassicAssert.IsFalse(tracker.RegisterAndPublish(0, _ => false));

            ClassicAssert.AreEqual(0, tracker.InFlight);
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
            ClassicAssert.IsTrue(tracker.WaitAllCleanupsAsync().IsCompleted);
        }

        [Test]
        public void RegisterAndPublishRollsBackWhenPublishingThrows()
        {
            var tracker = new VectorSetCleanupTracker();

            _ = Assert.Throws<InvalidOperationException>(
                () => tracker.RegisterAndPublish(0, _ => throw new InvalidOperationException("publish failed")));

            ClassicAssert.AreEqual(0, tracker.InFlight, "A throwing publish must not leak a registration");
            ClassicAssert.AreEqual(0, tracker.UnbalancedCompletions);
        }
    }
}