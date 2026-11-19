using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace RackScanner.Shared.Protocol
{
    /// <summary>리틀 엔디언 바이너리 페이로드 판독기. 길이가 모자라면 <see cref="ProtocolException"/>.</summary>
    public ref struct PayloadReader
    {
        readonly ReadOnlySpan<byte> _src;
        int _pos;

        public PayloadReader(ReadOnlySpan<byte> src) { _src = src; _pos = 0; }

        public int Remaining => _src.Length - _pos;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _pos + n > _src.Length) throw new ProtocolException("payload truncated");
            var s = _src.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public byte ReadU8() => Take(1)[0];
        public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float ReadF32() => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Take(4)));
        public double ReadF64() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));

        public string ReadString()
        {
            int n = ReadU16();
            return Encoding.UTF8.GetString(Take(n));
        }

        public Vector3 ReadVector3() => new Vector3(ReadF32(), ReadF32(), ReadF32());
        public Quaternion ReadQuaternion() => new Quaternion(ReadF32(), ReadF32(), ReadF32(), ReadF32());

        public ReadOnlySpan<byte> ReadBytes(int n) => Take(n);

        /// <summary>페이로드를 다 읽었는지 확인. 남은 바이트가 있으면 형식 불일치로 본다.</summary>
        public void EnsureEnd()
        {
            if (Remaining != 0) throw new ProtocolException($"{Remaining} unexpected trailing bytes");
        }
    }
}
