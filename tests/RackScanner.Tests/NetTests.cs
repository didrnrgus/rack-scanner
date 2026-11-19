using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using RackScanner.Receiver.Net;
using RackScanner.Scanner.Net;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using Xunit;

namespace RackScanner.Tests
{
    /// <summary>실제 TCP 루프백으로 아이패드 송신기와 PC 수신 서버를 맞물려 돌린다.</summary>
    public class NetTests
    {
        static PointChunkMessage Chunk(uint i) => new PointChunkMessage
        {
            ChunkIndex = i,
            KeyframeTime = i * 0.1,
            Points = new[] { new PointXYZRGB(i, 0, 0), new PointXYZRGB(i, 1, 0) },
        };

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

        static int ChunkEvents(List<ServerEvent> ev) => ev.FindAll(e => e.Message is PointChunkMessage).Count;

        [Fact]
        public void FullScanIsDeliveredInOrderWithAcks()
        {
            using var server = new ScanServer(0);
            server.Start();
            using var sender = new NetSender("127.0.0.1", server.Port, "sess-A", "test");
            sender.SetMarkerDef(new MarkerDefMessage { Marker = new MarkerDefinition { Id = "base" } });
            sender.SetAligned(new AlignedMessage { MarkerId = "base", FrameCount = 30 });
            for (uint i = 0; i < 50; i++) sender.EnqueueChunk(Chunk(i));
            sender.EndScan(50);
            sender.Start();

            var ev = Drain(server, e => e.Exists(x => x.Message is EndScanMessage));
            Assert.Equal(ServerEventKind.Connected, ev[0].Kind);
            Assert.Equal("sess-A", ev[0].SessionId);
            Assert.IsType<MarkerDefMessage>(ev[1].Message);
            Assert.IsType<AlignedMessage>(ev[2].Message);
            Assert.Equal(50, ChunkEvents(ev));
            Assert.True(server.GetTracker("sess-A").IsComplete(50));

            var sw = Stopwatch.StartNew();
            while (!sender.IsTransferComplete && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Assert.True(sender.IsTransferComplete);
            Assert.Equal(50, sender.AckedCount);
        }

        /// <summary>M2 통과 기준: 와이파이를 끊었다 이어도 누락 청크 0개 (중복도 0개).</summary>
        [Fact]
        public void DroppedConnectionResumesWithoutLossOrDuplicates()
        {
            using var server = new ScanServer(0);
            server.Start();
            using var sender = new NetSender("127.0.0.1", server.Port, "sess-B", "test") { ReconnectDelay = TimeSpan.FromMilliseconds(100) };
            sender.Start();

            var all = new List<ServerEvent>();
            for (uint i = 0; i < 100; i++)
            {
                sender.EnqueueChunk(Chunk(i));
                if (i == 30 || i == 70)
                {
                    all.AddRange(Drain(server, e => ChunkEvents(e) > 0, 3000));
                    server.DropClient(); // 끊김
                }
                if (i % 10 == 0) Thread.Sleep(5);
            }
            sender.EndScan(100);
            all.AddRange(Drain(server, e => e.Exists(x => x.Message is EndScanMessage) && ChunkEvents(e) + ChunkEvents(all) >= 100, 15000));

            var seen = new HashSet<uint>();
            foreach (var e in all)
                if (e.Message is PointChunkMessage pc) Assert.True(seen.Add(pc.ChunkIndex), $"duplicate chunk {pc.ChunkIndex}");
            Assert.Equal(100, seen.Count);
            Assert.Empty(server.GetTracker("sess-B").Missing(100));
            Assert.True(sender.ConnectCount >= 2, "should have reconnected");
        }

        [Fact]
        public void VersionMismatchIsRejectedWithReason()
        {
            using var server = new ScanServer(0);
            server.Start();
            using var client = new TcpClient("127.0.0.1", server.Port);
            var hello = MessageCodec.Encode(new HelloMessage { SessionId = "old" });
            hello[4] = (byte)(ProtocolConstants.Version + 1);
            client.GetStream().Write(hello, 0, hello.Length);

            var ev = Drain(server, e => e.Count > 0);
            Assert.Single(ev);
            Assert.Equal(ServerEventKind.Rejected, ev[0].Kind);
            Assert.Contains("버전", ev[0].Text);
        }
    }
}
