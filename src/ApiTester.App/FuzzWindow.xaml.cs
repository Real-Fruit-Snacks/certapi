using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ApiTester.Core;

namespace ApiTester.App;

public partial class FuzzWindow : Window
{
    public sealed record CertChoice(string Label, X509Certificate2? Cert, string? Thumbprint);

    /// <summary>A discovered endpoint the caller can open in a tab.</summary>
    public sealed record DiscoveredEndpoint(string Method, string BaseUrl, string Path, string? CertThumbprint);

    public sealed class Row
    {
        public string OutcomeLabel { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string Method { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string Ms { get; init; } = "";
        public string Path { get; init; } = "";
        public Brush OutcomeColor { get; init; } = Brushes.Gray;
        public FuzzResult Result { get; init; } = null!;
    }

    // Per-outcome text colours, frozen once and reused so rows don't each allocate a brush. Two
    // sets: bright hues for the dark theme, deeper hues that read on white for the light theme.
    private sealed record OutcomePalette(
        Brush Found, Brush Unauthorized, Brush MethodNotAllowed, Brush Redirect,
        Brush ServerError, Brush Error, Brush Muted);

    private static readonly OutcomePalette DarkOutcomes = new(
        Found: Frozen("#3DDC97"), Unauthorized: Frozen("#FFC247"), MethodNotAllowed: Frozen("#5CB8FF"),
        Redirect: Frozen("#B78CFF"), ServerError: Frozen("#FF6E7A"), Error: Frozen("#E06C75"), Muted: Frozen("#8A8F99"));

    private static readonly OutcomePalette LightOutcomes = new(
        Found: Frozen("#167A4E"), Unauthorized: Frozen("#9A6700"), MethodNotAllowed: Frozen("#0B5FA5"),
        Redirect: Frozen("#6D3BD1"), ServerError: Frozen("#C8324C"), Error: Frozen("#C0392B"), Muted: Frozen("#6B6F78"));

    private static OutcomePalette Outcomes =>
        string.Equals((Application.Current as App)?.CurrentTheme, "Light", System.StringComparison.OrdinalIgnoreCase)
            ? LightOutcomes : DarkOutcomes;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush OutcomeBrush(FuzzOutcome o)
    {
        var pal = Outcomes;
        return o switch
        {
            FuzzOutcome.Found => pal.Found,
            FuzzOutcome.Unauthorized => pal.Unauthorized,
            FuzzOutcome.MethodNotAllowed => pal.MethodNotAllowed,
            FuzzOutcome.Redirect => pal.Redirect,
            FuzzOutcome.ServerError => pal.ServerError,
            FuzzOutcome.Error => pal.Error,
            _ => pal.Muted
        };
    }

    private readonly AppState _state;
    private readonly ApiClient _client;
    private readonly IReadOnlyList<CertChoice> _certs;
    private readonly bool _insecure;
    private readonly int _timeout;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly List<FuzzResult> _all = new();
    private CancellationTokenSource? _cts;

    /// <summary>Set when the user double-clicks a row to open it in a tab; the owner reads it after close.</summary>
    public DiscoveredEndpoint? OpenRequested { get; private set; }

    /// <summary>True if the user saved discovered endpoints into the shared state (owner should refresh).</summary>
    public bool DiscoveredSaved { get; private set; }

    public FuzzWindow(AppState state, ApiClient client, string baseUrl, string? certThumbprint,
        IReadOnlyList<CertChoice> certs, bool insecure, int timeout)
    {
        InitializeComponent();
        _state = state;
        _client = client;
        _certs = certs;
        _insecure = insecure;
        _timeout = timeout;
        BaseUrlBox.Text = baseUrl;
        CertCombo.ItemsSource = certs.Select(c => c.Label).ToList();
        int idx = certs.ToList().FindIndex(c => c.Thumbprint == certThumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
        ResultsGrid.ItemsSource = _rows;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Stop any in-flight discovery run so closing (or double-click-to-open) doesn't leave
        // probes hammering the target after the window is gone.
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Wordlist (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) WordlistPathBox.Text = dlg.FileName;
    }

    private void BuiltIn_Click(object sender, RoutedEventArgs e)
    {
        PasteBox.Text = BuiltInWordlist.Text;
        WordlistPathBox.Text = "";
        StatusText.Text = $"Loaded the built-in starter list ({BuiltInWordlist.Entries.Count} endpoints).";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        string listText = !string.IsNullOrWhiteSpace(PasteBox.Text) ? PasteBox.Text
            : !string.IsNullOrWhiteSpace(WordlistPathBox.Text) && System.IO.File.Exists(WordlistPathBox.Text)
                ? System.IO.File.ReadAllText(WordlistPathBox.Text)
                : BuiltInWordlist.Text;   // nothing supplied: fall back to the built-in starter list
        var entries = EndpointList.Parse(listText);
        if (entries.Count == 0) { StatusText.Text = "Add a wordlist file or paste some endpoints first."; return; }

        var methods = new List<string>();
        if (Mget.IsChecked == true) methods.Add("GET");
        if (Mhead.IsChecked == true) methods.Add("HEAD");
        if (Mpost.IsChecked == true) methods.Add("POST");
        if (Mput.IsChecked == true) methods.Add("PUT");
        if (Mdelete.IsChecked == true) methods.Add("DELETE");
        if (methods.Count == 0) methods.Add("GET");

        int concurrency = int.TryParse(ConcurrencyBox.Text, out var c) && c > 0 ? c : 8;
        var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Cert : null;
        string baseUrl = BaseUrlBox.Text.Trim();

        _rows.Clear();
        _all.Clear();
        SaveCollectionButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        var plan = new FuzzPlan { BaseUrl = baseUrl, Entries = entries, Methods = methods, Concurrency = concurrency };
        var captureLock = new object();
        async Task<ApiResponse> Send(ApiRequest request, CancellationToken ct)
        {
            var headers = request.Headers.ToList();
            lock (captureLock) TokenService.AutoAttach(_state, request.Url, headers, out _);
            var probe = request with { Headers = headers, Timeout = System.TimeSpan.FromSeconds(_timeout) };
            var response = await _client.SendAsync(probe, cert,
                transport: new TransportOptions { IgnoreServerCertificateErrors = _insecure, FollowRedirects = false },
                cancellationToken: ct);
            if (response.Error is null) lock (captureLock) TokenService.Capture(_state, response.FinalUrl(request.Url), response.Body, response.ContentType, response.Headers);
            return response;
        }

        var progress = new Progress<FuzzProgress>(p =>
        {
            AddRow(p.Last);
            StatusText.Text = $"probing {p.Completed}/{p.Total}…";
        });

        try
        {
            var report = await EndpointFuzzer.RunAsync(plan, Send, progress, _cts.Token);
            StatusText.Text = $"{report.Total} probed · {report.Discovered} discovered.";
            SaveCollectionButton.IsEnabled = report.Discovered > 0;
        }
        catch (System.OperationCanceledException) { StatusText.Text = $"Stopped. {_all.Count} probed."; }
        finally { RunButton.IsEnabled = true; StopButton.IsEnabled = false; _cts?.Dispose(); _cts = null; }
    }

    private void AddRow(FuzzResult r)
    {
        _all.Add(r);
        if (HideNoise.IsChecked == true && !FuzzClassifier.IsDiscovery(r.Outcome)) return;
        _rows.Add(ToRow(r));
    }

    private static Row ToRow(FuzzResult r) => new()
    {
        OutcomeLabel = r.Outcome.ToString(),
        StatusText = r.StatusCode?.ToString() ?? "ERR",
        Method = r.Method,
        SizeText = r.Error is null ? Human(r.SizeBytes) : "—",
        Ms = r.Elapsed.TotalMilliseconds.ToString("F0"),
        Path = r.Path,
        OutcomeColor = OutcomeBrush(r.Outcome),
        Result = r
    };

    private static string Human(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:F1} KB" : $"{b / 1048576.0:F1} MB";

    private void HideNoise_Toggle(object sender, RoutedEventArgs e)
    {
        if (_rows is null) return;
        _rows.Clear();
        foreach (var r in _all)
        {
            if (HideNoise.IsChecked == true && !FuzzClassifier.IsDiscovery(r.Outcome)) continue;
            _rows.Add(ToRow(r));
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is Row row)
        {
            var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Thumbprint : null;
            OpenRequested = new DiscoveredEndpoint(row.Method, BaseUrlBox.Text.Trim(), row.Path, cert);
            Close();
        }
    }

    private void SaveCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "Save discovered", "Collection name", "Discovered");
        if (string.IsNullOrWhiteSpace(name)) return;
        var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Thumbprint : null;
        var folder = _state.Collections.FirstOrDefault(c => c.IsFolder && c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        if (folder is null) { folder = new CollectionNode { Name = name, IsFolder = true }; _state.Collections.Add(folder); }
        int added = 0;
        foreach (var r in _all.Where(x => FuzzClassifier.IsDiscovery(x.Outcome)))
        {
            folder.Children.Add(new CollectionNode
            {
                Name = $"{r.Method} {r.Path}", IsFolder = false,
                Request = new RequestModel { Method = r.Method, BaseUrl = BaseUrlBox.Text.Trim(), Path = r.Path, CertThumbprint = cert }
            });
            added++;
        }
        StatusText.Text = $"Saved {added} endpoint(s) to “{name}”. Reopen Collections to see them.";
        DiscoveredSaved = true;
    }
}
