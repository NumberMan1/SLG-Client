using System;
using System.Buffers.Binary;
using System.IO;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.Sockets;
#endif
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Slg.Protocol;
using UnityEngine.Networking;

namespace Slg.Networking
{
    /// <summary>持有通过网关认证的长连接，原生端使用 TCP，网页端使用 WebSocket。</summary>
    public sealed partial class GatewayConnection : IDisposable
    {
        // Orbit 帧头包含负载长度、消息类型、序号和错误码，整数按大端序编码。
        private const int HeaderSize = 10;
        private const int MaxPayload = 1024;
        private const uint ProtocolVersion = 1;
        private const uint ConnectSequence = 1;
        private uint nextSequence = ConnectSequence;
        private bool worldRequestPending;

        /// <summary>网关确认的账号及当前连接的会话编号。</summary>
        public ConnectReply Session { get; }

        /// <summary>在当前认证连接上读取状态或提交行军。每条连接同一时间只处理一个玩法请求。</summary>
        public async Task<WorldReply> UpdateWorldAsync(WorldRequest request)
        {
            if (worldRequestPending)
            {
                throw new InvalidOperationException("A world request is already in progress.");
            }
            var payload = request.ToByteArray();
            if (payload.Length > MaxPayload)
            {
                throw new InvalidDataException("World request exceeds the frame limit.");
            }
            var sequence = ++nextSequence;
            if (sequence == 0) sequence = ++nextSequence;
            var frame = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)WorldMessageId.Request);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), sequence);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

            worldRequestPending = true;
            try
            {
                return await ExchangeWorldAsync(frame, sequence);
            }
            finally
            {
                worldRequestPending = false;
            }
        }

        private static int ValidateWorldHeader(ReadOnlySpan<byte> header, uint sequence)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(0, 2));
            if (length == 0 || length > MaxPayload ||
                BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2, 2)) != (ushort)WorldMessageId.Reply ||
                BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4)) != sequence ||
                BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2)) != 0)
            {
                throw new InvalidDataException("Gateway returned an invalid world reply.");
            }
            return length;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private readonly TcpClient tcp;
        private GatewayConnection(TcpClient tcp, ConnectReply session)
        {
            this.tcp = tcp;
            Session = session;
        }

        /// <summary>通过 HTTP 获取登录票据，再用票据完成 TCP 握手。调用方负责释放返回的连接。</summary>
        public static async Task<GatewayConnection> LoginAsync(string gatewayUrl, string account, string password)
        {
            var gateway = ParseGateway(gatewayUrl);

            var login = await RequestTicketAsync(gateway, account, password);
            if (string.IsNullOrEmpty(login.Ticket) || login.TcpPort == 0 || login.TcpPort > 65535)
            {
                throw new InvalidDataException("Gateway returned an invalid login reply.");
            }

            var tcp = new TcpClient { NoDelay = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // 主动关闭套接字，使同一个时限覆盖 TCP 建连和认证收发。
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

#endif
        /// <summary>通过网关创建账号。注册成功后，由调用方发起登录并建立会话。</summary>
        public static async Task RegisterAsync(string gatewayUrl, string account, string password)
        {
            using var request = CreateCredentialRequest(ParseGateway(gatewayUrl), "/v1/register",
                new RegisterRequest { Account = account, Password = password });
            await SendCredentialRequestAsync(request);
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 201)
            {
                throw new InvalidOperationException(request.responseCode switch
                {
                    400 => "Use an account of up to 64 UTF-8 bytes and a password of at least 8 characters (up to 72 UTF-8 bytes).",
                    409 => "This account already exists. Choose another name or sign in.",
                    429 => "Too many registration requests. Try again shortly.",
                    503 => "Registration is temporarily unavailable. Try again shortly.",
                    _ => $"Registration failed (HTTP {request.responseCode}): {request.error}"
                });
            }
        }

        private static Uri ParseGateway(string gatewayUrl)
        {
            if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gateway) ||
                (gateway.Scheme != "http" && gateway.Scheme != "https"))
            {
                throw new ArgumentException("Gateway URL must use HTTP or HTTPS.");
            }
            return gateway;
        }

        private static async Task<LoginReply> RequestTicketAsync(Uri gateway, string account, string password)
        {
            using var request = CreateCredentialRequest(gateway, "/v1/login",
                new LoginRequest { Account = account, Password = password });
            await SendCredentialRequestAsync(request);
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
            {
                string message = request.responseCode == 401
                    ? "Account or password is incorrect. You can create an account below."
                    : $"Login failed (HTTP {request.responseCode}): {request.error}";
                throw new InvalidOperationException(message);
            }
            return LoginReply.Parser.ParseFrom(request.downloadHandler.data);
        }

        private static UnityWebRequest CreateCredentialRequest(Uri gateway, string path, IMessage credentials)
        {
            // 原生端和网页端共用 HTTP 协议，凭据只提交给指定的网关地址。
            var request = new UnityWebRequest(new Uri(gateway, path).AbsoluteUri, "POST")
            {
                uploadHandler = new UploadHandlerRaw(credentials.ToByteArray()),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 5,
                redirectLimit = 0
            };
            request.SetRequestHeader("Content-Type", "application/protobuf");
            return request;
        }

        private static Task SendCredentialRequestAsync(UnityWebRequest request)
        {
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            request.SendWebRequest().completed += _ => finished.TrySetResult(true);
            return finished.Task;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
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
            // TCP 读取可能只返回部分数据，凑齐所需字节后再解析帧。
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token);
                if (count == 0)
                {
                    throw new EndOfStreamException("Gateway closed the TCP connection before the frame was complete.");
                }
                offset += count;
            }
        }

        private async Task<WorldReply> ExchangeWorldAsync(byte[] frame, uint sequence)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stream = tcp.GetStream();
            await stream.WriteAsync(frame, 0, frame.Length, timeout.Token);
            var header = new byte[HeaderSize];
            await ReadExactlyAsync(stream, header, timeout.Token);
            var payload = new byte[ValidateWorldHeader(header, sequence)];
            await ReadExactlyAsync(stream, payload, timeout.Token);
            return WorldReply.Parser.ParseFrom(payload);
        }

        /// <summary>关闭认证后的 TCP 连接。</summary>
        public void Dispose() => tcp.Dispose();
#endif
    }
}
