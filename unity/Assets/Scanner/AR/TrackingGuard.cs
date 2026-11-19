#if RS_ARFOUNDATION
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace RackScanner.Scanner.AR
{
    /// <summary>AR 세션 추적 상태를 감시하고, 정상일 때만 수집을 허용한다.</summary>
    public sealed class TrackingGuard
    {
        public bool IsNormal { get; private set; }
        /// <summary>정상 → 비정상으로 바뀐 적이 있으면 true (마커 재정렬 필요). 재정렬 후 Clear.</summary>
        public bool LostSinceAlign { get; private set; }
        public string Description { get; private set; } = "";
        public event System.Action<bool> Changed;

        public void Update()
        {
            bool normal = ARSession.state == ARSessionState.SessionTracking && ARSession.notTrackingReason == NotTrackingReason.None;
            Description = normal ? "정상" : $"{ARSession.state} / {ARSession.notTrackingReason}";
            if (normal != IsNormal)
            {
                IsNormal = normal;
                if (!normal) LostSinceAlign = true;
                Changed?.Invoke(normal);
            }
        }

        public void ClearLost() => LostSinceAlign = false;
    }
}
#endif
