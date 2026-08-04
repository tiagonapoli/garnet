// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Garnet.common;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Inspection and injection seams used only by tests.
    /// </summary>
    public sealed partial class VectorManager
    {
        /// <summary>
        /// When drops are pending, hold the drop pass in flight so a store-empty boundary can be crossed
        /// while native drops are still outstanding — proving the sync-path drain barrier waits for them.
        /// Injection can only be armed in DEBUG, so this completes synchronously in Release.
        /// </summary>
        internal Task TestHookPauseInNativeDropAsync()
            => requestedDrops.IsEmpty
                ? Task.CompletedTask
                : ExceptionInjectionHelper.ResetAndWaitAsync(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);

        /// <summary>
        /// Counts element records whose namespace maps to a single context block.
        /// </summary>
        private sealed class TestHookContextRecordCounter : IScanIteratorFunctions
        {
            private readonly ulong pairedContext;

            public int Count { get; private set; }

            public TestHookContextRecordCounter(ulong pairedContext)
            {
                this.pairedContext = pairedContext;
            }

            public void OnException(Exception exception, long numberOfRecords) { }
            public bool OnStart(long beginAddress, long endAddress) => true;
            public void OnStop(bool completed, long numberOfRecords) { }

            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Skip;

                if (!logRecord.HasNamespace)
                    return true;

                var namespaceBytes = logRecord.NamespaceBytes;
                if (namespaceBytes.Length is not (sizeof(byte) or sizeof(uint)))
                    return true;

                var ns = ExtractContextFromNamespaces(namespaceBytes);
                if ((ns & ~(ContextStep - 1)) == pairedContext)
                    Count++;

                return true;
            }
        }

        internal static int TestHookReservedCount(in ContextMetadata metadata) => BitOperations.PopCount(metadata.ReservedMask);

        /// <summary>
        /// Add every reserved context in <paramref name="metadata"/> to <paramref name="into"/>, composed
        /// with the block's <paramref name="offset"/>.
        /// </summary>
        internal static void TestHookCollectReservedContexts(in ContextMetadata metadata, ulong offset, List<ulong> into)
        {
            var reserved = metadata.ReservedMask;
            while (reserved != 0)
            {
                var bit = BitOperations.TrailingZeroCount(reserved);
                reserved &= reserved - 1;
                into.Add(offset + ((ulong)bit * ContextStep));
            }
        }

        /// <summary>
        /// Number of Vector Set contexts still reserved across every <see cref="ContextMetadata"/> block.
        /// </summary>
        internal int TestHookGetReservedContextCount()
        {
            lock (this)
            {
                var count = 0;
                for (var i = 0; i < contextMetadatas.Length; i++)
                {
                    count += TestHookReservedCount(in contextMetadatas[i]);
                }

                return count;
            }
        }

        /// <summary>
        /// Number of <see cref="ContextMetadata"/> blocks still pending persistence.
        /// </summary>
        internal int TestHookGetDirtyContextMetadataCount()
        {
            lock (this)
            {
                return dirtyContextMetadatas.Count;
            }
        }

        /// <summary>
        /// Number of native DiskANN index drops still pending.
        /// </summary>
        internal int TestHookGetPendingDropCount() => requestedDrops.Count;

        /// <summary>
        /// The composed contexts currently reserved across every <see cref="ContextMetadata"/> block.
        /// </summary>
        internal List<ulong> TestHookGetReservedContexts()
        {
            var ret = new List<ulong>();
            lock (this)
            {
                for (var i = 0; i < contextMetadatas.Length; i++)
                {
                    var offset = ContextMetadata.OffsetForContextMetadata(i);
                    TestHookCollectReservedContexts(in contextMetadatas[i], offset, ret);
                }
            }

            return ret;
        }

        /// <summary>
        /// Write a single Vector Set element record directly into <paramref name="context"/>'s namespace,
        /// bypassing context reservation and the <see cref="WaitForDiskANNIndexDrop"/> recreate guard —
        /// how a diskless full sync streams records in as-is.
        /// </summary>
        internal void TestHookStreamElementIntoContext(ulong context, ReadOnlySpan<byte> elementKey, ReadOnlySpan<byte> value)
        {
            using var session = (RespServerSession)getTempSession();
            if (session.activeDbId != dbId && !session.TrySwitchActiveDatabaseSession(dbId))
                throw new GarnetException($"Could not switch VectorManager test session to {dbId}, initialization failed");

            Span<byte> nsBytes = stackalloc byte[sizeof(uint)];
            StoreContextInNamespace(context, ref nsBytes);

            VectorElementKey key = new(nsBytes, elementKey);
            VectorInput input = default;
            input.AlignmentExpected = true;
            VectorOutput outputSpan = new(new SpanByteAndMemory());

            ref var vectorCtx = ref session.storageSession.vectorBasicContext;
            var status = vectorCtx.Upsert(key, ref input, value, ref outputSpan);
            if (status.IsPending)
                CompletePending(ref status, ref outputSpan, ref vectorCtx);

            if (!status.IsCompletedSuccessfully)
                throw new GarnetException("Test-only streamed element write did not complete successfully");
        }

        /// <summary>
        /// Number of element records whose namespace maps to <paramref name="context"/>'s block.
        /// </summary>
        internal int TestHookCountRecordsInContext(ulong context)
        {
            using var session = (RespServerSession)getTempSession();
            if (session.activeDbId != dbId && !session.TrySwitchActiveDatabaseSession(dbId))
                throw new GarnetException($"Could not switch VectorManager test session to {dbId}, initialization failed");

            var counter = new TestHookContextRecordCounter(context & ~(ContextStep - 1));
            ref var scanCtx = ref session.storageSession.stringBasicContext;
            _ = scanCtx.Session.IterateLookupSnapshot(ref counter);

            return counter.Count;
        }
    }
}