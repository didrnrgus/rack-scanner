using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using RackScanner.Analysis;
using RackScanner.Receiver.Net;
using RackScanner.Receiver.Session;
using RackScanner.Receiver.View;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.IO;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using UnityEngine;

namespace RackScanner.Receiver
{
    /// <summary>
    /// PC 수신·분석 앱. 수신은 ScanServer 스레드, 누적과 렌더링은 메인 스레드(Update).
    /// 화면은 IMGUI 라 씬에 이 컴포넌트 하나만 있으면 된다 (Receiver 씬이 없으면 자동 생성: ReceiverBootstrap).
    /// </summary>
    public sealed class ReceiverApp : MonoBehaviour
    {
        public int Port = ProtocolConstants.DefaultPort;
        [Tooltip("처음 병합 칸 크기 (m). 같은 칸에 다시 들어온 점은 화면에 새로 그리지 않고 평균에 합쳐 points.ply 로 저장한다. 화면에서 원본/5mm/1cm 로 바꿀 수 있다")] public float AccumulateVoxel = 0.005f;

        [Tooltip("베이스 마커 이미지. 원점에 실제 크기·기울기로 쿼드를 그린다 (비우면 노란 사각형)")] public Texture2D MarkerTexture;
        ScanServer _server;
        MarkerQuadView _markerQuad;
        // 병합: 받은 원본은 _raw 에 두고, 화면·저장은 선택한 칸으로 합친다 (같은 물체를 다시 비춰도 겹쳐 쌓이지 않게)
        static readonly float[] MergeSizes = { 0.001f, 0.005f, 0.01f };
        static readonly string[] MergeLabels = { "원본 (1mm)", "5mm", "1cm" };
        int _mergeIdx = 1;
        readonly List<PointXYZRGB> _raw = new List<PointXYZRGB>();
        VoxelSet _displaySet;
        // 최신 프레임만 보기: 가장 최근 키프레임 시각의 청크만 따로 그린다
        PointCloudView _live;
        bool _liveOnly;
        double _liveTime = double.NegativeInfinity;
        float MergeSize => MergeSizes[_mergeIdx];
        ScanSession _session;
        PointCloudView _cloud;
        // 메쉬: 받는 동안은 블록 그대로, 단순화를 고르면 합친 메쉬 하나로 바꿔 보여주고 mesh.obj 로 저장한다
        MeshView _meshView;
        static readonly float[] SimplifyErrors = { 0f, 0.002f, 0.005f, 0.01f };
        static readonly string[] SimplifyLabels = { "원본", "2mm", "5mm", "1cm" };
        int _simplifyIdx = 2;
        bool _showPoints = true, _showMesh = true, _wireframe;
        Task<IndexedMesh> _simplify;
        string _simplifyDir;
        int _simplifySrcTris;
        float _simplifyErr;
        IndexedMesh _loadedMesh;   // 불러온 세션의 mesh_raw.obj
        string _loadedDir;
        string _meshStatus = "";
        RackSpawner _spawner;
        readonly List<string> _log = new List<string>();
        string _status = "대기 중";
        string _deviceInfo = "";
        string _lastState = "";
        string _rejectReason = "";

        // 분석 설정 UI
        bool _useSpec;
        string _bayWidth = "2.70", _levelPitch = "1.40", _firstLevel = "0", _depth = "1.10";
        bool _showObjects = true, _detectRacks = true;
        Task<AnalysisResult> _analysis;
        AnalysisResult _result;
        string _analyzedDir;
        Vector2 _sessionScroll, _logScroll;
        bool _showSessions;

