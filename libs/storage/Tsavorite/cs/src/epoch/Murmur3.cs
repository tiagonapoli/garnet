// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    /// <summary>
    /// A 32-bit murmur3 implementation.
    /// </summary>
    /// <remarks>
    /// Deliberately duplicates <c>Utility.Murmur3</c>: Tsavorite.core references this project, so
    /// reaching the other way would be a cycle.
    /// </remarks>
    public static class Murmur3
    {
        /// <summary>
        /// Hash a 32-bit value.
        /// </summary>
        /// <param name="h">Value to hash</param>
        /// <returns>The hashed value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Hash(int h)
        {
            uint a = (uint)h;
            a ^= a >> 16;
            a *= 0x85ebca6b;
            a ^= a >> 13;
            a *= 0xc2b2ae35;
            a ^= a >> 16;
            return (int)a;
        }
    }
}
