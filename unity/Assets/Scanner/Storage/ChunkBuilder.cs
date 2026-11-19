using System.Collections.Generic;
using RackScanner.Shared.Model;
using RackScanner.Shared.Protocol;

namespace RackScanner.Scanner.Storage
{
    /// <summary>
    /// 새 점(복셀 필터 통과분)을 청크로 묶는다. 점 수가 MaxPoints 에 닿거나 MaxInterval 이 지나면 청크를 낸다.
    /// (UnityEngine 비의존)
    /// </summary>
    public sealed class ChunkBuilder
    {
        public int MaxPoints = 8000;
        public double MaxInterval = 0.5;

        readonly List<PointXYZRGB> _buf = new List<PointXYZRGB>();
        double _firstTime = -1;
        uint _nextIndex;

        public uint ChunkCount => _nextIndex;
        public int BufferedPoints => _buf.Count;

        public void Add(in PointXYZRGB p, double time)
        {
            if (_buf.Count == 0) _firstTime = time;
            _buf.Add(p);
        }

        /// <summary>낼 청크가 있으면 true. force 면 버퍼에 남은 점을 모두 낸다.</summary>
        public bool TryBuild(double now, bool force, out PointChunkMessage chunk)
        {
            chunk = null;
            if (_buf.Count == 0) return false;
            if (!force && _buf.Count < MaxPoints && now - _firstTime < MaxInterval) return false;
            int n = System.Math.Min(_buf.Count, MaxPoints);
            chunk = new PointChunkMessage
            {
                ChunkIndex = _nextIndex++,
                KeyframeTime = _firstTime,
                Points = _buf.GetRange(0, n).ToArray(),
            };
            _buf.RemoveRange(0, n);
            if (_buf.Count > 0) _firstTime = now;
            return true;
        }
    }
}
