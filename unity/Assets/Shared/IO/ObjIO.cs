using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using RackScanner.Shared.Mesh;

namespace RackScanner.Shared.IO
{
    /// <summary>
    /// Wavefront OBJ 읽기/쓰기 (정점 + 삼각형, 정점 색이 있으면 "v x y z r g b" (0..1) 확장 형식 — MeshLab·Blender 가 읽는다).
    /// 메모리의 메쉬는 Unity 좌표(왼손, Y 위, 앞면 시계 방향)이고, 파일은 OBJ 관례(오른손, 앞면 반시계 방향)로 쓴다:
    /// X 를 뒤집고 삼각형 순서를 바꾼다. Unity 에디터가 OBJ 를 가져올 때 같은 변환을 거꾸로 하므로,
    /// 파일을 Assets 에 넣고 위치·회전 0 으로 두면 마커 기준 원래 좌표에 그대로 놓인다. Blender·MeshLab 에서도 바르게 보인다.
    /// </summary>
    public static class ObjIO
    {
        public static void Write(string path, IndexedMesh mesh, string comment = null)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false))) Write(w, mesh, comment);
        }

        public static void Write(TextWriter w, IndexedMesh mesh, string comment = null)
        {
            var ci = CultureInfo.InvariantCulture;
            w.NewLine = "\n";
            w.WriteLine("# rack-scanner mesh: marker-frame meters. File is right-handed (X mirrored from Unity); Unity import restores Unity coords.");
            if (!string.IsNullOrEmpty(comment))
                foreach (var line in comment.Split('\n')) w.WriteLine("# " + line.TrimEnd('\r'));
            w.WriteLine($"# vertices {mesh.VertexCount}, triangles {mesh.TriangleCount}");
            w.WriteLine("o scan");
            var sb = new StringBuilder(64);
            bool colors = mesh.HasColors;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var v = mesh.Vertices[i];
                sb.Clear();
                sb.Append("v ").Append((0f - v.X).ToString("0.#####", ci)) /* 0f - x: "-0" 이 안 나오게 */.Append(' ')
                  .Append(v.Y.ToString("0.#####", ci)).Append(' ')
                  .Append(v.Z.ToString("0.#####", ci));
                if (colors)
                {
                    var c = mesh.Colors[i];
                    sb.Append(' ').Append((c.R / 255f).ToString("0.###", ci))
                      .Append(' ').Append((c.G / 255f).ToString("0.###", ci))
                      .Append(' ').Append((c.B / 255f).ToString("0.###", ci));
                }
                w.WriteLine(sb.ToString());
            }
            var idx = mesh.Indices;
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                // 1부터 세는 인덱스, 순서 a c b (거울상이라 감김 방향을 되돌린다)
                w.Write("f "); w.Write(idx[t] + 1); w.Write(' '); w.Write(idx[t + 2] + 1); w.Write(' '); w.Write(idx[t + 1] + 1); w.WriteLine();
            }
        }

        public static IndexedMesh Read(string path)
        {
            using (var r = new StreamReader(path)) return Read(r);
        }

        /// <summary>v / f 만 읽는다 (모든 정점에 색이 있으면 색도). 다각형 면은 부채꼴로 나누고, "f 1/2/3" 형식과 음수 인덱스도 받는다.</summary>
        public static IndexedMesh Read(TextReader r)
        {
            var ci = CultureInfo.InvariantCulture;
            var m = new IndexedMesh();
            var face = new List<int>(8);
            var colors = new List<Rgb24>();
            bool allColored = true;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 2) continue;
                if (line[0] == 'v' && line[1] == ' ')
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) throw new FormatException("bad vertex line: " + line);
                    m.Vertices.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)));
                    if (p.Length >= 7 && allColored)
                        colors.Add(new Rgb24(Channel(p[4]), Channel(p[5]), Channel(p[6])));
                    else allColored = false;
                }
                else if (line[0] == 'f' && line[1] == ' ')
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    face.Clear();
                    for (int i = 1; i < p.Length; i++)
                    {
                        var s = p[i];
                        int slash = s.IndexOf('/');
                        int k = int.Parse(slash >= 0 ? s.Substring(0, slash) : s, ci);
                        face.Add(k > 0 ? k - 1 : m.Vertices.Count + k);
                    }
                    for (int i = 1; i + 1 < face.Count; i++)
                    {
                        m.Indices.Add(face[0]); m.Indices.Add(face[i + 1]); m.Indices.Add(face[i]);
                    }
                }
            }
            foreach (var k in m.Indices)
                if (k < 0 || k >= m.Vertices.Count) throw new FormatException($"face index {k} out of range");
            if (allColored && colors.Count == m.Vertices.Count && colors.Count > 0) m.Colors = colors;
            return m;
        }

        static byte Channel(string s)
        {
            float x = float.Parse(s, CultureInfo.InvariantCulture);
            if (x > 1f) return (byte)Math.Clamp((int)MathF.Round(x), 0, 255); // 0..255 로 쓴 파일도 받는다
            return (byte)Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
        }
    }
}
