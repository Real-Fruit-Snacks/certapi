using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ApiTester.App;

/// <summary>
/// Colours the OS-drawn window caption (title bar) and border via the Desktop Window Manager
/// so the native chrome matches the active RFS palette (Nightshift / Dayshift). Reads the current theme
/// from <see cref="App.CurrentTheme"/>, so every window follows a light/dark toggle. No-ops on
/// Windows versions that don't support these attributes (pre-Windows 11), leaving the default.
/// </summary>
internal static class NativeTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // COLORREF is 0x00BBGGRR.
    private static int Rgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    private static bool CurrentThemeIsLight =>
        string.Equals((Application.Current as App)?.CurrentTheme, "Light", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apply the caption/border colours for the app's current theme to <paramref name="window"/>.</summary>
    public static void ApplyTitleBar(Window window) => ApplyTitleBar(window, CurrentThemeIsLight);

    /// <summary>Apply the caption/border colours for a specific theme. Call again after a live toggle.</summary>
    public static void ApplyTitleBar(Window window, bool light)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            int caption = light ? Rgb(0xff, 0xff, 0xff)  // Bg.Panel
                                : Rgb(0x13, 0x15, 0x1a);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

            int text = light ? Rgb(0x1a, 0x1b, 0x1e)      // Text.Normal
                             : Rgb(0xe8, 0xe6, 0xe1);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));

            int border = light ? Rgb(0xd6, 0xd3, 0xcb)    // Border
                               : Rgb(0x26, 0x2a, 0x33);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch
        {
            // Attribute unsupported on this OS build — keep the default caption.
        }
    }
}
