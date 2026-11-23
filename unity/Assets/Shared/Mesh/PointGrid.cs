using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 메쉬 다듬기·색 입히기용 점 격자. 점을 복셀로 줄여(복셀 안 평균 → 잡음도 줄어듦) 칸 단위로 정렬해 두고, 반경 안의 점을 빨리 찾는다.
    /// 색은 0..1 로 둔다. 점이 모두 흰색(255,255,255)이면 카메라 색이 없던 세션(원본 모드 옛 세션)으로 보고 HasColor = false.
    /// 만든 뒤에는 읽기만 하므로 여러 스레드에서 동시에 Query 해도 된다. (UnityEngine 비의존)
    /// </summary>
    public sealed class PointGrid
    {
        public readonly Vector3[] Positions;
        public readonly Vector3[] Colors;
        public bool HasColor { get; }
        public float CellSize { get; }
        public int Count => Positions.Length;

        readonly Dictionary<long, (int start, int count)> _cells = new Dictionary<long, (int, int)>();

        /// <param name="points">마커 좌표 점 (색 포함)</param>
        /// <param name="voxel">먼저 줄일 복셀 크기 (m). 0 이면 줄이지 않는다.</param>
        /// <param name="cellSize">찾기 칸 크기 (m). 보통 찾는 반경 정도.</param>
        public PointGrid(IReadOnlyList<PointXYZRGB> points, float voxel = 0.004f, float cellSize = 0.015f)
        {
            IReadOnlyList<PointXYZRGB> src = voxel > 0 ? VoxelAccumulator.Downsample(points, voxel) : points;
            CellSize = cellSize;
            int n = src.Count;
            var keys = new long[n];
            var order = new int[n];
            bool anyColor = false;
            for (int i = 0; i < n; i++)
            {
                var p = src[i];
                keys[i] = VoxelKey.FromPoint(p.X, p.Y, p.Z, cellSize);
                order[i] = i;
                if (p.R != 255 || p.G != 255 || p.B != 255) anyColor = true;
            }
            HasColor = anyColor;
            Array.Sort(keys, order);
            Positions = new Vector3[n];
            Colors = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var p = src[order[i]];
                Positions[i] = new Vector3(p.X, p.Y, p.Z);
                Colors[i] = new Vector3(p.R, p.G, p.B) * (1f / 255f);
            }
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && keys[j] == keys[i]) j++;
                _cells[keys[i]] = (i, j - i);
                i = j;
            }
        }

        /// <summary>p 에서 radius 안의 점 번호를 result 에 넣는다 (result 는 비우고 시작).</summary>
        public void Query(Vector3 p, float radius, List<int> result)
        {
            result.Clear();
            float inv = 1f / CellSize, r2 = radius * radius;
            int x0 = (int)MathF.Floor((p.X - radius) * inv), x1 = (int)MathF.Floor((p.X + radius) * inv);
            int y0 = (int)MathF.Floor((p.Y - radius) * inv), y1 = (int)MathF.Floor((p.Y + radius) * inv);
            int z0 = (int)MathF.Floor((p.Z - radius) * inv), z1 = (int)MathF.Floor((p.Z + radius) * inv);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!_cells.TryGetValue(VoxelKey.Pack(x, y, z), out var c)) continue;
                        for (int i = c.start, e = c.start + c.count; i < e; i++)
                            if (Vector3.DistanceSquared(Positions[i], p) <= r2) result.Add(i);
                    }
        }
    }
}
