using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Geometry;

namespace RackScanner.Analysis
{
    /// <summary>
    /// 위에서 본 발자국 하나. 점은 "바닥 기준 높이" 좌표 (X, H, Z) 로 들고 있다.
    /// YawDeg 는 길이 방향(Length)의 Unity yaw, 길이 방향 단위벡터 = (sin, 0, cos).
    /// </summary>
    public sealed class Footprint
    {
        public List<Vector3> Points = new List<Vector3>(); // (x, 바닥 위 높이, z)
        public float CenterX, CenterZ;
        public float Length, Width;
        public float YawDeg;
        public float MaxHeight;     // 98 백분위 높이
        public float CellArea;      // 점유 칸 면적 (m²)
        public float OwnYawDeg;     // 이 발자국 자체의 최소 면적 사각형 yaw

        public Vector3 Dir => new Vector3((float)Math.Sin(YawDeg * YawMath.Deg2Rad), 0, (float)Math.Cos(YawDeg * YawMath.Deg2Rad));
        /// <summary>길이 방향을 Y 축으로 +90° 돌린 방향 (Unity 로컬 +X).</summary>
        public Vector3 Perp => new Vector3((float)Math.Cos(YawDeg * YawMath.Deg2Rad), 0, -(float)Math.Sin(YawDeg * YawMath.Deg2Rad));
    }

    /// <summary>3단계 발자국 후보: 2D 격자 투영 → 팽창 → 연결 영역 → 방향 있는 사각형.</summary>
    public static class FootprintDetector
    {
        /// <param name="leveled">(x, 바닥 위 높이, z) 점들. 바닥 근처 점은 이미 제거된 상태.</param>
        public static List<Footprint> Detect(IReadOnlyList<Vector3> leveled, AnalysisSettings s)
        {
            float cell = s.FootprintCell;
            var occupied = new Dictionary<long, List<int>>();
            for (int i = 0; i < leveled.Count; i++)
            {
                var p = leveled[i];
                long key = Key((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Z / cell));
                if (!occupied.TryGetValue(key, out var l)) occupied[key] = l = new List<int>();
                l.Add(i);
            }

            // 팽창
            var dilated = new HashSet<long>();
            int d = Math.Max(0, s.FootprintDilateCells);
            foreach (var key in occupied.Keys)
            {
                Unkey(key, out int x, out int z);
                for (int dx = -d; dx <= d; dx++)
                for (int dz = -d; dz <= d; dz++)
                    dilated.Add(Key(x + dx, z + dz));
            }

            // 연결 영역 (8-이웃)
            var label = new Dictionary<long, int>();
            int nLabels = 0;
            var stack = new Stack<long>();
            foreach (var start in dilated)
            {
                if (label.ContainsKey(start)) continue;
                int id = nLabels++;
                label[start] = id;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    Unkey(stack.Pop(), out int x, out int z);
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        long nk = Key(x + dx, z + dz);
                        if (dilated.Contains(nk) && !label.ContainsKey(nk)) { label[nk] = id; stack.Push(nk); }
                    }
                }
            }

            var groups = new List<Footprint>();
            for (int i = 0; i < nLabels; i++) groups.Add(new Footprint());
            var cellCount = new int[nLabels];
            foreach (var kv in occupied)
            {
                int id = label[kv.Key];
                cellCount[id]++;
                foreach (int idx in kv.Value) groups[id].Points.Add(leveled[idx]);
            }

            var result = new List<Footprint>();
            for (int i = 0; i < nLabels; i++)
            {
                var f = groups[i];
                f.CellArea = cellCount[i] * cell * cell;
                if (f.Points.Count < 5 || f.CellArea < s.MinObjectArea) continue;
                f.MaxHeight = Percentile(f.Points, 0.98f);
                if (f.MaxHeight < s.MinObjectHeight) continue;
                f.OwnYawDeg = MinAreaYaw(f.Points);
                Fit(f, f.OwnYawDeg);
                result.Add(f);
            }
            return result;
        }

        /// <summary>주어진 yaw 로 사각형을 맞춘다. 길이 방향이 더 긴 쪽이 되도록 90° 돌릴 수 있다.</summary>
        public static void Fit(Footprint f, float yawDeg)
        {
            Extents(f.Points, yawDeg, out float minU, out float maxU, out float minV, out float maxV);
            if (maxV - minV > maxU - minU)
            {
                yawDeg += 90f;
                Extents(f.Points, yawDeg, out minU, out maxU, out minV, out maxV);
            }
            f.YawDeg = YawMath.NormalizeDeg(yawDeg);
            var dir = f.Dir; var perp = f.Perp;
            float cu = 0.5f * (minU + maxU), cv = 0.5f * (minV + maxV);
            var c = dir * cu + perp * cv;
            f.CenterX = c.X; f.CenterZ = c.Z;
            f.Length = maxU - minU;
            f.Width = maxV - minV;
        }

        /// <summary>0~90° 를 1° 간격으로 돌려 보며 최소 면적 사각형의 yaw 를 찾는다.</summary>
        public static float MinAreaYaw(List<Vector3> pts)
        {
            float bestYaw = 0, bestArea = float.MaxValue;
            for (int a = 0; a < 90; a++)
            {
                Extents(pts, a, out float u0, out float u1, out float v0, out float v1);
                float area = (u1 - u0) * (v1 - v0);
                if (area < bestArea - 1e-6f) { bestArea = area; bestYaw = a; }
            }
            return bestYaw;
        }

        /// <summary>
        /// 여러 발자국의 공통 주축 (mod 90°). 점 수로 가중한 1° 히스토그램에서 최빈 각을 고른다.
        /// </summary>
        public static float DominantYaw(IReadOnlyList<Footprint> fs)
        {
            var hist = new float[90];
            foreach (var f in fs)
            {
                float w = f.Points.Count * Math.Max(1f, f.Length / Math.Max(0.05f, f.Width)); // 길쭉할수록 방향이 믿을 만함
                int a = ((int)Math.Round(f.OwnYawDeg) % 90 + 90) % 90;
                hist[a] += w;
            }
            int best = 0; float bestV = -1;
            for (int a = 0; a < 90; a++)
            {
                float v = hist[a] + 0.5f * (hist[(a + 1) % 90] + hist[(a + 89) % 90]);
                if (v > bestV) { bestV = v; best = a; }
            }
            return best;
        }

        public static void Extents(List<Vector3> pts, float yawDeg, out float minU, out float maxU, out float minV, out float maxV)
        {
            float sn = (float)Math.Sin(yawDeg * YawMath.Deg2Rad), cs = (float)Math.Cos(yawDeg * YawMath.Deg2Rad);
            minU = minV = float.MaxValue; maxU = maxV = float.MinValue;
            foreach (var p in pts)
            {
                float u = p.X * sn + p.Z * cs;     // dir = (sin, 0, cos)
                float v = p.X * cs - p.Z * sn;     // perp = (cos, 0, -sin)
                if (u < minU) minU = u; if (u > maxU) maxU = u;
                if (v < minV) minV = v; if (v > maxV) maxV = v;
            }
        }

        static float Percentile(List<Vector3> pts, float q)
        {
            var hs = new float[pts.Count];
            for (int i = 0; i < hs.Length; i++) hs[i] = pts[i].Y;
            Array.Sort(hs);
            return hs[Math.Min(hs.Length - 1, (int)(q * (hs.Length - 1)))];
        }

        static long Key(int x, int z) => ((long)x << 32) | (uint)z;
        static void Unkey(long k, out int x, out int z) { x = (int)(k >> 32); z = (int)(k & 0xffffffff); }
    }
}
