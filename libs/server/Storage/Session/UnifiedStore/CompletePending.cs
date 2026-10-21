// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Garnet.common;
using Tsavorite.core;

namespace Garnet.server
{
    sealed partial class StorageSession
    {
        /// <summary>
        /// Handles the complete pending for Unified Store session
        /// </summary>
        /// <param name="status"></param>
        /// <param name="output"></param>
        /// <param name="unified"></param>
        internal static void CompletePendingForUnifiedStoreSession<TUnifiedContext>(ref Status status, ref UnifiedOutput output, ref TUnifiedContext unified)
            where TUnifiedContext : ITsavoriteContext<FixedSpanByteKey, UnifiedInput, UnifiedOutput, long, UnifiedSessionFunctions, StoreFunctions, StoreAllocator>
        {
            unified.CompletePendingWithOutputs(out var completedOutputs, wait: true);
            var more = completedOutputs.Next();
            if (!(more)) global::System.Environment.FailFast("Debug.Assert failed at libs/server/Storage/Session/UnifiedStore/CompletePending.cs:23: more");
            status = completedOutputs.Current.Status;
            output = completedOutputs.Current.Output;
            if (!(!completedOutputs.Next())) global::System.Environment.FailFast("Debug.Assert failed at libs/server/Storage/Session/UnifiedStore/CompletePending.cs:26: !completedOutputs.Next()");
            completedOutputs.Dispose();
        }
    }
}