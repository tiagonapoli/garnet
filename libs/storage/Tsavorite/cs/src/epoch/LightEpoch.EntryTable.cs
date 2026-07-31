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
        /// <remarks>
        /// This is address arithmetic only. It carries no ordering of its own, so a caller that
        /// needs an atomic or a fence still has to say so at the point of use.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref Entry EntryAt(int index) => ref *(tableAligned + index);

        /// <summary>
        /// Asserts that the slot at <paramref name="index"/> has been acquired, i.e. carries an announced epoch.
        /// </summary>
        /// <remarks>
        /// <c>localCurrentEpoch</c> is the word the reservation CAS claims from 0, so it -- not
        /// <c>threadId</c>, which is only a trailing tag -- is what says the slot is held.
        /// <see cref="ConditionalAttribute"/> rather than a plain method wrapping the assert, so the
        /// call site disappears from release builds instead of compiling to an empty call.
        /// </remarks>
        [Conditional("DEBUG")]
        void AssertEpochAcquired(int index, string message = "Epoch table entry has no announced epoch") => Debug.Assert(EntryAt(index).localCurrentEpoch != 0, message);
    }
}
