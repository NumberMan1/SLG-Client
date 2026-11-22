#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Google.Protobuf;
using Slg.Protocol;
using UnityEngine;

namespace Slg.Networking
{
    public sealed partial class GatewayConnection
    {
        [DllImport("__Internal")] private static extern int SlgSocketOpen(string url);
        [DllImport("__Internal")] private static extern int SlgSocketState(int id);
        [DllImport("__Internal")] private static extern int SlgSocketSend(int id, byte[] data, int length);
        [DllImport("__Internal")] private static extern int SlgSocketReceive(int id, byte[] data, int capacity);
        [DllImport("__Internal")] private static extern void SlgSocketClose(int id);

        private int socketId;

        private GatewayConnection(int socketId, ConnectReply session)
        {
            this.socketId = socketId;
            Session = session;
        }

        /// <summary>通过 HTTP 获取票据，再通过同源 WebSocket 完成认证。调用方负责释放返回的连接。</summary>
        public static async Task<GatewayConnection> LoginAsync(string gatewayUrl, string account, string password)
        {
            var gateway = ParseGateway(gatewayUrl);
            var login = await RequestTicketAsync(gateway, account, password);
            if (string.IsNullOrEmpty(login.Ticket))
            {
                throw new GatewayException("网关返回的登录响应无效。");
            }
            // 保持网关的主机和端口，按页面协议选择 WS 或 WSS。
            var endpoint = new UriBuilder(new Uri(gateway, "/v1/session"))
            {
                Scheme = gateway.Scheme == "https" ? "wss" : "ws"
            };
            int id = SlgSocketOpen(endpoint.Uri.AbsoluteUri);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            try
            {
                // 每帧让出执行权，浏览器才能继续处理建连和消息回调。
                while (SlgSocketState(id) == 0)
                {
                    CheckDeadline(deadline);
                    await Task.Yield();
                }
                if (SlgSocketState(id) != 1)
                {
                    throw new GatewayException("无法连接网关，请检查地址和网络。");
                }
                var payload = new ConnectRequest { Ticket = login.Ticket, ProtocolVersion = ProtocolVersion }.ToByteArray();
                var frame = new byte[HeaderSize + payload.Length];
                if (payload.Length > MaxPayload)
                {
                    throw new GatewayException("连接请求的数据过大。");
                }
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)SessionMessageId.ConnectRequest);
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), ConnectSequence);
                Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
                if (SlgSocketSend(id, frame, frame.Length) != 1)
                {
                    throw new GatewayException("无法发送认证请求。");
                }
                // WebSocket 每条二进制消息应包含一整帧 Orbit 数据。
                var buffer = new byte[HeaderSize + MaxPayload];
                int size;
                while ((size = SlgSocketReceive(id, buffer, buffer.Length)) == 0)
                {
                    if (SlgSocketState(id) != 1)
                    {
                        throw new GatewayException("认证过程中连接已断开。");
                    }
                    CheckDeadline(deadline);
                    await Task.Yield();
                }
                if (size < HeaderSize || size != HeaderSize + BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2)) ||
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2)) != (ushort)SessionMessageId.ConnectReply ||
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)) != ConnectSequence ||
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2)) != 0)
                {
                    throw new GatewayException("网关返回的认证响应无效。");
                }
                var reply = ConnectReply.Parser.ParseFrom(buffer, HeaderSize, size - HeaderSize);
                ValidateSession(reply, account);
                return new GatewayConnection(id, reply);
            }
            catch
            {
                SlgSocketClose(id);
                throw;
            }
        }

        private static void CheckDeadline(double deadline)
        {
            if (Time.realtimeSinceStartupAsDouble >= deadline)
            {
                throw new GatewayException("连接认证超时，请重试。");
            }
        }

        private async Task<WorldReply> ExchangeWorldAsync(byte[] frame, uint sequence)
        {
            int id = socketId;
            if (SlgSocketSend(id, frame, frame.Length) != 1)
            {
                throw new GatewayException("无法发送世界请求。");
            }
            var buffer = new byte[HeaderSize + MaxPayload];
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            int size;
            // 浏览器每次返回一整条消息，待网关回包期间让出主线程。
            while ((size = SlgSocketReceive(id, buffer, buffer.Length)) == 0)
            {
                if (SlgSocketState(id) != 1)
                {
                    throw new GatewayException("等待世界响应时连接已断开。");
                }
                if (Time.realtimeSinceStartupAsDouble >= deadline)
                {
                    throw new GatewayException("世界请求超时，请重新登录。");
                }
                await Task.Yield();
            }
            if (size < HeaderSize || size != HeaderSize + ValidateWorldHeader(buffer, sequence))
            {
                throw new GatewayException("网关返回的世界数据不完整。");
            }
            return WorldReply.Parser.ParseFrom(buffer, HeaderSize, size - HeaderSize);
        }

        /// <summary>关闭 WebSocket 并释放 JavaScript 桥接层中的连接记录，可重复调用。</summary>
        public void Dispose()
        {
            if (socketId == 0) return;
            SlgSocketClose(socketId);
            socketId = 0;
        }
    }
}
#endif
