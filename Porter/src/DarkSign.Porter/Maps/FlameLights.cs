using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Lamps at DS3's flames: the candles, torches and braziers burn (<see cref="ObjectSfx"/>) but lit nothing around
/// them (2026-11-19 playtest: "most of the candles now look like they are lit, but I'm not seeing any light from
/// them").
/// <para>
/// DS3 baked a flame's light into the walls around it (its lightmaps, which Elden Ring has no shaders for), so its
/// light file holds no lamp for a candle: Firelink Shrine's 45 lamps are blue-white fills (動的_拠点内部, colour 150 195
/// 255), not one of them at a candle. Elden Ring lights a flame with a lamp in the map's light file, not from the
/// effect: its candle effect 800401 carries no light, and Stormveil's file has 54 "touch_室内R" (indoor torch) and 13
/// "touch_松明" (torch) lamps at its flames: colour 240 180 120, strength 0.5 to 1.5 (most 1), reach 4 to 6.5 m,
/// sharpness 0.5 to 1, no shadows.
/// </para>
/// <para>
/// So the port adds such a lamp wherever a placed DS3 object burns: an object part whose ModelSfxParam row names a
/// converted effect that glows without carrying a light of its own, in a colour that isn't cold
/// (<see cref="FxrPort.Look.IsFlame"/>; port-weapons writes each effect's look into Work\map-sfx.tsv). The lamp
/// stands at the effect's dummy point on the object. Flames within <see cref="MergeDistance"/> of each other share
/// one lamp, a little stronger and farther-reaching per flame (a table of candles is one light, like Elden Ring's
/// one lamp per chandelier), and a flame within <see cref="NearLamp"/> of a warm DS3 lamp gets none (DS3 lit that
/// one itself). At most <see cref="MaxLamps"/> a map: past that the flames are merged over a wider distance.
/// </para>
/// </summary>
public static class FlameLights
{
    /// <summary>Name prefix of the lamps this step adds, so a rerun can find and replace them.</summary>
    public const string NamePrefix = "DS3 flame ";

    /// <summary>Flames this close share a lamp (metres).</summary>
    public const float MergeDistance = 3f;

    /// <summary>A flame this close to a warm DS3 lamp gets no lamp of its own (metres).</summary>
    public const float NearLamp = 2f;

    public const int MaxLamps = 200;

    /// <summary>Stormveil's indoor torch lamps (touch_室内R).</summary>
    public static readonly System.Drawing.Color Colour = System.Drawing.Color.FromArgb(255, 240, 180, 120);

    /// <summary>One flame's lamp, and what each further flame sharing it adds, inside what Stormveil's torch lamps do.</summary>
    public const float Strength = 0.75f, StrengthPerFlame = 0.05f, MaxStrength = 1.5f,
        Reach = 5f, ReachPerFlame = 0.4f, MaxReach = 9f, Sharpness = 0.75f;

    public sealed record Flame(Vector3 Position, int Effect);

    public sealed record Cluster(Vector3 Position, int Flames);

    /// <summary>Groups flames: each joins the first group whose middle is within <paramref name="distance"/>, else starts one.</summary>
    public static List<Cluster> Group(IEnumerable<Vector3> flames, float distance)
    {
        var sums = new List<(Vector3 Sum, int Count)>();
        foreach (var p in flames)
        {
            int at = sums.FindIndex(c => Vector3.Distance(c.Sum / c.Count, p) <= distance);
            if (at < 0)
                sums.Add((p, 1));
            else
                sums[at] = (sums[at].Sum + p, sums[at].Count + 1);
        }
        return [.. sums.Select(c => new Cluster(c.Sum / c.Count, c.Count))];
    }

    /// <summary>True for a DS3 lamp that already lights a flame's surroundings warmly.</summary>
    public static bool IsWarm(BTL.Light light) =>
        light.DiffusePower >= 0.3f && light.DiffuseColor.R > light.DiffuseColor.B * 1.2f;

    public sealed record Result(int Lamps, int Flames, int NearDs3Lamp, float MergedWithin);

