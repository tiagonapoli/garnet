// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Garnet.common
{
    /// <summary>
    /// Pool entry
    /// </summary>
    public unsafe class PoolEntry : IDisposable
    {
        /// <summary>
        /// Entry
        /// </summary>
        public readonly byte[] entry;

        /// <summary>
        /// Entry pointer
        /// </summary>
        public byte* entryPtr;

        readonly LimitedFixedBufferPool pool;
        bool disposed;

        /// <summary>
        /// Packed source identifier: low byte = <see cref="PoolEntryBufferType"/>, byte 1 = <see cref="PoolOwnerType"/>.
        /// Set when the entry is acquired via <see cref="LimitedFixedBufferPool.Get"/>.
        /// </summary>
        internal int source;

        /// <summary>
        /// Constructor
        /// </summary>
        public PoolEntry(int size, LimitedFixedBufferPool pool)
        {
            if (!(pool != null)) global::System.Environment.FailFast("Debug.Assert failed at libs/common/Memory/PoolEntry.cs:39: pool != null");
            this.pool = pool;
            this.disposed = false;
            entry = GC.AllocateArray<byte>(size, pinned: true);
            entryPtr = (byte*)Unsafe.AsPointer(ref entry[0]);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!(!disposed)) global::System.Environment.FailFast("Debug.Assert failed at libs/common/Memory/PoolEntry.cs:49: !disposed");
            disposed = true;
            pool.Return(this);
        }

        /// <summary>
        /// Reuse
        /// </summary>
        public void Reuse()
        {
            if (!(disposed)) global::System.Environment.FailFast("Debug.Assert failed at libs/common/Memory/PoolEntry.cs:59: disposed");
            disposed = false;
        }
    }
}