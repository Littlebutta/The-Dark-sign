using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Builds an Elden Ring map layout (MSBE) from a Dark Souls III one (MSB3).
///
/// Elden Ring parts carry many fields DS3 has no equivalent for (shadow flags, tile loading, gparam slots). Each
/// converted part is a copy of a vanilla part of the same kind from the host map's own MSB, so those fields hold
/// values the game already accepts, and only what DS3 defines is overwritten: name, model, position, rotation,
/// scale, entity ID and, for collisions, the hit filter. This is the same "template part" approach Smithbox's
/// map editor uses when it duplicates parts.
///
/// Stage 1 ports map pieces, collisions and the player start points. Objects, enemies, regions and events follow
/// once the enemy porter can place DS3 characters.
/// </summary>
public static class MsbPort
{
    public sealed class Options
    {
        /// <summary>
        /// Draw every part from everywhere (all display and draw groups on). DS3 and Elden Ring both hide parts by
        /// group masks, but Elden Ring's three mask arrays are not confirmed to mean what DS3's do, and a wrong mask
        /// hides the map. Off: DS3's masks are copied by position (DrawGroups -> DisplayGroups and so on).
        /// </summary>
        public bool AllVisible { get; init; } = true;

        /// <summary>
        /// No group masks at all (every array zero), the way JortPob's parts and the colosseum's own parts have them.
        /// Wins over <see cref="AllVisible"/>. Fallback "map-groups" C.
        /// </summary>
        public bool NoGroups { get; init; }

        /// <summary>The PartsDrawParam row every map piece names (see <see cref="MapDrawParams"/>).</summary>
        public short DrawParamId { get; init; } = MapDrawParams.VanillaRow;
    }

    public sealed record Result(MSBE Msb, int MapPieces, int Collisions, int Players, int StartEntityId,
        List<string> Notes);

    /// <summary>The entity ID of the player start the "Start DS3 run" warp lands on: 990 in the host map's range.</summary>
    public static int StartEntityId(MapId er) => er.EntityBase + 990;

