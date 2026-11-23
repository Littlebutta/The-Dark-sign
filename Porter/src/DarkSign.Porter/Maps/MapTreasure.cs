using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's items lying in the maps: MSB "Treasure" events, each an item lot on a part (a corpse, a chest, a glowing
/// spot on the floor). Up to 1.0.57x none were ported (map-port-plan "Still missing: ... treasure"), so the ported maps
/// held no pickups. Each DS3 treasure whose part was placed becomes an Elden Ring treasure event on the ported part:
/// its lot is laid out in ItemLotParam_map with the lots the scripts award (<see cref="Enemies.DropPort.PortLot"/>,
/// so its "got it" flag moves with the scripts' and an item is taken once), the pickup animation is DS3's (the
/// player's animation IDs mean the same in both games, ObjectPort), and a treasure in a chest starts hidden until the
/// chest's script shows it, as DS3's InChest does. DS3's second lot (rarely used) and its prompt row are left out:
/// Elden Ring has one lot per treasure and its own pickup prompt.
/// </summary>
public static class MapTreasure
{
    /// <summary>Name prefix of the events this step adds, so a rerun can find and replace them.</summary>
    public const string NamePrefix = "DS3 treasure ";

    /// <summary>Every item lot DS3's treasure events in these maps give (for the lot port).</summary>
    public static SortedSet<int> Lots(GameArchives ds3, IEnumerable<MapSlots.Slot> slots)
    {
        var lots = new SortedSet<int>();
        foreach (var slot in slots.Select(s => s.Ds3).Distinct())
        {
            var path = MapPorter.MsbPath(slot);
            if (!ds3.Contains(path))
                continue;
            foreach (var t in MSB3.Read(ds3.Read(path)).Events.Treasures)
                if (t.ItemLot1 > 0)
                    lots.Add(t.ItemLot1);
        }
        return lots;
    }

    /// <summary>
    /// Adds the treasure events to each ported map file. <paramref name="partNames"/>: DS3 part name -> ported part
    /// name for the slot; <paramref name="lot"/>: DS3 lot -> Elden Ring ItemLotParam_map row, or null.
    /// </summary>
    public static string Apply(PorterContext ctx, GameArchives ds3, IEnumerable<MapSlots.Slot> slots,
        Func<MapId, IReadOnlyDictionary<string, string>?> partNames, Func<long, long?> lot)
    {
        int added = 0, noPart = 0, noLot = 0, maps = 0;
        foreach (var slot in slots)
        {
            var ds3Path = MapPorter.MsbPath(slot.Ds3);
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!ds3.Contains(ds3Path) || !File.Exists(path))
                continue;
            var names = partNames(slot.Er) ?? new Dictionary<string, string>();
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            msb.Events.Treasures.RemoveAll(t => t.Name.StartsWith(NamePrefix, StringComparison.Ordinal));
            var placed = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var t in MSB3.Read(ds3.Read(ds3Path)).Events.Treasures)
            {
                if (t.TreasurePartName is null || !names.TryGetValue(t.TreasurePartName, out var part) || !placed.Contains(part))
                {
                    noPart++;
                    continue;
                }
                if (t.ItemLot1 <= 0 || lot(t.ItemLot1) is not { } erLot)
                {
                    noLot++;
                    continue;
                }
                msb.Events.Treasures.Add(new MSBE.Event.Treasure
                {
                    Name = $"{NamePrefix}{t.Name}",
                    TreasurePartName = part,
                    ItemLotID = (int)erLot,
                    ActionButtonID = -1,
                    PickupAnimID = t.PickupAnimID,
                    InChest = (byte)(t.InChest ? 1 : 0),
                    StartDisabled = (byte)(t.InChest ? 1 : 0),
                    EntityID = t.EntityID > 0 ? (uint)slot.Ds3.MoveEntity(t.EntityID, slot.Er) : 0,
                });
                added++;
            }
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            maps++;
        }
        return $"map treasure: {added} DS3 items lying in {maps} maps placed as Elden Ring treasure"
               + (noPart > 0 ? $"; {noPart} on parts not placed" : "") + (noLot > 0 ? $"; {noLot} whose lot has no Elden Ring item yet" : "");
    }
}
