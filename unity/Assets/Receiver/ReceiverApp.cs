using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using RackScanner.Analysis;
using RackScanner.Receiver.Net;
using RackScanner.Receiver.Session;
using RackScanner.Receiver.View;
using RackScanner.Shared.IO;
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
        [Tooltip("PC 누적 복셀 크기 (m). 같은 칸의 점을 평균해 points.ply 로 저장한다. 1mm 면 사실상 받은 점 그대로 (아이패드 원본 모드용)")] public float AccumulateVoxel = 0.001f;

        ScanServer _server;
        ScanSession _session;
        PointCloudView _cloud;
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
            _spawner = GetComponent<RackSpawner>();
            if (_spawner == null) _spawner = gameObject.AddComponent<RackSpawner>(); // ?? 는 Unity 의 가짜 null 에서 틀리므로 쓰지 않는다
            SceneHelpers.CreateAxesAndGrid(transform);
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
                        _cloud.Clear();
                        _spawner.Clear();
                        _result = null;
                        _session = new ScanSession(SessionsRoot, e.SessionId, e.Hello.DeviceInfo, AccumulateVoxel);
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
                    if (chunk != null) _cloud.AddPoints(chunk.Points);
                    if (e.Message is MarkerDefMessage md) Log($"마커 정의: {md.Marker.Id}, {md.Marker.PrintedWidth * 100:F1}×{md.Marker.PrintedHeight * 100:F1}cm");
                    if (e.Message is AlignedMessage al) Log($"정렬 완료: {al.MarkerId}, 거리 {al.Distance:F2}m, 시야각 {al.ViewAngleDeg:F1}°, pitch {al.PitchDeg:F1}° roll {al.RollDeg:F1}° ({al.FrameCount}프레임)");
                    if (e.Message is EndScanMessage es) Log($"촬영완료: 총 {es.TotalChunks} 청크");
                    if (_session.IsComplete && !_session.Saved)
                    {
                        _session.Save();
                        Log($"세션 저장: {_session.SessionDir} (점 {_session.Meta.PointCount})");
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
                _cloud.Clear();
                _spawner.Clear();
                _cloud.AddPoints(pts);
                Log($"불러옴: {Path.GetFileName(dir)} ({pts.Count}점)");
                RunAnalysis(pts, dir);
            }
            catch (Exception e) { Log("불러오기 실패: " + e.Message); }
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
                GUILayout.Label($"청크 {_session.ReceivedChunks}{(_session.ExpectedChunks.HasValue ? "/" + _session.ExpectedChunks : "")} · 복셀 {_session.Points.Count:N0} · 표시 {_cloud.PointCount:N0}");
            }

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
