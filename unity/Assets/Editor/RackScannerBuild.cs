using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RackScanner.EditorTools
{
    /// <summary>
    /// 빌드 진입점. 빌드 대상마다 씬을 따로 쓴다 (아이패드: Scanner, PC: Receiver).
    /// CI(GameCI) 는 -customBuildPath 를 넘겨 BuildIOS 를 부른다.
    /// </summary>
    public static class RackScannerBuild
    {
        public const string ScannerScene = "Assets/Scanner/Scenes/Scanner.unity";
        public const string ReceiverScene = "Assets/Receiver/Scenes/Receiver.unity";

        [MenuItem("RackScanner/Build/iOS (Xcode project)")]
        public static void BuildIOS()
        {
            string path = Arg("-customBuildPath") ?? "Builds/iOS";
            PlayerSettings.iOS.cameraUsageDescription = "LiDAR 깊이와 카메라 영상으로 랙을 스캔합니다.";
            Build(BuildTarget.iOS, ScannerScene, path);
        }

        [MenuItem("RackScanner/Build/Windows (Receiver)")]
        public static void BuildWindows()
        {
            string path = Arg("-customBuildPath") ?? "Builds/Windows/RackReceiver.exe";
            Build(BuildTarget.StandaloneWindows64, ReceiverScene, path);
        }

        static void Build(BuildTarget target, string scene, string path)
        {
            if (!File.Exists(scene)) throw new Exception($"씬이 없습니다: {scene} (docs/build-and-install.md 의 '씬 만들기' 참고)");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = path,
                target = target,
                options = BuildOptions.None,
            });
            Debug.Log($"[RackScannerBuild] {target}: {report.summary.result}, {report.summary.totalSize / 1024 / 1024} MB → {path}");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }

        /// <summary>빈 Receiver 씬을 만든다 (ReceiverApp 은 실행 시 자동 생성).</summary>
        [MenuItem("RackScanner/Create Receiver Scene")]
        public static void CreateReceiverScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var type = Type.GetType("RackScanner.Receiver.ReceiverApp, RackScanner.Receiver");
            if (type != null) new GameObject("ReceiverApp", type);
            Directory.CreateDirectory(Path.GetDirectoryName(ReceiverScene));
            EditorSceneManager.SaveScene(scene, ReceiverScene);
            Debug.Log("[RackScanner] created " + ReceiverScene);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
