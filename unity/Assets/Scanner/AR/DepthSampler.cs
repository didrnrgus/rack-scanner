#if RS_ARFOUNDATION
using System;
using System.Collections.Generic;
using RackScanner.Shared.Geometry;
using RackScanner.Shared.Model;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace RackScanner.Scanner.AR
{
    /// <summary>깊이 이미지 픽셀 → 카메라 로컬 축 변환 방식. 기기에서 맞는 것을 고른다 (M1 에서 확정, TODO).</summary>
    public enum DepthAxes
    {
        /// <summary>화면 LandscapeLeft 고정 기준: 이미지 u→+X, v→−Y.</summary>
        LandscapeLeft = 0,
        LandscapeRight = 1,
        Portrait = 2,
        PortraitUpsideDown = 3,
    }

    /// <summary>
    /// 환경 깊이 + 신뢰도 + 카메라 내부 파라미터 + 카메라 포즈로 AR 세션 좌표의 점을 만든다.
    /// 신뢰도 높은 점만, PixelStep 간격으로.
    /// TODO: Jobs/Burst 로 옮기기 (지금은 256×192 전체·3프레임마다를 메인 스레드에서 한다. FPS 가 떨어지면 옮긴다).
    /// </summary>
    public sealed class DepthSampler
    {
        public int PixelStep = 2;
        /// <summary>LiDAR 오차는 거리에 따라 커진다. 물체를 정확히 찍으려고 3m 까지만 쓴다.</summary>
        public float MinDepth = 0.25f, MaxDepth = 3.0f;
        /// <summary>
        /// 가장자리 번짐(flying pixel) 제거: 상하좌우 이웃과 깊이가 이 비율(d 대비)보다 크게 다르면 버린다.
        /// 물체 경계에서 앞·뒤 깊이가 섞여 허공에 뜨는 점을 없앤다. 0 이면 끔.
        /// </summary>
        public float EdgeRelativeJump = 0.03f;
        /// <summary>ARKit 신뢰도: 0 낮음, 1 중간, 2 높음.</summary>
        public byte MinConfidence = 2;
        public bool SampleColor = true;
        public DepthAxes Axes = DepthAxes.LandscapeLeft;

        public int LastDepthWidth { get; private set; }
        public int LastDepthHeight { get; private set; }
        public string LastError { get; private set; } = "";

        NativeArray<byte> _rgba;

        /// <summary>현재 프레임의 점을 AR 세션 좌표로 out 에 추가. 성공하면 true.</summary>
        public bool Sample(AROcclusionManager occlusion, ARCameraManager camera, Transform cameraTransform, List<(Vector3 p, Color32 c)> output)
        {
            if (!camera.TryGetIntrinsics(out var intr)) { LastError = "no intrinsics"; return false; }
            if (!occlusion.TryAcquireEnvironmentDepthCpuImage(out var depth)) { LastError = "no depth image"; return false; }
            using (depth)
            {
                XRCpuImage conf = default;
                bool hasConf = occlusion.TryAcquireEnvironmentDepthConfidenceCpuImage(out conf);
                try
                {
                    int w = depth.width, h = depth.height;
                    LastDepthWidth = w; LastDepthHeight = h;
                    if (depth.format != XRCpuImage.Format.DepthFloat32) { LastError = "depth format " + depth.format; return false; }
                    var dPlane = depth.GetPlane(0);
                    var dData = dPlane.data.Reinterpret<float>(1);
                    int dStride = dPlane.rowStride / 4;
                    NativeArray<byte> cData = default; int cStride = 0;
                    if (hasConf) { var cp = conf.GetPlane(0); cData = cp.data; cStride = cp.rowStride; }

                    bool color = SampleColor && TryGetColor(camera, w, h);

                    // 내부 파라미터는 카메라 이미지 해상도 기준 → 깊이 해상도로 축소
                    float sx = (float)w / intr.resolution.x, sy = (float)h / intr.resolution.y;
                    float fx = intr.focalLength.x * sx, fy = intr.focalLength.y * sy;
                    float cx = intr.principalPoint.x * sx, cy = intr.principalPoint.y * sy;
                    var m = cameraTransform.localToWorldMatrix;

                    for (int v = 0; v < h; v += PixelStep)
                    for (int u = 0; u < w; u += PixelStep)
                    {
                        if (hasConf && cData[v * cStride + u] < MinConfidence) continue;
                        float d = dData[v * dStride + u];
                        if (!(d >= MinDepth && d <= MaxDepth)) continue; // NaN 도 거름
                        if (EdgeRelativeJump > 0f && IsDepthEdge(dData, dStride, w, h, u, v, d)) continue;
                        float x = (u + 0.5f - cx) * d / fx;
                        float y = (v + 0.5f - cy) * d / fy;
                        Vector3 local;
                        switch (Axes)
                        {
                            case DepthAxes.LandscapeRight: local = new Vector3(-x, y, d); break;
                            case DepthAxes.Portrait: local = new Vector3(-y, -x, d); break;
                            case DepthAxes.PortraitUpsideDown: local = new Vector3(y, x, d); break;
                            default: local = new Vector3(x, -y, d); break;
                        }
                        var c = color ? new Color32(_rgba[(v * w + u) * 4], _rgba[(v * w + u) * 4 + 1], _rgba[(v * w + u) * 4 + 2], 255) : new Color32(255, 255, 255, 255);
                        output.Add((m.MultiplyPoint3x4(local), c));
                    }
                    LastError = "";
                    return true;
                }
                finally { if (hasConf) conf.Dispose(); }
            }
        }

        bool IsDepthEdge(NativeArray<float> dData, int stride, int w, int h, int u, int v, float d)
        {
            float limit = d * EdgeRelativeJump;
            if (u > 0 && !(Mathf.Abs(dData[v * stride + u - 1] - d) <= limit)) return true;      // NaN 이웃도 가장자리로 본다
            if (u < w - 1 && !(Mathf.Abs(dData[v * stride + u + 1] - d) <= limit)) return true;
            if (v > 0 && !(Mathf.Abs(dData[(v - 1) * stride + u] - d) <= limit)) return true;
            if (v < h - 1 && !(Mathf.Abs(dData[(v + 1) * stride + u] - d) <= limit)) return true;
            return false;
        }

        /// <summary>카메라 이미지를 깊이 해상도로 RGBA 변환 (같은 센서 방향·4:3 이라 픽셀이 대응).</summary>
        bool TryGetColor(ARCameraManager camera, int w, int h)
        {
            if (!camera.TryAcquireLatestCpuImage(out var img)) return false;
            using (img)
            {
                var p = new XRCpuImage.ConversionParams(img, TextureFormat.RGBA32, XRCpuImage.Transformation.None)
                {
                    outputDimensions = new Vector2Int(w, h),
                };
                int size = img.GetConvertedDataSize(p);
                if (!_rgba.IsCreated || _rgba.Length != size)
                {
                    if (_rgba.IsCreated) _rgba.Dispose();
                    _rgba = new NativeArray<byte>(size, Allocator.Persistent);
                }
                img.Convert(p, _rgba);
                return true;
            }
        }

        public void Dispose() { if (_rgba.IsCreated) _rgba.Dispose(); }
    }
}
#endif
