using System.Numerics;

namespace RackScanner.Shared.Model
{
    /// <summary>마커 기준(Unity) 좌표의 점 하나. 단위는 미터, 색은 0~255.</summary>
    public struct PointXYZRGB
    {
        public float X, Y, Z;
        public byte R, G, B;

        public PointXYZRGB(float x, float y, float z, byte r = 255, byte g = 255, byte b = 255)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b;
        }

        public PointXYZRGB(Vector3 p, byte r = 255, byte g = 255, byte b = 255) : this(p.X, p.Y, p.Z, r, g, b) { }

        public Vector3 Position
        {
            get => new Vector3(X, Y, Z);
            set { X = value.X; Y = value.Y; Z = value.Z; }
        }

        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}) #{R:X2}{G:X2}{B:X2}";
    }
}
