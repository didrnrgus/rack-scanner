using UnityEngine;

namespace RackScanner.Receiver.View
{
    /// <summary>
    /// 마우스 궤도 카메라 (Unity 씬 뷰와 비슷하게).
    /// - 우클릭 드래그: 회전 (Target 기준)
    /// - 휠 버튼 드래그, 또는 Shift/Ctrl + 우클릭 드래그: 패닝 (Target 을 화면 평면으로 이동)
    /// - 휠: 확대/축소
    /// - WASD / QE (Shift 누르면 빠르게): Target 이동 (앞뒤·좌우는 수평면 기준, QE 는 위아래)
    /// - F: 원점(마커)으로, R: 처음 시점으로
    /// </summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Vector3 Target = new Vector3(0, 0.5f, 0);
        public float Distance = 6f, Yaw = 30f, Pitch = 35f;
        [Tooltip("WASD 이동 속도 (거리 1m 당 m/s)")] public float KeyMoveSpeed = 0.6f;

        Vector3 _startTarget;
        float _startDistance, _startYaw, _startPitch;

        void Start()
        {
            _startTarget = Target; _startDistance = Distance; _startYaw = Yaw; _startPitch = Pitch;
        }

        void LateUpdate()
        {
            // 구 Input Manager 사용 (Player Settings → Active Input Handling = Both).
            float dx = Input.GetAxis("Mouse X"), dy = Input.GetAxis("Mouse Y");
            bool modifier = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                            || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool pan = Input.GetMouseButton(2) || (Input.GetMouseButton(1) && modifier);
            bool orbit = Input.GetMouseButton(1) && !modifier;

            if (pan)
                Target -= (transform.right * dx + transform.up * dy) * Distance * 0.05f;
            else if (orbit)
            {
                Yaw += dx * 4f;
                Pitch = Mathf.Clamp(Pitch - dy * 4f, -89f, 89f);
            }

            Distance = Mathf.Clamp(Distance * (1f - Input.mouseScrollDelta.y * 0.1f), 0.1f, 100f);

            // 키보드 이동: 수평면 기준 앞/옆 + 위아래
            var fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            var right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            var move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += fwd;
            if (Input.GetKey(KeyCode.S)) move -= fwd;
            if (Input.GetKey(KeyCode.D)) move += right;
            if (Input.GetKey(KeyCode.A)) move -= right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
            if (GUIUtility.keyboardControl != 0) move = Vector3.zero; // OnGUI 입력칸에 타이핑 중
            if (move != Vector3.zero)
            {
                float speed = KeyMoveSpeed * Mathf.Max(0.5f, Distance) * (modifier ? 3f : 1f);
                Target += move.normalized * speed * Time.unscaledDeltaTime;
            }

            if (Input.GetKeyDown(KeyCode.F)) Target = Vector3.zero;
            if (Input.GetKeyDown(KeyCode.R)) { Target = _startTarget; Distance = _startDistance; Yaw = _startYaw; Pitch = _startPitch; }

            var rot = Quaternion.Euler(Pitch, Yaw, 0);
            transform.SetPositionAndRotation(Target - rot * Vector3.forward * Distance, rot);
        }
    }
}
