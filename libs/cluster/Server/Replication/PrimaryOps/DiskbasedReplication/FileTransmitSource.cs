// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Garnet.client;
using Garnet.common;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.cluster
{
    /// <summary>
    /// Transmits checkpoint file segments over the network using chunked reads from its owned <see cref="FileDataSource"/>.
    /// Sends each chunk via <see cref="GarnetClientSession.ExecuteClusterSnapshotData"/>
    /// followed by an empty end-of-transmission packet.
    /// </summary>
    internal sealed class FileTransmitSource : ISnapshotTransmitSource
    {
        readonly ILogger logger;

        public ISnapshotDataSource DataSource { get; }

        public FileTransmitSource(ISnapshotDataSource dataSource, ILogger logger = null)
        {
            DataSource = dataSource;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task TransmitAsync(GarnetClientSession gcs, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var fileTokenBytes = DataSource.Token.ToByteArray();
            var window = new SnapshotChunkWindow(timeout, resp => $"Primary error at TransmitAsync {DataSource.Type} {resp} [{DataSource.StartOffset},{DataSource.CurrentOffset},{DataSource.EndOffset}]", cancellationToken);

            // The next chunk is read while the current one is sent, so disk reads overlap the network transfer.
            var nextRead = DataSource.HasNextChunk ? DataSource.ReadNextChunkAsync(cancellationToken) : null;
            try
            {
                while (nextRead != null)
                {
                    var result = await nextRead.ConfigureAwait(false);
                    nextRead = null;

                    Task<string> response;
                    try
                    {
                        if (DataSource.HasNextChunk)
                            nextRead = DataSource.ReadNextChunkAsync(cancellationToken);

                        response = gcs.ExecuteClusterSnapshotData(fileTokenBytes, (int)DataSource.Type, startAddress: result.ChunkStartAddress, result.Buffer.GetSlice(result.BytesRead));
                    }
                    finally
                    {
                        result.Buffer.Return();
                    }

                    await window.AddAsync(response).ConfigureAwait(false);
                }
            }
            finally
            {
                await SnapshotReadAhead.AbandonAsync(nextRead).ConfigureAwait(false);
            }

            await window.DrainAsync().ConfigureAwait(false);

            // Send empty package to indicate end of transmission
            var endResp = await gcs.ExecuteClusterSnapshotData(
                fileTokenBytes, (int)DataSource.Type, DataSource.CurrentOffset, [])
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (!endResp.Equals("OK"))
                ExceptionUtils.ThrowException(new GarnetException(
                    $"Primary error at TransmitAsync Completion {DataSource.Type} {endResp}"));
        }

        public void Dispose()
        {
            DataSource?.Dispose();
        }
    }

    internal static unsafe class SectorAlignedMemoryExtensions
    {
        public static Span<byte> GetSlice(this SectorAlignedMemory pbuffer, int length)
        {
            return new Span<byte>(pbuffer.aligned_pointer, length);
        }
    }
}