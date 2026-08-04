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
    /// <see cref="VectorSetCleanupWorkCounter"/> registration: adding registers, and completing both removes the
    /// entry and releases the registration. The two moving together stops a drain returning while an entry is
    /// still visible to callers polling for it.
    /// </summary>
    internal sealed class VectorSetCleanupWorkSet<TValue>
    {
        private readonly VectorSetCleanupWorkCounter counter;
        private readonly ConcurrentDictionary<byte[], TValue> entries;
#if NET9_0_OR_GREATER
        private readonly ConcurrentDictionary<byte[], TValue>.AlternateLookup<ReadOnlySpan<byte>> lookup;
#endif

        public VectorSetCleanupWorkSet(VectorSetCleanupWorkCounter counter)
        {
            this.counter = counter;
            entries = new(ByteArrayComparer.Instance);
#if NET9_0_OR_GREATER
            lookup = entries.GetAlternateLookup<ReadOnlySpan<byte>>();
#endif
        }

        /// <summary>
        /// Whether work is still pending for <paramref name="key"/>.
        /// </summary>
        public bool Contains(ReadOnlySpan<byte> key)
        {
#if NET9_0_OR_GREATER
            return lookup.ContainsKey(key);
#else
            return entries.ContainsKey(key.ToArray());
#endif
        }

        /// <summary>
        /// Block until no work is pending for <paramref name="key"/>. Entries are removed only once the work
        /// has been performed, so returning implies completion, not merely dequeue.
        ///
        /// Do not call this while holding any Vector Set related locks, we will deadlock.
        /// </summary>
        public void WaitForCompletion(ReadOnlySpan<byte> key)
        {
            while (Contains(key))
            {
                _ = Thread.Yield();
            }
        }

        /// <summary>
        /// Register a unit of work and add it. Returns false, leaving nothing registered, if work is already
        /// pending for <paramref name="key"/>.
        /// </summary>
        public bool TryAdd(byte[] key, TValue value)
        {
            counter.RegisterCleanup();

            if (entries.TryAdd(key, value))
            {
                return true;
            }

            counter.OnCleanupComplete();
            return false;
        }

        /// <summary>
        /// Remove the entry and release its registration. False if it was not present.
        ///
        /// Removed before released: between the two the count is still non-zero so a drain keeps waiting.
        /// Releasing first would let the count reach zero while <see cref="Contains"/> still reports pending.
        /// </summary>
        public bool TryComplete(byte[] key)
        {
            if (!entries.TryRemove(key, out _))
            {
                return false;
            }

            counter.OnCleanupComplete();
            return true;
        }

        /// <summary>
        /// Iterate the pending work, so a consumer can service the whole backlog in one pass.
        /// </summary>
        public IEnumerator<KeyValuePair<byte[], TValue>> GetEnumerator() => entries.GetEnumerator();
    }
}