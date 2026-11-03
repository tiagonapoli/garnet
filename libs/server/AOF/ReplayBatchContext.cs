// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Garnet.server
{
    /// <summary>
    /// Shared page (unit of work) handed by the replay leader to the parallel replay tasks.
    /// Coordination is done centrally via <see cref="Garnet.common.DoubleTurnstileBarrier"/>, a shared
    /// rendezvous barrier in which the leader and every replay task participate; this context only carries
    /// the page data every task scans.
    /// </summary>
    /// <param name="replayTasks"></param>
    public unsafe class ReplayBatchContext(int replayTasks)
    {
        byte[] privatePage;

        /// <summary>
        /// Number of parallel replay tasks scanning each page.
        /// </summary>
        public readonly int ReplayTasks = replayTasks;
        /// <summary>
        /// Record pointer.
        /// </summary>
        public byte* Record;
        /// <summary>
        /// Record length.
        /// </summary>
        public int RecordLength;
        /// <summary>
        /// Represents the current address value for a given TsavoriteLog page.
        /// </summary>
        public long CurrentAddress;
        /// <summary>
        /// Represents the next address value for a given TsavoriteLog page.
        /// </summary>
        public long NextAddress;

        /// <summary>
        /// Copies an epoch-protected log page into a private buffer, valid until the next call, so the leader can release
        /// the log epoch while replay tasks scan it.
        /// </summary>
        public byte* CopyToPrivatePage(byte* record, int recordLength)
        {
            if (privatePage == null || privatePage.Length < recordLength)
                privatePage = GC.AllocateUninitializedArray<byte>(recordLength, pinned: true);
            fixed (byte* page = privatePage)
            {
                Buffer.MemoryCopy(record, page, privatePage.Length, recordLength);
                return page;
            }
        }
    }
}