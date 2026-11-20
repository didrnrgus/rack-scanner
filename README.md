# rack-scanner

아이패드 LiDAR로 찍은 점군을 와이파이로 PC에 실시간 누적하고, "촬영완료" 후 PC가 랙의 열·칸·층을 규칙 기반으로 구해
물류가 빠진 빈 랙으로 재현하는 도구다. 아이패드 앱(iOS)과 PC 앱(Windows)은 **Unity 프로젝트 하나의 두 빌드 대상**이고,
통신 프로토콜·좌표 변환·데이터 모델은 `Shared` 코드를 그대로 공유한다. 기준 문서는 [docs/DEV_PLAN.md](docs/DEV_PLAN.md).

| 문서 | 내용 |
| --- | --- |
| [docs/DEV_PLAN.md](docs/DEV_PLAN.md) | 개발 문서 (확정된 결정, 구조, 마일스톤) |
| [docs/protocol.md](docs/protocol.md) | 전송 프로토콜 명세 (v2: LiDAR 메쉬 블록 추가) |
| [docs/build-and-install.md](docs/build-and-install.md) | Unity 세팅, 씬, iOS 빌드(맥북 / GitHub Actions) → Sideloadly 설치 |
| [docs/analysis.md](docs/analysis.md) | 랙 분석 파이프라인 구현 메모, 결과 형식, 현재 정확도 |

## 폴더

```
unity/Assets/Shared     프로토콜, 좌표 변환, 복셀, PLY/JSON (양쪽 공용, UnityEngine 비의존)
unity/Assets/Scanner    아이패드 앱 (iOS + Editor). AR 코드는 AR Foundation 설치 시에만 컴파일 (RS_ARFOUNDATION)
unity/Assets/Receiver   PC 수신·표시 앱 (iOS 제외)
unity/Assets/Analysis   랙 분석 (iOS 제외, UnityEngine 비의존)
unity/Assets/Editor     빌드 메뉴·CI 진입점, iOS Info.plist 후처리
tools/                  make_marker.py (마커 인쇄물), pcd_convert.py, AnalysisCli (저장 세션에 분석 반복 실행)
tests/                  Unity 없이 도는 dotnet 테스트 (Shared, Analysis, 송수신 코어)
```

## 바로 해 볼 수 있는 것 (Unity 없이)

```bash
dotnet test tests/RackScanner.Tests
```

```bash
dotnet run --project tools/AnalysisCli -- --synthetic /tmp/rack.ply --bays 4 --levels 4 --fill 0.3
```

```bash
dotnet run --project tools/AnalysisCli -- /tmp/rack.ply --bay-width 1.2 --level-pitch 0.6 --depth 0.6
# 저장된 세션의 LiDAR 메쉬를 적응형 단순화 (넓은 면은 크게, 복잡한 곳은 원래대로) → mesh.obj
dotnet run --project tools/AnalysisCli -- --mesh sessions/<세션> --error-mm 5
```
