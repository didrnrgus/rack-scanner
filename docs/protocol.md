# 전송 프로토콜 v1

아이패드 → PC TCP. 길이 접두 바이너리 메시지를 청크 단위로 보내고 청크마다 ACK 를 받는다.
구현은 `unity/Assets/Shared/Protocol/` 한 곳뿐이고 두 앱이 공유한다. **형식을 바꾸면 `ProtocolConstants.Version` 을 올리고 이 문서를 같이 고친다.**

- 기본 포트: `47800` (`ProtocolConstants.DefaultPort`)
- 모든 정수·실수는 리틀 엔디언. 문자열은 `uint16 바이트 길이 + UTF-8`.
- 벡터 = `float32 x, y, z`, 쿼터니언 = `float32 x, y, z, w`. 좌표는 마커 기준 Unity 좌표 (Y 위, Z 앞, 미터).

## 헤더 (12B)

| 오프셋 | 크기 | 필드 | 값 |
| --- | --- | --- | --- |
| 0 | 4 | magic | 바이트 `'R' 'K' 'S' 'C'` (uint32 LE 로 `0x43534B52`) |
| 4 | 2 | version | `1` |
| 6 | 2 | type | 아래 표 |
| 8 | 4 | length | 페이로드 바이트 수 (최대 16 MiB, 넘으면 연결 종료) |

## 메시지

| type | 이름 | 방향 | 페이로드 |
| --- | --- | --- | --- |
| 1 | HELLO | iPad → PC | `u16 protocolVersion`, `str sessionId`, `str deviceInfo` |
| 2 | MARKER_DEF | iPad → PC | `str markerId`, `f32 printedWidth(m)`, `f32 printedHeight(m)`, `vec3 unityPosition`, `quat unityRotation` |
| 3 | ALIGNED | iPad → PC | `str markerId`, `f32 distance(m)`, `f32 viewAngleDeg`, `f32 pitchDeg`, `f32 rollDeg`, `u16 frameCount` |
| 4 | POINT_CHUNK | iPad → PC | `u32 chunkIndex`, `f64 keyframeTime(s)`, `u32 pointCount`, 점 × pointCount (`f32 x, f32 y, f32 z, u8 r, u8 g, u8 b` = 15B) |
| 5 | ACK | PC → iPad | `u32 chunkIndex` |
| 6 | STATE | 양방향 | `u8 state` (1 Paused, 2 Resumed, 10 TrackingNormal, 11 TrackingLimited, 12 TrackingLost) |
| 7 | END_SCAN | iPad → PC | `u32 totalChunks` |

페이로드 뒤에 남는 바이트가 있으면 형식 불일치로 보고 연결을 끊는다.

## 연결 흐름

1. 아이패드가 연결하고 **HELLO** 를 먼저 보낸다. PC 는 헤더 version 과 HELLO 의 protocolVersion 을 검사해 다르면
   연결을 끊고 PC 화면에 이유를 표시한다.
2. 연결마다 MARKER_DEF → (정렬돼 있으면) ALIGNED → ACK 안 된 POINT_CHUNK 들(번호 순) → (촬영완료 후면) END_SCAN.
3. PC 는 POINT_CHUNK 를 받는 즉시 수신 스레드에서 ACK 를 보낸다. 이미 받은 번호면 누적하지 않고 ACK 만 다시 보낸다.
4. END_SCAN 을 받고 0..totalChunks-1 이 모두 모이면 PC 가 세션 폴더에 저장한다.

## 끊김과 재연결

- 아이패드는 ACK 받기 전까지 청크를 메모리에 보관하고(로컬 파일에도 저장), 재연결하면 가장 작은 미확인 번호부터 다시 보낸다.
- 보낸 청크가 있는데 5초 동안 ACK 가 없으면 연결이 죽은 것으로 보고 다시 연결한다 (와이파이가 끊기면 쓰기가 성공처럼 보이는 경우 대비).
- PC 는 한 번에 한 기기만 받는다. 새 연결이 오면 이전 소켓을 닫는다 (재연결 시 반쯤 죽은 소켓 정리).
- 같은 sessionId 로 다시 HELLO 하면 PC 는 같은 세션으로 이어 받는다.
- **아이패드의 로컬 원본(`chunks.bin`)이 항상 기준**이고, 전송은 실시간 확인용이다. `chunks.bin` 은 이 프로토콜 프레임을 그대로
  이어 쓴 파일이라, 저장된 세션을 그대로 다시 보낼 수 있다 (아이패드 앱 "저장된 세션 → PC로 재전송").

## TODO (프로토콜 변경이 필요해 사람 결정 대기)

- 거절 사유를 아이패드에도 표시하려면 PC → iPad 메시지가 필요하다 (지금은 연결만 끊음).
- DEV_PLAN 의 PC 보조 표시 "아이패드의 현재 위치와 방향" 을 보내는 메시지가 표에 없다 (예: DEVICE_POSE). 추가하면 v2.
- Bonjour(mDNS) 자동 발견 (지금은 PC IP 직접 입력).
- 대역폭이 부족하면 점 양자화·압축 (측정 후).
