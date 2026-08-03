// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Garnet.common;

namespace Garnet.server
{
    /// <summary>
    /// A cleanup work queue whose items are counted by a <see cref="VectorSetCleanupTracker"/>: publishing
    /// registers before the item is visible, and reading hands back a lease that completes the registration
    /// when disposed, so an early <c>continue</c>, an exception, or a <c>return</c> added later cannot leak
    /// one. A leaked registration is unrecoverable — every subsequent FLUSH and replica full sync then blocks
    /// forever. Queues that carry no cleanup obligation should stay a plain <see cref="Channel{T}"/>.
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

        internal VectorSetCleanupChannel(VectorSetCleanupTracker tracker)
        {
            this.tracker = tracker;
            channel = Channel.CreateUnbounded<T>(new() { SingleWriter = false, SingleReader = true, AllowSynchronousContinuations = false });
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

        /// <summary>
        /// Stop accepting items and block until the queue has drained and <paramref name="consumer"/> has exited.
        /// </summary>
        internal void CompleteAndWaitForConsumer(Task consumer)
        {
            CompleteWriter();
            AsyncUtils.BlockingWait(Completion);
            AsyncUtils.BlockingWait(consumer);
        }
    }
}