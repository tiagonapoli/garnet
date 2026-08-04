// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
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

        private int inflight;

        /// <summary>
        /// Units of cleanup work in flight. Diagnostic only - to wait, use <see cref="WaitAllCleanupsAsync"/>.
        /// </summary>
        public int Inflight { get { lock (sync) { return inflight; } } }

        private int unbalancedCompletions;

        /// <summary>
        /// <see cref="OnCleanupComplete"/> calls with no matching <see cref="RegisterCleanup"/>. Always zero
        /// in a correct pipeline.
        /// </summary>
        public int UnbalancedCompletions { get { lock (sync) { return unbalancedCompletions; } } }

        /// <summary>
        /// Completed when <see cref="inflight"/> falls to zero. Null when nobody is waiting.
        /// </summary>
        private TaskCompletionSource allCleanupsComplete;

        /// <summary>
        /// Record a unit of cleanup work. Call before the work is visible to its performer, and balance with
        /// exactly one <see cref="OnCleanupComplete"/> on every path.
        /// </summary>
        public void RegisterCleanup()
        {
            lock (sync)
            {
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
                if (inflight == 0)
                {
                    // Driving this negative would hang every future WaitAllCleanupsAsync
                    Debug.Fail("Cleanup completed without a matching registration");
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

            // Signalled outside the lock so a waiter's continuation cannot run inline on a cleanup thread
            // that is still mid-pass, holding lock (VectorManager) or an unreleased lease.
            _ = toSignal?.TrySetResult();
        }

        /// <summary>
        /// Rule 1 as code: register, then publish, rolling the registration back if publishing fails.
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
        /// Register a unit of work and run it on the thread pool, releasing the registration when it ends.
        /// Registering before scheduling stops a drain slipping between the two.
        /// </summary>
        public Task RunTrackedTaskAsync<TState>(TState state, Action<TState> work)
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
                if (inflight == 0)
                {
                    return Task.CompletedTask;
                }

                allCleanupsComplete ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                return allCleanupsComplete.Task;
            }
        }
    }
}