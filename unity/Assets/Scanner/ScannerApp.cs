#if RS_ARFOUNDATION
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RackScanner.Scanner.AR;
using RackScanner.Scanner.Net;
using RackScanner.Scanner.Storage;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace RackScanner.Scanner
{
    /// <summary>
    /// 아이패드 스캔 앱. 모듈 조립:
    /// TrackingGuard → MarkerAligner → DepthSampler → VoxelFilter → ChunkBuilder → LocalStore + NetSender, ScanUI(OnGUI).
    /// 씬 요구사항: AR Session, XR Origin(Mobile AR) + AR Camera Manager / AR Occlusion Manager / AR Tracked Image Manager.
    /// 참조를 비워 두면 씬에서 찾는다.
    /// </summary>
    public sealed class ScannerApp : MonoBehaviour
    {
        [Header("AR 참조 (비우면 자동 탐색)")]
        public ARCameraManager CameraManager;
        public AROcclusionManager OcclusionManager;
        public ARTrackedImageManager ImageManager;

        [Header("마커 (인쇄 실측 크기를 자로 재서 넣는다)")]
        public string MarkerId = "base";
        public float MarkerPrintedWidth = 0.20f;
        public float MarkerPrintedHeight = 0.20f;
        [Tooltip("MarkerLibrary 에 든 마커 이미지 (Read/Write 켜고 RGBA32). 입력한 cm 로 ARKit 마커 크기를 바꿀 때 쓴다")]
        public Texture2D MarkerTexture;
        [Tooltip("빌드에 든 MarkerLibrary 의 Physical Size (m). 입력값이 이와 다르면 실행 중에 바꿔 적용한다")]
        public float MarkerLibraryWidth = 0.20f;
        public Vector3 MarkerUnityPosition = Vector3.zero;
        public Vector3 MarkerUnityEuler = Vector3.zero;

        [Header("수집 (초기값, 측정 후 조정)")]
        [Tooltip("아이패드 쪽 중복 제거 복셀 (m). 칸마다 첫 점만 남기므로 작을수록 촘촘하다. 앱 화면에서 바꿀 수 있고 새 세션부터 적용")]
        public float VoxelSize = 0.02f;
        [Tooltip("깊이 맵(256×192)을 몇 픽셀마다 읽을지. 1 = 전부")]
        public int DepthPixelStep = 1;
        [Tooltip("원본 모드: ARKit 깊이 맵을 가공 없이 보낸다 (시간 평활화·복셀·가장자리 필터·컬러 끔, 거리 5m 까지). 점이 많아 RawSampleEveryNFrames 마다 한 프레임만")]
        public bool RawMode = true;
        public int RawSampleEveryNFrames = 30;
        [Tooltip("화면에 카메라 영상을 그릴지. 끄면 검은 화면에 UI 만 (LiDAR 수집·추적·마커 인식은 그대로, 렌더링 부하·배터리 절약)")]
        public bool ShowCameraFeed = false;
        public int SampleEveryNFrames = 3;
        public int ChunkMaxPoints = 8000;

        [Header("메쉬 (ARKit 장면 재구성)")]
        [Tooltip("LiDAR 메쉬를 점군과 함께 모아 PC 로 보내고 로컬에도 저장한다 (mesh.bin, 촬영완료 시 mesh_raw.obj). 촬영 중에는 바꾸지 않는다")]
        public bool MeshCapture = true;
        [Tooltip("아이패드 화면에 메쉬를 반투명으로 겹쳐 그린다 (어디까지 찍혔는지 확인)")]
        public bool MeshOverlay = true;
        [Tooltip("바뀐 메쉬 블록을 몇 초마다 읽어 보낼지")]
        public float MeshInterval = 1.0f;

        // 상태
        public enum Phase { Idle, Scanning, Paused, Completed }
        public Phase State { get; private set; } = Phase.Idle;
        public string PcHost = "192.168.0.10";
        public int PcPort = ProtocolConstants.DefaultPort;

        internal TrackingGuard Tracking { get; private set; }
        internal MarkerAligner Aligner { get; private set; }
        internal MarkerLibrarySizer MarkerSizer { get; private set; }
        internal DepthSampler Depth { get; private set; }
        internal NetSender Sender { get; private set; }
        internal LocalStore Store { get; private set; }
        internal string SessionId { get; private set; }
        internal int TotalPoints { get; private set; }
        internal int FrameCount { get; private set; }
        internal float Fps { get; private set; }
        internal string Message = "";
        internal MeshCollector Meshes { get; private set; }
        /// <summary>이 세션에서 살아 있는 메쉬 블록 (블록마다 마지막 판). mesh.bin 정리와 OBJ 쓰기에 쓴다.</summary>
        readonly SortedDictionary<uint, MeshBlockMessage> _meshLatest = new SortedDictionary<uint, MeshBlockMessage>();
        readonly List<MeshBlockMessage> _meshBuf = new List<MeshBlockMessage>();
        internal int MeshBlockCount => _meshLatest.Count;
        internal int MeshTriangleCount { get; private set; }

        VoxelSet _voxels;
        ChunkBuilder _chunks;
        readonly List<(Vector3 p, Color32 c)> _sampleBuf = new List<(Vector3, Color32)>(20000);
        float _scanStart;
        Transform _camTransform;

        string SessionsRoot => Path.Combine(Application.persistentDataPath, "sessions");

        void Start()
        {
            Application.targetFrameRate = 30; // 목표 30fps 고정
            Screen.orientation = ScreenOrientation.LandscapeLeft; // DepthSampler 의 축 변환 기준
            if (CameraManager == null) CameraManager = FindAnyObjectByType<ARCameraManager>();
            if (OcclusionManager == null) OcclusionManager = FindAnyObjectByType<AROcclusionManager>();
            if (ImageManager == null) ImageManager = FindAnyObjectByType<ARTrackedImageManager>();
            if (OcclusionManager != null)
            {
                OcclusionManager.requestedEnvironmentDepthMode = UnityEngine.XR.ARSubsystems.EnvironmentDepthMode.Best;
                OcclusionManager.environmentDepthTemporalSmoothingRequested = true;
                // 깊이 이미지는 점 수집용으로만 쓴다. 화면 가림(occlusion)을 켜 두면 카메라 배경이 LiDAR 깊이를 깊이 버퍼에 써서,
                // 실제 표면과 같은 자리에 있는 메쉬 오버레이가 거친 깊이(256×192)와 겹쳐 얼룩덜룩 가려진다. CPU 깊이 이미지는 그대로 받는다.
                OcclusionManager.requestedOcclusionPreferenceMode = UnityEngine.XR.ARSubsystems.OcclusionPreferenceMode.NoOcclusion;
            }
            _camTransform = CameraManager != null ? CameraManager.transform : Camera.main.transform;
            // 카메라 화면을 끄면 ARCameraBackground 가 투영 행렬을 Unity 기본값으로 되돌린다.
            // 그때도 메쉬 오버레이가 실제 카메라 화각으로 그려지도록 ARKit 투영 행렬을 직접 넣는다.
            if (CameraManager != null) CameraManager.frameReceived += OnCameraFrame;

            PcHost = PlayerPrefs.GetString("pcHost", PcHost);
            MarkerPrintedWidth = PlayerPrefs.GetFloat("markerWidth", MarkerPrintedWidth);
            MarkerPrintedHeight = PlayerPrefs.GetFloat("markerHeight", MarkerPrintedHeight);
            VoxelSize = PlayerPrefs.GetFloat("voxelSize", VoxelSize);
            RawMode = PlayerPrefs.GetInt("rawMode", RawMode ? 1 : 0) != 0;
            ShowCameraFeed = PlayerPrefs.GetInt("showCamera", ShowCameraFeed ? 1 : 0) != 0;
            MeshCapture = PlayerPrefs.GetInt("meshCapture", MeshCapture ? 1 : 0) != 0;
            MeshOverlay = PlayerPrefs.GetInt("meshOverlay", MeshOverlay ? 1 : 0) != 0;
            Meshes = new MeshCollector(MeshOverlay) { Interval = Mathf.Max(0.2f, MeshInterval) };
            Meshes.Enabled = MeshCapture;

            Tracking = new TrackingGuard();
            Tracking.Changed += OnTrackingChanged;
            Aligner = new MarkerAligner(BuildMarkerDefinition());
            Aligner.Logged += e => Store?.LogAlignment(e);
            Aligner.Aligned += OnAligned;
            MarkerSizer = new MarkerLibrarySizer(ImageManager, MarkerTexture, MarkerId, MarkerLibraryWidth);
            MarkerSizer.Applied += _ =>
            {
                // 이전 크기로 한 정렬은 원점이 틀렸으므로 버리고 다시 정렬한다.
                Aligner.Marker = BuildMarkerDefinition();
                Aligner.RequireRealign("마커 크기를 바꿨습니다. 마커를 다시 비춰 정렬하세요");
            };
            Depth = new DepthSampler { PixelStep = Mathf.Max(1, DepthPixelStep) };
            ApplyCaptureMode();
            ApplyCameraFeed();
            if (gameObject.GetComponent<ScanUI>() == null) gameObject.AddComponent<ScanUI>();
        }

        void OnDestroy()
        {
            Sender?.Dispose();
            Store?.Dispose();
            Depth?.Dispose();
            Meshes?.Dispose();
            if (CameraManager != null) CameraManager.frameReceived -= OnCameraFrame;
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (ShowCameraFeed || !args.projectionMatrix.HasValue) return; // 켜져 있으면 ARCameraBackground 가 넣는다
            var cam = CameraManager.GetComponent<Camera>();
            if (cam != null) cam.projectionMatrix = args.projectionMatrix.Value;
        }

        internal MarkerDefinition BuildMarkerDefinition() => new MarkerDefinition
        {
            Id = MarkerId,
            PrintedWidth = MarkerPrintedWidth,
            PrintedHeight = MarkerPrintedHeight,
            Position = MarkerUnityPosition.ToNumerics(),
            Rotation = Quaternion.Euler(MarkerUnityEuler).ToNumerics(),
        };

        void Update()
        {
            FrameCount++;
            Fps = Mathf.Lerp(Fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            Tracking.Update();
            MarkerSizer.Update(MarkerPrintedWidth);
            Aligner.Update(ImageManager, _camTransform, Time.realtimeSinceStartupAsDouble, Time.deltaTime);
            if (Tracking.IsNormal && Tracking.LostSinceAlign && Aligner.IsAligned)
            {
                // 추적 끊김 후 복구: 드리프트 가능성이 있어 마커 재정렬 전까지 수집 중단
                Aligner.RequireRealign();
            }

            int every = RawMode ? RawSampleEveryNFrames : SampleEveryNFrames;
            if (State == Phase.Scanning && Tracking.IsNormal && Aligner.IsAligned && FrameCount % Mathf.Max(1, every) == 0)
                Collect();
            if (State == Phase.Scanning && Tracking.IsNormal && Aligner.IsAligned && MeshCapture)
                CollectMesh(force: false);

            if (_chunks != null)
            {
                while (_chunks.TryBuild(ScanTime, false, out var chunk)) Emit(chunk);
            }
        }

        double ScanTime => Time.realtimeSinceStartup - _scanStart;

        void Collect()
        {
            if (OcclusionManager == null || CameraManager == null) { Message = "AR 매니저가 씬에 없음"; return; }
            _sampleBuf.Clear();
            if (!Depth.Sample(OcclusionManager, CameraManager, _camTransform, _sampleBuf)) return;
            var t = Aligner.ArToUnity;
            double now = ScanTime;
            foreach (var (p, c) in _sampleBuf)
            {
                var q = t.Apply(p.ToNumerics()); // AR 세션 좌표 → 마커 기준 Unity 좌표
                var pt = new PointXYZRGB(q, c.r, c.g, c.b);
                if (RawMode || _voxels.TryAdd(pt)) _chunks.Add(pt, now);
            }
        }

        /// <summary>바뀐 메쉬 블록을 마커 좌표로 읽어 로컬 저장 + 전송. force 면 간격을 무시하고 지금 읽는다.</summary>
        void CollectMesh(bool force)
        {
            if (Meshes == null || Store == null) return;
            _meshBuf.Clear();
            Meshes.Collect(Aligner.ArToUnity, ScanTime, force, _meshBuf);
            foreach (var b in _meshBuf) EmitMesh(b);
            if (!string.IsNullOrEmpty(Meshes.LastError)) Message = "메쉬: " + Meshes.LastError;
        }

        void EmitMesh(MeshBlockMessage b)
        {
            var frame = Store.AppendMesh(b); // 로컬 원본이 기준
            Sender?.EnqueueMeshFrame(b.BlockId, b.Revision, frame);
            if (_meshLatest.TryGetValue(b.BlockId, out var old)) MeshTriangleCount -= old.TriangleCount;
            if (b.IsRemoval) _meshLatest.Remove(b.BlockId);
            else { _meshLatest[b.BlockId] = b; MeshTriangleCount += b.TriangleCount; }

            // 같은 블록의 옛 판이 쌓여 mesh.bin 이 살아 있는 양의 3배 + 8MB 를 넘으면 마지막 판만 남긴다
            long live = 0;
            foreach (var m in _meshLatest.Values) live += 40 + m.Vertices.Length * ProtocolConstants.BytesPerMeshVertex + m.Indices.Length * 4;
            if (Store.MeshFileBytes > live * 3 + 8L * 1024 * 1024) Store.CompactMesh(_meshLatest.Values);
        }

        internal void SetMeshCapture(bool on)
        {
            if (State == Phase.Scanning || State == Phase.Paused) return;
            MeshCapture = on;
            PlayerPrefs.SetInt("meshCapture", on ? 1 : 0);
            if (Meshes != null) Meshes.Enabled = on;
        }

        internal void SetMeshOverlay(bool on)
        {
            MeshOverlay = on;
            PlayerPrefs.SetInt("meshOverlay", on ? 1 : 0);
            if (Meshes != null) Meshes.Overlay = on;
            ApplyCameraFeed();
        }

        /// <summary>원본 모드 켜기/끄기. 촬영 중에는 바꾸지 않는다.</summary>
        internal void SetRawMode(bool raw)
        {
            if (State == Phase.Scanning || State == Phase.Paused) return;
            RawMode = raw;
            PlayerPrefs.SetInt("rawMode", raw ? 1 : 0);
            ApplyCaptureMode();
        }

        internal void SetCameraFeed(bool show)
        {
            ShowCameraFeed = show;
            PlayerPrefs.SetInt("showCamera", show ? 1 : 0);
            ApplyCameraFeed();
        }

        void ApplyCameraFeed()
        {
            if (CameraManager == null) return;
            var bg = CameraManager.GetComponent<ARCameraBackground>();
            if (bg != null) bg.enabled = ShowCameraFeed;
            var cam = CameraManager.GetComponent<Camera>();
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                // 끄면 아무것도 그리지 않고 검게 지우기만. 메쉬 겹쳐 보기가 켜져 있으면 검은 바탕에 메쉬만 그린다
                cam.cullingMask = ShowCameraFeed || MeshOverlay ? ~0 : 0;
            }
        }

        void ApplyCaptureMode()
        {
            if (OcclusionManager != null) OcclusionManager.environmentDepthTemporalSmoothingRequested = !RawMode;
            Depth.EdgeRelativeJump = RawMode ? 0f : 0.03f;
            Depth.SampleColor = !RawMode;
            Depth.MaxDepth = RawMode ? 5f : 3f;
        }

        void Emit(PointChunkMessage chunk)
        {
            var frame = Store.Append(chunk); // 로컬 원본이 기준
            Sender?.EnqueueChunkFrame(chunk.ChunkIndex, frame);
            TotalPoints += chunk.Points.Length;
        }

        void OnAligned(AlignedMessage al)
        {
            Tracking.ClearLost();
            Meshes?.MarkAllDirty(); // 정렬이 바뀌었으니 메쉬도 새 좌표로 다시 보낸다
            if (Store != null) Store.Append(al);
            Sender?.SetAligned(al);
        }

        void OnTrackingChanged(bool normal)
        {
            var st = normal ? ScanState.TrackingNormal : ScanState.TrackingLost;
            Sender?.SendState(st);
        }

        /// <summary>새 세션 준비: 세션 ID, 로컬 저장소, (주소가 있으면) PC 연결, MARKER_DEF 미리 전송.</summary>
        internal void BeginSession(bool connect)
        {
            EndSessionObjects();
            SessionId = SessionMeta.NewSessionId(DateTime.Now);
            Store = new LocalStore(SessionsRoot, SessionId);
            var marker = BuildMarkerDefinition();
            Aligner.Marker = marker;
            var md = new MarkerDefMessage { Marker = marker };
            Store.Append(md);
            Store.SaveMeta(new SessionMeta { SessionId = SessionId, DeviceInfo = DeviceInfo(), Marker = marker });
            if (Aligner.LastAligned != null && Aligner.IsAligned) Store.Append(Aligner.LastAligned);
            if (connect) Connect(md);
            VoxelSize = Mathf.Clamp(VoxelSize, 0.005f, 0.10f);
            _voxels = new VoxelSet(VoxelSize);
            _chunks = new ChunkBuilder { MaxPoints = ChunkMaxPoints };
            TotalPoints = 0;
            _meshLatest.Clear();
            MeshTriangleCount = 0;
            State = Phase.Idle;
            PlayerPrefs.SetString("pcHost", PcHost);
            PlayerPrefs.SetFloat("markerWidth", MarkerPrintedWidth);
            PlayerPrefs.SetFloat("markerHeight", MarkerPrintedHeight);
            PlayerPrefs.SetFloat("voxelSize", VoxelSize);
        }

        void Connect(MarkerDefMessage md)
        {
            Sender?.Dispose();
            Sender = new NetSender(PcHost.Trim(), PcPort, SessionId, DeviceInfo());
            Sender.SetMarkerDef(md);
            if (Aligner.LastAligned != null && Aligner.IsAligned) Sender.SetAligned(Aligner.LastAligned);
            Sender.Start();
        }

        internal void StartScan()
        {
            if (Store == null) BeginSession(connect: false);
            if (!Aligner.IsAligned) { Message = "먼저 마커를 비춰 정렬하세요"; return; }
            _scanStart = Time.realtimeSinceStartup;
            Meshes?.MarkAllDirty(); // 촬영 전에 이미 만들어진 메쉬도 이 세션에 담는다
            State = Phase.Scanning;
            Sender?.SendState(ScanState.Resumed);
        }

        internal void TogglePause()
        {
            if (State == Phase.Scanning) { State = Phase.Paused; Sender?.SendState(ScanState.Paused); }
            else if (State == Phase.Paused) { State = Phase.Scanning; Sender?.SendState(ScanState.Resumed); }
        }

        /// <summary>촬영완료: 남은 점을 청크로 내고 END_SCAN.</summary>
        internal void CompleteScan()
        {
            if (Store == null) return;
            while (_chunks.TryBuild(ScanTime, true, out var chunk)) Emit(chunk);
            string meshNote = "";
            if (MeshCapture && Aligner.IsAligned && State != Phase.Idle)
            {
                CollectMesh(force: true); // END_SCAN 전에 마지막 판까지 내보낸다
                if (_meshLatest.Count > 0)
                {
                    try
                    {
                        Store.CompactMesh(_meshLatest.Values);
                        var merged = IndexedMesh.Combine(_meshLatest.Values);
                        ObjIO.Write(Path.Combine(Store.SessionDir, SessionMeta.MeshRawFileName), merged, $"session {SessionId}, ARKit mesh blocks {_meshLatest.Count}");
                        meshNote = $", 메쉬 블록 {_meshLatest.Count} / 삼각형 {merged.TriangleCount:N0} ({SessionMeta.MeshRawFileName})";
                    }
                    catch (Exception e) { meshNote = ", 메쉬 저장 실패: " + e.Message; }
                }
            }
            var end = new EndScanMessage { TotalChunks = _chunks.ChunkCount };
            Store.Append(end);
            Store.SaveMeta(new SessionMeta { SessionId = SessionId, DeviceInfo = DeviceInfo(), Marker = Aligner.Marker, UsedMarkerId = Aligner.Marker.Id, TotalChunks = (int)end.TotalChunks, PointCount = TotalPoints, MeshBlockCount = _meshLatest.Count, MeshTriangleCount = MeshTriangleCount });
            Sender?.EndScan(end.TotalChunks);
            State = Phase.Completed;
            Message = $"촬영완료: 청크 {end.TotalChunks}, 점 {TotalPoints}{meshNote}. 로컬 저장: {Store.SessionDir}";
        }

        /// <summary>저장된 세션을 PC 로 다시 보낸다 (촬영 후 파일 전송, 7일 만료 전 백업).</summary>
        internal void ResendStored(string sessionId)
        {
            var s = LocalStore.Read(Path.Combine(SessionsRoot, sessionId));
            Sender?.Dispose();
            Sender = new NetSender(PcHost.Trim(), PcPort, sessionId, DeviceInfo());
            if (s.MarkerDef != null) Sender.SetMarkerDef(s.MarkerDef);
            if (s.Aligned != null) Sender.SetAligned(s.Aligned);
            foreach (var c in s.Chunks) Sender.EnqueueChunk(c);
            foreach (var b in s.MeshBlocks.Values) Sender.EnqueueMesh(b);
            Sender.EndScan(s.EndScan?.TotalChunks ?? (uint)s.Chunks.Count);
            Sender.Start();
            Message = $"재전송 시작: {sessionId} (청크 {s.Chunks.Count}, 메쉬 블록 {s.MeshBlocks.Count}{(s.Truncated ? ", 끝부분 잘림" : "")})";
        }

        internal List<string> StoredSessions() => LocalStore.ListSessions(SessionsRoot);

        void EndSessionObjects()
        {
            Store?.Dispose(); Store = null;
        }

        static string DeviceInfo() => $"{SystemInfo.deviceModel} / {SystemInfo.operatingSystem}";

        internal static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
#endif
