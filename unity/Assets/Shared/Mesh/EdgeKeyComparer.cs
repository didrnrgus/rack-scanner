using System.Collections.Generic;

namespace RackScanner.Shared.Mesh
{
    /// <summary>
    /// 변 키((long)a &lt;&lt; 32 | b)용 해시. long 기본 해시는 위·아래 32비트를 XOR 해서, 번호가 비슷한 두 정점(이웃)의 키가 거의 모두 충돌한다
    /// (MeshRefiner 에서 32만 삼각형 나누기가 몇 분 걸렸음). splitmix64 로 섞는다.
    /// </summary>
    public sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();

        public bool Equals(long x, long y) => x == y;

        public int GetHashCode(long k)
        {
            ulong z = (ulong)k + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (int)(z ^ (z >> 31));
        }
    }
}
