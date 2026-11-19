using System.Numerics;

namespace RackScanner.Shared.Model
{
    /// <summary>
    /// 마커 정의. 아이패드가 PC로 MARKER_DEF 로 보내 두 기기가 같은 값을 갖는다.
    /// 인쇄 크기는 자로 잰 실측값(미터)을 넣는다.
    /// </summary>
    public sealed class MarkerDefinition
    {
        /// <summary>마커 ID. 이미지 트래킹에서는 Reference Image 이름과 같아야 한다.</summary>
        public string Id = "base";
        public float PrintedWidth = 0.20f;
        public float PrintedHeight = 0.20f;
        /// <summary>마커의 Unity 상 위치. 임시 원점이면 (0,0,0).</summary>
        public Vector3 Position = Vector3.Zero;
        /// <summary>마커의 Unity 상 회전. 임시 원점이면 identity.</summary>
        public Quaternion Rotation = Quaternion.Identity;

        public MarkerDefinition Clone() => (MarkerDefinition)MemberwiseClone();
    }
}
