# 아이패드 LiDAR → PC 랙 자동 인식 개발 문서

Nov 18, 2026 · @양국현

## 개요와 목표

아이패드 LiDAR로 찍은 점군을 PC로 보내, 랙의 열·칸·층을 자동으로 구하고 물류가 빠진 빈 랙으로 재현하는 도구를 만든다. 아이패드 앱, PC 앱, 공용 코드는 **하나의 레포**에서 함께 관리한다.

**목표**

- 마커 한 장을 원점으로 삼아, 아이패드 스캔 결과가 Unity 좌표계에 바로 겹친다.
- 점군이 와이파이로 PC에 실시간 누적되고, "촬영완료"를 누르면 PC가 랙을 분석한다.
- 분석 결과는 "N열 M번째 칸 K층" 정보와 빈 랙 에셋 배치로 나온다.
- 책상·모니터·상자로 파이프라인을 먼저 검증하고, 이후 철제 선반과 실제 랙으로 넓힌다.

**이번 범위 밖**

- 스케일의 최종 보정과 현장 도면 좌표 맞추기 (마지막에 변환 하나로 처리)
- 학습 기반 객체 인식 (규칙 기반이 막힐 때만 검토)
- 넓은 창고 전체 스캔 (서브 마커 보정은 확장 단계)
- 여러 기기 동시 스캔

## 확정된 결정

