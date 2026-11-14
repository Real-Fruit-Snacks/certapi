using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>The whole browser check for the active request: the preflight and the request a page
/// on an origin would make (redirects followed), an audit of the risky origins, and the server
/// configuration that would fix it. The same engine as `certapi cors`.</summary>
public partial class CorsWindow : Window
{
    private readonly ApiClient _client;
    private readonly X509Certificate2? _cert;
    private readonly TransportOptions _transport;
    private readonly Func<X509Certificate2?, bool> _trust;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;
    private readonly string? _body;
    private readonly string? _contentType;
    private readonly int _maxRedirects;
    private CancellationTokenSource? _cts;

    /// <summary>The page origin last used, so the next window opens with it.</summary>
    public string Origin => OriginBox.Text.Trim();

    public CorsWindow(ApiClient client, ApiRequest request, X509Certificate2? cert, TransportOptions transport,
        Func<X509Certificate2?, bool> trust, string? lastOrigin)
    {
        InitializeComponent();
        _client = client;
        _cert = cert;
        _maxRedirects = transport.FollowRedirects ? transport.MaxRedirects : 0;
        _transport = transport with { FollowRedirects = false };
        _trust = trust;
        _headers = request.Headers.Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).ToList();
        _body = request.Body;
        _contentType = request.ContentType;
        UrlBox.Text = request.Url;
        MethodBox.Text = request.Method.Method;
        OriginBox.Text = lastOrigin
            ?? request.Headers.FirstOrDefault(h => h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).Value
            ?? "http://localhost:3000";
        var named = _headers.Select(h => h.Key).ToList();
        if (_contentType is not null && _body is not null) named.Add("Content-Type: " + _contentType);
        HeadersNote.Text = named.Count == 0
            ? "The request's own headers and body are checked with it (it has none)."
            : "Checked with the request's own headers: " + string.Join(", ", named);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private string? Framework => FrameworkCombo.SelectedIndex switch
    {
        <= 0 => null,
        1 => "",
        _ => (FrameworkCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
    };

    private List<string>? Origins(out string? problem)
    {
        problem = null;
        var list = new List<string>();
        foreach (var raw in OriginBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "null") { list.Add(raw); continue; }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                problem = $"'{raw}' is not an origin: write scheme://host[:port], e.g. https://app.example.com";
                return null;
            }
            list.Add(u.GetLeftPart(UriPartial.Authority));
        }
        return list;
    }

    private Task<ApiResponse> SendAsync(ApiRequest r, CancellationToken ct)
    {
        bool preflight = r.Method == HttpMethod.Options && r.Headers.Any(h => h.Key.Equals("Access-Control-Request-Method", StringComparison.OrdinalIgnoreCase));
        var cert = preflight && NoCertPreflightCheck.IsChecked == true ? null : _cert;
        return Task.Run(() => _client.SendAsync(r, cert, _transport, _trust, cancellationToken: ct), ct);
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> work)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = AuditButton.IsEnabled = false;
        BusyText.Text = "checking…";
        try { ResultBox.Text = await work(_cts.Token); }
        catch (OperationCanceledException) { ResultBox.Text = "Cancelled."; }
        catch (Exception ex) { ResultBox.Text = "Error: " + ex.Message; }
        finally
        {
            CheckButton.IsEnabled = AuditButton.IsEnabled = true;
            BusyText.Text = "";
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        string method = MethodBox.Text.Trim().ToUpperInvariant();
        var origins = Origins(out var problem);
        if (origins is null || origins.Count == 0) { ResultBox.Text = problem ?? "Enter the page's origin."; return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) { ResultBox.Text = "Enter an absolute URL."; return; }
        if (method.Length == 0) method = "GET";
        bool credentials = CredentialsCheck.IsChecked == true;
        bool privateNetwork = PrivateNetworkCheck.IsChecked == true;
        bool actual = ActualCheck.IsChecked == true;
        string? framework = Framework;

        await RunAsync(async ct =>
        {
            if (target.Scheme is "ws" or "wss") return await WebSocketAsync(url, origins, audit: false, ct);
            var headers = _headers.ToList();
            if (_body is not null && _contentType is not null) headers.Add(new("Content-Type", _contentType));
            var sb = new System.Text.StringBuilder();
            var checks = new List<(CorsRequest, CorsProbeResult?)>();
            foreach (var origin in origins)
            {
                var request = new CorsRequest(origin, method, headers, credentials) { PrivateNetwork = privateNetwork };
                var result = await CorsProbe.RunAsync(url, request, _body, _contentType, actual ? true : null,
                    r => SendAsync(r, ct), TimeSpan.FromSeconds(100), _maxRedirects);
                checks.Add((request, result));
                if (sb.Length > 0) sb.AppendLine().AppendLine(new string('─', 60));
                sb.AppendLine(CorsProbe.Describe(result, request, url));
            }
            if (framework is not null)
                sb.AppendLine().AppendLine(CorsSuggest.For(CorsNeeds.From(checks), framework.Length == 0 ? null : framework));
            return sb.ToString().TrimEnd();
        });
    }

    private async void Audit_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        var origins = Origins(out var problem);
        if (origins is null) { ResultBox.Text = problem!; return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) { ResultBox.Text = "Enter an absolute URL."; return; }
        string? framework = Framework;
        await RunAsync(async ct =>
        {
            if (target.Scheme is "ws" or "wss") return await WebSocketAsync(url, origins, audit: true, ct);
            var trusted = origins.FirstOrDefault(o => o != "null") is { } o ? new Uri(o) : new Uri(target.GetLeftPart(UriPartial.Authority));
            var result = await CorsAudit.RunAsync(url, trusted, _headers, r => SendAsync(r, ct), TimeSpan.FromSeconds(100));
            string text = CorsAudit.Describe(result, url);
            if (framework is not null)
                text += "\n\n" + CorsSuggest.For(new CorsNeeds(new[] { result.BaseOrigin }, new[] { "GET" }, Array.Empty<string>(), Array.Empty<string>(), true, false),
                    framework.Length == 0 ? null : framework);
            return text;
        });
    }

    private async Task<string> WebSocketAsync(string url, List<string> origins, bool audit, CancellationToken ct)
    {
        var target = new Uri(url);
        var trusted = origins.FirstOrDefault(o => o != "null") is { } o ? new Uri(o)
            : new Uri((target.Scheme == "wss" ? "https://" : "http://") + target.Authority);
        var list = audit ? CorsAudit.ProbesFor(trusted) : origins.Select(x => ("origin", x)).ToList();
        var probes = await WebSocketOrigins.ProbeAsync(list, async origin =>
        {
            await using var session = new WebSocketSession();
            var sent = _headers.Append(new KeyValuePair<string, string>("Origin", origin)).ToList();
            try
            {
                await Task.Run(() => session.ConnectAsync(url, _cert, sent, _transport.IgnoreServerCertificateErrors, _transport, _trust, ct), ct);
                await session.CloseAsync(CancellationToken.None);
                return (true, (int?)101, (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (false, session.HandshakeStatus, ex.Message);
            }
        });
        return WebSocketOrigins.Describe(probes, audit ? WebSocketOrigins.Grade(probes) : null, url);
    }
}
