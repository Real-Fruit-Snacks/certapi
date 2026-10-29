using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>An interactive browser window for capturing a login session. The user drives a real
/// login; each response is observed for bearer tokens, and on Finish the session cookies are read
/// from WebView2's store. Captured cookies/tokens are persisted per origin (feeding the app's and
/// the CLI's automatic auth), and the observed API calls can be saved as a request collection.</summary>
public partial class SessionCaptureWindow : Window
{
    private readonly AppState _state;
    // Tokens seen while browsing are held here and stored only on Finish: Cancel means none of
    // this session is kept, and a token in the live state would be attached to later sends.
    private readonly AppState _pending = new();
    private bool _finishing;
    private readonly SessionCaptureController _controller;
    private readonly List<ObservedCall> _calls = new();
    private readonly HashSet<string> _origins = new();
    private int _tokenCount;

    /// <summary>Deduped observed calls, set on Finish (empty until then).</summary>
    public IReadOnlyList<ObservedCall> CapturedCalls { get; private set; } = new List<ObservedCall>();

    /// <summary>The collection name the user chose to save observed calls into, or null to skip.</summary>
    public string? SaveCollectionName { get; private set; }

    public SessionCaptureWindow(AppState state, X509Certificate2? cert, bool ignoreServerCertErrors, string? startUrl)
    {
        InitializeComponent();
        _state = state;
        _controller = new SessionCaptureController(Browser, cert, ignoreServerCertErrors);
        _controller.CallObserved += OnCall;
        _controller.ResponseBody += OnBodyForToken;
        _controller.Failed += msg => CaptureSummary.Text = $"Last request failed: {msg}";
        _controller.Stopped += msg => CaptureSummary.Text = msg;
        Loaded += async (_, _) =>
        {
            if (!await _controller.InitializeAsync())
            {
                CaptureSummary.Text = "The capture browser needs the Microsoft Edge WebView2 runtime, " +
                                      "which couldn't be started on this machine.";
                FinishButton.IsEnabled = false;
                return;
            }
            // Only auto-navigate to a genuine absolute http(s) URL — never to placeholder text.
            if (Uri.TryCreate(startUrl, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            {
                AddressBox.Text = u.ToString();
                _controller.Navigate(u.ToString());
            }
        };
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosed(System.EventArgs e) { _controller.Dispose(); base.OnClosed(e); }

    private void OnCall(ObservedCall call)
    {
        _calls.Add(call);
        if (TokenService.OriginOf(call.Url) is { } o) _origins.Add(o);
        RefreshSummary();
    }

    // Store any bearer token seen during the session, scoped to its origin — the same capture the
    // app does on normal sends — and count it for the live panel.
    private void OnBodyForToken(byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> headers, string url)
    {
        if (TokenService.Capture(_pending, url, body, contentType, headers) is not null) { _tokenCount++; RefreshSummary(); }
    }

    private void RefreshSummary() =>
        CaptureSummary.Text =
            $"Seen {ObservedCall.Dedup(_calls).Count} API call(s) across {_origins.Count} origin(s); " +
            $"{_tokenCount} bearer token(s) detected. Cookies are read when you press Finish.";

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        // Once: a second click (or Cancel) while the cookies are being read must not finish twice.
        if (_finishing) return;
        _finishing = true;
        FinishButton.IsEnabled = false;
        try
        {
            var cookiesByOrigin = new List<(string Origin, IReadOnlyList<SessionCookie> Cookies)>();
            foreach (var origin in _origins.ToList())
            {
                var cookies = await _controller.ReadCookiesAsync(origin);
                if (cookies.Count > 0) cookiesByOrigin.Add((origin, cookies));
            }
            if (!IsLoaded) return;   // closed while the cookies were read: that was a Cancel

            foreach (var (origin, cookies) in cookiesByOrigin) CookieService.Capture(_state, origin, cookies);
            foreach (var token in _pending.SessionTokens)
            {
                _state.SessionTokens.RemoveAll(t => t.Origin == token.Origin);
                _state.SessionTokens.Add(token);
            }

            CapturedCalls = ObservedCall.Dedup(_calls);
            if (CapturedCalls.Count > 0)
            {
                var name = InputDialog.Show(this, "Save requests",
                    $"Save {CapturedCalls.Count} observed call(s) as requests into a new collection named:", "captured");
                SaveCollectionName = string.IsNullOrWhiteSpace(name) ? null : name;
            }

            _state.Save();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            CaptureSummary.Text = "Could not finish the capture: " + ex.Message;
            _finishing = false;
            FinishButton.IsEnabled = true;
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            var url = NormalizeUrl(AddressBox.Text.Trim());
            AddressBox.Text = url;
            // WebView2 throws on an address it cannot parse ("https://my app"): say so instead.
            try { _controller.Navigate(url); }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or System.Runtime.InteropServices.COMException)
            {
                CaptureSummary.Text = $"That is not an address the browser can open: {ex.Message}";
            }
        }
    }

    /// <summary>Add a scheme when the user omits it. Loopback / bare-IP hosts default to http so a
    /// local mock or dev server works; everything else defaults to https.</summary>
    private static string NormalizeUrl(string input)
    {
        if (input.Contains("://")) return input;
        var host = input.Split('/')[0];
        bool loopback = host.StartsWith("127.", StringComparison.Ordinal) ||
                        host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                        host.StartsWith("[::1]", StringComparison.Ordinal);
        return (loopback ? "http://" : "https://") + input;
    }

    // After the browser process exits the control cannot be used; the buttons then do nothing.
    private void Back_Click(object sender, RoutedEventArgs e) { if (_controller.Ready && Browser.CanGoBack) Browser.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_controller.Ready && Browser.CanGoForward) Browser.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (_controller.Ready) Browser.Reload(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
