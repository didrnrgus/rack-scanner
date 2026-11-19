#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace RackScanner.EditorTools
{
    /// <summary>
    /// iOS 빌드 후 Info.plist 에 필요한 키를 넣는다.
    /// - NSLocalNetworkUsageDescription: PC 로 TCP 전송 (iOS 로컬 네트워크 권한)
    /// - UIFileSharingEnabled / LSSupportsOpeningDocumentsInPlace: 파일 앱·Finder 에서 세션 파일을 꺼내 PC 로 백업
    /// 카메라 사용 이유는 PlayerSettings.iOS.cameraUsageDescription (RackScannerBuild.BuildIOS) 로 넣는다.
    /// </summary>
    public static class IosPostprocess
    {
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;
            root.SetString("NSLocalNetworkUsageDescription", "스캔한 점군을 같은 와이파이의 PC 로 보냅니다.");
            if (!root.values.ContainsKey("NSCameraUsageDescription"))
                root.SetString("NSCameraUsageDescription", "LiDAR 깊이와 카메라 영상으로 랙을 스캔합니다.");
            root.SetBoolean("UIFileSharingEnabled", true);
            root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
