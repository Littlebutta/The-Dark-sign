using DarkSign.Core;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>Per-game facts the porter needs on top of the launcher's <see cref="GameDefinition"/>.</summary>
public sealed record PorterGame(
    GameId Id,
    string ShortName,
    BHD5.Game ArchiveFormat,
    string UxmName,
    string UxmKeysField,
    string ParamdexName,
    IReadOnlyList<string> Archives)
{
    public static readonly PorterGame DarkSouls3 = new(
        GameId.DarkSouls3, "DS3", BHD5.Game.DarkSouls3, "DarkSouls3", "DarkSouls3Keys", "DS3",
        ["Data1", "Data2", "Data3", "Data4", "Data5", "DLC1", "DLC2"]);

    public static readonly PorterGame EldenRing = new(
        GameId.EldenRing, "ER", BHD5.Game.EldenRing, "EldenRing", "EldenRingKeys", "ER",
        ["Data0", "Data1", "Data2", "Data3", "DLC", @"sd\sd", @"sd\sd_dlc02"]);

    public static readonly IReadOnlyList<PorterGame> All = [DarkSouls3, EldenRing];

    public static PorterGame Parse(string name) =>
        All.FirstOrDefault(g => string.Equals(g.ShortName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown game '{name}'. Use DS3 or ER.");
}
