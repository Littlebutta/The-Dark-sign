namespace DarkSign.Porter.Maps;

/// <summary>
/// One place that says what a DS3 number becomes in Elden Ring, for the kinds of numbers that no single port step
/// owns. Up to 1.0.57x each kind was moved only by the step that first needed it, and the event scripts copied the
/// rest unchanged (DS3 to Elden Ring reference map, 2026-11-22): the lots the scripts award, the AI the scripts
/// switch characters to, and the items they check named nothing, or other things, in Elden Ring.
/// <para>
/// The tables come from the files the earlier steps of the same run write next to the mod (the enemy port's
/// darksign_chr_map.tsv and darksign_script_lots.tsv), read once by <see cref="Load"/> before the events step. A
/// number with no entry is kept as it was, as before.
/// </para>
/// </summary>
public sealed class Ds3Ids
{
    /// <summary>The run's tables, set by <see cref="Load"/>; null keeps every number (offline checks, other commands).</summary>
    public static Ds3Ids? Current { get; set; }

    private readonly Dictionary<long, long> _lots;
    private readonly EnemyPort.ChrMap? _chrMap;
    private readonly Func<int, int, int?>? _items;

    public Ds3Ids(Dictionary<long, long> lots, EnemyPort.ChrMap? chrMap, Func<int, int, int?>? items)
    {
        _lots = lots;
        _chrMap = chrMap;
        _items = items;
    }

    public static Ds3Ids Load(PorterContext ctx, Func<int, int, int?>? items)
    {
        var chrPath = ctx.ModPath(EnemyPort.ChrMap.FileName);
        var chrMap = File.Exists(chrPath) ? EnemyPort.ChrMap.Parse(File.ReadAllLines(chrPath)) : null;
        return Current = new Ds3Ids(Enemies.DropPort.ReadScriptLots(ctx), chrMap, items);
    }

    /// <summary>The ItemLotParam_map row a DS3 lot the scripts award was ported to, or null.</summary>
    public long? ItemLot(long ds3) => _lots.TryGetValue(ds3, out var er) ? er : null;

    /// <summary>The NpcThinkParam row a DS3 row was ported to with its character's AI (any model), or null.</summary>
    public long? Think(long ds3) => ds3 is > 0 and < int.MaxValue && _chrMap?.ThinkAny((int)ds3) is { } er ? er : null;

    /// <summary>
    /// The Elden Ring item for a DS3 item of an event item type (0 weapon, 1 armour, 2 ring, 3 goods): the ported
    /// weapon, else the Elden Ring item of the same name (the drops' rule), or null.
    /// </summary>
    public long? Item(long type, long ds3) => ds3 is > 0 and < int.MaxValue && type is >= 0 and <= 3 ? _items?.Invoke((int)type, (int)ds3) : null;

    public int Lots => _lots.Count;
}
