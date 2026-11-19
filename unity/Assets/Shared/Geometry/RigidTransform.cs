using System.Numerics;

namespace RackScanner.Shared.Geometry
{
    /// <summary>
    /// 이동·회전·균일 스케일 변환. p' = Translation + Scale * (Rotation * p).
    /// 점군 원본은 고치지 않고, 최종 변환(현장 좌표·스케일)은 이 값 하나로 따로 보관한다.
    /// </summary>
    public struct RigidTransform
    {
        public Quaternion Rotation;
        public Vector3 Translation;
        public float Scale;

        public static RigidTransform Identity => new RigidTransform(Quaternion.Identity, Vector3.Zero, 1f);

        public RigidTransform(Quaternion rotation, Vector3 translation, float scale = 1f)
        {
            Rotation = Quaternion.Normalize(rotation);
            Translation = translation;
            Scale = scale;
        }

        public Vector3 Apply(Vector3 p) => Translation + Scale * Vector3.Transform(p, Rotation);

        public Quaternion ApplyRotation(Quaternion q) => Quaternion.Normalize(Rotation * q);

        /// <summary>a.Then(b) : 먼저 a, 다음 b 를 적용하는 변환.</summary>
        public RigidTransform Then(RigidTransform b) => Compose(b, this);

        /// <summary>outer ∘ inner : inner 를 먼저 적용.</summary>
        public static RigidTransform Compose(RigidTransform outer, RigidTransform inner)
        {
            return new RigidTransform(
                outer.Rotation * inner.Rotation,
                outer.Apply(inner.Translation),
                outer.Scale * inner.Scale);
        }

        public RigidTransform Inverse()
        {
            var inv = Quaternion.Inverse(Rotation);
            float s = 1f / Scale;
            return new RigidTransform(inv, -s * Vector3.Transform(Translation, inv), s);
        }

        public override string ToString() => $"T={Translation} R={Rotation} S={Scale}";
    }
}
