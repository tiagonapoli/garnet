using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    internal static class Utility
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Murmur3(int h)
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
