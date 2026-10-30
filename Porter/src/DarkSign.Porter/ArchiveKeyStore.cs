using System.Text.RegularExpressions;

namespace DarkSign.Porter;

/// <summary>Reads the RSA public keys for each archive header out of UXM's ArchiveKeys.cs.</summary>
public static partial class ArchiveKeyStore
{
    [GeneratedRegex(@"public static Dictionary<string, string> (\w+)\s*=")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"\[""(.+?)""\]\s*=\s*@""(-----BEGIN RSA PUBLIC KEY-----.+?-----END RSA PUBLIC KEY-----)""", RegexOptions.Singleline)]
    private static partial Regex KeyRegex();

    /// <summary>Archive name (e.g. "Data1", @"sd\sd") to PEM key, for one game.</summary>
    public static IReadOnlyDictionary<string, string> Load(string keysFile, PorterGame game)
    {
        var text = File.ReadAllText(keysFile);
        var fields = FieldRegex().Matches(text).ToList();
        var start = fields.FindIndex(m => m.Groups[1].Value == game.UxmKeysField);
        if (start < 0)
            throw new InvalidDataException($"No {game.UxmKeysField} in {keysFile}.");

        var end = start + 1 < fields.Count ? fields[start + 1].Index : text.Length;
        var section = text[fields[start].Index..end];
        return KeyRegex().Matches(section).ToDictionary(
            m => m.Groups[1].Value.Replace(@"\\", @"\"),
            m => m.Groups[2].Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
