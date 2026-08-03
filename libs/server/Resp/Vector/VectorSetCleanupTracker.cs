// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading.Tasks;

namespace Garnet.server
{
    /// <summary>
    /// Counts Vector Set cleanup work that is in flight so a caller can block until there is none.
    ///
    /// This deliberately knows nothing about what cleanup <em>is</em> — it does not schedule, perform,
    /// or order anything, it only counts. Two rules make it correct:
    ///
    /// <list type="number">
    /// <item><b>Register before publish.</b> A producer must register a unit of work before that work
    /// becomes visible to whoever will perform it (before writing it to a channel or adding it to a
    /// shared set). Registering on the consumer side after dequeuing leaves a window where the work
    /// exists but is invisible to a waiter.</item>
    /// <item><b>Register the successor before completing the predecessor.</b> Where one stage hands
    /// work off to the next, the new registration must happen before the old one completes, or the
    /// count can dip to zero mid-pipeline and a waiter returns early.</item>
    /// </list>
    ///
    /// With that discipline the entire quiescence argument is local to each register/complete pair:
    /// a count of zero implies no cleanup work is outstanding, no matter how many stages, channels or
    /// background tasks the pipeline grows. That replaces having to reason globally about channel FIFO
    /// order, sentinel round-trips, and a set of separate in-progress flags.
    /// </summary>
    internal sealed class VectorSetCleanupTracker
    {
        private readonly object sync = new();

        private int inFlight;

        /// <summary>
        /// Non-null only while <see cref="inFlight"/> is greater than zero and somebody is waiting.
        /// </summary>
        private TaskCompletionSource idle;

        /// <summary>
        /// Count of <see cref="OnCleanupComplete"/> calls that had no matching
        /// <see cref="RegisterCleanup"/>. Always zero in a correct pipeline.
        ///
        /// Such a call is ignored rather than allowed to drive the count negative: a negative count
        /// could never return to zero, so it would hang every future <see cref="WaitAllCleanupsAsync"/>
        /// — turning a bookkeeping bug into a permanently wedged FLUSH and replica sync. Callers that
        /// want to detect the bug assert on this instead.
        /// </summary>
        internal int UnbalancedCompletions
        {
            get { lock (sync) { return unbalancedCompletions; } }
        }

        private int unbalancedCompletions;

        /// <summary>
        /// Units of cleanup work currently in flight. Diagnostic and test use only — a caller that
        /// wants to wait must use <see cref="WaitAllCleanupsAsync"/>, which cannot miss a transition.
        /// </summary>
        internal int InFlight
        {
            get { lock (sync) { return inFlight; } }
        }

        /// <summary>
        /// Record that a unit of cleanup work now exists.
        ///
        /// Must be called before the work becomes visible to the task that will perform it, and must be
        /// balanced by exactly one <see cref="OnCleanupComplete"/> on every path — including failure
        /// paths, so callers should complete from a finally.
        /// </summary>
        public void RegisterCleanup()
        {
            lock (sync)
            {
                inFlight++;
            }
        }

        /// <summary>
        /// Record that a unit of work registered by <see cref="RegisterCleanup"/> is fully done.
        ///
        /// If this work handed off to a later stage, that stage must already have been registered.
        /// </summary>
        public void OnCleanupComplete()
        {
            TaskCompletionSource toSignal = null;

            lock (sync)
            {
                if (inFlight == 0)
                {
                    unbalancedCompletions++;
                    return;
                }

                inFlight--;
                if (inFlight == 0)
                {
                    toSignal = idle;
                    idle = null;
                }
            }

            // Signalled outside the lock, and with RunContinuationsAsynchronously, so a waiter's
            // continuation can never run inline on a background cleanup thread while it still owns
            // pipeline state.
            _ = toSignal?.TrySetResult();
        }

        /// <summary>
        /// Register a unit of work and then make it visible via <paramref name="publish"/>, rolling the
        /// registration back if publishing fails. Returns whether the work was published.
        ///
        /// This is rule 1 expressed as code: a caller physically cannot publish cleanup work without
        /// having registered it first, so the register-then-publish ordering is not left to convention.
        /// <paramref name="state"/> is threaded through so the callback need not capture.
        /// </summary>
        public bool RegisterAndPublish<TState>(TState state, Func<TState, bool> publish)
        {
            RegisterCleanup();

            var published = false;
            try
            {
                published = publish(state);
            }
            finally
            {
                if (!published)
                {
                    OnCleanupComplete();
                }
            }

            return published;
        }

        /// <summary>
        /// Completes once no cleanup work is in flight.
        ///
        /// This says nothing about work registered after it returns — callers use it at boundaries
        /// where they have already stopped new work from being produced (an emptied store, a paused
        /// primary), so "nothing outstanding at the instant of return" is the contract.
        /// </summary>
        public Task WaitAllCleanupsAsync()
        {
            lock (sync)
            {
                if (inFlight == 0)
                {
                    return Task.CompletedTask;
                }

                idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                return idle.Task;
            }
        }
    }
}