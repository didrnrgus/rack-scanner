using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace RackScanner.Shared.Mesh
{
    public sealed class RefineOptions
    {
        /// <summary>점 격자를 만들 때 줄일 복셀 (m). 복셀 안 평균이라 잡음도 줄어든다.</summary>
        public float PointVoxel = 0.004f;
        /// <summary>정점 주변에서 점을 모으는 옆 방향 반경 (m).</summary>
        public float Radius = 0.015f;
        /// <summary>법선 방향 커널 (m). 이보다 훨씬 떨어진 면(뒤 벽, 판의 반대쪽)의 점은 거의 섞이지 않는다.</summary>
        public float NormalSigma = 0.008f;
        /// <summary>정점이 원래 자리에서 법선 방향으로 움직일 수 있는 최대 거리 (m). 처음 찾는 범위이기도 하다.</summary>
        public float MaxOffset = 0.03f;
        public int ShiftIterations = 5;
        /// <summary>가중치 합이 이보다 작으면(점이 거의 없으면) 정점을 그대로 둔다.</summary>
        public float MinSupport = 3f;
        /// <summary>디테일이 있는 곳만 삼각형을 나누는 횟수. 한 번에 변 길이가 반이 된다.</summary>
        public int SubdivideLevels = 2;
        /// <summary>나눈 뒤 변 길이가 이보다 짧아지면 더 나누지 않는다 (m).</summary>
        public float MinEdge = 0.01f;
        /// <summary>삼각형 안 점들이 삼각형 면에서 이만큼(RMS, m) 넘게 벗어나야 나눈다. 실제 기준은 max(이 값, NoiseFactor × 추정 잡음).</summary>
        public float DetailTolerance = 0.003f;
        public float NoiseFactor = 2.5f;
        /// <summary>삼각형 수가 원래의 이 배수를 넘지 않게 한다.</summary>
        public float MaxTriangleGrowth = 4f;
        /// <summary>카메라 색을 가중치에 쓴다 (색이 비슷한 점끼리 → 색이 다른 물체 경계에서 섞이지 않게). 점에 색이 없으면 무시.</summary>
        public bool UseColor = true;
        /// <summary>색 차이 커널 (RGB 0..1 거리).</summary>
        public float ColorSigma = 0.15f;
    }

    public sealed class RefineStats
    {
        /// <summary>Unsupported: 원래 정점 중 움직이지 않은 수 (근처에 점이 없거나 뒤집힘 방지로 취소).</summary>
        public int SourceTriangles, Triangles, Vertices, Moved, Unsupported, SplitRequested;
        public float MeanOffset, NoiseRms, Tolerance;
        public bool UsedColor;
        public override string ToString() =>
            $"triangles {SourceTriangles:N0} -> {Triangles:N0}, vertices moved {Moved:N0} (mean {MeanOffset * 1000:F1} mm, unchanged {Unsupported:N0}), " +
            $"noise {NoiseRms * 1000:F1} mm, split tolerance {Tolerance * 1000:F1} mm, color {(UsedColor ? "on" : "off")}";
    }

    /// <summary>
    /// ARKit 메쉬를 깊이 점(LiDAR + 카메라 융합 깊이, 컬러)으로 다듬는다. ARKit 메쉬는 해상도가 낮고 모서리·얇은 빔이 뭉개지는데,
    /// 같은 장면을 여러 프레임에서 찍은 점은 그보다 촘촘하다.
    /// 1) 맞추기: 정점마다 법선 방향으로 주변 점의 분포를 따라가(mean-shift) 가장 가까운 실제 면으로 옮긴다.
    ///    옆 반경·법선 거리·(있으면) 색 차이로 가중한다. 색 가중은 깊이가 비슷해도 색이 다른 물체(주황 빔과 회색 벽 등)의 점이 섞이지 않게 한다.
    ///    면이 뒤집히는 이동은 줄이거나 취소한다.
    /// 2) 나누기: 삼각형 안의 점이 삼각형 면에서 잡음보다 크게 벗어나는 곳(굴곡·모서리·작은 물체)만 1→4 로 나누고(이웃은 이음매가 맞게 1→2, 1→3) 다시 맞춘다.
    ///    평평한 곳은 그대로 둔다. MinEdge 까지, 삼각형 수는 MaxTriangleGrowth 배까지.
    /// 결과에는 색이 없다 (끝에 MeshColorizer). (UnityEngine 비의존)
    /// </summary>
    public static class MeshRefiner
    {
        public static IndexedMesh Refine(IndexedMesh src, PointGrid points, RefineOptions o, out RefineStats stats)
        {
            o = o ?? new RefineOptions();
            stats = new RefineStats { SourceTriangles = src.TriangleCount, UsedColor = o.UseColor && points.HasColor };
            var m = new IndexedMesh();
            m.Vertices.AddRange(src.Vertices);
            m.Indices.AddRange(src.Indices);
            if (points.Count == 0 || m.TriangleCount == 0) { stats.Triangles = m.TriangleCount; stats.Vertices = m.VertexCount; return m; }

            var orig = m.Vertices.ToArray();
            Project(m, points, o, stats.UsedColor);
            int maxTris = (int)Math.Min(int.MaxValue, src.TriangleCount * (double)Math.Max(1f, o.MaxTriangleGrowth));
            for (int level = 0; level < o.SubdivideLevels; level++)
            {
                var split = SelectTriangles(m, points, o, stats, maxTris);
                if (split.Count == 0) break;
                stats.SplitRequested += split.Count;
                Subdivide(m, split, 2f * o.MinEdge);
                Project(m, points, o, stats.UsedColor);
            }

            // 통계: 원래 정점이 얼마나 움직였나 (나누며 생긴 정점은 제외)
            double sum = 0; int moved = 0, unsupported = 0;
            for (int i = 0; i < orig.Length; i++)
            {
                float d = Vector3.Distance(orig[i], m.Vertices[i]);
                if (d > 1e-5f) { moved++; sum += d; } else unsupported++;
            }
            stats.Moved = moved; stats.Unsupported = unsupported;
            stats.MeanOffset = moved > 0 ? (float)(sum / moved) : 0f;
            stats.Triangles = m.TriangleCount; stats.Vertices = m.VertexCount;
            return m;
        }

        // ---------- 1) 맞추기 ----------

        static void Project(IndexedMesh m, PointGrid g, RefineOptions o, bool useColor)
        {
            int nv = m.VertexCount;
            var normals = MeshAdjacency.VertexNormals(m);
            var offset = new float[nv];
            var pos = m.Vertices;
            Parallel.For(0, nv, () => new List<int>(128), (v, _, buf) =>
            {
                offset[v] = Shift(g, pos[v], normals[v], o, useColor, buf);
                return buf;
            }, _ => { });

            // 적용: 이웃 면이 뒤집히면 이동을 반씩 줄이고, 세 번 안에 안 되면 그대로 둔다 (차례로 적용해 이미 옮긴 이웃 기준으로 검사)
            var vf = MeshAdjacency.VertexFaces(m);
            for (int v = 0; v < nv; v++)
            {
                float t = offset[v];
                if (t == 0f || vf[v] == null) continue;
                var p0 = pos[v];
                for (int attempt = 0; attempt < 3; attempt++, t *= 0.5f)
                {
                    var p1 = p0 + normals[v] * t;
                    if (!Flips(m, vf[v], v, p1)) { pos[v] = p1; break; }
                }
            }
        }

        /// <summary>정점 p 를 법선 n 방향으로 얼마나 옮길지 (m). 점이 부족하면 0.</summary>
        static float Shift(PointGrid g, Vector3 p0, Vector3 n, RefineOptions o, bool useColor, List<int> buf)
        {
            if (n == Vector3.Zero) return 0f;
            float R = o.Radius, R2 = R * R;
            float invT2 = 1f / (2f * (R * 0.5f) * (R * 0.5f));
            float invC2 = 1f / (2f * o.ColorSigma * o.ColorSigma);
            float invNarrow2 = 1f / (2f * o.NormalSigma * o.NormalSigma);
            float t = 0f;
            Vector3 cref = default;
            bool hasRef = false;
            int iters = Math.Max(1, o.ShiftIterations);
            for (int it = 0; it < iters; it++)
            {
                // 커널을 넓게 시작해(먼 곳의 면도 찾게) 점점 좁힌다
                float f = iters == 1 ? 1f : it / (float)(iters - 1);
                float sigma = o.MaxOffset * 0.5f + (o.NormalSigma - o.MaxOffset * 0.5f) * f;
                float invN2 = 1f / (2f * sigma * sigma);
                var p = p0 + n * t;
                g.Query(p, MathF.Sqrt(R2 + 6.25f * sigma * sigma), buf);
                double sw = 0, sd = 0, swc = 0;
                Vector3 sc = Vector3.Zero;
                foreach (int i in buf)
                {
                    var d = g.Positions[i] - p;
                    float dn = Vector3.Dot(d, n);
                    float dt2 = d.LengthSquared() - dn * dn;
                    if (dt2 > R2) continue;
                    float ws = MathF.Exp(-dt2 * invT2);
                    float w = ws * MathF.Exp(-dn * dn * invN2);
                    if (useColor)
                    {
                        if (hasRef) w *= MathF.Exp(-Vector3.DistanceSquared(g.Colors[i], cref) * invC2);
                        // 기준 색: 지금 위치 바로 근처(좁은 커널)의 색
                        float wc = ws * MathF.Exp(-dn * dn * invNarrow2);
                        if (hasRef) wc *= MathF.Exp(-Vector3.DistanceSquared(g.Colors[i], cref) * invC2);
                        sc += g.Colors[i] * wc; swc += wc;
                    }
                    sw += w; sd += w * dn;
                }
                if (sw < o.MinSupport) break;
                t += (float)(sd / sw);
                t = Math.Clamp(t, -o.MaxOffset, o.MaxOffset);
                if (useColor && swc > 1e-6) { cref = sc / (float)swc; hasRef = true; }
            }
            return t;
        }

        static bool Flips(IndexedMesh m, List<int> faces, int v, Vector3 p1)
        {
            var idx = m.Indices; var pos = m.Vertices;
            foreach (int f in faces)
            {
                int a = idx[3 * f], b = idx[3 * f + 1], c = idx[3 * f + 2];
                var pa = pos[a]; var pb = pos[b]; var pc = pos[c];
                var before = Vector3.Cross(pb - pa, pc - pa);
                if (a == v) pa = p1; else if (b == v) pb = p1; else pc = p1;
                var after = Vector3.Cross(pb - pa, pc - pa);
                float lb = before.Length(), la = after.Length();
                if (lb < 1e-12f) continue;
                if (la < 1e-12f || Vector3.Dot(before, after) < 0.2f * lb * la) return true; // 78° 넘게 돌아가면 뒤집힘으로 본다
            }
            return false;
        }

        // ---------- 2) 나누기 ----------

        /// <summary>점이 삼각형 면에서 잡음보다 크게 벗어나는 삼각형 (벗어난 정도 큰 순, 예산 안에서).</summary>
        static List<int> SelectTriangles(IndexedMesh m, PointGrid g, RefineOptions o, RefineStats stats, int maxTris)
        {
            int nt = m.TriangleCount;
            var rms = new float[nt];
            var longest = new float[nt];
            var idx = m.Indices; var pos = m.Vertices;
            Parallel.For(0, nt, () => new List<int>(128), (t, _, buf) =>
            {
                var a = pos[idx[3 * t]]; var b = pos[idx[3 * t + 1]]; var c = pos[idx[3 * t + 2]];
                longest[t] = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
                rms[t] = float.NaN;
                var nf = Vector3.Cross(b - a, c - a);
                float l = nf.Length();
                if (l < 1e-12f) return buf;
                nf /= l;
                var cc = (a + b + c) / 3f;
                float r = MathF.Max(Vector3.Distance(cc, a), MathF.Max(Vector3.Distance(cc, b), Vector3.Distance(cc, c)));
                g.Query(cc, MathF.Sqrt(r * r + o.MaxOffset * o.MaxOffset), buf);
                double s = 0; int cnt = 0;
                foreach (int i in buf)
                {
                    var q = g.Positions[i];
                    float dn = Vector3.Dot(q - a, nf);
                    if (MathF.Abs(dn) > o.MaxOffset) continue;
                    if (!InsideTriangle(q - nf * dn, a, b, c)) continue;
                    s += dn * dn; cnt++;
                }
                if (cnt >= 4) rms[t] = (float)Math.Sqrt(s / cnt);
                return buf;
            }, _ => { });

            // 잡음 추정: 대부분의 삼각형은 평평한 곳이므로 중앙값 ≈ 점 잡음
            var valid = new List<float>(nt);
            foreach (var x in rms) if (!float.IsNaN(x)) valid.Add(x);
            if (valid.Count == 0) return new List<int>();
            valid.Sort();
            stats.NoiseRms = valid[valid.Count / 2];
            float tol = MathF.Max(o.DetailTolerance, o.NoiseFactor * stats.NoiseRms);
            stats.Tolerance = tol;

            float minSplit = 2f * o.MinEdge;
            var cand = new List<int>();
            for (int t = 0; t < nt; t++)
                if (longest[t] >= minSplit && !float.IsNaN(rms[t]) && rms[t] > tol) cand.Add(t);
            cand.Sort((x, y) => rms[y].CompareTo(rms[x]));
            int budget = (maxTris - nt) / 6; // 1→4 에 이웃 이음매까지 대략 삼각형 6개 늘어난다
            if (cand.Count > budget) cand.RemoveRange(Math.Max(0, budget), cand.Count - Math.Max(0, budget));
            return cand;
        }

        static bool InsideTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var v0 = b - a; var v1 = c - a; var v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (MathF.Abs(den) < 1e-20f) return false;
            float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
            return v >= 0 && w >= 0 && v + w <= 1;
        }

        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>
        /// 고른 삼각형의 (충분히 긴) 변을 반으로 나누고, 나뉜 변을 가진 모든 삼각형을 이음매가 맞게 다시 만든다 (red-green).
        /// 감김 방향은 유지한다.
        /// </summary>
        static void Subdivide(IndexedMesh m, List<int> tris, float minSplit)
        {
            var idx = m.Indices; var pos = m.Vertices;
            int nt = m.TriangleCount;
            var marked = new HashSet<long>(EdgeKeyComparer.Instance);
            float min2 = minSplit * minSplit;
            bool Long(int a, int b) => Vector3.DistanceSquared(pos[a], pos[b]) >= min2;
            foreach (int t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int a = idx[3 * t + k], b = idx[3 * t + (k + 1) % 3];
                    if (Long(a, b)) marked.Add(EdgeKey(a, b));
                }
            // 변 두 개가 나뉜 삼각형은 셋째 변도 나눠 1→4 로 (가는 삼각형이 덜 생기게)
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                for (int t = 0; t < nt; t++)
                {
                    int cnt = 0, free = -1;
                    for (int k = 0; k < 3; k++)
                    {
                        if (marked.Contains(EdgeKey(idx[3 * t + k], idx[3 * t + (k + 1) % 3]))) cnt++; else free = k;
                    }
                    if (cnt != 2) continue;
                    int a = idx[3 * t + free], b = idx[3 * t + (free + 1) % 3];
                    if (Long(a, b)) { marked.Add(EdgeKey(a, b)); changed = true; }
                }
                if (!changed) break;
            }
            if (marked.Count == 0) return;

            var mid = new Dictionary<long, int>(marked.Count, EdgeKeyComparer.Instance);
            foreach (var key in marked)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                mid[key] = pos.Count;
                pos.Add((pos[a] + pos[b]) * 0.5f);
            }

            var outIdx = new List<int>(idx.Count + marked.Count * 12);
            void Tri(int a, int b, int c) { outIdx.Add(a); outIdx.Add(b); outIdx.Add(c); }
            for (int t = 0; t < nt; t++)
            {
                int v0 = idx[3 * t], v1 = idx[3 * t + 1], v2 = idx[3 * t + 2];
                int m0 = mid.TryGetValue(EdgeKey(v0, v1), out var x0) ? x0 : -1;
                int m1 = mid.TryGetValue(EdgeKey(v1, v2), out var x1) ? x1 : -1;
                int m2 = mid.TryGetValue(EdgeKey(v2, v0), out var x2) ? x2 : -1;
                int cnt = (m0 >= 0 ? 1 : 0) + (m1 >= 0 ? 1 : 0) + (m2 >= 0 ? 1 : 0);
                if (cnt == 0) { Tri(v0, v1, v2); continue; }
                if (cnt == 3)
                {
                    Tri(v0, m0, m2); Tri(m0, v1, m1); Tri(m2, m1, v2); Tri(m0, m1, m2);
                    continue;
                }
                // 돌려서 (a,b,c) 의 변 ab 가 나뉜 변(1개일 때) / ab·bc 가 나뉜 변(2개일 때)이 되게 한다
                int a = v0, b = v1, c = v2, mab = m0, mbc = m1, mca = m2;
                for (int r = 0; r < 3; r++)
                {
                    bool ok = cnt == 1 ? mab >= 0 : (mab >= 0 && mbc >= 0);
                    if (ok) break;
                    int ta = a; a = b; b = c; c = ta;
                    int tm = mab; mab = mbc; mbc = mca; mca = tm;
                }
                if (cnt == 1) { Tri(a, mab, c); Tri(mab, b, c); }
                else
                {
                    Tri(mab, b, mbc);
                    // 남은 사각형 a-mab-mbc-c 는 짧은 대각선으로 나눈다
                    if (Vector3.DistanceSquared(pos[a], pos[mbc]) <= Vector3.DistanceSquared(pos[mab], pos[c])) { Tri(a, mab, mbc); Tri(a, mbc, c); }
                    else { Tri(a, mab, c); Tri(mab, mbc, c); }
                }
            }
            m.Indices = outIdx;
        }
    }
}
