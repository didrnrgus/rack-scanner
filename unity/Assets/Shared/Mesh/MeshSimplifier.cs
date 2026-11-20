using System;
using System.Collections.Generic;
using System.Numerics;

namespace RackScanner.Shared.Mesh
{
    public sealed class SimplifyOptions
    {
        /// <summary>
        /// 허용 오차 (m). 합친 정점이 원래 면들에서 이만큼까지 벗어나도 된다.
        /// 평평한 면은 오차가 0 이라 큰 삼각형 몇 개로 합쳐지고, 굴곡·모서리가 많은 곳은 오차가 금방 차서 원래 밀도가 남는다.
        /// </summary>
        public float MaxError = 0.005f;
        /// <summary>합친 뒤 주변 삼각형 법선이 원래와 이 값(cos) 미만으로 어긋나면 합치지 않는다 (면 뒤집힘 방지).</summary>
        public float MinNormalDot = 0.2f;
        /// <summary>열린 가장자리(구멍·스캔 끝)를 지키는 무게. 1 이면 가장자리도 MaxError 만큼만 움직인다.</summary>
        public float BoundaryWeight = 1f;
        /// <summary>
        /// 단순화 전 잡음 제거(<see cref="MeshDenoiser"/>) 의 법선 필터 반복 수. 0 이면 끈다.
        /// 끄면 LiDAR 잡음 때문에 넓은 면이 잘 합쳐지지 않는다 (1mm 잡음 2cm 격자에서 5mm 오차로 원래의 ~22% 까지만 줄어듦).
        /// </summary>
        public int DenoiseIterations = 5;
        /// <summary>이보다 크게 꺾인 이웃 면은 잡음 제거에서 섞지 않는다 (모서리 보존, 도).</summary>
        public float FeatureAngleDeg = 30f;
    }

    /// <summary>
    /// 적응형 메쉬 단순화: Garland–Heckbert 이차 오차(QEM) 기반 엣지 붕괴.
    /// 정점마다 "주변 원래 면들까지 거리 제곱의 합" 을 들고 다니며, 그 오차가 가장 작은 엣지부터 합친다.
    /// 오차가 MaxError² 를 넘으면 멈추므로 결과 삼각형 크기는 형상의 복잡도를 따라간다 (넓은 면 = 크게, 복잡한 곳 = 원래대로).
    /// 원래 메쉬보다 촘촘하게 만들지는 않는다 (세분화는 하지 않음).
    /// (UnityEngine 비의존)
    /// </summary>
    public static class MeshSimplifier
    {
        public static IndexedMesh Simplify(IndexedMesh src, SimplifyOptions o = null)
        {
            o = o ?? new SimplifyOptions();
            if (o.DenoiseIterations > 0) src = MeshDenoiser.Denoise(src, o.DenoiseIterations, o.DenoiseIterations * 2, o.FeatureAngleDeg);
            var s = new State(src, o);
            s.Run();
            return s.Output();
        }

        struct Quadric
        {
            public double A00, A01, A02, A03, A11, A12, A13, A22, A23, A33;

            public static Quadric Plane(Vector3 n, float d, double w)
            {
                double a = n.X, b = n.Y, c = n.Z, dd = d;
                return new Quadric
                {
                    A00 = w * a * a, A01 = w * a * b, A02 = w * a * c, A03 = w * a * dd,
                    A11 = w * b * b, A12 = w * b * c, A13 = w * b * dd,
                    A22 = w * c * c, A23 = w * c * dd,
                    A33 = w * dd * dd,
                };
            }

            public static Quadric operator +(in Quadric x, in Quadric y) => new Quadric
            {
                A00 = x.A00 + y.A00, A01 = x.A01 + y.A01, A02 = x.A02 + y.A02, A03 = x.A03 + y.A03,
                A11 = x.A11 + y.A11, A12 = x.A12 + y.A12, A13 = x.A13 + y.A13,
                A22 = x.A22 + y.A22, A23 = x.A23 + y.A23,
                A33 = x.A33 + y.A33,
            };

            public double Eval(Vector3 p)
            {
                double x = p.X, y = p.Y, z = p.Z;
                double e = A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x
                         + A11 * y * y + 2 * A12 * y * z + 2 * A13 * y
                         + A22 * z * z + 2 * A23 * z
                         + A33;
                return e > 0 ? e : 0;
            }

            /// <summary>오차를 최소로 하는 위치 (3×3 선형계). 면들이 거의 한 평면/한 직선이면 해가 불안정해 false.</summary>
            public bool TryOptimal(out Vector3 p)
            {
                double det = A00 * (A11 * A22 - A12 * A12) - A01 * (A01 * A22 - A12 * A02) + A02 * (A01 * A12 - A11 * A02);
                double tr = A00 + A11 + A22;
                if (Math.Abs(det) <= 1e-6 * Math.Max(1, tr * tr * tr)) { p = default; return false; }
                double bx = -A03, by = -A13, bz = -A23;
                double dx = bx * (A11 * A22 - A12 * A12) - A01 * (by * A22 - A12 * bz) + A02 * (by * A12 - A11 * bz);
                double dy = A00 * (by * A22 - A12 * bz) - bx * (A01 * A22 - A12 * A02) + A02 * (A01 * bz - by * A02);
                double dz = A00 * (A11 * bz - by * A12) - A01 * (A01 * bz - by * A02) + bx * (A01 * A12 - A11 * A02);
                p = new Vector3((float)(dx / det), (float)(dy / det), (float)(dz / det));
                return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
            }
        }

