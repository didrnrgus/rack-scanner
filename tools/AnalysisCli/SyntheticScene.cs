using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Model;

namespace RackScanner.Tools
{
    /// <summary>
    /// 테스트용 합성 점군 생성기: 바닥, 랙(기둥·빔), 물류 상자, 단순 물체.
    /// 좌표는 Unity 마커 좌표 (Y 위, m).
    /// </summary>
    public sealed class SyntheticScene
    {
        public readonly List<PointXYZRGB> Points = new List<PointXYZRGB>();
        readonly Random _rng;
        public float Spacing = 0.02f;
        public float Noise = 0.004f;
        public float FloorY = 0f;

        public SyntheticScene(int seed = 1) { _rng = new Random(seed); }

        public sealed class RackParams
        {
            public float OriginX, OriginZ, YawDeg;
            public int Bays = 3;
            public float BayWidth = 1.2f;
            public float Depth = 0.6f;
            public float[] LevelHeights = { 0.15f, 0.75f, 1.35f, 1.95f };
            public float UprightSize = 0.08f;
            public float BeamHeight = 0.1f;
            public float BeamThickness = 0.05f;
            /// <summary>칸·층마다 상자를 놓을 확률.</summary>
            public float BoxFill = 0f;
        }

        public SyntheticScene AddFloor(float minX, float maxX, float minZ, float maxZ, float spacing = 0.05f)
        {
            for (float x = minX; x <= maxX; x += spacing)
            for (float z = minZ; z <= maxZ; z += spacing)
                Add(new Vector3(x, FloorY, z), 120, 120, 120);
            return this;
        }

        public SyntheticScene AddRack(RackParams p)
        {
            float rad = p.YawDeg * (float)Math.PI / 180f;
            var dir = new Vector3((float)Math.Sin(rad), 0, (float)Math.Cos(rad));
            var perp = new Vector3((float)Math.Cos(rad), 0, -(float)Math.Sin(rad));
            var origin = new Vector3(p.OriginX, FloorY, p.OriginZ);
            Vector3 W(float u, float h, float v) => origin + dir * u + perp * v + new Vector3(0, h, 0);

            float top = p.LevelHeights[p.LevelHeights.Length - 1] + p.BeamHeight;
            float us = p.UprightSize;
            // 기둥: 칸 경계마다 앞·뒤
            for (int k = 0; k <= p.Bays; k++)
            {
                float u = us / 2 + k * p.BayWidth;
                foreach (float v in new[] { us / 2, p.Depth - us / 2 })
                    AddBox(W, u, top / 2, v, us, top, us, 230, 120, 30);
            }
            // 빔: 층마다 앞·뒤 (주황)
            foreach (float h in p.LevelHeights)
            {
                for (int k = 0; k < p.Bays; k++)
                {
                    float u = us / 2 + (k + 0.5f) * p.BayWidth;
                    foreach (float v in new[] { p.BeamThickness / 2, p.Depth - p.BeamThickness / 2 })
                        AddBox(W, u, h + p.BeamHeight / 2, v, p.BayWidth - us, p.BeamHeight, p.BeamThickness, 240, 100, 20);
                }
            }
            // 물류 상자
            for (int li = 0; li < p.LevelHeights.Length - 1; li++)
            {
                float baseH = p.LevelHeights[li] + p.BeamHeight;
                float clear = p.LevelHeights[li + 1] - baseH - 0.08f;
                for (int k = 0; k < p.Bays; k++)
                {
                    if (_rng.NextDouble() >= p.BoxFill) continue;
                    float bw = p.BayWidth * (0.7f + 0.2f * (float)_rng.NextDouble());
                    float bh = clear * (0.5f + 0.45f * (float)_rng.NextDouble());
                    float bd = p.Depth * 0.9f;
                    float u = us / 2 + (k + 0.5f) * p.BayWidth;
                    AddBox(W, u, baseH + bh / 2, p.Depth / 2, bw, bh, bd, 150, 110, 70);
                }
            }
            return this;
        }

        /// <summary>단순 물체 (책상 상판+다리, 상자 등)를 축 정렬 박스 하나로.</summary>
        public SyntheticScene AddSolidBox(float cx, float cz, float yawDeg, float length, float width, float height)
        {
            float rad = yawDeg * (float)Math.PI / 180f;
            var dir = new Vector3((float)Math.Sin(rad), 0, (float)Math.Cos(rad));
            var perp = new Vector3((float)Math.Cos(rad), 0, -(float)Math.Sin(rad));
            var c = new Vector3(cx, FloorY, cz);
            Vector3 W(float u, float h, float v) => c + dir * u + perp * v + new Vector3(0, h, 0);
            AddBox(W, 0, height / 2, 0, length, height, width, 90, 160, 200);
            return this;
        }

        public SyntheticScene AddOutliers(int count, float range)
        {
            for (int i = 0; i < count; i++)
                Add(new Vector3(R(-range, range), R(0, range), R(-range, range)), 255, 0, 255);
            return this;
        }

        void AddBox(Func<float, float, float, Vector3> W, float cu, float ch, float cv, float su, float sh, float sv, byte r, byte g, byte b)
        {
            float hu = su / 2, hh = sh / 2, hv = sv / 2;
            // 6면을 Spacing 격자로 샘플
            for (float a = -hu; a <= hu + 1e-4f; a += Spacing)
            for (float c = -hh; c <= hh + 1e-4f; c += Spacing)
            {
                Add(W(cu + a, ch + c, cv - hv), r, g, b);
                Add(W(cu + a, ch + c, cv + hv), r, g, b);
            }
            for (float a = -hv; a <= hv + 1e-4f; a += Spacing)
            for (float c = -hh; c <= hh + 1e-4f; c += Spacing)
            {
                Add(W(cu - hu, ch + c, cv + a), r, g, b);
                Add(W(cu + hu, ch + c, cv + a), r, g, b);
            }
            for (float a = -hu; a <= hu + 1e-4f; a += Spacing)
            for (float c = -hv; c <= hv + 1e-4f; c += Spacing)
            {
                Add(W(cu + a, ch - hh, cv + c), r, g, b);
                Add(W(cu + a, ch + hh, cv + c), r, g, b);
            }
        }

        void Add(Vector3 p, byte r, byte g, byte b)
        {
            p += new Vector3(Gauss() * Noise, Gauss() * Noise, Gauss() * Noise);
            Points.Add(new PointXYZRGB(p, r, g, b));
        }

        float R(float a, float b) => a + (b - a) * (float)_rng.NextDouble();

        float Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }
    }
}
