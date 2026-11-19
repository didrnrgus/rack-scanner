using System.Collections.Generic;
using System.Linq;
using RackScanner.Shared.IO;

namespace RackScanner.Analysis
{
    /// <summary>바닥 평면 y = A*x + B*z + C (Unity 마커 좌표).</summary>
    public struct FloorPlane
    {
        public float A, B, C;
        public int InlierCount;
        public bool Found;
        public float HeightAt(float x, float z) => A * x + B * z + C;
    }

    /// <summary>M3: 단순 물체(책상·모니터·상자)를 감싸는 방향 있는 박스.</summary>
    public sealed class ObjectBox
    {
        public string Id;
        public float CenterX, CenterY, CenterZ;
        /// <summary>Length = 박스 로컬 Z(주축) 길이, Width = 로컬 X, Height = Y.</summary>
        public float Length, Width, Height;
        public float YawDeg;
        public int PointCount;
    }

    /// <summary>
    /// 랙 하나의 분석 결과. pose 는 "1번 칸 쪽 끝, 앞면(로컬 X=0) 모서리의 바닥 점"이고
    /// yaw 는 랙 길이 방향(로컬 +Z)의 Unity yaw. 로컬 +X 가 깊이 방향.
    /// </summary>
    public sealed class RackResult
    {
        public string RackId;
        public int Row;
        public int IndexInRow;
        public string Mode;               // "spec"(A단계) 또는 "auto"(B단계)
        public int BayCount;
        public int LevelCount;
        public float BayWidth;
        public List<float> LevelHeights = new List<float>();
        public List<bool> LevelObserved = new List<bool>();
        public List<float> UprightPositions = new List<float>(); // 로컬 Z (m)
        public List<bool> UprightObserved = new List<bool>();
        public float Depth;
        public float Length;
        public float Height;
        public float PoseX, PoseY, PoseZ, YawDeg;
        public float Confidence;
    }

    public sealed class AnalysisResult
    {
        public const int FormatVersion = 1;
        public FloorPlane Floor;
        public int InputPointCount;
        public int FilteredPointCount;
        public List<ObjectBox> Objects = new List<ObjectBox>();
        public List<RackResult> Racks = new List<RackResult>();

        /// <summary>result.json 형식. 각 랙은 docs/DEV_PLAN.md 의 출력 예시 필드를 포함한다.</summary>
        public string ToJson()
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("formatVersion", FormatVersion)
                .Prop("inputPointCount", InputPointCount)
                .Prop("filteredPointCount", FilteredPointCount);
            w.Key("floor").BeginObject().Prop("found", Floor.Found).Prop("a", Floor.A).Prop("b", Floor.B).Prop("c", Floor.C).Prop("inliers", Floor.InlierCount).EndObject();
            w.Key("racks").BeginArray();
            foreach (var r in Racks)
            {
                w.BeginObject()
                    .Prop("rackId", r.RackId)
                    .Prop("row", r.Row)
                    .Prop("indexInRow", r.IndexInRow)
                    .Prop("mode", r.Mode)
                    .Prop("bayCount", r.BayCount)
                    .Prop("levelCount", r.LevelCount)
                    .Prop("bayWidth", r.BayWidth);
                w.Key("levelHeights").NumArray(r.LevelHeights.Select(x => (double)x));
                w.Key("levelObserved").BeginArray(); foreach (var b in r.LevelObserved) w.Bool(b); w.EndArray();
                w.Key("uprightPositions").NumArray(r.UprightPositions.Select(x => (double)x));
                w.Key("uprightObserved").BeginArray(); foreach (var b in r.UprightObserved) w.Bool(b); w.EndArray();
                w.Prop("depth", r.Depth).Prop("length", r.Length).Prop("height", r.Height);
                w.Key("pose").BeginObject().Prop("x", r.PoseX).Prop("y", r.PoseY).Prop("z", r.PoseZ).Prop("yaw", r.YawDeg).EndObject();
                w.Prop("confidence", r.Confidence);
                w.EndObject();
            }
            w.EndArray();
            w.Key("objects").BeginArray();
            foreach (var o in Objects)
            {
                w.BeginObject()
                    .Prop("id", o.Id)
                    .Prop("pointCount", o.PointCount);
                w.Key("center").NumArray(new double[] { o.CenterX, o.CenterY, o.CenterZ });
                w.Key("size").NumArray(new double[] { o.Width, o.Height, o.Length });
                w.Prop("yaw", o.YawDeg);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        /// <summary>사람이 읽는 요약: "N열 M번째 칸 K층" 형식의 정보.</summary>
        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"점 {InputPointCount} → 필터 후 {FilteredPointCount}, 바닥 {(Floor.Found ? "검출" : "미검출")}, 물체 {Objects.Count}개, 랙 {Racks.Count}개");
            foreach (var r in Racks)
                sb.AppendLine($"  {r.RackId}: {r.Row}열 (줄 안 {r.IndexInRow}번째) · 칸 {r.BayCount}개 × 폭 {r.BayWidth:F2}m · 층 {r.LevelCount}개 [{string.Join(", ", r.LevelHeights.Select(h => h.ToString("F2")))}] · 깊이 {r.Depth:F2}m · 신뢰도 {r.Confidence:F2} ({r.Mode})");
            return sb.ToString();
        }
    }
}
