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
    }

    public sealed record Result(MSBE Msb, int MapPieces, int Collisions, int Players, int StartEntityId,
        List<string> Notes);

    /// <summary>The entity ID of the player start the "Start DS3 run" warp lands on: 990 in the host map's range.</summary>
    public static int StartEntityId(MapId er) => er.EntityBase + 990;

    public static Result Convert(MSB3 ds3, MSBE host, MapId ds3Id, MapId erId, Options? options = null)
    {
        options ??= new Options();
        var notes = new List<string>();
        var msb = new MSBE();

        var mapPieceTemplate = host.Parts.MapPieces.FirstOrDefault() ?? NewMapPiece();
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
            ApplyGroups(src, part.Unk1, options);
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
                    unk2.DispGroups[i] = options.AllVisible ? 0xFFFFFFFF : i < src.DispGroups.Length ? src.DispGroups[i] : 0;
            // Same numbering in both games (8 = normal floor and walls).
            part.HitFilterID = (MSBE.Part.Collision.HitFilterType)src.HitFilterID;
            // Online/region IDs from the colosseum would only confuse the game; Elden Ring treats 0/-1 as unset.
            part.PlayRegionID = -1;
            part.LocationTextID = -1;
            msb.Parts.Collisions.Add(part);
            AddModel(msb.Models.Collisions, src.ModelName, collisionModel, erId);
        }

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

    private static void CopyCommon(MSB3.Part src, MSBE.Part part, MapId ds3Id, MapId erId)
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

    // "m000100_0003" -> 3
    private static int InstanceOf(string name)
    {
        var underscore = name.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(name[(underscore + 1)..], out var n) ? n : 0;
    }

    private static void ApplyGroups(MSB3.Part src, MSBE.Part.UnkStruct1? unk1, Options options)
    {
        if (unk1 is null)
            return;
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
    }

    private static void AddModel<T>(List<T> models, string name, T? template, MapId erId) where T : MSBE.Model, new()
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
