using System;
using System.Buffers.Binary;

namespace RackScanner.Shared.Protocol
{
    /// <summary>모든 메시지 앞에 붙는 12바이트 헤더. 모든 정수는 리틀 엔디언.</summary>
    public struct MessageHeader
    {
        public uint Magic;
        public ushort Version;
        public MessageType Type;
        public uint Length;

        public MessageHeader(MessageType type, int length)
        {
            Magic = ProtocolConstants.Magic;
            Version = ProtocolConstants.Version;
            Type = type;
            Length = (uint)length;
        }

        public void WriteTo(Span<byte> dst)
        {
            if (dst.Length < ProtocolConstants.HeaderSize) throw new ArgumentException("header buffer too small");
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), (ushort)Type);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), Length);
        }

        /// <summary>헤더를 읽고 magic 과 길이를 검사한다. 버전 검사는 호출 측(HELLO 처리) 책임.</summary>
        public static MessageHeader Read(ReadOnlySpan<byte> src)
        {
            if (src.Length < ProtocolConstants.HeaderSize) throw new ProtocolException("header truncated");
            var h = new MessageHeader
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4)),
                Version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2)),
                Type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(6, 2)),
                Length = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4)),
            };
            if (h.Magic != ProtocolConstants.Magic)
                throw new ProtocolException($"bad magic 0x{h.Magic:X8}");
            if (h.Length > ProtocolConstants.MaxPayloadLength)
                throw new ProtocolException($"payload too large ({h.Length} bytes)");
            return h;
        }
    }
}
