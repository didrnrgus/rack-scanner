using System;
using System.Collections.Generic;
using System.IO;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;

namespace RackScanner.Receiver.Session
{
    /// <summary>
    /// PC 쪽 스캔 세션 상태: 마커 정의, 정렬 로그, 복셀 누적 점군, 청크 수신 현황.
    /// 메인 스레드에서만 다룬다. (UnityEngine 비의존)
    /// </summary>
    public sealed class ScanSession : IDisposable
    {
        public string SessionId { get; }
        public string SessionDir { get; }
        public SessionMeta Meta { get; }
        public VoxelAccumulator Points { get; private set; }
        public List<AlignedMessage> AlignmentLog { get; } = new List<AlignedMessage>();
        /// <summary>ARKit 메쉬 블록 (블록마다 받은 마지막 판, 블록 번호 순).</summary>
        public SortedDictionary<uint, MeshBlockMessage> MeshBlocks { get; } = new SortedDictionary<uint, MeshBlockMessage>();
        public int MeshTriangleCount { get; private set; }
        public int ReceivedChunks { get; private set; }
        public uint? ExpectedChunks { get; private set; }
        public bool Saved { get; private set; }
        public bool IsComplete => ExpectedChunks.HasValue && ReceivedChunks >= ExpectedChunks.Value;

        FileStream _chunksFile;

