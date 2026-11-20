#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.IO;
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

        public static async Task<GatewayConnection> LoginAsync(string gatewayUrl, string account, string password)
        {
            var gateway = new Uri(gatewayUrl, UriKind.Absolute);
            if (gateway.Scheme != "http" && gateway.Scheme != "https")
            {
                throw new ArgumentException("Gateway URL must use HTTP or HTTPS.", nameof(gatewayUrl));
            }
            var login = await RequestTicketAsync(gateway, account, password);
            if (string.IsNullOrEmpty(login.Ticket))
            {
                throw new InvalidDataException("Gateway returned an invalid login reply.");
            }
            var endpoint = new UriBuilder(new Uri(gateway, "/v1/session"))
            {
                Scheme = gateway.Scheme == "https" ? "wss" : "ws"
            };
            int id = SlgSocketOpen(endpoint.Uri.AbsoluteUri);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            try
            {
                while (SlgSocketState(id) == 0)
                {
                    CheckDeadline(deadline);
                    await Task.Yield();
                }
                if (SlgSocketState(id) != 1)
                {
                    throw new IOException("Could not open the gateway session.");
                }
                var payload = new ConnectRequest { Ticket = login.Ticket, ProtocolVersion = ProtocolVersion }.ToByteArray();
                var frame = new byte[HeaderSize + payload.Length];
                if (payload.Length > MaxPayload)
                {
                    throw new InvalidDataException("Connect request exceeds the frame limit.");
                }
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)SessionMessageId.ConnectRequest);
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), ConnectSequence);
                Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
                if (SlgSocketSend(id, frame, frame.Length) != 1)
                {
                    throw new IOException("Could not send the connect request.");
                }
                var buffer = new byte[HeaderSize + MaxPayload];
                int size;
                while ((size = SlgSocketReceive(id, buffer, buffer.Length)) == 0)
                {
                    if (SlgSocketState(id) != 1)
                    {
                        throw new EndOfStreamException("Gateway closed the connection during authentication.");
                    }
                    CheckDeadline(deadline);
                    await Task.Yield();
                }
                if (size < HeaderSize || size != HeaderSize + BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2)) ||
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2)) != (ushort)SessionMessageId.ConnectReply ||
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)) != ConnectSequence ||
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2)) != 0)
                {
                    throw new InvalidDataException("Gateway returned an invalid connect frame.");
                }
                var reply = ConnectReply.Parser.ParseFrom(buffer, HeaderSize, size - HeaderSize);
                if (reply.Account != account || reply.SessionId == 0)
                {
                    throw new InvalidDataException("Gateway returned an invalid session.");
                }
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
                throw new TimeoutException("Gateway authentication timed out.");
            }
        }

        public async Task WaitForDisconnectAsync()
        {
            var buffer = new byte[HeaderSize + MaxPayload];
            while (socketId != 0 && SlgSocketState(socketId) == 1)
            {
                if (SlgSocketReceive(socketId, buffer, buffer.Length) != 0)
                {
                    throw new InvalidDataException("Gateway sent unexpected data after authentication.");
                }
                await Task.Yield();
            }
        }

        public void Dispose()
        {
            if (socketId == 0) return;
            SlgSocketClose(socketId);
            socketId = 0;
        }
    }
}
#endif
