using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;

namespace RackScanner.Analysis
{
    /// <summary>
    /// 랙 분석 파이프라인 진입점. 물류를 지우는 대신 뼈대(기둥·빔)를 찾아 치수를 재고,
    /// 그 치수로 빈 랙을 생성할 정보를 낸다. 학습 없는 규칙 기반.
    /// 1 전처리 → 2 바닥 → 3 발자국 → 4 층 → 5 칸 → 6 규칙성 보정 → (7 생성은 Receiver 의 RackSpawner)
    /// </summary>
    public static class RackAnalyzer
    {
        public static AnalysisResult Analyze(IReadOnlyList<PointXYZRGB> input, AnalysisSettings s = null)
        {
            s = s ?? new AnalysisSettings();
            var result = new AnalysisResult { InputPointCount = input.Count };

            // 1. 전처리
            var pts = Preprocess.Run(input, s);
            result.FilteredPointCount = pts.Count;

            // 2. 바닥 (못 찾으면 가장 낮은 점들 기준 수평면)
            var floor = FloorDetector.Detect(pts, s);
            if (!floor.Found)
            {
                float minY = float.MaxValue;
                foreach (var p in pts) minY = Math.Min(minY, p.Y);
                floor.C = pts.Count > 0 ? minY : 0;
            }
            result.Floor = floor;

            var leveled = new List<Vector3>(pts.Count);
            foreach (var p in pts)
            {
                float h = p.Y - floor.HeightAt(p.X, p.Z);
                if (h > s.FloorClearance) leveled.Add(new Vector3(p.X, h, p.Z));
            }

            // 3. 발자국
            var prints = FootprintDetector.Detect(leveled, s);
            int objIndex = 1;
            foreach (var f in prints)
            {
                result.Objects.Add(new ObjectBox
                {
                    Id = $"O{objIndex++:00}",
                    CenterX = f.CenterX,
                    CenterZ = f.CenterZ,
                    CenterY = floor.HeightAt(f.CenterX, f.CenterZ) + f.MaxHeight / 2,
                    Length = f.Length,
                    Width = f.Width,
                    Height = f.MaxHeight,
                    YawDeg = f.YawDeg,
                    PointCount = f.Points.Count,
                });
            }
            if (!s.DetectRacks) return result;

            // 랙 후보와 공통 주축
            var rackPrints = new List<Footprint>();
            foreach (var f in prints)
                if (f.MaxHeight >= s.MinRackHeight && Math.Max(f.Length, f.Width) >= s.MinRackLength) rackPrints.Add(f);
            float dominant = rackPrints.Count > 0 ? FootprintDetector.DominantYaw(rackPrints) : 0;
            if (s.AssumeParallelRacks)
            {
                foreach (var f in rackPrints) FootprintDetector.Fit(f, dominant);
                rackPrints = PairFrames(rackPrints, dominant, s);
            }

            var racks = new List<(RackResult r, Footprint f)>();
            foreach (var f in rackPrints)
            {
                // 1번 칸 쪽(원점에 가까운 끝)이 u=0 이 되도록 방향을 정한다
                var c = new Vector3(f.CenterX, 0, f.CenterZ);
                var endPlus = c + f.Dir * (f.Length / 2);
                var endMinus = c - f.Dir * (f.Length / 2);
                if (endPlus.Length() < endMinus.Length()) f.YawDeg = YawMath.NormalizeDeg(f.YawDeg + 180f);

                var dir = f.Dir; var perp = f.Perp;
                var origin = c - dir * (f.Length / 2) - perp * (f.Width / 2);
                var local = new List<Vector3>(f.Points.Count);
                foreach (var p in f.Points)
                {
                    var d = new Vector3(p.X, 0, p.Z) - origin;
                    local.Add(new Vector3(Vector3.Dot(d, dir), p.Y, Vector3.Dot(d, perp)));
                }

                var st = RackStructureDetector.Detect(local, f.Length, f.Width, f.MaxHeight, s);
                var o = origin + dir * st.OriginShiftU;
                var r = new RackResult
                {
                    Mode = st.SpecMode ? "spec" : "auto",
                    BayCount = Math.Max(0, st.Uprights.Count - 1),
                    LevelCount = st.Levels.Count,
                    BayWidth = st.BayWidth,
                    LevelHeights = st.Levels,
                    LevelObserved = st.LevelObserved,
                    UprightPositions = st.Uprights,
                    UprightObserved = st.UprightObserved,
                    Depth = st.Depth,
                    Length = st.Length,
                    Height = st.Height,
                    PoseX = o.X,
                    PoseZ = o.Z,
                    PoseY = floor.HeightAt(o.X, o.Z),
                    YawDeg = f.YawDeg,
                    Confidence = st.Confidence,
                };
                racks.Add((r, f));
            }

            AssignNames(racks);
            foreach (var x in racks) result.Racks.Add(x.r);
            result.Racks.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.IndexInRow.CompareTo(b.IndexInRow));
            return result;
        }

