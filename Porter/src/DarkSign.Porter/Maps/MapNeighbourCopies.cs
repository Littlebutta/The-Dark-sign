using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map's low-detail copies of a joined neighbour, hidden the way Elden Ring hides its own: as assets that switch off
/// while the neighbour is loaded (MSB asset field DisableWhenMapLoadedMapID, "disables the asset when the specified map
/// is loaded", SoulsFormatsNEXT MSBE PartsParam AssetUnk3).
/// <para>
/// <b>Why.</b> DS3 ships low-detail copies of one map inside another: the High Wall (m30_00) holds Lothric Castle's
/// outside as blocky shells (m980xxx, "m30_00_low_royalpalace..." textures) for the view across the bridge, and hides
/// them by draw groups wherever the real castle (m30_01) is shown. <see cref="MapStandIns"/> keeps them, because
/// nothing in their own map stands for them. In the port the castle is a map of its own, so standing in it the High
/// Wall's shells were drawn over the real castle: the black slab 3-5 m ahead at the Lothric Castle start (1.0.681
/// round 8: removing m980010/12/14 from m45_01's layout cleared it). Six draw group layouts (#254, V1, #314, #315,
/// #316, #321) never hid them in game: Elden Ring draws every piece of every loaded map whatever its groups.
/// </para>
/// <para>
/// <b>What is done (every map, every joined pair).</b> A piece is a copy of a neighbour when it is mostly "low"
/// texture (<see cref="MapStandIns.IsLowTexture"/>), the neighbour is joined to its map by a DS3 doorway and is a
/// different Elden Ring slot, and the neighbour's detail fills its box: at least <see cref="MapStandIns.MinCovered"/>
/// of the neighbour's detailed pieces stand inside it, they hold at least a quarter of its vertices (the same test
/// <see cref="MapStandIns"/> uses within a map), and more than its own map's detail inside it. Such a piece is placed
/// as an asset of the same model in the same place, disabled while that neighbour is loaded. With the neighbour
/// loaded its real geometry is drawn in full there, so the copy is never needed; with it unloaded (the far view from
/// the High Wall) the copy shows as before.
/// </para>
/// </summary>
public static class MapNeighbourCopies
{
    /// <summary>One copy: piece <paramref name="Piece"/> of <paramref name="Ds3"/> stands for <paramref name="Neighbour"/>'s detail.</summary>
    public sealed record Copy(MapId Ds3, MapId Er, string Piece, MapId Neighbour, MapId NeighbourEr, int Vertices, int Covered, long CoveredVertices, long OwnVertices)
    {
        public string Line => $"{Piece} (copy of {Neighbour} -> {NeighbourEr}: {Covered} of its detailed pieces with {CoveredVertices:N0} vertices inside, own detail {OwnVertices:N0}, itself {Vertices:N0})";
    }

    /// <summary>Every low-detail piece that copies a joined neighbour's detail, judged in DS3's shared world space.</summary>
    public static List<Copy> Find(IReadOnlyList<MapConnectDisplay.MapData> maps)
    {
        var ids = maps.Select(m => m.Ds3).ToList();
        var joined = ids.ToDictionary(m => m, _ => new HashSet<MapId>());
        foreach (var m in maps)
            foreach (var c in m.Msb.Parts.ConnectCollisions)
                if (MsbPort.ConnectTarget(c.MapID, ids) is { } t && t != m.Ds3)
                {
                    joined[m.Ds3].Add(t);
                    joined[t].Add(m.Ds3);
                }
        var byId = maps.ToDictionary(m => m.Ds3);
        var copies = new List<Copy>();
        foreach (var m in maps)
        {
            foreach (var s in m.Pieces)
            {
                if (!s.Low || s.Vertices <= 0)
                    continue;
                long own = Inside(s, m.Pieces, out _);
                Copy? best = null;
                foreach (var n in joined[m.Ds3].OrderBy(n => n.ToString()))
                {
                    var other = byId[n];
                    if (other.Er == m.Er)
                        continue;
                    long covered = Inside(s, other.Pieces, out int count);
                    if (count < MapStandIns.MinCovered || covered * 4 < s.Vertices || covered <= own)
                        continue;
                    if (best is null || covered > best.CoveredVertices)
                        best = new Copy(m.Ds3, m.Er, s.Name, n, other.Er, s.Vertices, count, covered, own);
                }
                if (best is not null)
                    copies.Add(best);
            }
        }
        return copies;
    }

