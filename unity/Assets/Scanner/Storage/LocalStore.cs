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
    /// 메쉬 블록(MESH_BLOCK)은 같은 블록이 계속 갱신되므로 따로 mesh.bin 에 이어 쓰고, 파일이 커지면 블록마다 마지막 판만 남겨 다시 쓴다.
    /// (UnityEngine 비의존)
    /// </summary>
    public sealed class LocalStore : IDisposable
    {
        public string SessionDir { get; }
        public string SessionId { get; }
        /// <summary>mesh.bin 크기 (바이트).</summary>
        public long MeshFileBytes { get; private set; }
        FileStream _chunks;
        FileStream _mesh;
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

        /// <summary>메쉬 블록 판을 mesh.bin 에 이어 쓰고 프레임 바이트를 돌려준다 (전송에 재사용).</summary>
        public byte[] AppendMesh(MeshBlockMessage block)
        {
            var frame = MessageCodec.Encode(block);
            if (_mesh == null) OpenMesh();
            _mesh.Write(frame, 0, frame.Length);
            _mesh.Flush();
            MeshFileBytes += frame.Length;
            return frame;
        }

        /// <summary>
        /// mesh.bin 을 블록마다 마지막 판만 남겨 다시 쓴다 (임시 파일에 쓴 뒤 바꿔치기라 도중에 죽어도 원래 파일이 남는다).
        /// </summary>
        public void CompactMesh(IEnumerable<MeshBlockMessage> latest)
        {
            string path = Path.Combine(SessionDir, SessionMeta.MeshFramesFileName);
            string tmp = path + ".tmp";
            long bytes = 0;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                foreach (var b in latest)
                {
                    if (b.IsRemoval) continue;
                    var f = MessageCodec.Encode(b);
                    fs.Write(f, 0, f.Length);
                    bytes += f.Length;
                }
            }
            _mesh?.Dispose(); _mesh = null;
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            MeshFileBytes = bytes;
        }

        void OpenMesh()
        {
            _mesh = new FileStream(Path.Combine(SessionDir, SessionMeta.MeshFramesFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
            MeshFileBytes = _mesh.Length;
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
            _mesh?.Dispose(); _mesh = null;
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
        /// chunks.bin 과 (있으면) mesh.bin 을 읽는다. 마지막 프레임이 잘려 있으면 (앱 강제 종료 등) 그 앞까지만 쓴다.
        /// 예전 버전(<see cref="ProtocolConstants.MinStoredVersion"/> 이상)으로 저장된 세션도 읽는다.
        /// </summary>
        public static StoredSession Read(string sessionDir)
        {
            var s = new StoredSession { SessionId = Path.GetFileName(sessionDir) };
            ReadFrames(Path.Combine(sessionDir, SessionMeta.ChunksFileName), s, msg =>
            {
                switch (msg)
                {
                    case MarkerDefMessage md: s.MarkerDef = md; break;
                    case AlignedMessage al: s.Aligned = al; break;
                    case PointChunkMessage pc: s.Chunks.Add(pc); break;
                    case EndScanMessage es: s.EndScan = es; break;
                }
            });
            string meshPath = Path.Combine(sessionDir, SessionMeta.MeshFramesFileName);
            if (File.Exists(meshPath))
                ReadFrames(meshPath, s, msg =>
                {
                    if (!(msg is MeshBlockMessage mb)) return;
                    if (s.MeshBlocks.TryGetValue(mb.BlockId, out var old) && old.Revision > mb.Revision) return;
                    if (mb.IsRemoval) s.MeshBlocks.Remove(mb.BlockId);
                    else s.MeshBlocks[mb.BlockId] = mb;
                });
            return s;
        }

        static void ReadFrames(string path, StoredSession s, Action<IMessage> onMessage)
        {
            var header = new byte[ProtocolConstants.HeaderSize];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // 쓰는 중인 세션도 읽을 수 있게
            {
                while (true)
                {
                    Frame frame;
                    try
                    {
                        if (!MessageCodec.TryReadFrame(fs, header, out frame)) break;
                    }
                    catch (EndOfStreamException) { s.Truncated = true; break; }
                    onMessage(MessageCodec.DecodeStored(frame));
                }
            }
        }
    }

    public sealed class StoredSession
    {
        public string SessionId;
        public MarkerDefMessage MarkerDef;
        public AlignedMessage Aligned;
        public List<PointChunkMessage> Chunks = new List<PointChunkMessage>();
        public EndScanMessage EndScan;
        /// <summary>블록마다 마지막 판 (블록 번호 순).</summary>
        public SortedDictionary<uint, MeshBlockMessage> MeshBlocks = new SortedDictionary<uint, MeshBlockMessage>();
        public bool Truncated;

        public int PointCount { get { int n = 0; foreach (var c in Chunks) n += c.Points.Length; return n; } }
    }
}
