// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;

namespace Tsavorite.epoch.litmus
{
    /// <summary>Which cores the harness pins its threads to.</summary>
    internal readonly struct CoreLayout
    {
        internal int ReclaimerCore { get; init; }
        internal int ReaderCore { get; init; }
        internal int[] DisturberCores { get; init; }

        /// <summary>
        /// Lay the threads out on even-numbered logical processors, which on an SMT machine puts
        /// them on distinct physical cores. False if the machine is too small to separate them.
        /// </summary>
        internal static bool TrySelect(out CoreLayout cores)
        {
            var logical = Environment.ProcessorCount;
            if (logical < 4)
            {
                cores = default;
                return false;
            }

            var disturbers = new List<int>();
            for (var core = 4; core < logical && disturbers.Count < 6; core += 2)
                disturbers.Add(core);

            cores = new CoreLayout { ReclaimerCore = 0, ReaderCore = 2, DisturberCores = [.. disturbers] };
            return true;
        }

        public override string ToString()
            => $"reclaimer={ReclaimerCore} reader={ReaderCore} disturbers=[{string.Join(",", DisturberCores)}]";
    }
}