// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.IO;
using Embedded.server;
using Garnet.common;
using Garnet.networking;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    [TestFixture]
    public class NetworkReceiveBufferTests : TestBase
    {
        const int BaseSize = 4096;
        [TearDown]
        public void TearDown() => TestUtils.OnTearDown();

        [TestCase(64 * 1024, false)]
        [TestCase(64 * 1024, true)]
        [TestCase(2 * 1024 * 1024, false)]
        [TestCase(2 * 1024 * 1024, true)]
        public void BulkReceiveBuffersSurviveFragmentedRecurringMessages(int largestClass, bool pressure)
        {
            var settings = Settings(largestClass);
            var budget = new NetworkBufferBudget(pressure ? 1 : 0, BaseSize, 512, 512);
            using var pool = settings.CreateBufferPool(maxPooledBytes: 4 * 1024 * 1024, budget: budget);
            var consumer = new FrameConsumer();
            using var handler = new ReceiveHandler(settings, pool, consumer, bulkTransfer: true);
            var payload = Frame(1024 * 1024 - 1024);
            Feed(handler, payload);
            var retained = handler.Capacity;
            ClassicAssert.GreaterOrEqual(retained, payload.Length);

            for (var i = 0; i < 4; i++)
                ClassicAssert.AreEqual(retained, Feed(handler, payload), "a partial or completed large message released its receive capacity");

            ClassicAssert.AreEqual(5, consumer.Messages);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BulkReceiveBuffersShrinkAfterCompletedSmallMessages(bool pressure)
        {
            var settings = Settings(64 * 1024);
            var budget = new NetworkBufferBudget(pressure ? 1 : 0, BaseSize, 512, 512);
            using var pool = settings.CreateBufferPool(maxPooledBytes: 4 * 1024 * 1024, budget: budget);
            var consumer = new FrameConsumer();
            using var handler = new ReceiveHandler(settings, pool, consumer, bulkTransfer: true);
            var large = Frame(1024 * 1024 - 1024);
            Feed(handler, large);
            ClassicAssert.GreaterOrEqual(handler.Capacity, large.Length);

            var small = Frame(16);
            var rounds = pressure ? 32 : 512;
            for (var i = 0; i < rounds; i++)
                Feed(handler, small);

            ClassicAssert.LessOrEqual(handler.Capacity, BaseSize);
            Feed(handler, large);
            ClassicAssert.AreEqual(rounds + 2, consumer.Messages, "messages must still round-trip after shrinking and growing again");
        }

        [Test]
        public void DataPlaneStillReleasesOversizedReceiveBuffersImmediately()
        {
            var settings = Settings(64 * 1024);
            using var pool = settings.CreateBufferPool();
            var consumer = new FrameConsumer();
            using var handler = new ReceiveHandler(settings, pool, consumer, bulkTransfer: false);
            Feed(handler, Frame(1024 * 1024 - 1024));
            ClassicAssert.AreEqual(settings.maxReceiveBufferSize, handler.Capacity);
            ClassicAssert.AreEqual(1, consumer.Messages);
        }

        static NetworkBufferSettings Settings(int largestClass) => new(sendBufferSize: BaseSize, initialReceiveBufferSize: BaseSize, maxReceiveBufferSize: largestClass, minAllocationSize: 512);

        static byte[] Frame(int length)
        {
            var frame = new byte[length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, length);
            frame.AsSpan(sizeof(int)).Fill(0x5A);
            return frame;
        }

        static int Feed(ReceiveHandler handler, ReadOnlySpan<byte> frame)
        {
            var minimumCapacity = handler.Capacity;
            while (!frame.IsEmpty)
            {
                var count = Math.Min(1024, Math.Min(frame.Length, handler.Available));
                ClassicAssert.Greater(count, 0, "receive buffer must have room to make progress");
                handler.Receive(frame[..count]);
                frame = frame[count..];
                minimumCapacity = Math.Min(minimumCapacity, handler.Capacity);
            }

            return minimumCapacity;
        }

        sealed class ReceiveHandler : NetworkHandler<FrameConsumer, EmbeddedNetworkSender>
        {
            readonly bool bulkTransfer;

            public unsafe ReceiveHandler(NetworkBufferSettings settings, LimitedFixedBufferPool pool, FrameConsumer consumer, bool bulkTransfer) : base(consumer, new EmbeddedNetworkSender(), settings, pool, useTLS: false, messageConsumer: consumer)
            {
                this.bulkTransfer = bulkTransfer;
                networkReceiveBufferEntry = pool.Get(BaseReceiveBufferSize, PoolEntryBufferType.NetworkReceiveBuffer);
                networkReceiveBuffer = networkReceiveBufferEntry.entry;
                networkReceiveBufferPtr = networkReceiveBufferEntry.entryPtr;
            }

            protected override bool RetainReceiveBuffers => bulkTransfer;
            public int Capacity => networkReceiveBuffer.Length;
            public int Available => networkReceiveBuffer.Length - networkBytesRead;
            public override string RemoteEndpointName => "";
            public override string LocalEndpointName => "";
            public override bool IsLocalConnection() => true;
            public override bool TryClose() => false;
            public override void Dispose() => DisposeImpl();

            public void Receive(ReadOnlySpan<byte> bytes)
            {
                bytes.CopyTo(networkReceiveBuffer.AsSpan(networkBytesRead));
                OnNetworkReceiveWithoutTLS(bytes.Length);
            }
        }

        sealed class FrameConsumer : IServerHook, IMessageConsumer
        {
            public int Messages { get; private set; }
            public bool Disposed => false;
            public void Dispose() { }
            public void DisposeMessageConsumer(INetworkHandler handler) => Dispose();
            public bool TryCreateMessageConsumer(Span<byte> bytes, INetworkSender sender, out IMessageConsumer session)
            {
                session = this;
                return true;
            }

            public unsafe int TryConsumeMessages(byte* buffer, int length)
            {
                if (length < sizeof(int))
                    return 0;

                var bytes = new ReadOnlySpan<byte>(buffer, length);
                var frameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                if (length < frameLength)
                    return 0;

                if (bytes.Slice(sizeof(int), frameLength - sizeof(int)).IndexOfAnyExcept((byte)0x5A) >= 0)
                    throw new InvalidDataException("Receive buffer corrupted frame contents");

                Messages++;
                return frameLength;
            }
        }
    }
}