    /// <param name="slots">Every ported DS3 map and its slot, so connect collisions can point at the right map.</param>
    /// <param name="connectTemplate">An Elden Ring connect collision to copy (the host map usually has none).</param>
    public static Result Convert(MSB3 ds3, MSBE host, MapId ds3Id, MapId erId, Options? options = null,
        IReadOnlyList<(MapId Ds3, MapId Er)>? slots = null, MSBE.Part.ConnectCollision? connectTemplate = null)
    {
        options ??= new Options();
        var notes = new List<string>();
        var msb = new MSBE();

        // An ordinary piece when the host has one. The colosseums hold a single map piece, a proxy drawn only in
        // reflections, so whatever is copied is then set to draw normally (DrawNormally).
        var mapPieceTemplate = host.Parts.MapPieces.FirstOrDefault(p => !p.DrawOnlyReflectCam && !p.IsShadowOnly)
                               ?? host.Parts.MapPieces.FirstOrDefault() ?? NewMapPiece();
        if (mapPieceTemplate.DrawOnlyReflectCam || mapPieceTemplate.IsShadowOnly)
            notes.Add("the host's map pieces are drawn only in reflections or shadows; the ported pieces get ordinary draw flags");
        var collisionTemplate = host.Parts.Collisions.FirstOrDefault() ?? NewCollision();
        var playerTemplate = host.Parts.Players.FirstOrDefault() ?? NewPlayer();
        if (host.Parts.MapPieces.Count == 0 || host.Parts.Collisions.Count == 0 || host.Parts.Players.Count == 0)
            notes.Add("the host MSB lacks a map piece, collision or player part to copy; defaults were used");

        var mapPieceModel = host.Models.MapPieces.FirstOrDefault();
        var collisionModel = host.Models.Collisions.FirstOrDefault();
        var playerModel = host.Models.Players.FirstOrDefault();

        foreach (var src in ds3.Parts.MapPieces)
        {
            var part = (MSBE.Part.MapPiece)mapPieceTemplate.DeepCopy();
            CopyCommon(src, part, ds3Id, erId);
            ApplyPieceGroups(src, part.Unk1, options);
            DrawNormally(part, options.DrawParamId);
            msb.Parts.MapPieces.Add(part);
            AddModel(msb.Models.MapPieces, src.ModelName, mapPieceModel, erId);
        }

        foreach (var src in ds3.Parts.Collisions)
        {
            var part = (MSBE.Part.Collision)collisionTemplate.DeepCopy();
            CopyCommon(src, part, ds3Id, erId);
            ApplyGroups(src, part.Unk1, options);
            if (part.Unk2 is { } unk2)
                for (int i = 0; i < unk2.DispGroups.Length; i++)
                    unk2.DispGroups[i] = options.NoGroups ? 0 : options.AllVisible ? 0xFFFFFFFF : i < src.DispGroups.Length ? src.DispGroups[i] : 0;
            // Same numbering in both games (8 = normal floor and walls).
            part.HitFilterID = (MSBE.Part.Collision.HitFilterType)src.HitFilterID;
            // Online/region IDs from the colosseum would only confuse the game; Elden Ring treats 0/-1 as unset.
            part.PlayRegionID = -1;
            part.LocationTextID = -1;
            msb.Parts.Collisions.Add(part);
            AddModel(msb.Models.Collisions, src.ModelName, collisionModel, erId);
        }

        // Connect collisions load the neighbouring map when the player stands on them: the DS3 map they name moves to
        // its slot. One that names a map with no slot would load nothing, so it's left out.
        connectTemplate ??= host.Parts.ConnectCollisions.FirstOrDefault();
        int connects = 0, connectsSkipped = 0;
        foreach (var src in ds3.Parts.ConnectCollisions)
        {
            MapId? target = slots?.Where(s => s.Ds3.Area == src.MapID[0] && s.Ds3.Block == src.MapID[1]).Select(s => (MapId?)s.Er).FirstOrDefault();
            if (target is not { } to || connectTemplate is null || msb.Parts.Collisions.All(c => c.Name != src.CollisionName))
            {
                connectsSkipped++;
                continue;
            }
            var part = (MSBE.Part.ConnectCollision)connectTemplate.DeepCopy();
            CopyCommon(src, part, ds3Id, erId);
            ApplyGroups(src, part.Unk1, options);
            part.CollisionName = src.CollisionName;
            part.MapID = [(byte)to.Area, (byte)to.Block, (byte)to.Region, (byte)to.Index];
            msb.Parts.ConnectCollisions.Add(part);
            AddModel(msb.Models.Collisions, src.ModelName, collisionModel, erId);
            connects++;
        }
        if (ds3.Parts.ConnectCollisions.Count > 0)
            notes.Add($"connect collisions: {connects} ported, {connectsSkipped} left out (map not ported"
                      + (connectTemplate is null ? ", or no Elden Ring connect collision to copy" : "") + ")");

        // Player parts are spawn points (warps, respawns). The run's start point gets a fixed entity ID.
        var start = ChooseStart(ds3, ds3Id);
        foreach (var src in ds3.Parts.Players)
        {
            var part = (MSBE.Part.Player)playerTemplate.DeepCopy();
            CopyCommon(src, part, ds3Id, erId);
            ApplyGroups(src, part.Unk1, options);
            if (src == start)
            {
                part.Name = "c0000_9990";
                part.InstanceID = 9990;
                part.EntityID = (uint)StartEntityId(erId);
            }
            msb.Parts.Players.Add(part);
            AddModel(msb.Models.Players, "c0000", playerModel, erId);
        }
        if (start is null)
            notes.Add("DS3 map has no player start point; the run warp has nowhere to land");

        // Elden Ring's MSBs have one layer table entry list and reference models by index; SoulsFormats rebuilds
        // indices from names on write.
        return new Result(msb, msb.Parts.MapPieces.Count, msb.Parts.Collisions.Count, msb.Parts.Players.Count,
            StartEntityId(erId), notes);
    }

    /// <summary>
    /// The DS3 player part the run starts at: one whose map-local entity number is 990-999 (arrival points), else
    /// the first with an entity ID, else the first.
    /// </summary>
    public static MSB3.Part.Player? ChooseStart(MSB3 ds3, MapId ds3Id) =>
        ds3.Parts.Players.FirstOrDefault(p => ds3Id.OwnsDs3Id(p.EntityID) && (p.EntityID - ds3Id.Ds3EntityBase) is >= 990 and < 1000)
        ?? ds3.Parts.Players.FirstOrDefault(p => p.EntityID > 0)
        ?? ds3.Parts.Players.FirstOrDefault();

    internal static void CopyCommon(MSB3.Part src, MSBE.Part part, MapId ds3Id, MapId erId)
    {
        part.Name = src.Name;
        part.InstanceID = InstanceOf(src.Name);
        part.ModelName = src.ModelName;
        part.Position = src.Position;
        part.Rotation = src.Rotation;
        part.Scale = src.Scale == Vector3.Zero ? Vector3.One : src.Scale;
        part.EntityID = src.EntityID > 0 ? (uint)ds3Id.MoveEntity(src.EntityID, erId) : 0;
        part.EntityGroupIDs = new uint[part.EntityGroupIDs?.Length ?? 8];
        for (int i = 0; i < Math.Min(part.EntityGroupIDs.Length, src.EntityGroups.Length); i++)
            part.EntityGroupIDs[i] = src.EntityGroups[i] > 0 ? (uint)ds3Id.MoveEntity(src.EntityGroups[i], erId) : 0;
    }

