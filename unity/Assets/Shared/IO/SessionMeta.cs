using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;

namespace RackScanner.Shared.IO
{
    /// <summary>
    /// 세션 폴더의 session.json. 마커 정의, 사용한 마커 ID, 최종 변환(이동·회전·스케일), 프로토콜 버전.
    /// 최종 변환은 이 값만 바꿔 다시 적용한다. 점군(points.ply) 자체는 고치지 않는다.
    /// </summary>
    public sealed class SessionMeta
    {
        public string SessionId = "";
        public string DeviceInfo = "";
        public int ProtocolVersion = ProtocolConstants.Version;
        public string CreatedUtc = DateTime.UtcNow.ToString("o");
        public MarkerDefinition Marker = new MarkerDefinition();
        public string UsedMarkerId = "";
        public RigidTransform FinalTransform = RigidTransform.Identity;
        public int TotalChunks;
        public int PointCount;
        /// <summary>ARKit 메쉬 블록 수와 합친 원본 메쉬의 삼각형 수 (메쉬 수집을 껐으면 0).</summary>
        public int MeshBlockCount;
        public int MeshTriangleCount;

        public const string FileName = "session.json";
        public const string PointsFileName = "points.ply";
        public const string ChunksFileName = "chunks.bin";
        public const string AlignmentLogFileName = "alignment_log.csv";
        public const string ResultFileName = "result.json";
        /// <summary>메쉬 블록 원본: MESH_BLOCK 프레임을 이어 쓴 파일 (아이패드, 블록마다 마지막 판이 유효).</summary>
        public const string MeshFramesFileName = "mesh.bin";
        /// <summary>ARKit 메쉬 블록을 합친 원본 (OBJ, 마커 좌표). 점에 카메라 색이 있으면 정점 색 포함 (모양은 ARKit 그대로).</summary>
        public const string MeshRawFileName = "mesh_raw.obj";
        /// <summary>원본 메쉬를 깊이 점으로 다듬은 메쉬 (OBJ, 마커 좌표, 정점 색). 디테일 있는 곳만 촘촘해진다.</summary>
        public const string MeshRefinedFileName = "mesh_refined.obj";
        /// <summary>적응형 단순화한 메쉬 (OBJ, 마커 좌표, 정점 색). 넓은 면은 큰 삼각형, 복잡한 곳은 원래 밀도.</summary>
        public const string MeshFileName = "mesh.obj";

        public string ToJson()
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("sessionId", SessionId)
                .Prop("deviceInfo", DeviceInfo)
                .Prop("protocolVersion", ProtocolVersion)
                .Prop("createdUtc", CreatedUtc)
                .Prop("usedMarkerId", UsedMarkerId)
                .Prop("totalChunks", TotalChunks)
                .Prop("pointCount", PointCount)
                .Prop("meshBlockCount", MeshBlockCount)
                .Prop("meshTriangleCount", MeshTriangleCount);
            w.Key("marker").BeginObject()
                .Prop("id", Marker.Id)
                .Prop("printedWidth", Marker.PrintedWidth)
                .Prop("printedHeight", Marker.PrintedHeight);
            w.Key("position"); WriteVec(w, Marker.Position);
            w.Key("rotation"); WriteQuat(w, Marker.Rotation);
            w.EndObject();
            w.Key("finalTransform").BeginObject();
            w.Key("translation"); WriteVec(w, FinalTransform.Translation);
            w.Key("rotation"); WriteQuat(w, FinalTransform.Rotation);
            w.Prop("scale", FinalTransform.Scale);
            w.EndObject();
            w.EndObject();
            return w.ToString();
        }

        public static SessionMeta FromJson(string json)
        {
            var d = JsonParser.Parse(json) as Dictionary<string, object> ?? throw new FormatException("session.json is not an object");
            var m = new SessionMeta
            {
                SessionId = d.GetString("sessionId", ""),
                DeviceInfo = d.GetString("deviceInfo", ""),
                ProtocolVersion = (int)d.GetNumber("protocolVersion", 0),
                CreatedUtc = d.GetString("createdUtc", ""),
                UsedMarkerId = d.GetString("usedMarkerId", ""),
                TotalChunks = (int)d.GetNumber("totalChunks", 0),
                PointCount = (int)d.GetNumber("pointCount", 0),
                MeshBlockCount = (int)d.GetNumber("meshBlockCount", 0),
                MeshTriangleCount = (int)d.GetNumber("meshTriangleCount", 0),
            };
            var mk = d.GetObject("marker");
            if (mk != null)
            {
                m.Marker = new MarkerDefinition
                {
                    Id = mk.GetString("id", "base"),
                    PrintedWidth = (float)mk.GetNumber("printedWidth", 0.2),
                    PrintedHeight = (float)mk.GetNumber("printedHeight", 0.2),
                    Position = ReadVec(mk.GetArray("position"), Vector3.Zero),
                    Rotation = ReadQuat(mk.GetArray("rotation")),
                };
            }
            var ft = d.GetObject("finalTransform");
            if (ft != null)
                m.FinalTransform = new RigidTransform(ReadQuat(ft.GetArray("rotation")), ReadVec(ft.GetArray("translation"), Vector3.Zero), (float)ft.GetNumber("scale", 1));
            return m;
        }

        public void Save(string sessionDir) => File.WriteAllText(Path.Combine(sessionDir, FileName), ToJson());
        public static SessionMeta Load(string sessionDir) => FromJson(File.ReadAllText(Path.Combine(sessionDir, FileName)));

        static void WriteVec(JsonWriter w, Vector3 v) => w.NumArray(new double[] { v.X, v.Y, v.Z });
        static void WriteQuat(JsonWriter w, Quaternion q) => w.NumArray(new double[] { q.X, q.Y, q.Z, q.W });

        static Vector3 ReadVec(List<object> a, Vector3 fallback)
            => a != null && a.Count == 3 ? new Vector3(F(a[0]), F(a[1]), F(a[2])) : fallback;
        static Quaternion ReadQuat(List<object> a)
            => a != null && a.Count == 4 ? Quaternion.Normalize(new Quaternion(F(a[0]), F(a[1]), F(a[2]), F(a[3]))) : Quaternion.Identity;
        static float F(object o) => o is double d ? (float)d : 0f;

        /// <summary>세션 ID 기본 형식: yyyy-MM-dd_HHmmss (기기 로컬 시각).</summary>
        public static string NewSessionId(DateTime now) => now.ToString("yyyy-MM-dd_HHmmss");
    }

    /// <summary>alignment_log.csv 한 줄: 마커를 인식할 때마다 기록해, 중력만 써도 되는지 데이터로 판단한다.</summary>
    public struct AlignmentLogEntry
    {
        public double Time;
        public string MarkerId;
        public float Distance;
        public float ViewAngleDeg;
        public float PitchDeg;
        public float RollDeg;
        /// <summary>평균에 쓰인 프레임인지 (거리·각도 조건 통과).</summary>
        public bool Used;

        public const string CsvHeader = "time,markerId,distance,viewAngleDeg,pitchDeg,rollDeg,used";

        public string ToCsv() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F3},{1},{2:F4},{3:F2},{4:F2},{5:F2},{6}", Time, MarkerId, Distance, ViewAngleDeg, PitchDeg, RollDeg, Used ? 1 : 0);
    }
}
