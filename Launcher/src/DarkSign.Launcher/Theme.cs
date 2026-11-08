using System.Windows;

namespace DarkSign.Launcher;

/// <summary>Gives windows made in code the Dark Souls menu look from Theme.xaml.</summary>
internal static class Theme
{
    public static Style WindowStyle => (Style)Application.Current.FindResource("MenuWindow");
}