- 레포는 하나(rack-scanner)이고, Unity 프로젝트 하나에 빌드 대상이 둘(아이패드 iOS, PC Windows)이다.
- 아이패드 앱은 Unity AR Foundation(C#)으로 만든다.
- 마커 자리를 임시 원점으로 삼고, 위치와 yaw는 마커에서, 기울기는 중력에서 얻는다. 스케일과 현장 좌표는 마지막에 변환 하나로 처리한다.
- 전송은 TCP로 청크 단위에 ACK를 받고, 아이패드의 로컬 원본이 기준이다.
- 랙 분석은 학습 없는 규칙 기반이며, 규격을 입력하는 A단계부터 만든다.
- 비용을 쓰지 않는다: 유료 Apple 계정 없이 무료 Apple ID를 쓴다. 맥북의 Xcode로 직접 빌드가 되면 그렇게 하고, 안 되면 GitHub Actions로 빌드해 Windows PC의 Sideloadly로 설치한다.
- 테스트는 책상·모니터·상자, 철제 선반, 실제 랙 순서로 넓힌다.

## 시스템 구조

아이패드는 촬영과 전송, PC는 누적·표시·분석, 맥북은 아이패드 앱 빌드만 맡는다.

| 기기 | 역할 | 주요 작업 |
| --- | --- | --- |
| 아이패드 (11인치, M1 이전, LiDAR) | 스캔 앱 | 마커 정렬, LiDAR 깊이 → 월드 좌표 변환, 복셀 샘플링, 로컬 저장, PC로 전송 |
| 데스크톱 PC | 수신·분석 앱 (Unity) | 점군 누적·표시, 촬영완료 후 랙 분석, 빈 랙 배치, 결과 저장 |
| 맥북프로 (15인치, 2017) | 빌드 머신 | 같은 레포를 받아 iOS 빌드 (Unity → Xcode → 아이패드 설치) |

**데이터 흐름**

1. 아이패드가 마커 정보(ID, 인쇄 크기, Unity 상 위치·방향)를 PC로 미리 보내 두 기기가 같은 정의를 갖는다.
2. 아이패드가 마커를 인식해 AR 좌표계를 Unity 좌표계에 맞춘다.
3. 스캔 중 새로 생긴 점만 청크로 묶어 PC로 보내고, 같은 내용을 아이패드 로컬 파일에도 저장한다.
4. PC가 청크를 받아 누적하고 화면에 그린다.
5. "촬영완료"를 누르면 PC가 분석해 열·칸·층 정보와 빈 랙 배치를 만든다.

&#91;embedded content: 시스템 구조 · 기기 3대와 레포 1개\]

점 청크는 아이패드에서 PC로 한 방향으로 흐르고, 세 기기는 같은 레포를 기준으로 맞춰진다.

## 개발 환경과 사전 점검

시작 전에 가장 먼저 확인할 것은 맥북에서 쓸 수 있는 Xcode가 아이패드의 iPadOS를 지원하는지다. 지원하면 맥북에서 직접 빌드하고, 안 되면 GitHub Actions로 빌드해 Sideloadly로 설치한다(맥북 없이도 가능).

| 항목 | 요구사항 | 비고 |
| --- | --- | --- |
| 맥북 macOS / Xcode | 2017 맥북프로는 정식으로 macOS Ventura 13까지 지원되고, 쓸 수 있는 Xcode는 15.2 정도가 상한으로 알고 있음 | 확인 필요 |
| 아이패드 iPadOS | 맥북의 Xcode가 인식하는 범위 안에 있어야 디버그 설치 가능 | 설정 → 일반 → 정보에서 확인 |
| Unity | 세 기기 모두 Unity 6.6 (6000.6.8f1) 으로 고정 (ProjectVersion.txt 커밋) | 인텔 맥북에서 설치 가능한지 확인 필요 |
| Unity 패키지 | AR Foundation 6.6.2, Apple ARKit XR Plugin 6.6.2, Input System 1.20.1, URP 17.6.0 | 버전 고정 (Packages/manifest.json). Unity 6.6 에 번들된 버전 |
| 렌더 파이프라인 | URP (Assets/Settings/URP.asset). 렌더러에 AR Background Renderer Feature 포함 | PC 앱도 같은 에셋을 쓴다 |
| Apple 계정 | 무료 Apple ID 사용(유료 계정은 쓰지 않음). 설치한 앱이 7일마다 만료되어 Sideloadly로 재설치 | 앱 설치 전용 계정을 따로 만들어 쓰는 것을 권장 |
| 네트워크 | 아이패드와 PC가 같은 공유기, 5GHz 권장. iOS의 로컬 네트워크 권한 허용 필요 | Info.plist에 사용 이유 문구 추가 |

## 빌드와 설치 (GitHub Actions + Sideloadly)

유료 Apple 계정 없이 무료 Apple ID로 설치한다. 맥북의 Xcode가 아이패드를 인식하면 맥북에서 직접 빌드하고, 인식하지 못하면 아래 경로를 쓴다. 이 경로는 맥북이 없어도 된다.

1. main에 푸시하면 GitHub Actions가 자동으로 돈다(문서·테스트·도구만 바뀐 푸시는 건너뜀, 수동 실행도 가능). Linux 러너에서 Unity가 Xcode 프로젝트를 내보낸다.
2. macOS 러너가 서명 없이 xcodebuild로 아카이브해 .ipa로 묶는다. macOS는 무료 분량을 10배로 쓰므로 이 단계만 맥 러너에서 돌린다.
3. 실행이 끝나면 버전이 자동으로 올라간 태그(vX.Y.Z, 푸시마다 patch +1)와 GitHub Release가 만들어지고, Release에서 .ipa를 내려받는다.
4. Windows PC에서 Sideloadly로 .ipa를 아이패드에 설치한다(무료 Apple ID, USB-C 연결).
5. 7일이 지나면 같은 Apple ID와 번들 ID로 다시 설치한다. 그 전에 아이패드의 로컬 스캔 파일을 PC로 백업한다.

| 항목 | 내용 |
| --- | --- |
| 무료 Apple ID 제한 | 앱 3개까지, 7일마다 만료 |
| GitHub 무료 분량 | 비공개 레포는 월 2,000분이고 macOS는 10배로 소진(약 200분). 앱과 무관한 경로 제외와 Library 캐시로 아낀다. Release 저장은 무료 |
| Unity 라이선스 | Personal은 GameCI 방식의 활성화 파일이 필요하다. CI에서 막힐 수 있어 M0에서 시험한다 |
| 검증 필요 | 서명 없는 .ipa 생성, Sideloadly 설치, 아이패드의 iPadOS 호환 |

## 레포 구조 (하나의 레포)

레포 하나에 Unity 프로젝트 하나를 두고, 아이패드 앱과 PC 앱을 같은 프로젝트의 서로 다른 빌드 대상으로 관리한다. 통신 프로토콜과 좌표 변환 코드를 두 앱이 그대로 공유해서, 한쪽만 바뀔 어긋나는 일을 막는다.

```
rack-scanner/
├─ .github/workflows/        # CI: iOS 빌드 (main 푸시 시 자동, Release 업로드)
├─ unity/                    # Unity 프로젝트 (빌드 대상 2개)
│  ├─ Assets/
│  │  ├─ Shared/             # 프로토콜, 좌표 변환, 데이터 모델 (양쪽 공용)
│  │  ├─ Scanner/            # 아이패드 앱 (iOS 전용)
│  │  ├─ Receiver/           # PC 수신·표시 앱 (Windows)
│  │  ├─ Analysis/           # 랙 분석 (PC)
│  │  └─ RackAssets/         # 빈 랙 프리팹
│  ├─ Packages/
│  └─ ProjectSettings/
├─ tools/                    # 마커 인쇄물 생성, PCD 변환 스크립트
├─ samples/                  # 테스트용 작은 점군 (Git LFS)
├─ docs/                     # 이 문서(DEV_PLAN.md), 프로토콜 명세, 빌드·설치 안내
├─ CLAUDE.md                 # 에이전트 작업 규칙
└─ README.md
```

- 코드는 어셈블리 정의(asmdef)로 나눠 플랫폼을 제한한다: Shared는 양쪽, Scanner는 iOS 전용, Receiver·Analysis는 PC 전용.
- 맥북은 iOS만, PC는 Windows만 빌드하므로 한 기기에서 빌드 대상을 오가며 다시 임포트하는 일이 없다.
- `.gitignore`에 Library, Temp, Logs, Builds, UserSettings를 넣고, 점군(.ply, .pcd)과 모델 같은 큰 파일은 Git LFS로 관리한다.
- 프로토콜에 버전 번호를 넣어, 두 앱 버전이 다르면 연결을 거절한다.
- 작업 흐름: PC에서 개발·커밋 → 맥북에서 pull 후 iOS 빌드 → 아이패드 설치. 맥북에서 고친 것도 커밋해 PC로 가져온다.
- 대안: 프로젝트가 커지면 Unity 프로젝트를 둘로 나누고 Shared를 패키지로 공유한다. 지금은 하나가 단순하다.

## 좌표계와 마커 정렬 설계

마커가 놓인 자리를 임시 원점으로 삼고, 위치와 yaw는 마커에서, 기울기는 중력에서 얻는다. 스케일과 현장 좌표 맞추기는 마지막에 변환 하나로 처리한다.

| 요소 | 출처 | 처리 |
| --- | --- | --- |
| 위치 (x, y, z) | 베이스 마커 | 인식 후 1\~2초 동안 여러 프레임을 평균 |
| yaw | 베이스 마커 | 마커 평면 위의 한 축을 수평면에 투영해 구함 |
| pitch, roll | 중력 (ARKit) | 마커가 주는 값은 비교 로그로만 기록 |
| 스케일 | LiDAR (미터 단위) | 마지막에 알려진 거리로 검증 |

**정렬 규칙**

- 마커 정의(ID, 인쇄 실측 크기, Unity 상 위치·회전)는 아이패드가 PC로 보낸 값을 양쪽이 공유한다. 크기는 자로 재서 넣는다.
- 가깝고 정면에 가까운 프레임만 쓰고, N프레임을 평균한다 (회전은 쿠터니언 평균). 재보정이 필요할 때는 한 번에 덮어쓰지 않고 보간한다.
- 점군 원본은 마커 기준 좌표 그대로 저장하고, 최종 변환(이동·회전·스케일)은 별도 값으로 보관한다.
- 비교 로그: 마커를 인식할 때마다 마커의 pitch·roll(중력 대비), 거리, 시야각을 기록해, 중력만 써도 되는지 데이터로 판단한다.
- 추적 상태가 정상이 아닐 때는 점 누적을 멈추고, 복구되면 마커를 다시 비추도록 안내해 재정렬한다.
- 마커는 스캔 도중 움직이면 안 된다. 바닥에 위치 표시를 해 다음 세션에도 같은 자리에 놓는다.

**마커 종류와 확장**

- 처음에는 AR Foundation의 이미지 트래킹으로 빠르게 시작하고, 정확도가 부족하면 AprilTag로 바꾼다. 정렬 모듈의 인터페이스를 분리해 둘 중 어느 쪽이든 교체할 수 있게 한다.
- 넓은 공간은 서브 마커를 추가한다. 서브 마커마다 "실측 위치 있음(절대 보정)/없음(일관성 보정)"을 선택할 수 있게 둔다.

## 아이패드 앱 설계

아이패드 앱은 Unity AR Foundation(C#)으로 만들고, 점 추출·정렬·전송을 모듈로 나뉜다. 하나의 레포에서 언어와 도구를 통일하고 Shared 코드를 그대로 쓰기 위해서다.

**기술 선택**

- 기본: Unity AR Foundation. 맥북에서 Unity로 iOS 빌드를 만들고 Xcode로 아이패드에 설치한다.
- 대안: Swift + ARKit 네이티브. 성능과 API 접근이 유리하지만 언어와 툴체인이 둘로 나뉜다. 프레임이 부족할 때 점 추출 부분만 네이티브 플러그인으로 교체하는 것부터 검토한다.

**모듈**

| 모듈 | 책임 |
| --- | --- |
| TrackingGuard | AR 세션 추적 상태를 감시하고, 정상일 때만 수집을 허용 |
| MarkerAligner | 마커 인식, 프레임 평균, AR → Unity 변환 계산, 비교 로그 기록 |
| DepthSampler | 환경 깊이 이미지와 신뢰도 맵, 카메라 내부 파라미터, 카메라 포즈로 월드 점을 계산. 신뢰도 높은 점만, 일정 픽셀 간격으로 |
| VoxelFilter | 복셀 격자로 중복을 제거하고 새 복셀만 통과 |
| ChunkBuilder | 새 점을 청크로 묶음 |
| NetSender | TCP 전송, ACK 처리, 재연결과 재전송 |
| LocalStore | 청크를 파일에 이어 써서 원본 보관 |
| ScanUI | 상태 표시와 버튼 |

**화면**

- 상태 영역: 추적 상태, 마커 인식 여부, PC 연결 상태, 누적 점 수, 프레임 수
- 버튼: PC 주소 입력·연결, 촬영 시작, 일시정지, 촬영완료, 저장된 세션 목록
- 점군 표시는 기본으로 최소한만 그린다. 그리는 비용이 추적 성능을 떨어뜨리지 않게 하기 위해서다.

**성능과 설정 (초기값, 측정 후 조정)**

- 목표 프레임은 30fps로 고정한다. 떨어지면 수집 간격을 늘린다.
- 복셀 크기 3\~5cm, 수집은 매 프레임이 아니라 몇 프레임에 한 번, 점 계산은 Jobs/Burst로 처리한다.
- iOS 설정: 카메라 사용 이유와 로컬 네트워크 사용 이유를 Info.plist에 추가한다.

## 전송 프로토콜

TCP 위에 길이 접두 바이너리 메시지를 엮어 청크 단위로 보내고, 청크마다 ACK를 받아 끊김에 대비한다. 이 명세는 Shared 코드에 한 번만 구현해 양쪽이 공유한다.

| 메시지 | 방향 | 내용 |
| --- | --- | --- |
| HELLO | 아이패드 → PC | 프로토콜 버전, 세션 ID, 기기 정보 |
| MARKER\_DEF | 아이패드 → PC | 마커 ID, 인쇄 크기, Unity 위치·회전 (스캔 전에 미리) |
| ALIGNED | 아이패드 → PC | 정렬 완료, 사용한 마커 ID, 인식 시의 거리·각도 로그 |
| POINT\_CHUNK | 아이패드 → PC | 청크 번호, 키프레임 시각, 점 수, 점 배열(x, y, z, 색) |
| ACK | PC → 아이패드 | 받은 청크 번호 |
| STATE | 양방향 | 일시정지, 재개, 추적 상태 변화 |
| END\_SCAN | 아이패드 → PC | 촬영완료, 총 청크 수 |

**형식**

- 헤더: magic(4B) + 버전(2B) + 타입(2B) + 길이(4B).
- 점 인코딩은 처음에 float32 x, y, z + RGB 3B로 단순하게 시작하고, 대역폭이 부족하면 양자화·압축을 추가한다. 청크 크기는 수천\~수만 점으로 시작하고 측정 후 조정한다.

**연결 관리**

- 재연결: 아이패드가 마지막 ACK 이후 청크부터 다시 보내고, PC는 청크 번호로 중복을 무시한다.
- 발견: 처음에는 PC IP를 직접 입력하고, 이후 Bonjour(mDNS) 자동 발견을 추가한다.
- 버전 불일치: HELLO의 프로토콜 버전이 다르면 PC가 연결을 거절하고 이유를 화면에 표시한다.
- 아이패드의 로컬 원본이 항상 기준이고, 전송은 실시간 확인용이다.

## PC(Unity) 수신·누적·표시

수신은 별도 스레드에서, 누적과 렌더링은 메인 스레드에서 하고, 점은 복셀 단위로 중복을 제거하며 쌓는다.

- 수신: TcpListener 스레드가 메시지를 큐에 넣고, 메인 스레드가 처리한다. ACK는 받는 즉시 돌려보낸다.
- 누적: 복셀 좌표를 키로 하는 해시 맵으로 중복 점을 병합한다(복셀 내 평균 위치).
- 표시: 처음에는 청크 단위 Mesh(Points 토폴로지)로 그린다. 점이 수백만 개를 넘으면 GraphicsBuffer 기반 렌더링이나 VFX Graph로 전환한다.
- 보조 표시: 마커 원점 축, 바닥 격자, 아이패드의 현재 위치와 방향.
- 저장: END\_SCAN을 받으면 세션 폴더에 .ply(마커 좌표 원본)와 세션 메타 JSON을 저장한다.
- 불러오기: 저장한 세션을 다시 열어 분석만 반복 실행할 수 있게 한다. 분석을 개발할 때 매번 아이패드로 다시 촬영하지 않기 위해서다.

## 랙 분석 파이프라인

물류를 지우는 대신 랙의 뼈대(기둥과 빔)만 찾아 치수를 재고, 그 치수로 빈 랙을 새로 생성한다. 물류는 뼈대가 아니므로 결과에서 자연히 빠진다. 학습 없이 규칙 기반으로 만든다.

**처리 단계**

1. 전처리: 복셀 3cm로 점을 줄이고 통계적 이상치를 제거한다.
2. 바닥 제거: 높이(Y) 히스토그램에서 가장 아래에서 점이 많이 모인 봉우리를 바닥으로 보고 평면 피팅으로 확인한 뒤, 그 위 약 5cm 이하를 제거한다.
3. 발자국 후보: 위에서 본 2D 격자(5cm)로 투영해 높이가 일정 이상인 연결 영역을 묶고, 방향 있는 사각형(위치·크기·yaw)을 구한다. yaw는 랙들이 평행하다는 가정으로 주축에 맞춘다.
4. 층 검출: 후보 안의 점을 높이 방향으로 히스토그램을 만들어 빔의 높이 피크를 찾는다. 피크 수가 층 수이고 간격이 층 높이다.
5. 칸 검출: 랙의 길이 방향으로 점을 투영해 세로 기둥의 위치 피크를 찾는다. 피크 사이가 칸 폭이고 수가 칸 수다.
6. 규칙성 보정: 물류가 빔과 기둥을 가리는 경우를 위해 "칸 폭은 균일하다, 층 간격은 일정하다"는 규칙으로 빈틈을 메운다. 관측된 부분과 추정한 부분을 구분해 신뢰도를 기록한다.
7. 생성: 구한 치수로 빈 랙 프리팹(기둥, 빔, 선반)을 배치한다. 신뢰도가 낮은 랙은 다른 색으로 표시해 사람이 확인하게 한다.

**단계적 접근**

- A단계(규격 입력): 칸 폭·층 간격·깊이 같은 규격을 미리 입력하고, 점군에 가장 잘 맞는 위치와 칸·층 수만 찾는다. 난이도가 낮아 먼저 구현한다.
- B단계(자동): 규격 없이 히스토그램 피크에서 치수를 구한다. 가려진 부분이 많으면 실패할 수 있어 A단계 결과를 비교 기준으로 쓴다.
- 빔이 주황·파랑 등 뚜렷한 색이면 카메라 색을 점에 입혀 색으로 분리하는 기능을 추가한다(LiDAR가 얇은 빔을 잘 못 잡는 경우 대비).

**이름 규칙**

- 열(row): 마커 기준 축 방향으로 가까운 줄부터 1, 2, 3.
- 칸(bay): 한 줄의 시작 쪽(원점에 가까운 쪽)부터 1, 2, 3.
- 층(level): 바닥에서 위로 1, 2, 3.
- 정확한 규칙은 현장 관행에 맞게 확정해야 한다 (열·칸을 어느 방향에서 세는지).

**출력 예시 (값은 예시)**

```json
{
  "rackId": "R01",
  "row": 1,
  "bayCount": 4,
  "levelCount": 3,
  "bayWidth": 2.70,
  "levelHeights": [0.20, 1.60, 3.00],
  "depth": 1.10,
  "pose": { "x": 3.2, "z": 1.0, "yaw": 90 },
  "confidence": 0.82
}
```

## 학습 데이터와 모델 (후속 단계)

이 섹션은 규칙 기반 분석(A단계)이 M4~M5에서 막힐 때 시작하는 후속 계획이다. 지금 범위에는 포함하지 않으며, 막히는 지점이 숫자로 확인된 뒤에만 착수한다. Apple의 내장 모델(ARKit 메시 분류, RoomPlan)은 가구 범주만 알고 철제 랙·빔은 모르며 추가 학습도 할 수 없으므로, 필요하면 직접 학습한 모델을 따로 만든다.

**시작 조건**

- 규칙 기반 결과를 사람이 고친 데이터가 쌓여서, 어떤 경우에 실패하는지(예: 물류가 빔을 많이 가림) 숫자로 정리되어 있다.
- 규칙 기반 개선만으로는 해결되지 않는다고 판단된다.

**학습 데이터: 점군 + 에디터 정답 한 쌍**

- 입력은 아이패드 LiDAR로 찍은 점군(`points.ply`, 마커 기준 좌표)이다.
- 정답은 맵에디터(랙 에디터)에서 그 점군 위에 랙을 그려 export한 `labels.json`이다.
- 점군을 에디터에 올려 그렸으므로 JSON은 점군과 같은 좌표계이며, 별도 변환이 필요 없다.
- 정답 JSON 항목: 랙 ID, 위치, yaw, 열·칸·층 수, 칸 폭, 층 높이, 깊이. (실제 스키마는 에디터 export 형식 확인 후 확정 — TODO)
- 점 단위 라벨은 JSON에서 자동으로 만든다. 그려진 기둥·빔 위치에서 허용 거리(초기값 3\~5cm) 안의 점은 기둥·빔, 그 외는 바닥·물류·기타로 표시한다.
- 라벨 작업 속도를 위해 규칙 기반 분석 결과를 에디터에 초안으로 불러온 뒤 사람이 고친다.

**데이터 늘리기**

- 증강: 회전, 이동, 점 일부 제거, 노이즈 추가, 한 스캔을 여러 조각으로 자르기.
- 합성 데이터: 에디터로 랙 배치를 자동 생성하고 Unity에서 가상 LiDAR로 점군을 뽑아 먼저 학습한 뒤, 실제 스캔 쌍으로 보정한다. 합성 시 얇은 빔 누락, 노이즈, 물류 가림을 흉내 낸다.
- 철제 선반과 실제 랙 등 규격이 다른 랙을 모두 데이터에 포함한다.

**학습 대상 (우선순위)**

1. 점 단위 분할: 각 점이 바닥·기둥·빔·물류 중 무엇인지 맞힌다. 결과를 기존 규칙 기반 분석(층·칸 검출)에 넣는다.
2. 파라미터 회귀: 칸 수·층 수·칸 폭 같은 값을 직접 예측한다. 분할이 잘 되는 것을 확인한 뒤 검토한다.

**학습·배포**

- 학습은 PC에서 한다. GPU가 없으면 외부 환경 사용 여부는 비용 방침(비용을 쓰지 않는다)과 충돌하므로 사람이 결정한다.
- 모델은 ONNX로 내보내 PC의 Unity(Sentis)에서 추론한다. 아이패드 추론(Core ML)은 올인원으로 갈 때만 검토한다.
- `Analysis`는 점군을 받아 결과를 돌려주는 순수 모듈로 유지한다. 규칙 기반 구현과 학습 모델 구현을 같은 인터페이스로 교체하거나 병행할 수 있게 한다.

**평가**

- 규칙 기반 결과와 같은 지표(층·칸 수 일치, 칸 폭·층 높이 오차, 위치 오차)로 비교한다. 학습 모델이 이기는 경우에만 채택한다.
- 학습에 쓰지 않은 스캔(다른 날, 다른 랙)으로 평가한다.

**위험**

| 위험 | 대응 |
| --- | --- |
| 정답(사람이 그린 랙)의 몇 cm 오차 | 그 오차보다 정밀한 성능은 목표로 하지 않는다 |
| 마커 정렬 오차로 정답이 어긋남 | M1의 정렬 정확도를 먼저 확보한다 |
| 합성 데이터와 실제 LiDAR의 차이 | 합성에 노이즈·빔 누락을 넣고 실제 스캔으로 보정한다 |
| 학습하지 않은 랙 규격에서 성능 저하 | 규격이 다른 랙을 데이터에 포함하고, 낮은 신뢰도는 사람이 확인한다 |

**레포 배치**

- `tools/` 아래에 합성 데이터 생성, 에디터 JSON → 점 단위 라벨 변환, 학습 스크립트를 둔다.
- 라벨이 붙은 작은 샘플은 `samples/`에 Git LFS로 올린다.

## 저장 포맷과 데이터 관리

스캔 한 번을 폴더 하나로 저장하고, 원본 점군과 최종 변환을 분리해 보관한다.

```
sessions/2026-11-18_001/
├─ session.json          # 마커 정의, 사용한 마커 ID, 최종 변환(이동·회전·스케일), 프로토콜 버전
├─ points.ply            # 마커 기준 좌표의 원본 점군
├─ chunks.bin            # 아이패드 원본 청크 (재전송·복구용)
├─ alignment_log.csv     # 마커 인식 로그 (거리, 각도, pitch·roll 비교)
└─ result.json           # 랙 분석 결과
```

- 최종 변환은 session.json의 값만 바꿔 다시 적용한다. 점군 자체는 고치지 않는다.
- sessions/ 폴더는 .gitignore에 넣고, 테스트용 작은 세션만 samples/에 Git LFS로 올린다.
- 스캔 때마다 마커 자리를 기록해 두면 세션 간 비교가 가능하다.

## 테스트 계획과 단계별 마일스톤

작은 것부터 순서대로 통과하고, 각 단계의 결과를 숫자로 재서 다음 단계로 넘어간다. 아래 통과 기준은 잠정 목표이며 실측 후 조정한다.

| 단계 | 내용 | 통과 기준 (잠정) |
| --- | --- | --- |
| M0 환경 점검 | 빈 AR Foundation 앱을 빌드해 아이패드에 설치(맥북이 안 되면 GitHub Actions 빌드 + Sideloadly 설치), LiDAR 깊이 이미지 수신 확인 | 설치 성공, 깊이 이미지와 프레임 수 측정 |
| M1 마커 정렬 + 로컬 저장 | 마커를 인식해 좌표계를 맞추고 점군을 파일로 저장 | 같은 책상을 두 번 찍어 점군이 수 cm 이내로 겹침, yaw 2° 이내 |
| M2 전송 | 촬영 완료 후 파일 전송 → 실시간 청크 전송 | 와이파이를 끊었다 이어도 누락 청크 0개 |
| M3 단순 물체 분석 | 책상·모니터·상자를 클러스터링해 박스로 배치 | 실측 치수와 비교해 오차를 숫자로 기록 |
| M4 철제 선반 분석 | 집의 철제 선반으로 기둥·빔 검출과 층·칸 계산 | 층 수와 칸 수가 정확히 맞음 |
| M5 실제 랙 한 줄 (규격 입력) | A단계로 열·칸·층 계산과 빈 랙 배치 | 열·칸·층이 맞고 배치 위치가 실물과 겹침 |
| M6 확장 | 자동 검출(B단계), 서브 마커, 최종 변환과 스케일 검증 | 규격 입력 없이도 M5와 같은 결과 |

M3에서는 스캔을 다시 하지 않고 저장한 세션으로 분석을 반복해서 개발 속도를 높인다.

## 위험 요소와 대응

가장 큰 위험은 빌드 환경(Xcode·iPadOS 호환)과 LiDAR가 얇은 빔을 잡지 못하는 것이다. 둘 다 M0과 M4에서 가장 먼저 확인한다.

| 위험 | 영향 | 대응 |
| --- | --- | --- |
| Xcode와 iPadOS 비호환 | 아이패드에 앱 설치 불가 | M0에서 가장 먼저 확인. 불가면 GitHub Actions 빌드 + Sideloadly 설치로 전환 |
| Unity 라이선스를 CI에서 활성화하지 못함 | CI 빌드 불가 | M0에서 시험. 막히면 맥북 직접 빌드나 라이선스 정책을 다시 확인 |
| GitHub macOS 무료 분량 소진 | CI 빌드 중단 | Unity 내보내기는 Linux에서, macOS는 xcodebuild만. 수동 실행과 Library 캐시 |
| 무료 Apple ID 7일 만료 | 앱이 실행되지 않음 | 7일마다 같은 번들 ID로 재설치, 로컬 스캔 파일은 PC로 백업 |
| LiDAR가 얇은 빔을 못 잡음 | 층·칸 검출 실패 | 규격 입력(A단계), 색 정보 활용, 가까이서 천천히 스캔 |
| 프레임 드랍·추적 끊김 | 드리프트, 점 겹침 | 샘플링 하향, 추적 정상일 때만 수집, 마커 재정렬 |
| 마커 인식 불안정 | 정렬 오차 | 크게 인쇄, 여러 프레임 평균, 필요하면 AprilTag로 교체 |
| 와이파이 끊김 | 데이터 유실 | ACK·재전송, 아이패드 로컬 원본 저장 |
| 물류가 빔·기둥을 가림 | 칸·층 추정 오류 | 규칙성 보정, 신뢰도 표시, 사람이 확인하는 단계 |
| 두 앱의 버전 불일치 | 통신 오류 | 한 레포에서 관리, 프로토콜 버전 검사 |
| Unity 버전·맥북 호환 | 빌드 실패 | Unity 버전 고정, M0에서 맥북 설치 확인 |

## 레포 자동 세팅 지침 (에이전트용)

이 문서를 레포의 `docs/DEV_PLAN.md`로 넣고 에이전트(Claude Code 등)에게 이 문서대로 레포를 세팅하라고 시키면, 아래 순서대로 뼈대가 만들어지게 한다. 에이전트는 위의 "확정된 결정"을 바꾸지 않고, 불확실한 항목은 TODO로 남긴다.

**작업 규칙**

- 파일로 만들 수 있는 것(폴더, asmdef, C# 뼈대, 설정, 워크플로, 문서)만 만든다. Unity 에디터가 필요한 작업(씬·프리팹 생성, 패키지 설치)은 하지 않고 아래 "사람이 할 일"로 남긴다.
- Scanner(iOS 전용)와 Receiver·Analysis(PC 전용) 코드를 섞지 않는다.
- 프로토콜을 바꾸면 버전 상수를 올리고 `docs/protocol.md`를 함께 고친다.
- 이 문서와 코드가 어긋나면 임의로 고치지 말고 사람에게 알린다.

**체크리스트**

- [ ] 최상위 폴더 만들기: .github/workflows, unity, tools, samples, docs
- [ ] 이 문서를 docs/DEV\_PLAN.md로 두고, README.md에 한 단락 요약과 링크 쓰기
- [ ] 루트 .gitattributes, 루트 .gitignore(sessions/ 포함), unity/.gitignore 쓰기 (아래)
- [ ] unity/Assets 아래 Shared, Scanner, Receiver, Analysis, RackAssets 폴더와 asmdef 쓰기 (아래)
- [ ] Shared에 프로토콜 정의(메시지 타입, 헤더 구조, 버전 상수)와 좌표 변환 뼈대 쓰기 (이 문서의 전송 프로토콜, 좌표계와 마커 정렬 설계 기준)
- [ ] .github/workflows/build-ios.yml 쓰기 (아래)
- [ ] docs/protocol.md, docs/build-and-install.md 쓰기 (이 문서의 해당 섹션 기준)
- [ ] 루트 CLAUDE.md 쓰기 (아래)
- [ ] 마지막에 "사람이 할 일" 목록을 출력하기

**.gitattributes**

```
*.ply filter=lfs diff=lfs merge=lfs -text
*.pcd filter=lfs diff=lfs merge=lfs -text
*.fbx filter=lfs diff=lfs merge=lfs -text
```

**unity/.gitignore**

```
[Ll]ibrary/
[Tt]emp/
[Oo]bj/
[Bb]uild/
[Bb]uilds/
[Ll]ogs/
[Uu]ser[Ss]ettings/
.vs/
.idea/
*.csproj
*.sln
```

**어셈블리(asmdef) 규칙**

| 폴더 | asmdef 이름 | 참조 | 플랫폼 |
| --- | --- | --- | --- |
| Shared | RackScanner.Shared | 없음 | 전체 |
| Scanner | RackScanner.Scanner | Shared, AR Foundation 계열 | Editor, iOS |
| Receiver | RackScanner.Receiver | Shared, Analysis | iOS 제외 |
| Analysis | RackScanner.Analysis | Shared | iOS 제외 |

```json
{
  "name": "RackScanner.Scanner",
  "references": ["RackScanner.Shared"],
  "includePlatforms": ["Editor", "iOS"]
}
```

Receiver와 Analysis는 includePlatforms 대신 `"excludePlatforms": ["iOS"]`를 쓴다. AR Foundation 어셈블리 참조는 패키지를 설치한 뒤 사람이 추가한다.

**빌드 워크플로 초안 (.github/workflows/build-ios.yml)**

최초 초안이다. 실제 파일은 main 푸시 시 자동 실행, 버전 자동 증가, Release 업로드로 바뀌었으니 파일을 기준으로 본다. 초안이며 실제로 돌려 검증해야 한다. 특히 GameCI의 출력 경로(아래 build/iOS)와 Xcode 프로젝트 이름은 실행 결과를 보고 맞춘다.

```yaml
name: Build iOS (unsigned ipa)
on:
  workflow_dispatch:   # 수동 실행만. 푸시마다 돌리지 않는다 (macOS 무료 분량 절약)

jobs:
  unity-export:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          lfs: true
      - uses: actions/cache@v4
        with:
          path: unity/Library
          key: Library-iOS-${{ hashFiles('unity/Packages/manifest.json', 'unity/ProjectSettings/ProjectVersion.txt') }}
      - uses: game-ci/unity-builder@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
          UNITY_EMAIL: ${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: ${{ secrets.UNITY_PASSWORD }}
        with:
          projectPath: unity
          targetPlatform: iOS
      - uses: actions/upload-artifact@v4
        with:
          name: xcode-project
          path: build/iOS

  xcode-build:
    needs: unity-export
    runs-on: macos-15
    steps:
      - uses: actions/download-artifact@v4
        with:
          name: xcode-project
          path: xcode-project
      - name: Archive without signing
        run: |
          cd xcode-project
          xcodebuild -project Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release -sdk iphoneos -archivePath ../build/app.xcarchive archive CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY=""
      - name: Package ipa
        run: |
          mkdir -p Payload
          cp -R build/app.xcarchive/Products/Applications/*.app Payload/
          zip -r app.ipa Payload
      - uses: actions/upload-artifact@v4
        with:
          name: unsigned-ipa
          path: app.ipa
```

**CLAUDE.md**

```
# 작업 규칙
- 기준 문서는 docs/DEV_PLAN.md 이다. 구조, 프로토콜, 좌표 규칙은 그 문서를 따른다.
- Shared의 프로토콜을 바꾸면 버전 상수를 올리고 docs/protocol.md를 같이 고친다.
- Scanner(iOS 전용)와 Receiver·Analysis(PC 전용)의 코드를 섞지 않는다.
- Unity 에디터가 필요한 작업은 하지 않고 사람에게 요청한다.
- 문서와 어긋나는 결정은 임의로 바꾸지 말고 TODO로 남긴다.
```

**사람이 할 일 (에이전트는 하지 않음)**

- [x] Unity Hub에서 Unity 6.6 (6000.6.8f1) 프로젝트를 unity/ 에 만들고, AR Foundation과 Apple ARKit XR Plugin을 설치하기 (URP 포함, 메뉴 RackScanner → Setup Project)
- [ ] iOS 빌드 설정에서 카메라 사용 이유와 로컬 네트워크 사용 이유를 Info.plist에 넣기
- [ ] Unity 라이선스 활성화 파일을 만들어 GitHub 시크릿 UNITY\_LICENSE, UNITY\_EMAIL, UNITY\_PASSWORD로 등록하기
- [ ] 설치 전용 무료 Apple ID 준비, Windows PC에 iTunes·iCloud(Apple 사이트 버전)와 Sideloadly 설치, 아이패드 개발자 모드 켜기
- [ ] 마커를 인쇄해 실측 크기를 기록하기
