// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Garnet.server
{
    /// <summary>
    /// Counts Vector Set cleanup work in flight so a caller can block until there is none.
    /// Two placement rules make a count of zero mean "nothing outstanding":
    /// <list type="number">
    /// <item><b>Register before publish</b>, so work is never visible to a performer while invisible to a waiter.</item>
    /// <item><b>Register the successor before completing the predecessor</b>, so the count cannot dip to zero
    /// while work is handed from one pipeline stage to the next.</item>
    /// </list>
    /// </summary>
    internal sealed class VectorSetCleanupWorkCounter
    {
        private readonly object sync = new();

        private int inflight;

        private int unbalancedCompletions;

        /// <summary>
        /// Completed when <see cref="inflight"/> falls to zero. Null when nobody is waiting.
        /// </summary>
        private TaskCompletionSource allCleanupsComplete;

        /// <summary>
        /// Units of cleanup work in flight. Diagnostic only - to wait, use <see cref="WaitAllCleanupsAsync"/>.
        /// </summary>
        public int Inflight { get { lock (sync) { return inflight; } } }

        /// <summary>
        /// <see cref="OnCleanupComplete"/> calls with no matching registration. Always zero in a correct
        /// pipeline: Debug builds assert on the first one, this is the release fallback that keeps a
        /// misuse from hanging every waiter.
        /// </summary>
        public int UnbalancedCompletions { get { lock (sync) { return unbalancedCompletions; } } }

        /// <summary>
        /// Record a unit of cleanup work. Call before the work is visible to its performer, and balance with
        /// exactly one <see cref="OnCleanupComplete"/> on every path.
        /// </summary>
        public void RegisterCleanup()
        {
            lock (sync)
            {
                AssertInvariants();
                Debug.Assert(inflight != int.MaxValue, "Cleanup work in flight overflowed, so registrations are being leaked");

                inflight++;
            }
        }

        /// <summary>
        /// Record that a registered unit of work is done. Any successor stage must already be registered.
        /// </summary>
        public void OnCleanupComplete()
        {
            TaskCompletionSource toSignal = null;

            lock (sync)
            {
                AssertInvariants();

                if (inflight == 0)
                {
                    // Driving this negative would hang every future WaitAllCleanupsAsync
                    Debug.Fail("OnCleanupComplete with no matching RegisterCleanup, so a unit of work was completed twice or never registered");
                    unbalancedCompletions++;
                    return;
                }

                inflight--;
                if (inflight == 0)
                {
                    toSignal = allCleanupsComplete;
                    allCleanupsComplete = null;
                }
            }

            Debug.Assert(toSignal?.Task.IsCompleted != true, "The pending waiter was already completed, so it is being signalled twice");

            // Signalled outside the lock so a waiter's continuation cannot run inline on a cleanup thread
            // that is still mid-pass, holding lock (VectorManager) or an unreleased lease.
            _ = toSignal?.TrySetResult();
        }

        /// <summary>
        /// Register a unit of work and run it on the thread pool, releasing the registration when it ends.
        /// Registering before scheduling stops a drain slipping between the two.
        /// </summary>
        public Task RunCountedTaskAsync<TState>(TState state, Action<TState> work)
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
        /// Completes once no cleanup work is in flight. Says nothing about work registered after it returns.
        /// </summary>
        public Task WaitAllCleanupsAsync()
        {
            lock (sync)
            {
                AssertInvariants();

                if (inflight == 0)
                {
                    return Task.CompletedTask;
                }

                allCleanupsComplete ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                return allCleanupsComplete.Task;
            }
        }

        /// <summary>
        /// State that only misuse can produce. Checked on entry to every locked region, so a violation is
        /// reported at the next call rather than at the eventual hang.
        /// </summary>
        [Conditional("DEBUG")]
        private void AssertInvariants()
        {
            Debug.Assert(Monitor.IsEntered(sync), "Invariants are only meaningful while holding the lock");
            Debug.Assert(inflight >= 0, "Cleanup work in flight went negative, so a unit of work was completed more than once");
            Debug.Assert(allCleanupsComplete is null || inflight > 0, "A waiter is pending with no work in flight, so nothing will ever signal it");
            Debug.Assert(allCleanupsComplete?.Task.IsCompleted != true, "A completed waiter is still stored, so a later drain would return an already-signalled task");
        }
    }
}