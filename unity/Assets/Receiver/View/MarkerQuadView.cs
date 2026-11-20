using UnityEngine;

namespace RackScanner.Receiver.View
{
    /// <summary>
    /// 베이스 마커를 원점에 실제 크기·기울기로 그린다 (점군과 겹쳐 보며 정렬과 크기를 눈으로 확인).
    /// - 크기: 아이패드가 보낸 MARKER_DEF 의 인쇄(표시) 크기
    /// - 기울기: ALIGNED 의 pitch·roll (중력 기준). 바닥에 눕히면 pitch≈0, 벽·모니터에 세우면 pitch≈90.
    /// - 방향(yaw): 정렬이 마커의 yaw 를 Unity 기준으로 맞추므로 고정. 마커 로컬 +X = Unity +X.
    /// 마커 로컬 좌표는 AR Foundation 이미지 트래킹과 같다: 이미지 평면 XZ, 법선 +Y, 이미지 위쪽 +Z.
    /// </summary>
    public sealed class MarkerQuadView : MonoBehaviour
    {
        Transform _quad;
        Material _material;
        float _width = 0.2f, _height = 0.2f, _pitch, _roll;

        public static MarkerQuadView Create(Transform parent, Texture2D texture)
        {
            var go = new GameObject("MarkerQuad");
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<MarkerQuadView>();
            view.Build(texture);
            go.SetActive(false); // MARKER_DEF 를 받으면 보인다
            return view;
        }

        void Build(Texture2D texture)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.name = "Quad";
            quad.transform.SetParent(transform, false);
            // Unity Quad: XY 평면, 보이는 면 -Z. X 축으로 90° 돌리면 XZ 평면, 보이는 면 +Y, 텍스처 위쪽 +Z.
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _quad = quad.transform;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            _material = new Material(shader);
            if (texture != null)
            {
                _material.mainTexture = texture;
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", texture);
            }
            else if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", new Color(1f, 0.8f, 0.2f));
            // 양면으로 보이게 (뒤에서 봐도 마커 위치를 알 수 있게)
            if (_material.HasProperty("_Cull")) _material.SetFloat("_Cull", 0f);
            quad.GetComponent<MeshRenderer>().sharedMaterial = _material;
            Apply();
        }

        /// <summary>MARKER_DEF: 마커 위치와 인쇄 크기 (m).</summary>
        public void SetMarker(Vector3 position, float width, float height)
        {
            transform.localPosition = position;
            _width = Mathf.Max(0.01f, width);
            _height = Mathf.Max(0.01f, height);
            gameObject.SetActive(true);
            Apply();
        }

        /// <summary>ALIGNED: 정렬에 쓴 프레임들의 평균 pitch·roll (도).</summary>
        public void SetTilt(float pitchDeg, float rollDeg)
        {
            _pitch = pitchDeg;
            _roll = rollDeg;
            Apply();
        }

        void Apply()
        {
            if (_quad == null) return;
            // pitch = 로컬 +Z(이미지 위쪽)가 수평면에서 들린 각도. X 축으로 -pitch 돌리면 +Z 가 위로 들린다.
            // roll = 로컬 +X 가 수평면에서 기운 각도 (보통 1° 이내).
            transform.localRotation = Quaternion.Euler(-_pitch, 0f, 0f) * Quaternion.Euler(0f, 0f, _roll);
            _quad.localScale = new Vector3(_width, _height, 1f);
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
