using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.IO
{
    /// <summary>
    /// PLY 읽기/쓰기. 쓰기는 binary_little_endian (x,y,z float + red,green,blue uchar).
    /// 읽기는 ascii / binary_little_endian 의 vertex 요소를 지원하고 다른 속성은 건너뛴다.
    /// </summary>
    public static class PlyIO
    {
        public static void Write(string path, IReadOnlyList<PointXYZRGB> points)
        {
            using (var fs = File.Create(path)) Write(fs, points);
        }

        public static void Write(Stream s, IReadOnlyList<PointXYZRGB> points)
        {
            var header = "ply\nformat binary_little_endian 1.0\ncomment rack-scanner marker-frame points (meters, Unity Y-up)\n" +
                         $"element vertex {points.Count}\n" +
                         "property float x\nproperty float y\nproperty float z\n" +
                         "property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n";
            var hb = Encoding.ASCII.GetBytes(header);
            s.Write(hb, 0, hb.Length);
            var buf = new byte[15];
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), p.X);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 4, 4), p.Y);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 8, 4), p.Z);
                buf[12] = p.R; buf[13] = p.G; buf[14] = p.B;
                s.Write(buf, 0, 15);
            }
        }

        public static List<PointXYZRGB> Read(string path)
        {
            using (var fs = File.OpenRead(path)) return Read(fs);
        }

        sealed class Prop { public string Name; public string Type; public int Size; }

        public static List<PointXYZRGB> Read(Stream s)
        {
            // 헤더 (ASCII 줄 단위)
            string format = null;
            int vertexCount = 0;
            var props = new List<Prop>();
            var preVertexElems = new List<(int count, List<Prop> props)>();
            string currentElem = null;
            List<Prop> currentProps = null;
            int currentCount = 0;
            string line = ReadLine(s);
            if (line != "ply") throw new InvalidDataException("not a PLY file");
            bool seenVertex = false;
            while ((line = ReadLine(s)) != null)
            {
                var tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length == 0) continue;
                if (tok[0] == "end_header") break;
                if (tok[0] == "format") format = tok[1];
                else if (tok[0] == "element")
                {
                    if (currentElem != null && currentElem != "vertex" && !seenVertex)
                        preVertexElems.Add((currentCount, currentProps));
                    currentElem = tok[1];
                    currentCount = int.Parse(tok[2], CultureInfo.InvariantCulture);
                    currentProps = new List<Prop>();
                    if (currentElem == "vertex") { vertexCount = currentCount; props = currentProps; seenVertex = true; }
                }
                else if (tok[0] == "property")
                {
                    if (tok[1] == "list") { if (currentElem == "vertex") throw new InvalidDataException("list property in vertex not supported"); continue; }
                    currentProps?.Add(new Prop { Type = tok[1], Name = tok[2], Size = SizeOf(tok[1]) });
                }
            }
            if (preVertexElems.Count > 0) throw new InvalidDataException("elements before vertex not supported");
            int ix = props.FindIndex(p => p.Name == "x"), iy = props.FindIndex(p => p.Name == "y"), iz = props.FindIndex(p => p.Name == "z");
            if (ix < 0 || iy < 0 || iz < 0) throw new InvalidDataException("vertex x/y/z missing");
            int ir = props.FindIndex(p => p.Name == "red" || p.Name == "r");
            int ig = props.FindIndex(p => p.Name == "green" || p.Name == "g");
            int ib = props.FindIndex(p => p.Name == "blue" || p.Name == "b");

            var result = new List<PointXYZRGB>(vertexCount);
            var vals = new double[props.Count];
            if (format == "ascii")
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    var l = ReadLine(s) ?? throw new EndOfStreamException();
                    var tok = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    for (int k = 0; k < props.Count; k++) vals[k] = double.Parse(tok[k], CultureInfo.InvariantCulture);
                    result.Add(Make(vals, ix, iy, iz, ir, ig, ib));
                }
            }
            else if (format == "binary_little_endian")
            {
                int stride = 0;
                foreach (var p in props) stride += p.Size;
                var buf = new byte[stride];
                for (int v = 0; v < vertexCount; v++)
                {
                    int got = 0;
                    while (got < stride) { int n = s.Read(buf, got, stride - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
                    int off = 0;
                    for (int k = 0; k < props.Count; k++) { vals[k] = ReadValue(buf, off, props[k].Type); off += props[k].Size; }
                    result.Add(Make(vals, ix, iy, iz, ir, ig, ib));
                }
            }
            else throw new InvalidDataException($"unsupported PLY format '{format}'");
            return result;
        }

        static PointXYZRGB Make(double[] v, int ix, int iy, int iz, int ir, int ig, int ib)
        {
            return new PointXYZRGB((float)v[ix], (float)v[iy], (float)v[iz],
                ir >= 0 ? (byte)v[ir] : (byte)255, ig >= 0 ? (byte)v[ig] : (byte)255, ib >= 0 ? (byte)v[ib] : (byte)255);
        }

        static int SizeOf(string t)
        {
            switch (t)
            {
                case "char": case "uchar": case "int8": case "uint8": return 1;
                case "short": case "ushort": case "int16": case "uint16": return 2;
                case "int": case "uint": case "float": case "int32": case "uint32": case "float32": return 4;
                case "double": case "float64": return 8;
                default: throw new InvalidDataException($"unknown PLY type {t}");
            }
        }

        static double ReadValue(byte[] b, int o, string t)
        {
            switch (t)
            {
                case "char": case "int8": return (sbyte)b[o];
                case "uchar": case "uint8": return b[o];
                case "short": case "int16": return BitConverter.ToInt16(b, o);
                case "ushort": case "uint16": return BitConverter.ToUInt16(b, o);
                case "int": case "int32": return BitConverter.ToInt32(b, o);
                case "uint": case "uint32": return BitConverter.ToUInt32(b, o);
                case "float": case "float32": return BitConverter.ToSingle(b, o);
                default: return BitConverter.ToDouble(b, o);
            }
        }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            int c;
            while ((c = s.ReadByte()) >= 0)
            {
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)c);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
