using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RackScanner.Analysis;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Tools;

// 저장된 세션의 점군(.ply)에 랙 분석을 반복 실행하는 도구. Unity 없이 분석 로직을 빠르게 다듬기 위해 쓴다.
//
//   dotnet run --project tools/AnalysisCli -- <points.ply | 세션 폴더> [옵션]
//   dotnet run --project tools/AnalysisCli -- --synthetic out.ply [--bays 3 --levels 4 --fill 0.5]
//   dotnet run --project tools/AnalysisCli -- --mesh <mesh_raw.obj | 세션 폴더> [--error-mm 5] [--denoise 5] [--compare ref.obj] [--out mesh.obj]
//   dotnet run --project tools/AnalysisCli -- --synthetic-mesh out.obj [--noise-mm 1]
//   dotnet run --project tools/AnalysisCli -- --mesh <세션 폴더> --refine [--no-color-guide] [--min-edge-mm 10] [--levels 2] [--error-mm 5]
//        점(points.ply)으로 메쉬를 다듬고(mesh_refined.obj) 카메라 색을 입힌 뒤 단순화(mesh.obj). 색은 mesh_raw.obj 에도 입힌다.
//   dotnet run --project tools/AnalysisCli -- --synthetic-refine [--out 폴더] [--noise-mm 3]
//        합성 장면에서 ARKit 흉내 메쉬 → 다듬기 전/후를 정답 면과 비교 (색 가중 끔/켬)
//
// 옵션:
//   --bay-width <m>      A단계: 칸 폭 (주면 규격 입력 모드)
//   --depth <m>          A단계: 랙 깊이
//   --levels-at a,b,c    A단계: 빔 높이들
//   --level-pitch <m>    A단계: 층 간격 (--first-level 과 함께)
//   --first-level <m>
//   --objects-only       랙 검출 없이 물체 박스만 (M3)
//   --out <result.json>  결과 저장 (기본: 세션 폴더면 result.json, 아니면 화면만)
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: AnalysisCli <points.ply|sessionDir> [--bay-width m] [--depth m] [--levels-at a,b,c] [--level-pitch m --first-level m] [--objects-only] [--out file]\n       AnalysisCli --synthetic out.ply [--bays n] [--levels n] [--fill p] [--yaw deg] [--seed n]"); return 2; }
        var opt = ParseOptions(args, out var positional);

        if (opt.TryGetValue("synthetic-mesh", out var synthMesh))
        {
            var m = SyntheticMesh.Room(Float(opt, "noise-mm", 1) / 1000f);
            ObjIO.Write(synthMesh, m, "synthetic room (floor, wall, shelf, boxes) at 2cm grid");
            Console.WriteLine($"wrote {m.TriangleCount:N0} triangles to {synthMesh}");
            return 0;
        }

        if (opt.ContainsKey("synthetic-refine")) return SyntheticRefineBench(opt);

        if (opt.TryGetValue("mesh", out var meshIn) && opt.ContainsKey("refine")) return RefineSession(meshIn, opt);

        if (opt.TryGetValue("mesh", out meshIn))
        {
            // 적응형 단순화를 저장된 메쉬에 반복 실행 (넓은 면은 크게, 복잡한 곳은 원래 밀도)
            string src = Directory.Exists(meshIn) ? Path.Combine(meshIn, SessionMeta.MeshRawFileName) : meshIn;
            var raw = ObjIO.Read(src);
            float err = Float(opt, "error-mm", 5) / 1000f;
            var t = System.Diagnostics.Stopwatch.StartNew();
            var simplified = MeshSimplifier.Simplify(raw, new SimplifyOptions { MaxError = err, DenoiseIterations = Int(opt, "denoise", 5) });
            Console.WriteLine($"triangles {raw.TriangleCount:N0} -> {simplified.TriangleCount:N0} ({100.0 * simplified.TriangleCount / Math.Max(1, raw.TriangleCount):F1}%), area {raw.SurfaceArea():F3} -> {simplified.SurfaceArea():F3} m2, error {err * 1000:F1} mm ({t.ElapsedMilliseconds} ms)");
            if (opt.TryGetValue("compare", out var refPath))
            {
                // 기준 메쉬(예: 잡음 없는 합성 장면)와 표면 거리를 양방향으로 잰다
                var reference = ObjIO.Read(refPath);
                var a = MeshCompare.Stats(simplified, new MeshCompare(reference));
                var b = MeshCompare.Stats(reference, new MeshCompare(simplified));
                Console.WriteLine($"  simplified->ref  mean {a.mean * 1000:F2} mm, p95 {a.p95 * 1000:F2} mm, max {a.max * 1000:F2} mm");
                Console.WriteLine($"  ref->simplified  mean {b.mean * 1000:F2} mm, p95 {b.p95 * 1000:F2} mm, max {b.max * 1000:F2} mm");
            }
            string dst = opt.TryGetValue("out", out var mo) ? mo : (Directory.Exists(meshIn) ? Path.Combine(meshIn, SessionMeta.MeshFileName) : null);
            if (dst != null) { ObjIO.Write(dst, simplified, $"adaptive simplification (QEM), max error {err * 1000:F1} mm"); Console.WriteLine($"saved {dst}"); }
            return 0;
        }

        if (opt.ContainsKey("synthetic"))
        {
            var scene = new SyntheticScene(Int(opt, "seed", 1));
            int levels = Int(opt, "levels", 4);
            var p = new SyntheticScene.RackParams
            {
                OriginX = 1.0f, OriginZ = 1.0f,
                YawDeg = Float(opt, "yaw", 0),
                Bays = Int(opt, "bays", 3),
                BayWidth = Float(opt, "bay-width", 1.2f),
                Depth = Float(opt, "depth", 0.6f),
                LevelHeights = Enumerable.Range(0, levels).Select(i => 0.15f + i * 0.6f).ToArray(),
                BoxFill = Float(opt, "fill", 0.4f),
            };
            scene.AddFloor(-1, 6, -1, 4).AddRack(p).AddOutliers(200, 5);
            PlyIO.Write(opt["synthetic"], scene.Points);
            Console.WriteLine($"wrote {scene.Points.Count} points to {opt["synthetic"]}");
            return 0;
        }

        string input = positional.FirstOrDefault() ?? throw new ArgumentException("input path required");
        string plyPath = Directory.Exists(input) ? Path.Combine(input, SessionMeta.PointsFileName) : input;
        var points = PlyIO.Read(plyPath);

        var s = new AnalysisSettings { DetectRacks = !opt.ContainsKey("objects-only") };
        if (opt.ContainsKey("bay-width") || opt.ContainsKey("levels-at") || opt.ContainsKey("level-pitch") || opt.ContainsKey("depth"))
        {
            s.Spec = new RackSpec
            {
                BayWidth = Float(opt, "bay-width", 0),
                Depth = Float(opt, "depth", 0),
                LevelPitch = Float(opt, "level-pitch", 0),
                FirstLevelHeight = Float(opt, "first-level", 0),
            };
            if (opt.TryGetValue("levels-at", out var la))
                s.Spec.LevelHeights = la.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToList();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = RackAnalyzer.Analyze(points, s);
        Console.WriteLine(result.Summary());
        Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");

        string outPath = opt.TryGetValue("out", out var o) ? o : (Directory.Exists(input) ? Path.Combine(input, SessionMeta.ResultFileName) : null);
        if (outPath != null) { File.WriteAllText(outPath, result.ToJson()); Console.WriteLine($"saved {outPath}"); }
        return 0;
    }

    static RefineOptions RefineOpts(Dictionary<string, string> opt) => new RefineOptions
    {
        UseColor = !opt.ContainsKey("no-color-guide"),
        MinEdge = Float(opt, "min-edge-mm", 10) / 1000f,
        SubdivideLevels = Int(opt, "levels", 2),
        Radius = Float(opt, "radius-mm", 15) / 1000f,
        NormalSigma = Float(opt, "normal-sigma-mm", 8) / 1000f,
        MaxOffset = Float(opt, "max-offset-mm", 30) / 1000f,
        PointVoxel = Float(opt, "point-voxel-mm", 4) / 1000f,
    };

    /// <summary>저장된 세션: mesh_raw.obj + points.ply → mesh_refined.obj, mesh.obj (색 포함), mesh_raw.obj 에 색.</summary>
    static int RefineSession(string dir, Dictionary<string, string> opt)
    {
        if (!Directory.Exists(dir)) { Console.Error.WriteLine("--refine needs a session folder (mesh_raw.obj + points.ply)"); return 2; }
        var raw = ObjIO.Read(Path.Combine(dir, SessionMeta.MeshRawFileName));
        var pts = PlyIO.Read(Path.Combine(dir, SessionMeta.PointsFileName));
        var o = RefineOpts(opt);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = MeshPipeline.Run(raw, pts, o, Float(opt, "error-mm", 5) / 1000f, refine: true);
        Console.WriteLine($"points {pts.Count:N0} -> grid {result.PointCount:N0} (voxel {o.PointVoxel * 1000:F0} mm), color {(result.HasColor ? "yes" : "no (white points)")}");
        Console.WriteLine("refine: " + result.Stats);
        Console.WriteLine($"simplified: {result.Simplified.TriangleCount:N0} triangles ({sw.ElapsedMilliseconds} ms total)");
        result.Save(dir);
        Console.WriteLine($"saved {SessionMeta.MeshRawFileName} (colors), {SessionMeta.MeshRefinedFileName}, {SessionMeta.MeshFileName} in {dir}");
        return 0;
    }

    static int SyntheticRefineBench(Dictionary<string, string> opt)
    {
        var reference = SyntheticRefine.Reference();
        var refCmp = new MeshCompare(reference);
        var pts = SyntheticRefine.Points(noise: Float(opt, "noise-mm", 3) / 1000f);
        var coarse = SyntheticRefine.ArkitLike();
        var o = RefineOpts(opt);
        var grid = new PointGrid(pts, o.PointVoxel, o.Radius);
        Console.WriteLine($"reference {reference.TriangleCount:N0} tris, points {pts.Count:N0} -> grid {grid.Count:N0}, ARKit-like mesh {coarse.TriangleCount:N0} tris");

        void Report(string name, IndexedMesh m)
        {
            var a = MeshCompare.Stats(m, refCmp);
            var b = MeshCompare.Stats(reference, new MeshCompare(m));
            if (opt.ContainsKey("worst"))
            {
                var cm = new MeshCompare(m);
                var worst = reference.Vertices.Select(v => (v, d: cm.Distance(v))).OrderByDescending(x => x.d).Take(8);
                foreach (var w in worst) Console.WriteLine($"      worst ref vertex {w.v.X:F3},{w.v.Y:F3},{w.v.Z:F3} -> {w.d * 1000:F1} mm");
            }
            Console.WriteLine($"  {name,-18} tris {m.TriangleCount,7:N0} | mesh->ref mean {a.mean * 1000,5:F2} p95 {a.p95 * 1000,5:F2} mm | ref->mesh mean {b.mean * 1000,5:F2} p95 {b.p95 * 1000,5:F2} max {b.max * 1000,5:F1} mm");
        }
        Report("ARKit-like", coarse);
        string outDir = opt.TryGetValue("out", out var od) ? od : null;
        if (outDir != null) { Directory.CreateDirectory(outDir); ObjIO.Write(Path.Combine(outDir, "reference.obj"), reference); ObjIO.Write(Path.Combine(outDir, "arkit_like.obj"), coarse); PlyIO.Write(Path.Combine(outDir, "points.ply"), pts); }
        foreach (bool color in new[] { false, true })
        {
            o.UseColor = color;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = MeshRefiner.Refine(coarse, grid, o, out var st);
            long ms = sw.ElapsedMilliseconds;
            Report(color ? "refined+color" : "refined", r);
            Console.WriteLine($"    {st} ({ms} ms)");
            MeshColorizer.Colorize(r, grid);
            int ok = 0;
            for (int i = 0; i < r.VertexCount; i++)
            {
                var t = SyntheticRefine.TrueColor(r.Vertices[i]); var c = r.Colors[i];
                if (Math.Abs(t.R - c.R) <= 40 && Math.Abs(t.G - c.G) <= 40 && Math.Abs(t.B - c.B) <= 40) ok++;
            }
            Console.WriteLine($"    vertex color within 40/255 of true object color: {100.0 * ok / r.VertexCount:F1}%");
            if (outDir != null) ObjIO.Write(Path.Combine(outDir, color ? "refined_color.obj" : "refined.obj"), r);
        }
        return 0;
    }

    static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
    {
        var d = new Dictionary<string, string>();
        positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string k = args[i].Substring(2);
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                d[k] = hasValue ? args[++i] : "true";
            }
            else positional.Add(args[i]);
        }
        return d;
    }

    static float Float(Dictionary<string, string> d, string k, float def) => d.TryGetValue(k, out var v) ? float.Parse(v, CultureInfo.InvariantCulture) : def;
    static int Int(Dictionary<string, string> d, string k, int def) => d.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
}
