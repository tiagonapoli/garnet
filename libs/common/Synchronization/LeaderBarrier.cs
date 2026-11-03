// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
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
        /// Attempts to signal arrival and wait for other participants within the specified timeout and cancellation
        /// token.
        /// </summary>
        /// <param name="exception">When this method returns, contains the exception that occurred during the operation, or null if no exception
        /// was thrown.</param>
        /// <param name="timeout">The maximum time the first participant waits for the others to arrive; a timeout is reported through <paramref name="exception"/>.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting.</param>
        /// <returns>true if the caller is the first participant to arrive; otherwise, false.</returns>
        public bool TrySignalOrWait(out Exception exception, TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            exception = null;
            var newValue = Interlocked.Decrement(ref arrivedCount);

            try
            {
                // First participant to arrive
                if (newValue == participantCount - 1)
                {
                    // Wait only if there are more participants to arrive
                    if (newValue > 0 && !releaseFirst.Wait(timeout, cancellationToken))
                        exception = new TimeoutException($"{nameof(LeaderBarrier)} leader timed out after {timeout} waiting for {participantCount} participants to arrive");
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