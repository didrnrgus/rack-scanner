using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.Geometry
{
    /// <summary>복셀 좌표 (정수 3개) ↔ long 키. 축마다 21비트 (±약 100만 칸).</summary>
    public static class VoxelKey
    {
        const int Bits = 21;
        const long Mask = (1L << Bits) - 1;
        const int Offset = 1 << (Bits - 1);

        public static long Pack(int x, int y, int z)
        {
            return ((long)(x + Offset) & Mask) | (((long)(y + Offset) & Mask) << Bits) | (((long)(z + Offset) & Mask) << (2 * Bits));
        }

        public static void Unpack(long key, out int x, out int y, out int z)
        {
            x = (int)(key & Mask) - Offset;
            y = (int)((key >> Bits) & Mask) - Offset;
            z = (int)((key >> (2 * Bits)) & Mask) - Offset;
        }

        public static long FromPoint(float x, float y, float z, float voxelSize)
        {
            float inv = 1f / voxelSize;
            return Pack((int)MathF.Floor(x * inv), (int)MathF.Floor(y * inv), (int)MathF.Floor(z * inv));
        }

        public static long FromPoint(Vector3 p, float voxelSize) => FromPoint(p.X, p.Y, p.Z, voxelSize);
    }

    /// <summary>복셀 집합. 아이패드의 VoxelFilter 가 "새 복셀만 통과" 시키는 데 쓴다.</summary>
    public sealed class VoxelSet
    {
        readonly HashSet<long> _set = new HashSet<long>();
        public float VoxelSize { get; }
        public int Count => _set.Count;

        public VoxelSet(float voxelSize)
        {
            if (voxelSize <= 0) throw new ArgumentOutOfRangeException(nameof(voxelSize));
            VoxelSize = voxelSize;
        }

        /// <summary>처음 보는 복셀이면 true.</summary>
        public bool TryAdd(Vector3 p) => _set.Add(VoxelKey.FromPoint(p, VoxelSize));
        public bool TryAdd(in PointXYZRGB p) => _set.Add(VoxelKey.FromPoint(p.X, p.Y, p.Z, VoxelSize));
        public void Clear() => _set.Clear();
    }

    /// <summary>
    /// 복셀 좌표를 키로 하는 해시 맵으로 점을 병합한다 (복셀 내 평균 위치·평균 색).
    /// PC 누적과 분석 전처리(복셀 다운샘플)에 같이 쓴다.
    /// </summary>
    public sealed class VoxelAccumulator
    {
        struct Acc
        {
            public double X, Y, Z;
            public int R, G, B;
            public int N;
        }

        readonly Dictionary<long, Acc> _map = new Dictionary<long, Acc>();
        public float VoxelSize { get; }
        public int Count => _map.Count;

        public VoxelAccumulator(float voxelSize)
        {
            if (voxelSize <= 0) throw new ArgumentOutOfRangeException(nameof(voxelSize));
            VoxelSize = voxelSize;
        }

        /// <summary>점을 더한다. 새 복셀이 생기면 true.</summary>
        public bool Add(in PointXYZRGB p)
        {
            long k = VoxelKey.FromPoint(p.X, p.Y, p.Z, VoxelSize);
            bool isNew = !_map.TryGetValue(k, out var a);
            a.X += p.X; a.Y += p.Y; a.Z += p.Z;
            a.R += p.R; a.G += p.G; a.B += p.B;
            a.N++;
            _map[k] = a;
            return isNew;
        }

        /// <summary>점들을 더하고 새로 생긴 복셀 수를 돌려준다.</summary>
        public int AddRange(IReadOnlyList<PointXYZRGB> points)
        {
            int added = 0;
            for (int i = 0; i < points.Count; i++) if (Add(points[i])) added++;
            return added;
        }

        public void Clear() => _map.Clear();

        public List<PointXYZRGB> ToPoints()
        {
            var list = new List<PointXYZRGB>(_map.Count);
            foreach (var a in _map.Values)
            {
                double inv = 1.0 / a.N;
                list.Add(new PointXYZRGB((float)(a.X * inv), (float)(a.Y * inv), (float)(a.Z * inv),
                    (byte)(a.R / a.N), (byte)(a.G / a.N), (byte)(a.B / a.N)));
            }
            return list;
        }

        public static List<PointXYZRGB> Downsample(IReadOnlyList<PointXYZRGB> points, float voxelSize)
        {
            var acc = new VoxelAccumulator(voxelSize);
            acc.AddRange(points);
            return acc.ToPoints();
        }
    }
}
