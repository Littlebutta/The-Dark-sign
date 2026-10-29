using System.Text.Json;
using System.Text.Json.Nodes;

namespace DarkSign.Core;

/// <summary>
/// Bloodborne's port (bbport) reads its saves folder and online switch from
/// %APPDATA%\bbport-launcher\settings.json, which outranks environment variables. Each profile
/// gets its own copy of that file inside the project, pointed at the profile's saves with online
/// play off, and the game is started with APPDATA aimed at it. The player's own file is only read.
/// </summary>
public static class BbportConfig
{
    private const string FolderName = "bbport-launcher";
    private const string FileName = "settings.json";

    public static string AppDataDirectory(string root, string profileName) =>
        Path.Combine(root, "TheDarkSign", "Config", SaveIsolation.SanitizeFolderName(profileName), "BB");

    /// <summary>Writes the profile's settings file and returns the folder to use as APPDATA.</summary>
    public static string Prepare(string root, string profileName, string saveDir, string? playerAppData)
    {
        var appData = AppDataDirectory(root, profileName);
        PathGuard.EnsureInsideRoot(appData, root);
        var file = Path.Combine(appData, FolderName, FileName);

        // First start of a profile: begin from the player's own setup (game folder, graphics).
        var source = File.Exists(file) ? file
            : playerAppData is null ? null
            : Path.Combine(playerAppData, FolderName, FileName);
        var settings = Load(source);

        settings["user_dir"] = saveDir;
        settings["online"] = false;

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return appData;
    }

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
