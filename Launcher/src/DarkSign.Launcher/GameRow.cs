using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>One line of the game list. <paramref name="root"/> gives the current data folder.</summary>
public sealed class GameRow(GameDefinition definition, Func<string> root) : INotifyPropertyChanged
{
    private string _exePath = "";
    private string _arguments = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameDefinition Definition { get; } = definition;
    public string DisplayName => Definition.DisplayName;

    public string ExePath
    {
        get => _exePath;
        set { _exePath = value; Notify(); Notify(nameof(Status)); Notify(nameof(CanLaunch)); }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; Notify(); }
    }

    /// <summary>Elden Ring is played with the PLAY button (with the mod); the others launch from the list.</summary>
    public bool IsEldenRing => Definition.Id == GameId.EldenRing;

    /// <summary>Only copies inside the data folder can be launched from the list: the save redirect is put next to the exe.</summary>
    public bool CanLaunch => !IsEldenRing && File.Exists(ExePath) && PathGuard.IsInsideRoot(ExePath, root());

    public string Status => string.IsNullOrEmpty(ExePath) ? "Not found: Browse…"
        : !File.Exists(ExePath) ? "Missing: Browse…"
        : IsEldenRing ? "Ready (PLAY above)"
        // A player's own install (e.g. Steam) is only read, to build the Elden Ring mod from.
        : !PathGuard.IsInsideRoot(ExePath, root()) ? "Found (read only)"
        : SaveRedirectInstaller.Check(Definition.Id, ExePath, RedirectDll.Bytes) switch
        {
            RedirectState.Conflict => "dinput8.dll in use",
            RedirectState.Missing when RedirectDll.Bytes is null => "No save redirect",
            _ => "Ready",
        };

    public void Refresh()
    {
        Notify(nameof(Status));
        Notify(nameof(CanLaunch));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
