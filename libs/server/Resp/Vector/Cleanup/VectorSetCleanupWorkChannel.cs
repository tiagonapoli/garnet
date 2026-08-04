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
    /// A cleanup work queue whose items are counted by a <see cref="VectorSetCleanupWorkCounter"/>: publishing
    /// registers before the item is visible, and reading hands back a lease that completes the registration
    /// when disposed. A leaked registration blocks every subsequent drain forever.
    /// </summary>
    internal sealed class VectorSetCleanupWorkChannel<T>
    {
        private readonly Channel<T> channel;
        private readonly VectorSetCleanupWorkCounter counter;

        public VectorSetCleanupWorkChannel(VectorSetCleanupWorkCounter counter)
        {
            this.counter = counter;
            channel = Channel.CreateUnbounded<T>(new() { SingleWriter = false, SingleReader = true, AllowSynchronousContinuations = false });
        }

        /// <summary>
        /// Register the item and publish it. False only once the writer is completed, i.e. during shutdown,
        /// in which case the registration is rolled back.
        /// </summary>
        public bool TryPublish(T item)
        {
            counter.RegisterCleanup();

            if (channel.Writer.TryWrite(item))
            {
                return true;
            }

            counter.OnCleanupComplete();
            return false;
        }

        /// <summary>
        /// Completes when an item may be available. False once completed and drained.
        /// </summary>
        public ValueTask<bool> WaitToReadAsync() => channel.Reader.WaitToReadAsync();

        /// <summary>
        /// Whether any item is queued. Diagnostic only - to wait for quiescence use
        /// <see cref="VectorSetCleanupWorkCounter.WaitAllCleanupsAsync"/>, which cannot miss a transition.
        /// </summary>
        public bool HasPending => channel.Reader.TryPeek(out _);

        /// <summary>
        /// Take the next item, if any. The returned <see cref="Lease"/> must be disposed.
        /// </summary>
        public bool TryAcquire(out Lease lease)
        {
            if (channel.Reader.TryRead(out var item))
            {
                lease = new Lease(counter, item);
                return true;
            }

            lease = default;
            return false;
        }

        /// <summary>
        /// Wait for and take the next item. Null once the queue is completed and drained. The returned
        /// <see cref="Lease"/> must be disposed.
        /// </summary>
        public async ValueTask<Lease?> WaitToAcquireAsync()
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (TryAcquire(out var lease))
                {
                    return lease;
                }
            }

            return null;
        }

        /// <summary>
        /// Take every item currently available. The returned <see cref="Batch"/> must be disposed.
        /// </summary>
        public Batch ReadAllAvailable()
        {
            var items = new List<T>();
            while (channel.Reader.TryRead(out var item))
            {
                items.Add(item);
            }

            return new Batch(counter, items);
        }

        /// <summary>
        /// Stop accepting items and block until they drain and <paramref name="consumerTask"/> exits.
        /// </summary>
        public void CompleteAndWaitForConsumerTask(Task consumerTask)
        {
            channel.Writer.Complete();
            AsyncUtils.BlockingWait(channel.Reader.Completion);
            AsyncUtils.BlockingWait(consumerTask);
        }

        /// <summary>
        /// One item, and the obligation to complete its registration. Dispose exactly once, ideally via
        /// <c>using</c>.
        /// </summary>
        public readonly struct Lease(VectorSetCleanupWorkCounter counter, T item) : IDisposable
        {
            public T Item { get; } = item;

            public void Dispose() => counter?.OnCleanupComplete();
        }

        /// <summary>
        /// Every item available at the time of the call, and the obligation to complete all of their
        /// registrations. Disposing releases the whole batch, so a stage that hands off to a later one must
        /// publish the successor first.
        /// </summary>
        public readonly struct Batch(VectorSetCleanupWorkCounter counter, List<T> items) : IDisposable
        {
            public List<T> Items { get; } = items;

            public void Dispose()
            {
                for (var i = 0; counter != null && i < Items.Count; i++)
                {
                    counter.OnCleanupComplete();
                }
            }
        }
    }

    internal static class VectorSetCleanupWorkChannelExtensions
    {
        /// <summary>
        /// Publish an item with no payload, similar to a signal, for a queue whose work is described elsewhere.
        /// </summary>
        public static bool TryPublish(this VectorSetCleanupWorkChannel<object> channel) => channel.TryPublish(null);
    }
}