        struct Candidate
        {
            public double Cost;
            public int U, V, StampU, StampV;
            public Vector3 P;
        }

        /// <summary>비용이 작은 것부터 꺼내는 이진 힙.</summary>
        sealed class Heap
        {
            Candidate[] _a = new Candidate[1024];
            int _n;
            public int Count => _n;

            public void Push(in Candidate c)
            {
                if (_n == _a.Length) Array.Resize(ref _a, _n * 2);
                int i = _n++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_a[parent].Cost <= c.Cost) break;
                    _a[i] = _a[parent];
                    i = parent;
                }
                _a[i] = c;
            }

            public Candidate Pop()
            {
                var top = _a[0];
                var last = _a[--_n];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= _n) break;
                    int r = l + 1;
                    int m = r < _n && _a[r].Cost < _a[l].Cost ? r : l;
                    if (_a[m].Cost >= last.Cost) break;
                    _a[i] = _a[m];
                    i = m;
                }
                if (_n > 0) _a[i] = last;
                return top;
            }
        }

        sealed class State
        {
            readonly SimplifyOptions _o;
            readonly Vector3[] _pos;
            readonly int[] _tri;
            readonly bool[] _triAlive;
            readonly List<int>[] _vtris;
            readonly Quadric[] _q;
            readonly bool[] _vAlive, _locked;
            readonly int[] _stamp, _mark;
            int _gen;
            readonly Heap _heap = new Heap();
            readonly List<int> _nbr = new List<int>();

            public State(IndexedMesh src, SimplifyOptions o)
            {
                _o = o;
                int nv = src.Vertices.Count, nt = src.TriangleCount;
                _pos = src.Vertices.ToArray();
                _tri = new int[nt * 3];
                src.Indices.CopyTo(0, _tri, 0, nt * 3);
                _triAlive = new bool[nt];
                _vtris = new List<int>[nv];
                _q = new Quadric[nv];
                _vAlive = new bool[nv];
                _locked = new bool[nv];
                _stamp = new int[nv];
                _mark = new int[nv];

                var faceN = new Vector3[nt];
                for (int t = 0; t < nt; t++)
                {
                    int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
                    if (a == b || b == c || a == c) continue;
                    var n = Vector3.Cross(_pos[b] - _pos[a], _pos[c] - _pos[a]);
                    float len = n.Length();
                    if (!(len > 1e-12f)) continue;
                    n /= len;
                    faceN[t] = n;
                    _triAlive[t] = true;
                    var pq = Quadric.Plane(n, -Vector3.Dot(n, _pos[a]), 1);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = _tri[3 * t + k];
                        _q[v] = _q[v] + pq;
                        (_vtris[v] ?? (_vtris[v] = new List<int>(8))).Add(t);
                        _vAlive[v] = true;
                    }
                }

                // 엣지마다 붙은 면 수: 1 = 열린 가장자리, 2 = 내부, 3 이상 = 비다양체(손대지 않음)
                var edges = new Dictionary<long, (int count, int face)>();
                for (int t = 0; t < nt; t++)
                {
                    if (!_triAlive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        long key = Key(_tri[3 * t + k], _tri[3 * t + (k + 1) % 3], nv);
                        edges[key] = edges.TryGetValue(key, out var e) ? (e.count + 1, e.face) : (1, t);
                    }
                }
                foreach (var kv in edges)
                {
                    int u = (int)(kv.Key / nv), v = (int)(kv.Key % nv);
                    if (kv.Value.count > 2) { _locked[u] = _locked[v] = true; continue; }
                    if (kv.Value.count != 1) continue;
                    // 가장자리를 지나고 면에 수직인 평면: 가장자리를 따라 미끄러지는 건 자유, 바깥/안쪽으로 줄어드는 건 오차
                    var dir = _pos[v] - _pos[u];
                    var bn = Vector3.Cross(dir, faceN[kv.Value.face]);
                    float bl = bn.Length();
                    if (!(bl > 1e-12f)) continue;
                    bn /= bl;
                    var bq = Quadric.Plane(bn, -Vector3.Dot(bn, _pos[u]), _o.BoundaryWeight);
                    _q[u] = _q[u] + bq;
                    _q[v] = _q[v] + bq;
                }
                foreach (var kv in edges)
                    if (kv.Value.count <= 2) Push((int)(kv.Key / nv), (int)(kv.Key % nv));
            }

            static long Key(int a, int b, int nv) => a < b ? (long)a * nv + b : (long)b * nv + a;

            void Push(int u, int v)
            {
                if (_locked[u] || _locked[v]) return;
                var q = _q[u] + _q[v];
                Vector3 pu = _pos[u], pv = _pos[v], mid = (pu + pv) * 0.5f;
                Vector3 best = mid;
                double cost = q.Eval(mid);
                double cu = q.Eval(pu); if (cu < cost) { cost = cu; best = pu; }
                double cv = q.Eval(pv); if (cv < cost) { cost = cv; best = pv; }
                if (q.TryOptimal(out var opt))
                {
                    // 엣지에서 너무 멀리 튀는 해는 수치 오류로 보고 버린다
                    float lim = Math.Max(Vector3.Distance(pu, pv), 1e-4f) * 2f;
                    if (Vector3.Distance(opt, mid) <= lim)
                    {
                        double co = q.Eval(opt);
                        if (co < cost) { cost = co; best = opt; }
                    }
                }
                _heap.Push(new Candidate { Cost = cost, U = u, V = v, StampU = _stamp[u], StampV = _stamp[v], P = best });
            }

            public void Run()
            {
                double limit = (double)_o.MaxError * _o.MaxError;
                while (_heap.Count > 0)
                {
                    var c = _heap.Pop();
                    if (c.Cost > limit) break;
                    if (!_vAlive[c.U] || !_vAlive[c.V] || _stamp[c.U] != c.StampU || _stamp[c.V] != c.StampV) continue;
                    if (!CanCollapse(c.U, c.V, c.P)) continue;
                    Collapse(c.U, c.V, c.P);
                }
            }

            bool Contains(int t, int v) => _tri[3 * t] == v || _tri[3 * t + 1] == v || _tri[3 * t + 2] == v;

            bool CanCollapse(int u, int v, Vector3 p)
            {
                // 이 엣지를 공유하는 면 수
                int shared = 0;
                foreach (int t in _vtris[u]) if (_triAlive[t] && Contains(t, v)) shared++;
                if (shared == 0) return false;

                // 링크 조건: u, v 의 공통 이웃이 공유 면 수와 같아야 붕괴 후에도 다양체가 유지된다
                _gen++;
                foreach (int t in _vtris[u])
                {
                    if (!_triAlive[t]) continue;
                    for (int k = 0; k < 3; k++) { int w = _tri[3 * t + k]; if (w != u && w != v) _mark[w] = _gen; }
                }
                int genU = _gen;
                _gen++;
                int common = 0;
                foreach (int t in _vtris[v])
                {
                    if (!_triAlive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _tri[3 * t + k];
                        if (w == u || w == v || _mark[w] != genU) continue;
                        _mark[w] = _gen; // 한 번만 센다
                        common++;
                    }
                }
                if (common != shared) return false;

                return !Flips(u, v, p) && !Flips(v, u, p);
            }

            /// <summary>a 를 p 로 옮겼을 때 a 의 면(엣지 a-b 를 공유하지 않는 것) 중 뒤집히거나 찌그러지는 게 있는지.</summary>
            bool Flips(int a, int b, Vector3 p)
            {
                foreach (int t in _vtris[a])
                {
                    if (!_triAlive[t] || Contains(t, b)) continue;
                    var p0 = _pos[_tri[3 * t]]; var p1 = _pos[_tri[3 * t + 1]]; var p2 = _pos[_tri[3 * t + 2]];
                    var nOld = Vector3.Cross(p1 - p0, p2 - p0);
                    if (_tri[3 * t] == a) p0 = p; else if (_tri[3 * t + 1] == a) p1 = p; else p2 = p;
                    var nNew = Vector3.Cross(p1 - p0, p2 - p0);
                    float lo = nOld.Length(), ln = nNew.Length();
                    if (!(ln > 1e-12f) || !(lo > 1e-12f)) return true;
                    if (Vector3.Dot(nOld, nNew) / (lo * ln) < _o.MinNormalDot) return true;
                }
                return false;
            }

            void Collapse(int u, int v, Vector3 p)
            {
                foreach (int t in _vtris[v])
                {
                    if (!_triAlive[t]) continue;
                    if (Contains(t, u)) { _triAlive[t] = false; continue; }
                    for (int k = 0; k < 3; k++) if (_tri[3 * t + k] == v) _tri[3 * t + k] = u;
                    _vtris[u].Add(t);
                }
                _vAlive[v] = false;
                _vtris[v] = null;
                _pos[u] = p;
                _q[u] = _q[u] + _q[v];
                _stamp[u]++;
                _vtris[u].RemoveAll(t => !_triAlive[t]);

                _nbr.Clear();
                _gen++;
                foreach (int t in _vtris[u])
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _tri[3 * t + k];
                        if (w == u || _mark[w] == _gen) continue;
                        _mark[w] = _gen;
                        _nbr.Add(w);
                    }
                foreach (int w in _nbr) Push(u, w);
            }

            public IndexedMesh Output()
            {
                var m = new IndexedMesh();
                var map = new int[_pos.Length];
                for (int i = 0; i < map.Length; i++) map[i] = -1;
                for (int t = 0; t < _triAlive.Length; t++)
                {
                    if (!_triAlive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = _tri[3 * t + k];
                        if (map[v] < 0) { map[v] = m.Vertices.Count; m.Vertices.Add(_pos[v]); }
                        m.Indices.Add(map[v]);
                    }
                }
                return m;
            }
        }
    }
}
