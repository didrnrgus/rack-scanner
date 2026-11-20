using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Mesh;

namespace RackScanner.Tools
{
    /// <summary>
    /// 두 메쉬 표면 사이 거리 (정점 → 상대 표면 최단 거리). 양방향으로 재면 대칭 Hausdorff:
    /// - 단순화 → 기준: 단순화가 표면에서 얼마나 벗어났나
    /// - 기준 → 단순화: 기둥·빔 같은 형상이 사라졌나 (사라지면 이쪽이 커진다)
    /// 삼각형을 격자 칸에 나눠 담아 가까운 칸만 찾는다.
    /// </summary>
    public sealed class MeshCompare
    {
        readonly IndexedMesh _m;
        readonly float _cell;
        readonly Dictionary<(int, int, int), List<int>> _grid = new Dictionary<(int, int, int), List<int>>();

        public MeshCompare(IndexedMesh target, float cell = 0.05f)
        {
            _m = target; _cell = cell;
            for (int t = 0; t < target.TriangleCount; t++)
            {
                var a = V(t, 0); var b = V(t, 1); var c = V(t, 2);
                var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
                var l = Cell(lo); var h = Cell(hi);
                for (int x = l.Item1; x <= h.Item1; x++)
                    for (int y = l.Item2; y <= h.Item2; y++)
                        for (int z = l.Item3; z <= h.Item3; z++)
                        {
                            if (!_grid.TryGetValue((x, y, z), out var list)) _grid[(x, y, z)] = list = new List<int>();
                            list.Add(t);
                        }
            }
        }

        Vector3 V(int t, int k) => _m.Vertices[_m.Indices[3 * t + k]];
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

        /// <summary>표면까지 최단 거리. maxSearch 안에 표면이 없으면 maxSearch.</summary>
        public float Distance(Vector3 p, float maxSearch = 0.2f)
        {
            float best = maxSearch;
            var c = Cell(p);
            int r = (int)MathF.Ceiling(maxSearch / _cell);
            for (int ring = 0; ring <= r; ring++)
            {
                if ((ring - 1) * _cell > best) break; // 이 고리는 이미 찾은 것보다 멀다
                for (int x = -ring; x <= ring; x++)
                    for (int y = -ring; y <= ring; y++)
                        for (int z = -ring; z <= ring; z++)
                        {
                            if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != ring) continue;
                            if (!_grid.TryGetValue((c.Item1 + x, c.Item2 + y, c.Item3 + z), out var list)) continue;
                            foreach (int t in list) best = MathF.Min(best, PointTriangle(p, V(t, 0), V(t, 1), V(t, 2)));
                        }
            }
            return best;
        }

        public static (float mean, float p95, float max) Stats(IndexedMesh from, MeshCompare to)
        {
            var d = new List<float>(from.VertexCount);
            double sum = 0;
            foreach (var v in from.Vertices) { float x = to.Distance(v); d.Add(x); sum += x; }
            if (d.Count == 0) return (0, 0, 0);
            d.Sort();
            return ((float)(sum / d.Count), d[(int)(d.Count * 0.95)], d[d.Count - 1]);
        }

        /// <summary>점-삼각형 최단 거리 (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        static float PointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
            var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
            var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
            float denom = 1f / (va + vb + vc);
            return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
        }
    }
}
