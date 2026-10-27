using System;
using System.Windows;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>
/// Interaction logic for App.xaml. Owns the runtime theme: the base (dark) palette lives in
/// TerminalWorkbench.xaml and every palette reference in XAML is a <c>DynamicResource</c>, so a
/// light theme is just a small overlay dictionary merged on top — adding or removing it repaints
/// the entire live UI without a restart.
/// </summary>
public partial class App : Application
{
    private const string LightPaletteUri = "Themes/Palette.Light.xaml";
    private ResourceDictionary? _lightOverlay;

    /// <summary>The theme currently applied: "Dark" or "Light".</summary>
    public string CurrentTheme { get; private set; } = "Dark";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // What an update left behind (the previous exe, renamed because it was running) goes now.
        ReleaseUpdater.CleanUpPrevious(Environment.ProcessPath);
        ApplyTheme(AppState.Load().Theme);
    }

    /// <summary>Apply a theme by name. "Light" (case-insensitive) merges the light palette overlay
    /// over the base dark palette; anything else removes the overlay and reverts to dark.</summary>
    public void ApplyTheme(string? theme)
    {
        bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        CurrentTheme = light ? "Light" : "Dark";

        if (light)
        {
            if (_lightOverlay is null)
            {
                _lightOverlay = new ResourceDictionary { Source = new Uri(LightPaletteUri, UriKind.Relative) };
                Resources.MergedDictionaries.Add(_lightOverlay);
            }
        }
        else if (_lightOverlay is not null)
        {
            Resources.MergedDictionaries.Remove(_lightOverlay);
            _lightOverlay = null;
        }
    }
}
