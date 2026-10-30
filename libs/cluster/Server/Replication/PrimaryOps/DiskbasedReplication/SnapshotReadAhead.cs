// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Threading.Tasks;

namespace Garnet.cluster
{
    /// <summary>
    /// Helpers for the chunk read issued ahead of the send loop.
    /// </summary>
    internal static class SnapshotReadAhead
    {
        /// <summary>
        /// Waits out a read-ahead the send loop will not consume, so the read does not outlive its data source, and
        /// returns its buffer. A failed read is ignored because the transfer is already failing.
        /// </summary>
        public static async ValueTask AbandonAsync(Task<DataSourceReadResult> read)
        {
            if (read == null)
                return;

            try
            {
                var result = await read.ConfigureAwait(false);
                result.Buffer?.Return();
            }
            catch
            {
            }
        }
    }
}