using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;

namespace RackScanner.Tools
{
    /// <summary>
    /// 메쉬 다듬기(MeshRefiner) 시험용 합성 장면. 같은 장면을
    /// - 기준: 잡음 없는 정확한 면 (거리 비교용)
    /// - 점: 4mm 간격 + 가우스 잡음 + 물체별 색 (아이패드 깊이 점 흉내)
    /// - ARKit 흉내 메쉬: 5cm 격자 → 라플라시안 평활(모서리·얇은 빔이 뭉개짐) → 잡음
    /// 으로 만든다. 좌표는 마커 기준 Unity 좌표 (Y 위, 미터).
    /// </summary>
    public static class SyntheticRefine
    {
        sealed class Obj
        {
            public Vector3 Min, Max; public bool Bottom; public Rgb24 Color; public bool Plane; public Vector3 U, V;
        }

        static List<Obj> Scene()
        {
            var s = new List<Obj>();
            void Box(Vector3 min, Vector3 max, Rgb24 c, bool bottom = false) => s.Add(new Obj { Min = min, Max = max, Color = c, Bottom = bottom });
            void Face(Vector3 o, Vector3 u, Vector3 v, Rgb24 c) => s.Add(new Obj { Min = o, U = u, V = v, Color = c, Plane = true });
            Face(new Vector3(-0.5f, 0, -0.5f), new Vector3(0, 0, 2.0f), new Vector3(2.5f, 0, 0), new Rgb24(110, 110, 110));      // 바닥 (회색)
            Face(new Vector3(-0.5f, 0, 1.5f), new Vector3(0, 2.0f, 0), new Vector3(2.5f, 0, 0), new Rgb24(200, 200, 190));      // 뒤 벽 (밝은 회색)
            var blue = new Rgb24(30, 60, 160); var orange = new Rgb24(230, 120, 20);
            Box(new Vector3(0.00f, 0, 1.00f), new Vector3(0.08f, 1.8f, 1.08f), blue);                                     // 기둥 8cm
            Box(new Vector3(1.42f, 0, 1.00f), new Vector3(1.50f, 1.8f, 1.08f), blue);
            Box(new Vector3(0.08f, 0.50f, 1.00f), new Vector3(1.42f, 0.60f, 1.05f), orange, true);                       // 빔 10×5cm
            Box(new Vector3(0.08f, 1.20f, 1.00f), new Vector3(1.42f, 1.30f, 1.05f), orange, true);
            Box(new Vector3(0.30f, 0, 0.30f), new Vector3(0.70f, 0.35f, 0.60f), new Rgb24(150, 110, 70));                // 상자
            Box(new Vector3(0.90f, 0, 0.20f), new Vector3(1.20f, 0.12f, 0.50f), new Rgb24(60, 140, 60));                 // 낮은 상자 12cm
            return s;
        }

        static SyntheticMesh Add(SyntheticMesh m, Obj o)
        {
            if (o.Plane) m.Face(o.Min, o.U, o.V); else m.Box(o.Min, o.Max, o.Bottom);
            return m;
        }

        public static IndexedMesh Reference()
        {
            var m = new SyntheticMesh(0.02f);
            foreach (var o in Scene()) Add(m, o);
            return m.Build();
        }

        public static List<PointXYZRGB> Points(float spacing = 0.004f, float noise = 0.003f, int seed = 1)
        {
            var rng = new Random(seed);
            var pts = new List<PointXYZRGB>();
            foreach (var o in Scene())
            {
                var part = Add(new SyntheticMesh(spacing), o).Build(0);
                foreach (var v in part.Vertices)
                {
                    var j = new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)) * noise;
                    pts.Add(new PointXYZRGB(v + j, o.Color.R, o.Color.G, o.Color.B));
                }
            }
            return pts;
        }

        /// <summary>ARKit 흉내: 거친 격자 + 평활 + 잡음.</summary>
        public static IndexedMesh ArkitLike(float step = 0.05f, int smooth = 2, float noise = 0.002f, int seed = 2)
        {
            var sm = new SyntheticMesh(step);
            foreach (var o in Scene()) Add(sm, o);
            var m = sm.Build(0.001f);
            var vf = MeshAdjacency.VertexFaces(m);
            var nb = new HashSet<int>[m.VertexCount];
            for (int t = 0; t < m.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = m.Indices[3 * t + k], b = m.Indices[3 * t + (k + 1) % 3];
                    (nb[a] ?? (nb[a] = new HashSet<int>())).Add(b);
                    (nb[b] ?? (nb[b] = new HashSet<int>())).Add(a);
                }
            for (int it = 0; it < smooth; it++)
            {
                var next = m.Vertices.ToArray();
                for (int v = 0; v < m.VertexCount; v++)
                {
                    if (nb[v] == null) continue;
                    var c = Vector3.Zero;
                    foreach (int u in nb[v]) c += m.Vertices[u];
                    c /= nb[v].Count;
                    next[v] = m.Vertices[v] + (c - m.Vertices[v]) * 0.5f;
                }
                m.Vertices = new List<Vector3>(next);
            }
            var rng = new Random(seed);
            for (int v = 0; v < m.VertexCount; v++) m.Vertices[v] += new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)) * noise;
            return m;
        }

        /// <summary>점 p 의 정답 색 (가장 가까운 물체).</summary>
        public static Rgb24 TrueColor(Vector3 p)
        {
            float best = float.MaxValue; Rgb24 c = default;
            foreach (var o in Scene())
            {
                float d;
                if (o.Plane)
                {
                    var n = Vector3.Normalize(Vector3.Cross(o.U, o.V));
                    d = MathF.Abs(Vector3.Dot(p - o.Min, n));
                    var q = p - o.Min; float u = Vector3.Dot(q, Vector3.Normalize(o.U)), w = Vector3.Dot(q, Vector3.Normalize(o.V));
                    if (u < 0 || u > o.U.Length() || w < 0 || w > o.V.Length()) d += 1f;
                }
                else
                {
                    var cl = Vector3.Clamp(p, o.Min, o.Max);
                    d = Vector3.Distance(p, cl);
                    if (d == 0) d = MathF.Min(MathF.Min(MathF.Min(p.X - o.Min.X, o.Max.X - p.X), MathF.Min(p.Y - o.Min.Y, o.Max.Y - p.Y)), MathF.Min(p.Z - o.Min.Z, o.Max.Z - p.Z));
                }
                if (d < best) { best = d; c = o.Color; }
            }
            return c;
        }

        static float Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
    }
}
