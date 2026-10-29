using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ApiTester.Core;

/// <summary>How this machine is configured to reach the internet, as Internet Options records it.
/// Null strings mean the setting is absent, which is not the same as empty.</summary>
public sealed record ProxySettings(
    bool AutoDetect, string? AutoConfigUrl, bool ProxyEnabled, string? ProxyServer, string? ProxyOverride)
{
    /// <summary>True when nothing at all is configured — the answer for any URL is DIRECT, and
    /// saying so plainly saves a reader hunting for a proxy that was never there.</summary>
    public bool IsEmpty =>
        !AutoDetect && string.IsNullOrWhiteSpace(AutoConfigUrl) &&
        (!ProxyEnabled || string.IsNullOrWhiteSpace(ProxyServer));
}

/// <summary>What the machine's own proxy engine answers for one URL, and what .NET would do with
/// the same URL. Two sources on purpose: they normally agree, and when they do not, that
/// disagreement IS the finding.</summary>
public sealed record ProxyDecision(
    string Url, string? WinHttpProxy, string? WinHttpError, string? DotNetProxy)
{
    /// <summary>True when both engines were consulted and named different destinations.</summary>
    public bool Disagrees
    {
        get
        {
            if (WinHttpError is not null) return false;
            // WinHTTP may answer with a list ("a:8080;b:8080" — PAC failover order) and without a
            // scheme; .NET answers with one absolute URL. They agree when .NET's choice is any of
            // WinHTTP's candidates, each read as host:port.
            var winhttp = Candidates(WinHttpProxy);
            string dotnet = Normalize(DotNetProxy);
            return !winhttp.Contains(dotnet, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<string> Candidates(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return new[] { "DIRECT" };
        return proxy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Normalize).ToList();
    }

    /// <summary>host:port, lower-case, however it was spelled: "http://proxy.corp:8080/",
    /// "proxy.corp:8080", "PROXY proxy.corp:8080" and "https=proxy.corp:8080" all read the same.</summary>
    internal static string Normalize(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return "DIRECT";
        string p = proxy.Trim();
        if (p.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return "DIRECT";
        if (p.StartsWith("PROXY ", StringComparison.OrdinalIgnoreCase)) p = p[6..].Trim();
        int eq = p.IndexOf('=');
        if (eq > 0 && !p.Contains("://", StringComparison.Ordinal)) p = p[(eq + 1)..];
        if (!p.Contains("://", StringComparison.Ordinal)) p = "http://" + p;
        return Uri.TryCreate(p, UriKind.Absolute, out var uri)
            ? $"{uri.Host.ToLowerInvariant()}:{uri.Port}"
            : proxy.Trim().ToLowerInvariant();
    }
}

/// <summary>Reads the machine's proxy configuration and asks Windows itself which proxy applies to
/// a given URL. The per-URL answer comes from WinHTTP's own PAC engine
/// (<c>WinHttpGetProxyForUrl</c>), not a re-implementation: a proxy auto-config script is
/// JavaScript, and the only honest answer to "which proxy will I actually get" is the one the
/// operating system computes. Everything is behind seams so the rendering and the
/// disagreement logic are testable without a PAC server.</summary>
public static class ProxyIntrospection
{
    /// <summary>Internet Options, as the current user has them. Empty settings on a non-Windows
    /// platform rather than throwing — the caller reports what it got.</summary>
    public static ProxySettings ReadSettings()
    {
        if (!OperatingSystem.IsWindows()) return new ProxySettings(false, null, false, null, null);
        return ReadSettingsWindows();
    }

    [SupportedOSPlatform("windows")]
    private static ProxySettings ReadSettingsWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return new ProxySettings(false, null, false, null, null);

            // AutoDetect is bit 3 of the first byte of DefaultConnectionSettings — the same flag
            // the "Automatically detect settings" checkbox sets.
            bool autoDetect = false;
            if (key.GetValue("DefaultConnectionSettings") is byte[] { Length: >= 9 } blob)
                autoDetect = (blob[8] & 0x08) != 0;

            return new ProxySettings(
                autoDetect,
                key.GetValue("AutoConfigURL") as string,
                key.GetValue("ProxyEnable") is int enable && enable != 0,
                key.GetValue("ProxyServer") as string,
                key.GetValue("ProxyOverride") as string);
        }
        catch (Exception)
        {
            // A registry we cannot read contributes nothing; it is never worth failing the command.
            return new ProxySettings(false, null, false, null, null);
        }
    }

    /// <summary>The settings as .NET's system proxy reads them: <c>WinHttpGetIEProxyConfigForCurrentUser</c>,
    /// which also sees what <see cref="ReadSettings"/> cannot (a policy that makes the settings
    /// per machine, the settings of the connection in use). <see cref="ReadSettings"/> when that
    /// call fails or the platform is not Windows.</summary>
    public static ProxySettings ReadEffectiveSettings()
    {
        if (!OperatingSystem.IsWindows()) return ReadSettings();
        var config = default(WinHttpCurrentUserIeProxyConfig);
        try
        {
            if (!WinHttpGetIEProxyConfigForCurrentUser(ref config)) return ReadSettings();
            string? proxy = config.lpszProxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(config.lpszProxy);
            return new ProxySettings(
                config.fAutoDetect,
                config.lpszAutoConfigUrl == IntPtr.Zero ? null : Marshal.PtrToStringUni(config.lpszAutoConfigUrl),
                !string.IsNullOrWhiteSpace(proxy),
                proxy,
                config.lpszProxyBypass == IntPtr.Zero ? null : Marshal.PtrToStringUni(config.lpszProxyBypass));
        }
        catch (Exception) { return ReadSettings(); }
        finally
        {
            if (config.lpszAutoConfigUrl != IntPtr.Zero) GlobalFree(config.lpszAutoConfigUrl);
            if (config.lpszProxy != IntPtr.Zero) GlobalFree(config.lpszProxy);
            if (config.lpszProxyBypass != IntPtr.Zero) GlobalFree(config.lpszProxyBypass);
        }
    }

    /// <summary>The proxy Windows itself would use for <paramref name="url"/>, evaluated by WinHTTP
    /// with the machine's configured PAC/WPAD. Returns null with no error for DIRECT.
    /// <paramref name="winHttp"/> exists so tests can supply an engine without a PAC server.</summary>
    public static ProxyDecision Decide(
        string url, ProxySettings settings,
        Func<string, ProxySettings, (string? Proxy, string? Error)>? winHttp = null,
        Func<string, string?>? dotNet = null)
    {
        var engine = winHttp ?? WinHttpProxyForUrl;
        var (proxy, error) = engine(url, settings);

        string? net;
        if (dotNet is not null) net = dotNet(url);
        else
        {
            try
            {
                net = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? System.Net.Http.HttpClient.DefaultProxy.GetProxy(uri)?.ToString()
                    : null;
            }
            catch (Exception ex) { net = "(could not be evaluated: " + ex.Message + ")"; }
        }

        return new ProxyDecision(url, proxy, error, net);
    }

    // ---------------------------------------------------------------- WinHTTP

    private const int WinHttpAccessTypeNoProxy = 1;
    private const uint WinHttpAutoDetectTypeDhcp = 0x00000001;
    private const uint WinHttpAutoDetectTypeDnsA = 0x00000002;
    private const uint WinHttpAutoproxyAutoDetect = 0x00000001;
    private const uint WinHttpAutoproxyConfigUrl = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinHttpAutoproxyOptions
    {
        public uint dwFlags;
        public uint dwAutoDetectFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszAutoConfigUrl;
        public IntPtr lpvReserved;
        public uint dwReserved;
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinHttpProxyInfo
    {
        public uint dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinHttpCurrentUserIeProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoDetect;
        public IntPtr lpszAutoConfigUrl;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WinHttpCurrentUserIeProxyConfig pProxyConfig);

    private const int ErrorWinHttpLoginFailure = 12015;

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(string? pszAgentW, int dwAccessType,
        string? pszProxyW, string? pszProxyBypassW, int dwFlags);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(IntPtr hSession, string lpcwszUrl,
        ref WinHttpAutoproxyOptions pAutoProxyOptions, out WinHttpProxyInfo pProxyInfo);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(IntPtr hInternet);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>Evaluate one URL against a PAC script the caller names — a URL, or a local file —
    /// through WinHTTP's own engine, exactly as the system would run it. The `--pac` flag: test a
    /// script before IT rolls it out, or use one on a machine whose Internet Options name none.</summary>
    public static (string? Proxy, string? Error) EvaluatePac(string pacUrlOrPath, string targetUrl)
    {
        string pacUrl = pacUrlOrPath;
        if (!pacUrlOrPath.Contains("://", StringComparison.Ordinal))
        {
            string full = Path.GetFullPath(pacUrlOrPath);
            if (!File.Exists(full)) return (null, $"--pac: file not found: {pacUrlOrPath}");
            pacUrl = new Uri(full).AbsoluteUri;
        }
        return WinHttpProxyForUrl(targetUrl, new ProxySettings(false, pacUrl, false, null, null));
    }

    /// <summary>The manual proxy Internet Options names (ProxyEnable + ProxyServer), unless the URL
    /// is bypassed by ProxyOverride — what Windows uses when nothing automatic is configured.
    /// Null for DIRECT.</summary>
    internal static string? StaticProxyFor(string url, ProxySettings settings)
    {
        if (!settings.ProxyEnabled || string.IsNullOrWhiteSpace(settings.ProxyServer)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        // ProxyOverride: "a.corp;*.internal;<local>" — wildcards, and <local> for dotless hosts.
        foreach (var raw in (settings.ProxyOverride ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.Host.Contains('.')) return null;
                continue;
            }
            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(raw).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(uri.Host, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(uri.Authority, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return null;
        }

        // ProxyServer: "host:port", or per scheme "http=a:8080;https=b:8443".
        string server = settings.ProxyServer.Trim();
        if (server.Contains('='))
        {
            string? match = null;
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part[..eq].Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)) { match = part[(eq + 1)..]; break; }
            }
            return string.IsNullOrWhiteSpace(match) ? null : match;
        }
        return server;
    }

    /// <summary>What Windows answers for <paramref name="url"/> with these settings: the proxy list
    /// WinHTTP gives (or the manual proxy), null for DIRECT, or the error.
    /// <paramref name="autoLogonWhenChallenged"/> asks again, signed in, when fetching the script
    /// asked for a sign-in — what .NET's own proxy does.</summary>
    internal static (string? Proxy, string? Error) Answer(string url, ProxySettings settings, bool autoLogonWhenChallenged = false) =>
        WinHttpProxyForUrl(url, settings, autoLogonWhenChallenged);

    /// <summary>Ask WinHTTP to evaluate this URL against the configured PAC/WPAD. Never throws:
    /// any failure comes back as the error string, because a diagnostic must not be what breaks.</summary>
    private static (string? Proxy, string? Error) WinHttpProxyForUrl(string url, ProxySettings settings) =>
        WinHttpProxyForUrl(url, settings, autoLogonWhenChallenged: false);

    private static (string? Proxy, string? Error) WinHttpProxyForUrl(string url, ProxySettings settings, bool autoLogonWhenChallenged)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "WinHTTP is only available on Windows.");
        if (!settings.AutoDetect && string.IsNullOrWhiteSpace(settings.AutoConfigUrl))
            return (StaticProxyFor(url, settings), null);   // no PAC/WPAD: the manual proxy, if any, or DIRECT

        IntPtr session = IntPtr.Zero;
        try
        {
            session = WinHttpOpen("certapi-proxy", WinHttpAccessTypeNoProxy, null, null, 0);
            if (session == IntPtr.Zero)
                return (null, $"WinHttpOpen failed ({Marshal.GetLastWin32Error()})");

            var options = new WinHttpAutoproxyOptions
            {
                // No ALLOW_STATIC / ALLOW_CM: they belong to WinHttpGetProxyForUrlEx, and the
                // values once used here (0x4, 0x8) are HOST_KEEPCASE / HOST_LOWERCASE instead.
                dwFlags = 0,
                // fAutoLogonIfChallenged starts false: for a report, a PAC fetch that demands
                // credentials should be reported, not silently authenticated with the user's
                // identity. The gateway's own question asks again signed in, as .NET does.
                fAutoLogonIfChallenged = false
            };
            if (settings.AutoDetect)
            {
                options.dwFlags |= WinHttpAutoproxyAutoDetect;
                options.dwAutoDetectFlags = WinHttpAutoDetectTypeDhcp | WinHttpAutoDetectTypeDnsA;
            }
            if (!string.IsNullOrWhiteSpace(settings.AutoConfigUrl))
            {
                options.dwFlags |= WinHttpAutoproxyConfigUrl;
                options.lpszAutoConfigUrl = settings.AutoConfigUrl;
            }

            bool answered = WinHttpGetProxyForUrl(session, url, ref options, out var info);
            int error = answered ? 0 : Marshal.GetLastWin32Error();
            if (!answered && error == ErrorWinHttpLoginFailure && autoLogonWhenChallenged)
            {
                options.fAutoLogonIfChallenged = true;
                answered = WinHttpGetProxyForUrl(session, url, ref options, out info);
                error = answered ? 0 : Marshal.GetLastWin32Error();
            }
            if (!answered)
            {
                return (null, error switch
                {
                    12180 => "WPAD found no proxy-configuration script on this network (WINHTTP_ERROR_AUTODETECTION_FAILED).",
                    12167 => "the proxy-configuration script could not be downloaded (WINHTTP_ERROR_UNABLE_TO_DOWNLOAD_SCRIPT).",
                    12166 => "the proxy-configuration script has an error in it (WINHTTP_ERROR_BAD_AUTO_PROXY_SCRIPT).",
                    _ => $"WinHttpGetProxyForUrl failed ({error})"
                });
            }

            try
            {
                string? proxy = info.lpszProxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.lpszProxy);
                return (string.IsNullOrWhiteSpace(proxy) ? null : proxy, null);
            }
            finally
            {
                if (info.lpszProxy != IntPtr.Zero) GlobalFree(info.lpszProxy);
                if (info.lpszProxyBypass != IntPtr.Zero) GlobalFree(info.lpszProxyBypass);
            }
        }
        catch (Exception ex) { return (null, ex.Message); }
        finally { if (session != IntPtr.Zero) WinHttpCloseHandle(session); }
    }
}
