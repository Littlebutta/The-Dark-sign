using System.IO;

namespace DarkSign.Launcher;

/// <summary>The save-redirect DLL embedded in this exe, or null in a build made without it.</summary>
internal static class RedirectDll
{
    public static byte[]? Bytes { get; } = Load();

    private static byte[]? Load()
    {
        using var stream = typeof(RedirectDll).Assembly.GetManifestResourceStream("dksign_saveredirect.dll");
        if (stream is null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
