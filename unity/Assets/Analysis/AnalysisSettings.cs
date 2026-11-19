using System.Collections.Generic;

namespace RackScanner.Analysis
{
    /// <summary>랙 분석 파라미터. 초기값이며 실측 후 조정한다 (docs/DEV_PLAN.md 랙 분석 파이프라인).</summary>
    public sealed class AnalysisSettings
    {
        // 1. 전처리
        public float VoxelSize = 0.03f;
        public int OutlierNeighbors = 8;          // 통계적 이상치: k 최근접
        public float OutlierStdRatio = 2.5f;      // 평균 + ratio*표준편차 보다 먼 점 제거
        public float OutlierSearchRadius = 0.15f; // 이웃 탐색 반경

        // 2. 바닥
        public float FloorHistogramBin = 0.02f;
        public float FloorPeakMinRatio = 0.3f;    // 최대 봉우리 대비 이 비율 이상인 가장 낮은 봉우리를 바닥으로
        public float FloorFitBand = 0.05f;
        public float FloorClearance = 0.05f;      // 바닥 위 이 높이 이하는 제거

        // 3. 발자국
        public float FootprintCell = 0.05f;
        public int FootprintDilateCells = 2;      // 랙의 빈 틈을 메우는 팽창 (칸 수)
        public float MinObjectHeight = 0.10f;
        public float MinObjectArea = 0.02f;       // m²
        public bool AssumeParallelRacks = true;   // 랙 후보는 모두 같은 주축(평행)으로 맞춤

        // 랙 후보 조건
        public bool DetectRacks = true;
        public float MinRackHeight = 0.6f;
        public float MinRackLength = 0.4f;
        /// <summary>
        /// 빈 랙은 위에서 보면 앞 프레임과 뒤 프레임이 따로 보인다(가로 연결재를 LiDAR 가 못 잡으면).
        /// 폭이 이보다 얇은 평행 발자국 둘을 한 랙으로 묶는다.
        /// </summary>
        public float FrameMaxWidth = 0.3f;
        public float MaxRackDepth = 1.5f;

        // 4. 층
        public float LevelHeightBin = 0.02f;
        public float LevelLengthBin = 0.10f;
        public float LevelMinCoverage = 0.4f;     // 그 높이에서 길이 방향으로 점이 덮는 비율
        public float LevelMinProminence = 0.2f;   // 주변(±LevelBaselineWindow) 대비 돌출
        public float LevelBaselineWindow = 0.3f;
        public float LevelMinSeparation = 0.25f;

        // 5. 칸
        public float BayLengthBin = 0.05f;
        public float BayHeightBin = 0.05f;
        public float UprightWidth = 0.08f;
        public float UprightMinCoverage = 0.5f;   // 높이 방향으로 점이 덮는 비율
        public float MinBayWidth = 0.4f;
        public int MaxBays = 30;

        /// <summary>A단계 규격. null 이면 B단계(자동).</summary>
        public RackSpec Spec;
    }

    /// <summary>
    /// A단계(규격 입력): 칸 폭·층 높이·깊이를 미리 넣고, 점군에 가장 잘 맞는 위치와 칸·층 수만 찾는다.
    /// 값이 0 이하이거나 비어 있으면 그 항목은 자동 검출한다.
    /// </summary>
    public sealed class RackSpec
    {
        /// <summary>칸 폭 (기둥 중심 간격, m).</summary>
        public float BayWidth;
        /// <summary>랙 깊이 (m).</summary>
        public float Depth;
        /// <summary>빔 높이들 (바닥 기준, m). 있으면 그대로 쓴다.</summary>
        public List<float> LevelHeights = new List<float>();
        /// <summary>LevelHeights 가 없을 때: 첫 빔 높이와 층 간격으로 생성.</summary>
        public float FirstLevelHeight;
        public float LevelPitch;
    }
}
