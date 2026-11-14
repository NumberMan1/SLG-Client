using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Slg.Protocol;
using UnityEngine.Networking;

namespace Slg.Networking
{
    public sealed class GatewayConnection : IDisposable
    {
        private const int HeaderSize = 10;
        private const int MaxPayload = 1024;
        private const uint ProtocolVersion = 1;
        private const uint ConnectSequence = 1;

        private readonly TcpClient tcp;
        public ConnectReply Session { get; }

        private GatewayConnection(TcpClient tcp, ConnectReply session)
        {
            this.tcp = tcp;
            Session = session;
        }

        public static async Task<GatewayConnection> LoginAsync(string gatewayUrl, string account, string password)
        {
            var gateway = new Uri(gatewayUrl, UriKind.Absolute);
            if (gateway.Scheme != "http" && gateway.Scheme != "https")
            {
                throw new ArgumentException("Gateway URL must use HTTP or HTTPS.", nameof(gatewayUrl));
            }

            var login = await RequestTicketAsync(gateway, account, password);
            if (string.IsNullOrEmpty(login.Ticket) || login.TcpPort == 0 || login.TcpPort > 65535)
            {
                throw new InvalidDataException("Gateway returned an invalid login reply.");
            }

            var tcp = new TcpClient { NoDelay = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var closeOnTimeout = timeout.Token.Register(tcp.Dispose);
            try
            {
                await tcp.ConnectAsync(gateway.DnsSafeHost, (int)login.TcpPort);
                var stream = tcp.GetStream();
                var request = new ConnectRequest { Ticket = login.Ticket, ProtocolVersion = ProtocolVersion };
                await SendConnectAsync(stream, request, timeout.Token);
                var reply = await ReadConnectAsync(stream, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (reply.Account != account || reply.SessionId == 0)
                {
                    throw new InvalidDataException("Gateway returned an invalid session.");
                }
                return new GatewayConnection(tcp, reply);
            }
            catch (Exception error)
            {
                tcp.Dispose();
                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("TCP authentication timed out.", error);
                }
                throw;
            }
        }

        private static async Task<LoginReply> RequestTicketAsync(Uri gateway, string account, string password)
        {
            var body = new LoginRequest { Account = account, Password = password }.ToByteArray();
            using var request = new UnityWebRequest(new Uri(gateway, "/v1/login").AbsoluteUri, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 5,
                redirectLimit = 0
            };
            request.SetRequestHeader("Content-Type", "application/protobuf");
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            request.SendWebRequest().completed += _ => finished.TrySetResult(true);
            await finished.Task;

            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
            {
                throw new InvalidOperationException($"Login failed (HTTP {request.responseCode}): {request.error}");
            }
            return LoginReply.Parser.ParseFrom(request.downloadHandler.data);
        }

        private static async Task SendConnectAsync(NetworkStream stream, ConnectRequest request, CancellationToken token)
        {
            var payload = request.ToByteArray();
            if (payload.Length > MaxPayload)
            {
                throw new InvalidDataException("Connect request exceeds the frame limit.");
            }
            var frame = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)SessionMessageId.ConnectRequest);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), ConnectSequence);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            await stream.WriteAsync(frame, 0, frame.Length, token);
        }

        private static async Task<ConnectReply> ReadConnectAsync(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[HeaderSize];
            await ReadExactlyAsync(stream, header, token);
            int size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            if (size == 0 || size > MaxPayload ||
                BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != (ushort)SessionMessageId.ConnectReply ||
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) != ConnectSequence ||
                BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2)) != 0)
            {
                throw new InvalidDataException("Gateway returned an invalid connect frame.");
            }
            var payload = new byte[size];
            await ReadExactlyAsync(stream, payload, token);
            return ConnectReply.Parser.ParseFrom(payload);
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token);
                if (count == 0)
                {
                    throw new EndOfStreamException("Gateway closed the TCP connection before authentication completed.");
                }
                offset += count;
            }
        }

        public async Task WaitForDisconnectAsync()
        {
            var buffer = new byte[1];
            int count = await tcp.GetStream().ReadAsync(buffer, 0, buffer.Length);
            if (count != 0)
            {
                throw new InvalidDataException("Gateway sent unexpected data after authentication.");
            }
        }

        public void Dispose() => tcp.Dispose();
    }
}
