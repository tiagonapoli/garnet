// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Garnet.common;
using Garnet.networking;
using Microsoft.Extensions.Logging;

namespace BDN.benchmark.Network
{
    internal sealed class TlsReceiveBufferSession : IAsyncDisposable
    {
        private readonly byte[] payload;
        private readonly LimitedFixedBufferPool pool;
        private readonly SemaphoreSlim completed = new(0);
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        private readonly FrameConsumer consumer = new();
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        private Socket clientSocket;
        private SslStream clientStream;
        private ReceiveHandler handler;
        private Task receivePump;
        private X509Certificate2 certificate;

        public TlsReceiveBufferSession(int messageSizeMiB, int largestReceiveClassMiB)
        {
            payload = new byte[checked(messageSizeMiB * 1024 * 1024 - 1024)];
            BinaryPrimitives.WriteInt32LittleEndian(payload, payload.Length);
            payload.AsSpan(sizeof(int)).Fill(0x5A);
            var settings = new NetworkBufferSettings(maxReceiveBufferSize: checked(largestReceiveClassMiB * 1024 * 1024));
            pool = settings.CreateBufferPool(ownerType: PoolOwnerType.ServerNetwork, maxPooledBytes: 64L * 1024 * 1024);
            Settings = settings;
        }

        public int PayloadBytes => payload.Length;
        public int ReceiveCapacity => handler.ReceiveCapacity;
        public string PoolStats => pool.GetStats();
        private NetworkBufferSettings Settings { get; }

        public async Task StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Reimport so Windows TLS can acquire credentials for the generated private key.
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var accept = listener.AcceptAsync(timeout.Token);
            clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync(listener.LocalEndPoint, timeout.Token);
            var accepted = await accept;
            handler = new ReceiveHandler(accepted, Settings, pool, consumer, loggerFactory.CreateLogger<ReceiveHandler>());
            receivePump = PumpAsync();
            var serverAuth = handler.StartAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token: timeout.Token);
            clientStream = new SslStream(new NetworkStream(clientSocket, ownsSocket: false), leaveInnerStreamOpen: false,
                (_, remote, _, _) => remote != null && remote.GetCertHashString() == certificate.GetCertHashString());
            await Task.WhenAll(serverAuth, clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token));
        }

        public async Task SendAsync()
        {
            await clientStream.WriteAsync(payload, timeout.Token);
            var completion = completed.WaitAsync(timeout.Token);
            var finished = await Task.WhenAny(completion, receivePump);
            await finished;
            if (finished == receivePump)
                throw new IOException("TLS receive pump stopped before completing a message");
        }

        private async Task PumpAsync()
        {
            var signalled = 0;
            try
            {
                while (await handler.ReceiveAsync(timeout.Token))
                {
                    var received = consumer.Messages;
                    if (received > signalled)
                    {
                        completed.Release(received - signalled);
                        signalled = received;
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            timeout.Cancel();
            try
            {
                if (receivePump != null)
                    await receivePump;
            }
            finally
            {
                clientStream?.Dispose();
                clientSocket?.Dispose();
                handler?.Dispose();
                certificate?.Dispose();
                pool.Dispose();
                completed.Dispose();
                timeout.Dispose();
                loggerFactory.Dispose();
            }
        }

        private sealed class ReceiveHandler : NetworkHandler<FrameHook, GarnetTcpNetworkSender>
        {
            private readonly Socket socket;
            private int closeRequested;

            public unsafe ReceiveHandler(Socket socket, NetworkBufferSettings settings, LimitedFixedBufferPool pool, FrameConsumer consumer, ILogger logger) : base(new FrameHook(consumer), new GarnetTcpNetworkSender(socket, settings, pool), settings, pool, useTLS: true, messageConsumer: consumer, logger: logger)
            {
                this.socket = socket;
                networkReceiveBufferEntry = pool.Get(BaseReceiveBufferSize, PoolEntryBufferType.NetworkReceiveBuffer);
                networkReceiveBuffer = networkReceiveBufferEntry.entry;
                networkReceiveBufferPtr = networkReceiveBufferEntry.entryPtr;
            }

            public int ReceiveCapacity => transportReceiveBuffer.Length;
            public override string RemoteEndpointName => socket.RemoteEndPoint.ToString();
            public override string LocalEndpointName => socket.LocalEndPoint.ToString();
            public override bool IsLocalConnection() => true;
            public override bool TryClose()
            {
                if (Interlocked.Exchange(ref closeRequested, 1) != 0)
                    return false;

                socket.Close();
                return true;
            }

            public override void Dispose()
            {
                socket.Dispose();
                DisposeImpl();
            }

            public async ValueTask<bool> ReceiveAsync(CancellationToken token)
            {
                var count = await socket.ReceiveAsync(networkReceiveBuffer.AsMemory(networkBytesRead), SocketFlags.None, token);
                if (count == 0)
                    return false;

                // Await the production TLS processing and buffer maintenance before acknowledging the message.
                await OnNetworkReceiveWithTLSAsync(count);
                return true;
            }
        }

        private sealed class FrameHook : IServerHook
        {
            private readonly FrameConsumer consumer;
            public FrameHook(FrameConsumer consumer) => this.consumer = consumer;
            public bool Disposed => false;
            public bool TryCreateMessageConsumer(Span<byte> bytesReceived, INetworkSender networkSender, out IMessageConsumer session)
            {
                session = consumer;
                return true;
            }

            public void DisposeMessageConsumer(INetworkHandler session) => consumer.Dispose();
        }

        private sealed class FrameConsumer : IMessageConsumer
        {
            public int Messages { get; private set; }
            public unsafe int TryConsumeMessages(byte* reqBuffer, int bytesRead)
            {
                if (bytesRead < sizeof(int))
                    return 0;

                var bytes = new ReadOnlySpan<byte>(reqBuffer, bytesRead);
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                if (length < sizeof(int) || length > 64 * 1024 * 1024)
                    throw new InvalidDataException($"Invalid frame length: {length}");

                if (bytesRead < length)
                    return 0;

                if (bytes.Slice(sizeof(int), length - sizeof(int)).IndexOfAnyExcept((byte)0x5A) >= 0)
                    throw new InvalidDataException("TLS frame content mismatch");

                Messages++;
                return length;
            }

            public void Dispose() { }
        }
    }
}