    /// <summary>
    /// The draw flags of an ordinary Elden Ring map piece (314 of Stormveil's 343): drawn by the main camera and
    /// receiving shadows. The colosseums' only map piece, the template every ported piece was copied from, is a
    /// reflection proxy (DrawOnlyReflectCam), which left every DS3 map invisible (2026-11-09 playtest).
    /// </summary>
    internal static void DrawNormally(MSBE.Part part, short drawParamId = MapDrawParams.VanillaRow)
    {
        // Every vanilla map piece names a PartsDrawParam row (draw distance, LOD distances); the proxy names none.
        part.isUsePartsDrawParamID = 1;
        part.PartsDrawParamID = drawParamId;
        part.DrawOnlyReflectCam = false;
        part.DrawByReflectCam = false;
        part.IsShadowOnly = false;
        part.IsShadowDest = true;
        part.EnableOnAboveShadow = 0;
        part.IsCascade3ShadowSrc = 0;
    }

    // "m000100_0003" -> 3
    private static int InstanceOf(string name)
    {
        var underscore = name.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(name[(underscore + 1)..], out var n) ? n : 0;
    }

    private static void ClearGroups(MSBE.Part.UnkStruct1? unk1)
    {
        if (unk1 is null)
            return;
        Array.Clear(unk1.DisplayGroups);
        Array.Clear(unk1.DrawGroups);
        Array.Clear(unk1.CollisionMask);
        unk1.Condition1 = 0;
        unk1.Condition2 = 0;
    }

    /// <summary>
    /// Map pieces carry no group masks unless DS3's are asked for: every part the colosseums draw (483 assets) has
    /// all three arrays zero with both conditions 0, and so do JortPob's map pieces. All-ones masks, which draw
    /// enemies, are not a state any vanilla map piece is in.
    /// </summary>
    internal static void ApplyPieceGroups(MSB3.Part src, MSBE.Part.UnkStruct1? unk1, Options options)
    {
        if (options.NoGroups || options.AllVisible)
            ClearGroups(unk1);
        else
            ApplyGroups(src, unk1, options);
    }

    internal static void ApplyGroups(MSB3.Part src, MSBE.Part.UnkStruct1? unk1, Options options)
    {
        if (unk1 is null)
            return;
        if (options.NoGroups)
        {
            ClearGroups(unk1);
            return;
        }
        if (options.AllVisible)
        {
            Array.Fill(unk1.DisplayGroups, 0xFFFFFFFF);
            Array.Fill(unk1.DrawGroups, 0xFFFFFFFF);
            Array.Fill(unk1.CollisionMask, 0xFFFFFFFF);
            return;
        }
        // By position in the part struct: DS3 Draw/Disp/Backread -> Elden Ring Display/Draw/CollisionMask.
        Array.Clear(unk1.DisplayGroups);
        Array.Clear(unk1.DrawGroups);
        Array.Clear(unk1.CollisionMask);
        src.DrawGroups.AsSpan(0, Math.Min(8, src.DrawGroups.Length)).CopyTo(unk1.DisplayGroups);
        src.DispGroups.AsSpan(0, Math.Min(8, src.DispGroups.Length)).CopyTo(unk1.DrawGroups);
        src.BackreadGroups.AsSpan(0, Math.Min(8, src.BackreadGroups.Length)).CopyTo(unk1.CollisionMask);
        // Vanilla parts with masks have Condition2 = 1 (340 of Stormveil's 343 map pieces); parts without have 0.
        unk1.Condition2 = (byte)(unk1.DisplayGroups.Concat(unk1.DrawGroups).Any(g => g != 0) ? 1 : 0);
    }

    internal static void AddModel<T>(List<T> models, string name, T? template, MapId erId) where T : MSBE.Model, new()
    {
        if (models.Any(m => m.Name == name))
            return;
        var model = template is null ? new T() : (T)template.DeepCopy();
        model.Name = name;
        model.SibPath = template is null ? "" : SibPathFor(template.SibPath, template.Name, name, erId);
        models.Add(model);
    }

    /// <summary>The host model's SIB path with the model name (and map) swapped for the new model's.</summary>
    private static string SibPathFor(string templatePath, string templateName, string name, MapId erId)
    {
        if (string.IsNullOrEmpty(templatePath))
            return "";
        var path = templatePath.Replace(templateName, name, StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(path, @"m\d\d_\d\d_\d\d_\d\d", erId.ToString());
    }

    private static MSBE.Part.MapPiece NewMapPiece() => new();
    private static MSBE.Part.Collision NewCollision() => new();
    private static MSBE.Part.Player NewPlayer() => new();
}
