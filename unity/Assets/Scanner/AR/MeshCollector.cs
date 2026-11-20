#if RS_ARFOUNDATION
using System.Collections.Generic;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Protocol;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using N = System.Numerics;

namespace RackScanner.Scanner.AR
{
    /// <summary>
    /// ARKit 장면 재구성(LiDAR 메쉬)을 블록 단위로 모은다. ARKit 메쉬 앵커 하나 = 블록 하나.
    /// - 씬에 ARMeshManager 가 없으면 XR Origin 아래에 만든다 (씬 수정 없이 동작하게).
    /// - 바뀐 블록만 Interval 마다 읽어 마커 기준 Unity 좌표로 바꾸고 판 번호를 올려 내보낸다.
    /// - 블록 위치(앵커 transform)나 마커 정렬이 바뀌어도 다시 보낸다 (같은 블록의 새 판).
    /// ARKit 메쉬의 해상도는 ARKit 이 정한다 (밀도 설정은 ARKit 에서 쓰이지 않음). 넓은 면/복잡한 곳에 맞춘 단순화는 PC 가 한다.
    /// </summary>
    public sealed class MeshCollector
    {
        sealed class Block
        {
            public uint Id;
            public uint Revision;
            public bool Dirty;
            public int Triangles;
            public N.Vector3 Probe0, Probe1, Probe2; // 지난번 보낸 기준 점 3개 (자세 변화 감지)
        }

        public ARMeshManager Manager { get; private set; }
        /// <summary>같은 블록을 다시 읽어 보내는 최소 간격 (초). 메쉬는 매 프레임 바뀌므로 묶어서 보낸다.</summary>
        public double Interval = 1.0;
        /// <summary>블록 자세가 이만큼(m) 넘게 바뀌면 다시 보낸다.</summary>
        public float PoseEpsilon = 0.002f;
        public string LastError { get; private set; } = "";
        public int BlockCount => _blocks.Count;
        public int TriangleCount { get; private set; }

        readonly Dictionary<MeshFilter, Block> _blocks = new Dictionary<MeshFilter, Block>();
        readonly List<uint> _removed = new List<uint>();
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<int> _tris = new List<int>();
        uint _nextId = 1;
        double _lastCollect = double.NegativeInfinity;
        bool _enabled, _overlay;
        Material _overlayMaterial;

        /// <summary>ARMeshManager 를 찾거나 만든다. 처음에는 꺼진 상태.</summary>
        public MeshCollector(bool overlay)
        {
            _overlay = overlay;
            Manager = Object.FindAnyObjectByType<ARMeshManager>(FindObjectsInactive.Include);
            if (Manager == null)
            {
                var origin = Object.FindAnyObjectByType<XROrigin>();
                if (origin == null) { LastError = "XR Origin 이 없어 메쉬를 만들 수 없음"; return; }
                var go = new GameObject("ARMeshManager (RackScanner)");
                go.SetActive(false); // meshPrefab 을 넣기 전에 OnEnable 이 돌지 않게
                go.transform.SetParent(origin.transform, false);
                go.transform.localScale = Vector3.one * 10f; // AR Foundation 기본값 (경계 볼륨, ARKit 은 무시)
                Manager = go.AddComponent<ARMeshManager>();
                Manager.meshPrefab = CreateTemplate(go.transform.parent);
                Manager.normals = false;
                Manager.tangents = false;
                Manager.textureCoordinates = false;
                Manager.colors = false;
                Manager.enabled = false;
                go.SetActive(true);
            }
            else if (Manager.meshPrefab == null)
            {
                Manager.meshPrefab = CreateTemplate(Manager.transform.parent);
            }
            Manager.meshesChanged += OnMeshesChanged;
            _enabled = Manager.enabled;
        }

        MeshFilter CreateTemplate(Transform parent)
        {
            // 꺼진 holder 아래의 켜진 오브젝트 = 프리팹 대용. Instantiate 결과는 켜진 상태로 나온다.
            var holder = new GameObject("RackScanner Mesh Template Holder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            var t = new GameObject("RackScanner Mesh Block");
            t.transform.SetParent(holder.transform, false);
            var mf = t.AddComponent<MeshFilter>();
            var mr = t.AddComponent<MeshRenderer>();
            mr.sharedMaterial = OverlayMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mf;
        }

        Material OverlayMaterial()
        {
            if (_overlayMaterial != null) return _overlayMaterial;
            var shader = Shader.Find("RackScanner/MeshOverlay");
            if (shader == null) { Debug.LogWarning("RackScanner/MeshOverlay shader not found"); shader = Shader.Find("Sprites/Default"); }
            _overlayMaterial = new Material(shader);
            return _overlayMaterial;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (Manager != null) Manager.enabled = value;
            }
        }

