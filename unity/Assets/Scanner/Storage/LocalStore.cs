using System;
using System.Collections.Generic;
using System.IO;
using RackScanner.Shared.IO;
using RackScanner.Shared.Protocol;

namespace RackScanner.Scanner.Storage
{
    /// <summary>
    /// 아이패드 로컬 원본 저장. 세션 폴더의 chunks.bin 에 프로토콜 프레임을 그대로 이어 쓴다
    /// (MARKER_DEF, ALIGNED, POINT_CHUNK..., END_SCAN). 전송과 같은 형식이라 그대로 재전송·복구할 수 있다.
    /// (UnityEngine 비의존)
    /// </summary>
    public sealed class LocalStore : IDisposable
    {
        public string SessionDir { get; }
        public string SessionId { get; }
        FileStream _chunks;
        StreamWriter _alignLog;

        public LocalStore(string sessionsRoot, string sessionId)
        {
            SessionId = sessionId;
            SessionDir = Path.Combine(sessionsRoot, sessionId);
            Directory.CreateDirectory(SessionDir);
            _chunks = new FileStream(Path.Combine(SessionDir, SessionMeta.ChunksFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
            string logPath = Path.Combine(SessionDir, SessionMeta.AlignmentLogFileName);
            bool newLog = !File.Exists(logPath);
            _alignLog = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read));
            if (newLog) _alignLog.WriteLine(AlignmentLogEntry.CsvHeader);
        }

        /// <summary>메시지를 인코딩해 이어 쓰고 프레임 바이트를 돌려준다 (전송에 재사용).</summary>
        public byte[] Append(IMessage msg)
        {
            var frame = MessageCodec.Encode(msg);
            _chunks.Write(frame, 0, frame.Length);
            _chunks.Flush(); // 앱이 죽어도 남도록 매 청크 flush
            return frame;
        }

        public void LogAlignment(AlignmentLogEntry e)
        {
            _alignLog.WriteLine(e.ToCsv());
            _alignLog.Flush();
        }

        public void SaveMeta(SessionMeta meta) => meta.Save(SessionDir);

        public void Dispose()
        {
            _chunks?.Dispose(); _chunks = null;
            _alignLog?.Dispose(); _alignLog = null;
        }

        /// <summary>저장된 세션 목록 (최신 순).</summary>
        public static List<string> ListSessions(string sessionsRoot)
        {
            var list = new List<string>();
            if (!Directory.Exists(sessionsRoot)) return list;
            foreach (var d in Directory.GetDirectories(sessionsRoot))
                if (File.Exists(Path.Combine(d, SessionMeta.ChunksFileName))) list.Add(Path.GetFileName(d));
            list.Sort(StringComparer.Ordinal);
            list.Reverse();
            return list;
        }

        /// <summary>
        /// chunks.bin 을 읽는다. 마지막 프레임이 잘려 있으면 (앱 강제 종료 등) 그 앞까지만 쓴다.
        /// </summary>
        public static StoredSession Read(string sessionDir)
        {
            var s = new StoredSession { SessionId = Path.GetFileName(sessionDir) };
            var header = new byte[ProtocolConstants.HeaderSize];
            using (var fs = File.OpenRead(Path.Combine(sessionDir, SessionMeta.ChunksFileName)))
            {
                while (true)
                {
                    Frame frame;
                    try
                    {
                        if (!MessageCodec.TryReadFrame(fs, header, out frame)) break;
                    }
                    catch (EndOfStreamException) { s.Truncated = true; break; }
                    var msg = MessageCodec.Decode(frame);
                    switch (msg)
                    {
                        case MarkerDefMessage md: s.MarkerDef = md; break;
                        case AlignedMessage al: s.Aligned = al; break;
                        case PointChunkMessage pc: s.Chunks.Add(pc); break;
                        case EndScanMessage es: s.EndScan = es; break;
                    }
                }
            }
            return s;
        }
    }

    public sealed class StoredSession
    {
        public string SessionId;
        public MarkerDefMessage MarkerDef;
        public AlignedMessage Aligned;
        public List<PointChunkMessage> Chunks = new List<PointChunkMessage>();
        public EndScanMessage EndScan;
        public bool Truncated;

        public int PointCount { get { int n = 0; foreach (var c in Chunks) n += c.Points.Length; return n; } }
    }
}
