using System.IO;
using UnityEditor;
using UnityEngine;

namespace RackScanner.EditorTools
{
    /// <summary>
    /// Play 중인 Receiver 화면을 PNG 로 저장한다 (원격 확인용, MCP 의 execute_menu_item 으로 부를 수 있다).
    /// 지금 시점 + 정면·옆·위 시점 4장. 카메라는 원래대로 돌려놓는다. 저장 위치: unity/Temp/ViewCapture/
    /// OrbitCamera 는 Receiver 어셈블리라 직접 참조하지 않고 SerializedObject 로 값만 바꾼다.
    /// </summary>
    public static class ViewCapture
    {
        const string Dir = "Temp/ViewCapture";

        [MenuItem("RackScanner/Debug/Capture Views")]
        public static void CaptureViews()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[ViewCapture] Main Camera 없음 (Play 중인지 확인)"); return; }
            Directory.CreateDirectory(Dir);

            var orbit = cam.GetComponent("OrbitCamera") as MonoBehaviour;
            Save(cam, "0_current");
            if (orbit == null) { Debug.Log("[ViewCapture] OrbitCamera 없음: 지금 시점만 저장"); return; }

            var so = new SerializedObject(orbit);
            var yaw = so.FindProperty("Yaw"); var pitch = so.FindProperty("Pitch");
            var dist = so.FindProperty("Distance"); var target = so.FindProperty("Target");
            float y0 = yaw.floatValue, p0 = pitch.floatValue, d0 = dist.floatValue; var t0 = target.vector3Value;
            var camPos = cam.transform.position; var camRot = cam.transform.rotation;

            Shot(cam, so, yaw, pitch, dist, target, 0f, 10f, 1.6f, Vector3.zero, "1_front");
            Shot(cam, so, yaw, pitch, dist, target, 90f, 10f, 1.6f, Vector3.zero, "2_side");
            Shot(cam, so, yaw, pitch, dist, target, 0f, 89f, 3.0f, Vector3.zero, "3_top");

            yaw.floatValue = y0; pitch.floatValue = p0; dist.floatValue = d0; target.vector3Value = t0;
            so.ApplyModifiedPropertiesWithoutUndo();
            cam.transform.SetPositionAndRotation(camPos, camRot);
            Debug.Log("[ViewCapture] saved to " + Path.GetFullPath(Dir));
        }

        static void Shot(Camera cam, SerializedObject so, SerializedProperty yaw, SerializedProperty pitch, SerializedProperty dist,
            SerializedProperty target, float y, float p, float d, Vector3 t, string name)
        {
            yaw.floatValue = y; pitch.floatValue = p; dist.floatValue = d; target.vector3Value = t;
            so.ApplyModifiedPropertiesWithoutUndo();
            // OrbitCamera.LateUpdate 와 같은 식으로 바로 배치해서 찍는다
            var rot = Quaternion.Euler(p, y, 0);
            cam.transform.SetPositionAndRotation(t - rot * Vector3.forward * d, rot);
            Save(cam, name);
        }

        static void Save(Camera cam, string name)
        {
            const int w = 1280, h = 800;
            var rt = RenderTexture.GetTemporary(w, h, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
