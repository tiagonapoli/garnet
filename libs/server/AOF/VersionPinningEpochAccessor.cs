// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Epoch accessor that keeps an AOF record's store version active in the <see cref="StateMachineDriver"/> while its append is suspended.
    /// </summary>
    /// <remarks>
    /// An append that waits for AOF space suspends the store epoch, which lets a checkpoint advance past the record's version
    /// and enqueue its END marker first; replay then drops the record as older than the checkpoint. Pinning the version
    /// before suspending makes the checkpoint wait for the append before entering WAIT_FLUSH, where END is enqueued.
    /// The caller must call <see cref="StateMachineDriver.EndTransaction"/> once the append completes if <c>*pinned</c> is set.
    /// </remarks>
    internal unsafe struct VersionPinningEpochAccessor<TEpochAccessor>(TEpochAccessor inner, StateMachineDriver driver, long version, bool* pinned) : IEpochAccessor
        where TEpochAccessor : IEpochAccessor
    {
        /// <inheritdoc/>
        public bool TrySuspend()
        {
            if (inner is null)
                return false;

            // Pin while still protected so the checkpoint's last-version tracking, which runs after an epoch drain, observes it.
            var newlyPinned = false;
            if (!*pinned && driver != null)
            {
                driver.PinVersion(version);
                *pinned = newlyPinned = true;
            }

            if (inner.TrySuspend())
                return true;

            if (newlyPinned)
            {
                *pinned = false;
                driver.EndTransaction(version);
            }

            return false;
        }

        /// <inheritdoc/>
        public void Resume() => inner.Resume();
    }
}
