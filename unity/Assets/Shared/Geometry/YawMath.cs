using System;
using System.Numerics;

namespace RackScanner.Shared.Geometry
{
    /// <summary>
    /// Unity 좌표 규약(Y 위, Z 앞) 기준 yaw/pitch/roll 계산.
    /// yaw 는 Unity 의 Quaternion.Euler(0, yaw, 0) 과 같은 부호: yaw 만큼 돌린 +Z 는 (sin, 0, cos).
    /// </summary>
    public static class YawMath
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);

        public static readonly Vector3 Up = Vector3.UnitY;
        public static readonly Vector3 Forward = Vector3.UnitZ;
        public static readonly Vector3 Right = Vector3.UnitX;

        public static Quaternion RotationFromYaw(float yawDeg) => Quaternion.CreateFromAxisAngle(Up, yawDeg * Deg2Rad);

        /// <summary>수평 방향 벡터의 yaw (도). +Z = 0, +X = 90.</summary>
        public static float YawOfDirection(Vector3 dir) => (float)Math.Atan2(dir.X, dir.Z) * Rad2Deg;

        /// <summary>
        /// 마커 회전에서 yaw 를 구한다. 마커 평면 위의 축(로컬 +Z, 없으면 로컬 +X)을 수평면에 투영한다.
        /// 바닥에 놓인 마커는 로컬 +Z, 벽에 붙인 마커는 로컬 +X 가 수평에 가까워 그 축을 쓴다.
        /// 두 경우 모두 "로컬 +Z 가 수평일 때의 yaw" 로 환산해 같은 기준이 되게 한다.
        /// </summary>
        public static float MarkerYaw(Quaternion markerRotation)
        {
            var fwd = Vector3.Transform(Forward, markerRotation);
            var right = Vector3.Transform(Right, markerRotation);
            var fwdH = new Vector3(fwd.X, 0, fwd.Z);
            var rightH = new Vector3(right.X, 0, right.Z);
            if (fwdH.Length() >= rightH.Length())
                return YawOfDirection(fwdH);
            // +X 의 yaw 에서 90° 빼면 +Z 기준 yaw 가 된다.
            return NormalizeDeg(YawOfDirection(rightH) - 90f);
        }

        /// <summary>마커가 보고한 pitch (로컬 +Z 의 수평면 대비 기울기, 도). 바닥 마커는 0 근처.</summary>
        public static float PitchDeg(Quaternion rotation)
        {
            var fwd = Vector3.Normalize(Vector3.Transform(Forward, rotation));
            return (float)Math.Asin(Math.Clamp(fwd.Y, -1f, 1f)) * Rad2Deg;
        }

        /// <summary>마커가 보고한 roll (로컬 +X 의 수평면 대비 기울기, 도). 바닥 마커는 0 근처.</summary>
        public static float RollDeg(Quaternion rotation)
        {
            var right = Vector3.Normalize(Vector3.Transform(Right, rotation));
            return (float)Math.Asin(Math.Clamp(right.Y, -1f, 1f)) * Rad2Deg;
        }

        /// <summary>(-180, 180] 범위로.</summary>
        public static float NormalizeDeg(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg <= -180f) deg += 360f;
            return deg;
        }

        public static float AngleBetweenDeg(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
            return (float)Math.Acos(Math.Clamp(d, -1f, 1f)) * Rad2Deg;
        }
    }
}
