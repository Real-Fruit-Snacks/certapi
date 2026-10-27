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
        ApplyTheme(SavedTheme());
        DispatcherUnhandledException += OnUnhandledUiException;
    }

    /// <summary>A bug in one handler must not take the workspace down with it: the error is shown,
    /// the workspace is saved, and the app keeps running. Without this, any exception escaping a
    /// UI event ends the process before the window's own save on close ever runs.</summary>
    private int _unexpectedErrors;

    private void OnUnhandledUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Only once the main window holds the workspace: an error while it is still being built
        // must not save its empty lists over the file — and a window that never appeared would
        // leave the process running headless. Then the crash is the honest outcome. The same goes
        // for an error that keeps recurring (a layout or timer loop of message boxes).
        if (MainWindow is not MainWindow { CanSaveAfterError: true } || ++_unexpectedErrors > 5) return;
        e.Handled = true;
        try { (MainWindow as MainWindow)?.SaveAfterUnexpectedError(); } catch { /* nothing more to try */ }
        try
        {
            MessageBox.Show(MainWindow, $"Something went wrong, and the workspace was saved.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
                "certapi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* no window to show it in */ }
    }

    /// <summary>Apply a theme by name. "Light" (case-insensitive) merges the light palette overlay
    /// over the base dark palette; anything else removes the overlay and reverts to dark.</summary>
    /// <summary>The saved theme, read without side effects: an unreadable workspace is reported (and
    /// kept) once, by the main window's own load, not here.</summary>
    private static string? SavedTheme()
    {
        try { return System.IO.File.Exists(AppState.DefaultPath) ? AppState.LoadFrom(AppState.DefaultPath).Theme : null; }
        catch (Exception) { return null; }
    }

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
