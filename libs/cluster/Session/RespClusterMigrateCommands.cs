// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Garnet.client;
using Garnet.common;
using Garnet.server;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.cluster
{
    internal sealed unsafe partial class ClusterSession : IClusterSession
    {
        long lastLog = 0;
        long totalKeyCount = 0;

        // Per-connection reassembly state for MigrationRecordSpanType.ChunkedLogRecord records whose chunks may span commands.
        ChunkedRecordReassembler chunkedRecordReassembler;

        /// <summary>
        /// Complete a reassembled <see cref="MigrationRecordSpanType.ChunkedLogRecord"/>. A non-inline object value is streamed
        /// from a <see cref="System.Buffers.ReadOnlySequence{T}"/> and deserialized with the object serializer (so it can exceed
        /// 2 GB), returned pre-deserialized alongside the small inline+key header; every other record is returned as one
        /// contiguous buffer. Returns true when the record has a streamed object value.
        /// </summary>
        /// <summary>
        /// Build the <see cref="DiskLogRecord"/> for a completed chunked record from the pieces reassembled by
        /// <see cref="chunkedRecordReassembler"/>: a fully-inline record from its contiguous inline buffer, else the inline portion
        /// plus the pre-populated overflow key/value and/or the streamed (now deserialized) object value.
        /// <paramref name="headerPtr"/> must point at the pinned inline buffer (<see cref="ChunkedRecordReassembler.InlineBuffer"/>)
        /// and remain pinned while the output record is used.
        /// </summary>
        unsafe bool TryCompleteChunkedRecordReassembly(byte* headerPtr, StoreWrapper storeWrapper, ObjectIdMap transientObjectIdMap, out DiskLogRecord diskLogRecord)
        {
            var reassembler = chunkedRecordReassembler;
            var headerSpan = PinnedSpanByte.FromPinnedPointer(headerPtr, reassembler.InlineSize);
            if (reassembler.RecordIsInline)
                return DiskLogRecord.TryDeserialize(headerSpan, storeWrapper.GarnetObjectSerializer, transientObjectIdMap, out diskLogRecord);

            // Non-inline: defer object deserialization until the inline header is validated.
            Func<IHeapObject> deserializeValueObject = reassembler.IsObjectValue
                ? () => (IHeapObject)storeWrapper.GarnetObjectSerializer.Deserialize(reassembler.ObjectValueSequence())
                : null;
            return DiskLogRecord.TryCompleteDeserializeChunkedRecord(headerSpan, reassembler.KeyOverflow, reassembler.ValueOverflow,
                deserializeValueObject, transientObjectIdMap, out diskLogRecord);
        }

        /// <summary>
        /// Logging of migrate session status
        /// </summary>
        /// <param name="keyCount"></param>
        /// <param name="completed"></param>
        private void TrackImportProgress(int keyCount, bool completed = false)
        {
            totalKeyCount += keyCount;
            var duration = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - lastLog);
            if (completed || lastLog == 0 || duration >= clusterProvider.storeWrapper.loggingFrequency)
            {
                logger?.LogTrace("[{op}]: totalKeyCount:({totalKeyCount})", completed ? "COMPLETED" : "IMPORTING", totalKeyCount.ToString("N0"));
                lastLog = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// Implements CLUSTER MIGRATE command (only for internode use)
        /// </summary>
        /// <param name="invalidParameters"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private bool NetworkClusterMigrate(out bool invalidParameters)
        {
            invalidParameters = false;

            // Expecting exactly 4 arguments
            if (parseState.Count != 4)
            {
                invalidParameters = true;
                return true;
            }

            var replace = parseState.GetArgSliceByRef(1).ReadOnlySpan;
            var vectorSet = parseState.GetArgSliceByRef(2).ReadOnlySpan;
            var payloadStartPtr = parseState.GetArgSliceByRef(3).ToPointer();
            var lastParam = parseState.GetArgSliceByRef(parseState.Count - 1);

            var payloadEndPtr = lastParam.ToPointer() + lastParam.Length;

            var replaceOption = replace.EqualsUpperCaseSpanIgnoringCase("T"u8);
            var vectorSetOption = vectorSet.EqualsUpperCaseSpanIgnoringCase("T"u8);

            var buffer = new Span<byte>(payloadStartPtr, (int)(payloadEndPtr - payloadStartPtr)).ToArray();

            if (clusterProvider.serverOptions.FastMigrate)
                _ = Task.Run(() => Process(basicGarnetApi, buffer, replaceOption, vectorSetOption));
            else
                Process(basicGarnetApi, buffer, replaceOption, vectorSetOption);

            void Process(BasicGarnetApi basicGarnetApi, byte[] input, bool replaceOption, bool vectorSetOption)
            {
                if (input.Length < sizeof(int))
                {
                    logger?.LogError("Rejected migrated payload without a key count");
                    throw new GarnetException("Malformed migrated payload: missing key count");
                }

                var currentConfig = clusterProvider.clusterManager.CurrentConfig;
                byte migrateState = 0;

                fixed (byte* ptr = input)
                {
                    var payloadPtr = ptr;
                    var payloadEndPtr = ptr + input.Length;

                    var keyCount = *(int*)payloadPtr;
                    payloadPtr += sizeof(int);
                    if (keyCount < 0)
                    {
                        logger?.LogError("Rejected migrated payload with negative key count: {KeyCount}", keyCount);
                        throw new GarnetException("Malformed migrated payload: negative key count");
                    }

                    var i = 0;

                    TrackImportProgress(keyCount, keyCount == 0);
                    var storeWrapper = clusterProvider.storeWrapper;
                    var transientObjectIdMap = storeWrapper.store.Log.TransientObjectIdMap;

                    DiskLogRecord diskLogRecord = default;
                    try
                    {
                        if (vectorSetOption)
                        {
                            // Vector Sets need special handling
                            while (i < keyCount)
                            {
                                if (payloadPtr >= payloadEndPtr)
                                {
                                    logger?.LogError("Rejected migrated payload missing a Vector Set record kind");
                                    throw new GarnetException("Malformed migrated payload: missing record kind");
                                }

                                var kind = (MigrationRecordSpanType)(*payloadPtr);
                                payloadPtr++;

                                if (!RespReadUtils.GetSerializedRecordSpan(out var payloadRaw, ref payloadPtr, payloadEndPtr))
                                {
                                    logger?.LogError("Rejected malformed Vector Set migrated record frame");
                                    throw new GarnetException("Malformed Vector Set migrated record frame");
                                }

                                if (kind != MigrationRecordSpanType.VectorSetIndex)
                                    throw new InvalidOperationException($"Unexpected {nameof(MigrationRecordSpanType)}: {kind}");

                                var payload = payloadRaw.ReadOnlySpan;

                                VectorManager.DeserializeMigratedIndexKey(payload, out var keyBytes, out var valueBytes, out var expiration);

                                // An error has occurred
                                if (migrateState > 0)
                                {
                                    i++;
                                    continue;
                                }

                                clusterProvider.storeWrapper.DefaultDatabase.VectorManager.HandleMigratedIndexKey(clusterProvider.storeWrapper.DefaultDatabase, clusterProvider.storeWrapper, keyBytes, valueBytes, expiration);
                                i++;
                            }
                        }
                        else
                        {
                            while (i < keyCount)
                            {
                                if (payloadPtr >= payloadEndPtr)
                                {
                                    logger?.LogError("Rejected migrated payload missing a record kind");
                                    throw new GarnetException("Malformed migrated payload: missing record kind");
                                }

                                var kind = (MigrationRecordSpanType)(*payloadPtr);
                                payloadPtr++;

                                if (kind == MigrationRecordSpanType.ChunkedLogRecord)
                                {
                                    // A record too large for one send buffer arrives as chunks (possibly across commands):
                                    // [int chunkLength | continuation][chunk bytes]. GetSerializedRecordSpan cannot read these
                                    // because the continuation flag makes the length read as negative.
                                    if (payloadPtr + sizeof(int) > payloadEndPtr)
                                    {
                                        logger?.LogError("Rejected migrated chunk without a length prefix");
                                        throw new GarnetException("Malformed migrated chunk: missing length prefix");
                                    }

                                    var rawChunkLength = *(int*)payloadPtr;
                                    payloadPtr += sizeof(int);
                                    var moreChunksFollow = (rawChunkLength & ChunkedRecordConstants.ContinuationFlag) != 0;
                                    var chunkLength = rawChunkLength & ~ChunkedRecordConstants.ContinuationFlag;
                                    if (chunkLength > payloadEndPtr - payloadPtr)
                                    {
                                        logger?.LogError("Rejected migrated chunk extending beyond its payload");
                                        throw new GarnetException("Malformed migrated chunk: length exceeds payload");
                                    }

                                    var chunkSpan = new ReadOnlySpan<byte>(payloadPtr, chunkLength);
                                    payloadPtr += chunkLength;

                                    // An error has occurred; keep consuming chunks but do not process.
                                    if (migrateState > 0)
                                    {
                                        chunkedRecordReassembler?.Reset();
                                        i++;
                                        continue;
                                    }

                                    chunkedRecordReassembler ??= new();
                                    if (chunkedRecordReassembler.Append(chunkSpan, moreChunksFollow))
                                    {
                                        // The reassembler owns the inline buffer; pin it while the record it backs is used.
                                        fixed (byte* headerPtr = chunkedRecordReassembler.InlineBuffer)
                                        {
                                            if (!TryCompleteChunkedRecordReassembly(headerPtr, storeWrapper, transientObjectIdMap, out diskLogRecord))
                                            {
                                                logger?.LogError("Rejected malformed or null chunked migrated log record");
                                                throw new GarnetException("Malformed or null chunked migrated log record");
                                            }

                                            var slot = HashSlotUtils.HashSlot(diskLogRecord.Key);
                                            if (!currentConfig.IsImportingSlot(slot)) // Slot is not in importing state
                                            {
                                                migrateState = 1;
                                            }
                                            else
                                            {
                                                // Set if key replace flag is set or key does not exist
                                                var keySlice = PinnedSpanByte.FromPinnedSpan(diskLogRecord.Key);
                                                if (replaceOption || !Exists(keySlice))
                                                    _ = basicGarnetApi.SET(in diskLogRecord);
                                            }

                                            storeWrapper.storeFunctions.OnDisposeDiskRecord(ref diskLogRecord, DisposeReason.DeserializedFromDisk);
                                            diskLogRecord.Dispose();
                                            diskLogRecord = default; // prevent double-trigger in finally
                                        }
                                        chunkedRecordReassembler.Reset();
                                    }

                                    i++;
                                    continue;
                                }

                                if (!RespReadUtils.GetSerializedRecordSpan(out var payloadRaw, ref payloadPtr, payloadEndPtr))
                                {
                                    logger?.LogError("Rejected malformed migrated record frame");
                                    throw new GarnetException("Malformed migrated record frame");
                                }

                                // An error has occurred
                                if (migrateState > 0)
                                {
                                    i++;
                                    continue;
                                }

                                // Protocol enforcement: while receiving a RangeIndex stream, only SerializedRangeIndexStream records are valid
                                if (clusterProvider.serverOptions.EnableRangeIndexPreview && rangeIndexMigrationState.IsReceiving && kind != MigrationRecordSpanType.SerializedRangeIndexStream)
                                {
                                    logger?.LogError("Protocol violation: expected SerializedRangeIndexStream continuation after {ChunkCount} chunks, got {Kind}", rangeIndexMigrationState.CurrentChunkCount, kind);
                                    migrateState = 1;
                                    i++;
                                    continue;
                                }

                                if (kind == MigrationRecordSpanType.VectorSetElement)
                                {
                                    // This is a Vector Set namespace key being migrated - it won't necessarily look like it's "in" a hash slot
                                    // because it's dependent on some other key (the index key) being migrated which itself is in a moving hash slot

                                    // Vector Set elements are Namespace + Key + Value

                                    var payload = payloadRaw.Span;

                                    VectorManager.DeserializeMigratedElementKey(payload, out var namespaceBytes, out var keyBytes, out var valueBytes);

                                    // An error has occurred
                                    if (migrateState > 0)
                                    {
                                        i++;
                                        continue;
                                    }

                                    clusterProvider.storeWrapper.DefaultDatabase.VectorManager.HandleMigratedElementKey(ref stringBasicContext, ref vectorBasicContext, namespaceBytes, keyBytes, valueBytes);
                                }
                                else if (kind == MigrationRecordSpanType.SerializedRangeIndexStream)
                                {
                                    if (!clusterProvider.serverOptions.EnableRangeIndexPreview)
                                    {
                                        logger?.LogError("Received RangeIndex migration data but RangeIndex feature is not enabled");
                                        migrateState = 1;
                                        i++;
                                        continue;
                                    }

                                    if (!rangeIndexMigrationState.ProcessRecord(payloadRaw.ReadOnlySpan, currentConfig, ref stringBasicContext, replaceOption))
                                    {
                                        logger?.LogError("Failed to process RangeIndex migration record");
                                        migrateState = 1;
                                        i++;
                                        continue;
                                    }
                                }
                                else if (kind == MigrationRecordSpanType.LogRecord)
                                {
                                    // An error has occurred
                                    if (migrateState > 0)
                                    {
                                        i++;
                                        continue;
                                    }

                                    if (!DiskLogRecord.TryDeserialize(payloadRaw, storeWrapper.GarnetObjectSerializer, transientObjectIdMap, out diskLogRecord))
                                    {
                                        logger?.LogError("Rejected malformed or null migrated log record");
                                        throw new GarnetException("Malformed or null migrated log record");
                                    }

                                    var slot = HashSlotUtils.HashSlot(diskLogRecord.Key);
                                    if (!currentConfig.IsImportingSlot(slot)) // Slot is not in importing state
                                    {
                                        migrateState = 1;
                                        i++;
                                        continue;
                                    }

                                    // Set if key replace flag is set or key does not exist
                                    var keySlice = PinnedSpanByte.FromPinnedSpan(diskLogRecord.Key);
                                    if (replaceOption || !Exists(keySlice))
                                        _ = basicGarnetApi.SET(in diskLogRecord);

                                    storeWrapper.storeFunctions.OnDisposeDiskRecord(ref diskLogRecord, DisposeReason.DeserializedFromDisk);
                                    diskLogRecord.Dispose();
                                    diskLogRecord = default; // prevent double-trigger in finally
                                }
                                else
                                {
                                    throw new InvalidOperationException($"Unexpected {nameof(MigrationRecordSpanType)}: {kind}");
                                }

                                i++;
                            }
                        }
                    }
                    finally
                    {
                        if (diskLogRecord.IsSet)
                        {
                            storeWrapper.storeFunctions.OnDisposeDiskRecord(ref diskLogRecord, DisposeReason.DeserializedFromDisk);
                            diskLogRecord.Dispose();
                        }
                    }
                }
            }

            while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_OK, ref dcurr, dend))
                SendAndReset();

            return true;
        }

        /// <summary>
        /// Implements CLUSTER MTASKS command
        /// </summary>
        /// <param name="invalidParameters"></param>
        /// <returns></returns>
        private bool NetworkClusterMTasks(out bool invalidParameters)
        {
            invalidParameters = false;

            if (parseState.Count != 0)
            {
                invalidParameters = true;
                return true;
            }

            var mtasks = clusterProvider.migrationManager.GetMigrationTaskCount();
            while (!RespWriteUtils.TryWriteInt32(mtasks, ref dcurr, dend))
                SendAndReset();

            return true;
        }
    }
}