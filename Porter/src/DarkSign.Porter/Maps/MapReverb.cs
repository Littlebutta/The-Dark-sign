using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's room reverb, per place, as Elden Ring's reverb regions (launcher fallback map-reverb).
/// <para><b>How DS3 decides reverb</b> (measured on Devin's DS3 map files, 2026-11-20): every collision part carries a
/// SoundSpaceType byte (MSB3 Part.Collision, SoulsFormats: NoReverb 0, Small A/B 1-2, Middle A/B 3-4, Large A/B 5-6,
/// Extra large A/B 7-8) and the sound engine uses the one of the ground the player stands on. 60-90% of each map's
/// collisions are 0 (outdoors and open courtyards are dry): m40 36 of 45, m30 64 of 91; the Catacombs (m38) are the
/// exception, 30 of 123.</para>
/// <para><b>How Elden Ring decides it</b> (Devin's Elden Ring files): every vanilla collision's matching byte
/// (MSBE Collision UnkT01) is 255 in Stormveil, Leyndell, the Roundtable and the colosseums: Elden Ring no longer sets
/// reverb per piece of ground. It picks it automatically from distance checks around the listener
/// (SoundAutoReverbSelectParam + SoundAutoReverbEvaluationDistParam; the exe names the Wwise inputs ReverbType,
/// OutdoorIndoor, UsingAutoReverb...). In vanilla those rows never give 0 (row 391: any outdoor place gets type 1, near
/// walls type 2, indoors 3-6 by distance), and DS3's maps are walled in by cliffs and castles, so every ported place
/// reverbed: the "blanket echo" Devin heard on DS3's voices once they were built on Elden Ring's NPC voice bank (PR
/// #240), whose sounds take the game's reverb. Elden Ring overrides the automatic choice with sound regions (MSBE region
/// type 30): Stormveil's "サウンド領域_リバーブ設定_[6]" ("sound region, reverb setting [6]") has UnkT00 = 6, every other
/// sound region UnkT00 = 255; the exe lists the region's settings as SoundSpaceTypeOverride, SoundIndoorOutdoorOverride,
/// SoundBgmPlaceTypeOverride, SoundEnvPlaceTypeOverride, SoundCommonPlaceType, DisableAutoCliffWind.</para>
/// <para><b>The port</b>: DS3's rule drawn as boxes. On a grid (<see cref="Step"/> m) the space above each DS3 floor
/// (every upward-facing triangle of a collision the player can stand on, from <see cref="Footroom"/> below to
/// <see cref="Headroom"/> above, where the camera hears from) takes that collision's reverb type; the rest of each
/// collision's bounds takes it too, and the rest of the map is dry. Where two overlap the smaller collision wins (a room
/// inside the outdoors, a courtyard inside a castle's bounds), floors over bounds. The painted grid is cut into as few
/// boxes as a greedy sweep finds, each an Elden Ring sound region with that reverb override. No two regions overlap,
/// so the order the game checks them in can't matter.</para>
/// </summary>
public static class MapReverb
{
    /// <summary>DARKSIGN_MAP_REVERB=0: no reverb regions, Elden Ring's automatic reverb everywhere (as up to 1.0.55x).</summary>
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_MAP_REVERB") != "0";

    /// <summary>DS3's largest reverb type (ExtraLargeReverbB); Elden Ring's types use the same numbers (0 dry ... 8).</summary>
    public const byte MaxType = 8;

    /// <summary>How far above a floor its reverb reaches: the camera, where the game hears from, rides up to a few
    /// metres above the player.</summary>
    public const float Headroom = 4f;

    /// <summary>How far below a floor its reverb reaches.</summary>
    public const float Footroom = 1f;

    /// <summary>The dry box over the whole map reaches this far past the outermost collision.</summary>
    public const float MapMargin = 30f;

    /// <summary>The finest grid step (metres). A map whose plan has more than <see cref="MaxRegions"/> boxes is planned
    /// again 1 m coarser (Firelink/Cemetery: 2 m 2,523 boxes, 4 m 750 at the same 95% match with DS3; the Cathedral
    /// of the Deep needs 4 m: 3,031 boxes, 94%).</summary>
    public const float Step = 2f;

    public const long MaxCells = 48_000_000;

    /// <summary>At most this many regions per map: the grid gets coarser until the plan fits, then the largest are kept.</summary>
    public const int MaxRegions = 3500;

    /// <summary>A triangle counts as floor when its normal is within ~45° of straight up (or down: either winding).</summary>
    public const float FloorNormalY = 0.7f;

    // DS3 hit filters no player stands on: camera-only, enemy-only, fall-death camera, kill planes.
    private static readonly HashSet<byte> NotGround = [9, 11, 13, 15];

