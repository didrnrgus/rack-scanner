using System;
using System.Collections.Generic;
using System.Numerics;

namespace RackScanner.Analysis
{
    /// <summary>
    /// 4~6단계: 발자국 하나 안에서 층(빔 높이)과 칸(기둥 위치)을 찾고 규칙성으로 빈틈을 메운다.
    /// 로컬 좌표: u = 길이 방향 (0 = 1번 칸 쪽 끝), v = 깊이 방향 (0 = 앞면), h = 바닥 위 높이.
    /// </summary>
    public static class RackStructureDetector
    {
        public sealed class Structure
        {
            public bool SpecMode;
            public float OriginShiftU;        // 발자국 시작점 대비 랙 원점의 u 이동 (A단계 위치 맞춤)
            public float Length, Depth, Height;
            public float BayWidth;
            public List<float> Uprights = new List<float>();
            public List<bool> UprightObserved = new List<bool>();
            public List<float> Levels = new List<float>();
            public List<bool> LevelObserved = new List<bool>();
            public float Confidence;
            // 디버그·튜닝용 프로파일
            public float[] LevelCoverage;
            public float[] UprightCoverage;
        }

        /// <param name="local">(v, h, u) 가 아니라 (u, h, v) 로 담는다: X=u, Y=h, Z=v.</param>
        public static Structure Detect(List<Vector3> local, float length, float width, float height, AnalysisSettings s)
        {
            var st = new Structure { SpecMode = s.Spec != null, Height = height, Length = length };
            var spec = s.Spec;

            // ---- 층: 높이별로 "길이 방향 덮임 비율" 프로파일 ----
            float hb = s.LevelHeightBin, lb = s.LevelLengthBin;
            int hBins = Math.Max(1, (int)Math.Ceiling((height + 0.1f) / hb));
            int uBins = Math.Max(1, (int)Math.Ceiling(length / lb));
            var occ = new bool[hBins, uBins];
            foreach (var p in local)
            {
                int hi = (int)(p.Y / hb), ui = (int)(p.X / lb);
                if (hi >= 0 && hi < hBins && ui >= 0 && ui < uBins) occ[hi, ui] = true;
            }
            var cov = new float[hBins];
            for (int h = 0; h < hBins; h++)
            {
                int n = 0;
                for (int u = 0; u < uBins; u++)
                {
                    bool any = false;
                    for (int dh = -1; dh <= 1 && !any; dh++) { int hh = h + dh; if (hh >= 0 && hh < hBins && occ[hh, u]) any = true; }
                    if (any) n++;
                }
                cov[h] = (float)n / uBins;
            }
            st.LevelCoverage = cov;
            var detectedLevels = FindLevelPeaks(cov, s);

            // ---- 칸: 길이 위치별로 "높이 방향 덮임 비율" 프로파일 ----
            float bb = s.BayLengthBin, hb2 = s.BayHeightBin;
            int uBins2 = Math.Max(1, (int)Math.Ceiling(length / bb));
            int hBins2 = Math.Max(1, (int)Math.Ceiling(height / hb2));
            var occ2 = new bool[uBins2, hBins2];
            foreach (var p in local)
            {
                int ui = (int)(p.X / bb), hi = (int)(p.Y / hb2);
                if (ui >= 0 && ui < uBins2 && hi >= 0 && hi < hBins2) occ2[ui, hi] = true;
            }
            var vc = new float[uBins2];
            for (int u = 0; u < uBins2; u++)
            {
                int n = 0;
                for (int h = 0; h < hBins2; h++)
                {
                    bool any = false;
                    for (int du = -1; du <= 1 && !any; du++) { int uu = u + du; if (uu >= 0 && uu < uBins2 && occ2[uu, h]) any = true; }
                    if (any) n++;
                }
                vc[u] = (float)n / hBins2;
            }
            st.UprightCoverage = vc;

            float uw = s.UprightWidth;
            if (spec != null && spec.BayWidth > 0)
                FitBaysWithSpec(st, vc, length, spec.BayWidth, s);
            else
                FitBaysAuto(st, vc, length, s);

            // ---- 층 확정 (A단계 규격 또는 B단계 + 규칙성 보정) ----
            if (spec != null && spec.LevelHeights != null && spec.LevelHeights.Count > 0)
            {
                foreach (var h in spec.LevelHeights) { st.Levels.Add(h); st.LevelObserved.Add(NearAny(detectedLevels, h, 0.06f)); }
            }
            else if (spec != null && spec.LevelPitch > 0)
            {
                float first = spec.FirstLevelHeight > 0 ? spec.FirstLevelHeight : FitLevelOffset(cov, hb, spec.LevelPitch, height);
                for (float h = first; h <= height + 0.05f; h += spec.LevelPitch)
                {
                    st.Levels.Add(h);
                    st.LevelObserved.Add(NearAny(detectedLevels, h, 0.06f));
                }
            }
            else
            {
                FillLevelsByRegularity(detectedLevels, st.Levels, st.LevelObserved);
            }

            st.Depth = spec != null && spec.Depth > 0 ? spec.Depth : width;

            float upObs = Ratio(st.UprightObserved), lvObs = Ratio(st.LevelObserved);
            st.Confidence = 0.5f * upObs + 0.5f * lvObs;
            return st;
        }

