using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>One line of the game list.</summary>
public sealed class GameRow(GameDefinition definition) : INotifyPropertyChanged
{
    private string _exePath = "";
    private string _arguments = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameDefinition Definition { get; } = definition;
    public string DisplayName => Definition.DisplayName;

    public string ExePath
    {
        get => _exePath;
        set { _exePath = value; Notify(); Notify(nameof(Status)); }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; Notify(); }
    }

    public string Status => string.IsNullOrEmpty(ExePath) ? "Not found"
        : !File.Exists(ExePath) ? "Missing"
        : SaveIsolation.IsRedirectInstalled(ExePath) ? "Ready" : "No save redirect";

    public void Refresh() => Notify(nameof(Status));

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
