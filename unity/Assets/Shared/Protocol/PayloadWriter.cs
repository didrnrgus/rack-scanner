using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace RackScanner.Shared.Protocol
{
    /// <summary>리틀 엔디언 바이너리 페이로드 작성기 (자동 확장 버퍼).</summary>
    public sealed class PayloadWriter
    {
        byte[] _buf;
        int _len;

        public PayloadWriter(int capacity = 256) { _buf = new byte[Math.Max(16, capacity)]; }

        public int Length => _len;

        Span<byte> Take(int n)
        {
            if (_len + n > _buf.Length)
            {
                int cap = _buf.Length;
                while (cap < _len + n) cap *= 2;
                Array.Resize(ref _buf, cap);
            }
            var s = new Span<byte>(_buf, _len, n);
            _len += n;
            return s;
        }

        public void WriteU8(byte v) { Take(1)[0] = v; }
        public void WriteU16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v); }
        public void WriteU32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v); }
        public void WriteF32(float v) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(v)); }
        public void WriteF64(double v) { BinaryPrimitives.WriteInt64LittleEndian(Take(8), BitConverter.DoubleToInt64Bits(v)); }

        /// <summary>uint16 바이트 길이 + UTF-8.</summary>
        public void WriteString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
            WriteU16((ushort)bytes.Length);
            bytes.CopyTo(Take(bytes.Length));
        }

        public void WriteVector3(Vector3 v) { WriteF32(v.X); WriteF32(v.Y); WriteF32(v.Z); }
        public void WriteQuaternion(Quaternion q) { WriteF32(q.X); WriteF32(q.Y); WriteF32(q.Z); WriteF32(q.W); }

        public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(_buf, 0, _len);
    }
}
