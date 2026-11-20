#if RS_ARFOUNDATION
using RackScanner.Scanner.AR;
using UnityEngine;

namespace RackScanner.Scanner
{
    /// <summary>
    /// 상태 표시와 버튼 (IMGUI). 점군 표시는 추적 성능을 위해 그리지 않고 숫자만 보여준다.
    /// </summary>
    public sealed class ScanUI : MonoBehaviour
    {
        ScannerApp _app;
        string _port, _mw, _mh, _voxel;
        bool _showSessions;
        Vector2 _scroll;

        void Start()
        {
            _app = GetComponent<ScannerApp>();
            _port = _app.PcPort.ToString();
            _mw = (_app.MarkerPrintedWidth * 100).ToString("F1");
            _mh = (_app.MarkerPrintedHeight * 100).ToString("F1");
            _voxel = (_app.VoxelSize * 100).ToString("F1");
        }

        void OnGUI()
        {
            if (_app == null || _app.Tracking == null) return;
            float scale = Screen.height / 720f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUI.skin.label.fontSize = GUI.skin.button.fontSize = GUI.skin.textField.fontSize = 16;
            var rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };

            GUILayout.BeginArea(new Rect(10, 10, 420, 700), GUI.skin.box);
            GUILayout.Label($"추적: {C(_app.Tracking.IsNormal)}{_app.Tracking.Description}</color>", rich);
            GUILayout.Label($"마커: {C(_app.Aligner.IsAligned)}{(_app.Aligner.IsAligned ? "정렬됨" : "미정렬")}</color> · {(_app.Aligner.MarkerVisible ? "보임" : "안 보임")} · {_app.Aligner.Hint}", rich);
            var s = _app.Sender;
            string net = s == null ? "미연결" : s.IsConnected ? $"연결됨 (ACK {s.AckedCount}, 대기 {s.PendingCount})" : $"연결 시도 중… {s.LastError}";
            GUILayout.Label($"PC: {C(s != null && s.IsConnected)}{net}</color>", rich);
            GUILayout.Label($"상태: {_app.State} · 점 {_app.TotalPoints:N0} · {_app.Fps:F0}fps · 프레임 {_app.FrameCount}");
            if (_app.SessionId != null) GUILayout.Label($"세션: {_app.SessionId}");
            if (!string.IsNullOrEmpty(_app.Message)) GUILayout.Label(_app.Message, rich);
            if (!string.IsNullOrEmpty(_app.MarkerSizer.Status)) GUILayout.Label(_app.MarkerSizer.Status);
            if (!string.IsNullOrEmpty(_app.Depth.LastError)) GUILayout.Label("깊이: " + _app.Depth.LastError);

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("PC 주소", GUILayout.Width(80));
            _app.PcHost = GUILayout.TextField(_app.PcHost, GUILayout.Width(180));
            _port = GUILayout.TextField(_port, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("마커 cm", GUILayout.Width(80));
            _mw = GUILayout.TextField(_mw, GUILayout.Width(70));
            GUILayout.Label("×", GUILayout.Width(15));
            _mh = GUILayout.TextField(_mh, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("복셀 cm", GUILayout.Width(80));
            _voxel = GUILayout.TextField(_voxel, GUILayout.Width(70));
            GUILayout.Label("작을수록 촘촘 (새 세션부터)");
            GUILayout.EndHorizontal();

            GUI.enabled = _app.State != ScannerApp.Phase.Scanning && _app.State != ScannerApp.Phase.Paused;
            if (GUILayout.Button("PC 연결 (새 세션)", GUILayout.Height(44)))
            {
                ApplyFields();
                _app.BeginSession(connect: true);
            }
            GUI.enabled = _app.Aligner.IsAligned && _app.State != ScannerApp.Phase.Scanning && _app.State != ScannerApp.Phase.Paused;
            if (GUILayout.Button("촬영 시작", GUILayout.Height(44)))
            {
                ApplyFields();
                if (_app.State == ScannerApp.Phase.Completed) _app.BeginSession(connect: _app.Sender != null);
                _app.StartScan();
            }
            GUI.enabled = _app.State == ScannerApp.Phase.Scanning || _app.State == ScannerApp.Phase.Paused;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_app.State == ScannerApp.Phase.Paused ? "재개" : "일시정지", GUILayout.Height(44))) _app.TogglePause();
            if (GUILayout.Button("촬영완료", GUILayout.Height(44))) _app.CompleteScan();
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (GUILayout.Button($"깊이 축: {_app.Depth.Axes} (기기 확인용)")) _app.Depth.Axes = (DepthAxes)(((int)_app.Depth.Axes + 1) % 4);
            if (GUILayout.Button(_showSessions ? "세션 목록 닫기" : "저장된 세션 (PC로 재전송)")) _showSessions = !_showSessions;
            if (_showSessions)
            {
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(200));
                foreach (var id in _app.StoredSessions())
                    if (GUILayout.Button(id)) { ApplyFields(); _app.ResendStored(id); _showSessions = false; }
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }

        void ApplyFields()
        {
            if (int.TryParse(_port, out var p)) _app.PcPort = p;
            _app.MarkerPrintedWidth = ScannerApp.ParseFloat(_mw, _app.MarkerPrintedWidth * 100) / 100f;
            _app.MarkerPrintedHeight = ScannerApp.ParseFloat(_mh, _app.MarkerPrintedHeight * 100) / 100f;
            _app.VoxelSize = ScannerApp.ParseFloat(_voxel, _app.VoxelSize * 100) / 100f;
        }

        static string C(bool ok) => ok ? "<color=#5f5>" : "<color=#f77>";
    }
}
#endif
