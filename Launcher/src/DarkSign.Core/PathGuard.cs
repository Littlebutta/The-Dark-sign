namespace DarkSign.Core;

/// <summary>Enforces folder isolation: nothing outside the project root is ever launched or written.</summary>
public static class PathGuard
{
    public static bool IsInsideRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        var fullRoot = Normalize(root);
        var fullPath = Normalize(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    public static void EnsureInsideRoot(string path, string root)
    {
        if (!IsInsideRoot(path, root))
            throw new IsolationViolationException($"'{path}' is outside the project root '{root}'.");
    }

    private static string Normalize(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public sealed class IsolationViolationException(string message) : Exception(message);
