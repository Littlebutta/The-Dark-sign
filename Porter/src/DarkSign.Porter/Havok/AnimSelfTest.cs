using System.Numerics;
using HKLib.hk2018;
using SoulsAssetPipeline.Animation;

namespace DarkSign.Porter;

/// <summary>
/// anim-selftest: offline checks of how DS3 bone rotations are carried over to Elden Ring's skeleton
/// (<see cref="AnimPort.PlanRetarget"/>) and of the spline rotation rewrite (<see cref="SplineTranscode"/>). No game
/// files needed. Would have caught the backwards off-hand weapon: a bone whose rest pose differs between the two
/// skeletons must move the Elden Ring mesh and dummies exactly as DS3's bone moves DS3's (same skinning matrix).
/// </summary>
public static class AnimSelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        var rng = new Random(7);
        Quaternion RandomRotation() => Quaternion.Normalize(new Quaternion((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1));
        static bool Same(Matrix4x4 a, Matrix4x4 b, float tolerance = 2e-4f)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    if (MathF.Abs(a[r, c] - b[r, c]) > tolerance)
                        return false;
            return true;
        }
        static Matrix4x4 M(Quaternion q) => Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));

        // 1. The quaternion order the retarget relies on: Concatenate(a, b) is a, then b: M(a) * M(b) for row vectors.
        {
            var a = RandomRotation();
            var b = RandomRotation();
            Check(Same(M(Quaternion.Concatenate(a, b)), M(a) * M(b)), "quaternions: Concatenate(a, b) is 'a then b' (M(a) * M(b))");
        }

        // 2. Skinning equality on a 3-bone chain whose every rest rotation differs between the two skeletons, with the
        //    same bone names and hierarchy. The game moves a vertex by inv(bind world) * current world. After the
        //    retarget, that matrix must be the same for each Elden Ring bone as for the DS3 bone on every frame.
        {
            string[] names = ["Root", "Hand", "Weapon"];
            short[] parents = [-1, 0, 1];
            var ds3Rest = new[] { RandomRotation(), RandomRotation(), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) };
            var erRest = new[] { RandomRotation(), RandomRotation(), Quaternion.Identity };
            var ds3 = new AnimPort.Ds3Skeleton
            {
                Names = names.ToList(),
                Parents = parents.ToList(),
                ReferencePose = ds3Rest.Select(q => new NewBlendableTransform { Translation = Vector3.Zero, Rotation = q, Scale = Vector3.One }).ToList(),
            };
            var er = new AnimPort.ErSkeleton
            {
                Names = names.ToList(),
                Parents = parents.ToList(),
                ReferencePose = erRest.Select(q => new hkQsTransform { m_translation = Vector4.Zero, m_rotation = q, m_scale = Vector4.One }).ToList(),
            };
            AnimPort.PlanRetarget(ds3, er);
            Check(er.Pre is not null && er.Post is not null && er.Frozen is { Count: 0 }, "retarget: a chain with the same hierarchy gets a correction for every bone and freezes none");
            bool allSame = true;
            string worst = "";
            for (int frame = 0; frame < 20 && er.Pre is not null; frame++)
            {
                // A DS3 frame: every bone turned somewhat away from its rest.
                var ds3Local = ds3Rest.Select(q => Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Slerp(Quaternion.Identity, RandomRotation(), 0.3f), q))).ToArray();
                var erLocal = ds3Local.Select((q, b) => AnimPort.Apply(er.Pre![b], q, er.Post![b])).ToArray();
                for (int b = 0; b < 3; b++)
                {
                    var skinDs3 = Skin(ds3Rest, ds3Local, parents, b);
                    var skinEr = Skin(erRest, erLocal, parents, b);
                    if (!Same(skinDs3, skinEr))
                    {
                        allSame = false;
                        worst = $" (frame {frame} bone {names[b]})";
                    }
                }
            }
            Check(allSame, "retarget: every bone's skinning matrix (inverse bind × current world) equals DS3's on every frame" + worst);
            // The DS3 rest frame itself maps to Elden Ring's rest frame: the ported idle holds the weapon as Elden Ring does.
            var restLocal = ds3Rest.Select((q, b) => AnimPort.Apply(er.Pre![b], q, er.Post![b])).ToArray();
            Check(Enumerable.Range(0, 3).All(b => AnimPort.Degrees(restLocal[b], erRest[b]) < 0.01f), "retarget: DS3's rest pose becomes Elden Ring's rest pose");
        }

        // 2b. The real case: only the weapon bone rests differently (DS3's L_Weapon 180° about X under L_Hand, Elden
        //     Ring's at identity), the hand chain above it rests alike.
        {
            string[] names = ["Root", "Hand", "Weapon"];
            short[] parents = [-1, 0, 1];
            var hand = RandomRotation();
            var ds3Rest = new[] { Quaternion.Identity, hand, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) };
            var erRest = new[] { Quaternion.Identity, hand, Quaternion.Identity };
            var ds3 = new AnimPort.Ds3Skeleton
            {
                Names = names.ToList(),
                Parents = parents.ToList(),
                ReferencePose = ds3Rest.Select(q => new NewBlendableTransform { Translation = Vector3.Zero, Rotation = q, Scale = Vector3.One }).ToList(),
            };
            var er = new AnimPort.ErSkeleton
            {
                Names = names.ToList(),
                Parents = parents.ToList(),
                ReferencePose = erRest.Select(q => new hkQsTransform { m_translation = Vector4.Zero, m_rotation = q, m_scale = Vector4.One }).ToList(),
            };
            AnimPort.PlanRetarget(ds3, er);
            Check(er.Pre is not null && er.Post is not null && AnimPort.Degrees(er.Pre[2], Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)) < 0.01f && AnimPort.Degrees(er.Post[2]) < 0.01f
                  && er.Pre[1] == Quaternion.Identity && er.Post[1] == Quaternion.Identity,
                  "retarget: L_Weapon's case: Pre = 180° about X, Post = identity; the hand, resting alike, is copied");
            // Copying DS3's rotation as it was (DARKSIGN_WEAPON_BONES=ds3) turns the weapon bone's skin 180° (about the hand's X axis).
            var copied = Skin(erRest, ds3Rest, parents, 2);
            float copiedDegrees = AnimPort.Degrees(Quaternion.CreateFromRotationMatrix(copied));
            Check(MathF.Abs(copiedDegrees - 180) < 0.1f,
                  $"retarget: copying the DS3 rotation unchanged turns the weapon bone's mesh {copiedDegrees:0.#}° (the backwards off-hand weapon)");
            // A DS3 twirl of the weapon in the hand (a turn about the bone's own Y) comes through as the same twirl.
            var twirl = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
            var ds3Local = new[] { Quaternion.Identity, hand, Quaternion.Normalize(Quaternion.Concatenate(twirl, ds3Rest[2])) };
            var erLocal = ds3Local.Select((q, b) => AnimPort.Apply(er.Pre![b], q, er.Post![b])).ToArray();
            Check(Same(Skin(ds3Rest, ds3Local, parents, 2), Skin(erRest, erLocal, parents, 2)), "retarget: a DS3 twirl of the weapon in the hand moves the Elden Ring weapon the same way");
        }

        // 3. A bone hanging from a different parent in the two skeletons keeps Elden Ring's pose.
        {
            var ds3 = new AnimPort.Ds3Skeleton
            {
                Names = ["Root", "Hand", "Weapon", "Shield"],
                Parents = [-1, 0, 1, 1],
                ReferencePose = Enumerable.Repeat(new NewBlendableTransform { Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }, 4).ToList(),
            };
            var er = new AnimPort.ErSkeleton
            {
                Names = ["Root", "Hand", "Weapon", "Shield", "Extra"],
                Parents = [-1, 0, 1, 2, 1],
                ReferencePose = Enumerable.Repeat(new hkQsTransform { m_translation = Vector4.Zero, m_rotation = Quaternion.Identity, m_scale = Vector4.One }, 5).ToList(),
            };
            AnimPort.PlanRetarget(ds3, er);
            Check(er.Frozen is not null && er.Frozen.SetEquals([3]), "retarget: a shared bone under a different parent (L_Shield) keeps Elden Ring's pose; bones only Elden Ring has are untouched");
        }

        // 4. 48-bit rotations round-trip.
        {
            float worst = 0;
            for (int i = 0; i < 200; i++)
            {
                var q = RandomRotation();
                var back = SplineTranscode.ReadRotations([(byte)(1 | (2 << 2) | (1 << 6)), 0, 0x0F, 0], SplineTranscode.ThreeComp48(q));
                worst = MathF.Max(worst, AnimPort.Degrees(back[0], q));
            }
            Check(worst < 0.02f, $"48-bit rotation: encode then decode within {worst:0.####}° (200 random rotations)");
        }

        // 5. Rotation rewrite of a static and a spline track, with every quantization the decoder reads.
        {
            var pre = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            var post = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
            // Static 48-bit.
            var q = RandomRotation();
            var (mask, body) = SplineTranscode.RewriteRotation([(byte)(1 | (2 << 2) | (1 << 6)), 0, 0x0F, 0], SplineTranscode.ThreeComp48(q), pre, post);
            var got = SplineTranscode.ReadRotations(mask, body);
            Check(got.Length == 1 && AnimPort.Degrees(got[0], AnimPort.Apply(pre, q, post)) < 0.02f && ((mask[0] >> 2) & 0xF) == 2 && mask[2] == 0x0F,
                  "spline rewrite: a static 48-bit rotation becomes Pre · q · Post");
            // Absent rotation (identity every frame).
            (mask, body) = SplineTranscode.RewriteRotation([(byte)(1 | (0 << 2) | (1 << 6)), 0, 0, 0], [], pre, post);
            got = SplineTranscode.ReadRotations(mask, body);
            Check(got.Length == 1 && AnimPort.Degrees(got[0], AnimPort.Apply(pre, Quaternion.Identity, post)) < 0.02f && mask[2] == 0x0F && ((mask[0] >> 2) & 0xF) == 2,
                  "spline rewrite: a track without rotation data gets the fixed rotation Pre · Post as a static 48-bit value");
            // Spline, 48-bit: header (items, degree, knots), align 2, control points.
            var points = Enumerable.Range(0, 5).Select(_ => RandomRotation()).ToArray();
            var src = new List<byte>();
            src.AddRange(BitConverter.GetBytes((short)(points.Length - 1)));
            src.Add(3);
            src.AddRange(Enumerable.Range(0, points.Length - 1 + 3 + 2).Select(i => (byte)i));
            while (src.Count % 2 != 0) src.Add(0);
            foreach (var p in points) src.AddRange(SplineTranscode.ThreeComp48(p));
            var splineMask = new byte[] { (byte)(1 | (2 << 2) | (1 << 6)), 0, 0xF0, 0 };
            Check(SplineTranscode.ReadRotations(splineMask, src.ToArray()).Zip(points).All(z => AnimPort.Degrees(z.First, z.Second) < 0.02f), "spline rewrite: the test spline reads back its control points");
            (mask, body) = SplineTranscode.RewriteRotation(splineMask, src.ToArray(), pre, post);
            got = SplineTranscode.ReadRotations(mask, body);
            Check(got.Length == points.Length && got.Zip(points).All(z => AnimPort.Degrees(z.First, AnimPort.Apply(pre, z.Second, post)) < 0.02f) && body.Length == src.Count && mask[2] == 0xF0,
                  "spline rewrite: every control point of a spline becomes Pre · p · Post, knots and degree kept");
            // Turning the control points turns the sampled curve: a de Boor sum of turned points, normalized, is the turned sum.
            var sum = Quaternion.Normalize(new Quaternion(points[0].X * 0.25f + points[1].X * 0.5f + points[2].X * 0.25f, points[0].Y * 0.25f + points[1].Y * 0.5f + points[2].Y * 0.25f,
                points[0].Z * 0.25f + points[1].Z * 0.5f + points[2].Z * 0.25f, points[0].W * 0.25f + points[1].W * 0.5f + points[2].W * 0.25f));
            var turned = got.Take(3).ToArray();
            var turnedSum = Quaternion.Normalize(new Quaternion(turned[0].X * 0.25f + turned[1].X * 0.5f + turned[2].X * 0.25f, turned[0].Y * 0.25f + turned[1].Y * 0.5f + turned[2].Y * 0.25f,
                turned[0].Z * 0.25f + turned[1].Z * 0.5f + turned[2].Z * 0.25f, turned[0].W * 0.25f + turned[1].W * 0.5f + turned[2].W * 0.25f));
            Check(AnimPort.Degrees(turnedSum, AnimPort.Apply(pre, sum, post)) < 0.05f, "spline rewrite: a weighted sum of turned control points is the turned weighted sum (sampling commutes with the rewrite)");
        }

        Console.WriteLine(failures == 0 ? "anim-selftest: all checks passed" : $"anim-selftest: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>inv(bind world) × current world of bone <paramref name="b"/>, row-vector matrices.</summary>
    private static Matrix4x4 Skin(Quaternion[] rest, Quaternion[] local, short[] parents, int b)
    {
        Matrix4x4 World(Quaternion[] locals, int i) => parents[i] < 0 ? Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(locals[i]))
            : Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(locals[i])) * World(locals, parents[i]);
        Matrix4x4.Invert(World(rest, b), out var inverseBind);
        return inverseBind * World(local, b);
    }
}
