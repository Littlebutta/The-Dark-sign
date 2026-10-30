using System.Net.Http;

namespace DarkSign.Porter;

/// <summary>
/// Community data the porter needs but the repo does not carry: archive keys and file-name lists from
/// UXM Selective Unpack, and paramdefs and row names from Paramdex. Each comes from a pinned commit and is
/// cached under the work folder, so a run after the first works offline.
/// </summary>
public sealed class ExternalData(string cacheDir)
{
    private const string UxmBase =
        "https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/";
    private const string ParamdexBase =
        "https://raw.githubusercontent.com/soulsmods/Paramdex/ff7245e524329bc3eab00036723d2bd53384cedf/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string UxmKeysFile() => Get(UxmBase + "ArchiveKeys.cs", "uxm/ArchiveKeys.cs");

    public string UxmDictionary(PorterGame game) =>
        Get(UxmBase + $"res/{game.UxmName}Dictionary.txt", $"uxm/{game.UxmName}Dictionary.txt");

    /// <summary>
    /// A paramdef by file name, or null. Paramdex names DS3 defs by param type (EQUIP_PARAM_WEAPON_ST) and
    /// Elden Ring defs by param name (EquipParamWeapon), so callers try both.
    /// </summary>
    public string? ParamDef(PorterGame game, string fileName) =>
        TryGet(ParamdexBase + $"{game.ParamdexName}/Defs/{fileName}.xml", $"paramdex/{game.ParamdexName}/Defs/{fileName}.xml");

    /// <summary>Row names for a param, or null when Paramdex has none for it.</summary>
    public string? ParamNames(PorterGame game, string paramName) =>
        TryGet(ParamdexBase + $"{game.ParamdexName}/Names/{paramName}.txt", $"paramdex/{game.ParamdexName}/Names/{paramName}.txt");

    private string Get(string url, string relative) =>
        TryGet(url, relative) ?? throw new FileNotFoundException($"Could not download {url}");

    private string? TryGet(string url, string relative)
    {
        var path = Path.Combine(cacheDir, relative.Replace('/', Path.DirectorySeparatorChar));
        var missing = path + ".missing";
        if (File.Exists(path))
            return path;
        if (File.Exists(missing))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = Http.GetAsync(url).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            File.WriteAllText(missing, url);
            return null;
        }
        response.EnsureSuccessStatusCode();
        var tmp = path + ".part";
        File.WriteAllBytes(tmp, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