        /// <param name="sessionsRoot">세션 폴더들이 놓일 곳 (예: sessions/)</param>
        /// <param name="voxelSize">PC 누적 복셀 크기 (m)</param>
        /// <param name="recordChunks">받은 원본 프레임을 chunks.bin 에 기록할지</param>
        public ScanSession(string sessionsRoot, string sessionId, string deviceInfo, float voxelSize = 0.01f, bool recordChunks = true)
        {
            SessionId = sessionId;
            SessionDir = Path.Combine(sessionsRoot, Sanitize(sessionId));
            Directory.CreateDirectory(SessionDir);
            Meta = new SessionMeta { SessionId = sessionId, DeviceInfo = deviceInfo };
            Points = new VoxelAccumulator(voxelSize);
            if (recordChunks)
                _chunksFile = new FileStream(Path.Combine(SessionDir, SessionMeta.ChunksFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
        }

        /// <summary>메시지 하나를 반영한다. 새로 누적된 청크면 그 청크를 돌려준다 (화면 표시용).</summary>
        public PointChunkMessage Apply(IMessage msg, byte[] rawFrame)
        {
            if (rawFrame != null && _chunksFile != null && !(msg is StateMessage))
            {
                _chunksFile.Write(rawFrame, 0, rawFrame.Length);
                _chunksFile.Flush();
            }
            switch (msg)
            {
                case MarkerDefMessage md:
                    Meta.Marker = md.Marker.Clone();
                    return null;
                case AlignedMessage al:
                    Meta.UsedMarkerId = al.MarkerId;
                    AlignmentLog.Add(al);
                    return null;
                case PointChunkMessage pc:
                    ReceivedChunks++;
                    Points.AddRange(pc.Points);
                    return pc;
                case EndScanMessage es:
                    ExpectedChunks = es.TotalChunks;
                    return null;
                case MeshBlockMessage mb:
                    ApplyMesh(mb);
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>메쉬 블록 판 하나를 반영한다. 가진 것보다 새 판이면 true.</summary>
        public bool ApplyMesh(MeshBlockMessage mb)
        {
            if (MeshBlocks.TryGetValue(mb.BlockId, out var old))
            {
                if (old.Revision >= mb.Revision) return false;
                MeshTriangleCount -= old.TriangleCount;
            }
            if (mb.IsRemoval) MeshBlocks.Remove(mb.BlockId);
            else { MeshBlocks[mb.BlockId] = mb; MeshTriangleCount += mb.TriangleCount; }
            Saved = false;
            _meshDirty = true;
            return true;
        }

        /// <summary>블록들을 하나로 합친 원본 메쉬 (경계 정점 1mm 안은 붙임).</summary>
        public IndexedMesh BuildMergedMesh() => IndexedMesh.Combine(MeshBlocks.Values);

        /// <summary>병합 칸 크기를 바꿔 누적을 다시 계산한다 (PC 화면의 "병합 칸"). 다시 저장해야 points.ply 에 반영된다.</summary>
        public void ResetAccumulator(float voxelSize, IReadOnlyList<PointXYZRGB> rawPoints)
        {
            Points = new VoxelAccumulator(voxelSize);
            Points.AddRange(rawPoints);
            Saved = false;
        }

        bool _meshDirty;

        /// <summary>
        /// 세션 폴더에 points.ply, session.json, alignment_log.csv 와 (메쉬가 있으면) mesh_raw.obj 를 쓴다.
        /// mesh_raw.obj 는 메쉬가 바뀌었을 때만 다시 쓴다 (수신 앱이 색을 입혀 다시 쓴 파일을, 병합 칸만 바꾼 재저장이 덮어쓰지 않게).
        /// </summary>
        public void Save()
        {
            if (MeshBlocks.Count > 0 && _meshDirty)
            {
                var merged = BuildMergedMesh();
                ObjIO.Write(Path.Combine(SessionDir, SessionMeta.MeshRawFileName), merged, $"session {SessionId}, ARKit mesh blocks {MeshBlocks.Count} (original density)");
                Meta.MeshBlockCount = MeshBlocks.Count;
                Meta.MeshTriangleCount = merged.TriangleCount;
                _meshDirty = false;
            }
            var pts = Points.ToPoints();
            PlyIO.Write(Path.Combine(SessionDir, SessionMeta.PointsFileName), pts);
            Meta.PointCount = pts.Count;
            Meta.TotalChunks = (int)(ExpectedChunks ?? (uint)ReceivedChunks);
            Meta.Save(SessionDir);
            using (var w = new StreamWriter(Path.Combine(SessionDir, SessionMeta.AlignmentLogFileName)))
            {
                w.WriteLine("index,markerId,distance,viewAngleDeg,pitchDeg,rollDeg,frameCount");
                for (int i = 0; i < AlignmentLog.Count; i++)
                {
                    var a = AlignmentLog[i];
                    w.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:F4},{3:F2},{4:F2},{5:F2},{6}",
                        i, a.MarkerId, a.Distance, a.ViewAngleDeg, a.PitchDeg, a.RollDeg, a.FrameCount));
                }
            }
            Saved = true;
        }

        public void Dispose()
        {
            _chunksFile?.Dispose();
            _chunksFile = null;
        }

        static string Sanitize(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return string.IsNullOrWhiteSpace(id) ? "session" : id;
        }

        /// <summary>저장된 세션 폴더 목록 (points.ply 가 있는 것, 최신 순).</summary>
        public static List<string> ListSaved(string sessionsRoot)
        {
            var list = new List<string>();
            if (!Directory.Exists(sessionsRoot)) return list;
            foreach (var d in Directory.GetDirectories(sessionsRoot))
                if (File.Exists(Path.Combine(d, SessionMeta.PointsFileName))) list.Add(d);
            list.Sort(StringComparer.Ordinal);
            list.Reverse();
            return list;
        }

        /// <summary>저장된 세션의 원본 메쉬 (mesh_raw.obj). 없으면 null.</summary>
        public static IndexedMesh LoadMesh(string sessionDir)
        {
            string path = Path.Combine(sessionDir, SessionMeta.MeshRawFileName);
            return File.Exists(path) ? ObjIO.Read(path) : null;
        }

        /// <summary>저장된 세션의 점군과 메타를 읽는다 (분석만 반복 실행할 때).</summary>
        public static List<PointXYZRGB> LoadPoints(string sessionDir, out SessionMeta meta)
        {
            meta = File.Exists(Path.Combine(sessionDir, SessionMeta.FileName)) ? SessionMeta.Load(sessionDir) : new SessionMeta { SessionId = Path.GetFileName(sessionDir) };
            return PlyIO.Read(Path.Combine(sessionDir, SessionMeta.PointsFileName));
        }
    }
}
