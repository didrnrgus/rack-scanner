using System.Collections.Generic;
using RackScanner.Shared.Mesh;
using RackScanner.Shared.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace RackScanner.Receiver.View
{
    /// <summary>
    /// 스캔 메쉬 표시. 두 가지 모드:
    /// - 블록: 받는 대로 ARKit 블록마다 오브젝트 하나 (실시간 확인용, 원래 밀도)
    /// - 합친 메쉬: 다듬기·단순화 결과 같은 메쉬 하나 (블록 표시는 숨김). 정점 색(카메라 색)이 있으면 그 색으로 그릴 수 있다.
    /// 와이어프레임을 켜면 삼각형 엣지를 선으로 겹쳐 그려 넓은 면이 얼마나 합쳐졌는지 보인다.
    /// 좌표는 마커 기준 Unity 좌표 그대로 (변환 없음).
    /// </summary>
    public sealed class MeshView : MonoBehaviour
    {
        sealed class Item
        {
            public GameObject Surface, Wire;
            public Vector3[] Vertices;
            public int[] Indices;
        }

        Material _surface, _surfaceColored, _wire;
        readonly Dictionary<uint, Item> _blocks = new Dictionary<uint, Item>();
        Item _merged;
        Transform _blockRoot;
        bool _wireframe;

        public int BlockCount => _blocks.Count;
        public bool ShowingMerged => _merged != null;

        void Awake()
        {
            var shader = Shader.Find("RackScanner/Mesh");
            if (shader == null) { Debug.LogError("RackScanner/Mesh shader not found"); shader = Shader.Find("Sprites/Default"); }
            _surface = new Material(shader);
            _surfaceColored = new Material(shader);
            _surfaceColored.SetFloat("_VertexColor", 1f);
            _wire = PointCloudView.CreateVertexColorMaterial();
            _wire.SetColor("_Tint", new Color(0.05f, 0.05f, 0.08f, 1f));
            _blockRoot = new GameObject("Blocks").transform;
            _blockRoot.SetParent(transform, false);
        }

        public bool Wireframe
        {
            get => _wireframe;
            set
            {
                if (_wireframe == value) return;
                _wireframe = value;
                foreach (var it in _blocks.Values) UpdateWire(it, it.Surface.transform.parent);
                if (_merged != null) UpdateWire(_merged, transform);
            }
        }

        /// <summary>블록 하나의 새 판을 반영한다 (정점 0개 = 삭제).</summary>
        public void SetBlock(MeshBlockMessage b)
        {
            if (_blocks.TryGetValue(b.BlockId, out var it)) { DestroyItem(it); _blocks.Remove(b.BlockId); }
            if (b.IsRemoval) return;
            var v = new Vector3[b.Vertices.Length];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector3(b.Vertices[i].X, b.Vertices[i].Y, b.Vertices[i].Z);
            it = Build("block " + b.BlockId, v, b.Indices, _blockRoot);
            _blocks[b.BlockId] = it;
        }

        /// <summary>합친(다듬은·단순화한) 메쉬를 보여주고 블록 표시는 숨긴다. useColors 이고 정점 색이 있으면 카메라 색으로 그린다.</summary>
        public void ShowMerged(IndexedMesh m, bool useColors = true)
        {
            ClearMerged();
            var v = new Vector3[m.VertexCount];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector3(m.Vertices[i].X, m.Vertices[i].Y, m.Vertices[i].Z);
            Color32[] cols = null;
            if (useColors && m.HasColors)
            {
                cols = new Color32[v.Length];
                for (int i = 0; i < cols.Length; i++) { var c = m.Colors[i]; cols[i] = new Color32(c.R, c.G, c.B, 255); }
            }
            _merged = Build("merged", v, m.Indices.ToArray(), transform, cols);
            _blockRoot.gameObject.SetActive(false);
        }

        /// <summary>블록 표시로 돌아간다.</summary>
        public void ShowBlocks()
        {
            ClearMerged();
            _blockRoot.gameObject.SetActive(true);
        }

        public void Clear()
        {
            foreach (var it in _blocks.Values) DestroyItem(it);
            _blocks.Clear();
            ShowBlocks();
        }

        void ClearMerged()
        {
            if (_merged == null) return;
            DestroyItem(_merged);
            _merged = null;
        }

        /// <summary>오브젝트와 함께 Mesh 에셋도 지운다 (블록은 1초마다 갱신되므로 남기면 메모리가 샌다).</summary>
        static void DestroyItem(Item it)
        {
            DestroyWire(it);
            if (it.Surface == null) return;
            var mf = it.Surface.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(it.Surface);
        }

        static void DestroyWire(Item it)
        {
            if (it.Wire == null) return;
            var mf = it.Wire.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(it.Wire);
            it.Wire = null;
        }

        Item Build(string name, Vector3[] v, int[] idx, Transform parent, Color32[] colors = null)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = v;
            if (colors != null) mesh.colors32 = colors;
            mesh.SetIndices(idx, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = colors != null ? _surfaceColored : _surface;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            var it = new Item { Surface = go, Vertices = v, Indices = idx };
            UpdateWire(it, parent);
            return it;
        }

        void UpdateWire(Item it, Transform parent)
        {
            if (!_wireframe)
            {
                DestroyWire(it);
                return;
            }
            if (it.Wire != null) return;
            // 삼각형 엣지를 중복 없이 선으로
            var seen = new HashSet<long>(EdgeKeyComparer.Instance);
            var lines = new List<int>(it.Indices.Length * 2);
            for (int t = 0; t + 2 < it.Indices.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = it.Indices[t + k], b = it.Indices[t + (k + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (seen.Add(key)) { lines.Add(a); lines.Add(b); }
                }
            var mesh = new Mesh { name = "wire", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = it.Vertices;
            var cols = new Color32[it.Vertices.Length];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = cols;
            mesh.SetIndices(lines, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            var go = new GameObject("wire");
            go.transform.SetParent(it.Surface.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _wire;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            it.Wire = go;
        }
    }
}
