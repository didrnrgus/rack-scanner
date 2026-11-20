using System.Collections.Generic;

namespace RackScanner.Shared.Net
{
    /// <summary>
    /// 아이패드 쪽: 메쉬 블록의 최신 판을 ACK 받을 때까지 보관한다. 블록마다 가장 최근 판 하나만 남는다
    /// (보내기 전에 새 판이 오면 옛 판은 보내지 않는다). 재연결하면 남은 블록을 모두 다시 보낸다.
    /// 스레드 안전.
    /// </summary>
    public sealed class PendingMeshStore
    {
        sealed class Entry
        {
            public uint Revision;
            public byte[] Frame;
            /// <summary>이 판을 보낸 연결 번호 (0 = 아직 안 보냄).</summary>
            public int SentOnConnection;
        }

        readonly SortedDictionary<uint, Entry> _pending = new SortedDictionary<uint, Entry>();
        readonly object _lock = new object();
        int _acked;

        public int PendingCount { get { lock (_lock) return _pending.Count; } }
        public int AckedCount { get { lock (_lock) return _acked; } }

        /// <summary>블록의 새 판을 넣는다. 이미 더 새로운 판이 있으면 무시한다.</summary>
        public void Put(uint blockId, uint revision, byte[] frame)
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(blockId, out var e) && e.Revision > revision) return;
                _pending[blockId] = new Entry { Revision = revision, Frame = frame };
            }
        }

        /// <summary>ACK 처리. 보관 중인 판보다 오래된 ACK 면 (그 뒤에 새 판이 들어왔으면) 남겨 둔다. 지웠으면 true.</summary>
        public bool Ack(uint blockId, uint revision)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(blockId, out var e) || e.Revision > revision) return false;
                _pending.Remove(blockId);
                _acked++;
                return true;
            }
        }

        /// <summary>이 연결에서 아직 보내지 않은 블록 하나를 꺼내 "보냄" 으로 표시한다 (connection 은 1부터).</summary>
        public bool TryTakeUnsent(int connection, out uint blockId, out byte[] frame)
        {
            lock (_lock)
            {
                foreach (var kv in _pending)
                {
                    if (kv.Value.SentOnConnection == connection) continue;
                    kv.Value.SentOnConnection = connection;
                    blockId = kv.Key; frame = kv.Value.Frame;
                    return true;
                }
            }
            blockId = 0; frame = null;
            return false;
        }

        public bool HasUnsent(int connection)
        {
            lock (_lock)
            {
                foreach (var kv in _pending) if (kv.Value.SentOnConnection != connection) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// PC 쪽: 블록마다 받은 가장 큰 판 번호를 기록해, 같은 판이나 옛 판(재전송·순서 뒤바뀜)은 반영하지 않게 한다.
    /// 스레드 안전.
    /// </summary>
    public sealed class MeshReceiptTracker
    {
        readonly Dictionary<uint, uint> _latest = new Dictionary<uint, uint>();
        readonly object _lock = new object();

        /// <summary>처음 보는 블록이거나 더 새로운 판이면 true (반영해야 함). 그래도 ACK 는 항상 보낸다.</summary>
        public bool MarkReceived(uint blockId, uint revision)
        {
            lock (_lock)
            {
                if (_latest.TryGetValue(blockId, out var r) && r >= revision) return false;
                _latest[blockId] = revision;
                return true;
            }
        }

        public int BlockCount { get { lock (_lock) return _latest.Count; } }
    }
}
