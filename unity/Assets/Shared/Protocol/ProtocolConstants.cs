namespace RackScanner.Shared.Protocol
{
    /// <summary>
    /// 전송 프로토콜 상수. 메시지 형식을 바꾸면 반드시 <see cref="Version"/> 을 올리고 docs/protocol.md 를 함께 고친다.
    /// </summary>
    public static class ProtocolConstants
    {
        /// <summary>바이트 순서로 'R','K','S','C' (리틀 엔디언 uint32 로 읽은 값).</summary>
        public const uint Magic = 0x43534B52;

        /// <summary>프로토콜 버전. HELLO 의 버전이 다르면 PC 가 연결을 거절한다.
        /// v2: MESH_BLOCK / MESH_ACK 추가 (기존 메시지 형식은 v1 과 같다).</summary>
        public const ushort Version = 2;

        /// <summary>
        /// 저장된 파일(chunks.bin, mesh.bin)을 읽을 때 받아들이는 가장 낮은 버전.
        /// v2 는 메시지를 추가만 했으므로 v1 으로 저장된 세션도 그대로 읽어 다시 보낼 수 있다. 네트워크 연결은 정확히 같은 버전만 받는다.
        /// </summary>
        public const ushort MinStoredVersion = 1;

        /// <summary>헤더 = magic(4) + version(2) + type(2) + length(4).</summary>
        public const int HeaderSize = 12;

        /// <summary>한 메시지 페이로드의 최대 길이. 이보다 크면 잘못된 스트림으로 보고 연결을 끊는다.</summary>
        public const int MaxPayloadLength = 16 * 1024 * 1024;

        /// <summary>점 하나의 인코딩 크기: float32 x,y,z (12B) + RGB (3B).</summary>
        public const int BytesPerPoint = 15;

        /// <summary>메쉬 정점 하나의 인코딩 크기: float32 x,y,z.</summary>
        public const int BytesPerMeshVertex = 12;

        /// <summary>PC 수신 앱의 기본 TCP 포트.</summary>
        public const int DefaultPort = 47800;
    }
}
