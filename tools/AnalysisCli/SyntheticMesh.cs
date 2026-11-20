using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Mesh;

namespace RackScanner.Tools
{
    /// <summary>
    /// 메쉬 단순화 시험용 합성 장면: ARKit 비슷한 밀도(기본 2cm 격자)로 바닥·벽·철제 선반(기둥·빔)·상자를 만들고 약간의 잡음을 넣는다.
    /// 좌표는 마커 기준 Unity 좌표 (Y 위, 미터).
    /// </summary>
    public sealed class SyntheticMesh
    {
        readonly List<(IReadOnlyList<Vector3> v, IReadOnlyList<int> i)> _parts = new List<(IReadOnlyList<Vector3>, IReadOnlyList<int>)>();
        readonly float _step;

        public SyntheticMesh(float step = 0.02f) { _step = step; }

        /// <summary>격자 면. 앞면 법선 = Cross(u, v) 방향.</summary>
        public SyntheticMesh Face(Vector3 o, Vector3 u, Vector3 v)
        {
            int nu = Math.Max(1, (int)MathF.Ceiling(u.Length() / _step)), nv = Math.Max(1, (int)MathF.Ceiling(v.Length() / _step));
            var verts = new List<Vector3>();
            var idx = new List<int>();
            for (int j = 0; j <= nv; j++)
                for (int i = 0; i <= nu; i++)
                    verts.Add(o + u * (i / (float)nu) + v * (j / (float)nv));
            int W = nu + 1;
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    int a = j * W + i, b = a + 1, c = a + W, d = c + 1;
                    idx.AddRange(new[] { a, b, c, b, d, c });
                }
            _parts.Add((verts, idx));
            return this;
        }

        /// <summary>바깥이 앞면인 상자 (바닥면은 floor 위에 닿으므로 bottom=false 면 생략).</summary>
        public SyntheticMesh Box(Vector3 min, Vector3 max, bool bottom = true)
        {
            var s = max - min;
            var X = new Vector3(s.X, 0, 0); var Y = new Vector3(0, s.Y, 0); var Z = new Vector3(0, 0, s.Z);
            Face(new Vector3(min.X, max.Y, min.Z), Z, X);  // +Y
            if (bottom) Face(min, X, Z);                    // -Y
            Face(new Vector3(max.X, min.Y, min.Z), Y, Z);  // +X
            Face(min, Z, Y);                                // -X
            Face(new Vector3(min.X, min.Y, max.Z), X, Y);  // +Z
            Face(min, Y, X);                                // -Z
            return this;
        }

        /// <summary>바닥 (w×d), 뒤 벽, 철제 선반 1칸×levels 층, 선반 위 상자 몇 개.</summary>
        public static IndexedMesh Room(float noise, int seed = 1)
        {
            var m = new SyntheticMesh();
            m.Face(new Vector3(-1, 0, -1), new Vector3(0, 0, 4), new Vector3(5, 0, 0));        // 바닥 5×4m (+Y)
            m.Face(new Vector3(-1, 0, 3), new Vector3(0, 2.5f, 0), new Vector3(5, 0, 0));      // 뒤 벽 (−Z, 방 쪽을 봄)
            // 선반: 폭 1.2m, 깊이 0.5m, 기둥 4개(4cm 각), 층 4개(선반판 2cm 두께)
            float x0 = 0.5f, z0 = 2.3f, w = 1.2f, d = 0.5f, post = 0.04f;
            foreach (var (px, pz) in new[] { (x0, z0), (x0 + w - post, z0), (x0, z0 + d - post), (x0 + w - post, z0 + d - post) })
                m.Box(new Vector3(px, 0, pz), new Vector3(px + post, 1.8f, pz + post), bottom: false);
            for (int l = 0; l < 4; l++)
            {
                float y = 0.15f + l * 0.5f;
                m.Box(new Vector3(x0 + post, y, z0), new Vector3(x0 + w - post, y + 0.02f, z0 + d));
            }
            m.Box(new Vector3(0.6f, 0.17f, 2.35f), new Vector3(0.95f, 0.45f, 2.7f), bottom: false);
            m.Box(new Vector3(1.1f, 0.67f, 2.4f), new Vector3(1.6f, 0.9f, 2.75f), bottom: false);
            m.Box(new Vector3(2.5f, 0, 0.5f), new Vector3(3.1f, 0.75f, 1.2f), bottom: false);    // 책상 크기 상자

            var mesh = IndexedMesh.Combine(m._parts, 0.001f);
            if (noise > 0)
            {
                var rng = new Random(seed);
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    var v = mesh.Vertices[i];
                    mesh.Vertices[i] = v + new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * noise;
                }
            }
            return mesh;
        }
    }
}
