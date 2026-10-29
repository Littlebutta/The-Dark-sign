using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DarkSign.Core;

public sealed record UpdateInfo(Version Version, string Tag, string AssetApiUrl);

/// <summary>
/// Keeps the launcher on the latest GitHub release. CI publishes each build of main as a
/// release tagged "launcher-v&lt;version&gt;" with TheDarkSignLauncher.exe attached.
/// </summary>
public sealed class SelfUpdater(string? token, HttpClient? http = null)
{
    public const string Repository = "Littlebutta/The-Dark-sign";
    public const string TagPrefix = "launcher-v";
    public const string AssetName = "TheDarkSignLauncher.exe";

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>Returns the latest release when it is newer than <paramref name="current"/>, else null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = NewRequest($"https://api.github.com/repos/{Repository}/releases?per_page=20", "application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return PickNewest(json.RootElement, current);
    }

    internal static UpdateInfo? PickNewest(JsonElement releases, Version current)
    {
        UpdateInfo? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || !Version.TryParse(tag[TagPrefix.Length..], out var version))
                continue;
            var asset = release.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind == JsonValueKind.Undefined)
                continue;
            if (Normalize(version) > Normalize(current) && (best is null || version > best.Version))
                best = new UpdateInfo(version, tag, asset.GetProperty("url").GetString()!);
        }
        return best;
    }

    /// <summary>
    /// Downloads the update next to <paramref name="exePath"/> and swaps it in. A running exe can be
    /// renamed on Windows, so the current one moves aside to ".old" and is deleted on the next start.
    /// </summary>
    public async Task InstallAsync(UpdateInfo update, string exePath, CancellationToken ct = default)
    {
        var newPath = exePath + ".new";
        using (var request = NewRequest(update.AssetApiUrl, "application/octet-stream"))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(newPath);
            await response.Content.CopyToAsync(file, ct);
        }

        var oldPath = exePath + ".old";
        File.Delete(oldPath);
        File.Move(exePath, oldPath);
        File.Move(newPath, exePath);
    }

    public static void CleanUpPreviousUpdate(string exePath)
    {
        try { File.Delete(exePath + ".old"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Restart(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! });

    private HttpRequestMessage NewRequest(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TheDarkSignLauncher");
        request.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return request;
    }

    // Version("1.0.5") has Revision -1, which compares lower than "1.0.5.0".
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
