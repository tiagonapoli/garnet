// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    public sealed unsafe partial class LightEpoch
    {
        /// <summary>
        /// The epoch table slot at <paramref name="index"/>, by reference.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref Entry EntryAt(int index) => ref *(tableAligned + index);

        /// <summary>
        /// Asserts that the slot at <paramref name="index"/> is acquired. <c>localCurrentEpoch</c> is the
        /// word the reservation CAS claims from 0, so it is what says a slot is held.
        /// </summary>
        [Conditional("DEBUG")]
        private void DebugAssertEpochAcquired(int index, string message = "Epoch table entry has no announced epoch") => Debug.Assert(EntryAt(index).localCurrentEpoch > 0, message);
    }
}