        /// <summary>층 후보: 덮임 비율의 국소 최대 중 충분히 높고 주변보다 돌출된 것.</summary>
        public static List<float> FindLevelPeaks(float[] cov, AnalysisSettings s)
        {
            float hb = s.LevelHeightBin;
            int win = Math.Max(2, (int)Math.Round(s.LevelBaselineWindow / hb));
            var cands = new List<(float h, float score)>();
            for (int i = 0; i < cov.Length; i++)
            {
                if (cov[i] < s.LevelMinCoverage) continue;
                if (i > 0 && cov[i] < cov[i - 1]) continue;
                if (i < cov.Length - 1 && cov[i] < cov[i + 1]) continue;
                var around = new List<float>();
                for (int j = i - win; j <= i + win; j++)
                {
                    if (Math.Abs(j - i) <= 2) continue;
                    around.Add(j >= 0 && j < cov.Length ? cov[j] : 0f);
                }
                float baseline = Profile.Median(around);
                float prom = cov[i] - baseline;
                if (prom < s.LevelMinProminence) continue;
                // 평평한 봉우리는 가운데로
                int k = i;
                while (k + 1 < cov.Length && cov[k + 1] == cov[i]) k++;
                cands.Add(((0.5f * (i + k) + 0.5f) * hb, cov[i] + prom));
                i = k;
            }
            // 가까운 후보는 점수 높은 쪽만 (NMS)
            cands.Sort((a, b) => b.score.CompareTo(a.score));
            var kept = new List<float>();
            foreach (var c in cands)
            {
                bool tooClose = false;
                foreach (var k in kept) if (Math.Abs(k - c.h) < s.LevelMinSeparation) { tooClose = true; break; }
                if (!tooClose) kept.Add(c.h);
            }
            kept.Sort();
            return kept;
        }

        /// <summary>
        /// 규칙성 보정: 층 간격은 일정하다고 보고, 간격의 정수배로 벌어진 곳에 가려진 층을 추정해 넣는다.
        /// </summary>
        public static void FillLevelsByRegularity(List<float> observed, List<float> outLevels, List<bool> outObserved)
        {
            if (observed.Count < 3)
            {
                foreach (var h in observed) { outLevels.Add(h); outObserved.Add(true); }
                return;
            }
            var diffs = new List<float>();
            for (int i = 1; i < observed.Count; i++) diffs.Add(observed[i] - observed[i - 1]);
            float pitch = Profile.Median(diffs);
            outLevels.Add(observed[0]); outObserved.Add(true);
            for (int i = 1; i < observed.Count; i++)
            {
                float d = observed[i] - observed[i - 1];
                int k = (int)Math.Round(d / pitch);
                if (k >= 2 && Math.Abs(d - k * pitch) <= 0.2f * pitch)
                {
                    float step = d / k;
                    for (int j = 1; j < k; j++) { outLevels.Add(observed[i - 1] + j * step); outObserved.Add(false); }
                }
                outLevels.Add(observed[i]); outObserved.Add(true);
            }
        }

        /// <summary>
        /// B단계: 칸 수 n 을 바꿔 가며 "빗살" 패턴 상관을 본다:
        /// 점수 = 기둥 예상 위치의 평균 덮임 − 기둥에서 먼 위치의 평균 덮임.
        /// n 이 너무 작으면 설명 못 한 기둥이 "먼 위치"에 남아 점수가 떨어지고, 너무 크면 빈 칸 가운데를 기둥으로 예상해 떨어진다.
        /// </summary>
        static void FitBaysAuto(Structure st, float[] vc, float length, AnalysisSettings s)
        {
            float bb = s.BayLengthBin, uw = s.UprightWidth;
            float span = Math.Max(0.01f, length - uw);
            int maxN = Math.Max(1, Math.Min(s.MaxBays, (int)Math.Floor(span / s.MinBayWidth)));
            int bestN = 1; float bestScore = float.MinValue;
            float farDist = uw / 2 + 2.5f * bb;
            for (int n = 1; n <= maxN; n++)
            {
                float w = span / n, up = 0;
                for (int k = 0; k <= n; k++) up += Profile.MaxAround(vc, bb, uw / 2 + k * w, bb);
                up /= n + 1;
                float far = 0; int nf = 0;
                for (int i = 0; i < vc.Length; i++)
                {
                    float x = (i + 0.5f) * bb;
                    float rel = (x - uw / 2) / w;
                    float dist = Math.Abs(rel - (float)Math.Round(rel)) * w;
                    if (dist > farDist) { far += vc[i]; nf++; }
                }
                far = nf > 0 ? far / nf : 0;
                float score = up - far;
                if (score > bestScore + 1e-3f) { bestScore = score; bestN = n; }
            }
            float bw = span / bestN;
            float search = Math.Min(bw / 4, 0.15f);
            for (int k = 0; k <= bestN; k++)
            {
                float x = uw / 2 + k * bw;
                // 예상 위치 근처의 실제 봉우리로 다듬는다
                float bestX = x, bestV = -1;
                for (float t = x - search; t <= x + search; t += bb * 0.5f)
                {
                    float v = Profile.Sample(vc, bb, t);
                    if (v > bestV + 1e-4f) { bestV = v; bestX = t; }
                }
                bool obs = bestV >= s.UprightMinCoverage;
                st.Uprights.Add(obs ? Math.Clamp(bestX, 0, length) : x);
                st.UprightObserved.Add(obs);
            }
            st.BayWidth = bestN > 0 ? (st.Uprights[bestN] - st.Uprights[0]) / bestN : span;
            st.OriginShiftU = 0;
        }