    public sealed record Box(Vector3 Min, Vector3 Max, byte Type, string Source)
    {
        public Vector3 Size => Max - Min;
        public float Volume => Math.Max(0, Size.X) * Math.Max(0, Size.Y) * Math.Max(0, Size.Z);
        public bool Contains(Vector3 p) =>
            p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    }

    /// <summary>A DS3 collision model in its own space: its bounds and its triangles.</summary>
    public sealed record Model(Vector3 Min, Vector3 Max, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles);

    /// <summary>One placed DS3 collision: its bounds box (with DS3's reverb type) and the boxes over its floors.</summary>
    public sealed record Place(Box Bounds, List<Box> Floors);

    public sealed record Result(int Collisions, int WithReverb, int Regions, IReadOnlyDictionary<byte, int> ByType, IReadOnlyList<string> Notes)
    {
        public string Summary =>
            $"reverb: {Regions} reverb regions from {Collisions} DS3 collisions ({WithReverb} with a room reverb), by type " +
            string.Join(" ", ByType.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
    }

    /// <summary>A DS3 collision model's mesh bodies, in its own space (null when it has no mesh triangles).</summary>
    public static Model? ReadModel(Ds3.hkRootLevelContainer root)
    {
        var bodies = (root.m_namedVariants ?? []).Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>()
            .SelectMany(s => s.m_systemDatas ?? []).SelectMany(s => s.m_bodyCinfos ?? []).ToList();
        var decoded = CollisionPort.Decode(root);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        for (int b = 0; b < decoded.Count; b++)
        {
            var body = bodies.FirstOrDefault(x => x.m_name == decoded[b].Name) ?? (b < bodies.Count ? bodies[b] : null);
            var m = body is null ? Matrix4x4.Identity
                : Matrix4x4.CreateFromQuaternion(body.m_orientation == default ? Quaternion.Identity : body.m_orientation)
                  * Matrix4x4.CreateTranslation(body.m_position.X, body.m_position.Y, body.m_position.Z);
            foreach (var (a0, b0, c0) in decoded[b].Triangles)
            {
                var (a, bb, c) = (Vector3.Transform(a0, m), Vector3.Transform(b0, m), Vector3.Transform(c0, m));
                lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(bb, c)));
                hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(bb, c)));
                triangles.Add((a, bb, c));
            }
        }
        return lo.X > hi.X ? null : new Model(lo, hi, triangles);
    }

    public static bool IsFloor(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        return len > 1e-6f && MathF.Abs(n.Y) / len >= FloorNormalY;
    }

    /// <summary>The axis-aligned bounds of a local box moved by a matrix.</summary>
    public static (Vector3 Min, Vector3 Max) Transform(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), m);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }

    /// <summary>A part's placement, as the map pieces and collisions are placed (scale, X, Z, Y rotation, position).</summary>
    public static Matrix4x4 PartMatrix(MSB3.Part part)
    {
        const float rad = MathF.PI / 180;
        var r = part.Rotation;
        var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
               * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
    }

    /// <summary>Every DS3 collision the player can stand on, placed, with DS3's reverb type; <paramref name="model"/>
    /// gives a collision model by its model number (null when unknown).</summary>
    public static List<Place> Places(MSB3 ds3, Func<string, Model?> model, List<string> notes)
    {
        var places = new List<Place>();
        int noMesh = 0, notGround = 0, strange = 0;
        foreach (var c in ds3.Parts.Collisions)
        {
            if (NotGround.Contains(c.HitFilterID) && c.SoundSpaceType == MSB3.Part.Collision.SoundSpace.NoReverb)
            {
                notGround++;
                continue;
            }
            if (model(MapPorter.ModelNumber(c.ModelName)) is not { } mesh)
            {
                noMesh++;
                continue;
            }
            byte type = (byte)c.SoundSpaceType;
            if (type > MaxType)
            {
                strange++;
                type = 0;
            }
            var m = PartMatrix(c);
            var (lo, hi) = Transform(mesh.Min, mesh.Max, m);
            var up = new Vector3(0, Headroom, 0);
            var down = new Vector3(0, Footroom, 0);
            var floors = new List<Box>();
            foreach (var (a0, b0, c0) in mesh.Triangles)
            {
                var (a, b, cc) = (Vector3.Transform(a0, m), Vector3.Transform(b0, m), Vector3.Transform(c0, m));
                if (IsFloor(a, b, cc))
                    floors.Add(new Box(Vector3.Min(a, Vector3.Min(b, cc)) - down, Vector3.Max(a, Vector3.Max(b, cc)) + up, type, c.Name));
            }
            places.Add(new Place(new Box(lo - down, hi + up, type, c.Name), floors));
        }
        if (notGround > 0)
            notes.Add($"{notGround} camera/enemy/kill collisions with no reverb skipped");
        if (noMesh > 0)
            notes.Add($"{noMesh} collisions without a readable mesh skipped");
        if (strange > 0)
            notes.Add($"{strange} collisions with a reverb type above {MaxType} taken as dry");
        return places;
    }

    /// <summary>
    /// Paints the map onto a grid, lowest priority first: the whole map dry, each collision's bounds (largest first),
    /// then each collision's floors (largest collision first), so a smaller collision wins over a larger one and a floor
    /// over a bounds box. Then the grid is cut back into boxes by a greedy sweep (along X, then Z, then up). Capped at
    /// <see cref="MaxRegions"/> (largest kept).
    /// </summary>
    public static List<Box> Plan(List<Place> places, List<string> notes)
    {
        if (places.Count == 0)
            return [];
        List<Box> plan = [];
        for (float step = Step; step <= MaxStep; step += 1)
        {
            plan = PlanAt(places, step, notes);
            if (plan.Count <= MaxRegions)
            {
                if (step > Step)
                    notes.Add($"grid step {step:F0} m (finer grids gave more than {MaxRegions} regions)");
                return plan;
            }
        }
        notes.Add($"{plan.Count} boxes at a {MaxStep:F0} m grid, the {plan.Count - MaxRegions} smallest dropped (Elden Ring's automatic reverb there)");
        return plan.OrderByDescending(b => b.Volume).Take(MaxRegions).ToList();
    }

    /// <summary>The coarsest grid step <see cref="Plan"/> tries.</summary>
    public const float MaxStep = 8f;

    private static List<Box> PlanAt(List<Place> places, float step, List<string> notes)
    {
        var lo = places.Select(p => p.Bounds.Min).Aggregate(Vector3.Min) - new Vector3(MapMargin);
        var hi = places.Select(p => p.Bounds.Max).Aggregate(Vector3.Max) + new Vector3(MapMargin);
        (int X, int Y, int Z) Cells(float s) => ((int)MathF.Ceiling((hi.X - lo.X) / s), (int)MathF.Ceiling((hi.Y - lo.Y) / s), (int)MathF.Ceiling((hi.Z - lo.Z) / s));
        var n = Cells(step);
        if ((long)n.X * n.Y * n.Z > MaxCells)
        {
            while ((long)n.X * n.Y * n.Z > MaxCells)
                n = Cells(step *= 1.25f);
            notes.Add($"grid step raised to {step:F1} m (a big map)");
        }
        var grid = new byte[(long)n.X * n.Y * n.Z]; // all 0: the whole map starts dry
        long At(int x, int y, int z) => ((long)y * n.Z + z) * n.X + x;

        // Cells whose centre is inside the box; a box thinner than a cell still takes the cell its middle is in.
        static (int, int) Span(float min, float max, float origin, float s, int count)
        {
            int a = (int)MathF.Round((min - origin) / s), b = (int)MathF.Round((max - origin) / s);
            if (b <= a)
                b = (a = (int)MathF.Floor(((min + max) / 2 - origin) / s)) + 1;
            return (Math.Clamp(a, 0, count), Math.Clamp(b, 0, count));
        }
        void Paint(Box box)
        {
            var (x0, x1) = Span(box.Min.X, box.Max.X, lo.X, step, n.X);
            var (y0, y1) = Span(box.Min.Y, box.Max.Y, lo.Y, step, n.Y);
            var (z0, z1) = Span(box.Min.Z, box.Max.Z, lo.Z, step, n.Z);
            for (int y = y0; y < y1; y++)
                for (int z = z0; z < z1; z++)
                    for (int x = x0; x < x1; x++)
                        grid[At(x, y, z)] = box.Type;
        }

        var bySize = places.OrderByDescending(p => p.Bounds.Volume).ThenBy(p => p.Bounds.Source, StringComparer.Ordinal).ToList();
        foreach (var place in bySize)
            Paint(place.Bounds);
        foreach (var place in bySize)
            foreach (var floor in place.Floors)
                Paint(floor);

        // Greedy sweep: from each unclaimed cell grow along X, then Z (whole rows), then Y (whole layers).
        const byte Claimed = 255;
        var plan = new List<Box>();
        for (int y = 0; y < n.Y; y++)
            for (int z = 0; z < n.Z; z++)
                for (int x = 0; x < n.X; x++)
                {
                    byte t = grid[At(x, y, z)];
                    if (t == Claimed)
                        continue;
                    int x1 = x + 1;
                    while (x1 < n.X && grid[At(x1, y, z)] == t)
                        x1++;
                    bool Row(int yy, int zz)
                    {
                        for (int xx = x; xx < x1; xx++)
                            if (grid[At(xx, yy, zz)] != t)
                                return false;
                        return true;
                    }
                    int z1 = z + 1;
                    while (z1 < n.Z && Row(y, z1))
                        z1++;
                    int y1 = y + 1;
                    while (y1 < n.Y)
                    {
                        bool layer = true;
                        for (int zz = z; zz < z1 && layer; zz++)
                            layer = Row(y1, zz);
                        if (!layer)
                            break;
                        y1++;
                    }
                    for (int yy = y; yy < y1; yy++)
                        for (int zz = z; zz < z1; zz++)
                            for (int xx = x; xx < x1; xx++)
                                grid[At(xx, yy, zz)] = Claimed;
                    plan.Add(new Box(lo + new Vector3(x, y, z) * step, lo + new Vector3(x1, y1, z1) * step, t, "grid"));
                }
        return plan;
    }

    /// <summary>The reverb type the plan gives a point (null outside every box); for checks.</summary>
    public static byte? TypeAt(IEnumerable<Box> plan, Vector3 p) => plan.FirstOrDefault(b => b.Contains(p))?.Type;

    /// <summary>Adds the plan to the layout as Elden Ring sound regions with a reverb override (box regions stand on
    /// the centre of their bottom face), each with its own RegionID, after any earlier ones of ours are removed.</summary>
    public static int AddRegions(MSBE msb, IReadOnlyList<Box> plan)
    {
        Strip(msb);
        int nextRegionId = msb.Regions.GetEntries().Select(r => r.RegionID).DefaultIfEmpty(-1).Max() + 1;
        int i = 0;
        foreach (var box in plan)
        {
            var size = box.Size;
            msb.Regions.SoundRegions.Add(new MSBE.Region.SoundRegion
            {
                Name = $"{Prefix}{box.Type}_{i++:D4}",
                Shape = new MSB.Shape.Box { Width = size.X, Height = size.Y, Depth = size.Z },
                Position = new Vector3((box.Min.X + box.Max.X) / 2, box.Min.Y, (box.Min.Z + box.Max.Z) / 2),
                Rotation = Vector3.Zero,
                RegionID = nextRegionId++,
                MapStudioLayer = 0xFFFFFFFF,
                MapID = -1,
                UnkE08 = 255,
                UnkS04 = 0,
                UnkS0C = -1,
                // As vanilla's reverb region: only the reverb override set, the others at their "not set" values.
                UnkT00 = box.Type,
                UnkT01 = 0,
                UnkT02 = 0,
                UnkT03 = 255,
                UnkT04 = -1,
                UnkT08 = -1,
                UnkT0A = -1,
                UnkT0C = 255,
            });
        }
        return plan.Count;
    }

    /// <summary>Our regions' name prefix ("DS3 reverb 5_0012"); Elden Ring names its own "サウンド領域_リバーブ設定_[n]".</summary>
    public const string Prefix = "DS3 reverb ";

    public static void Strip(MSBE msb) => msb.Regions.SoundRegions.RemoveAll(r => r.Name?.StartsWith(Prefix, StringComparison.Ordinal) == true);

    /// <summary>Reads a DS3 map's collision models (h binder) by model number.</summary>
    public static Dictionary<string, Model?> ReadModels(BXF4 binder, List<string> notes)
    {
        var models = new Dictionary<string, Model?>(StringComparer.Ordinal);
        foreach (var f in binder.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                models[MapCollisions.ModelNumberOf(ItemText.Leaf(f.Name))] = ReadModel(CollisionPort.ReadDs3(DCX.Decompress(f.Bytes)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                notes.Add($"{ItemText.Leaf(f.Name)} unreadable ({e.GetType().Name})");
            }
        }
        return models;
    }

    /// <summary>The whole step for one map: reads DS3's collision models, plans and adds the regions.</summary>
    public static Result Port(GameArchives ds3, MSB3 ds3Msb, MapId ds3Id, MSBE msb)
    {
        var notes = new List<string>();
        var path = MapCollisions.Ds3BinderPath(ds3Id, "h");
        var models = ds3.Contains(path) ? ReadModels(BXF4.Read(ds3.Read(path), ds3.Read(path[..^3] + "bdt")), notes) : [];
        if (models.Count == 0)
            notes.Add($"no collision models in {path}");
        return Port(ds3Msb, msb, n => models.GetValueOrDefault(n), notes);
    }

    public static Result Port(MSB3 ds3Msb, MSBE msb, Func<string, Model?> model, List<string> notes)
    {
        var places = Places(ds3Msb, model, notes);
        var plan = Plan(places, notes);
        AddRegions(msb, plan);
        return new Result(places.Count, places.Count(p => p.Bounds.Type != 0), plan.Count,
            plan.GroupBy(b => b.Type).ToDictionary(g => g.Key, g => g.Count()), notes);
    }
}
