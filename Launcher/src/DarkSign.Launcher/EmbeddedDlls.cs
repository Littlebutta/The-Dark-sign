using System.IO;

namespace DarkSign.Launcher;

/// <summary>The save-redirect DLL embedded in this exe, or null in a build made without it.</summary>
internal static class RedirectDll
{
    public static byte[]? Bytes { get; } = EmbeddedDlls.Load("dksign_saveredirect.dll");
}

/// <summary>The F1 test menu DLL embedded in this exe, or null in a build made without it.</summary>
internal static class TestMenuDll
{
    public static byte[]? Bytes { get; } = EmbeddedDlls.Load("dksign_testmenu.dll");
}

internal static class EmbeddedDlls
{
    public static byte[]? Load(string name)
    {
        using var stream = typeof(EmbeddedDlls).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
