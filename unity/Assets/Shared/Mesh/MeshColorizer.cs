using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 메쉬 정점에 카메라 색을 입힌다. 색은 컬러 점군(깊이 픽셀마다 같은 위치의 카메라 색을 받은 점, 마커 좌표)에서 가져온다:
    /// 정점 주변 반경 안의 점을 거리와 "정점 면에서 법선 방향으로 떨어진 정도" 로 가중 평균한다.
    /// 법선 방향 가중 덕분에 얇은 판의 반대쪽 면이나 뒤 벽의 색이 섞이지 않는다.
    /// 근처에 점이 없으면 반경을 두 배로 한 번 더 찾고, 그래도 없으면 회색(128)으로 둔다. (UnityEngine 비의존)
    /// </summary>
    public static class MeshColorizer
    {
        public static readonly Rgb24 Unknown = new Rgb24(128, 128, 128);

        /// <summary>mesh.Colors 를 채운다. 점에 색이 없으면(HasColor = false) 아무것도 하지 않고 false.</summary>
        /// <param name="radius">찾는 반경 (m)</param>
        /// <param name="normalSigma">법선 방향 거리 가중 (m)</param>
        public static bool Colorize(IndexedMesh mesh, PointGrid points, float radius = 0.01f, float normalSigma = 0.008f)
        {
            if (points == null || !points.HasColor) return false;
            var normals = MeshAdjacency.VertexNormals(mesh);
            var colors = new Rgb24[mesh.VertexCount];
            Parallel.For(0, mesh.VertexCount, () => new List<int>(64), (v, _, buf) =>
            {
                var p = mesh.Vertices[v];
                var c = Sample(points, p, normals[v], radius, normalSigma, buf);
                if (c == null) c = Sample(points, p, normals[v], radius * 2f, normalSigma * 2f, buf);
                colors[v] = c.HasValue ? ToRgb(c.Value) : Unknown;
                return buf;
            }, _ => { });
            mesh.Colors = new List<Rgb24>(colors);
            return true;
        }

        static Vector3? Sample(PointGrid g, Vector3 p, Vector3 n, float radius, float normalSigma, List<int> buf)
        {
            g.Query(p, radius, buf);
            if (buf.Count == 0) return null;
            float invS2 = 1f / (2f * (radius * 0.5f) * (radius * 0.5f));
            float invN2 = 1f / (2f * normalSigma * normalSigma);
            var sum = Vector3.Zero;
            float wsum = 0;
            bool hasNormal = n != Vector3.Zero;
            foreach (int i in buf)
            {
                var d = g.Positions[i] - p;
                float dn = hasNormal ? Vector3.Dot(d, n) : 0f;
                float w = MathF.Exp(-d.LengthSquared() * invS2 - dn * dn * invN2);
                sum += g.Colors[i] * w;
                wsum += w;
            }
            if (wsum < 1e-6f) return null;
            return sum / wsum;
        }

        static Rgb24 ToRgb(Vector3 c)
        {
            byte B(float x) => (byte)Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
            return new Rgb24(B(c.X), B(c.Y), B(c.Z));
        }
    }

    /// <summary>정점-면 연결과 법선 (다듬기·색 입히기 공용).</summary>
    public static class MeshAdjacency
    {
        /// <summary>넓이 가중 정점 법선 (단위 벡터, 연결된 면이 없으면 0). 방향(앞·뒤)은 메쉬 감김을 따른다.</summary>
        public static Vector3[] VertexNormals(IndexedMesh m)
        {
            var n = new Vector3[m.VertexCount];
            var idx = m.Indices;
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                var fn = Vector3.Cross(m.Vertices[b] - m.Vertices[a], m.Vertices[c] - m.Vertices[a]); // 길이 = 넓이 × 2
                n[a] += fn; n[b] += fn; n[c] += fn;
            }
            for (int i = 0; i < n.Length; i++)
            {
                float l = n[i].Length();
                n[i] = l > 1e-12f ? n[i] / l : Vector3.Zero;
            }
            return n;
        }

        /// <summary>정점마다 그 정점을 쓰는 삼각형 번호.</summary>
        public static List<int>[] VertexFaces(IndexedMesh m)
        {
            var vf = new List<int>[m.VertexCount];
            for (int t = 0; t < m.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = m.Indices[3 * t + k];
                    (vf[v] ?? (vf[v] = new List<int>(8))).Add(t);
                }
            return vf;
        }
    }
}
