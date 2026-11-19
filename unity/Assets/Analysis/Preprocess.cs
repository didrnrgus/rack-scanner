using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;

namespace RackScanner.Analysis
{
    /// <summary>1단계 전처리: 복셀 다운샘플 + 통계적 이상치 제거.</summary>
    public static class Preprocess
    {
        public static List<PointXYZRGB> Run(IReadOnlyList<PointXYZRGB> input, AnalysisSettings s)
        {
            var down = VoxelAccumulator.Downsample(input, s.VoxelSize);
            return RemoveStatisticalOutliers(down, s.OutlierNeighbors, s.OutlierStdRatio, s.OutlierSearchRadius);
        }

        /// <summary>
        /// 각 점의 k 최근접 평균 거리를 구해, 전체 평균 + ratio*표준편차 보다 큰 점을 버린다.
        /// 이웃은 반경 크기 격자의 주변 27칸에서만 찾는다 (반경 안에 이웃이 k개 미만이면 반경을 거리로 채움).
        /// </summary>
        public static List<PointXYZRGB> RemoveStatisticalOutliers(List<PointXYZRGB> pts, int k, float stdRatio, float radius)
        {
            if (pts.Count < k + 1 || k <= 0) return pts;
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                long key = VoxelKey.FromPoint(pts[i].X, pts[i].Y, pts[i].Z, radius);
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            var meanDist = new float[pts.Count];
            var best = new float[k];
            float r2 = radius * radius;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i].Position;
                VoxelKey.Unpack(VoxelKey.FromPoint(p, radius), out int cx, out int cy, out int cz);
                int found = 0;
                for (int j = 0; j < k; j++) best[j] = r2;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(VoxelKey.Pack(cx + dx, cy + dy, cz + dz), out var cell)) continue;
                    foreach (int j in cell)
                    {
                        if (j == i) continue;
                        float d2 = Vector3.DistanceSquared(p, pts[j].Position);
                        if (d2 >= best[k - 1]) continue;
                        // 삽입 정렬로 k개 유지
                        int pos = k - 1;
                        while (pos > 0 && best[pos - 1] > d2) { best[pos] = best[pos - 1]; pos--; }
                        best[pos] = d2;
                        found++;
                    }
                }
                double sum = 0;
                for (int j = 0; j < k; j++) sum += Math.Sqrt(best[j]);
                meanDist[i] = (float)(sum / k);
            }
            double mean = 0, sq = 0;
            foreach (var d in meanDist) mean += d;
            mean /= meanDist.Length;
            foreach (var d in meanDist) sq += (d - mean) * (d - mean);
            double std = Math.Sqrt(sq / meanDist.Length);
            double limit = mean + stdRatio * std;
            var result = new List<PointXYZRGB>(pts.Count);
            for (int i = 0; i < pts.Count; i++) if (meanDist[i] <= limit) result.Add(pts[i]);
            return result;
        }
    }

    /// <summary>2단계 바닥 검출: 높이 히스토그램의 가장 낮은 큰 봉우리 → 평면 피팅.</summary>
    public static class FloorDetector
    {
        public static FloorPlane Detect(IReadOnlyList<PointXYZRGB> pts, AnalysisSettings s)
        {
            var plane = new FloorPlane();
            if (pts.Count < 10) return plane;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in pts) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            int bins = Math.Max(1, (int)Math.Ceiling((maxY - minY) / s.FloorHistogramBin) + 1);
            var hist = new int[bins];
            foreach (var p in pts) hist[(int)((p.Y - minY) / s.FloorHistogramBin)]++;
            var smooth = Profile.Smooth(Array.ConvertAll(hist, x => (float)x), 1);
            float maxCount = 0;
            foreach (var v in smooth) maxCount = Math.Max(maxCount, v);
            int peak = -1;
            for (int i = 0; i < bins; i++)
            {
                bool isLocalMax = (i == 0 || smooth[i] >= smooth[i - 1]) && (i == bins - 1 || smooth[i] >= smooth[i + 1]);
                if (isLocalMax && smooth[i] >= s.FloorPeakMinRatio * maxCount) { peak = i; break; }
            }
            if (peak < 0) return plane;
            float peakY = minY + (peak + 0.5f) * s.FloorHistogramBin;

            // 봉우리 근처 점으로 평면 y = a x + b z + c 최소제곱 피팅, 한 번 더 인라이어로 재피팅
            float a = 0, b = 0, c = peakY;
            float band = s.FloorFitBand;
            for (int iter = 0; iter < 3; iter++)
            {
                double sxx = 0, sxz = 0, szz = 0, sx = 0, sz = 0, n = 0, sxy = 0, szy = 0, sy = 0;
                foreach (var p in pts)
                {
                    float r = p.Y - (a * p.X + b * p.Z + c);
                    if (Math.Abs(r) > band) continue;
                    sxx += p.X * p.X; sxz += p.X * p.Z; szz += p.Z * p.Z;
                    sx += p.X; sz += p.Z; n++;
                    sxy += p.X * p.Y; szy += p.Z * p.Y; sy += p.Y;
                }
                if (n < 30) break;
                if (!Solve3(sxx, sxz, sx, sxz, szz, sz, sx, sz, n, sxy, szy, sy, out double na, out double nb, out double nc))
                {
                    c = (float)(sy / n);
                }
                else
                {
                    a = (float)na; b = (float)nb; c = (float)nc;
                }
                plane.InlierCount = (int)n;
                band = Math.Max(0.02f, band * 0.6f);
            }
            // 기울기가 너무 크면(>10°) 평면이 아니라고 보고 수평으로
            if (Math.Sqrt(a * a + b * b) > 0.18) { a = 0; b = 0; c = peakY; }
            plane.A = a; plane.B = b; plane.C = c;
            plane.Found = plane.InlierCount >= 30;
            return plane;
        }

        static bool Solve3(double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33,
                           double b1, double b2, double b3, out double x, out double y, out double z)
        {
            double det = a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
            x = y = z = 0;
            if (Math.Abs(det) < 1e-9) return false;
            x = (b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) / det;
            y = (a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31)) / det;
            z = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31)) / det;
            return true;
        }
    }

    /// <summary>1차원 프로파일(히스토그램) 도구.</summary>
    public static class Profile
    {
        public static float[] Smooth(float[] v, int radius)
        {
            if (radius <= 0) return (float[])v.Clone();
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float s = 0; int n = 0;
                for (int j = Math.Max(0, i - radius); j <= Math.Min(v.Length - 1, i + radius); j++) { s += v[j]; n++; }
                r[i] = s / n;
            }
            return r;
        }

        /// <summary>연속 위치 x 에서의 값 (bin 중심 사이 선형 보간).</summary>
        public static float Sample(float[] v, float bin, float x)
        {
            if (v.Length == 0) return 0;
            float f = x / bin - 0.5f;
            if (f <= 0) return v[0];
            if (f >= v.Length - 1) return v[v.Length - 1];
            int i = (int)f;
            float t = f - i;
            return v[i] * (1 - t) + v[i + 1] * t;
        }

        /// <summary>구간 [x-halfWidth, x+halfWidth] 의 최대값.</summary>
        public static float MaxAround(float[] v, float bin, float x, float halfWidth)
        {
            int a = Math.Max(0, (int)Math.Floor((x - halfWidth) / bin));
            int b = Math.Min(v.Length - 1, (int)Math.Floor((x + halfWidth) / bin));
            float m = 0;
            for (int i = a; i <= b; i++) m = Math.Max(m, v[i]);
            return m;
        }

        public static float Median(List<float> xs)
        {
            if (xs.Count == 0) return 0;
            var c = new List<float>(xs);
            c.Sort();
            return c.Count % 2 == 1 ? c[c.Count / 2] : 0.5f * (c[c.Count / 2 - 1] + c[c.Count / 2]);
        }
    }
}
