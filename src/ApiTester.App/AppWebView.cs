using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ApiTester.App;

/// <summary>The one WebView2 environment every browser view in the app shares.
/// <para>Left to its default, WebView2 keeps its profile (the "user data folder") in a folder beside
/// the exe. Run from a network share or a redirected folder — common on a VDI — that put the browser
/// process's constant disk traffic on the network, which Microsoft says not to do: a slow share
/// stalls the browser process, and the window waiting on it stops responding. The profile goes to
/// %LOCALAPPDATA%\CertApiTester\WebView2 instead.</para>
/// <para>In a remote session (RDP, Citrix ICA) there is usually no GPU to use. Chromium's GPU
/// process then runs on a software device and is the part that crashes and restarts in a loop on a
/// VDI, so there it starts with --disable-gpu (CERTAPI_WEBVIEW2_GPU=1 keeps the GPU). An
/// administrator's WEBVIEW2_USER_DATA_FOLDER or WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS still win:
/// WebView2 reads them over what is passed here.</para></summary>
internal static class AppWebView
{
    private static Task<CoreWebView2Environment>? _environment;

    public static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertApiTester", "WebView2");

    /// <summary>The shared environment, created on first use. A failed start is not kept, so a
    /// runtime installed meanwhile is found by the next attempt. Called on the UI thread.</summary>
    public static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        var start = _environment ??= CreateAsync();
        try { return await start; }
        catch
        {
            if (ReferenceEquals(_environment, start)) _environment = null;
            throw;
        }
    }

    /// <summary>After the browser process has exited, the next view starts a new one.</summary>
    public static void Forget() => _environment = null;

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        string? arguments = RemoteSession() && Environment.GetEnvironmentVariable("CERTAPI_WEBVIEW2_GPU") != "1"
            ? "--disable-gpu"
            : null;
        var options = new CoreWebView2EnvironmentOptions(additionalBrowserArguments: arguments);
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: UserDataFolder, options: options);
    }

    /// <summary>A message for a browser view whose process failed, or null for a failure WebView2
    /// recovers from by itself (the GPU and utility processes restart on their own).</summary>
    public static string? Describe(CoreWebView2ProcessFailedEventArgs e) => e.ProcessFailedKind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited =>
            "The browser view stopped: WebView2's browser process exited.",
        CoreWebView2ProcessFailedKind.RenderProcessExited =>
            "The page stopped: WebView2's page process exited.",
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive =>
            "The page is not responding (WebView2's page process is stuck).",
        _ => null,
    };

    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>True in an RDP or Citrix session. SM_REMOTESESSION covers most; a session with
    /// RemoteFX reports 0 there, and is told apart by not being the console's ("glass") session.</summary>
    internal static bool RemoteSession()
    {
        try
        {
            if (GetSystemMetrics(SmRemoteSession) != 0) return true;
            string? name = Environment.GetEnvironmentVariable("SESSIONNAME");
            if (name is not null && (name.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase) ||
                                     name.StartsWith("ICA-", StringComparison.OrdinalIgnoreCase)))
                return true;
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\");
            if (key?.GetValue("GlassSessionId") is int glass)
                return glass != Process.GetCurrentProcess().SessionId;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                       System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return false;
    }
}
