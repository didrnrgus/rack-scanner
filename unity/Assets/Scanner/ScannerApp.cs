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
        public Vector3 MarkerUnityPosition = Vector3.zero;
        public Vector3 MarkerUnityEuler = Vector3.zero;

        [Header("수집 (초기값, 측정 후 조정)")]
        public float VoxelSize = 0.04f;
        public int SampleEveryNFrames = 3;
        public int ChunkMaxPoints = 8000;

        // 상태
        public enum Phase { Idle, Scanning, Paused, Completed }
        public Phase State { get; private set; } = Phase.Idle;
        public string PcHost = "192.168.0.10";
        public int PcPort = ProtocolConstants.DefaultPort;

        internal TrackingGuard Tracking { get; private set; }
        internal MarkerAligner Aligner { get; private set; }
        internal DepthSampler Depth { get; private set; }
        internal NetSender Sender { get; private set; }
        internal LocalStore Store { get; private set; }
        internal string SessionId { get; private set; }
        internal int TotalPoints { get; private set; }
        internal int FrameCount { get; private set; }
        internal float Fps { get; private set; }
        internal string Message = "";

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
            }
            _camTransform = CameraManager != null ? CameraManager.transform : Camera.main.transform;

            PcHost = PlayerPrefs.GetString("pcHost", PcHost);
            MarkerPrintedWidth = PlayerPrefs.GetFloat("markerWidth", MarkerPrintedWidth);
            MarkerPrintedHeight = PlayerPrefs.GetFloat("markerHeight", MarkerPrintedHeight);

            Tracking = new TrackingGuard();
            Tracking.Changed += OnTrackingChanged;
            Aligner = new MarkerAligner(BuildMarkerDefinition());
            Aligner.Logged += e => Store?.LogAlignment(e);
            Aligner.Aligned += OnAligned;
            Depth = new DepthSampler();
            if (gameObject.GetComponent<ScanUI>() == null) gameObject.AddComponent<ScanUI>();
        }

        void OnDestroy()
        {
            Sender?.Dispose();
            Store?.Dispose();
            Depth?.Dispose();
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
            Aligner.Update(ImageManager, _camTransform, Time.realtimeSinceStartupAsDouble, Time.deltaTime);
            if (Tracking.IsNormal && Tracking.LostSinceAlign && Aligner.IsAligned)
            {
                // 추적 끊김 후 복구: 드리프트 가능성이 있어 마커 재정렬 전까지 수집 중단
                Aligner.RequireRealign();
            }

            if (State == Phase.Scanning && Tracking.IsNormal && Aligner.IsAligned && FrameCount % Mathf.Max(1, SampleEveryNFrames) == 0)
                Collect();

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
                if (_voxels.TryAdd(pt)) _chunks.Add(pt, now);
            }
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
            _voxels = new VoxelSet(VoxelSize);
            _chunks = new ChunkBuilder { MaxPoints = ChunkMaxPoints };
            TotalPoints = 0;
            State = Phase.Idle;
            PlayerPrefs.SetString("pcHost", PcHost);
            PlayerPrefs.SetFloat("markerWidth", MarkerPrintedWidth);
            PlayerPrefs.SetFloat("markerHeight", MarkerPrintedHeight);
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
            var end = new EndScanMessage { TotalChunks = _chunks.ChunkCount };
            Store.Append(end);
            Store.SaveMeta(new SessionMeta { SessionId = SessionId, DeviceInfo = DeviceInfo(), Marker = Aligner.Marker, UsedMarkerId = Aligner.Marker.Id, TotalChunks = (int)end.TotalChunks, PointCount = TotalPoints });
            Sender?.EndScan(end.TotalChunks);
            State = Phase.Completed;
            Message = $"촬영완료: 청크 {end.TotalChunks}, 점 {TotalPoints}. 로컬 저장: {Store.SessionDir}";
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
            Sender.EndScan(s.EndScan?.TotalChunks ?? (uint)s.Chunks.Count);
            Sender.Start();
            Message = $"재전송 시작: {sessionId} (청크 {s.Chunks.Count}{(s.Truncated ? ", 끝부분 잘림" : "")})";
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