        /// <summary>아이패드 화면에 메쉬를 반투명으로 겹쳐 그릴지 (어디까지 찍혔는지 확인용).</summary>
        public bool Overlay
        {
            get => _overlay;
            set
            {
                _overlay = value;
                foreach (var mf in _blocks.Keys)
                    if (mf != null && mf.TryGetComponent<MeshRenderer>(out var mr)) mr.enabled = value;
            }
        }

        void OnMeshesChanged(ARMeshesChangedEventArgs e)
        {
            if (e.added != null) foreach (var mf in e.added) Touch(mf);
            if (e.updated != null) foreach (var mf in e.updated) Touch(mf);
            if (e.removed != null)
                foreach (var mf in e.removed)
                {
                    if (mf != null && _blocks.TryGetValue(mf, out var b))
                    {
                        _removed.Add(b.Id);
                        TriangleCount -= b.Triangles;
                        _blocks.Remove(mf);
                    }
                }
        }

        void Touch(MeshFilter mf)
        {
            if (mf == null) return;
            if (!_blocks.TryGetValue(mf, out var b))
            {
                b = new Block { Id = _nextId++ };
                _blocks[mf] = b;
                if (mf.TryGetComponent<MeshRenderer>(out var mr)) mr.enabled = _overlay;
            }
            b.Dirty = true;
        }

        /// <summary>모든 블록을 다시 보내게 한다 (새 세션 시작, 마커 재정렬 후).</summary>
        public void MarkAllDirty()
        {
            foreach (var b in _blocks.Values) b.Dirty = true;
        }

        /// <summary>
        /// 바뀐 블록을 마커 기준 좌표로 읽어 output 에 넣는다. force 가 아니면 Interval 에 한 번만.
        /// 삭제된 블록은 정점 0개 블록으로 나간다.
        /// </summary>
        public void Collect(RigidTransform arToUnity, double now, bool force, List<MeshBlockMessage> output)
        {
            if (!force && now - _lastCollect < Interval) return;
            _lastCollect = now;

            foreach (var id in _removed)
                output.Add(new MeshBlockMessage { BlockId = id, Revision = uint.MaxValue, Time = now });
            _removed.Clear();

            List<MeshFilter> dead = null;
            foreach (var kv in _blocks)
            {
                var mf = kv.Key; var b = kv.Value;
                if (mf == null) { (dead ?? (dead = new List<MeshFilter>())).Add(mf); continue; }
                var l2w = mf.transform.localToWorldMatrix;
                var p0 = arToUnity.Apply(l2w.MultiplyPoint3x4(Vector3.zero).ToNumerics());
                var p1 = arToUnity.Apply(l2w.MultiplyPoint3x4(Vector3.right).ToNumerics());
                var p2 = arToUnity.Apply(l2w.MultiplyPoint3x4(Vector3.forward).ToNumerics());
                if (!b.Dirty && b.Revision > 0)
                {
                    float moved = Mathf.Max(N.Vector3.Distance(p0, b.Probe0), Mathf.Max(N.Vector3.Distance(p1, b.Probe1), N.Vector3.Distance(p2, b.Probe2)));
                    if (moved <= PoseEpsilon) continue;
                }
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (!mesh.isReadable) { LastError = "ARKit 메쉬를 CPU 에서 읽을 수 없음"; continue; }

                _verts.Clear(); _tris.Clear();
                mesh.GetVertices(_verts);
                if (mesh.subMeshCount > 0) mesh.GetTriangles(_tris, 0);
                var v = new N.Vector3[_verts.Count];
                for (int i = 0; i < v.Length; i++) v[i] = arToUnity.Apply(l2w.MultiplyPoint3x4(_verts[i]).ToNumerics());
                var msg = new MeshBlockMessage { BlockId = b.Id, Revision = ++b.Revision, Time = now, Vertices = v, Indices = _tris.ToArray() };
                if (msg.Vertices.Length == 0) { b.Dirty = false; continue; } // 빈 블록은 삭제로 오해되지 않게 보내지 않는다
                TriangleCount += msg.TriangleCount - b.Triangles;
                b.Triangles = msg.TriangleCount;
                b.Probe0 = p0; b.Probe1 = p1; b.Probe2 = p2;
                b.Dirty = false;
                output.Add(msg);
            }
            if (dead != null)
                foreach (var mf in dead)
                {
                    // removed 이벤트 없이 사라진 블록 (DestroyAllMeshes 등): PC 에서도 지운다
                    var b = _blocks[mf];
                    TriangleCount -= b.Triangles;
                    _blocks.Remove(mf);
                    if (b.Revision > 0) output.Add(new MeshBlockMessage { BlockId = b.Id, Revision = uint.MaxValue, Time = now });
                }
        }

        public void Dispose()
        {
            if (Manager != null) Manager.meshesChanged -= OnMeshesChanged;
        }
    }
}
#endif