    /// <summary>
    /// Replaces the flame lamps in <paramref name="btl"/> (an Elden Ring light file) with ones for
    /// <paramref name="flames"/>. <paramref name="salt"/> (the map's name) goes into each lamp's ID bytes.
    /// </summary>
    public static Result Build(BTL btl, IReadOnlyList<Vector3> flames, string salt)
    {
        btl.Lights.RemoveAll(l => l.Name.StartsWith(NamePrefix, StringComparison.Ordinal));
        var warm = btl.Lights.Where(IsWarm).Select(l => l.Position).ToList();
        var free = flames.Where(f => !warm.Any(w => Vector3.Distance(w, f) <= NearLamp)).ToList();
        float distance = MergeDistance;
        var clusters = Group(free, distance);
        while (clusters.Count > MaxLamps)
            clusters = Group(free, distance *= 1.5f);
        int n = 0;
        foreach (var c in clusters)
        {
            var light = NewLamp($"{NamePrefix}{n++:D3}", salt);
            light.Position = c.Position;
            light.DiffusePower = light.SpecularPower = Math.Min(MaxStrength, Strength + StrengthPerFlame * (c.Flames - 1));
            light.Radius = Math.Min(MaxReach, Reach + ReachPerFlame * (c.Flames - 1));
            btl.Lights.Add(light);
        }
        return new Result(clusters.Count, flames.Count, flames.Count - free.Count, distance);
    }

    private static readonly PropertyInfo IdBytes = typeof(BTL.Light).GetProperty(nameof(BTL.Light.Unk00))!;

    /// <summary>A point lamp with the settings of Stormveil's torch lamps (every field of touch_室内R_009 but its
    /// place, strength and reach).</summary>
    public static BTL.Light NewLamp(string name, string salt)
    {
        var light = new BTL.Light
        {
            Name = name,
            Type = BTL.LightType.Point,
            Unk1C = true,
            DiffuseColor = Colour,
            SpecularColor = Colour,
            CastShadows = false,
            ShadowColor = System.Drawing.Color.FromArgb(0, 0, 0, 0),
            ConeAngle = 0,
            Rotation = Vector3.Zero,
            Unk30 = 0,
            Unk34 = 0,
            Unk50 = 4,
            Unk54 = 0,
            Unk5C = -1,
            Unk64 = [0, 0, 0, 1],
            Unk68 = 0,
            Unk70 = 0,
            FlickerIntervalMin = 0,
            FlickerIntervalMax = 0,
            FlickerBrightnessMult = 1,
            Unk80 = -1,
            Unk84 = [0, 0, 0, 0],
            Unk88 = 0,
            Unk90 = 0,
            Unk98 = 1,
            NearClip = 1,
            UnkA0 = [0, 0, 2, 0],
            Sharpness = Sharpness,
            UnkAC = 1,
            Width = 0,
            UnkBC = 0,
            UnkC0 = [1, 1, 0, 1],
            UnkC4 = 20,
            UnkC8 = 1,
            UnkCC = 1,
            UnkD0 = 0,
            UnkD4 = 0,
            UnkD8 = 0.2f,
            UnkDC = 0,
            UnkE0 = 0,
            UnkE4 = 1,
        };
        // Every lamp of both games carries 16 bytes of its own; the library only lets a read set them.
        IdBytes.SetValue(light, MD5.HashData(Encoding.UTF8.GetBytes($"{salt}/{name}")));
        return light;
    }

    /// <summary>
    /// Where the flames of a DS3 map's placed objects are: for each object part in <paramref name="partNames"/> (the
    /// parts the port placed) whose ModelSfxParam row names effects that are flames by <paramref name="looks"/> and
    /// converted, the effect's dummy point on the model (<paramref name="dummies"/>: model name -> dummy ID -> spot
    /// in the model's space; the part's own spot when the model has no such point), placed with the part.
    /// </summary>
    public static List<Flame> Flames(MSB3 ds3, IReadOnlyDictionary<string, string> partNames, PARAM ds3Param,
        IReadOnlyDictionary<int, int> converted, IReadOnlyDictionary<int, FxrPort.Look> looks,
        Func<string, IReadOnlyDictionary<int, Vector3>?> dummies, IDictionary<int, (int Parts, string Why)>? tally = null)
    {
        var flames = new List<Flame>();
        foreach (var part in ds3.Parts.Objects)
        {
            if (!partNames.ContainsKey(part.Name))
                continue;
            var rowId = ObjectSfx.Ds3Row(part);
            if (rowId < 0 || rowId > int.MaxValue || ds3Param[(int)rowId] is not { } row)
                continue;
            var placement = Placement(part);
            IReadOnlyDictionary<int, Vector3>? points = null;
            bool asked = false;
            foreach (var (effect, dummy) in ObjectSfx.Effects(row))
            {
                if (!converted.ContainsKey(effect))
                    continue;
                var look = looks.GetValueOrDefault(effect);
                if (tally is not null)
                    tally[effect] = (tally.TryGetValue(effect, out var t) ? t.Parts + 1 : 1, look?.Why ?? "look not known (port-weapons of an older build wrote the table)");
                if (look is not { IsFlame: true })
                    continue;
                if (!asked)
                {
                    points = dummies(part.ModelName);
                    asked = true;
                }
                var local = points is not null && points.TryGetValue(dummy, out var p) ? p : Vector3.Zero;
                flames.Add(new Flame(Vector3.Transform(local, placement), effect));
            }
        }
        return flames;
    }

