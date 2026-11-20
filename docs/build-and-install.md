# 빌드와 설치

유료 Apple 계정 없이 무료 Apple ID 로 설치한다. 맥북의 Xcode 가 아이패드를 인식하면 맥북에서 직접 빌드하고,
인식하지 못하면 GitHub Actions → Sideloadly 경로를 쓴다 (맥북 없이도 됨). 배경은 [DEV_PLAN.md](DEV_PLAN.md) 의 "빌드와 설치".

## 0. 사전 점검 (M0)

- [ ] 아이패드 iPadOS 버전 (설정 → 일반 → 정보)
- [ ] 맥북 macOS / Xcode 버전, 그 Xcode 가 아이패드의 iPadOS 를 인식하는지 (2017 맥북프로: Ventura 13, Xcode 15.2 근처가 상한으로 알려짐 — 확인 필요)
- [ ] 세 기기 모두 Unity 6.6 (6000.6.8f1) 설치 가능 여부 (인텔 맥북 포함)

## 1. Unity 프로젝트 열기 (최초 1회, 한 사람만)

레포에는 `unity/Assets` 의 코드만 있고 `ProjectSettings/`, `Packages/` 는 아직 없다.

1. Unity Hub → **Add → Add project from disk** → `unity/` 폴더 선택 → **Unity 6000.6.8f1** 로 연다 (다른 버전으로 열면 ProjectVersion.txt 가 바뀌니 주의).
   (`ProjectSettings/`, `Packages/manifest.json` 이 생성된다.)
2. Package Manager 에서 설치: **AR Foundation**, **Apple ARKit XR Plugin** (6.x). 설치되면 `RS_ARFOUNDATION` 이 켜져 Scanner 의 AR 코드가 컴파일된다.
3. `unity/Assets/Scanner/RackScanner.Scanner.asmdef` 의 References 에 추가:
   `Unity.XR.ARFoundation`, `Unity.XR.ARSubsystems`, `Unity.XR.CoreUtils` (안 하면 패키지 설치 직후 Scanner 컴파일 에러가 난다).
4. Project Settings → XR Plug-in Management → iOS 탭 → **ARKit** 체크.
5. Player Settings → Active Input Handling 을 **Both** 로 (PC 앱의 궤도 카메라가 구 Input Manager 를 씀).
6. Player Settings (iOS) → Bundle Identifier 를 정하고 고정 (7일마다 같은 번들 ID 로 재설치), Target minimum iOS 는 아이패드 버전 이하.
7. `ProjectSettings/`, `Packages/`, 생성된 `.meta` 파일을 **모두 커밋**한다. 특히 `ProjectSettings/ProjectVersion.txt` (버전 고정).

## 2. 씬 만들기

**Receiver (PC)**: 메뉴 **RackScanner → Create Receiver Scene** 한 번. (`Assets/Receiver/Scenes/Receiver.unity`, ReceiverApp 은 실행 시 자동 생성)

**Scanner (아이패드)**: `Assets/Scanner/Scenes/Scanner.unity` 를 새로 만들고
1. Hierarchy → XR → **AR Session**
2. Hierarchy → XR → **XR Origin (Mobile AR)** (기본 Main Camera 는 삭제)
3. XR Origin 의 AR Camera 에 **AR Occlusion Manager** 추가
4. XR Origin 에 **AR Tracked Image Manager** 추가, Reference Image Library 를 만들어 연결:
   Create → XR → Reference Image Library → `tools/make_marker.py` 로 만든 `marker_base.png` 추가,
   이름 `base`, **Specify Size 켜고 Physical Size 에 인쇄 실측값(m)**. 앱의 "마커 cm" 와 같은 값이어야 한다.
5. 빈 GameObject 에 **ScannerApp** 컴포넌트 추가 (ScanUI 는 자동으로 붙음)

## 3-A. 맥북에서 직접 빌드

