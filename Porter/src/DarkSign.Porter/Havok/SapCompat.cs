using System.Numerics;

namespace SoulsAssetPipeline;

// The two helpers the vendored SoulsAssetPipeline HKX files (third_party/SoulsAssetPipeline-HKX) use from the rest of
// that project.
internal static class SapMath
{
    public const float PiOver2 = MathF.PI / 2;

    public static Vector3 XYZ(this Vector4 v) => new(v.X, v.Y, v.Z);
}
