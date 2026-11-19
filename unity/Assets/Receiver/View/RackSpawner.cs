using System.Collections.Generic;
using RackScanner.Analysis;
using UnityEngine;

namespace RackScanner.Receiver.View
{
    /// <summary>
    /// 7단계 생성: 분석 결과의 치수로 빈 랙(기둥·빔)을 배치한다. 물류는 뼈대가 아니므로 결과에 없다.
    /// 프리팹(RackAssets)이 지정돼 있으면 쓰고, 없으면 기본 큐브로 만든다.
    /// 신뢰도가 낮은 랙은 다른 색으로 표시해 사람이 확인하게 한다.
    /// </summary>
    public sealed class RackSpawner : MonoBehaviour
    {
        [Tooltip("기둥 프리팹 (1m 높이, 피벗 바닥 중앙). 비우면 큐브")] public GameObject UprightPrefab;
        [Tooltip("빔 프리팹 (Z 방향 1m 길이, 피벗 중앙). 비우면 큐브")] public GameObject BeamPrefab;
        public float LowConfidence = 0.7f;
        public Color RackColor = new Color(1f, 0.55f, 0.1f);
        public Color LowConfidenceColor = new Color(1f, 0.15f, 0.15f);
        public Color InferredColor = new Color(0.6f, 0.6f, 0.6f);
        public Color ObjectColor = new Color(0.3f, 0.7f, 1f, 1f);
        public float UprightSize = 0.08f, BeamHeight = 0.1f, BeamThickness = 0.05f;

        readonly List<GameObject> _spawned = new List<GameObject>();

        public void Clear()
        {
            foreach (var go in _spawned) Destroy(go);
            _spawned.Clear();
        }

        public void Spawn(AnalysisResult result, bool showObjects)
        {
            Clear();
            foreach (var r in result.Racks) _spawned.Add(SpawnRack(r));
            if (showObjects)
                foreach (var o in result.Objects) _spawned.Add(SpawnObject(o));
        }

        GameObject SpawnRack(RackResult r)
        {
            var root = new GameObject($"{r.RackId} ({r.Row}열, 칸 {r.BayCount}, 층 {r.LevelCount})");
            root.transform.SetParent(transform, false);
            // 로컬 +Z = 길이(칸) 방향, +X = 깊이, 원점 = 1번 칸 앞 모서리 바닥
            root.transform.SetPositionAndRotation(new Vector3(r.PoseX, r.PoseY, r.PoseZ), Quaternion.Euler(0, r.YawDeg, 0));
            var color = r.Confidence < LowConfidence ? LowConfidenceColor : RackColor;
            float top = r.Height;
            for (int k = 0; k < r.UprightPositions.Count; k++)
            {
                float z = r.UprightPositions[k];
                var c = r.UprightObserved[k] ? color : InferredColor;
                foreach (float x in new[] { UprightSize / 2, r.Depth - UprightSize / 2 })
                    Part(root.transform, UprightPrefab, new Vector3(x, top / 2, z), new Vector3(UprightSize, top, UprightSize), c, $"Upright{k + 1}");
            }
            for (int li = 0; li < r.LevelHeights.Count; li++)
            {
                float h = r.LevelHeights[li];
                var c = r.LevelObserved[li] ? color : InferredColor;
                for (int b = 0; b < r.UprightPositions.Count - 1; b++)
                {
                    float z0 = r.UprightPositions[b], z1 = r.UprightPositions[b + 1];
                    foreach (float x in new[] { BeamThickness / 2, r.Depth - BeamThickness / 2 })
                        Part(root.transform, BeamPrefab, new Vector3(x, h, (z0 + z1) / 2), new Vector3(BeamThickness, BeamHeight, z1 - z0 - UprightSize), c, $"Beam L{li + 1} B{b + 1}");
                }
            }
            return root;
        }

        GameObject SpawnObject(ObjectBox o)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = o.Id;
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(new Vector3(o.CenterX, o.CenterY, o.CenterZ), Quaternion.Euler(0, o.YawDeg, 0));
            go.transform.localScale = new Vector3(o.Width, o.Height, o.Length);
            SetColor(go, ObjectColor);
            return go;
        }

        static void Part(Transform parent, GameObject prefab, Vector3 localPos, Vector3 size, Color color, string name)
        {
            GameObject go;
            if (prefab != null)
            {
                // 프리팹 규약 (TODO: RackAssets 프리팹이 생기면 확정): 1m 단위 크기로 만들고 스케일로 늘린다
                go = Instantiate(prefab, parent);
                go.transform.localPosition = localPos;
                go.transform.localScale = size;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(parent, false);
                go.transform.localPosition = localPos;
                go.transform.localScale = size;
                SetColor(go, color);
            }
            go.name = name;
        }

        static void SetColor(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", c);      // Built-in
            mpb.SetColor("_BaseColor", c);  // URP
            r.SetPropertyBlock(mpb);
        }
    }
}
