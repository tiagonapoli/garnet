// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Garnet.common;
using Tsavorite.core;

namespace Garnet.server
{
    sealed partial class StorageSession
    {
        /// <summary>
        /// Handles the complete pending for Object Store session
        /// </summary>
        /// <param name="status"></param>
        /// <param name="output"></param>
        /// <param name="objectContext"></param>
        internal static void CompletePendingForObjectStoreSession<TObjectContext>(ref Status status, ref ObjectOutput output, ref TObjectContext objectContext)
            where TObjectContext : ITsavoriteContext<FixedSpanByteKey, ObjectInput, ObjectOutput, long, ObjectSessionFunctions, StoreFunctions, StoreAllocator>
        {
            objectContext.CompletePendingWithOutputs(out var completedOutputs, wait: true);
            var more = completedOutputs.Next();
            if (!(more)) global::System.Environment.FailFast("Debug.Assert failed at libs/server/Storage/Session/ObjectStore/CompletePending.cs:23: more");
            status = completedOutputs.Current.Status;
            output = completedOutputs.Current.Output;
            if (!(!completedOutputs.Next())) global::System.Environment.FailFast("Debug.Assert failed at libs/server/Storage/Session/ObjectStore/CompletePending.cs:26: !completedOutputs.Next()");
            completedOutputs.Dispose();
        }
    }
}