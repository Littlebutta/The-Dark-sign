using System.Net.Http;

namespace DarkSign.Porter;

/// <summary>
/// Community data the porter needs but the repo does not carry: archive keys and file-name lists from
/// UXM Selective Unpack, paramdefs and row names from Paramdex, and event instruction definitions from DarkScript3. Each comes from a pinned commit and is
/// cached under the work folder, so a run after the first works offline.
/// </summary>
public sealed class ExternalData(string cacheDir)
{
    private const string UxmBase =
        "https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/";
    private const string ParamdexBase =
        "https://raw.githubusercontent.com/soulsmods/Paramdex/ff7245e524329bc3eab00036723d2bd53384cedf/";

    // DarkScript3's instruction definitions (emedf) for each game's event scripts.
    private const string DarkScript3Base =
        "https://raw.githubusercontent.com/AinTunez/DarkScript3/733f024253930b70db8e3d0039c4b53b6ee0a2d4/DarkScript3/Resources/";

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

    /// <summary>DarkScript3's event instruction definitions: "ds3-common" or "er-common".</summary>
    public string Emedf(string name) => Get(DarkScript3Base + $"{name}.emedf.json", $"darkscript3/{name}.emedf.json");

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
            try { File.WriteAllText(missing, url); }
            catch (IOException) { /* another porter process wrote it */ }
            return null;
        }
        response.EnsureSuccessStatusCode();
        // port-weapons and port-maps run side by side, so on a first build both may fetch the same file: each writes its
        // own temporary file, and if the other process already put the file in place, that one is kept.
        var tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.part";
        File.WriteAllBytes(tmp, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        try
        {
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(tmp);
        }
        return path;
    }
}