PC 에서 커밋 → 맥북에서 pull → Unity 에서 **RackScanner → Build → iOS** → `unity/Builds/iOS` 의 Xcode 프로젝트를 열어
Signing 에 무료 Apple ID 팀 선택 → 아이패드 연결 후 Run.

## 3-B. GitHub Actions + Sideloadly (맥북 없이)

1. 시크릿 등록 (레포 Settings → Secrets → Actions): `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`
   (Personal 라이선스 활성화 파일 만드는 법은 GameCI 문서 "Activation" 참고).
2. main 에 푸시하면 **Build iOS (unsigned ipa)** 가 자동으로 돈다 (`docs/`, `*.md`, `tests/`, `tools/`, `samples/` 만 바뀐 푸시는 건너뜀).
   버전은 마지막 `vX.Y.Z` 태그에서 patch 를 자동으로 +1 한다 (태그가 없으면 `v0.1.0`). 직접 올릴 필요 없다.
   minor/major 를 올리려면 Actions → Run workflow 에서 `bump` 를 골라 main 으로 수동 실행한다.
   위 1~2단계(`ProjectSettings/`, `Packages/`, Scanner 씬 커밋)가 끝나기 전에는 빌드를 경고만 남기고 건너뛴다. 시크릿이 없으면 바로 실패한다.
3. 끝나면 레포의 **Releases** 에서 `RackScanner-vX.Y.Z.ipa` 내려받기 (다른 브랜치에서 수동 실행하면 Release 없이 Artifacts 에만 올라감).
4. Windows PC: iTunes·iCloud (Apple 사이트 버전, MS Store 버전 말고) 와 Sideloadly 설치, 아이패드 USB-C 연결,
   아이패드 설정 → 개인정보 보호 및 보안 → **개발자 모드** 켜기.
5. Sideloadly 에 `.ipa` 를 넣고 설치 전용 무료 Apple ID 로 설치.
6. **7일마다 만료**: 그 전에 아이패드 파일 앱 → 나의 iPad → (앱 이름) → `sessions/` 를 PC 로 백업하고(또는 앱에서 "PC로 재전송"),
   같은 Apple ID·번들 ID 로 다시 설치한다.

| 제한 | 내용 |
| --- | --- |
| 무료 Apple ID | 앱 3개까지, 7일 만료 |
| GitHub 무료 분량 | 비공개 레포 월 2,000분, macOS 는 10배 소진 (약 200분). Unity 내보내기는 Linux, macOS 는 xcodebuild 만 |
| 워크플로 | 초안. GameCI 출력 경로·라이선스·서명 없는 ipa 를 M0 에서 검증 |

## 4. PC 앱 (Windows)

Unity 에서 **RackScanner → Build → Windows** → `unity/Builds/Windows/RackReceiver.exe`. 또는 에디터에서 Receiver 씬을 Play.
- 처음 실행 때 Windows 방화벽에서 **개인 네트워크 허용**.
- 화면 왼쪽 위에 표시되는 PC 주소를 아이패드 앱의 "PC 주소" 에 입력.
- 세션은 실행 파일 옆(에디터에서는 `unity/sessions/`)에 저장된다. git 에는 올라가지 않는다.

## 5. 아이패드 앱 사용 순서

1. PC 주소·마커 실측 크기 입력 → **PC 연결 (새 세션)**
2. 마커를 1m 이내 정면에서 비춘다 → "정렬됨"
3. **촬영 시작** → 천천히 이동 (추적이 끊기면 수집이 멈추고, 복구되면 마커를 다시 비춰 재정렬)
4. **촬영완료** → PC 가 받으면 자동 저장 → PC 에서 "현재 세션 분석"
5. 처음 실행 시 "깊이 축" 버튼으로 점군이 실제 물체와 겹치는 설정을 찾는다 (기본 LandscapeLeft). 확정되면 코드 기본값으로 고정.