    /// <summary>Vertices (and count) of the detailed pieces of <paramref name="pieces"/> whose centre is inside <paramref name="s"/>'s box.</summary>
    private static long Inside(MapStandIns.PieceBox s, IReadOnlyList<MapStandIns.PieceBox> pieces, out int count)
    {
        count = 0;
        long vertices = 0;
        foreach (var d in pieces)
        {
            if (d.Low || ReferenceEquals(d, s) || d.Name == s.Name && d.Min == s.Min)
                continue;
            var centre = (d.Min + d.Max) / 2;
            if (Vector3.Clamp(centre, s.Min - Vector3.One, s.Max + Vector3.One) != centre)
                continue;
            count++;
            vertices += d.Vertices;
        }
        return vertices;
    }

    /// <summary>The asset field's map: the neighbour's Elden Ring slot, area, block, region, index.</summary>
    public static sbyte[] MapBytes(MapId er) => [(sbyte)er.Area, (sbyte)er.Block, (sbyte)er.Region, (sbyte)er.Index];

    /// <summary>
    /// Replaces each copy's map piece in this Elden Ring layout by an asset part of <paramref name="assetOf"/>'s model,
    /// same place, disabled while the neighbour is loaded. Returns the pieces replaced; a copy whose asset didn't
    /// convert stays a map piece (named in <paramref name="notes"/>).
    /// </summary>
    public static int Place(MSBE msb, IEnumerable<Copy> copies, Func<Copy, ObjectPort.Asset?> assetOf, List<string> notes)
    {
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int replaced = 0;
        var kept = new List<string>();
        foreach (var copy in copies)
        {
            var piece = msb.Parts.MapPieces.FirstOrDefault(p => p.Name == copy.Piece);
            if (piece is null)
                continue;
            var asset = assetOf(copy);
            if (asset is not { Converted: true })
            {
                kept.Add($"{copy.Piece} ({asset?.Failure ?? "no asset"})");
                continue;
            }
            var part = ObjectPort.NewPart();
            part.Position = piece.Position;
            part.Rotation = piece.Rotation;
            part.Scale = piece.Scale;
            part.EntityID = piece.EntityID;
            part.Gparam.LightSetID = piece.Gparam.LightSetID;
            part.Gparam.FogParamID = piece.Gparam.FogParamID;
            MsbPort.ApplyPieceGroups(part.Unk1);
            var model = asset.Name;
            int instance = 9000;
            while (names.Contains($"{model}_{instance:D4}"))
                instance++;
            part.Name = $"{model}_{instance:D4}";
            part.InstanceID = instance;
            part.ModelName = model;
            part.UnkT54PartName = part.Name;
            part.UnkPartNames[4] = part.Name;
            part.UnkPartNames[5] = part.Name;
            part.AssetUnk3.DisableWhenMapLoadedMapID = MapBytes(copy.NeighbourEr);
            names.Add(part.Name);
            msb.Parts.Assets.Add(part);
            if (msb.Models.Assets.All(m => m.Name != model))
                msb.Models.Assets.Add(new MSBE.Model.Asset
                {
                    Name = model,
                    SibPath = $@"N:\GR\data\Asset\Environment\geometry\{model[..6]}\{model}\sib\{model}.sib",
                });
            msb.Parts.MapPieces.Remove(piece);
            if (msb.Parts.MapPieces.All(p => p.ModelName != piece.ModelName))
                msb.Models.MapPieces.RemoveAll(m => m.Name == piece.ModelName);
            asset.Placed++;
            replaced++;
        }
        if (kept.Count > 0)
            notes.Add($"{kept.Count} neighbour copies stay map pieces (asset not built): {string.Join(", ", kept.Take(8))}");
        return replaced;
    }

    /// <summary>The asset key of a copy's model: the DS3 map and model number, as cutscene map pieces are keyed (CutsceneCast.Ds3ModelPath).</summary>
    public static string ModelKey(MapId ds3, string ds3ModelName) => $"{ds3}_{MapPorter.ModelNumber(ds3ModelName)}";
}
