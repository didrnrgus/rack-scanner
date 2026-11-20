#if RS_ARFOUNDATION
using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace RackScanner.Scanner.AR
{
    /// <summary>
    /// 앱에 입력한 마커 실측 크기를 ARKit 레퍼런스 이미지 크기로 적용한다.
    /// ARKit 은 레퍼런스 이미지의 물리 크기로 마커까지 거리를 계산하므로, 빌드에 든 MarkerLibrary 의 크기(기본 20cm)와
    /// 실제 크기가 다르면 보는 위치에 따라 마커 위치가 다르게 잡혀 재정렬마다 원점이 어긋난다 (점군이 겹쳐 번짐).
    /// 크기가 기본값과 다르면 그 크기로 런타임 라이브러리를 새로 만들어 교체한다.
    /// </summary>
    public sealed class MarkerLibrarySizer
    {
        readonly ARTrackedImageManager _images;
        readonly Texture2D _texture;
        readonly string _name;
        float _applied;
        float _pendingWidth;
        MutableRuntimeReferenceImageLibrary _pendingLib;
        AddReferenceImageJobState _job;
        bool _pending, _unsupported;

        /// <summary>UI 에 보여 줄 상태. 비어 있으면 기본 크기 그대로.</summary>
        public string Status { get; private set; } = "";
        /// <summary>지금 ARKit 이 쓰는 마커 폭 (m).</summary>
        public float AppliedWidth => _applied;
        /// <summary>새 크기가 적용됐을 때 (m).</summary>
        public event Action<float> Applied;

        /// <param name="builtInWidth">빌드에 든 MarkerLibrary 의 물리 폭 (m). 입력값이 이와 같으면 아무것도 하지 않는다.</param>
        public MarkerLibrarySizer(ARTrackedImageManager images, Texture2D texture, string name, float builtInWidth)
        {
            _images = images;
            _texture = texture;
            _name = name;
            _applied = builtInWidth;
            if (_texture != null && !_texture.isReadable)
            {
                _unsupported = true;
                Status = "마커 텍스처를 읽을 수 없어 크기 변경 불가 (기본 크기 사용)";
            }
        }

        public void Update(float widthMeters)
        {
            if (_images == null || _texture == null || _unsupported) return;

            if (_pending)
            {
                if (!_job.jobHandle.IsCompleted) return;
                _job.jobHandle.Complete();
                _pending = false;
                var status = _job.status;
                if (status == AddReferenceImageJobStatus.Success)
                {
                    _images.referenceLibrary = _pendingLib;
                    _applied = _pendingWidth;
                    Status = $"마커 크기 적용: {_applied * 100:F1}cm";
                    Applied?.Invoke(_applied);
                }
                else
                {
                    _applied = _pendingWidth; // 같은 값으로 계속 재시도하지 않는다
                    Status = $"마커 크기 적용 실패: {status} (이전 크기 유지)";
                }
                _pendingLib = null;
                return;
            }

            if (widthMeters <= 0f || Mathf.Abs(widthMeters - _applied) < 0.0005f) return;
            if (ARSession.state < ARSessionState.Ready) return;

            try
            {
                if (!(_images.CreateRuntimeLibrary() is MutableRuntimeReferenceImageLibrary lib))
                {
                    _unsupported = true;
                    Status = "이 기기는 마커 크기 변경을 지원하지 않습니다 (기본 크기 사용)";
                    return;
                }
                if (!lib.IsTextureFormatSupported(_texture.format))
                {
                    _unsupported = true;
                    Status = $"마커 텍스처 형식 {_texture.format} 미지원 (기본 크기 사용)";
                    return;
                }
                _job = lib.ScheduleAddImageWithValidationJob(_texture, _name, widthMeters);
                _pendingLib = lib;
                _pendingWidth = widthMeters;
                _pending = true;
                Status = "마커 크기 적용 중...";
            }
            catch (Exception e)
            {
                _unsupported = true;
                Status = "마커 크기 적용 실패: " + e.Message;
            }
        }
    }
}
#endif
