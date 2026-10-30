// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Garnet.client;
using Microsoft.Extensions.Logging;

namespace Garnet.cluster
{
    /// <summary>
    /// Drives checkpoint transmission by iterating over checkpoint readers and sending segments.
    /// </summary>
    internal sealed class SnapshotTransmissionDriver : IDisposable
    {
        readonly List<ISnapshotReader> checkpointReaders = [];
        readonly GarnetClientSession gcs;
        readonly TimeSpan timeout;
        readonly ILogger logger;

        public SnapshotTransmissionDriver(GarnetClientSession gcs, TimeSpan timeout, ILogger logger = null)
        {
            this.gcs = gcs;
            this.timeout = timeout;
            this.logger = logger;
        }

        /// <summary>
        /// Adds a checkpoint reader whose transmit sources will be sent during <see cref="SendCheckpointAsync"/>.
        /// The driver takes ownership and will dispose the reader in <see cref="Dispose"/>.
        /// </summary>
        public void AddReader(ISnapshotReader reader) => checkpointReaders.Add(reader);

        public void Dispose()
        {
            foreach (var reader in checkpointReaders)
            {
                try { reader.Dispose(); }
                catch (Exception ex) { logger?.LogError(ex, "Error disposing checkpoint reader"); }
            }
        }

        /// <summary>
        /// Sends all checkpoint data by iterating transmit sources from each reader.
        /// For each source, delegates transmission to the <see cref="ISnapshotTransmitSource"/>.
        /// </summary>
        public async Task SendCheckpointAsync(CancellationToken cancellationToken = default)
        {
            var started = Stopwatch.GetTimestamp();
            var files = 0;
            long bytes = 0;
            var gen2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration();
            var allocated = GC.GetTotalAllocatedBytes(false);

            foreach (var checkpointReader in checkpointReaders)
            {
                foreach (var transmitSource in checkpointReader.GetTransmitSources())
                {
                    try
                    {
                        logger?.LogInformation("<Begin sending checkpoint data {token} {type} {startAddress} {endAddress}",
                            transmitSource.DataSource.Token, transmitSource.DataSource.Type, transmitSource.DataSource.StartOffset, transmitSource.DataSource.EndOffset);

                        await transmitSource.TransmitAsync(gcs, timeout, cancellationToken).ConfigureAwait(false);
                        files++;
                        bytes += transmitSource.DataSource.EndOffset - transmitSource.DataSource.StartOffset;

                        logger?.LogInformation("<Complete sending checkpoint data {token} {type} {startAddress} {endAddress}",
                            transmitSource.DataSource.Token, transmitSource.DataSource.Type, transmitSource.DataSource.StartOffset, transmitSource.DataSource.EndOffset);
                    }
                    finally
                    {
                        transmitSource.Dispose();
                    }
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            logger?.LogInformation("[SnapshotTransferSummary] files={files} bytes={bytes} elapsedSec={elapsedSec:F1} MBps={mbps:F1} gc2={gc2} gcPauseMs={pauseMs:F0} allocMB={allocMB:F1}",
                files, bytes, elapsed.TotalSeconds, bytes / 1e6 / Math.Max(elapsed.TotalSeconds, 1e-9), GC.CollectionCount(2) - gen2, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, (GC.GetTotalAllocatedBytes(false) - allocated) / 1e6);
        }
    }
}