// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading.Tasks;

namespace Garnet.server
{
    /// <summary>
    /// Counts Vector Set cleanup work in flight so a caller can block until there is none.
    /// Two placement rules make a count of zero mean "nothing outstanding":
    /// <list type="number">
    /// <item><b>Register before publish</b>, so work is never visible to a performer while invisible to a waiter.</item>
    /// <item><b>Register the successor before completing the predecessor</b>, so the count cannot dip to zero
    /// while work is being handed from one pipeline stage to the next.</item>
    /// </list>
    /// </summary>
    internal sealed class VectorSetCleanupTracker
    {
        private readonly object sync = new();

        private int inFlight;

        /// <summary>
        /// Handed to waiters and completed when <see cref="inFlight"/> falls to zero. Null when nobody
        /// is waiting.
        /// </summary>
        private TaskCompletionSource allCleanupsComplete;

        /// <summary>
        /// Count of <see cref="OnCleanupComplete"/> calls with no matching <see cref="RegisterCleanup"/>.
        /// Always zero in a correct pipeline; such a call is ignored rather than driving the count
        /// negative, since a negative count would hang every future <see cref="WaitAllCleanupsAsync"/>.
        /// </summary>
        internal int UnbalancedCompletions { get { lock (sync) { return unbalancedCompletions; } } }

        private int unbalancedCompletions;

        /// <summary>
        /// Units of cleanup work currently in flight. Diagnostic and test use only — a caller that
        /// wants to wait must use <see cref="WaitAllCleanupsAsync"/>, which cannot miss a transition.
        /// </summary>
        internal int InFlight { get { lock (sync) { return inFlight; } } }

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
                    toSignal = allCleanupsComplete;
                    allCleanupsComplete = null;
                }
            }

            // Signalled outside the lock, and with RunContinuationsAsynchronously, so a waiter's
            // continuation can never run inline on a cleanup thread that is still mid-pass — holding
            // lock (VectorManager) or a channel lease it has not released yet.
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
        /// Register a unit of work and run it on the thread pool, releasing the registration when it
        /// finishes or throws.
        ///
        /// For work that is a background pass rather than an entry in a queue or set: the registration
        /// is taken before the task is scheduled, so a drain cannot slip between the two, and the caller
        /// cannot forget to release it. <paramref name="state"/> is threaded through so the callback need
        /// not capture.
        /// </summary>
        public Task RunTrackedAsync<TState>(TState state, Action<TState> work)
        {
            RegisterCleanup();

            return Task.Run(() =>
            {
                try
                {
                    work(state);
                }
                finally
                {
                    OnCleanupComplete();
                }
            });
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

                allCleanupsComplete ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                return allCleanupsComplete.Task;
            }
        }
    }
}