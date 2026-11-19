using System;
using System.Collections.Generic;
using System.Numerics;

namespace RackScanner.Shared.Geometry
{
    /// <summary>쿼터니언 평균 (부호 정렬 후 합산·정규화). 서로 가까운 회전들에 대해 정확한 근사.</summary>
    public static class QuaternionMath
    {
        public static Quaternion Average(IReadOnlyList<Quaternion> qs)
        {
            if (qs.Count == 0) return Quaternion.Identity;
            var reference = qs[0];
            var sum = new Vector4();
            for (int i = 0; i < qs.Count; i++)
            {
                var q = qs[i];
                if (Quaternion.Dot(q, reference) < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                sum += new Vector4(q.X, q.Y, q.Z, q.W);
            }
            return Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
        }

        public static float AngleDeg(Quaternion a, Quaternion b)
        {
            float d = Math.Min(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 1f);
            return 2f * (float)Math.Acos(d) * YawMath.Rad2Deg;
        }
    }

    /// <summary>마커 인식 후 1~2초 동안 여러 프레임의 포즈를 모아 평균한다.</summary>
    public sealed class PoseAverager
    {
        readonly List<Vector3> _positions = new List<Vector3>();
        readonly List<Quaternion> _rotations = new List<Quaternion>();
        readonly List<double> _times = new List<double>();

        public int Count => _positions.Count;

        public void Clear() { _positions.Clear(); _rotations.Clear(); _times.Clear(); }

        public void Add(double time, Vector3 position, Quaternion rotation)
        {
            _times.Add(time); _positions.Add(position); _rotations.Add(rotation);
        }

        /// <summary>window 초보다 오래된 샘플을 버린다.</summary>
        public void DropOlderThan(double now, double window)
        {
            int drop = 0;
            while (drop < _times.Count && now - _times[drop] > window) drop++;
            if (drop == 0) return;
            _times.RemoveRange(0, drop); _positions.RemoveRange(0, drop); _rotations.RemoveRange(0, drop);
        }

        /// <summary>시간 범위 (가장 오래된 샘플부터 최신까지, 초).</summary>
        public double Span => _times.Count < 2 ? 0 : _times[_times.Count - 1] - _times[0];

        public Vector3 MeanPosition()
        {
            var s = Vector3.Zero;
            foreach (var p in _positions) s += p;
            return _positions.Count == 0 ? s : s / _positions.Count;
        }

        public Quaternion MeanRotation() => QuaternionMath.Average(_rotations);

        /// <summary>평균 위치에서 각 샘플까지 거리의 최대값 (m). 흔들림 판단용.</summary>
        public float PositionSpread()
        {
            var m = MeanPosition();
            float max = 0;
            foreach (var p in _positions) max = Math.Max(max, Vector3.Distance(p, m));
            return max;
        }
    }

    /// <summary>
    /// 재정렬 시 변환을 한 번에 덮어쓰지 않고 목표로 보간한다 (점군이 튀는 것을 막기 위해).
    /// </summary>
    public sealed class AlignmentSmoother
    {
        public RigidTransform Current { get; private set; } = RigidTransform.Identity;
        public RigidTransform Target { get; private set; } = RigidTransform.Identity;
        public bool HasValue { get; private set; }

        /// <summary>처음 정렬은 즉시 적용, 이후는 목표만 바꾼다.</summary>
        public void SetTarget(RigidTransform target)
        {
            Target = target;
            if (!HasValue) { Current = target; HasValue = true; }
        }

        public void Reset() { HasValue = false; Current = Target = RigidTransform.Identity; }

        /// <param name="dt">경과 시간 (초)</param>
        /// <param name="halfLife">차이가 절반이 되는 시간 (초)</param>
        public void Step(float dt, float halfLife = 0.5f)
        {
            if (!HasValue) return;
            float a = 1f - (float)Math.Pow(0.5, dt / Math.Max(1e-3f, halfLife));
            Current = new RigidTransform(
                Quaternion.Slerp(Current.Rotation, Target.Rotation, a),
                Vector3.Lerp(Current.Translation, Target.Translation, a),
                Current.Scale + (Target.Scale - Current.Scale) * a);
        }
    }
}
