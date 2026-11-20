using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RackScanner.Shared.Net;
using RackScanner.Shared.Protocol;

namespace RackScanner.Receiver.Net
{
    public enum ServerEventKind { Connected, Rejected, Disconnected, Message }

    /// <summary>수신 스레드 → 메인 스레드로 넘기는 이벤트.</summary>
    public sealed class ServerEvent
    {
        public ServerEventKind Kind;
        public string SessionId;
        public string Remote;
        public string Text;           // Rejected / Disconnected 사유
        public IMessage Message;      // Kind == Message
        public byte[] RawFrame;       // POINT_CHUNK 등 원본 프레임 (chunks.bin 기록용)
        public HelloMessage Hello;    // Kind == Connected
    }

    /// <summary>
    /// PC 수신 서버. (UnityEngine 비의존: 테스트에서 직접 돌린다.)
    /// - TcpListener 스레드가 받은 메시지를 큐에 넣고, 메인 스레드가 <see cref="TryDequeue"/> 로 처리한다.
    /// - POINT_CHUNK 는 받는 즉시 수신 스레드에서 ACK 를 돌려보내고, 청크 번호로 중복을 무시한다.
    /// - MESH_BLOCK 은 받는 즉시 MESH_ACK 를 돌려보내고, 블록마다 가진 판보다 새 판만 넘긴다.
    /// - 한 번에 한 기기만 받는다. 새 연결이 오면 이전 연결을 끊는다 (와이파이 재연결 시 반쯤 죽은 소켓 정리).
    /// - HELLO 의 프로토콜 버전이 다르면 연결을 거절하고 Rejected 이벤트로 사유를 알린다.
    /// </summary>
    public sealed class ScanServer : IDisposable
    {
        public int Port { get; private set; }
        public bool IsClientConnected => _client != null;

        readonly ConcurrentQueue<ServerEvent> _events = new ConcurrentQueue<ServerEvent>();
        readonly Dictionary<string, ChunkReceiptTracker> _trackers = new Dictionary<string, ChunkReceiptTracker>();
        readonly Dictionary<string, MeshReceiptTracker> _meshTrackers = new Dictionary<string, MeshReceiptTracker>();
        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _stop;
        TcpClient _client;
        readonly object _clientLock = new object();

        public ScanServer(int port = ProtocolConstants.DefaultPort) { Port = port; }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port; // port 0 이면 실제 포트
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RackScanner.ScanServer.Accept" };
            _acceptThread.Start();
        }

        public bool TryDequeue(out ServerEvent e) => _events.TryDequeue(out e);

        /// <summary>세션별 수신 기록 (재연결해도 같은 세션 ID 면 이어서 쓴다).</summary>
        public ChunkReceiptTracker GetTracker(string sessionId)
        {
            lock (_trackers)
            {
                if (!_trackers.TryGetValue(sessionId, out var t)) _trackers[sessionId] = t = new ChunkReceiptTracker();
                return t;
            }
        }

        /// <summary>세션별 메쉬 블록 수신 기록.</summary>
        public MeshReceiptTracker GetMeshTracker(string sessionId)
        {
            lock (_meshTrackers)
            {
                if (!_meshTrackers.TryGetValue(sessionId, out var t)) _meshTrackers[sessionId] = t = new MeshReceiptTracker();
                return t;
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener?.Stop(); } catch { }
            lock (_clientLock) { try { _client?.Close(); } catch { } _client = null; }
            _acceptThread?.Join(1000);
        }

