using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using RackScanner.Scanner.Storage;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.IO;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using Xunit;

namespace RackScanner.Tests
{
    public class IoTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-tests-" + Guid.NewGuid().ToString("N"));
        public IoTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void PlyBinaryRoundTrip()
        {
            var pts = new List<PointXYZRGB> { new PointXYZRGB(1, 2, 3, 4, 5, 6), new PointXYZRGB(-0.5f, 0.25f, 1e3f, 255, 0, 128) };
            var path = Path.Combine(_dir, "a.ply");
            PlyIO.Write(path, pts);
            Assert.Equal(pts, PlyIO.Read(path));
        }

        [Fact]
        public void PlyAsciiWithExtraPropertiesIsRead()
        {
            var ply = "ply\nformat ascii 1.0\nelement vertex 2\nproperty double x\nproperty double y\nproperty double z\nproperty float nx\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nelement face 0\nproperty list uchar int vertex_indices\nend_header\n" +
                      "1 2 3 0.5 10 20 30\n-1 -2 -3 0.1 1 2 3\n";
            var pts = PlyIO.Read(new MemoryStream(Encoding.ASCII.GetBytes(ply)));
            Assert.Equal(2, pts.Count);
            Assert.Equal(new PointXYZRGB(1, 2, 3, 10, 20, 30), pts[0]);
            Assert.Equal(-3f, pts[1].Z);
        }

        [Fact]
        public void SessionMetaJsonRoundTrip()
        {
            var m = new SessionMeta
            {
                SessionId = "2026-11-18_001",
                DeviceInfo = "iPad \"Pro\"",
                UsedMarkerId = "base",
                TotalChunks = 12,
                PointCount = 3456,
                Marker = new MarkerDefinition { Id = "base", PrintedWidth = 0.198f, PrintedHeight = 0.201f, Position = new Vector3(1, 0, 2), Rotation = YawMath.RotationFromYaw(30) },
                FinalTransform = new RigidTransform(YawMath.RotationFromYaw(-12), new Vector3(10, 0, -5), 1.01f),
            };
            m.Save(_dir);
            var r = SessionMeta.Load(_dir);
            Assert.Equal(m.SessionId, r.SessionId);
            Assert.Equal(m.DeviceInfo, r.DeviceInfo);
            Assert.Equal(12, r.TotalChunks);
            Assert.Equal(0.198f, r.Marker.PrintedWidth, 5);
            Assert.True(Vector3.Distance(m.Marker.Position, r.Marker.Position) < 1e-5f);
            Assert.True(QuaternionMath.AngleDeg(m.Marker.Rotation, r.Marker.Rotation) < 0.01f);
            Assert.Equal(1.01f, r.FinalTransform.Scale, 5);
            Assert.True(Vector3.Distance(m.FinalTransform.Translation, r.FinalTransform.Translation) < 1e-5f);
        }

        [Fact]
        public void JsonParserHandlesNesting()
        {
            var o = (Dictionary<string, object>)JsonParser.Parse("{\"a\": [1, 2.5, -3e2], \"b\": {\"c\": \"x\\\"y\\u0041\"}, \"d\": true, \"e\": null}");
            Assert.Equal(-300.0, ((List<object>)o["a"])[2]);
            Assert.Equal("x\"yA", o.GetObject("b").GetString("c"));
            Assert.Equal(true, o["d"]);
            Assert.Null(o["e"]);
        }

        [Fact]
        public void LocalStoreWritesAndReadsBackIncludingTruncatedTail()
        {
            using (var store = new LocalStore(_dir, "s1"))
            {
                store.Append(new MarkerDefMessage());
                store.Append(new AlignedMessage { MarkerId = "base", FrameCount = 30 });
                for (uint i = 0; i < 5; i++)
                    store.Append(new PointChunkMessage { ChunkIndex = i, Points = new[] { new PointXYZRGB(i, 0, 0) } });
                store.Append(new EndScanMessage { TotalChunks = 5 });
                store.LogAlignment(new AlignmentLogEntry { MarkerId = "base", Distance = 0.7f, Used = true });
            }
            var s = LocalStore.Read(Path.Combine(_dir, "s1"));
            Assert.Equal(5, s.Chunks.Count);
            Assert.Equal(5u, s.EndScan.TotalChunks);
            Assert.Equal(30, s.Aligned.FrameCount);
            Assert.False(s.Truncated);
            Assert.Contains("s1", LocalStore.ListSessions(_dir));
            Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "s1", SessionMeta.AlignmentLogFileName)).Length);

            // 앱이 청크를 쓰다 죽은 경우: 마지막 프레임이 잘려 있어도 앞부분은 살린다
            var path = Path.Combine(_dir, "s1", SessionMeta.ChunksFileName);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^7]);
            var t = LocalStore.Read(Path.Combine(_dir, "s1"));
            Assert.True(t.Truncated);
            Assert.Equal(5, t.Chunks.Count);
            Assert.Null(t.EndScan);
        }

        [Fact]
        public void ChunkBuilderSplitsBySizeAndTime()
        {
            var b = new ChunkBuilder { MaxPoints = 3, MaxInterval = 1.0 };
            for (int i = 0; i < 7; i++) b.Add(new PointXYZRGB(i, 0, 0), 0.1);
            Assert.True(b.TryBuild(0.2, false, out var c0));
            Assert.Equal(0u, c0.ChunkIndex);
            Assert.Equal(3, c0.Points.Length);
            Assert.True(b.TryBuild(0.2, false, out var c1));
            Assert.Equal(1u, c1.ChunkIndex);
            Assert.False(b.TryBuild(0.3, false, out _));    // 1점 남음, 시간 안 지남
            Assert.True(b.TryBuild(1.5, false, out var c2)); // 시간 초과로 배출
            Assert.Single(c2.Points);
            Assert.Equal(3u, b.ChunkCount);
        }
    }
}
