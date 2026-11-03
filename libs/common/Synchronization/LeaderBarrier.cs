// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;

namespace Garnet.common
{
    /// <summary>
    /// Synchronizes a group of participants, allowing one to act as the leader while others wait until released.
    /// </summary>
    /// <param name="participantCount"></param>
    public sealed class LeaderBarrier(int participantCount)
    {
        readonly int participantCount = participantCount;
        int arrivedCount = participantCount;

        ManualResetEventSlim releaseFirst = new(false);
        ManualResetEventSlim releaseAll = new(false);

        /// <summary>
        /// Signals arrival; the first participant waits until every participant arrives, the others wait until the leader releases them.
        /// </summary>
        /// <param name="exception">When this method returns, contains the exception that occurred during the operation, or null if no exception
        /// was thrown.</param>
        /// <param name="cancellationToken">Cancels the wait; the only way a wait ends before the barrier completes.</param>
        /// <param name="slowWaitReportInterval">Interval at which the leader reports that it is still waiting for participants.</param>
        /// <param name="onSlowWait">Invoked by the leader every <paramref name="slowWaitReportInterval"/> with the elapsed wait and the arrived participant count.</param>
        /// <returns>true if the caller is the first participant to arrive; otherwise, false.</returns>
        public bool TrySignalOrWait(out Exception exception, CancellationToken cancellationToken = default, TimeSpan slowWaitReportInterval = default, Action<TimeSpan, int> onSlowWait = null)
        {
            exception = null;
            var newValue = Interlocked.Decrement(ref arrivedCount);

            try
            {
                // First participant to arrive
                if (newValue == participantCount - 1)
                {
                    // Wait only if there are more participants to arrive. A slow participant is legitimate (for example a cold
                    // larger-than-memory read), so the leader never gives up on its own; only cancellation ends the wait.
                    if (newValue > 0)
                    {
                        var start = Stopwatch.GetTimestamp();
                        var interval = onSlowWait != null && slowWaitReportInterval > TimeSpan.Zero ? slowWaitReportInterval : Timeout.InfiniteTimeSpan;
                        while (!releaseFirst.Wait(interval, cancellationToken))
                            onSlowWait(Stopwatch.GetElapsedTime(start), participantCount - Volatile.Read(ref arrivedCount));
                    }
                    return true;
                }

                // Last participant to arrive - release the first
                if (newValue == 0)
                    releaseFirst.Set();

                // Non-first participants wait for the leader's operation, however long it runs; the leader always releases them.
                if (newValue >= 0)
                {
                    releaseAll.Wait(cancellationToken);
                    return false;
                }

                // Invalid state
                throw new Exception("Invalid count value < 0");
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        /// <summary>
        /// Release all waiting participants
        /// </summary>
        public void Release() => releaseAll.Set();
    }
}