using System.Numerics;
using RackScanner.Shared.Model;

namespace RackScanner.Shared.Geometry
{
    /// <summary>
    /// AR 세션 좌표 → Unity(마커 기준) 좌표 변환 계산.
    /// 위치와 yaw 는 마커에서, 기울기(pitch·roll)는 중력에서 얻는다:
    /// ARKit 세션 좌표는 이미 중력 정렬(Y 위)이므로 Y 축 회전만으로 맞춘다. 스케일은 1 (LiDAR 미터 단위).
    /// </summary>
    public static class MarkerAlignment
    {
        /// <param name="observedPosition">AR 세션 좌표에서 관측한 마커 중심 (여러 프레임 평균)</param>
        /// <param name="observedRotation">AR 세션 좌표에서 관측한 마커 회전 (여러 프레임 평균)</param>
        /// <param name="marker">마커의 Unity 상 위치·회전 정의</param>
        public static RigidTransform ComputeArToUnity(Vector3 observedPosition, Quaternion observedRotation, MarkerDefinition marker)
        {
            float yawAr = YawMath.MarkerYaw(observedRotation);
            float yawUnity = YawMath.MarkerYaw(marker.Rotation);
            var rot = YawMath.RotationFromYaw(YawMath.NormalizeDeg(yawUnity - yawAr));
            var t = marker.Position - Vector3.Transform(observedPosition, rot);
            return new RigidTransform(rot, t, 1f);
        }

        /// <summary>두 정렬 결과의 차이 (이동 거리 m, yaw 차이 도). 재정렬 판단과 테스트용.</summary>
        public static void Difference(RigidTransform a, RigidTransform b, out float translation, out float yawDeg)
        {
            translation = Vector3.Distance(a.Translation, b.Translation);
            float ya = YawMath.YawOfDirection(Vector3.Transform(YawMath.Forward, a.Rotation));
            float yb = YawMath.YawOfDirection(Vector3.Transform(YawMath.Forward, b.Rotation));
            yawDeg = System.Math.Abs(YawMath.NormalizeDeg(ya - yb));
        }
    }
}
