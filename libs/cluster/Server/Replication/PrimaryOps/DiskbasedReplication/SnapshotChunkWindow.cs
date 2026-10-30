// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Garnet.common;

namespace Garnet.cluster
{
    /// <summary>
    /// Bounds the snapshot chunks awaiting a replica response and checks the responses in send order, so a file
    /// transfer is not limited to one chunk per network round trip.
    /// </summary>
    /// <remarks>
    /// Pipelining is safe because <c>ExecuteClusterSnapshotData</c> copies the chunk into the send buffer before
    /// returning, and the replica applies and acknowledges chunks of a connection in order.
    /// </remarks>
    internal sealed class SnapshotChunkWindow
    {
        /// <summary>Maximum number of chunks sent but not yet acknowledged.</summary>
        internal const int MaxInFlight = 8;

        readonly Queue<Task<string>> pending = new(MaxInFlight);
        readonly TimeSpan timeout;
        readonly CancellationToken cancellationToken;
        readonly Func<string, string> errorMessage;

        /// <param name="timeout">Maximum wait for each response.</param>
        /// <param name="errorMessage">Builds the exception message from a non-OK response.</param>
        /// <param name="cancellationToken">Cancels waiting for responses.</param>
        public SnapshotChunkWindow(TimeSpan timeout, Func<string, string> errorMessage, CancellationToken cancellationToken)
        {
            this.timeout = timeout;
            this.errorMessage = errorMessage;
            this.cancellationToken = cancellationToken;
        }

        /// <summary>Tracks a sent chunk, waiting for the oldest response first when the window is full.</summary>
        public async ValueTask AddAsync(Task<string> response)
        {
            if (pending.Count == MaxInFlight)
                await CompleteOldestAsync().ConfigureAwait(false);

            pending.Enqueue(response);
        }

        /// <summary>Waits for and checks every outstanding response.</summary>
        public async ValueTask DrainAsync()
        {
            while (pending.Count > 0)
                await CompleteOldestAsync().ConfigureAwait(false);
        }

        async ValueTask CompleteOldestAsync()
        {
            var resp = await pending.Dequeue().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!resp.Equals("OK"))
                ExceptionUtils.ThrowException(new GarnetException(errorMessage(resp)));
        }
    }
}