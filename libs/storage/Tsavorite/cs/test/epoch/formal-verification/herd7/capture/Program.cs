using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// Each Op* wrapper is NoInlining so the JIT emits one standalone method per epoch
    /// operation, with the operation itself inlined into it exactly as it would be at a
    /// real call site. That gives per-operation disassembly without suppressing inlining
    /// inside the operation.
    /// </summary>
    public static class DisasmDriver
    {
        static readonly LightEpoch epoch = new LightEpoch();

        [MethodImpl(MethodImplOptions.NoInlining)] static void OpResume() => epoch.Resume();
        [MethodImpl(MethodImplOptions.NoInlining)] static void OpSuspend() => epoch.Suspend();
        [MethodImpl(MethodImplOptions.NoInlining)] static void OpProtectAndDrain() => epoch.ProtectAndDrain();
        [MethodImpl(MethodImplOptions.NoInlining)] static long OpBumpCurrentEpoch() => epoch.BumpCurrentEpoch();

        public static void Main()
        {
            long sink = 0;
            for (int i = 0; i < 500; i++)
            {
                OpResume();
                OpProtectAndDrain();
                sink += OpBumpCurrentEpoch();
                OpSuspend();
                Thread.SpinWait(1);
            }
            Console.WriteLine("driver done: " + epoch.CurrentEpoch + " " + sink);
        }
    }
}