        /// <summary>현재 연결을 강제로 끊는다 (와이파이 끊김 시험용).</summary>
        public void DropClient()
        {
            lock (_clientLock) { try { _client?.Close(); } catch { } }
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch (Exception) { if (_stop) break; continue; }
                c.NoDelay = true;
                lock (_clientLock)
                {
                    if (_client != null) { try { _client.Close(); } catch { } }
                    _client = c;
                }
                var t = new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "RackScanner.ScanServer.Client" };
                t.Start();
            }
        }

        void ClientLoop(TcpClient c)
        {
            string remote = SafeRemote(c);
            string sessionId = null;
            string reason = "closed";
            var header = new byte[ProtocolConstants.HeaderSize];
            try
            {
                var stream = c.GetStream();
                // 첫 메시지는 HELLO 여야 한다. 버전은 디코딩 전에 헤더에서 먼저 검사한다.
                if (!MessageCodec.TryReadFrame(stream, header, out var first)) return;
                if (first.Header.Type != MessageType.Hello) { Reject(c, remote, "first message is not HELLO"); return; }
                if (first.Header.Version != ProtocolConstants.Version)
                {
                    Reject(c, remote, $"프로토콜 버전 불일치: 아이패드 v{first.Header.Version}, PC v{ProtocolConstants.Version}. 두 앱을 같은 커밋으로 빌드하세요.");
                    return;
                }
                var hello = (HelloMessage)MessageCodec.Decode(first);
                if (hello.ProtocolVersion != ProtocolConstants.Version)
                {
                    Reject(c, remote, $"프로토콜 버전 불일치: 아이패드 v{hello.ProtocolVersion}, PC v{ProtocolConstants.Version}.");
                    return;
                }
                sessionId = hello.SessionId;
                var tracker = GetTracker(sessionId);
                var meshTracker = GetMeshTracker(sessionId);
                _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Connected, SessionId = sessionId, Remote = remote, Hello = hello });

                while (!_stop)
                {
                    if (!MessageCodec.TryReadFrame(stream, header, out var frame)) break;
                    var msg = MessageCodec.Decode(frame);
                    if (msg is PointChunkMessage chunk)
                    {
                        // ACK 는 받는 즉시. 중복이어도 다시 보낸다 (이전 ACK 가 유실됐을 수 있음).
                        var ack = MessageCodec.Encode(new AckMessage { ChunkIndex = chunk.ChunkIndex });
                        lock (c) stream.Write(ack, 0, ack.Length);
                        if (!tracker.MarkReceived(chunk.ChunkIndex)) continue;
                        _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Message, SessionId = sessionId, Message = msg, RawFrame = Combine(header, frame.Payload) });
                    }
                    else if (msg is MeshBlockMessage block)
                    {
                        var ack = MessageCodec.Encode(new MeshAckMessage { BlockId = block.BlockId, Revision = block.Revision });
                        lock (c) stream.Write(ack, 0, ack.Length);
                        if (!meshTracker.MarkReceived(block.BlockId, block.Revision)) continue;
                        // 메쉬 원본은 아이패드 mesh.bin 이 기준이라 chunks.bin 에는 기록하지 않는다 (RawFrame 없음)
                        _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Message, SessionId = sessionId, Message = msg });
                    }
                    else
                    {
                        _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Message, SessionId = sessionId, Message = msg, RawFrame = Combine(header, frame.Payload) });
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is ProtocolException || e is InvalidOperationException)
            {
                reason = e.Message;
            }
            finally
            {
                lock (_clientLock) { if (_client == c) _client = null; }
                try { c.Close(); } catch { }
            }
            if (sessionId != null)
                _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Disconnected, SessionId = sessionId, Remote = remote, Text = reason });
        }

        void Reject(TcpClient c, string remote, string reason)
        {
            // TODO(protocol): 거절 사유를 아이패드에도 보여주려면 메시지 타입 추가가 필요 (프로토콜 변경 → 버전 상승). 지금은 PC 화면에만 표시하고 연결을 끊는다.
            _events.Enqueue(new ServerEvent { Kind = ServerEventKind.Rejected, Remote = remote, Text = reason });
            try { c.Close(); } catch { }
        }

        static byte[] Combine(byte[] header, byte[] payload)
        {
            var b = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, b, 0, header.Length);
            Buffer.BlockCopy(payload, 0, b, header.Length, payload.Length);
            return b;
        }

        static string SafeRemote(TcpClient c)
        {
            try { return c.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { return "?"; }
        }

        /// <summary>화면 표시용: 이 PC 의 IPv4 주소들 (아이패드에 입력할 주소).</summary>
        public static List<string> LocalIPv4Addresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) list.Add(ip.ToString());
            }
            catch { }
            return list;
        }
    }
}
