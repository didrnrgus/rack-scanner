using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Protocol;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 정점 + 삼각형 인덱스 메쉬. 좌표는 마커 기준 Unity 좌표 (Y 위, 미터), 앞면은 Unity 규칙대로 시계 방향.
    /// (UnityEngine 비의존: Unity 의 Mesh 로 옮기는 일은 각 앱의 View 가 한다.)
    /// </summary>
    public sealed class IndexedMesh
    {
        public List<Vector3> Vertices = new List<Vector3>();
        public List<int> Indices = new List<int>();
        /// <summary>정점 색 (Vertices 와 같은 길이). 없으면 null. 위치를 바꾸는 처리(합치기·잡음 제거·단순화·다듬기)는 색을 버리므로 끝에 MeshColorizer 로 다시 입힌다.</summary>
        public List<Rgb24> Colors;

        public bool HasColors => Colors != null && Colors.Count == Vertices.Count;

        public int VertexCount => Vertices.Count;
        public int TriangleCount => Indices.Count / 3;

        public float SurfaceArea()
        {
            double a = 0;
            for (int i = 0; i + 2 < Indices.Count; i += 3)
            {
                var p = Vertices[Indices[i]];
                a += Vector3.Cross(Vertices[Indices[i + 1]] - p, Vertices[Indices[i + 2]] - p).Length() * 0.5;
            }
            return (float)a;
        }

        /// <summary>
        /// 여러 블록을 하나로 합친다. ARKit 블록은 경계 정점을 따로 가지므로 weld 거리 안의 정점을 하나로 붙여
        /// 블록 경계에서 메쉬가 이어지게 한다 (단순화가 블록을 넘어 넓은 면을 합칠 수 있도록). 넓이 0 인 삼각형은 버린다.
        /// </summary>
        /// <param name="weld">붙일 거리 (m). 0 이면 붙이지 않는다.</param>
        public static IndexedMesh Combine(IEnumerable<MeshBlockMessage> blocks, float weld = 0.001f)
        {
            var parts = new List<(IReadOnlyList<Vector3> v, IReadOnlyList<int> i)>();
            foreach (var b in blocks) if (!b.IsRemoval) parts.Add((b.Vertices, b.Indices));
            return Combine(parts, weld);
        }

        public static IndexedMesh Combine(IEnumerable<(IReadOnlyList<Vector3> v, IReadOnlyList<int> i)> parts, float weld = 0.001f)
        {
            var m = new IndexedMesh();
            var map = new Dictionary<(int, int, int), int>();
            float inv = weld > 0 ? 1f / weld : 0f;
            var remap = new List<int>();
            foreach (var (verts, idx) in parts)
            {
                remap.Clear();
                for (int i = 0; i < verts.Count; i++)
                {
                    var p = verts[i];
                    if (!IsFinite(p)) { remap.Add(-1); continue; }
                    if (weld <= 0) { remap.Add(m.Vertices.Count); m.Vertices.Add(p); continue; }
                    var key = ((int)MathF.Round(p.X * inv), (int)MathF.Round(p.Y * inv), (int)MathF.Round(p.Z * inv));
                    if (!map.TryGetValue(key, out int k)) { k = m.Vertices.Count; map[key] = k; m.Vertices.Add(p); }
                    remap.Add(k);
                }
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    int a = remap[idx[t]], b = remap[idx[t + 1]], c = remap[idx[t + 2]];
                    if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) continue;
                    m.Indices.Add(a); m.Indices.Add(b); m.Indices.Add(c);
                }
            }
            return m;
        }

        static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    }

    /// <summary>8비트 RGB 색.</summary>
    public struct Rgb24
    {
        public byte R, G, B;
        public Rgb24(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    }
}
