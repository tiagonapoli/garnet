// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    }
}
