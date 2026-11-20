using System;
using System.Collections.Generic;
using System.Numerics;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 모서리를 지키는 메쉬 잡음 제거 (Sun et al. 2007 방식: 면 법선 양방향 필터 → 정점을 걸러진 면 평면에 맞춤).
    /// - 이웃 면 법선이 FeatureAngle 안이면 평균하고, 그보다 꺾이면 섞지 않는다 → 바닥·벽의 잔물결은 펴지고 기둥·빔의 모서리는 남는다.
    /// - 단순화 전에 쓴다: LiDAR 잡음이 작은 면을 몇 도씩 기울여 놓으면, 넓은 면도 "평평하지 않다" 고 판단돼 잘 합쳐지지 않기 때문.
    /// 위상(삼각형 연결)은 바꾸지 않고 정점 위치만 옮긴다. (UnityEngine 비의존)
    /// </summary>
    public static class MeshDenoiser
    {
        public static IndexedMesh Denoise(IndexedMesh src, int normalIterations = 5, int vertexIterations = 10, float featureAngleDeg = 30f)
        {
            int nv = src.VertexCount, nt = src.TriangleCount;
            var pos = src.Vertices.ToArray();
            var idx = src.Indices;
            var vf = new List<int>[nv];
            for (int t = 0; t < nt; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = idx[3 * t + k];
                    (vf[v] ?? (vf[v] = new List<int>(8))).Add(t);
                }

            var n = new Vector3[nt];
            var area = new float[nt];
            for (int t = 0; t < nt; t++)
            {
                var c = Vector3.Cross(pos[idx[3 * t + 1]] - pos[idx[3 * t]], pos[idx[3 * t + 2]] - pos[idx[3 * t]]);
                float l = c.Length();
                area[t] = l * 0.5f;
                n[t] = l > 1e-12f ? c / l : Vector3.Zero;
            }

            // 1) 면 법선 필터: 꼭짓점을 공유하는 이웃 면 중 꺾임이 작은 것만 넓이 가중 평균
            float cosLimit = MathF.Cos(featureAngleDeg * MathF.PI / 180f);
            var next = new Vector3[nt];
            var stamp = new int[nt];
            int gen = 0;
            for (int it = 0; it < normalIterations; it++)
            {
                for (int t = 0; t < nt; t++)
                {
                    if (n[t] == Vector3.Zero) { next[t] = Vector3.Zero; continue; }
                    gen++;
                    var sum = Vector3.Zero;
                    for (int k = 0; k < 3; k++)
                        foreach (int f in vf[idx[3 * t + k]])
                        {
                            if (stamp[f] == gen) continue;
                            stamp[f] = gen;
                            if (Vector3.Dot(n[t], n[f]) < cosLimit) continue;
                            sum += n[f] * area[f];
                        }
                    float l = sum.Length();
                    next[t] = l > 1e-12f ? sum / l : n[t];
                }
                var tmp = n; n = next; next = tmp;
            }

            // 2) 정점 갱신: 각 정점을 주변 면의 (걸러진 법선) 평면 쪽으로 옮긴다
            var delta = new Vector3[nv];
            for (int it = 0; it < vertexIterations; it++)
            {
                for (int v = 0; v < nv; v++)
                {
                    var faces = vf[v];
                    if (faces == null) { delta[v] = Vector3.Zero; continue; }
                    var d = Vector3.Zero;
                    int cnt = 0;
                    foreach (int f in faces)
                    {
                        if (n[f] == Vector3.Zero) continue;
                        var c = (pos[idx[3 * f]] + pos[idx[3 * f + 1]] + pos[idx[3 * f + 2]]) / 3f;
                        d += n[f] * Vector3.Dot(n[f], c - pos[v]);
                        cnt++;
                    }
                    delta[v] = cnt > 0 ? d / cnt : Vector3.Zero;
                }
                for (int v = 0; v < nv; v++) pos[v] += delta[v];
            }

            var m = new IndexedMesh();
            m.Vertices.AddRange(pos);
            m.Indices.AddRange(idx);
            return m;
        }
    }
}
