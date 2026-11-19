using System;
using System.IO;
using System.Numerics;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using Xunit;

namespace RackScanner.Tests
{
    public class ProtocolTests
    {
        static T RoundTrip<T>(T msg) where T : IMessage
        {
            var bytes = MessageCodec.Encode(msg);
            using var ms = new MemoryStream(bytes);
            Assert.True(MessageCodec.TryReadFrame(ms, new byte[ProtocolConstants.HeaderSize], out var frame));
            Assert.Equal(msg.Type, frame.Header.Type);
            Assert.Equal(ProtocolConstants.Version, frame.Header.Version);
            return Assert.IsType<T>(MessageCodec.Decode(frame));
        }

        [Fact]
        public void HeaderLayoutIsMagicVersionTypeLength()
        {
            var bytes = MessageCodec.Encode(new AckMessage { ChunkIndex = 7 });
            Assert.Equal(new byte[] { (byte)'R', (byte)'K', (byte)'S', (byte)'C' }, bytes[..4]);
            Assert.Equal(ProtocolConstants.Version, BitConverter.ToUInt16(bytes, 4));
            Assert.Equal((ushort)MessageType.Ack, BitConverter.ToUInt16(bytes, 6));
            Assert.Equal(4u, BitConverter.ToUInt32(bytes, 8));
            Assert.Equal(16, bytes.Length);
        }

        [Fact]
        public void AllMessagesRoundTrip()
        {
            var h = RoundTrip(new HelloMessage { SessionId = "2026-11-18_001", DeviceInfo = "iPad 한글" });
            Assert.Equal("2026-11-18_001", h.SessionId);
            Assert.Equal("iPad 한글", h.DeviceInfo);
            Assert.Equal(ProtocolConstants.Version, h.ProtocolVersion);

            var md = RoundTrip(new MarkerDefMessage { Marker = new MarkerDefinition { Id = "base", PrintedWidth = 0.197f, PrintedHeight = 0.2f, Position = new Vector3(1, 2, 3), Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0, 0) } });
            Assert.Equal("base", md.Marker.Id);
            Assert.Equal(0.197f, md.Marker.PrintedWidth);
            Assert.Equal(new Vector3(1, 2, 3), md.Marker.Position);
            Assert.Equal(Quaternion.CreateFromYawPitchRoll(0.3f, 0, 0), md.Marker.Rotation);

            var al = RoundTrip(new AlignedMessage { MarkerId = "base", Distance = 0.8f, ViewAngleDeg = 12, PitchDeg = 0.5f, RollDeg = -0.3f, FrameCount = 30 });
            Assert.Equal(30, al.FrameCount);
            Assert.Equal(-0.3f, al.RollDeg);

            var pts = new PointXYZRGB[1000];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointXYZRGB(i * 0.01f, -i, i * 2, (byte)i, (byte)(i >> 1), 3);
            var pc = RoundTrip(new PointChunkMessage { ChunkIndex = 42, KeyframeTime = 12.5, Points = pts });
            Assert.Equal(42u, pc.ChunkIndex);
            Assert.Equal(12.5, pc.KeyframeTime);
            Assert.Equal(pts, pc.Points);

            Assert.Equal(9u, RoundTrip(new AckMessage { ChunkIndex = 9 }).ChunkIndex);
            Assert.Equal(ScanState.TrackingLost, RoundTrip(new StateMessage { State = ScanState.TrackingLost }).State);
            Assert.Equal(17u, RoundTrip(new EndScanMessage { TotalChunks = 17 }).TotalChunks);
        }

        [Fact]
        public void PointChunkSizeIs15BytesPerPoint()
        {
            var bytes = MessageCodec.Encode(new PointChunkMessage { Points = new PointXYZRGB[100] });
            Assert.Equal(ProtocolConstants.HeaderSize + 4 + 8 + 4 + 100 * 15, bytes.Length);
        }

        [Fact]
        public void VersionMismatchIsRejected()
        {
            var bytes = MessageCodec.Encode(new HelloMessage());
            bytes[4] = (byte)(ProtocolConstants.Version + 1);
            using var ms = new MemoryStream(bytes);
            MessageCodec.TryReadFrame(ms, new byte[12], out var frame);
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(frame));
        }

        [Fact]
        public void BadMagicAndOversizeAreRejected()
        {
            var bytes = MessageCodec.Encode(new AckMessage());
            bytes[0] = 0;
            Assert.Throws<ProtocolException>(() => MessageCodec.TryReadFrame(new MemoryStream(bytes), new byte[12], out _));

            bytes = MessageCodec.Encode(new AckMessage());
            BitConverter.TryWriteBytes(bytes.AsSpan(8), (uint)ProtocolConstants.MaxPayloadLength + 1);
            Assert.Throws<ProtocolException>(() => MessageCodec.TryReadFrame(new MemoryStream(bytes), new byte[12], out _));
        }

        [Fact]
        public void TruncatedPayloadThrows()
        {
            var bytes = MessageCodec.Encode(new PointChunkMessage { Points = new PointXYZRGB[10] });
            Assert.Throws<EndOfStreamException>(() => MessageCodec.TryReadFrame(new MemoryStream(bytes, 0, bytes.Length - 3), new byte[12], out _));
            Assert.False(MessageCodec.TryReadFrame(new MemoryStream(Array.Empty<byte>()), new byte[12], out _));
        }

        [Fact]
        public void LyingPointCountIsRejected()
        {
            var bytes = MessageCodec.Encode(new PointChunkMessage { Points = new PointXYZRGB[2] });
            BitConverter.TryWriteBytes(bytes.AsSpan(12 + 12), 1000u);
            MessageCodec.TryReadFrame(new MemoryStream(bytes), new byte[12], out var frame);
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(frame));
        }
    }
}
