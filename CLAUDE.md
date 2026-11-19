# 작업 규칙
- 기준 문서는 docs/DEV_PLAN.md 이다. 구조, 프로토콜, 좌표 규칙은 그 문서를 따른다.
- Shared의 프로토콜을 바꾸면 버전 상수를 올리고 docs/protocol.md를 같이 고친다.
- Scanner(iOS 전용)와 Receiver·Analysis(PC 전용)의 코드를 섞지 않는다.
- Unity 에디터가 필요한 작업은 하지 않고 사람에게 요청한다.
- 문서와 어긋나는 결정은 임의로 바꾸지 말고 TODO로 남긴다.

# 코드 구조 메모
- Shared, Analysis 는 UnityEngine 에 의존하지 않는 순수 C# 이다 (asmdef `noEngineReferences: true`).
  덕분에 `tests/` 의 dotnet 테스트로 Unity 없이 검증한다: `dotnet test tests/RackScanner.Tests`
- Scanner/Net, Scanner/Storage, Receiver/Net, Receiver/Session 도 순수 C# 로 유지한다 (테스트가 직접 컴파일함).
  UnityEngine 을 쓰는 코드는 MonoBehaviour 래퍼(Scanner/AR, Receiver/View, *App.cs)에만 둔다.
- C# 언어 버전은 Unity 6 기준 C# 9 이하로 쓴다 (file-scoped namespace, record struct, global using 금지).
  테스트 프로젝트가 LangVersion 9 로 컴파일해서 이를 검사한다.
- 랙 분석을 바꿀 때는 `tools/AnalysisCli` 로 저장된 세션(.ply)에 반복 실행해 본다.
