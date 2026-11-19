using System;
using System.Numerics;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.Protocol
{
    /// <summary>모든 프로토콜 메시지의 공통 인터페이스.</summary>
    public interface IMessage
    {
        MessageType Type { get; }
        void Write(PayloadWriter w);
    }

    /// <summary>HELLO (iPad → PC): 프로토콜 버전, 세션 ID, 기기 정보.</summary>
    public sealed class HelloMessage : IMessage
    {
        public MessageType Type => MessageType.Hello;
        public ushort ProtocolVersion = ProtocolConstants.Version;
        public string SessionId = "";
        public string DeviceInfo = "";

        public void Write(PayloadWriter w)
        {
            w.WriteU16(ProtocolVersion);
            w.WriteString(SessionId);
            w.WriteString(DeviceInfo);
        }

        public static HelloMessage Read(ref PayloadReader r) => new HelloMessage
        {
            ProtocolVersion = r.ReadU16(),
            SessionId = r.ReadString(),
            DeviceInfo = r.ReadString(),
        };
    }

    /// <summary>MARKER_DEF (iPad → PC): 마커 ID, 인쇄 크기, Unity 위치·회전. 스캔 전에 보낸다.</summary>
    public sealed class MarkerDefMessage : IMessage
    {
        public MessageType Type => MessageType.MarkerDef;
        public MarkerDefinition Marker = new MarkerDefinition();

        public void Write(PayloadWriter w)
        {
            w.WriteString(Marker.Id);
            w.WriteF32(Marker.PrintedWidth);
            w.WriteF32(Marker.PrintedHeight);
            w.WriteVector3(Marker.Position);
            w.WriteQuaternion(Marker.Rotation);
        }

        public static MarkerDefMessage Read(ref PayloadReader r) => new MarkerDefMessage
        {
            Marker = new MarkerDefinition
            {
                Id = r.ReadString(),
                PrintedWidth = r.ReadF32(),
                PrintedHeight = r.ReadF32(),
                Position = r.ReadVector3(),
                Rotation = r.ReadQuaternion(),
            }
        };
    }

    /// <summary>ALIGNED (iPad → PC): 정렬 완료와 인식 당시 로그.</summary>
    public sealed class AlignedMessage : IMessage
    {
        public MessageType Type => MessageType.Aligned;
        public string MarkerId = "";
        /// <summary>카메라-마커 평균 거리 (m).</summary>
        public float Distance;
        /// <summary>마커 법선과 카메라 방향 사이 평균 각도 (도). 0 이면 정면.</summary>
        public float ViewAngleDeg;
        /// <summary>마커가 보고한 pitch (중력 대비, 도). 비교 로그용.</summary>
        public float PitchDeg;
        /// <summary>마커가 보고한 roll (중력 대비, 도). 비교 로그용.</summary>
        public float RollDeg;
        /// <summary>평균에 쓴 프레임 수.</summary>
        public ushort FrameCount;

        public void Write(PayloadWriter w)
        {
            w.WriteString(MarkerId);
            w.WriteF32(Distance);
            w.WriteF32(ViewAngleDeg);
            w.WriteF32(PitchDeg);
            w.WriteF32(RollDeg);
            w.WriteU16(FrameCount);
        }

        public static AlignedMessage Read(ref PayloadReader r) => new AlignedMessage
        {
            MarkerId = r.ReadString(),
            Distance = r.ReadF32(),
            ViewAngleDeg = r.ReadF32(),
            PitchDeg = r.ReadF32(),
            RollDeg = r.ReadF32(),
            FrameCount = r.ReadU16(),
        };
    }

    /// <summary>POINT_CHUNK (iPad → PC): 청크 번호, 키프레임 시각, 점 수, 점 배열.</summary>
    public sealed class PointChunkMessage : IMessage
    {
        public MessageType Type => MessageType.PointChunk;
        public uint ChunkIndex;
        /// <summary>키프레임 시각 (스캔 시작 후 초).</summary>
        public double KeyframeTime;
        public PointXYZRGB[] Points = Array.Empty<PointXYZRGB>();

        public void Write(PayloadWriter w)
        {
            w.WriteU32(ChunkIndex);
            w.WriteF64(KeyframeTime);
            w.WriteU32((uint)Points.Length);
            for (int i = 0; i < Points.Length; i++)
            {
                var p = Points[i];
                w.WriteF32(p.X); w.WriteF32(p.Y); w.WriteF32(p.Z);
                w.WriteU8(p.R); w.WriteU8(p.G); w.WriteU8(p.B);
            }
        }

        public static PointChunkMessage Read(ref PayloadReader r)
        {
            var m = new PointChunkMessage { ChunkIndex = r.ReadU32(), KeyframeTime = r.ReadF64() };
            uint count = r.ReadU32();
            if ((long)count * ProtocolConstants.BytesPerPoint > r.Remaining)
                throw new ProtocolException($"point count {count} exceeds payload");
            var pts = new PointXYZRGB[count];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i].X = r.ReadF32(); pts[i].Y = r.ReadF32(); pts[i].Z = r.ReadF32();
                pts[i].R = r.ReadU8(); pts[i].G = r.ReadU8(); pts[i].B = r.ReadU8();
            }
            m.Points = pts;
            return m;
        }
    }

    /// <summary>ACK (PC → iPad): 받은 청크 번호.</summary>
    public sealed class AckMessage : IMessage
    {
        public MessageType Type => MessageType.Ack;
        public uint ChunkIndex;

        public void Write(PayloadWriter w) { w.WriteU32(ChunkIndex); }
        public static AckMessage Read(ref PayloadReader r) => new AckMessage { ChunkIndex = r.ReadU32() };
    }

    /// <summary>STATE (양방향): 일시정지, 재개, 추적 상태 변화.</summary>
    public sealed class StateMessage : IMessage
    {
        public MessageType Type => MessageType.State;
        public ScanState State;

        public void Write(PayloadWriter w) { w.WriteU8((byte)State); }
        public static StateMessage Read(ref PayloadReader r) => new StateMessage { State = (ScanState)r.ReadU8() };
    }

    /// <summary>END_SCAN (iPad → PC): 촬영완료, 총 청크 수.</summary>
    public sealed class EndScanMessage : IMessage
    {
        public MessageType Type => MessageType.EndScan;
        public uint TotalChunks;

        public void Write(PayloadWriter w) { w.WriteU32(TotalChunks); }
        public static EndScanMessage Read(ref PayloadReader r) => new EndScanMessage { TotalChunks = r.ReadU32() };
    }
}