    /// <summary>A model's dummy points by ID in the model's space (the first of each ID).</summary>
    public static Dictionary<int, Vector3> DummyPoints(FLVER2 flver)
    {
        var world = MapModelPort.WorldTransforms(flver);
        var points = new Dictionary<int, Vector3>();
        foreach (var d in flver.Dummies)
        {
            var at = d.ParentBoneIndex >= 0 && d.ParentBoneIndex < world.Length ? Vector3.Transform(d.Position, world[d.ParentBoneIndex]) : d.Position;
            points.TryAdd(d.ReferenceID, at);
        }
        return points;
    }

    /// <summary>
    /// The port-maps step, after the object effects: each slot's light file gets its flame lamps. A slot with no
    /// light file yet (a DS3 map without lamps) gets one in Elden Ring's format.
    /// </summary>
    public static string Apply(PorterContext ctx, GameArchives ds3, IEnumerable<MapSlots.Slot> slots,
        Func<MapId, IReadOnlyDictionary<string, string>?> partNames)
    {
        var converted = MapSfx.ReadTable(ctx);
        var looks = MapSfx.ReadLooks(ctx);
        if (converted.Count == 0)
            return "flame lights: none (port-weapons converted no map effects)";
        if (looks.Count == 0)
            return $"flame lights: none (Work\\{MapSfx.TableFile} has no effect looks: run port-weapons of this build)";
        var ds3Param = ctx.LoadParams(PorterGame.DarkSouls3).Get(ObjectSfx.Ds3ParamName)
                       ?? throw new InvalidDataException($"DS3 has no {ObjectSfx.Ds3ParamName}");
        var models = new Dictionary<string, IReadOnlyDictionary<int, Vector3>?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<int, Vector3>? Dummies(string model)
        {
            if (models.TryGetValue(model, out var known))
                return known;
            Dictionary<int, Vector3>? points = null;
            try
            {
                var path = ObjectPort.ObjBndPath(model);
                if (ds3.Contains(path))
                {
                    var bnd = BND4.Read(ds3.Read(path));
                    var file = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(model + ".flver", StringComparison.OrdinalIgnoreCase))
                               ?? bnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
                    if (file is not null)
                        points = DummyPoints(FLVER2.Read(file.Bytes));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The lamp stands at the part's own spot.
            }
            return models[model] = points;
        }

        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        foreach (var slot in slots)
        {
            var names = partNames(slot.Er);
            var msbPath = MapPorter.MsbPath(slot.Ds3);
            if (names is null || names.Count == 0 || !ds3.Contains(msbPath))
                continue;
            var tally = new SortedDictionary<int, (int Parts, string Why)>();
            var flames = Flames(MSB3.Read(ds3.Read(msbPath)), names, ds3Param, converted, looks, Dummies, tally);
            var target = ctx.ModPath(MapPorter.ModParts(MapLighting.ErLightPath(slot.Er)));
            BTL btl;
            DCX.CompressionInfo compression = MapTextures.EntryCompression;
            bool existed = File.Exists(target);
            if (existed)
                btl = BTL.Read(DCX.Decompress(File.ReadAllBytes(target), out compression));
            else
                btl = new BTL { Version = MapLighting.ErLightVersion, LongOffsets = true };
            var result = Build(btl, [.. flames.Select(f => f.Position)], slot.Er.ToString());
            if (existed || result.Lamps > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, DCX.Compress(btl.Write(), compression));
            }
            lines.Add(string.Create(inv, $"flame lights: {slot.Ds3} -> {slot.Er}: {result.Lamps} lamps for {result.Flames} flames") +
                      (result.NearDs3Lamp > 0 ? $" ({result.NearDs3Lamp} stand by a warm DS3 lamp and get none)" : "") +
                      (result.MergedWithin > MergeDistance ? string.Create(inv, $", merged within {result.MergedWithin:0.#} m to stay under {MaxLamps}") : "") +
                      (existed ? "" : result.Lamps > 0 ? ", in a new light file (the slot had none)" : "") +
                      (tally.Count == 0 ? "" : "; object effects: " + string.Join(", ", tally.Select(t => $"{t.Key} x{t.Value.Parts} {t.Value.Why}"))));
        }
        return lines.Count == 0 ? "flame lights: no ported objects carry DS3 effects" : string.Join(Environment.NewLine, lines);
    }

    /// <summary>A part's place in the map: scaled, turned (degrees, X then Z then Y, as DSMapStudio places DS3 parts) and moved.</summary>
    public static Matrix4x4 Placement(MSB3.Part part)
    {
        const float rad = MathF.PI / 180;
        var r = part.Rotation;
        var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
               * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
    }
}
