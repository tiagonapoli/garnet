// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;

namespace Tsavorite.epoch.litmus
{
    /// <summary>Which cores the harness pins its threads to.</summary>
    internal readonly struct CoreLayout
    {
        /// <summary>Disturbers only keep other epoch slots occupied, so more of them just adds scheduler noise.</summary>
        const int MaxDisturbers = 6;

        internal int ReclaimerCore { get; init; }
        internal int ReaderCore { get; init; }
        internal int[] DisturberCores { get; init; }

        /// <summary>
        /// Reclaimer on processor 0, reader on 2, disturbers on 4, 6, 8 and so on. Stepping by two
        /// gives each thread its own physical core on an SMT machine, where sibling threads share a
        /// store buffer and would mask the reordering. False if there are too few processors.
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
            for (var core = 4; core < logical && disturbers.Count < MaxDisturbers; core += 2)
                disturbers.Add(core);

            cores = new CoreLayout { ReclaimerCore = 0, ReaderCore = 2, DisturberCores = [.. disturbers] };
            return true;
        }

        public override string ToString()
            => $"reclaimer={ReclaimerCore} reader={ReaderCore} disturbers=[{string.Join(",", DisturberCores)}]";
    }
}