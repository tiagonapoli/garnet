// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Garnet.cluster
{
    /// <summary>
    /// SlotVerifiedState
    /// </summary>
    public enum SlotVerifiedState : byte
    {
        /// <summary>
        /// OK to server request
        /// </summary>
        OK,
        /// <summary>
        /// Slot down
        /// </summary>
        CLUSTERDOWN,
        /// <summary>
        /// Slot moved to remote node
        /// </summary>
        MOVED,
        /// <summary>
        /// Ask target node
        /// </summary>
        ASK,
        /// <summary>
        /// Crossslot operation
        /// </summary>
        CROSSSLOT,
        /// <summary>
        /// Retry when multi-key slot states conflict or a configuration changes during epoch release.
        /// </summary>
        TRYAGAIN
    }
}