#if RS_ARFOUNDATION
using System.Collections.Generic;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.IO;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace RackScanner.Scanner.AR
{
    /// <summary>
    /// 마커 인식 → 프레임 평균 → AR → Unity 변환 계산, 비교 로그 기록.
    /// - 가깝고 정면에 가까운 프레임만 평균에 쓴다 (MaxDistance, MaxViewAngle).
    /// - 위치·yaw 는 마커, 기울기는 중력 (Shared.MarkerAlignment).
    /// - 재정렬은 한 번에 덮어쓰지 않고 AlignmentSmoother 로 보간한다.
    /// 지금은 AR Foundation 이미지 트래킹. AprilTag 로 바꿀 때는 Observe() 에 넣는 관측만 바꾸면 된다.
    /// </summary>
    public sealed class MarkerAligner
    {
        public MarkerDefinition Marker;
        public float MaxDistance = 1.5f;
        public float MaxViewAngle = 35f;
        public double Window = 1.5;   // 평균 창 (초)
        public int MinFrames = 20;
        public float MaxSpread = 0.02f; // 창 안 위치 흔들림이 이보다 크면 평균하지 않음

        public bool IsAligned => _smoother.HasValue && !_needRealign;
        public bool MarkerVisible { get; private set; }
        public RigidTransform ArToUnity => _smoother.Current;
        public AlignedMessage LastAligned { get; private set; }
        public int AlignCount { get; private set; }
        public string Hint { get; private set; } = "마커를 비추세요";

        /// <summary>마커를 인식할 때마다 (평균 사용 여부와 함께) 기록.</summary>
        public event System.Action<AlignmentLogEntry> Logged;
        /// <summary>새 평균이 확정될 때마다.</summary>
        public event System.Action<AlignedMessage> Aligned;

        readonly PoseAverager _avg = new PoseAverager();
        readonly AlignmentSmoother _smoother = new AlignmentSmoother();
        bool _needRealign;
        // 평균 창(_avg)과 같은 프레임의 통계. 창에서 빠진 프레임은 여기서도 빼야 로그 평균이 맞다.
        // (예전에는 합계만 누적해서, 창에서 프레임이 빠질수록 거리·pitch 평균이 부풀었다: pitch 255° 같은 값)
        readonly Queue<FrameStat> _stats = new Queue<FrameStat>();

        struct FrameStat { public double Time; public float Dist, Angle, Pitch, Roll; }

        public MarkerAligner(MarkerDefinition marker) { Marker = marker; }

        /// <summary>추적이 끊겼다 돌아오면 호출: 마커를 다시 비춰 재정렬할 때까지 IsAligned=false.</summary>
        public void RequireRealign(string hint = null)
        {
            _needRealign = true;
            _avg.Clear();
            ResetSums();
            Hint = hint ?? "추적이 복구됐습니다. 마커를 다시 비춰 재정렬하세요";
        }

        public void Update(ARTrackedImageManager images, Transform cameraTransform, double time, float dt)
        {
            MarkerVisible = false;
            if (images != null)
            {
                foreach (var img in images.trackables)
                {
                    if (img.referenceImage.name != Marker.Id || img.trackingState != TrackingState.Tracking) continue;
                    MarkerVisible = true;
                    Observe(img.transform.position, img.transform.rotation, cameraTransform.position, time);
                }
            }
            _avg.DropOlderThan(time, Window);
            while (_stats.Count > 0 && time - _stats.Peek().Time > Window) _stats.Dequeue();
            _smoother.Step(dt);
        }

        void Observe(Vector3 pos, Quaternion rot, Vector3 camPos, double time)
        {
            float dist = Vector3.Distance(pos, camPos);
            // 이미지 트래킹: 마커 법선 = 로컬 +Y. 정면이면 0°.
            float viewAngle = Vector3.Angle(rot * Vector3.up, camPos - pos);
            var nq = rot.ToNumerics();
            float pitch = YawMath.PitchDeg(nq), roll = YawMath.RollDeg(nq);
            bool used = dist <= MaxDistance && viewAngle <= MaxViewAngle;
            Logged?.Invoke(new AlignmentLogEntry { Time = time, MarkerId = Marker.Id, Distance = dist, ViewAngleDeg = viewAngle, PitchDeg = pitch, RollDeg = roll, Used = used });
            if (!used)
            {
                Hint = dist > MaxDistance ? $"마커에 더 가까이 ({dist:F1}m)" : $"마커를 더 정면에서 ({viewAngle:F0}°)";
                return;
            }
            _avg.Add(time, pos.ToNumerics(), nq);
            _stats.Enqueue(new FrameStat { Time = time, Dist = dist, Angle = viewAngle, Pitch = pitch, Roll = roll });
            if (_avg.Count < MinFrames) { Hint = $"마커 인식 중 {_avg.Count}/{MinFrames}"; return; }
            if (_avg.PositionSpread() > MaxSpread) { Hint = "기기를 잠시 멈춰 주세요"; return; }

            var t = MarkerAlignment.ComputeArToUnity(_avg.MeanPosition(), _avg.MeanRotation(), Marker);
            _smoother.SetTarget(t);
            float sumDist = 0, sumAngle = 0, sumPitch = 0, sumRoll = 0;
            foreach (var f in _stats) { sumDist += f.Dist; sumAngle += f.Angle; sumPitch += f.Pitch; sumRoll += f.Roll; }
            int n = Mathf.Max(1, _stats.Count);
            LastAligned = new AlignedMessage
            {
                MarkerId = Marker.Id,
                Distance = sumDist / n, ViewAngleDeg = sumAngle / n, PitchDeg = sumPitch / n, RollDeg = sumRoll / n,
                FrameCount = (ushort)_avg.Count,
            };
            AlignCount++;
            _needRealign = false;
            Hint = "정렬됨";
            Aligned?.Invoke(LastAligned);
            _avg.Clear();
            ResetSums();
        }

        void ResetSums() { _stats.Clear(); }
    }
}
#endif
