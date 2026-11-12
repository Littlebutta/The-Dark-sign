using System.Text.RegularExpressions;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Converts Dark Souls III cutscenes (remo binders) into Elden Ring cutscene binders, so the boss intros and other
/// cutscenes the ported maps' scripts play run in Elden Ring.
/// </summary>
public static partial class CutscenePort
{
    /// <summary>"/remo/s45_00_0010.remobnd.dcx": where DS3 keeps a cutscene.</summary>
    public static string Ds3Path(string name) => $"/remo/{name}.remobnd.dcx";

    /// <summary>"/cutscene/s19_70_0010.cutscenebnd.dcx": where Elden Ring keeps one.</summary>
    public static string ErPath(string name) => $"/cutscene/{name}.cutscenebnd.dcx";

    /// <summary>"s45_00_0010" for cutscene ID 45000010: both games name a cutscene sAA_BB_NNNN after its ID AABBNNNN.</summary>
    public static string Name(int id) => $"s{id / 1_000_000:D2}_{id / 10_000 % 100:D2}_{id % 10_000:D4}";

    /// <summary>The cutscene ID of a name like "s45_00_0010", or null.</summary>
    public static int? IdOf(string name)
    {
        var m = NameRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) * 1_000_000 + int.Parse(m.Groups[2].Value) * 10_000 + int.Parse(m.Groups[3].Value) : null;
    }

    [GeneratedRegex(@"s(\d\d)_(\d\d)_(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();
}
