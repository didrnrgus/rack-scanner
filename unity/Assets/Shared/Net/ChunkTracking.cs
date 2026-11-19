using System.Collections.Generic;

namespace RackScanner.Shared.Net
{
    /// <summary>
    /// PC 쪽: 받은 청크 번호를 기록해 중복을 무시하고, END_SCAN 이후 빠진 번호를 알려준다.
    /// 스레드 안전.
    /// </summary>
    public sealed class ChunkReceiptTracker
    {
        readonly HashSet<uint> _received = new HashSet<uint>();
        readonly object _lock = new object();

        public int Count { get { lock (_lock) return _received.Count; } }

        /// <summary>처음 받은 청크면 true (중복이면 false — 그래도 ACK 는 다시 보낸다).</summary>
        public bool MarkReceived(uint index) { lock (_lock) return _received.Add(index); }

        public bool Contains(uint index) { lock (_lock) return _received.Contains(index); }

        public List<uint> Missing(uint totalChunks)
        {
            var list = new List<uint>();
            lock (_lock)
                for (uint i = 0; i < totalChunks; i++)
                    if (!_received.Contains(i)) list.Add(i);
            return list;
        }

        public bool IsComplete(uint totalChunks)
        {
            lock (_lock)
            {
                if (_received.Count < totalChunks) return false;
                for (uint i = 0; i < totalChunks; i++) if (!_received.Contains(i)) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// 아이패드 쪽: ACK 를 받기 전까지 청크 프레임을 보관한다. 재연결하면 가장 작은 미확인 번호부터 다시 보낸다.
    /// 스레드 안전.
    /// </summary>
    public sealed class PendingChunkStore
    {
        readonly SortedDictionary<uint, byte[]> _pending = new SortedDictionary<uint, byte[]>();
        readonly object _lock = new object();
        int _acked;

        public int PendingCount { get { lock (_lock) return _pending.Count; } }
        public int AckedCount { get { lock (_lock) return _acked; } }

        public void Add(uint index, byte[] frame) { lock (_lock) _pending[index] = frame; }

        /// <summary>ACK 처리. 처음 확인된 청크면 true.</summary>
        public bool Ack(uint index)
        {
            lock (_lock)
            {
                if (!_pending.Remove(index)) return false;
                _acked++;
                return true;
            }
        }

        /// <summary>after 보다 큰 번호 중 가장 작은 미확인 청크 (after = -1 이면 처음부터).</summary>
        public bool TryGetNextAfter(long after, out uint index, out byte[] frame)
        {
            lock (_lock)
            {
                foreach (var kv in _pending)
                {
                    if (kv.Key > after) { index = kv.Key; frame = kv.Value; return true; }
                }
            }
            index = 0; frame = null;
            return false;
        }
    }
}
