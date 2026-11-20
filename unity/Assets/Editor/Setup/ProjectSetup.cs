using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

namespace RackScanner.EditorTools
{
    /// <summary>
    /// docs/build-and-install.md 의 "1. 프로젝트 열기"(4~6단계)와 "2. 씬 만들기"를 메뉴 한 번으로 한다.
    /// 여러 번 눌러도 된다. 이미 있는 씬·에셋은 덮어쓰지 않는다.
    /// </summary>
    public static class ProjectSetup
    {
        // TODO: 번들 ID 확정 (docs/build-and-install.md 1-6). 무료 Apple ID 로 7일마다 같은 ID 로 재설치하므로 정하면 바꾸지 않는다.
        public const string BundleId = "com.didrnrgus.rackscanner";
        public const string ProductName = "RackScanner";

        const string MarkerDir = "Assets/Scanner/Markers";
        const string MarkerTexture = MarkerDir + "/marker_base.png";
        const string MarkerLibrary = MarkerDir + "/MarkerLibrary.asset";
        const string MarkerName = "base";          // ScannerApp.MarkerId 와 같아야 한다
        const float MarkerSizeMeters = 0.20f;      // ScannerApp.MarkerPrintedWidth 기본값. 인쇄 후 실측값으로 바꾼다
        const string ARKitLoader = "UnityEngine.XR.ARKit.ARKitLoader";

        const string SettingsDir = "Assets/Settings";
        const string UrpAssetPath = SettingsDir + "/URP.asset";
        const string UrpRendererPath = SettingsDir + "/URP_Renderer.asset";

        [MenuItem("RackScanner/Setup Project (Player, XR, Scenes)")]
        public static void Run()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            bool inputChanged = SetupPlayer();
            SetupURP();
            SetupXR();
            SetupMarkerLibrary();
            CreateReceiverScene();
            CreateScannerScene();
            // 빌드는 RackScannerBuild 가 씬을 직접 넘기지만, 에디터 Build Profiles 화면에서도 보이게 등록해 둔다.
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(RackScannerScenes.Receiver, true),
                new EditorBuildSettingsScene(RackScannerScenes.Scanner, true),
            };
            AssetDatabase.SaveAssets();

