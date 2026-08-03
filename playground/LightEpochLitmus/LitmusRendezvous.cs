// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.epoch.litmus
{
    /// <summary>
    /// Two-thread lockstep barrier for the litmus harnesses, plus the shared shutdown protocol.
    ///
    /// The reclaimer owns the deadline and runs one extra barrier pass with <see cref="Stop"/> set.
    /// That alone can still strand it, because the reader may observe Stop on its way out and never
    /// enter that pass; <see cref="Depart"/> covers it by releasing whoever is left waiting.
    /// </summary>
    internal sealed class LitmusRendezvous
    {
        int startCount;
        int startSense;
        int endCount;
        int endSense;
        volatile bool stop;
        volatile bool abandoned;

        internal bool Stop => stop;

        /// <summary>
        /// Announce that this thread is leaving the rendezvous for good, releasing a partner
        /// that is -- or later ends up -- waiting for it.
        /// </summary>
        internal void Depart() => abandoned = true;

        /// <summary>Release the reader and let it observe <see cref="Stop"/> on its next pass.</summary>
        internal void Shutdown()
        {
            stop = true;
            StartBarrier();
            EndBarrier();
            Depart();
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
            while (Volatile.Read(ref startSense) == sense && !abandoned)
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
            while (Volatile.Read(ref endSense) == sense && !abandoned)
                spinner.SpinOnce(-1);
        }
    }

    /// <summary>Which cores the litmus harness pins its threads to.</summary>
    internal readonly struct LitmusCores
    {
        internal int ReclaimerCore { get; init; }
        internal int ReaderCore { get; init; }
        internal int[] DisturberCores { get; init; }

        /// <summary>
        /// Lay the threads out on even-numbered logical processors, which on an SMT machine puts
        /// them on distinct physical cores. False if the machine is too small to separate them.
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