        /// <summary>A단계: 칸 폭이 주어졌을 때 칸 수 후보와 시작 위치를 훑어 기둥 덮임이 가장 큰 배치를 고른다.</summary>
        static void FitBaysWithSpec(Structure st, float[] vc, float length, float w, AnalysisSettings s)
        {
            float bb = s.BayLengthBin, uw = s.UprightWidth;
            float span = Math.Max(0.01f, length - uw);
            int n0 = Math.Max(1, (int)Math.Round(span / w));
            int bestN = n0; float bestS = 0, bestScore = float.MinValue;
            for (int n = Math.Max(1, n0 - 1); n <= n0 + 1; n++)
            {
                float S = n * w;
                float lo = Math.Min(0, span - S) - 0.15f, hi = Math.Max(0, span - S) + 0.15f;
                for (float off = lo; off <= hi + 1e-4f; off += 0.01f)
                {
                    float sum = 0;
                    for (int k = 0; k <= n; k++) sum += Profile.MaxAround(vc, bb, uw / 2 + off + k * w, bb);
                    float score = sum / (n + 1) - 0.05f * Math.Abs(n - n0);
                    if (score > bestScore + 1e-4f) { bestScore = score; bestN = n; bestS = off; }
                }
            }
            // 고른 n 에서 점수가 같은 구간의 가운데로 시작 위치를 다듬는다
            {
                float S = bestN * w;
                float lo = Math.Min(0, span - S) - 0.15f, hi = Math.Max(0, span - S) + 0.15f;
                var offs = new List<float>(); var scores = new List<float>();
                for (float off = lo; off <= hi + 1e-4f; off += 0.01f)
                {
                    float sum = 0;
                    for (int k = 0; k <= bestN; k++) sum += Profile.MaxAround(vc, bb, uw / 2 + off + k * w, bb);
                    offs.Add(off); scores.Add(sum / (bestN + 1));
                }
                bestS = PlateauCenter(offs, scores);
            }
            for (int k = 0; k <= bestN; k++)
            {
                float x = uw / 2 + bestS + k * w;
                st.Uprights.Add(x - bestS); // 새 원점 기준
                st.UprightObserved.Add(Profile.MaxAround(vc, bb, x, bb) >= s.UprightMinCoverage);
            }
            st.BayWidth = w;
            st.OriginShiftU = bestS;
            st.Length = bestN * w + uw;
        }

        /// <summary>A단계(층 간격만 주어짐): 첫 빔 높이를 0~간격 범위에서 훑어 빔 덮임 합이 가장 큰 값을 고른다.</summary>
        static float FitLevelOffset(float[] cov, float hb, float pitch, float height)
        {
            // 점수가 같은 구간(빔 두께만큼 평평)의 가운데를 고른다
            var offs = new List<float>(); var scores = new List<float>();
            for (float off = 0.05f; off < pitch + 0.05f; off += hb * 0.5f)
            {
                float sum = 0; int n = 0;
                for (float h = off; h <= height + 0.05f; h += pitch) { sum += Profile.MaxAround(cov, hb, h, hb); n++; }
                offs.Add(off); scores.Add(n > 0 ? sum / n : 0);
            }
            return PlateauCenter(offs, scores);
        }

        /// <summary>최고 점수와 같은 값이 이어진 구간의 가운데 x.</summary>
        static float PlateauCenter(List<float> xs, List<float> scores)
        {
            int best = 0;
            for (int i = 1; i < scores.Count; i++) if (scores[i] > scores[best] + 1e-4f) best = i;
            int a = best, b = best;
            while (b + 1 < scores.Count && Math.Abs(scores[b + 1] - scores[best]) <= 1e-4f) b++;
            return 0.5f * (xs[a] + xs[b]);
        }

        static bool NearAny(List<float> xs, float x, float tol)
        {
            foreach (var v in xs) if (Math.Abs(v - x) <= tol) return true;
            return false;
        }

        static float Ratio(List<bool> bs)
        {
            if (bs.Count == 0) return 0;
            int n = 0;
            foreach (var b in bs) if (b) n++;
            return (float)n / bs.Count;
        }
    }
}
