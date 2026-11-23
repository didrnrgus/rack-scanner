using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RackScanner.Shared.IO;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 촬영 후 메쉬 처리 순서 (PC 수신 앱과 AnalysisCli 가 같이 쓴다):
    /// 원본(ARKit) → [점으로 다듬기] → 단순화, 각 결과에 점의 카메라 색을 입힌다.
    /// 무거우므로 수신 앱은 백그라운드 스레드에서 부른다. (UnityEngine 비의존)
    /// </summary>
    public sealed class MeshPipeline
    {
        public IndexedMesh Raw, Refined, Simplified;
        public RefineStats Stats;
        public float SimplifyError;
        public int PointCount;
        public bool HasColor;

        /// <summary>다듬은 메쉬가 있으면 그것, 없으면 원본 (단순화의 입력).</summary>
        public IndexedMesh Detailed => Refined ?? Raw;

        /// <param name="points">마커 좌표 점 (없거나 비면 다듬기·색 없이 단순화만)</param>
        /// <param name="simplifyError">0 이면 단순화하지 않는다 (Simplified = Detailed)</param>
        public static MeshPipeline Run(IndexedMesh raw, IReadOnlyList<PointXYZRGB> points, RefineOptions o, float simplifyError, bool refine)
        {
            o = o ?? new RefineOptions();
            var r = new MeshPipeline { Raw = Copy(raw) };
            PointGrid grid = null;
            if (points != null && points.Count > 0)
            {
                grid = new PointGrid(points, o.PointVoxel, o.Radius);
                r.PointCount = grid.Count;
                r.HasColor = grid.HasColor;
            }
            MeshColorizer.Colorize(r.Raw, grid);
            if (refine && grid != null)
            {
                r.Refined = MeshRefiner.Refine(r.Raw, grid, o, out r.Stats);
                MeshColorizer.Colorize(r.Refined, grid);
            }
            r.Resimplify(simplifyError, grid);
            r._grid = grid;
            return r;
        }

        PointGrid _grid;

        /// <summary>단순화만 다른 오차로 다시 (다듬기는 다시 하지 않는다).</summary>
        public void Resimplify(float error) => Resimplify(error, _grid);

        void Resimplify(float error, PointGrid grid)
        {
            // 다 만든 뒤에 바꿔 넣는다 (다른 스레드가 보고 있는 Simplified 가 중간 상태로 보이지 않게)
            var s = Detailed;
            if (error > 0)
            {
                s = MeshSimplifier.Simplify(Detailed, new SimplifyOptions { MaxError = error });
                MeshColorizer.Colorize(s, grid);
            }
            Simplified = s;
            SimplifyError = error;
        }

        /// <summary>mesh_raw.obj (색), mesh_refined.obj (있으면), mesh.obj (단순화) 를 쓴다.</summary>
        public void Save(string sessionDir)
        {
            var ci = CultureInfo.InvariantCulture;
            string color = HasColor ? "vertex colors from camera (via colored depth points)" : "no camera colors (points are white)";
            ObjIO.Write(Path.Combine(sessionDir, SessionMeta.MeshRawFileName), Raw, "ARKit mesh blocks merged (original shape, original density)\n" + color);
            if (Refined != null)
                ObjIO.Write(Path.Combine(sessionDir, SessionMeta.MeshRefinedFileName), Refined, "ARKit mesh refined with depth points\n" + Stats + "\n" + color);
            if (SimplifyError > 0)
                SaveSimplified(sessionDir);
        }

        public string SaveSimplified(string sessionDir)
        {
            string path = Path.Combine(sessionDir, SessionMeta.MeshFileName);
            ObjIO.Write(path, Simplified, string.Format(CultureInfo.InvariantCulture,
                "adaptive simplification (QEM) of {0}, max error {1:F1} mm, triangles {2} -> {3}",
                Refined != null ? SessionMeta.MeshRefinedFileName : SessionMeta.MeshRawFileName, SimplifyError * 1000, Detailed.TriangleCount, Simplified.TriangleCount));
            return path;
        }

        static IndexedMesh Copy(IndexedMesh m)
        {
            var c = new IndexedMesh();
            c.Vertices.AddRange(m.Vertices);
            c.Indices.AddRange(m.Indices);
            return c;
        }
    }
}
