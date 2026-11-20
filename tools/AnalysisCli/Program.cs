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

        if (opt.TryGetValue("mesh", out var meshIn))
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
