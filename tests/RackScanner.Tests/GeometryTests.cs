using System;
using System.Collections.Generic;
using System.Numerics;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;
using Xunit;

namespace RackScanner.Tests
{
    public class GeometryTests
    {
        static Quaternion Euler(float pitch, float yaw, float roll) =>
            Quaternion.CreateFromYawPitchRoll(yaw * YawMath.Deg2Rad, pitch * YawMath.Deg2Rad, roll * YawMath.Deg2Rad);

        [Fact]
        public void YawMatchesUnityConvention()
        {
            // Unity: Quaternion.Euler(0, 90, 0) * forward = right(+X)
            var q = YawMath.RotationFromYaw(90);
            var f = Vector3.Transform(Vector3.UnitZ, q);
            Assert.Equal(1f, f.X, 4);
            Assert.Equal(0f, f.Z, 4);
            Assert.Equal(90f, YawMath.YawOfDirection(f), 3);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(37f, 0f)]      // 바닥 마커
        [InlineData(-120f, 0f)]
        [InlineData(37f, 90f)]     // 벽 마커 (로컬 +Z 가 위를 향함)
        [InlineData(150f, -90f)]
        public void MarkerYawIsRecoveredForFloorAndWallMarkers(float yaw, float pitch)
        {
            var q = YawMath.RotationFromYaw(yaw) * Euler(-pitch, 0, 0);
            Assert.Equal(0f, YawMath.NormalizeDeg(YawMath.MarkerYaw(q) - yaw), 2);
        }

        [Fact]
        public void TransformComposeAndInverse()
        {
            var a = new RigidTransform(Euler(10, 20, 30), new Vector3(1, 2, 3), 1.5f);
            var b = new RigidTransform(Euler(-5, 40, 0), new Vector3(-1, 0, 4), 0.8f);
            var p = new Vector3(0.3f, -0.7f, 2f);
            var ab = a.Then(b);
            Assert.True(Vector3.Distance(b.Apply(a.Apply(p)), ab.Apply(p)) < 1e-4f);
            Assert.True(Vector3.Distance(a.Inverse().Apply(a.Apply(p)), p) < 1e-4f);
        }

        /// <summary>AR 세션 좌표가 Unity 좌표와 임의의 yaw·이동만큼 어긋나 있을 때, 마커 하나로 정확히 되돌리는지.</summary>
        [Theory]
        [InlineData(0f, 0f, 0f, 0f, 0f)]
        [InlineData(73f, 1.2f, -0.4f, 3.0f, 0f)]
        [InlineData(-160f, -2f, 0.1f, 0.5f, 90f)]
        public void AlignmentMapsArToUnity(float yawOffset, float tx, float ty, float tz, float markerPitch)
        {
            var marker = new MarkerDefinition { Position = new Vector3(0.5f, 0, -1f), Rotation = YawMath.RotationFromYaw(25) * Euler(-markerPitch, 0, 0) };
            // 진짜 Unity→AR 변환 (ARKit 은 중력 정렬이므로 yaw + 이동만)
            var unityToAr = new RigidTransform(YawMath.RotationFromYaw(yawOffset), new Vector3(tx, ty, tz));
            var obsPos = unityToAr.Apply(marker.Position);
            var obsRot = unityToAr.ApplyRotation(marker.Rotation);

            var arToUnity = MarkerAlignment.ComputeArToUnity(obsPos, obsRot, marker);
            var rng = new Random(3);
            for (int i = 0; i < 20; i++)
            {
                var pu = new Vector3((float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 3, (float)rng.NextDouble() * 10 - 5);
                var back = arToUnity.Apply(unityToAr.Apply(pu));
                Assert.True(Vector3.Distance(pu, back) < 1e-4f, $"{pu} -> {back}");
            }
        }

        /// <summary>마커 관측에 잡음이 있어도 여러 프레임 평균으로 yaw 2° 이내 (M1 통과 기준).</summary>
        [Fact]
        public void AveragedNoisyObservationsGiveYawWithin2Degrees()
        {
            var rng = new Random(11);
            var marker = new MarkerDefinition();
            var truth = new RigidTransform(YawMath.RotationFromYaw(40), new Vector3(1, 0, 2));
            var avg = new PoseAverager();
            for (int i = 0; i < 45; i++) // 1.5초 × 30fps
            {
                var noiseRot = Euler(G(rng) * 1.5f, G(rng) * 3f, G(rng) * 1.5f);
                var noisePos = new Vector3(G(rng), G(rng), G(rng)) * 0.005f;
                avg.Add(i / 30.0, truth.Apply(marker.Position) + noisePos, noiseRot * truth.ApplyRotation(marker.Rotation));
            }
            var est = MarkerAlignment.ComputeArToUnity(avg.MeanPosition(), avg.MeanRotation(), marker);
            MarkerAlignment.Difference(est, truth.Inverse(), out float dt, out float dyaw);
            Assert.True(dyaw < 2f, $"yaw error {dyaw}");
            Assert.True(dt < 0.03f, $"translation error {dt}");
        }

        [Fact]
        public void SmootherConvergesToTarget()
        {
            var s = new AlignmentSmoother();
            s.SetTarget(RigidTransform.Identity);
            var target = new RigidTransform(YawMath.RotationFromYaw(10), new Vector3(0.1f, 0, 0));
            s.SetTarget(target);
            Assert.Equal(Vector3.Zero, s.Current.Translation); // 즉시 덮어쓰지 않음
            for (int i = 0; i < 100; i++) s.Step(0.05f, 0.3f);
            MarkerAlignment.Difference(s.Current, target, out float dt, out float dyaw);
            Assert.True(dt < 1e-3f && dyaw < 0.1f);
        }

        [Fact]
        public void PoseAveragerDropsOldSamples()
        {
            var a = new PoseAverager();
            for (int i = 0; i < 10; i++) a.Add(i * 0.5, new Vector3(i, 0, 0), Quaternion.Identity);
            a.DropOlderThan(4.5, 1.0);
            Assert.Equal(3, a.Count); // 3.5, 4.0, 4.5
            Assert.Equal(8f, a.MeanPosition().X, 4);
        }

        [Fact]
        public void VoxelKeyRoundTripsIncludingNegatives()
        {
            foreach (var (x, y, z) in new[] { (0, 0, 0), (-1, -1, -1), (12345, -54321, 7), (-1048576, 1048575, 0) })
            {
                VoxelKey.Unpack(VoxelKey.Pack(x, y, z), out int a, out int b, out int c);
                Assert.Equal((x, y, z), (a, b, c));
            }
        }

        [Fact]
        public void VoxelSetPassesOnlyNewVoxels()
        {
            var set = new VoxelSet(0.05f);
            Assert.True(set.TryAdd(new Vector3(0.01f, 0.01f, 0.01f)));
            Assert.False(set.TryAdd(new Vector3(0.04f, 0.02f, 0.03f)));
            Assert.True(set.TryAdd(new Vector3(-0.01f, 0.01f, 0.01f)));
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void VoxelAccumulatorAveragesWithinVoxel()
        {
            var acc = new VoxelAccumulator(0.1f);
            Assert.True(acc.Add(new PointXYZRGB(0.01f, 0.01f, 0.01f, 0, 0, 0)));
            Assert.False(acc.Add(new PointXYZRGB(0.03f, 0.05f, 0.07f, 200, 100, 50)));
            var pts = acc.ToPoints();
            Assert.Single(pts);
            Assert.Equal(0.02f, pts[0].X, 4);
            Assert.Equal(0.04f, pts[0].Z, 4);
            Assert.Equal(100, pts[0].R);
        }

        static float G(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }
    }
}
