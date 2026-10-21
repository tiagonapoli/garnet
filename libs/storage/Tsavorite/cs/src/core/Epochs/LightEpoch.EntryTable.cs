// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    public sealed unsafe partial class LightEpoch
    {
        /// <summary>
        /// Asserts that a slot is reserved for the calling thread.
        /// </summary>
        private static void DebugAssertEntryReserved(int index, string message = "No epoch table entry is reserved for this thread")
        {
            if (!(index != kInvalidIndex)) global::System.Environment.FailFast("Debug.Assert failed at libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.EntryTable.cs:16: index != kInvalidIndex" + " | " + (message) + " | " + ("No slot is reserved for this thread."));
            if (!(index > kInvalidIndex && index <= kTableSize)) global::System.Environment.FailFast("Debug.Assert failed at libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.EntryTable.cs:17: index > kInvalidIndex && index <= kTableSize" + " | " + (message) + " | " + ($"Slot {index} is out of range."));
        }

        /// <summary>
        /// Asserts that no slot is reserved for the calling thread.
        /// </summary>
        private static void DebugAssertEntryNotReserved(int index, string message = "An epoch table entry is already reserved for this thread")
            { if (index != kInvalidIndex) global::System.Environment.FailFast($"Debug.Assert failed: {message} | Slot {index} is already reserved for this thread."); }

        /// <summary>
        /// Asserts that the slot at <paramref name="index"/> is acquired by the calling thread.
        /// </summary>
        private void DebugAssertEpochAcquired(int index, string message = "Epoch table entry is not acquired by this thread")
        {
            DebugAssertEntryReserved(index, message);
            ref var entry = ref *(tableAligned + index);
            if (!(entry.localCurrentEpoch > 0)) global::System.Environment.FailFast("Debug.Assert failed at libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.EntryTable.cs:35: entry.localCurrentEpoch > 0" + " | " + (message) + " | " + ("The slot has no announced epoch."));
            if (!(entry.threadId == Metadata.threadId)) global::System.Environment.FailFast("Debug.Assert failed at libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.EntryTable.cs:36: entry.threadId == Metadata.threadId" + " | " + (message) + " | " + ("The slot does not carry this thread's id."));
        }
    }
}