        string SessionsRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "sessions"));

        void Start()
        {
            Application.runInBackground = true;
            _cloud = new GameObject("PointCloud").AddComponent<PointCloudView>();
            _cloud.transform.SetParent(transform, false);
            _live = new GameObject("LatestFrame").AddComponent<PointCloudView>();
            _live.transform.SetParent(transform, false);
            _live.gameObject.SetActive(false);
            _meshView = new GameObject("ScanMesh").AddComponent<MeshView>();
            _meshView.transform.SetParent(transform, false);
            _mergeIdx = NearestMerge(AccumulateVoxel);
            _displaySet = new VoxelSet(MergeSize);
            _spawner = GetComponent<RackSpawner>();
            if (_spawner == null) _spawner = gameObject.AddComponent<RackSpawner>(); // ?? 는 Unity 의 가짜 null 에서 틀리므로 쓰지 않는다
            SceneHelpers.CreateAxesAndGrid(transform);
            _markerQuad = MarkerQuadView.Create(transform, MarkerTexture);
            var cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera").AddComponent<Camera>(); cam.tag = "MainCamera"; }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            if (cam.GetComponent<OrbitCamera>() == null) cam.gameObject.AddComponent<OrbitCamera>();

            try
            {
                _server = new ScanServer(Port);
                _server.Start();
                Log($"수신 대기: 포트 {_server.Port}, 이 PC 주소 {string.Join(", ", ScanServer.LocalIPv4Addresses())}");
            }
            catch (Exception e)
            {
                _status = "서버 시작 실패: " + e.Message;
                Log(_status);
            }
        }

        void OnDestroy()
        {
            _server?.Dispose();
            _session?.Dispose();
        }

        void Update()
        {
            if (_server != null)
            {
                int budget = 200; // 한 프레임에 처리할 이벤트 수 제한
                while (budget-- > 0 && _server.TryDequeue(out var e)) Handle(e);
            }
            if (_analysis != null && _analysis.IsCompleted)
            {
                if (_analysis.IsFaulted) { Log("분석 실패: " + _analysis.Exception?.GetBaseException().Message); }
                else
                {
                    _result = _analysis.Result;
                    _spawner.Spawn(_result, _showObjects);
                    Log(_result.Summary());
                    if (_analyzedDir != null)
                    {
                        File.WriteAllText(Path.Combine(_analyzedDir, SessionMeta.ResultFileName), _result.ToJson());
                        Log("저장: " + Path.Combine(_analyzedDir, SessionMeta.ResultFileName));
                    }
                }
                _analysis = null;
            }
            if (_simplify != null && _simplify.IsCompleted)
            {
                if (_simplify.IsFaulted) Log("메쉬 단순화 실패: " + _simplify.Exception?.GetBaseException().Message);
                else
                {
                    var m = _simplify.Result;
                    _meshView.ShowMerged(m);
                    double pct = _simplifySrcTris > 0 ? 100.0 * m.TriangleCount / _simplifySrcTris : 0;
                    _meshStatus = $"단순화 {_simplifyErr * 1000:F0}mm: 삼각형 {_simplifySrcTris:N0} → {m.TriangleCount:N0} ({pct:F1}%)";
                    Log(_meshStatus);
                    if (_simplifyDir != null)
                    {
                        try { Log("저장: " + ScanSession.SaveSimplifiedMesh(_simplifyDir, m, _simplifyErr, _simplifySrcTris)); }
                        catch (Exception e) { Log("mesh.obj 저장 실패: " + e.Message); }
                    }
                }
                _simplify = null;
            }
        }

        /// <summary>지금 보이는 세션의 원본 메쉬 (수신 중 세션의 블록을 합친 것, 또는 불러온 mesh_raw.obj).</summary>
        IndexedMesh CurrentRawMesh(out string dir)
        {
            if (_session != null && _session.MeshBlocks.Count > 0) { dir = _session.SessionDir; return _session.BuildMergedMesh(); }
            dir = _loadedDir;
            return _loadedMesh;
        }

        /// <summary>고른 단순화 단계로 메쉬를 다시 만든다. "원본" 이면 원래 밀도로 보여 준다.</summary>
        void ApplyMeshLevel()
        {
            var raw = CurrentRawMesh(out var dir);
            if (raw == null || raw.TriangleCount == 0) return;
            if (SimplifyErrors[_simplifyIdx] <= 0)
            {
                if (_session != null && _session.MeshBlocks.Count > 0) _meshView.ShowBlocks(); else _meshView.ShowMerged(raw);
                _meshStatus = $"원본: 삼각형 {raw.TriangleCount:N0}";
                return;
            }
            if (_simplify != null) return; // 끝나면 다시 고르면 된다
            _simplifyErr = SimplifyErrors[_simplifyIdx];
            _simplifySrcTris = raw.TriangleCount;
            _simplifyDir = dir;
            float err = _simplifyErr;
            _simplify = Task.Run(() => MeshSimplifier.Simplify(raw, new SimplifyOptions { MaxError = err }));
            _meshStatus = $"단순화 중... ({raw.TriangleCount:N0} 삼각형)";
        }

        void ApplyVisibility()
        {
            _cloud.gameObject.SetActive(_showPoints && !_liveOnly);
            _live.gameObject.SetActive(_showPoints && _liveOnly);
            _meshView.gameObject.SetActive(_showMesh);
        }

        void Handle(ServerEvent e)
        {
            switch (e.Kind)
            {
                case ServerEventKind.Connected:
                    _deviceInfo = e.Hello.DeviceInfo;
                    _rejectReason = "";
                    if (_session == null || _session.SessionId != e.SessionId)
                    {
                        // 새 세션 (같은 세션 ID 면 재연결로 보고 이어서 받는다)
                        _session?.Dispose();
                        ClearDisplay();
                        _spawner.Clear();
                        _result = null;
                        _meshView.Clear();
                        _loadedMesh = null; _loadedDir = null; _meshStatus = "";
                        _session = new ScanSession(SessionsRoot, e.SessionId, e.Hello.DeviceInfo, MergeSize);
                        Log($"새 세션 {e.SessionId} ({e.Hello.DeviceInfo}) from {e.Remote}");
                    }
                    else Log($"재연결: {e.SessionId}");
                    _status = "연결됨";
                    break;
                case ServerEventKind.Rejected:
                    _rejectReason = e.Text;
                    Log($"연결 거절 ({e.Remote}): {e.Text}");
                    break;
                case ServerEventKind.Disconnected:
                    if (!_server.IsClientConnected) _status = "연결 끊김 (아이패드가 다시 연결하면 이어 받음)";
                    Log($"끊김: {e.Text}");
                    break;
                case ServerEventKind.Message:
                    if (_session == null || e.SessionId != _session.SessionId) break;
                    if (e.Message is StateMessage st) { _lastState = st.State.ToString(); break; }
                    var chunk = _session.Apply(e.Message, e.RawFrame);
                    if (chunk != null) AddChunk(chunk);
                    if (e.Message is MeshBlockMessage mb)
                    {
                        // 세션이 이 판을 받아들였을 때만 (옛 판이면 무시됨)
                        bool current = _session.MeshBlocks.TryGetValue(mb.BlockId, out var cur) ? ReferenceEquals(cur, mb) : mb.IsRemoval;
                        if (current)
                        {
                            if (_meshView.ShowingMerged) _meshView.ShowBlocks(); // 새 블록이 오면 실시간 블록 표시로
                            _meshView.SetBlock(mb);
                            _meshStatus = $"수신 중: 블록 {_session.MeshBlocks.Count} · 삼각형 {_session.MeshTriangleCount:N0}";
                        }
                    }
                    if (e.Message is MarkerDefMessage mdq)
                        _markerQuad.SetMarker(new Vector3(mdq.Marker.Position.X, mdq.Marker.Position.Y, mdq.Marker.Position.Z), mdq.Marker.PrintedWidth, mdq.Marker.PrintedHeight);
                    if (e.Message is AlignedMessage alq) _markerQuad.SetTilt(alq.PitchDeg, alq.RollDeg);
                    if (e.Message is MarkerDefMessage md) Log($"마커 정의: {md.Marker.Id}, {md.Marker.PrintedWidth * 100:F1}×{md.Marker.PrintedHeight * 100:F1}cm");
                    if (e.Message is AlignedMessage al) Log($"정렬 완료: {al.MarkerId}, 거리 {al.Distance:F2}m, 시야각 {al.ViewAngleDeg:F1}°, pitch {al.PitchDeg:F1}° roll {al.RollDeg:F1}° ({al.FrameCount}프레임)");
                    if (e.Message is EndScanMessage es) Log($"촬영완료: 총 {es.TotalChunks} 청크");
                    if (_session.IsComplete && !_session.Saved)
                    {
                        _session.Save();
                        string meshNote = _session.MeshBlocks.Count > 0 ? $", 메쉬 삼각형 {_session.Meta.MeshTriangleCount:N0} → {SessionMeta.MeshRawFileName}" : "";
                        Log($"세션 저장: {_session.SessionDir} (점 {_session.Meta.PointCount}{meshNote})");
                        if (_session.MeshBlocks.Count > 0) ApplyMeshLevel();
                    }
                    break;
            }
        }

        void RunAnalysis(IReadOnlyList<PointXYZRGB> points, string sessionDir)
        {
            if (_analysis != null) return;
            var s = new AnalysisSettings { DetectRacks = _detectRacks };
            if (_useSpec)
            {
                s.Spec = new RackSpec
                {
                    BayWidth = F(_bayWidth),
                    LevelPitch = F(_levelPitch),
                    FirstLevelHeight = F(_firstLevel),
                    Depth = F(_depth),
                };
            }
            _analyzedDir = sessionDir;
            var copy = new List<PointXYZRGB>(points);
            _analysis = Task.Run(() => RackAnalyzer.Analyze(copy, s));
            Log($"분석 시작 ({copy.Count}점, {(_useSpec ? "규격 입력(A)" : "자동(B)")})");
        }

        void LoadSession(string dir)
        {
            try
            {
                var pts = ScanSession.LoadPoints(dir, out var meta);
                ClearDisplay();
                _spawner.Clear();
                _raw.AddRange(pts);
                RebuildDisplay();
                Log($"불러옴: {Path.GetFileName(dir)} ({pts.Count}점)");
                _meshView.Clear();
                _meshStatus = "";
                _loadedDir = dir;
                _loadedMesh = ScanSession.LoadMesh(dir);
                if (_loadedMesh != null)
                {
                    Log($"메쉬 불러옴: {SessionMeta.MeshRawFileName} (삼각형 {_loadedMesh.TriangleCount:N0})");
                    if (_session == null || _session.MeshBlocks.Count == 0) ApplyMeshLevel();
                }
                RunAnalysis(pts, dir);
            }
            catch (Exception e) { Log("불러오기 실패: " + e.Message); }
        }

        static int NearestMerge(float size)
        {
            int best = 0;
            for (int i = 1; i < MergeSizes.Length; i++)
                if (Mathf.Abs(MergeSizes[i] - size) < Mathf.Abs(MergeSizes[best] - size)) best = i;
            return best;
        }

        void ClearDisplay()
        {
            _raw.Clear();
            _cloud.Clear();
            _live.Clear();
            _liveTime = double.NegativeInfinity;
            _displaySet = new VoxelSet(MergeSize);
        }

        void AddChunk(PointChunkMessage chunk)
        {
            _raw.AddRange(chunk.Points);
            AddToDisplay(chunk.Points);
            // 아이패드 원본 모드는 한 프레임이 같은 키프레임 시각의 청크 여러 개로 온다
            if (chunk.KeyframeTime > _liveTime + 0.3) { _live.Clear(); _liveTime = chunk.KeyframeTime; }
            _live.AddPoints(chunk.Points);
        }

        void AddToDisplay(IReadOnlyList<PointXYZRGB> pts)
        {
            var fresh = new List<PointXYZRGB>(pts.Count);
            for (int i = 0; i < pts.Count; i++) if (_displaySet.TryAdd(pts[i])) fresh.Add(pts[i]);
            if (fresh.Count > 0) _cloud.AddPoints(fresh);
        }

        void RebuildDisplay()
        {
            _cloud.Clear();
            _displaySet = new VoxelSet(MergeSize);
            AddToDisplay(_raw);
        }

        void SetMerge(int idx)
        {
            if (idx == _mergeIdx) return;
            _mergeIdx = idx;
            RebuildDisplay();
            if (_session != null)
            {
                bool wasSaved = _session.Saved;
                _session.ResetAccumulator(MergeSize, _raw);
                if (wasSaved) { _session.Save(); Log($"병합 {MergeLabels[_mergeIdx]} 로 다시 저장 (점 {_session.Meta.PointCount:N0})"); }
            }
        }

        void OnGUI()
        {
            GUI.skin.label.fontSize = GUI.skin.button.fontSize = GUI.skin.toggle.fontSize = GUI.skin.textField.fontSize = 14;
            GUILayout.BeginArea(new Rect(10, 10, 380, Screen.height - 20), GUI.skin.box);
            GUILayout.Label($"<b>Rack Scanner 수신</b>  (프로토콜 v{ProtocolConstants.Version})", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"주소: {string.Join(", ", ScanServer.LocalIPv4Addresses())} : {_server?.Port}");
            GUILayout.Label($"상태: {_status} {(_lastState != "" ? "/ " + _lastState : "")}");
            if (_rejectReason != "") GUILayout.Label("<color=red>거절: " + _rejectReason + "</color>", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
            if (_session != null)
            {
                GUILayout.Label($"세션: {_session.SessionId}  {_deviceInfo}");
                GUILayout.Label($"청크 {_session.ReceivedChunks}{(_session.ExpectedChunks.HasValue ? "/" + _session.ExpectedChunks : "")} · 받은 점 {_raw.Count:N0} · 병합 후 {_session.Points.Count:N0}");
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("병합 칸", GUILayout.Width(60));
            int merge = GUILayout.Toolbar(_mergeIdx, MergeLabels);
            GUILayout.EndHorizontal();
            if (merge != _mergeIdx) SetMerge(merge);
            bool live = GUILayout.Toggle(_liveOnly, $"최신 프레임만 보기 ({_live.PointCount:N0}점)");
            if (live != _liveOnly)
            {
                _liveOnly = live;
                ApplyVisibility();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            bool showPts = GUILayout.Toggle(_showPoints, "점군");
            bool showMesh = GUILayout.Toggle(_showMesh, $"메쉬 ({_meshView.BlockCount} 블록)");
            bool wire = GUILayout.Toggle(_wireframe, "와이어프레임");
            GUILayout.EndHorizontal();
            if (showPts != _showPoints || showMesh != _showMesh) { _showPoints = showPts; _showMesh = showMesh; ApplyVisibility(); }
            if (wire != _wireframe) { _wireframe = wire; _meshView.Wireframe = wire; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("단순화 오차", GUILayout.Width(80));
            GUI.enabled = _simplify == null;
            int level = GUILayout.Toolbar(_simplifyIdx, SimplifyLabels);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (level != _simplifyIdx) { _simplifyIdx = level; ApplyMeshLevel(); }
            if (_meshStatus != "") GUILayout.Label(_meshStatus, new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 });

            GUILayout.Space(6);
            _detectRacks = GUILayout.Toggle(_detectRacks, "랙 검출 (끄면 물체 박스만: M3)");
            _showObjects = GUILayout.Toggle(_showObjects, "물체 박스 표시");
            _useSpec = GUILayout.Toggle(_useSpec, "규격 입력 (A단계)");
            if (_useSpec)
            {
                Field("칸 폭 (m)", ref _bayWidth);
                Field("층 간격 (m)", ref _levelPitch);
                Field("첫 빔 높이 (0=자동)", ref _firstLevel);
                Field("깊이 (m, 0=자동)", ref _depth);
            }
            GUI.enabled = _analysis == null;
            if (_session != null && GUILayout.Button(_analysis == null ? "현재 세션 분석" : "분석 중..."))
            {
                if (!_session.Saved) _session.Save();
                RunAnalysis(_session.Points.ToPoints(), _session.SessionDir);
            }
            if (GUILayout.Button(_showSessions ? "세션 목록 닫기" : "저장된 세션 열기")) _showSessions = !_showSessions;
            GUI.enabled = true;
            if (_showSessions)
            {
                _sessionScroll = GUILayout.BeginScrollView(_sessionScroll, GUILayout.Height(150));
                foreach (var d in ScanSession.ListSaved(SessionsRoot))
                    if (GUILayout.Button(Path.GetFileName(d))) { _showSessions = false; LoadSession(d); }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(6);
            _logScroll = GUILayout.BeginScrollView(_logScroll);
            for (int i = _log.Count - 1; i >= 0; i--) GUILayout.Label(_log[i], new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 });
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void Field(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(170));
            value = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
        }

        static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        void Log(string s)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {s}");
            if (_log.Count > 200) _log.RemoveAt(0);
            Debug.Log("[Receiver] " + s);
        }
    }

    /// <summary>"Receiver" 라는 이름의 씬에 ReceiverApp 이 없으면 자동으로 만든다 (씬은 빈 씬이어도 된다).</summary>
    static class ReceiverBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (!scene.Contains("Receiver")) return;
            if (UnityEngine.Object.FindAnyObjectByType<ReceiverApp>() != null) return;
            new GameObject("ReceiverApp").AddComponent<ReceiverApp>();
        }
    }
}
