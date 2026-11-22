using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;
using Microsoft.Win32;

namespace ApiTester.App;

/// <summary>The whole browser check for the active request: the preflight and the request a page
/// on an origin would make (redirects followed), an audit of the risky origins, HAR inspection,
/// and the server configuration that would fix it. The same engine as `certapi cors`.</summary>
public partial class CorsWindow : Window
{
    private readonly ApiClient _client;
    private readonly X509Certificate2? _cert;
    private readonly TransportOptions _transport;
    private readonly Func<X509Certificate2?, bool> _trust;
    private readonly string? _body;
    private readonly string? _contentType;
    private readonly int _maxRedirects;
    private CancellationTokenSource? _cts;
    private string? _lastSuggestion;
    private string? _lastJson;

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
        _body = request.Body;
        _contentType = request.ContentType;
        UrlBox.Text = request.Url;
        MethodBox.Text = request.Method.Method;
        OriginBox.Text = lastOrigin
            ?? request.Headers.FirstOrDefault(h => h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).Value
            ?? "http://localhost:3000";

        var initialHeaders = request.Headers
            .Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}: {h.Value}")
            .ToList();
        if (_contentType is not null && _body is not null &&
            !request.Headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            initialHeaders.Add("Content-Type: " + _contentType);
        HeadersBox.Text = string.Join(Environment.NewLine, initialHeaders);

        FrameworkCombo.Items.Add(new ComboBoxItem { Content = "(none)" });
        FrameworkCombo.Items.Add(new ComboBoxItem { Content = "all" });
        foreach (var fw in CorsSuggest.Frameworks)
            FrameworkCombo.Items.Add(new ComboBoxItem { Content = fw });
        FrameworkCombo.SelectedIndex = 0;
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

    private void ActualCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (PreflightOnlyCheck is not null) PreflightOnlyCheck.IsChecked = false;
    }

    private void PreflightOnlyCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (ActualCheck is not null) ActualCheck.IsChecked = false;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ResultBox.Text)) Clipboard.SetText(ResultBox.Text);
    }

    private void CopyFix_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastSuggestion)) Clipboard.SetText(_lastSuggestion);
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastJson)) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export CORS Result as JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "cors-report.json"
        };
        if (dlg.ShowDialog(this) == true)
        {
            File.WriteAllText(dlg.FileName, _lastJson);
            BusyText.Text = "saved";
        }
    }

    private string? Framework => FrameworkCombo.SelectedIndex switch
    {
        <= 0 => null,
        1 => "",
        _ => (FrameworkCombo.SelectedItem as ComboBoxItem)?.Content as string
    };

    private List<KeyValuePair<string, string>> ParseHeaders(out string? problem)
    {
        problem = null;
        var list = new List<KeyValuePair<string, string>>();
        foreach (var raw in HeadersBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                problem = $"Header must be 'Name: Value', got '{line}'.";
                return list;
            }
            string name = line[..colon].Trim();
            if (!name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                list.Add(new(name, line[(colon + 1)..].Trim()));
        }
        return list;
    }

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

    private List<string> Methods()
    {
        var list = MethodBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0) list.Add("GET");
        return list;
    }

    private static bool TargetIsPrivate(Uri target)
    {
        string host = target.Host.Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var ip)) return AddressSpace.IsPrivate(ip);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            return addresses.Length > 0 && addresses.All(AddressSpace.IsPrivate);
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    private Task<ApiResponse> SendAsync(ApiRequest r, CancellationToken ct, bool forceNoCert = false)
    {
        bool preflight = r.Method == HttpMethod.Options && r.Headers.Any(h => h.Key.Equals("Access-Control-Request-Method", StringComparison.OrdinalIgnoreCase));
        bool noCert = forceNoCert || (preflight && NoCertPreflightCheck.IsChecked == true);
        var cert = noCert ? null : _cert;
        return Task.Run(() => _client.SendAsync(r, cert, _transport, _trust, cancellationToken: ct), ct);
    }

    private async Task RunAsync(Func<CancellationToken, Task<(string Text, string? Suggestion, string? Json)>> work)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = AuditButton.IsEnabled = HarButton.IsEnabled = false;
        CopyFixButton.IsEnabled = ExportJsonButton.IsEnabled = false;
        _lastSuggestion = null;
        _lastJson = null;
        BusyText.Text = "checking…";
        try
        {
            var (text, suggestion, json) = await work(_cts.Token);
            ResultBox.Text = text;
            _lastSuggestion = suggestion;
            _lastJson = json;
            CopyFixButton.IsEnabled = !string.IsNullOrEmpty(_lastSuggestion);
            ExportJsonButton.IsEnabled = !string.IsNullOrEmpty(_lastJson);
        }
        catch (OperationCanceledException) { ResultBox.Text = "Cancelled."; }
        catch (Exception ex) { ResultBox.Text = "Error: " + ex.Message; }
        finally
        {
            CheckButton.IsEnabled = AuditButton.IsEnabled = HarButton.IsEnabled = true;
            BusyText.Text = "";
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        var methods = Methods();
        var origins = Origins(out var problem);
        if (origins is null || origins.Count == 0) { ResultBox.Text = problem ?? "Enter the page's origin."; return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) { ResultBox.Text = "Enter an absolute URL."; return; }
        var headers = ParseHeaders(out var headerProblem);
        if (headerProblem is not null) { ResultBox.Text = headerProblem; return; }
        bool credentials = CredentialsCheck.IsChecked == true;
        bool privateNetwork = PrivateNetworkCheck.IsChecked == true;
        bool actual = ActualCheck.IsChecked == true;
        bool preflightOnly = PreflightOnlyCheck.IsChecked == true;
        string? framework = Framework;

        await RunAsync(async ct =>
        {
            if (target.Scheme is "ws" or "wss")
            {
                string wsText = await WebSocketAsync(url, origins, headers, audit: false, ct);
                return (wsText, null, null);
            }
            bool targetPrivate = await Task.Run(() => TargetIsPrivate(target), ct);
            bool? sendActual = preflightOnly ? false : actual ? true : null;
            var sb = new StringBuilder();
            var checks = new List<(CorsRequest Request, CorsProbeResult Result)>();
            foreach (var origin in origins)
                foreach (var method in methods)
                {
                    var request = new CorsRequest(origin, method, headers, credentials) { PrivateNetwork = privateNetwork };
                    var result = await CorsProbe.RunAsync(url, request, _body, _contentType, sendActual,
                        r => SendAsync(r, ct), TimeSpan.FromSeconds(100), _maxRedirects);
                    if (!privateNetwork && targetPrivate && !AddressSpace.IsLocalOrigin(origin))
                        result = result with
                        {
                            Verdict = result.Verdict with
                            {
                                Notes = result.Verdict.Notes.Append(
                                    $"{target.Host} is a private or loopback address and {origin} is not: current Chrome asks the user for permission to reach the local network (earlier versions sent a Private Network Access preflight, which Private network checks)").ToList()
                            }
                        };
                    checks.Add((request, result));
                }

            if (checks.Count == 1)
            {
                sb.AppendLine(CorsProbe.Describe(checks[0].Result, checks[0].Request, url));
            }
            else
            {
                int width = Math.Max(6, origins.Max(o => o.Length));
                sb.AppendLine($"{"origin".PadRight(width)}  {"method",-7} {"preflight",-9} {"status",-6} verdict");
                foreach (var (request, result) in checks)
                {
                    var v = result.Verdict;
                    string pf = !v.PreflightNeeded ? "-" : result.Preflight is { Error: null } p ? p.StatusCode.ToString()! : "failed";
                    string st = result.Response is { Error: null } r ? r.StatusCode.ToString()! : result.NotSent is not null ? "unsent" : "-";
                    string vd = v.Allowed ? (result.Response is null ? "preflight ok" : "allowed") : "BLOCKED: " + v.Problems[0];
                    sb.AppendLine($"{request.Origin.PadRight(width)}  {request.Method,-7} {pf,-9} {st,-6} {vd}");
                }
                foreach (var (request, result) in checks)
                {
                    sb.AppendLine().AppendLine(new string('─', 60));
                    sb.AppendLine(CorsProbe.Describe(result, request, url));
                }
            }

            string? suggestion = framework is not null
                ? CorsSuggest.For(CorsNeeds.From(checks.Select(c => (c.Request, (CorsProbeResult?)c.Result))), framework.Length == 0 ? null : framework)
                : null;
            if (suggestion is not null) sb.AppendLine().AppendLine(suggestion);

            string json = JsonSerializer.Serialize(new
            {
                url,
                results = checks.Select(c => new
                {
                    origin = c.Request.Origin,
                    method = c.Request.Method,
                    credentials = c.Request.Credentials,
                    allowed = c.Result.Verdict.Allowed,
                    preflightNeeded = c.Result.Verdict.PreflightNeeded,
                    preflightReason = c.Result.Verdict.PreflightReason,
                    preflightStatus = c.Result.Preflight?.StatusCode,
                    status = c.Result.Response?.StatusCode,
                    passed = c.Result.Verdict.Passed,
                    problems = c.Result.Verdict.Problems,
                    notes = c.Result.Verdict.Notes,
                    readableHeaders = c.Result.Verdict.Readable,
                    hiddenHeaders = c.Result.Verdict.Hidden
                }),
                suggestion
            }, new JsonSerializerOptions { WriteIndented = true });

            return (sb.ToString().TrimEnd(), suggestion, json);
        });
    }

    private async void Audit_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        var origins = Origins(out var problem);
        if (origins is null) { ResultBox.Text = problem!; return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) { ResultBox.Text = "Enter an absolute URL."; return; }
        var headers = ParseHeaders(out var headerProblem);
        if (headerProblem is not null) { ResultBox.Text = headerProblem; return; }
        bool noCertPreflight = NoCertPreflightCheck.IsChecked == true;
        string? framework = Framework;

        await RunAsync(async ct =>
        {
            if (target.Scheme is "ws" or "wss")
            {
                string wsText = await WebSocketAsync(url, origins, headers, audit: true, ct);
                return (wsText, null, null);
            }
            var trusted = origins.FirstOrDefault(o => o != "null") is { } o ? new Uri(o) : new Uri(target.GetLeftPart(UriPartial.Authority));
            bool includeDev = !await Task.Run(() => TargetIsPrivate(target), ct);
            Func<ApiRequest, Task<ApiResponse>>? sendNoCert = noCertPreflight ? r => SendAsync(r, ct, forceNoCert: true) : null;
            var result = await CorsAudit.RunAsync(url, trusted, headers, r => SendAsync(r, ct), TimeSpan.FromSeconds(100), includeDev, sendNoCert);
            string text = CorsAudit.Describe(result, url);
            string? suggestion = framework is not null
                ? CorsSuggest.For(new CorsNeeds(new[] { result.BaseOrigin }, new[] { "GET" }, Array.Empty<string>(), Array.Empty<string>(), true, false),
                    framework.Length == 0 ? null : framework)
                : null;
            if (suggestion is not null) text += "\n\n" + suggestion;
            string json = JsonSerializer.Serialize(new
            {
                url,
                trustedOrigin = result.BaseOrigin,
                worst = result.Worst?.ToString().ToLowerInvariant(),
                findings = result.Findings.Select(f => new { severity = f.Severity.ToString().ToLowerInvariant(), title = f.Title, detail = f.Detail }),
                probes = result.Probes.Select(p => new { kind = p.Kind, origin = p.Origin, status = p.Status, allowOrigin = p.AllowOrigin, allowCredentials = p.AllowCredentials, allowed = p.Reflected, error = p.Error }),
                suggestion
            }, new JsonSerializerOptions { WriteIndented = true });
            return (text, suggestion, json);
        });
    }

    private async void Har_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Inspect HAR Capture for CORS",
            Filter = "HAR files (*.har;*.json)|*.har;*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        var origins = Origins(out var problem);
        if (origins is null) { ResultBox.Text = problem!; return; }
        bool credentials = CredentialsCheck.IsChecked == true;
        string? framework = Framework;
        string file = dlg.FileName;

        await RunAsync(ct => Task.Run<(string, string?, string?)>(() =>
        {
            var har = HarReader.Parse(File.ReadAllText(file));
            var rows = CorsHar.Check(har, origins.Count == 1 ? origins[0] : null, credentials);
            var sb = new StringBuilder();
            if (rows.Count == 0)
            {
                sb.AppendLine($"No cross-origin requests found in {Path.GetFileName(file)}.");
            }
            else
            {
                foreach (var r in rows)
                {
                    sb.AppendLine($"{(r.Allowed ? "ok     " : "BLOCKED")}  {r.Status,3}  {r.Method,-7} {MarkdownSecrets.RedactUrl(r.Url)}  (from {r.Origin}{(r.Credentials ? ", credentials" : "")}{(r.PreflightReason is null ? "" : r.PreflightRecorded ? ", preflighted" : ", preflight not captured")})");
                    foreach (var p in r.Problems) sb.AppendLine("           " + p);
                    foreach (var n in r.Notes) sb.AppendLine("           note: " + n);
                }
                sb.AppendLine($"{rows.Count(r => r.Allowed)} of {rows.Count} cross-origin request(s) allowed, as recorded");
            }
            string? suggestion = framework is not null && rows.Count > 0
                ? CorsSuggest.For(CorsNeeds.From(rows.Select(r => (
                    new CorsRequest(r.Origin, r.Method, har.Log.Entries.First(en => en.Request.Url == r.Url).Request.Headers
                        .Where(h => !h.Name.StartsWith(':')).Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(), r.Credentials),
                    (CorsProbeResult?)null))), framework.Length == 0 ? null : framework)
                : null;
            if (suggestion is not null) sb.AppendLine().AppendLine(suggestion);
            string json = JsonSerializer.Serialize(new { file, recorded = rows, suggestion }, new JsonSerializerOptions { WriteIndented = true });
            return (sb.ToString().TrimEnd(), suggestion, json);
        }, ct));
    }

    private async Task<string> WebSocketAsync(string url, List<string> origins, List<KeyValuePair<string, string>> headers, bool audit, CancellationToken ct)
    {
        var target = new Uri(url);
        var trusted = origins.FirstOrDefault(o => o != "null") is { } o ? new Uri(o)
            : new Uri((target.Scheme == "wss" ? "https://" : "http://") + target.Authority);
        var list = audit ? CorsAudit.ProbesFor(trusted) : origins.Select(x => ("origin", x)).ToList();
        var probes = await WebSocketOrigins.ProbeAsync(list, async origin =>
        {
            await using var session = new WebSocketSession();
            var sent = headers.Append(new KeyValuePair<string, string>("Origin", origin)).ToList();
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
