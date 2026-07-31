// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    /// <summary>
    /// Read-only views of <see cref="LightEpoch"/>'s internal state, used only by the unit tests in
    /// Garnet.LightEpoch.test.
    /// </summary>
    public sealed unsafe partial class LightEpoch
    {
        /// <summary>
        /// The epoch table index this thread currently holds for this instance, or 0 if unprotected.
        /// </summary>
        internal int ThisThreadEntry() => Metadata.Entries.GetRef(instanceId);

        /// <summary>
        /// The epoch this thread currently announces for this instance, or 0 if unprotected.
        /// </summary>
        internal long ThisThreadAnnouncedEpoch()
        {
            var entry = Metadata.Entries.GetRef(instanceId);
            return entry == kInvalidIndex ? 0 : EntryAt(entry).localCurrentEpoch;
        }

        /// <summary>
        /// The epoch announced in epoch table slot <paramref name="entry"/>, or 0 if the slot is free.
        /// </summary>
        internal long AnnouncedEpochAt(int entry) => EntryAt(entry).localCurrentEpoch;

        /// <summary>
        /// The thread id recorded in epoch table slot <paramref name="entry"/>, or 0 if the slot is free.
        /// </summary>
        internal int ThreadIdAt(int entry) => EntryAt(entry).threadId;

        /// <summary>
        /// Smallest epoch announced by any slot, or <see cref="CurrentEpoch"/> if none is protected.
        /// <see cref="SafeToReclaimEpoch"/> must always stay strictly below this.
        /// </summary>
        internal long MinAnnouncedEpoch()
        {
            var min = CurrentEpoch;
            for (var index = 1; index <= kTableSize; index++)
            {
                var announced = EntryAt(index).localCurrentEpoch;
                if (announced != 0 && announced < min)
                    min = announced;
            }

            return min;
        }

        /// <summary>
        /// Number of threads parked waiting for a table slot.
        /// </summary>
        internal int WaiterCount => waiterCount & ~kDisposedFlag;

        /// <summary>
        /// Capacity of the drain list.
        /// </summary>
        internal static int DrainListCapacity => kDrainListSize;

        /// <summary>
        /// Maximum number of concurrent <see cref="LightEpoch"/> instances.
        /// </summary>
        internal static int MaxInstanceCount => InstanceIndexBuffer.MaxInstances;
    }
}
