using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using RackScanner.Receiver.Net;
using RackScanner.Receiver.Session;
using RackScanner.Scanner.Net;
using RackScanner.Scanner.Storage;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;
using RackScanner.Shared.Net;
using RackScanner.Shared.Protocol;
using Xunit;

namespace RackScanner.Tests
{
    /// <summary>메쉬 블록 전송(v2), 저장, 합치기, 적응형 단순화, OBJ 입출력.</summary>
    public class MeshTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-mesh-tests-" + Guid.NewGuid().ToString("N"));

        public MeshTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ---------- 테스트용 메쉬 ----------

        /// <summary>XZ 평면 격자 (y = height). n×n 칸, 칸 크기 step. 위에서 볼 때 앞면 (Unity 시계 방향).</summary>
        static IndexedMesh Grid(int n, float step, float height = 0, float x0 = 0, float z0 = 0)
        {
            var m = new IndexedMesh();
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                    m.Vertices.Add(new Vector3(x0 + i * step, height, z0 + j * step));
            int W = n + 1;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * W + i, b = a + 1, c = a + W, d = c + 1;
                    // Unity(왼손) 에서 +Y 를 보는 시계 방향: a → c → b, b → c → d
                    m.Indices.AddRange(new[] { a, c, b, b, c, d });
                }
            return m;
        }

        /// <summary>UV 구 (반지름 r). 바깥이 앞면.</summary>
        static IndexedMesh Sphere(float r, int rings, int segs)
        {
            var m = new IndexedMesh();
            for (int i = 0; i <= rings; i++)
            {
                double th = Math.PI * i / rings;
                for (int j = 0; j < segs; j++)
                {
                    double ph = 2 * Math.PI * j / segs;
                    m.Vertices.Add(new Vector3((float)(r * Math.Sin(th) * Math.Cos(ph)), (float)(r * Math.Cos(th)), (float)(r * Math.Sin(th) * Math.Sin(ph))));
                }
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = i * segs + j, b = i * segs + (j + 1) % segs, c = a + segs, d = b + segs;
                    if (i > 0) m.Indices.AddRange(new[] { a, b, c });
                    if (i < rings - 1) m.Indices.AddRange(new[] { b, d, c });
                }
            // 극점의 겹친 정점을 합친다
            return IndexedMesh.Combine(new[] { ((IReadOnlyList<Vector3>)m.Vertices, (IReadOnlyList<int>)m.Indices) }, 1e-5f);
        }

        static Vector3 FaceNormal(IndexedMesh m, int t)
        {
            var p = m.Vertices[m.Indices[3 * t]];
            return Vector3.Normalize(Vector3.Cross(m.Vertices[m.Indices[3 * t + 1]] - p, m.Vertices[m.Indices[3 * t + 2]] - p));
        }

        static MeshBlockMessage Block(uint id, uint rev, IndexedMesh m) => new MeshBlockMessage
        {
            BlockId = id, Revision = rev, Time = rev, Vertices = m.Vertices.ToArray(), Indices = m.Indices.ToArray(),
        };

        // ---------- 프로토콜 ----------

        static IMessage RoundTrip(IMessage msg)
        {
            var bytes = MessageCodec.Encode(msg);
            using var ms = new MemoryStream(bytes);
            Assert.True(MessageCodec.TryReadFrame(ms, new byte[ProtocolConstants.HeaderSize], out var frame));
            return MessageCodec.Decode(frame);
        }

        [Fact]
        public void MeshMessagesRoundTrip()
        {
            var g = Grid(3, 0.1f, 0.5f);
            var b = Assert.IsType<MeshBlockMessage>(RoundTrip(Block(7, 3, g)));
            Assert.Equal(7u, b.BlockId);
            Assert.Equal(3u, b.Revision);
            Assert.Equal(g.Vertices.ToArray(), b.Vertices);
            Assert.Equal(g.Indices.ToArray(), b.Indices);

            var removal = Assert.IsType<MeshBlockMessage>(RoundTrip(new MeshBlockMessage { BlockId = 9, Revision = uint.MaxValue }));
            Assert.True(removal.IsRemoval);

            var ack = Assert.IsType<MeshAckMessage>(RoundTrip(new MeshAckMessage { BlockId = 7, Revision = 3 }));
            Assert.Equal(7u, ack.BlockId);
            Assert.Equal(3u, ack.Revision);
        }

        [Fact]
        public void MeshBlockWithBadIndexIsRejected()
        {
            var bad = new MeshBlockMessage { BlockId = 1, Revision = 1, Vertices = new Vector3[3], Indices = new[] { 0, 1, 3 } };
            Assert.Throws<ProtocolException>(() => RoundTrip(bad));
            var notTriangles = new MeshBlockMessage { BlockId = 1, Revision = 1, Vertices = new Vector3[3], Indices = new[] { 0, 1 } };
            Assert.Throws<ProtocolException>(() => RoundTrip(notTriangles));
        }

        /// <summary>v2 는 메시지를 추가만 했으므로 v1 으로 저장된 프레임도 저장 파일에서는 읽힌다. 네트워크(Decode)는 거절.</summary>
        [Fact]
        public void StoredV1FramesStillDecode()
        {
            var bytes = MessageCodec.Encode(new EndScanMessage { TotalChunks = 5 });
            bytes[4] = 1; bytes[5] = 0; // 헤더 version = 1
            using var ms = new MemoryStream(bytes);
            Assert.True(MessageCodec.TryReadFrame(ms, new byte[ProtocolConstants.HeaderSize], out var frame));
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(frame));
            Assert.Equal(5u, Assert.IsType<EndScanMessage>(MessageCodec.DecodeStored(frame)).TotalChunks);
        }

        // ---------- 전송 추적 ----------

        [Fact]
        public void PendingMeshStoreKeepsOnlyLatestRevision()
        {
            var s = new PendingMeshStore();
            s.Put(1, 1, new byte[] { 1 });
            s.Put(2, 1, new byte[] { 2 });
            Assert.True(s.TryTakeUnsent(1, out var id, out _)); Assert.Equal(1u, id);
            s.Put(1, 2, new byte[] { 3 }); // 보낸 뒤 새 판 → 다시 보내야 함
            Assert.False(s.Ack(1, 1));      // 옛 판 ACK 로는 지우지 않는다
            Assert.Equal(2, s.PendingCount);
            s.Put(1, 1, new byte[] { 9 }); // 옛 판은 무시
            Assert.True(s.TryTakeUnsent(1, out id, out var f)); Assert.Equal(1u, id); Assert.Equal(3, f[0]);
            Assert.True(s.TryTakeUnsent(1, out id, out _)); Assert.Equal(2u, id);
            Assert.False(s.HasUnsent(1));
            Assert.True(s.HasUnsent(2)); // 재연결하면 남은 것 전부 다시
            Assert.True(s.Ack(1, 2));
            Assert.True(s.Ack(2, 1));
            Assert.Equal(0, s.PendingCount);
            Assert.Equal(2, s.AckedCount);
        }

        [Fact]
        public void MeshReceiptTrackerIgnoresOldRevisions()
        {
            var t = new MeshReceiptTracker();
            Assert.True(t.MarkReceived(1, 1));
            Assert.False(t.MarkReceived(1, 1));
            Assert.True(t.MarkReceived(1, 3));
            Assert.False(t.MarkReceived(1, 2));
            Assert.True(t.MarkReceived(2, 1));
            Assert.Equal(2, t.BlockCount);
        }

        static List<ServerEvent> Drain(ScanServer server, Func<List<ServerEvent>, bool> until, int timeoutMs = 10000)
        {
            var events = new List<ServerEvent>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                while (server.TryDequeue(out var e)) events.Add(e);
                if (until(events)) return events;
                Thread.Sleep(10);
            }
            return events;
        }

        [Fact]
        public void MeshBlocksAndChunksAreDeliveredAndAcked()
        {
            using var server = new ScanServer(0);
            server.Start();
            using var sender = new NetSender("127.0.0.1", server.Port, "mesh-A", "test");
            sender.SetMarkerDef(new MarkerDefMessage { Marker = new MarkerDefinition { Id = "base" } });
            for (uint i = 0; i < 10; i++)
                sender.EnqueueChunk(new PointChunkMessage { ChunkIndex = i, Points = new[] { new PointXYZRGB(i, 0, 0) } });
            for (uint b = 1; b <= 20; b++) sender.EnqueueMesh(Block(b, 1, Grid(4, 0.05f, b)));
            sender.EnqueueMesh(Block(5, 2, Grid(2, 0.05f, 99))); // 보내기 전 새 판 → 옛 판은 안 보내도 됨
            sender.EndScan(10);
            sender.Start();

            var ev = Drain(server, e => e.Exists(x => x.Message is EndScanMessage));
            var session = new ScanSession(_dir, "mesh-A", "test", recordChunks: false);
            foreach (var e in ev) if (e.Kind == ServerEventKind.Message) session.Apply(e.Message, e.RawFrame);
            Assert.Equal(10, session.ReceivedChunks);
            Assert.Equal(20, session.MeshBlocks.Count);
            Assert.Equal(2u, session.MeshBlocks[5].Revision);
            Assert.Equal(99f, session.MeshBlocks[5].Vertices[0].Y);
            // END_SCAN 은 모든 청크·블록 뒤에 온다
            int endIdx = ev.FindIndex(x => x.Message is EndScanMessage);
            Assert.DoesNotContain(ev.GetRange(endIdx + 1, ev.Count - endIdx - 1), x => x.Message is MeshBlockMessage);

            var sw = Stopwatch.StartNew();
            while (!sender.IsTransferComplete && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Assert.True(sender.IsTransferComplete);
            Assert.Equal(0, sender.MeshPendingCount);
            session.Dispose();
        }

        [Fact]
        public void MeshBlocksSurviveReconnect()
        {
            using var server = new ScanServer(0);
            server.Start();
            using var sender = new NetSender("127.0.0.1", server.Port, "mesh-B", "test") { ReconnectDelay = TimeSpan.FromMilliseconds(100) };
            sender.Start();
            var all = new List<ServerEvent>();
            for (uint rev = 1; rev <= 3; rev++)
            {
                for (uint b = 1; b <= 30; b++) sender.EnqueueMesh(Block(b, rev, Grid(3, 0.05f, rev)));
                all.AddRange(Drain(server, e => e.Exists(x => x.Message is MeshBlockMessage), 3000));
                server.DropClient();
            }
            sender.EndScan(0);
            all.AddRange(Drain(server, e => e.Exists(x => x.Message is EndScanMessage), 15000));

            var session = new ScanSession(_dir, "mesh-B", "test", recordChunks: false);
            foreach (var e in all) if (e.Kind == ServerEventKind.Message) session.Apply(e.Message, e.RawFrame);
            Assert.Equal(30, session.MeshBlocks.Count);
            foreach (var b in session.MeshBlocks.Values) Assert.Equal(3u, b.Revision);
            Assert.True(sender.ConnectCount >= 2, "should have reconnected");
            session.Dispose();
        }

        // ---------- 저장 ----------

        [Fact]
        public void LocalStoreKeepsLatestMeshAndCompacts()
        {
            using (var store = new LocalStore(_dir, "s1"))
            {
                store.Append(new MarkerDefMessage { Marker = new MarkerDefinition { Id = "base" } });
                for (uint rev = 1; rev <= 5; rev++)
                    for (uint b = 1; b <= 3; b++) store.AppendMesh(Block(b, rev, Grid(2, 0.1f, rev)));
                store.AppendMesh(new MeshBlockMessage { BlockId = 2, Revision = uint.MaxValue }); // 삭제
                long before = store.MeshFileBytes;

                var mid = LocalStore.Read(Path.Combine(_dir, "s1"));
                Assert.Equal(new uint[] { 1, 3 }, new List<uint>(mid.MeshBlocks.Keys));
                Assert.Equal(5u, mid.MeshBlocks[1].Revision);

                store.CompactMesh(mid.MeshBlocks.Values);
                Assert.True(store.MeshFileBytes < before / 3);
                store.AppendMesh(Block(4, 1, Grid(1, 0.1f))); // 정리 뒤에도 이어 쓰기
            }
            var s = LocalStore.Read(Path.Combine(_dir, "s1"));
            Assert.Equal(new uint[] { 1, 3, 4 }, new List<uint>(s.MeshBlocks.Keys));
            Assert.Equal(5f, s.MeshBlocks[3].Vertices[0].Y);
            Assert.NotNull(s.MarkerDef);
        }

        [Fact]
        public void ScanSessionSavesMergedMeshAsObj()
        {
            var session = new ScanSession(_dir, "s2", "test", recordChunks: false);
            Assert.True(session.ApplyMesh(Block(1, 1, Grid(2, 0.5f))));
            Assert.True(session.ApplyMesh(Block(2, 1, Grid(2, 0.5f, 0, 1.0f))));  // 옆 블록, 경계 정점 공유 위치
            Assert.False(session.ApplyMesh(Block(1, 1, Grid(1, 0.5f))));          // 같은 판은 무시
            session.Save();
            session.Dispose();
            var m = ScanSession.LoadMesh(session.SessionDir);
            Assert.Equal(16, m.TriangleCount);
            Assert.Equal(15, m.VertexCount); // 3×3 + 3×3 - 경계 3개 공유
            var meta = SessionMeta.Load(session.SessionDir);
            Assert.Equal(2, meta.MeshBlockCount);
            Assert.Equal(16, meta.MeshTriangleCount);
        }

        // ---------- 합치기 / OBJ ----------

        [Fact]
        public void CombineWeldsBlockBoundaries()
        {
            var a = Grid(2, 0.5f);
            var b = Grid(2, 0.5f, 0, 1.0f + 0.0002f); // 0.2mm 어긋난 경계
            var m = IndexedMesh.Combine(new[] { Block(1, 1, a), Block(2, 1, b) }, 0.001f);
            Assert.Equal(15, m.VertexCount);
            Assert.Equal(16, m.TriangleCount);
            Assert.Equal(2.0f, m.SurfaceArea(), 2);
        }

        [Fact]
        public void ObjRoundTripPreservesUnityCoordinatesAndWinding()
        {
            var g = Grid(2, 0.25f, 1.5f, -1, 2);
            string path = Path.Combine(_dir, "t.obj");
            ObjIO.Write(path, g, "test");
            var text = File.ReadAllText(path);
            Assert.Contains("v 1 1.5 2", text); // X 를 뒤집어 쓴다 (첫 정점 x = -1)

            var back = ObjIO.Read(path);
            Assert.Equal(g.Vertices, back.Vertices);
            Assert.Equal(g.TriangleCount, back.TriangleCount);
            for (int t = 0; t < g.TriangleCount; t++)
                Assert.True(Vector3.Dot(FaceNormal(g, t), FaceNormal(back, t)) > 0.99f, "winding must survive the round trip");
        }

        [Fact]
        public void ObjFileIsRightHandedCounterClockwise()
        {
            // Unity 에서 +Y 를 보는 앞면(시계 방향) → OBJ(오른손, 반시계)에서도 +Y 법선이어야 Blender 등에서 바르게 보인다
            var g = Grid(1, 1f);
            var sw = new StringWriter();
            ObjIO.Write(sw, g);
            var verts = new List<Vector3>();
            foreach (var line in sw.ToString().Split('\n'))
            {
                var p = line.Split(' ');
                if (p[0] == "v") verts.Add(new Vector3(float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3])));
                if (p[0] == "f")
                {
                    var a = verts[int.Parse(p[1]) - 1]; var b = verts[int.Parse(p[2]) - 1]; var c = verts[int.Parse(p[3]) - 1];
                    var n = Vector3.Cross(b - a, c - a); // 오른손 규칙
                    Assert.True(n.Y > 0, "OBJ face should face +Y");
                }
            }
            // Unity 쪽: 왼손에서 시계 방향 앞면 = Cross(b-a, c-a) 가 앞면 법선과 같은 방향
            Assert.True(FaceNormal(g, 0).Y > 0);
        }

        // ---------- 적응형 단순화 ----------

        [Fact]
        public void FlatFloorCollapsesToFewTriangles()
        {
            var g = Grid(50, 0.02f); // 1m × 1m, 5000 삼각형 (ARKit 수준의 2cm 격자)
            var s = MeshSimplifier.Simplify(g, new SimplifyOptions { MaxError = 0.002f });
            Assert.True(s.TriangleCount <= 20, $"flat floor should collapse, got {s.TriangleCount}");
            Assert.Equal(1.0f, s.SurfaceArea(), 3);   // 가장자리가 줄어들지 않는다
            foreach (var v in s.Vertices) Assert.Equal(0f, v.Y, 5);
            for (int t = 0; t < s.TriangleCount; t++) Assert.True(FaceNormal(s, t).Y > 0.99f, "no flipped faces");
        }

        [Fact]
        public void CurvedSurfaceKeepsDetailWithinError()
        {
            const float r = 0.3f, err = 0.002f;
            var sp = Sphere(r, 40, 80);
            var s = MeshSimplifier.Simplify(sp, new SimplifyOptions { MaxError = err });
            Assert.True(s.TriangleCount < sp.TriangleCount, "some reduction expected");
            Assert.True(s.TriangleCount > sp.TriangleCount / 20, $"curved surface must keep detail, got {s.TriangleCount}/{sp.TriangleCount}");
            foreach (var v in s.Vertices) Assert.InRange(v.Length(), r - 2 * err, r + 2 * err);
            for (int t = 0; t < s.TriangleCount; t++)
            {
                var c = (s.Vertices[s.Indices[3 * t]] + s.Vertices[s.Indices[3 * t + 1]] + s.Vertices[s.Indices[3 * t + 2]]) / 3;
                Assert.True(Vector3.Dot(FaceNormal(s, t), Vector3.Normalize(c)) > 0, "faces keep pointing outward");
            }
        }

        /// <summary>
        /// LiDAR 잡음(±1.5mm)이 있는 바닥 + 4cm 각 기둥: 잡음 제거 덕분에 바닥은 크게 합쳐지고, 기둥(얇은 형상)은 높이·굵기가 남는다.
        /// 잡음 제거를 끄면 기울어진 작은 면 때문에 바닥이 잘 합쳐지지 않는다.
        /// </summary>
        [Fact]
        public void NoisyFloorCollapsesButThinPostSurvives()
        {
            var parts = new List<(IReadOnlyList<Vector3>, IReadOnlyList<int>)>();
            void Add(IndexedMesh m) => parts.Add((m.Vertices, m.Indices));
            Add(Grid(60, 0.02f, 0, -0.6f, -0.6f));
            // 기둥 옆면 4개 (0.04 × 1.0m), 2cm 격자, 바깥이 앞면
            IndexedMesh Side(Vector3 o, Vector3 u, Vector3 v)
            {
                var m = new IndexedMesh();
                int nu = (int)MathF.Round(u.Length() / 0.02f), nvv = (int)MathF.Round(v.Length() / 0.02f);
                for (int j = 0; j <= nvv; j++) for (int i = 0; i <= nu; i++) m.Vertices.Add(o + u * (i / (float)nu) + v * (j / (float)nvv));
                int W = nu + 1;
                for (int j = 0; j < nvv; j++) for (int i = 0; i < nu; i++) { int a = j * W + i; m.Indices.AddRange(new[] { a, a + 1, a + W, a + 1, a + W + 1, a + W }); }
                return m;
            }
            var X = new Vector3(0.04f, 0, 0); var Y = new Vector3(0, 1, 0); var Z = new Vector3(0, 0, 0.04f);
            var p0 = new Vector3(0.1f, 0, 0.1f);
            Add(Side(p0 + Z, X, Y)); Add(Side(p0, Y, X)); Add(Side(p0 + X, Y, Z)); Add(Side(p0, Z, Y));
            Add(Side(p0 + Y, Z, X)); // 윗면
            var scene = IndexedMesh.Combine(parts, 0.001f);
            var rng = new Random(3);
            for (int i = 0; i < scene.Vertices.Count; i++)
                scene.Vertices[i] += new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.003f;

            var withDenoise = MeshSimplifier.Simplify(scene, new SimplifyOptions { MaxError = 0.005f });
            var without = MeshSimplifier.Simplify(scene, new SimplifyOptions { MaxError = 0.005f, DenoiseIterations = 0 });
            Assert.True(withDenoise.TriangleCount < scene.TriangleCount / 20, $"denoised: {withDenoise.TriangleCount}/{scene.TriangleCount}");
            Assert.True(without.TriangleCount > withDenoise.TriangleCount * 3, $"without denoise {without.TriangleCount} vs {withDenoise.TriangleCount}");

            // 기둥: 꼭대기 높이와 굵기가 유지된다
            float top = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
            foreach (var v in withDenoise.Vertices)
            {
                top = MathF.Max(top, v.Y);
                if (v.Y > 0.5f) { minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X); }
            }
            Assert.InRange(top, 1.0f - 0.005f, 1.0f + 0.005f);
            Assert.InRange(maxX - minX, 0.04f - 0.006f, 0.04f + 0.006f);
            // 바닥은 잡음 폭 안에 평평하다
            foreach (var v in withDenoise.Vertices) if (v.Y < 0.5f && MathF.Abs(v.X - 0.12f) > 0.05f) Assert.InRange(v.Y, -0.003f, 0.003f);
        }

        /// <summary>넓은 바닥 위에 작은 상자: 바닥은 거의 다 합쳐지고, 상자 모서리 주변은 남는다 (형상 적응).</summary>
        [Fact]
        public void DensityFollowsComplexity()
        {
            var floor = Grid(60, 0.02f, 0, -0.6f, -0.6f); // 1.2m, 7200 삼각형
            // 바닥 한가운데에 높이 3cm 의 울퉁불퉁한 언덕 (복잡한 곳)
            for (int i = 0; i < floor.Vertices.Count; i++)
            {
                var v = floor.Vertices[i];
                float d = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
                if (d < 0.15f) floor.Vertices[i] = new Vector3(v.X, 0.03f * MathF.Cos(d / 0.15f * MathF.PI / 2) * (1 + 0.3f * MathF.Sin(v.X * 60)), v.Z);
            }
            var s = MeshSimplifier.Simplify(floor, new SimplifyOptions { MaxError = 0.002f });
            int bump = 0, flat = 0;
            for (int t = 0; t < s.TriangleCount; t++)
            {
                var c = (s.Vertices[s.Indices[3 * t]] + s.Vertices[s.Indices[3 * t + 1]] + s.Vertices[s.Indices[3 * t + 2]]) / 3;
                if (MathF.Sqrt(c.X * c.X + c.Z * c.Z) < 0.15f) bump++; else flat++;
            }
            // 언덕 넓이는 전체의 ~5% 인데 삼각형은 대부분 언덕에 남는다
            Assert.True(bump > flat, $"bump {bump} should keep more triangles than the wide flat area {flat}");
            Assert.True(s.TriangleCount < floor.TriangleCount / 3, $"overall reduction expected, got {s.TriangleCount}");
        }
    }
}
