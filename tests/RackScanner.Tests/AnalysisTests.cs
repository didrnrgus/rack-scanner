using System;
using System.Collections.Generic;
using System.Linq;
using RackScanner.Analysis;
using RackScanner.Shared.Geometry;
using RackScanner.Tools;
using Xunit;
using Xunit.Abstractions;

namespace RackScanner.Tests
{
    public class AnalysisTests
    {
        readonly ITestOutputHelper _out;
        public AnalysisTests(ITestOutputHelper output) { _out = output; }

        AnalysisResult Run(SyntheticScene scene, AnalysisSettings s = null)
        {
            var r = RackAnalyzer.Analyze(scene.Points, s);
            _out.WriteLine(r.Summary());
            return r;
        }

        static void AssertLevels(float[] expected, RackResult r, float tol = 0.08f)
        {
            Assert.Equal(expected.Length, r.LevelCount);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - r.LevelHeights[i]) <= tol, $"level {i}: expected ~{expected[i]}, got {r.LevelHeights[i]}");
        }

        [Fact]
        public void FloorIsFoundEvenWhenOffsetAndTilted()
        {
            var scene = new SyntheticScene(1) { FloorY = -0.8f };
            scene.AddFloor(-2, 2, -2, 2);
            scene.AddSolidBox(0, 0, 0, 0.5f, 0.5f, 0.5f);
            var r = Run(scene, new AnalysisSettings { DetectRacks = false });
            Assert.True(r.Floor.Found);
            Assert.Equal(-0.8f, r.Floor.C, 1);
        }

        /// <summary>M3: 책상·모니터·상자 같은 단순 물체를 박스로 (치수 오차 5cm 이내).</summary>
        [Fact]
        public void SimpleObjectsBecomeBoxes()
        {
            var scene = new SyntheticScene(2);
            scene.AddFloor(-1, 4, -1, 4);
            scene.AddSolidBox(0.5f, 0.5f, 0, 1.2f, 0.6f, 0.72f);   // 책상
            scene.AddSolidBox(2.5f, 2.5f, 30, 0.4f, 0.3f, 0.3f);   // 상자 (30° 돌아감)
            var r = Run(scene, new AnalysisSettings { DetectRacks = false });
            Assert.Equal(2, r.Objects.Count);
            var desk = r.Objects.OrderBy(o => Math.Abs(o.CenterX - 0.5f) + Math.Abs(o.CenterZ - 0.5f)).First();
            Assert.Equal(1.2f, desk.Length, 1);
            Assert.True(Math.Abs(desk.Length - 1.2f) < 0.05f && Math.Abs(desk.Width - 0.6f) < 0.05f && Math.Abs(desk.Height - 0.72f) < 0.05f,
                $"desk {desk.Length}x{desk.Width}x{desk.Height}");
            var box = r.Objects.First(o => o != desk);
            Assert.True(Math.Abs(box.Length - 0.4f) < 0.05f && Math.Abs(box.Width - 0.3f) < 0.05f, $"box {box.Length}x{box.Width}");
            float yawErr = Math.Abs(YawMath.NormalizeDeg((box.YawDeg - 30f) * 2) / 2); // 180° 대칭
            Assert.True(yawErr < 3f, $"box yaw {box.YawDeg}");
        }

        /// <summary>M4/B단계: 빈 랙에서 규격 없이 칸·층을 정확히.</summary>
        [Theory]
        [InlineData(0f, 3, 4)]
        [InlineData(90f, 4, 3)]
        [InlineData(33f, 2, 5)]
        public void EmptyRackAutoDetection(float yaw, int bays, int levels)
        {
            var p = new SyntheticScene.RackParams
            {
                OriginX = 1, OriginZ = 1, YawDeg = yaw, Bays = bays, BayWidth = 1.0f,
                LevelHeights = Enumerable.Range(0, levels).Select(i => 0.15f + 0.5f * i).ToArray(),
            };
            var scene = new SyntheticScene(3);
            scene.AddFloor(-1, 6, -1, 6).AddRack(p).AddOutliers(100, 5);
            var r = Run(scene);
            var rack = Assert.Single(r.Racks);
            Assert.Equal(bays, rack.BayCount);
            Assert.Equal(1.0f, rack.BayWidth, 1);
            AssertLevels(p.LevelHeights.Select(h => h + p.BeamHeight / 2).ToArray(), rack);
            Assert.Equal(0.6f, rack.Depth, 1);
            Assert.True(rack.Confidence > 0.9f, $"confidence {rack.Confidence}");
            Assert.Equal("auto", rack.Mode);
        }

        /// <summary>물류(상자)가 일부 칸을 채운 랙: B단계에서도 칸·층 수가 맞는지.</summary>
        [Fact]
        public void PartiallyLoadedRackAutoDetection()
        {
            var p = new SyntheticScene.RackParams { OriginX = 1, OriginZ = 1, Bays = 4, BayWidth = 1.2f, BoxFill = 0.5f };
            var scene = new SyntheticScene(4);
            scene.AddFloor(-1, 7, -1, 4).AddRack(p);
            var rack = Assert.Single(Run(scene).Racks);
            Assert.Equal(4, rack.BayCount);
            Assert.Equal(4, rack.LevelCount);
        }

        /// <summary>M5/A단계: 규격(칸 폭·층 간격·깊이)을 주면 위치와 칸·층 수를 맞춘다. 물류가 많이 가려도.</summary>
        [Fact]
        public void HeavilyLoadedRackWithSpec()
        {
            var p = new SyntheticScene.RackParams { OriginX = 2, OriginZ = 1, YawDeg = 15, Bays = 5, BayWidth = 1.2f, BoxFill = 0.9f };
            var scene = new SyntheticScene(5);
            scene.AddFloor(-1, 9, -1, 6).AddRack(p);
            var s = new AnalysisSettings { Spec = new RackSpec { BayWidth = 1.2f, Depth = 0.6f, LevelPitch = 0.6f } };
            var rack = Assert.Single(Run(scene, s).Racks);
            Assert.Equal("spec", rack.Mode);
            Assert.Equal(5, rack.BayCount);
            Assert.Equal(1.2f, rack.BayWidth);
            AssertLevels(p.LevelHeights.Select(h => h + p.BeamHeight / 2).ToArray(), rack);
            // 배치 위치가 실물과 겹치는지: 원점 모서리 오차 10cm 이내
            Assert.True(Math.Abs(rack.PoseX - 2f) < 0.1f && Math.Abs(rack.PoseZ - 1f) < 0.1f, $"pose ({rack.PoseX}, {rack.PoseZ})");
            Assert.Equal(15f, rack.YawDeg, 0);
        }

        [Fact]
        public void LevelHeightsFromSpecAreKeptAndMarkedObserved()
        {
            var p = new SyntheticScene.RackParams { OriginX = 1, OriginZ = 1, Bays = 2 };
            var scene = new SyntheticScene(6);
            scene.AddFloor(-1, 5, -1, 4).AddRack(p);
            var spec = new RackSpec { LevelHeights = p.LevelHeights.Select(h => h + 0.05f).ToList() };
            var rack = Assert.Single(Run(scene, new AnalysisSettings { Spec = spec }).Racks);
            Assert.Equal(spec.LevelHeights, rack.LevelHeights);
            Assert.All(rack.LevelObserved, Assert.True);
        }

        /// <summary>규칙성 보정: 가려진 층을 간격으로 추정하고 "추정"으로 표시.</summary>
        [Fact]
        public void RegularityFillsMissingLevel()
        {
            var levels = new List<float>();
            var obs = new List<bool>();
            RackStructureDetector.FillLevelsByRegularity(new List<float> { 0.2f, 0.8f, 2.0f, 2.6f }, levels, obs);
            Assert.Equal(5, levels.Count);
            Assert.Equal(1.4f, levels[2], 2);
            Assert.Equal(new[] { true, true, false, true, true }, obs);
        }

        /// <summary>이름 규칙: 원점에서 가까운 줄이 1열, 줄 안에서 원점에 가까운 랙부터.</summary>
        [Fact]
        public void RowsAreNumberedFromOrigin()
        {
            var scene = new SyntheticScene(7);
            scene.AddFloor(-1, 10, -1, 8);
            // 길이 방향 = +X (yaw 90). 줄은 Z 방향으로 떨어져 있음.
            scene.AddRack(new SyntheticScene.RackParams { OriginX = 1, OriginZ = 4.6f, YawDeg = 90, Bays = 2 });   // 2열
            scene.AddRack(new SyntheticScene.RackParams { OriginX = 1, OriginZ = 1.6f, YawDeg = 90, Bays = 2 });   // 1열, 가까운 쪽
            scene.AddRack(new SyntheticScene.RackParams { OriginX = 5, OriginZ = 1.6f, YawDeg = 90, Bays = 2 });   // 1열, 먼 쪽
            var r = Run(scene);
            Assert.Equal(3, r.Racks.Count);
            var r1 = r.Racks[0]; var r2 = r.Racks[1]; var r3 = r.Racks[2];
            Assert.Equal((1, 1), (r1.Row, r1.IndexInRow));
            Assert.Equal((1, 2), (r2.Row, r2.IndexInRow));
            Assert.Equal((2, 1), (r3.Row, r3.IndexInRow));
            Assert.True(r1.PoseX < r2.PoseX);
            Assert.True(r3.PoseZ > r1.PoseZ + 1.5f);
            Assert.Equal("R01", r1.RackId);
            // 1번 칸 쪽 끝이 원점에 가까운 쪽 → 길이 방향은 +X
            Assert.Equal(90f, r1.YawDeg, 0);
        }

        [Fact]
        public void ResultJsonContainsDocumentedFields()
        {
            var scene = new SyntheticScene(8);
            scene.AddFloor(-1, 5, -1, 4).AddRack(new SyntheticScene.RackParams { OriginX = 1, OriginZ = 1, Bays = 2 });
            var json = Run(scene).ToJson();
            var root = (Dictionary<string, object>)RackScanner.Shared.IO.JsonParser.Parse(json);
            var rack = (Dictionary<string, object>)((List<object>)root["racks"])[0];
            foreach (var k in new[] { "rackId", "row", "bayCount", "levelCount", "bayWidth", "levelHeights", "depth", "pose", "confidence" })
                Assert.True(rack.ContainsKey(k), k);
        }
    }
}
