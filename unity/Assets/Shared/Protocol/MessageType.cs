namespace RackScanner.Shared.Protocol
{
    /// <summary>메시지 타입 (헤더의 type 2B). docs/protocol.md 의 표와 1:1 대응.</summary>
    public enum MessageType : ushort
    {
        Hello = 1,      // iPad → PC
        MarkerDef = 2,  // iPad → PC
        Aligned = 3,    // iPad → PC
        PointChunk = 4, // iPad → PC
        Ack = 5,        // PC → iPad
        State = 6,      // 양방향
        EndScan = 7,    // iPad → PC
    }

    /// <summary>STATE 메시지의 상태 코드.</summary>
    public enum ScanState : byte
    {
        Paused = 1,
        Resumed = 2,
        TrackingNormal = 10,
        TrackingLimited = 11,
        TrackingLost = 12,
    }
}
