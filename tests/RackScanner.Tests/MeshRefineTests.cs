using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;
using RackScanner.Tools;
using Xunit;

namespace RackScanner.Tests
{
    /// <summary>메쉬 색 입히기(MeshColorizer), 점으로 다듬기(MeshRefiner), 처리 순서(MeshPipeline), 색 있는 OBJ.</summary>
    public class MeshRefineTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-refine-tests-" + Guid.NewGuid().ToString("N"));
        public MeshRefineTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        /// <summary>XZ 평면 격자 (y = height), 위에서 볼 때 앞면.</summary>
        static IndexedMesh Grid(int n, float step, float height = 0)
        {
            var m = new IndexedMesh();
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                    m.Vertices.Add(new Vector3(i * step, height, j * step));
            int W = n + 1;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * W + i, b = a + 1, c = a + W, d = c + 1;
                    m.Indices.AddRange(new[] { a, c, b, b, c, d });
                }
            return m;
        }

        static List<PointXYZRGB> PlanePoints(float size, float spacing, Func<float, float, float> height, Func<float, float, Rgb24> color, float noise = 0, int seed = 1)
        {
            var rng = new Random(seed);
            var pts = new List<PointXYZRGB>();
            for (float z = 0; z <= size + 1e-4f; z += spacing)
                for (float x = 0; x <= size + 1e-4f; x += spacing)
                {
                    var c = color(x, z);
                    float y = height(x, z) + (float)(rng.NextDouble() * 2 - 1) * noise;
                    pts.Add(new PointXYZRGB(x, y, z, c.R, c.G, c.B));
                }
            return pts;
        }

        static readonly Rgb24 Red = new Rgb24(220, 30, 30), Blue = new Rgb24(30, 30, 220);

        [Fact]
        public void ColorizeTakesColorOfNearbyPoints()
        {
            var mesh = Grid(10, 0.05f);
            var pts = PlanePoints(0.5f, 0.004f, (x, z) => 0, (x, z) => x < 0.25f ? Red : Blue);
            Assert.True(MeshColorizer.Colorize(mesh, new PointGrid(pts)));
            Assert.True(mesh.HasColors);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float x = mesh.Vertices[i].X;
                if (MathF.Abs(x - 0.25f) < 0.02f) continue; // 경계는 섞여도 된다
                var want = x < 0.25f ? Red : Blue;
                Assert.InRange(mesh.Colors[i].R, want.R - 10, want.R + 10);
                Assert.InRange(mesh.Colors[i].B, want.B - 10, want.B + 10);
            }
        }

        [Fact]
        public void ColorDoesNotBleedFromSurfaceBehind()
        {
            // 메쉬는 y=0 면. 같은 자리 2.5cm 뒤(아래)에 파란 면이 있어도 빨강이어야 한다
            var mesh = Grid(10, 0.05f);
            var pts = PlanePoints(0.5f, 0.004f, (x, z) => 0, (x, z) => Red);
            pts.AddRange(PlanePoints(0.5f, 0.004f, (x, z) => -0.025f, (x, z) => Blue));
            MeshColorizer.Colorize(mesh, new PointGrid(pts));
            foreach (var c in mesh.Colors) { Assert.True(c.R > 180, c.ToString()); Assert.True(c.B < 70, c.ToString()); }
        }

        [Fact]
        public void WhitePointsMeanNoCameraColor()
        {
            var mesh = Grid(4, 0.05f);
            var pts = PlanePoints(0.2f, 0.004f, (x, z) => 0, (x, z) => new Rgb24(255, 255, 255));
            var grid = new PointGrid(pts);
            Assert.False(grid.HasColor);
            Assert.False(MeshColorizer.Colorize(mesh, grid));
            Assert.Null(mesh.Colors);
        }

        [Fact]
        public void RefineSnapsOffsetMeshOntoPointsWithoutSplittingFlatArea()
        {
            // 평평한 면: 메쉬가 1.2cm 떠 있으면 점 쪽으로 내려오고, 디테일이 없으니 나누지 않는다
            var mesh = Grid(10, 0.05f, 0.012f);
            var pts = PlanePoints(0.5f, 0.004f, (x, z) => 0, (x, z) => Red, noise: 0.002f);
            var r = MeshRefiner.Refine(mesh, new PointGrid(pts), new RefineOptions(), out var st);
            Assert.Equal(mesh.TriangleCount, r.TriangleCount);
            foreach (var v in r.Vertices)
            {
                bool edge = v.X < 0.01f || v.X > 0.49f || v.Z < 0.01f || v.Z > 0.49f;
                Assert.InRange(v.Y, edge ? -0.004f : -0.002f, edge ? 0.004f : 0.002f);
            }
            Assert.True(st.Moved > mesh.VertexCount * 0.9);
        }

        [Fact]
        public void RefineAddsDetailOnlyWhereSurfaceIsNotFlat()
        {
            // 5cm 격자 위에 높이 3cm·폭 4cm 턱(x 0.23~0.27). 메쉬는 평평하게 시작한다 → 턱 근처만 나뉘고 높이를 따라간다
            var mesh = Grid(10, 0.05f);
            float Step(float x, float z) => x > 0.23f && x < 0.27f ? 0.03f : 0f;
            var pts = PlanePoints(0.5f, 0.003f, Step, (x, z) => Red, noise: 0.001f);
            var r = MeshRefiner.Refine(mesh, new PointGrid(pts, 0.003f), new RefineOptions { MaxOffset = 0.04f }, out var st);
            Assert.True(r.TriangleCount > mesh.TriangleCount, st.ToString());
            Assert.True(r.TriangleCount < mesh.TriangleCount * 4, st.ToString());
            // 턱 위에 올라간 정점이 생겨야 한다
            int onTop = 0;
            foreach (var v in r.Vertices) if (v.X > 0.235f && v.X < 0.265f && v.Z > 0.05f && v.Z < 0.45f && v.Y > 0.02f) onTop++;
            Assert.True(onTop >= 5, $"on top {onTop}, {st}");
            // 먼 평평한 곳(x < 0.1)의 삼각형은 그대로 (5cm)
            for (int t = 0; t < r.TriangleCount; t++)
            {
                var a = r.Vertices[r.Indices[3 * t]]; var b = r.Vertices[r.Indices[3 * t + 1]]; var c = r.Vertices[r.Indices[3 * t + 2]];
                if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < 0.1f)
                    Assert.True(Vector3.Distance(a, b) > 0.045f || Vector3.Distance(b, c) > 0.045f || Vector3.Distance(c, a) > 0.045f);
            }
            AssertNoCracks(mesh, r);
        }

        /// <summary>나누기가 이음매를 만들지 않았나: 한 삼각형에만 쓰인 변(경계)의 총 길이가 원래 둘레와 같아야 한다.</summary>
        static void AssertNoCracks(IndexedMesh before, IndexedMesh after)
        {
            Assert.Equal(BoundaryLength(before, before), BoundaryLength(after, before), 2);
        }

        static float BoundaryLength(IndexedMesh m, IndexedMesh positionsFrom)
        {
            var count = new Dictionary<(int, int), int>();
            for (int t = 0; t < m.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = m.Indices[3 * t + k], b = m.Indices[3 * t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    count[key] = count.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            float len = 0;
            foreach (var kv in count)
                if (kv.Value == 1)
                {
                    // XZ 길이로 잰다 (정점이 Y 로 움직여도 둘레는 같다)
                    var a = m.Vertices[kv.Key.Item1]; var b = m.Vertices[kv.Key.Item2];
                    len += new Vector2(a.X - b.X, a.Z - b.Z).Length();
                }
            return len;
        }

        [Fact]
        public void RefineKeepsWindingDirection()
        {
            var mesh = Grid(10, 0.05f);
            float Step(float x, float z) => x > 0.23f && x < 0.27f ? 0.03f : 0f;
            var pts = PlanePoints(0.5f, 0.003f, Step, (x, z) => Red, noise: 0.001f);
            var r = MeshRefiner.Refine(mesh, new PointGrid(pts, 0.003f), new RefineOptions(), out _);
            // 원래 메쉬는 모두 +Y 쪽 앞면 (Unity 시계 방향 → Cross(b-a, c-a) 가 -Y). 다듬은 뒤에도 같은 쪽이어야 한다
            double sumY = 0;
            for (int t = 0; t < r.TriangleCount; t++)
            {
                var a = r.Vertices[r.Indices[3 * t]];
                sumY += Vector3.Cross(r.Vertices[r.Indices[3 * t + 1]] - a, r.Vertices[r.Indices[3 * t + 2]] - a).Y;
            }
            double sumY0 = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var a = mesh.Vertices[mesh.Indices[3 * t]];
                sumY0 += Vector3.Cross(mesh.Vertices[mesh.Indices[3 * t + 1]] - a, mesh.Vertices[mesh.Indices[3 * t + 2]] - a).Y;
            }
            Assert.Equal(Math.Sign(sumY0), Math.Sign(sumY));
            Assert.InRange(sumY / sumY0, 0.95, 1.05); // 위에서 본 넓이 합 그대로 (뒤집힌 삼각형이 있으면 줄어든다)
        }

        [Fact]
        public void RefineImprovesArkitLikeMeshOnSyntheticScene()
        {
            // 합성 장면: 거친(5cm)·뭉개진 메쉬를 4mm 간격·잡음 3mm 컬러 점으로 다듬는다
            var reference = SyntheticRefine.Reference();
            var refCmp = new MeshCompare(reference);
            var coarse = SyntheticRefine.ArkitLike();
            var pts = SyntheticRefine.Points();
            var o = new RefineOptions();
            var grid = new PointGrid(pts, o.PointVoxel, o.Radius);
            var r = MeshRefiner.Refine(coarse, grid, o, out var st);

            var before = MeshCompare.Stats(coarse, refCmp);
            var after = MeshCompare.Stats(r, refCmp);
            var beforeBack = MeshCompare.Stats(reference, new MeshCompare(coarse));
            var afterBack = MeshCompare.Stats(reference, new MeshCompare(r));
            string msg = $"mesh->ref p95 {before.p95 * 1000:F2} -> {after.p95 * 1000:F2} mm, ref->mesh p95 {beforeBack.p95 * 1000:F2} -> {afterBack.p95 * 1000:F2} mm, {st}";
            Assert.True(after.p95 < before.p95 * 0.4f, msg);          // 메쉬가 실제 면에 붙는다
            Assert.True(afterBack.p95 < beforeBack.p95 * 0.5f, msg);  // 뭉개졌던 모서리·빔이 살아난다
            Assert.True(after.mean < 0.0015f, msg);
            Assert.True(r.TriangleCount <= coarse.TriangleCount * 4, msg);

            // 색: 정점의 95% 이상이 그 자리 물체 색에 가깝다
            MeshColorizer.Colorize(r, grid);
            int ok = 0;
            for (int i = 0; i < r.VertexCount; i++)
            {
                var t = SyntheticRefine.TrueColor(r.Vertices[i]); var c = r.Colors[i];
                if (Math.Abs(t.R - c.R) <= 40 && Math.Abs(t.G - c.G) <= 40 && Math.Abs(t.B - c.B) <= 40) ok++;
            }
            Assert.True(ok > r.VertexCount * 0.95, $"color ok {100.0 * ok / r.VertexCount:F1}%");
        }

        [Fact]
        public void ObjRoundTripKeepsVertexColors()
        {
            var m = Grid(2, 0.1f);
            m.Colors = new List<Rgb24>();
            for (int i = 0; i < m.VertexCount; i++) m.Colors.Add(new Rgb24((byte)(i * 20), (byte)(255 - i * 20), 77));
            var path = Path.Combine(_dir, "c.obj");
            ObjIO.Write(path, m);
            Assert.Contains("v -0 0 0 0 1 0.302", File.ReadAllText(path).Replace("v 0 0 0", "v -0 0 0")); // x 는 뒤집혀 "0" 으로 쓰인다
            var back = ObjIO.Read(path);
            Assert.True(back.HasColors);
            for (int i = 0; i < m.VertexCount; i++)
            {
                Assert.Equal(m.Colors[i].R, back.Colors[i].R);
                Assert.Equal(m.Colors[i].G, back.Colors[i].G);
                Assert.Equal(m.Colors[i].B, back.Colors[i].B);
            }
            // 색 없는 메쉬는 예전처럼 "v x y z" 만
            var plain = Grid(1, 0.1f);
            ObjIO.Write(path, plain);
            Assert.Null(ObjIO.Read(path).Colors);
        }

        [Fact]
        public void PipelineSavesColoredRawRefinedAndSimplified()
        {
            var raw = SyntheticRefine.ArkitLike();
            var pts = SyntheticRefine.Points();
            var p = MeshPipeline.Run(raw, pts, new RefineOptions(), 0.005f, refine: true);
            Assert.True(p.HasColor);
            Assert.NotNull(p.Refined);
            Assert.True(p.Raw.HasColors && p.Refined.HasColors && p.Simplified.HasColors);
            Assert.Equal(raw.TriangleCount, p.Raw.TriangleCount); // 원본 모양은 그대로
            Assert.True(p.Simplified.TriangleCount < p.Refined.TriangleCount);
            p.Save(_dir);
            Assert.True(ObjIO.Read(Path.Combine(_dir, SessionMeta.MeshRawFileName)).HasColors);
            Assert.True(ObjIO.Read(Path.Combine(_dir, SessionMeta.MeshRefinedFileName)).HasColors);
            Assert.True(ObjIO.Read(Path.Combine(_dir, SessionMeta.MeshFileName)).HasColors);

            // 단순화만 다시: 다듬은 메쉬는 그대로
            var refined = p.Refined;
            p.Resimplify(0.01f);
            Assert.Same(refined, p.Refined);
            Assert.True(p.Simplified.HasColors);

            // 점이 없으면 다듬기·색 없이 단순화만
            var q = MeshPipeline.Run(raw, new List<PointXYZRGB>(), null, 0.005f, refine: true);
            Assert.Null(q.Refined);
            Assert.False(q.HasColor);
            Assert.False(q.Simplified.HasColors);
        }
    }
}
