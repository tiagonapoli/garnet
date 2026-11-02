// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Garnet.client;
using Garnet.common;
using Microsoft.Extensions.Logging;

namespace Garnet.cluster
{
    /// <summary>
    /// Transmits a RangeIndex file over the network. Sends a metadata header (with
    /// <c>startAddress = -1</c>) containing the serialized key hash and address needed to
    /// reconstruct the target path, followed by chunked file content, followed by an empty
    /// end-of-transmission marker.
    ///
    /// <para>Metadata payload layout:</para>
    /// <list type="bullet">
    /// <item><b>STORE_RANGEINDEX_FLUSH</b>: keyHash (32 bytes ASCII) + address (8 bytes little-endian) = 40 bytes</item>
    /// <item><b>STORE_RANGEINDEX_SNAPSHOT</b>: keyHash (32 bytes ASCII) = 32 bytes</item>
    /// </list>
    /// </summary>
    internal sealed class RangeIndexFileTransmitSource : ISnapshotTransmitSource
    {
        private readonly ILogger logger;

        public ISnapshotDataSource DataSource { get; }

        public RangeIndexFileTransmitSource(ISnapshotDataSource dataSource, ILogger logger = null)
        {
            DataSource = dataSource;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task TransmitAsync(GarnetClientSession gcs, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var riDataSource = (RangeIndexFileDataSource)DataSource;
            var fileTokenBytes = DataSource.Token.ToByteArray();

            // Get metadata from data source: keyHash + address (flush only)
            var metadata = riDataSource.GetMetadata();

            // Send header with startAddress = -1 to indicate single-message control payload.
            var headerResp = await gcs.ExecuteClusterSnapshotData(
                fileTokenBytes, (int)DataSource.Type, -1, metadata)
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (!headerResp.Equals("OK"))
                ExceptionUtils.ThrowException(new GarnetException(
                    $"Replica error at RangeIndex header {DataSource.Type} {headerResp} keyHash={riDataSource.KeyHash}"));

            var stats = new SnapshotTransferStats();
            long wireBytes = 0;
            var window = new SnapshotChunkWindow(timeout, resp => $"Replica error at RangeIndex TransmitAsync {DataSource.Type} {resp} [{DataSource.StartOffset},{DataSource.CurrentOffset},{DataSource.EndOffset}]", cancellationToken);

            if (riDataSource.Compress)
                wireBytes = await SendCompressedAsync(gcs, fileTokenBytes, window, stats, cancellationToken).ConfigureAwait(false);
            else
                wireBytes = await SendRawAsync(gcs, fileTokenBytes, window, stats, cancellationToken).ConfigureAwait(false);

            await window.DrainAsync().ConfigureAwait(false);

            // Send empty package to indicate end of transmission
            var endResp = await gcs.ExecuteClusterSnapshotData(
                fileTokenBytes, (int)DataSource.Type, DataSource.CurrentOffset, [])
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (!endResp.Equals("OK"))
                ExceptionUtils.ThrowException(new GarnetException(
                    $"Replica error at RangeIndex TransmitAsync Completion {DataSource.Type} {endResp}"));

            stats.Log(logger, "primary", DataSource.Type, $"{riDataSource.KeyHash}@{riDataSource.Address}");
            logger?.LogInformation("[RangeIndexTransferCompression] type={type} keyHash={keyHash} compress={compress} rawBytes={raw} wireBytes={wire} ratio={ratio:F1} rawChunks={rawChunks}",
                DataSource.Type, riDataSource.KeyHash, riDataSource.Compress, DataSource.CurrentOffset, wireBytes, wireBytes == 0 ? 0 : (double)DataSource.CurrentOffset / wireBytes, rawChunks);
        }

        /// <summary>
        /// Sends the file content as read, reading the next chunk while the current one is sent.
        /// </summary>
        private async Task<long> SendRawAsync(GarnetClientSession gcs, byte[] fileTokenBytes, SnapshotChunkWindow window, SnapshotTransferStats stats, CancellationToken cancellationToken)
        {
            long wireBytes = 0;
            var nextRead = DataSource.HasNextChunk ? DataSource.ReadNextChunkAsync(cancellationToken) : null;
            try
            {
                while (nextRead != null)
                {
                    var readStart = Stopwatch.GetTimestamp();
                    var result = await nextRead.ConfigureAwait(false);
                    nextRead = DataSource.HasNextChunk ? DataSource.ReadNextChunkAsync(cancellationToken) : null;
                    var sendStart = Stopwatch.GetTimestamp();
                    stats.AddRead(sendStart - readStart);

                    wireBytes += result.BytesRead;
                    await window.AddAsync(gcs.ExecuteClusterSnapshotData(fileTokenBytes, (int)DataSource.Type, startAddress: result.ChunkStartAddress, new Span<byte>(result.Data, 0, result.BytesRead))).ConfigureAwait(false);
                    stats.AddSend(Stopwatch.GetTimestamp() - sendStart, result.BytesRead);
                }
            }
            finally
            {
                await SnapshotReadAhead.AbandonAsync(nextRead).ConfigureAwait(false);
            }

            return wireBytes;
        }

        /// <summary>
        /// Sends compressed frames produced by a background task that reads and compresses ahead of the send loop, so
        /// disk reads and compression overlap the network round trips.
        /// </summary>
        private async Task<long> SendCompressedAsync(GarnetClientSession gcs, byte[] fileTokenBytes, SnapshotChunkWindow window, SnapshotTransferStats stats, CancellationToken cancellationToken)
        {
            // A frame is reused only after the send loop has moved past it: up to QueuedFrames wait in the channel, one is
            // being sent and one is being encoded.
            var frames = new byte[QueuedFrames + 2][];
            for (var i = 0; i < frames.Length; i++)
                frames[i] = ArrayPool<byte>.Shared.Rent(RangeIndexFileDataSource.DefaultChunkSize + 1);

            var ready = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(QueuedFrames) { SingleReader = true, SingleWriter = true });
            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = Task.Run(() => ProduceFramesAsync(frames, ready.Writer, producerCts.Token));
            long wireBytes = 0;
            try
            {
                while (true)
                {
                    var waitStart = Stopwatch.GetTimestamp();
                    if (!await ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        break;
                    if (!ready.Reader.TryRead(out var frame))
                        continue;
                    var sendStart = Stopwatch.GetTimestamp();
                    stats.AddRead(sendStart - waitStart);

                    wireBytes += frame.Length;
                    await window.AddAsync(gcs.ExecuteClusterSnapshotData(fileTokenBytes, (int)DataSource.Type, startAddress: frame.ChunkStartAddress, frames[frame.Index].AsSpan(0, frame.Length))).ConfigureAwait(false);
                    stats.AddSend(Stopwatch.GetTimestamp() - sendStart, frame.RawLength);
                }

                // Surfaces a producer failure, which completes the channel early.
                await producer.ConfigureAwait(false);
            }
            finally
            {
                await producerCts.CancelAsync().ConfigureAwait(false);
                try { await producer.ConfigureAwait(false); }
                catch { }

                foreach (var frame in frames)
                    ArrayPool<byte>.Shared.Return(frame);
            }

            return wireBytes;
        }

        private async Task ProduceFramesAsync(byte[][] frames, ChannelWriter<EncodedFrame> writer, CancellationToken cancellationToken)
        {
            Exception error = null;
            var rawChunksLeft = 0;
            var nextRead = DataSource.HasNextChunk ? DataSource.ReadNextChunkAsync(cancellationToken) : null;
            try
            {
                for (var index = 0; nextRead != null; index = (index + 1) % frames.Length)
                {
                    var result = await nextRead.ConfigureAwait(false);
                    nextRead = DataSource.HasNextChunk ? DataSource.ReadNextChunkAsync(cancellationToken) : null;
                    var chunk = result.Data.AsSpan(0, result.BytesRead);
                    int length;
                    if (rawChunksLeft > 0)
                    {
                        rawChunksLeft--;
                        length = WriteRawFrame(chunk, frames[index]);
                    }
                    else
                    {
                        length = EncodeChunk(chunk, frames[index]);
                        if ((long)length * MinCompressionRatio > chunk.Length)
                            rawChunksLeft = RawChunksAfterPoorCompression;
                    }

                    if (frames[index][0] == (byte)RangeIndexFileDataSource.ChunkCodec.Raw)
                        rawChunks++;
                    await writer.WriteAsync(new EncodedFrame(index, length, result.BytesRead, result.ChunkStartAddress), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                await SnapshotReadAhead.AbandonAsync(nextRead).ConfigureAwait(false);
                writer.TryComplete(error);
            }
        }

        /// <summary>
        /// Writes <paramref name="chunk"/> into <paramref name="frame"/> as a codec byte followed by the Deflate-compressed
        /// chunk, or by the raw chunk when compression does not shrink it. Returns the frame length.
        /// </summary>
        internal static int EncodeChunk(ReadOnlySpan<byte> chunk, byte[] frame)
        {
            try
            {
                // A non-expandable stream bounded by the chunk length throws once compression stops paying off.
                var output = new MemoryStream(frame, 1, chunk.Length);
                using (var deflater = new DeflateStream(output, RangeIndexFileDataSource.DeflateLevel, leaveOpen: true))
                    deflater.Write(chunk);

                frame[0] = (byte)RangeIndexFileDataSource.ChunkCodec.Deflate;
                return (int)output.Position + 1;
            }
            catch (NotSupportedException)
            {
                return WriteRawFrame(chunk, frame);
            }
        }

        private static int WriteRawFrame(ReadOnlySpan<byte> chunk, byte[] frame)
        {
            frame[0] = (byte)RangeIndexFileDataSource.ChunkCodec.Raw;
            chunk.CopyTo(frame.AsSpan(1));
            return chunk.Length + 1;
        }

        /// <summary>Frames queued ahead of the send loop.</summary>
        private const int QueuedFrames = 8;

        /// <summary>
        /// Compression ratio below which compressing costs more than it saves: Deflate slows to ~100 MB/s on data
        /// that barely compresses, such as random hex, far below the raw transfer rate.
        /// </summary>
        private const int MinCompressionRatio = 4;

        /// <summary>Chunks sent raw after a poorly compressing chunk before compression is tried again.</summary>
        private const int RawChunksAfterPoorCompression = 64;

        /// <summary>Chunks sent uncompressed; written by the producer and read after it completes.</summary>
        private long rawChunks;

        private readonly record struct EncodedFrame(int Index, int Length, int RawLength, long ChunkStartAddress);

        public void Dispose()
        {
            DataSource?.Dispose();
        }
    }
}