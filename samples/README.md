# samples

테스트용 작은 세션 (Git LFS: `*.ply`, `*.pcd`). 세션 폴더 구조는 DEV_PLAN 의 "저장 포맷과 데이터 관리" 와 같다.
실제 스캔 세션은 `sessions/` (gitignore) 에 두고, 분석 회귀 테스트에 쓸 만한 작은 것만 여기에 올린다.

합성 샘플은 직접 만들 수 있다:

```bash
dotnet run --project tools/AnalysisCli -- --synthetic samples/synthetic_rack.ply --bays 4 --levels 4 --fill 0.3
```
