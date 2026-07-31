// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.epoch.litmus
{
    /// <summary>
    /// Two-thread lockstep barrier for the litmus harnesses, plus the shared shutdown
    /// protocol. The reclaimer owns the deadline; when it expires it performs one extra
    /// barrier pass with <see cref="Stop"/> set so the reader is never left blocked
    /// waiting for a partner that has already gone.
    /// </summary>
    internal sealed class LitmusRendezvous
    {
        int startCount;
        int startSense;
        int endCount;
        int endSense;
        volatile bool stop;

        internal bool Stop => stop;

        /// <summary>Release the reader and let it observe <see cref="Stop"/> on its next pass.</summary>
        internal void Shutdown()
        {
            stop = true;
            StartBarrier();
            EndBarrier();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void StartBarrier()
        {
            var sense = Volatile.Read(ref startSense);
            if (Interlocked.Increment(ref startCount) == 2)
            {
                startCount = 0;
                Volatile.Write(ref startSense, sense ^ 1);
                return;
            }

            var spinner = new SpinWait();
            while (Volatile.Read(ref startSense) == sense)
                spinner.SpinOnce(-1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EndBarrier()
        {
            var sense = Volatile.Read(ref endSense);
            if (Interlocked.Increment(ref endCount) == 2)
            {
                endCount = 0;
                Volatile.Write(ref endSense, sense ^ 1);
                return;
            }

            var spinner = new SpinWait();
            while (Volatile.Read(ref endSense) == sense)
                spinner.SpinOnce(-1);
        }
    }

    /// <summary>
    /// Which cores the litmus harness pins its threads to. Two threads on distinct physical
    /// cores are what open the race window; the disturbers only keep the epoch table's cache
    /// lines shared rather than exclusively owned.
    /// </summary>
    internal readonly struct LitmusCores
    {
        internal int ReclaimerCore { get; init; }
        internal int ReaderCore { get; init; }
        internal int[] DisturberCores { get; init; }

        /// <summary>
        /// Lay the threads out on even-numbered logical processors, which on an SMT machine
        /// puts them on distinct physical cores. Returns false if the machine is too small
        /// for the reader and reclaimer to be separated at all.
        /// </summary>
        internal static bool TrySelect(out LitmusCores cores)
        {
            var logical = Environment.ProcessorCount;
            if (logical < 4)
            {
                cores = default;
                return false;
            }

            var disturbers = new System.Collections.Generic.List<int>();
            for (var core = 4; core < logical && disturbers.Count < 6; core += 2)
                disturbers.Add(core);

            cores = new LitmusCores { ReclaimerCore = 0, ReaderCore = 2, DisturberCores = [.. disturbers] };
            return true;
        }

        public override string ToString()
            => $"reclaimer={ReclaimerCore} reader={ReaderCore} disturbers=[{string.Join(",", DisturberCores)}]";
    }
}