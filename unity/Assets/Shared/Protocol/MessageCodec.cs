using System;
using System.IO;

namespace RackScanner.Shared.Protocol
{
    /// <summary>헤더와 페이로드를 묶은 수신 프레임.</summary>
    public readonly struct Frame
    {
        public readonly MessageHeader Header;
        public readonly byte[] Payload;

        public Frame(MessageHeader header, byte[] payload) { Header = header; Payload = payload; }
    }

    /// <summary>메시지 ↔ 길이 접두 바이너리 프레임 변환. 양쪽 앱이 이 코드 하나를 공유한다.</summary>
    public static class MessageCodec
    {
        /// <summary>메시지를 헤더 포함 프레임 바이트로 인코딩한다.</summary>
        public static byte[] Encode(IMessage msg)
        {
            var w = new PayloadWriter(msg is PointChunkMessage pc ? 32 + pc.Points.Length * ProtocolConstants.BytesPerPoint : 128);
            msg.Write(w);
            if (w.Length > ProtocolConstants.MaxPayloadLength) throw new ProtocolException("message too large");
            var frame = new byte[ProtocolConstants.HeaderSize + w.Length];
            new MessageHeader(msg.Type, w.Length).WriteTo(frame);
            w.AsSpan().CopyTo(new Span<byte>(frame, ProtocolConstants.HeaderSize, w.Length));
            return frame;
        }

        /// <summary>프레임을 메시지로 디코딩한다. 버전이 다른 프레임은 거절한다.</summary>
        public static IMessage Decode(in Frame frame)
        {
            if (frame.Header.Version != ProtocolConstants.Version)
                throw new ProtocolException($"protocol version mismatch: got {frame.Header.Version}, expected {ProtocolConstants.Version}");
            var r = new PayloadReader(frame.Payload);
            IMessage m;
            switch (frame.Header.Type)
            {
                case MessageType.Hello: m = HelloMessage.Read(ref r); break;
                case MessageType.MarkerDef: m = MarkerDefMessage.Read(ref r); break;
                case MessageType.Aligned: m = AlignedMessage.Read(ref r); break;
                case MessageType.PointChunk: m = PointChunkMessage.Read(ref r); break;
                case MessageType.Ack: m = AckMessage.Read(ref r); break;
                case MessageType.State: m = StateMessage.Read(ref r); break;
                case MessageType.EndScan: m = EndScanMessage.Read(ref r); break;
                default: throw new ProtocolException($"unknown message type {(ushort)frame.Header.Type}");
            }
            r.EnsureEnd();
            return m;
        }

        /// <summary>
        /// 스트림에서 프레임 하나를 블로킹으로 읽는다. 스트림이 정상적으로 끝나면 false.
        /// 헤더 도중에 끊기면 <see cref="EndOfStreamException"/>.
        /// </summary>
        public static bool TryReadFrame(Stream s, byte[] headerBuf, out Frame frame)
        {
            frame = default;
            if (!ReadExactly(s, headerBuf, 0, ProtocolConstants.HeaderSize, allowCleanEof: true)) return false;
            var header = MessageHeader.Read(headerBuf);
            var payload = new byte[header.Length];
            ReadExactly(s, payload, 0, payload.Length, allowCleanEof: false);
            frame = new Frame(header, payload);
            return true;
        }

        static bool ReadExactly(Stream s, byte[] buf, int off, int count, bool allowCleanEof)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, off + read, count - read);
                if (n <= 0)
                {
                    if (read == 0 && allowCleanEof) return false;
                    throw new EndOfStreamException("stream ended mid-frame");
                }
                read += n;
            }
            return true;
        }
    }
}
