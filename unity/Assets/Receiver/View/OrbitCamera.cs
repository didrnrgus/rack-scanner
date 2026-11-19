using UnityEngine;

namespace RackScanner.Receiver.View
{
    /// <summary>마우스 궤도 카메라: 우클릭/가운데 드래그 회전, Shift+드래그 이동, 휠 확대.</summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Vector3 Target = new Vector3(0, 0.5f, 0);
        public float Distance = 6f, Yaw = 30f, Pitch = 35f;

        void LateUpdate()
        {
            // 구 Input Manager 사용. 프로젝트가 Input System 전용이면 Player Settings 에서 "Both" 로 (사람이 할 일).
            bool drag = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (drag)
            {
                float dx = Input.GetAxis("Mouse X"), dy = Input.GetAxis("Mouse Y");
                if (Input.GetKey(KeyCode.LeftShift))
                    Target -= (transform.right * dx + transform.up * dy) * Distance * 0.05f;
                else { Yaw += dx * 4f; Pitch = Mathf.Clamp(Pitch - dy * 4f, -10f, 89f); }
            }
            Distance = Mathf.Clamp(Distance * (1f - Input.mouseScrollDelta.y * 0.1f), 0.3f, 100f);
            var rot = Quaternion.Euler(Pitch, Yaw, 0);
            transform.SetPositionAndRotation(Target - rot * Vector3.forward * Distance, rot);
        }
    }
}
