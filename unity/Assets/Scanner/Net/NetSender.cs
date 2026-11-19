using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using RackScanner.Shared.Net;
using RackScanner.Shared.Protocol;

namespace RackScanner.Scanner.Net
{
    /// <summary>
    /// 아이패드 → PC TCP 전송. (UnityEngine 비의존: 테스트에서 직접 돌린다.)
    /// - 청크는 ACK 를 받을 때까지 보관하고, 재연결하면 가장 작은 미확인 청크부터 다시 보낸다.
    /// - 연결마다 HELLO → MARKER_DEF → ALIGNED → 미확인 청크 → (있으면) END_SCAN 순서로 보낸다.
    /// - 미확인 청크가 있는데 AckTimeout 동안 ACK 가 없으면 연결이 죽은 것으로 보고 다시 연결한다
    ///   (와이파이가 끊기면 TCP 쓰기가 성공한 것처럼 보이는 경우가 있어서).
    /// 아이패드 로컬 원본(LocalStore)이 항상 기준이고, 이 전송은 실시간 확인용이다.
    /// </summary>
    public sealed class NetSender : IDisposable
    {
        public string Host { get; }
        public int Port { get; }
        public string SessionId { get; }
        public string DeviceInfo { get; }

        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
        public TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
        public TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

        public bool IsConnected => _connected;
        public int PendingCount => _pending.PendingCount;
        public int AckedCount => _pending.AckedCount;
        public string LastError { get; private set; } = "";
        public int ConnectCount { get; private set; }
        public bool EndScanQueued => _endScanFrame != null;
        /// <summary>END_SCAN 을 보냈고 모든 청크가 ACK 됨 = 전송 완료.</summary>
        public bool IsTransferComplete => _endScanFrame != null && _pending.PendingCount == 0 && _endScanSentOnce;

        readonly PendingChunkStore _pending = new PendingChunkStore();
        readonly ConcurrentQueue<byte[]> _control = new ConcurrentQueue<byte[]>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly object _metaLock = new object();
        byte[] _markerDefFrame, _alignedFrame;
        bool _markerDefDirty, _alignedDirty;
        volatile byte[] _endScanFrame;
        volatile bool _endScanSentOnce;
        volatile bool _connected;
        volatile bool _stop;
        long _lastAckTicks;
        Thread _worker;

        public NetSender(string host, int port, string sessionId, string deviceInfo)
        {
            Host = host; Port = port; SessionId = sessionId; DeviceInfo = deviceInfo;
        }

        public void Start()
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RackScanner.NetSender" };
            _worker.Start();
        }

        public void SetMarkerDef(MarkerDefMessage msg)
        {
            lock (_metaLock) { _markerDefFrame = MessageCodec.Encode(msg); _markerDefDirty = true; }
            _wake.Set();
        }

        public void SetAligned(AlignedMessage msg)
        {
            lock (_metaLock) { _alignedFrame = MessageCodec.Encode(msg); _alignedDirty = true; }
            _wake.Set();
        }

        public void EnqueueChunk(PointChunkMessage chunk) => EnqueueChunkFrame(chunk.ChunkIndex, MessageCodec.Encode(chunk));

        /// <summary>이미 인코딩된 청크 프레임 (LocalStore 에서 읽은 것 등)을 넣는다.</summary>
        public void EnqueueChunkFrame(uint index, byte[] frame)
        {
            _pending.Add(index, frame);
            _wake.Set();
        }

        /// <summary>STATE 는 연결돼 있을 때만 의미가 있어 재전송하지 않는다.</summary>
        public void SendState(ScanState state)
        {
            if (!_connected) return;
            _control.Enqueue(MessageCodec.Encode(new StateMessage { State = state }));
            _wake.Set();
        }

        public void EndScan(uint totalChunks)
        {
            _endScanFrame = MessageCodec.Encode(new EndScanMessage { TotalChunks = totalChunks });
            _wake.Set();
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _worker?.Join(2000);
        }

