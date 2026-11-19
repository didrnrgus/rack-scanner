using UnityEngine;
using N = System.Numerics;

namespace RackScanner.Scanner.AR
{
    /// <summary>System.Numerics(Shared) ↔ UnityEngine 변환. 좌표 규약은 같다(Y 위, Z 앞).</summary>
    public static class UnityConvert
    {
        public static Vector3 ToUnity(this N.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static Quaternion ToUnity(this N.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static N.Vector3 ToNumerics(this Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        public static N.Quaternion ToNumerics(this Quaternion q) => new N.Quaternion(q.x, q.y, q.z, q.w);
    }
}