            string msg = "Player / URP / XR(ARKit) / 마커 라이브러리 / 씬 설정 완료.";
            if (inputChanged) msg += "\n\nActive Input Handling 을 Both 로 바꿨습니다. 에디터를 재시작해야 적용됩니다.";
            // 대화상자는 띄우지 않는다 (MCP 등으로 원격 실행할 때 모달 창이 에디터를 멈춘다).
            if (inputChanged) Debug.LogWarning("[RackScanner] " + msg);
            else Debug.Log("[RackScanner] " + msg);
        }

        /// <returns>Active Input Handling 을 바꿨으면 true (재시작 필요)</returns>
        static bool SetupPlayer()
        {
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.iOS.cameraUsageDescription = "LiDAR 깊이와 카메라 영상으로 랙을 스캔합니다.";
            // ScannerApp 이 실행 중 LandscapeLeft 로 고정한다 (DepthSampler 축 변환 기준). 시작 화면부터 맞춘다.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            // PC 앱은 아이패드 데이터를 받는 동안 창이 포커스를 잃어도 계속 돈다.
            PlayerSettings.runInBackground = true;

            // Active Input Handling: Both (2). PC 앱 궤도 카메라는 구 Input Manager, AR 카메라 TrackedPoseDriver 는 Input System.
            var ps = Resources.FindObjectsOfTypeAll<PlayerSettings>().FirstOrDefault();
            if (ps == null) { Debug.LogWarning("[RackScanner] PlayerSettings 를 찾지 못해 Active Input Handling 은 수동으로 Both 로 바꾸세요."); return false; }
            var so = new SerializedObject(ps);
            var prop = so.FindProperty("activeInputHandler");
            if (prop == null || prop.intValue == 2) return false;
            prop.intValue = 2;
            so.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// URP 에셋 + Universal Renderer 를 만들어 Graphics 기본 파이프라인으로 지정한다.
        /// 렌더러에는 AR 카메라 배경을 그리는 ARBackgroundRendererFeature 를 넣는다 (URP 에서 ARCameraBackground 가 이것을 필요로 함).
        /// PC 앱도 같은 렌더러를 쓴다. AR 카메라가 없으면 이 피처는 아무것도 하지 않는다.
        /// </summary>
        static void SetupURP()
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null)
            {
                Directory.CreateDirectory(SettingsDir);
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                // 메뉴(Create → Rendering → URP Asset)와 같은 기본 후처리 데이터. internal API 라 리플렉션, 실패해도 후처리만 빠진다.
                var getPost = typeof(PostProcessData).GetMethod("GetDefaultPostProcessData", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (getPost != null) renderer.postProcessData = getPost.Invoke(null, null) as PostProcessData;
                AssetDatabase.CreateAsset(renderer, UrpRendererPath);

                var feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
                feature.name = nameof(ARBackgroundRendererFeature);
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
                renderer.SetDirty();
                EditorUtility.SetDirty(renderer);

                asset = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
                AssetDatabase.SaveAssets();
            }

            GraphicsSettings.defaultRenderPipeline = asset;
            // 품질 레벨별 파이프라인은 비워 두면 위 기본값을 쓴다. 다른 값이 들어 있으면 맞춘다.
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                if (QualitySettings.renderPipeline != null && QualitySettings.renderPipeline != asset) QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
        }

        static void SetupXR()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                string guid = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget").FirstOrDefault();
                if (guid != null) perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(guid));
                if (perTarget == null)
                {
                    Directory.CreateDirectory("Assets/XR");
                    perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                }
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }

            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.iOS))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
            var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
            if (!XRPackageMetadataStore.IsLoaderAssigned(ARKitLoader, BuildTargetGroup.iOS)
                && !XRPackageMetadataStore.AssignLoader(manager, ARKitLoader, BuildTargetGroup.iOS))
                Debug.LogWarning("[RackScanner] ARKit 로더 지정 실패. Project Settings → XR Plug-in Management → iOS → ARKit 을 직접 체크하세요.");
            EditorUtility.SetDirty(perTarget);
        }

        static XRReferenceImageLibrary SetupMarkerLibrary()
        {
            ConfigureMarkerTexture();
            var existing = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(MarkerLibrary);
            if (existing != null) return existing;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(MarkerTexture);
            if (tex == null)
            {
                Debug.LogWarning($"[RackScanner] {MarkerTexture} 가 없습니다. tools/make_marker.py 로 만들어 넣고 다시 실행하세요.");
                return null;
            }

            var lib = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
            AssetDatabase.CreateAsset(lib, MarkerLibrary);
            lib.Add();
            lib.SetTexture(0, tex, false);
            lib.SetName(0, MarkerName);
            lib.SetSpecifySize(0, true);
            lib.SetSize(0, new Vector2(MarkerSizeMeters, MarkerSizeMeters));
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            return lib;
        }

        /// <summary>
        /// 앱에서 입력한 cm 로 ARKit 마커 크기를 실행 중에 바꾸려면(MarkerLibrarySizer) 텍스처를 런타임에 읽을 수 있어야 한다:
        /// Read/Write 켜고, ARKit 이 받는 RGBA32 무압축으로.
        /// </summary>
        static void ConfigureMarkerTexture()
        {
            var importer = AssetImporter.GetAtPath(MarkerTexture) as TextureImporter;
            if (importer == null) return;
            bool changed = false;
            if (!importer.isReadable) { importer.isReadable = true; changed = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
            var ios = importer.GetPlatformTextureSettings("iOS");
            if (!ios.overridden || ios.format != TextureImporterFormat.RGBA32)
            {
                ios.overridden = true;
                ios.format = TextureImporterFormat.RGBA32;
                ios.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SetPlatformTextureSettings(ios);
                changed = true;
            }
            if (changed) importer.SaveAndReimport();
        }

        /// <summary>ScannerApp 에 마커 텍스처와 라이브러리 기본 폭을 넣는다. 바꿨으면 true.</summary>
        static bool AssignScannerMarker()
        {
            var appType = Type.GetType("RackScanner.Scanner.ScannerApp, RackScanner.Scanner");
            if (appType == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType(appType);
            if (app == null) return false;
            var so = new SerializedObject(app);
            var tex = so.FindProperty("MarkerTexture");
            var width = so.FindProperty("MarkerLibraryWidth");
            bool changed = false;
            var texAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(MarkerTexture);
            if (tex != null && tex.objectReferenceValue != texAsset) { tex.objectReferenceValue = texAsset; changed = true; }
            if (width != null && !Mathf.Approximately(width.floatValue, MarkerSizeMeters)) { width.floatValue = MarkerSizeMeters; changed = true; }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            return changed;
        }

        static bool AssignReceiverMarker()
        {
            var type = Type.GetType("RackScanner.Receiver.ReceiverApp, RackScanner.Receiver");
            if (type == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType(type);
            if (app == null) return false;
            var so = new SerializedObject(app);
            var prop = so.FindProperty("MarkerTexture");
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(MarkerTexture);
            if (prop == null || prop.objectReferenceValue == tex) return false;
            prop.objectReferenceValue = tex;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        static void CreateReceiverScene()
        {
            string path = RackScannerScenes.Receiver;
            if (File.Exists(path))
            {
                // 이미 있으면 ReceiverApp 의 마커 이미지 연결만 확인한다 (원점 마커 쿼드용).
                var existing = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                if (AssignReceiverMarker()) EditorSceneManager.SaveScene(existing);
                return;
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var type = Type.GetType("RackScanner.Receiver.ReceiverApp, RackScanner.Receiver");
            if (type != null) { new GameObject("ReceiverApp", type); AssignReceiverMarker(); }
            else Debug.LogWarning("[RackScanner] ReceiverApp 타입을 찾지 못했습니다 (실행 시 자동 생성되므로 그대로 둬도 됨).");
            EditorSceneManager.SaveScene(scene, path);
            Debug.Log("[RackScanner] created " + path);
        }

        static void CreateScannerScene()
        {
            string path = RackScannerScenes.Scanner;
            if (File.Exists(path))
            {
                // 이미 있으면 새로 만들지 않고 마커 라이브러리 연결만 확인한다.
                var existing = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var manager = UnityEngine.Object.FindAnyObjectByType<ARTrackedImageManager>();
                bool libChanged = manager != null && AssignMarkerLibrary(manager);
                bool appChanged = AssignScannerMarker();
                if (libChanged || appChanged) EditorSceneManager.SaveScene(existing);
                Debug.Log($"[RackScanner] {path} 가 이미 있어 마커 연결만 확인함 (라이브러리 {libChanged}, ScannerApp {appChanged})");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // AR Foundation 의 메뉴를 그대로 써서 기본 구성(ARSession+ARInputManager, XR Origin+카메라+TrackedPoseDriver)을 만든다.
            Selection.activeObject = null;
            if (!EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session"))
                throw new Exception("메뉴 GameObject/XR/AR Session 실행 실패");
            Selection.activeObject = null; // 선택된 오브젝트 아래에 만들어지지 않게
            if (!EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)"))
                throw new Exception("메뉴 GameObject/XR/XR Origin (Mobile AR) 실행 실패");

            var origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (origin == null || origin.Camera == null) throw new Exception("XR Origin 생성 실패");
            origin.Camera.gameObject.AddComponent<AROcclusionManager>();

            var images = origin.gameObject.AddComponent<ARTrackedImageManager>();
            images.requestedMaxNumberOfMovingImages = 1;
            AssignMarkerLibrary(images);

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var appType = Type.GetType("RackScanner.Scanner.ScannerApp, RackScanner.Scanner");
            if (appType != null) { new GameObject("ScannerApp", appType); AssignScannerMarker(); }
            else Debug.LogWarning("[RackScanner] ScannerApp 타입을 찾지 못했습니다. 빈 GameObject 에 ScannerApp 을 직접 추가하세요.");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            Debug.Log("[RackScanner] created " + path);
        }

        /// <summary>비어 있으면 MarkerLibrary 를 연결한다. 바꿨으면 true.</summary>
        static bool AssignMarkerLibrary(ARTrackedImageManager manager)
        {
            var so = new SerializedObject(manager);
            var prop = so.FindProperty("m_SerializedLibrary");
            if (prop.objectReferenceValue != null) return false;
            // NewScene 이 방금 만든 에셋 참조를 해제할 수 있어서 경로로 다시 읽는다.
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(MarkerLibrary);
            if (library == null) { Debug.LogWarning($"[RackScanner] {MarkerLibrary} 가 없어 AR Tracked Image Manager 에 연결하지 못함"); return false; }
            prop.objectReferenceValue = library;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }

    /// <summary>씬 경로. RackScannerBuild(Assembly-CSharp-Editor) 와 같은 값이어야 한다.</summary>
    static class RackScannerScenes
    {
        public const string Scanner = "Assets/Scanner/Scenes/Scanner.unity";
        public const string Receiver = "Assets/Receiver/Scenes/Receiver.unity";
    }
}
