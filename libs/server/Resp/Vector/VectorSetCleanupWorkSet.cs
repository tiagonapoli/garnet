// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Garnet.server
{
    /// <summary>
    /// A keyed set of outstanding cleanup work whose membership <em>is</em> its
    /// <see cref="VectorSetCleanupTracker"/> registration: adding an entry registers it, and completing the
    /// entry both removes it and releases the registration. Because the two move together, "the key is
    /// present" and "a registration is outstanding" cannot disagree, so a drain cannot return while an entry
    /// is still visible to callers polling for it.
    /// </summary>
    internal sealed class VectorSetCleanupWorkSet<TValue>
    {
        private readonly VectorSetCleanupTracker tracker;
        private readonly ConcurrentDictionary<byte[], TValue> entries;
#if NET9_0_OR_GREATER
        private readonly ConcurrentDictionary<byte[], TValue>.AlternateLookup<ReadOnlySpan<byte>> lookup;
#endif

        internal VectorSetCleanupWorkSet(VectorSetCleanupTracker tracker)
        {
            this.tracker = tracker;
            entries = new(ByteArrayComparer.Instance);
#if NET9_0_OR_GREATER
            lookup = entries.GetAlternateLookup<ReadOnlySpan<byte>>();
#endif
        }

        /// <summary>
        /// Whether any work is pending. Diagnostic only — a caller that needs to wait for quiescence must
        /// use <see cref="VectorSetCleanupTracker.WaitAllCleanupsAsync"/>, which cannot miss a transition.
        /// </summary>
        internal bool IsEmpty => entries.IsEmpty;

        internal int Count => entries.Count;

        /// <summary>
        /// Whether work is still pending for <paramref name="key"/>.
        /// </summary>
        internal bool Contains(ReadOnlySpan<byte> key)
        {
#if NET9_0_OR_GREATER
            return lookup.ContainsKey(key);
#else
            return entries.ContainsKey(key.ToArray());
#endif
        }

        /// <summary>
        /// Block until no work is pending for <paramref name="key"/>. The entry is only removed once the
        /// work has actually been performed, so this returning implies completion, not merely dequeue.
        ///
        /// Do not call this while holding any Vector Set related locks, we will deadlock.
        /// </summary>
        internal void WaitForCompletion(ReadOnlySpan<byte> key)
        {
            while (Contains(key))
            {
                _ = Thread.Yield();
            }
        }

        /// <summary>
        /// Register a unit of work and add it. Returns false, having registered nothing, if work is
        /// already pending for <paramref name="key"/>.
        /// </summary>
        internal bool TryAdd(byte[] key, TValue value)
            => tracker.RegisterAndPublish((entries, key, value), static s => s.entries.TryAdd(s.key, s.value));

        /// <summary>
        /// Remove the entry and release its registration. Returns false if it was not present, in which
        /// case nothing was released.
        ///
        /// The entry is removed before the registration is released, never the other way round: between
        /// the two the count is still non-zero, so a drain keeps waiting. Releasing first would let the
        /// count reach zero while <see cref="Contains"/> still reports the work as pending.
        /// </summary>
        internal bool TryComplete(byte[] key, out TValue value)
        {
            if (!entries.TryRemove(key, out value))
            {
                return false;
            }

            tracker.OnCleanupComplete();
            return true;
        }

        public IEnumerator<KeyValuePair<byte[], TValue>> GetEnumerator() => entries.GetEnumerator();
    }
}