        void WorkerLoop()
        {
            while (!_stop)
            {
                try
                {
                    RunConnection();
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is TimeoutException || e is ProtocolException)
                {
                    LastError = e.Message;
                }
                catch (Exception e)
                {
                    LastError = e.GetType().Name + ": " + e.Message;
                }
                _connected = false;
                if (_stop) break;
                _wake.WaitOne(ReconnectDelay);
            }
        }

        void RunConnection()
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendTimeout = 5000;
                var connectTask = client.ConnectAsync(Host, Port);
                if (!connectTask.Wait(ConnectTimeout)) throw new TimeoutException($"connect to {Host}:{Port} timed out");
                if (connectTask.IsFaulted) throw connectTask.Exception.GetBaseException();

                var stream = client.GetStream();
                ConnectCount++;
                LastError = "";
                _connected = true;
                Interlocked.Exchange(ref _lastAckTicks, DateTime.UtcNow.Ticks);

                var reader = new Thread(() => ReaderLoop(stream, client)) { IsBackground = true, Name = "RackScanner.NetSender.Reader" };
                reader.Start();

                Write(stream, MessageCodec.Encode(new HelloMessage { SessionId = SessionId, DeviceInfo = DeviceInfo }));
                lock (_metaLock) { _markerDefDirty = _markerDefFrame != null; _alignedDirty = _alignedFrame != null; }

                long cursor = -1; // 이 연결에서 보낸 가장 큰 청크 번호
                bool endSent = false;
                while (!_stop && _connected)
                {
                    bool didWork = false;

                    byte[] meta = null;
                    lock (_metaLock)
                    {
                        if (_markerDefDirty) { meta = _markerDefFrame; _markerDefDirty = false; }
                        else if (_alignedDirty) { meta = _alignedFrame; _alignedDirty = false; }
                    }
                    if (meta != null) { Write(stream, meta); didWork = true; }

                    while (_control.TryDequeue(out var ctl)) { Write(stream, ctl); didWork = true; }

                    if (meta == null && _pending.TryGetNextAfter(cursor, out var idx, out var frame))
                    {
                        Write(stream, frame);
                        cursor = idx;
                        didWork = true;
                    }
                    else if (meta == null && !endSent && _endScanFrame != null && !_pending.TryGetNextAfter(cursor, out _, out _))
                    {
                        Write(stream, _endScanFrame);
                        endSent = true;
                        _endScanSentOnce = true;
                        didWork = true;
                    }

                    if (!didWork)
                    {
                        if (_pending.PendingCount > 0 && !_pending.TryGetNextAfter(cursor, out _, out _))
                        {
                            // 보낸 청크들의 ACK 를 기다리는 중. 너무 오래 없으면 연결을 다시 맺는다.
                            var since = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastAckTicks), DateTimeKind.Utc);
                            if (since > AckTimeout) throw new TimeoutException("no ACK within timeout; reconnecting");
                        }
                        _wake.WaitOne(100);
                    }
                }
            }
        }

        void ReaderLoop(NetworkStream stream, TcpClient client)
        {
            var header = new byte[ProtocolConstants.HeaderSize];
            try
            {
                while (!_stop && _connected)
                {
                    if (!MessageCodec.TryReadFrame(stream, header, out var frame)) break;
                    var msg = MessageCodec.Decode(frame);
                    if (msg is AckMessage ack)
                    {
                        _pending.Ack(ack.ChunkIndex);
                        Interlocked.Exchange(ref _lastAckTicks, DateTime.UtcNow.Ticks);
                        _wake.Set();
                    }
                    // TODO: PC → iPad STATE 처리 (예: PC 에서 일시정지 요청). 지금은 무시.
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
            _connected = false;
            _wake.Set();
            try { client.Close(); } catch { }
        }

        void Write(NetworkStream stream, byte[] frame)
        {
            stream.Write(frame, 0, frame.Length);
        }
    }
}
