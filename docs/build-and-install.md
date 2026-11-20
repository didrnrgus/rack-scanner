# 빌드와 설치

유료 Apple 계정 없이 무료 Apple ID 로 설치한다. 맥북의 Xcode 가 아이패드를 인식하면 맥북에서 직접 빌드하고,
인식하지 못하면 GitHub Actions → Sideloadly 경로를 쓴다 (맥북 없이도 됨). 배경은 [DEV_PLAN.md](DEV_PLAN.md) 의 "빌드와 설치".

## 0. 사전 점검 (M0)

- [ ] 아이패드 iPadOS 버전 (설정 → 일반 → 정보)
- [ ] 맥북 macOS / Xcode 버전, 그 Xcode 가 아이패드의 iPadOS 를 인식하는지 (2017 맥북프로: Ventura 13, Xcode 15.2 근처가 상한으로 알려짐 — 확인 필요)
- [ ] 세 기기 모두 Unity 6.6 (6000.6.8f1) 설치 가능 여부 (인텔 맥북 포함)

## 1. Unity 프로젝트 열기

`ProjectSettings/`, `Packages/`, 씬, `.meta` 는 레포에 커밋되어 있다. 새 기기에서는 열기만 하면 된다.

1. Unity Hub → **Add → Add project from disk** → `unity/` 폴더 선택 → **Unity 6000.6.8f1** 로 연다 (다른 버전으로 열면 ProjectVersion.txt 가 바뀌니 주의).
2. 패키지는 `Packages/manifest.json` 에 고정되어 있어 자동으로 받는다 (모두 Unity 6.6 번들 버전).

| 패키지 | 버전 | 용도 |
| --- | --- | --- |
| AR Foundation / Apple ARKit XR Plugin | 6.6.2 | 아이패드 스캔. 설치되면 `RS_ARFOUNDATION` 이 켜져 Scanner 의 AR 코드가 컴파일된다 |
| Input System | 1.20.1 | AR 카메라의 TrackedPoseDriver |
| Universal RP | 17.6.0 | 렌더 파이프라인 (URP) |
| MCP Unity (`com.gamelovers.mcp-unity`) | git (packages-lock 에 고정) | 에디터 원격 조작 (에이전트용). 포트 **8091** (`ProjectSettings/McpUnitySettings.json`) — 8090 은 다른 프로젝트 에디터와 겹친다 |

### 처음 한 번 한 설정 (기록용, 다시 할 필요 없음)

메뉴 **RackScanner → Setup Project (Player, XR, Scenes)** (`Assets/Editor/Setup/ProjectSetup.cs`) 가 아래를 한 번에 한다.
여러 번 눌러도 되고, 이미 있는 씬·에셋은 덮어쓰지 않는다 (Scanner 씬은 마커 라이브러리 연결만 다시 확인). 설정이 꼬였을 때 다시 눌러 맞출 수 있다.
대화상자 없이 콘솔에만 결과를 남기므로 MCP 의 `execute_menu_item` 으로도 실행할 수 있다.

- Player: Product Name `RackScanner`, iOS Bundle Identifier `com.didrnrgus.rackscanner` (TODO: 확정. 정하면 바꾸지 않는다 — 7일마다 같은 번들 ID 로 재설치),
  카메라 사용 문구, 기본 방향 LandscapeLeft, Run In Background, **Active Input Handling = Both** (PC 앱 궤도 카메라가 구 Input Manager 를 씀. 바뀌면 에디터 재시작).
- URP: `Assets/Settings/URP.asset` + `URP_Renderer.asset` 을 만들어 Graphics 기본 파이프라인으로 지정.
  렌더러에 **AR Background Renderer Feature** 를 넣는다 (URP 에서는 이것이 없으면 아이패드 카메라 화면이 검게 나온다).
- XR Plug-in Management → iOS → **ARKit** 로더 지정 (`Assets/XR/`). PC(Standalone) 는 XR 없음.
- 마커: `Assets/Scanner/Markers/marker_base.png` (`tools/make_marker.py --id base --size-cm 20` 결과, 인쇄용 `marker_base_print.svg` 도 같이 있음) 로
  `MarkerLibrary.asset` (이름 `base`, Physical Size 0.20 m) 을 만든다.
- 씬 2개를 만든다 (아래).

Target minimum iOS 는 기본값 15.0. 아이패드 버전이 그보다 낮으면 Player Settings (iOS) 에서 내린다.

## 2. 씬

**Receiver (PC)**: `Assets/Receiver/Scenes/Receiver.unity` — ReceiverApp 하나 (없어도 실행 시 자동 생성).

**Scanner (아이패드)**: `Assets/Scanner/Scenes/Scanner.unity`
- AR Session (+ AR Input Manager)
- XR Origin (Mobile AR): Main Camera 에 AR Camera Manager / AR Camera Background / TrackedPoseDriver / **AR Occlusion Manager**,
  XR Origin 에 **AR Tracked Image Manager** (Reference Image Library = `MarkerLibrary`)
- ScannerApp (ScanUI 는 자동으로 붙음)

**마커 실측값 맞추기**: `marker_base_print.svg` 를 배율 100% 로 인쇄해 자로 잰다. 20 cm 가 아니면
`MarkerLibrary.asset` 의 Physical Size(m) 와 앱의 "마커 cm" 를 같은 실측값으로 바꾼다.

## 3-A. 맥북에서 직접 빌드

PC 에서 커밋 → 맥북에서 pull → Unity 에서 **RackScanner → Build → iOS** → `unity/Builds/iOS` 의 Xcode 프로젝트를 열어
Signing 에 무료 Apple ID 팀 선택 → 아이패드 연결 후 Run.

