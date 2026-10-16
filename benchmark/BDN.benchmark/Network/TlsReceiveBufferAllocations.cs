// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

namespace BDN.benchmark.Network
{
    /// <summary>
    /// Measures allocated bytes per large message through Garnet's TLS receive loop without storage allocations.
    /// </summary>
    [MemoryDiagnoser(displayGenColumns: false)]
    [HideColumns(Column.Mean, Column.Error, Column.StdDev, Column.Median, Column.Ratio, Column.RatioSD)]
    [WarmupCount(1), IterationCount(3), InvocationCount(1)]
    public class TlsReceiveBufferAllocations
    {
        private const int MessagesPerBatch = 4;
        private TlsReceiveBufferSession session;

        /// <summary>Power-of-two receive capacity required by each message, in MiB.</summary>
        [Params(8, 32, 64)]
        public int MessageSizeMiB { get; set; }

        /// <summary>Largest receive class; increasing it also enables pooling at larger sizes.</summary>
        [Params(1, 64)]
        public int LargestReceiveClassMiB { get; set; }

        /// <summary>Creates the payload and TLS connection and warms the buffer pools outside measurement.</summary>
        [GlobalSetup]
        public async Task Setup()
        {
            session = new TlsReceiveBufferSession(MessageSizeMiB, LargestReceiveClassMiB);
            await session.StartAsync();
            await ReceiveMessages();
        }

        /// <summary>Releases the connection and waits for its receive pump to stop.</summary>
        [GlobalCleanup]
        public async Task Cleanup() => await session.DisposeAsync();

        /// <summary>Receives content-verified messages, normalized to allocated bytes per message.</summary>
        [Benchmark(OperationsPerInvoke = MessagesPerBatch)]
        public async Task ReceiveMessages()
        {
            for (var i = 0; i < MessagesPerBatch; i++)
                await session.SendAsync();
        }

        /// <summary>Runs the same workload with process-wide allocation counters and pool statistics.</summary>
        public static async Task ReproduceAsync()
        {
            foreach (var messageSizeMiB in new[] { 8, 32, 64 })
            {
                foreach (var largestReceiveClassMiB in new[] { 1, 64 })
                {
                    await using var connection = new TlsReceiveBufferSession(messageSizeMiB, largestReceiveClassMiB);
                    await connection.StartAsync();
                    for (var i = 0; i < MessagesPerBatch; i++)
                        await connection.SendAsync();

                    var beforeStats = connection.PoolStats;
                    var before = GC.GetTotalAllocatedBytes(precise: true);
                    for (var i = 0; i < MessagesPerBatch; i++)
                        await connection.SendAsync();

                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        messageSizeMiB,
                        largestReceiveClassMiB,
                        messages = MessagesPerBatch,
                        payloadBytesPerMessage = connection.PayloadBytes,
                        allocatedBytesPerMessage = allocated / MessagesPerBatch,
                        receiveCapacityBytes = connection.ReceiveCapacity,
                        beforeStats,
                        afterStats = connection.PoolStats
                    }));
                }
            }
        }
    }
}