        /// <summary>
        /// 앞·뒤 프레임 묶기. 길이 방향으로 겹치는 얇은 발자국들을 깊이 방향 위치 순으로 정렬해
        /// 1-2, 3-4 ... 순서로 짝짓는다 (등을 맞댄 두 랙 F1 B1 | B2 F2 도 바르게 짝지어진다).
        /// 규격 깊이가 있으면 그 깊이와 맞는 짝만 묶는다.
        /// </summary>
        static List<Footprint> PairFrames(List<Footprint> prints, float dominantYaw, AnalysisSettings s)
        {
            var frames = prints.FindAll(f => f.Width <= s.FrameMaxWidth);
            if (frames.Count < 2) return prints;
            // 길이 방향은 각 발자국의 실제 yaw (주축 mod 90° 는 가로축일 수도 있다)
            Vector3 DirOf(Footprint f) => f.Dir;
            float Along(Footprint f, Vector3 dir) => Vector3.Dot(new Vector3(f.CenterX, 0, f.CenterZ), dir);
            bool SameAxis(Footprint a, Footprint b) => Math.Abs(Vector3.Dot(a.Dir, b.Dir)) > 0.999f;
            bool Overlap(Footprint a, Footprint b)
            {
                if (!SameAxis(a, b)) return false;
                var dir = DirOf(a);
                float a0 = Along(a, dir) - a.Length / 2, a1 = Along(a, dir) + a.Length / 2;
                float b0 = Along(b, dir) - b.Length / 2, b1 = Along(b, dir) + b.Length / 2;
                float ov = Math.Min(a1, b1) - Math.Max(a0, b0);
                return ov >= 0.5f * Math.Min(a.Length, b.Length);
            }

            // 길이 방향으로 겹치는 프레임끼리 묶음
            var groups = new List<List<Footprint>>();
            foreach (var f in frames)
            {
                var g = groups.Find(gr => gr.Exists(x => Overlap(x, f)));
                if (g == null) groups.Add(new List<Footprint> { f }); else g.Add(f);
            }

            var result = prints.FindAll(f => f.Width > s.FrameMaxWidth);
            foreach (var g in groups)
            {
                var perp = g[0].Perp;
                float Across(Footprint f) => Vector3.Dot(new Vector3(f.CenterX, 0, f.CenterZ), perp);
                g.Sort((a, b) => Across(a).CompareTo(Across(b)));
                int i = 0;
                while (i < g.Count)
                {
                    if (i + 1 < g.Count)
                    {
                        var a = g[i]; var b = g[i + 1];
                        float outer = Across(b) + b.Width / 2 - (Across(a) - a.Width / 2);
                        bool depthOk = s.Spec != null && s.Spec.Depth > 0 ? Math.Abs(outer - s.Spec.Depth) < 0.3f : outer <= s.MaxRackDepth;
                        if (depthOk)
                        {
                            var m = new Footprint { CellArea = a.CellArea + b.CellArea, MaxHeight = Math.Max(a.MaxHeight, b.MaxHeight), OwnYawDeg = dominantYaw };
                            m.Points.AddRange(a.Points); m.Points.AddRange(b.Points);
                            FootprintDetector.Fit(m, a.YawDeg);
                            result.Add(m);
                            i += 2;
                            continue;
                        }
                    }
                    result.Add(g[i]);
                    i++;
                }
            }
            return result;
        }

        /// <summary>
        /// 이름 규칙 (docs/DEV_PLAN.md): 열은 원점에서 가까운 줄부터 1,2,3 (줄 = 공통 주축에 수직한 방향의 위치),
        /// 같은 줄 안에서는 원점에 가까운 쪽부터. 현장 관행에 맞게 확정 필요 (TODO).
        /// </summary>
        static void AssignNames(List<(RackResult r, Footprint f)> racks)
        {
            if (racks.Count == 0) return;
            // 랙들은 평행하다고 가정: 가장 큰 랙의 길이 방향을 기준 축으로
            var main = racks[0].f;
            foreach (var x in racks) if (x.f.Points.Count > main.Points.Count) main = x.f;
            var dir = main.Dir;
            var perp = main.Perp;
            var items = new List<(int i, float across, float along, float depth)>();
            for (int i = 0; i < racks.Count; i++)
            {
                var c = new Vector3(racks[i].f.CenterX, 0, racks[i].f.CenterZ);
                items.Add((i, Vector3.Dot(c, perp), Vector3.Dot(c, dir), racks[i].f.Width));
            }
            items.Sort((a, b) => Math.Abs(a.across).CompareTo(Math.Abs(b.across)));
            var rowCenters = new List<float>();
            var rowOf = new int[racks.Count];
            foreach (var it in items)
            {
                int row = -1;
                for (int k = 0; k < rowCenters.Count; k++)
                    if (Math.Abs(rowCenters[k] - it.across) < Math.Max(0.3f, it.depth * 0.5f)) { row = k; break; }
                if (row < 0) { rowCenters.Add(it.across); row = rowCenters.Count - 1; }
                rowOf[it.i] = row + 1;
            }
            int id = 1;
            for (int row = 1; row <= rowCenters.Count; row++)
            {
                var inRow = items.FindAll(x => rowOf[x.i] == row);
                inRow.Sort((a, b) => Math.Abs(a.along).CompareTo(Math.Abs(b.along)));
                for (int k = 0; k < inRow.Count; k++)
                {
                    var r = racks[inRow[k].i].r;
                    r.Row = row;
                    r.IndexInRow = k + 1;
                    r.RackId = $"R{id++:00}";
                }
            }
        }
    }
}