## 3-B. GitHub Actions + Sideloadly (맥북 없이)

1. 시크릿 등록 (레포 Settings → Secrets and variables → Actions → New repository secret). CI 는 **개인 Unity 계정의 Personal 라이선스**를 쓴다.
   Actions 의 iOS 빌드는 GitHub 서버에서 Unity 를 실행해 Xcode 프로젝트를 만드는데, Unity 는 라이선스 없이는 시작하지 않기 때문이다.
   1. 개인 Unity ID 로 Unity Hub 에 로그인 → Settings(Preferences) → Licenses → Add → **Get a free personal license**.
      회사 Pro 계정으로 로그인해 둔 PC 에서 하면 회사 라이선스와 섞이니, 맥북처럼 개인 계정만 쓰는 기기에서 한다.
   2. 생긴 라이선스 파일을 텍스트로 연다:
      Windows `C:\ProgramData\Unity\Unity_lic.ulf`, macOS `/Library/Application Support/Unity/Unity_lic.ulf`.
   3. 시크릿 3개: `UNITY_LICENSE` = 그 파일 내용 전체, `UNITY_EMAIL` / `UNITY_PASSWORD` = 개인 Unity ID 로그인 정보.
   - 값은 등록 후 아무도 다시 볼 수 없고 빌드할 때만 쓰인다. Personal 라이선스는 주기적으로 만료되니, 빌드가 라이선스 오류로 실패하면 2~3 을 다시 한다.
   - TODO: 최신 Hub 가 `.ulf` 대신 `UnityEntitlementLicense.xml` 만 만들면 GameCI 가 받는지 첫 실행에서 확인.
2. main 에 푸시하면 **Build iOS (unsigned ipa)** 가 자동으로 돈다 (`docs/`, `*.md`, `tests/`, `tools/`, `samples/` 만 바뀐 푸시는 건너뜀).
   버전은 마지막 `vX.Y.Z` 태그에서 patch 를 자동으로 +1 한다 (태그가 없으면 `v0.1.0`). 직접 올릴 필요 없다.
   minor/major 를 올리려면 Actions → Run workflow 에서 `bump` 를 골라 main 으로 수동 실행한다.
   위 1~2단계(`ProjectSettings/`, `Packages/`, Scanner 씬 커밋)가 끝나기 전에는 빌드를 경고만 남기고 건너뛴다. 시크릿이 없으면 바로 실패한다.
3. 끝나면 레포의 **Releases** 에서 `RackScanner-vX.Y.Z.ipa` 내려받기 (다른 브랜치에서 수동 실행하면 Release 없이 Artifacts 에만 올라감).
4. Windows PC: iTunes·iCloud (Apple 사이트 버전, MS Store 버전 말고) 와 Sideloadly 설치, 아이패드 USB-C 연결,
   아이패드 설정 → 개인정보 보호 및 보안 → **개발자 모드** 켜기.
5. Sideloadly 에 `.ipa` 를 넣고 설치 전용 무료 Apple ID 로 설치.
   v0.1.0 은 `ApplicationVerificationFailed ... UnityRuntime.framework : 0xe8008001` 로 설치가 안 된다
   (프레임워크에 Unity 의 옛 서명이 남아 있었음). 워크플로가 남은 서명을 지우도록 고친 v0.1.1 부터는 그대로 설치된다.
6. **7일마다 만료**: 그 전에 아이패드 파일 앱 → 나의 iPad → (앱 이름) → `sessions/` 를 PC 로 백업하고(또는 앱에서 "PC로 재전송"),
   같은 Apple ID·번들 ID 로 다시 설치한다.

| 제한 | 내용 |
| --- | --- |
| 무료 Apple ID | 앱 3개까지, 7일 만료 |
| GitHub 무료 분량 | 비공개 레포 월 2,000분, macOS 는 10배 소진 (약 200분). Unity 내보내기는 Linux, macOS 는 xcodebuild 만 |
| 워크플로 | 초안. GameCI 출력 경로·라이선스·서명 없는 ipa 를 M0 에서 검증 |

## 4. PC 앱 (Windows)

기본은 **빌드하지 않고 에디터에서 Receiver 씬을 Play** 해서 쓴다 (GitHub Actions 는 아이패드용 iOS 만 빌드한다).
따로 실행 파일이 필요할 때만 **RackScanner → Build → Windows** → `unity/Builds/Windows/RackReceiver.exe`.
- 처음 실행 때 Windows 방화벽에서 **개인 네트워크 허용** (에디터로 돌리면 Unity Editor 에 대해 허용).
- 화면 왼쪽 위에 표시되는 PC 주소를 아이패드 앱의 "PC 주소" 에 입력.
- 세션은 실행 파일 옆(에디터에서는 `unity/sessions/`)에 저장된다. git 에는 올라가지 않는다.

## 5. 아이패드 앱 사용 순서

1. PC 주소·마커 실측 크기 입력 → **PC 연결 (새 세션)**
2. 마커를 1m 이내 정면에서 비춘다 → "정렬됨"
3. **촬영 시작** → 천천히 이동 (추적이 끊기면 수집이 멈추고, 복구되면 마커를 다시 비춰 재정렬)
4. **촬영완료** → PC 가 받으면 자동 저장 → PC 에서 "현재 세션 분석"
5. 처음 실행 시 "깊이 축" 버튼으로 점군이 실제 물체와 겹치는 설정을 찾는다 (기본 LandscapeLeft). 확정되면 코드 기본값으로 고정.
