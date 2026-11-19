using System.Collections.Generic;
using RackScanner.Shared.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace RackScanner.Receiver.View
{
    /// <summary>
    /// 점군 표시: 청크 단위 Mesh(Points 토폴로지). 점이 수백만 개를 넘으면 GraphicsBuffer 렌더링/VFX Graph 로 전환 (TODO).
    /// </summary>
    public sealed class PointCloudView : MonoBehaviour
    {
        Material _material;
        readonly List<GameObject> _chunks = new List<GameObject>();
        public int PointCount { get; private set; }

        public static Material CreateVertexColorMaterial()
        {
            var shader = Shader.Find("RackScanner/PointCloud");
            if (shader == null) { Debug.LogError("RackScanner/PointCloud shader not found"); shader = Shader.Find("Sprites/Default"); }
            return new Material(shader);
        }

        void Awake() { _material = CreateVertexColorMaterial(); }

        public void Clear()
        {
            foreach (var go in _chunks) Destroy(go);
            _chunks.Clear();
            PointCount = 0;
        }

        public void AddPoints(IReadOnlyList<PointXYZRGB> pts)
        {
            const int maxPerMesh = 60000;
            for (int start = 0; start < pts.Count; start += maxPerMesh)
            {
                int n = Mathf.Min(maxPerMesh, pts.Count - start);
                var verts = new Vector3[n];
                var cols = new Color32[n];
                var idx = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var p = pts[start + i];
                    verts[i] = new Vector3(p.X, p.Y, p.Z);
                    cols[i] = new Color32(p.R, p.G, p.B, 255);
                    idx[i] = i;
                }
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.vertices = verts;
                mesh.colors32 = cols;
                mesh.SetIndices(idx, MeshTopology.Points, 0);
                mesh.RecalculateBounds();
                var go = new GameObject("chunk");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                _chunks.Add(go);
                PointCount += n;
            }
        }
    }
}
