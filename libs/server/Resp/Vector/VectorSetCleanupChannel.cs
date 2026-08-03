// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Garnet.server
{
    /// <summary>
    /// A cleanup work queue: a channel whose items are counted by a <see cref="VectorSetCleanupTracker"/>,
    /// so that every queued item is outstanding from the moment it is published until the consumer is
    /// finished with it.
    ///
    /// The point is to make the tracker's accounting unrepresentable-to-get-wrong rather than merely
    /// documented. Publishing registers before the item is visible, and reading hands back a lease that
    /// completes the registration when disposed — so an early <c>continue</c>, an exception, or a new
    /// <c>return</c> added later cannot leak a registration. A leaked registration is unrecoverable:
    /// the count never returns to zero, so every subsequent FLUSH and replica full sync blocks forever
    /// with nothing to point at.
    ///
    /// Only queues that carry cleanup obligations should use this. A channel used purely to nudge a
    /// consumer that already tracks its work elsewhere carries no obligation and should stay a plain
    /// <see cref="Channel{T}"/>.
    /// </summary>
    internal sealed class VectorSetCleanupChannel<T>
    {
        /// <summary>
        /// One item read from the queue, and the obligation to complete its registration.
        /// Dispose exactly once, ideally via <c>using</c>.
        /// </summary>
        internal readonly struct Lease : IDisposable
        {
            private readonly VectorSetCleanupTracker tracker;

            internal T Value { get; }

            internal Lease(VectorSetCleanupTracker tracker, T value)
            {
                this.tracker = tracker;
                Value = value;
            }

            public void Dispose() => tracker?.OnCleanupComplete();
        }

        /// <summary>
        /// Every item available at the time of the call, and the obligation to complete all of their
        /// registrations. Disposing releases the whole batch, so a stage that hands off to a later one
        /// must publish the successor before this is disposed.
        /// </summary>
        internal readonly struct Batch : IDisposable
        {
            private readonly VectorSetCleanupTracker tracker;

            internal List<T> Items { get; }

            internal Batch(VectorSetCleanupTracker tracker, List<T> items)
            {
                this.tracker = tracker;
                Items = items;
            }

            public void Dispose()
            {
                if (tracker == null)
                {
                    return;
                }

                for (var i = 0; i < Items.Count; i++)
                {
                    tracker.OnCleanupComplete();
                }
            }
        }

        private readonly Channel<T> channel;
        private readonly VectorSetCleanupTracker tracker;

        internal VectorSetCleanupChannel(VectorSetCleanupTracker tracker, bool singleWriter)
        {
            this.tracker = tracker;
            channel = Channel.CreateUnbounded<T>(new() { SingleWriter = singleWriter, SingleReader = true, AllowSynchronousContinuations = false });
        }

        /// <summary>
        /// Register the item and publish it. Returns false only once the writer has been completed,
        /// that is during shutdown, in which case nothing was registered.
        /// </summary>
        internal bool TryPublish(T item) => tracker.RegisterAndPublish((channel.Writer, item), static s => s.Writer.TryWrite(s.item));

        internal ValueTask<bool> WaitToReadAsync() => channel.Reader.WaitToReadAsync();

        /// <summary>
        /// Whether any item is queued. Diagnostic only — a caller that needs to wait for quiescence must
        /// use <see cref="VectorSetCleanupTracker.WaitAllCleanupsAsync"/>, which cannot miss a transition.
        /// </summary>
        internal bool HasPending => channel.Reader.TryPeek(out _);

        /// <summary>
        /// Take the next item, if any. The returned <see cref="Lease"/> must be disposed.
        /// </summary>
        internal bool TryRead(out Lease lease)
        {
            if (channel.Reader.TryRead(out var item))
            {
                lease = new Lease(tracker, item);
                return true;
            }

            lease = default;
            return false;
        }

        /// <summary>
        /// Take every item currently available. The returned <see cref="Batch"/> must be disposed.
        /// </summary>
        internal Batch ReadAllAvailable()
        {
            var items = new List<T>();
            while (channel.Reader.TryRead(out var item))
            {
                items.Add(item);
            }

            return new Batch(tracker, items);
        }

        internal void CompleteWriter() => channel.Writer.Complete();

        internal Task Completion => channel.Reader.Completion;
    }
}