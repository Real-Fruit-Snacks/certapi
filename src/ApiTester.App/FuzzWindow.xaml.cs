using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        Found: Frozen("#3DDC97"), Unauthorized: Frozen("#FFC247"), MethodNotAllowed: Frozen("#74B8E8"),
        Redirect: Frozen("#FF9254"), ServerError: Frozen("#FF5C6C"), Error: Frozen("#FF5C6C"), Muted: Frozen("#8A8F99"));

    private static readonly OutcomePalette LightOutcomes = new(
        Found: Frozen("#127247"), Unauthorized: Frozen("#885800"), MethodNotAllowed: Frozen("#164669"),
        Redirect: Frozen("#AD3D0A"), ServerError: Frozen("#B91C2E"), Error: Frozen("#991525"), Muted: Frozen("#535B69"));

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
        // The run still writes the shared workspace (environments, captured tokens) until it has
        // actually stopped; closing before then would let the main window read lists the runner is
        // changing. So the window stays until the run ends, and closes itself then.
        if (_cts is not null)
        {
            e.Cancel = true;
            _closeWhenStopped = true;
            StatusText.Text = "Stopping…";
            return;
        }
        base.OnClosing(e);
    }

    private bool _closeWhenStopped;

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
        string pasteText = PasteBox.Text;
        string wordlistPath = WordlistPathBox.Text;
        string listText;
        try
        {
            listText = !string.IsNullOrWhiteSpace(pasteText) ? pasteText
                : !string.IsNullOrWhiteSpace(wordlistPath) && System.IO.File.Exists(wordlistPath)
                    ? await System.IO.File.ReadAllTextAsync(wordlistPath)
                    : BuiltInWordlist.Text;   // nothing supplied: fall back to the built-in starter list
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // A locked or unreadable file is a message, not a crash of the whole app.
            StatusText.Text = $"Could not read the wordlist: {ex.Message}";
            return;
        }
        var entries = await Task.Run(() => EndpointList.Parse(listText));
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
        var transport = new TransportOptions { IgnoreServerCertificateErrors = _insecure, FollowRedirects = false };
        var timeoutSpan = System.TimeSpan.FromSeconds(_timeout);

        async Task<ApiResponse> Send(ApiRequest request, CancellationToken ct)
        {
            var headers = request.Headers.ToList();
            lock (captureLock) TokenService.AutoAttach(_state, request.Url, headers, out _);
            var probe = request with { Headers = headers, Timeout = timeoutSpan };
            // EndpointFuzzer already runs every worker on the thread pool, so no nested Task.Run.
            var response = await _client.SendAsync(probe, cert, transport: transport, cancellationToken: ct).ConfigureAwait(false);
            if (response.Error is null &&
                TokenService.Detect(response.FinalUrl(request.Url), response.Body, response.ContentType, response.Headers) is { } detected)
            {
                lock (captureLock)
                {
                    _state.SessionTokens.RemoveAll(t => t.Origin == detected.Origin);
                    _state.SessionTokens.Add(detected);
                }
            }
            return response;
        }

        // Batch UI updates on a 50ms timer instead of posting a Dispatcher frame per probe, so
        // high-concurrency discovery keeps the window and Stop button responsive.
        var pending = new ConcurrentQueue<FuzzResult>();
        int latestCompleted = 0;
        int latestTotal = entries.Count * methods.Count;
        var progress = new InlineFuzzProgress(p =>
        {
            pending.Enqueue(p.Last);
            Volatile.Write(ref latestCompleted, p.Completed);
            Volatile.Write(ref latestTotal, p.Total);
        });

        void DrainPending()
        {
            bool any = false;
            while (pending.TryDequeue(out var item))
            {
                AddRow(item);
                any = true;
            }
            if (any)
                StatusText.Text = $"probing {Volatile.Read(ref latestCompleted)}/{Volatile.Read(ref latestTotal)}…";
        }

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => DrainPending(), Dispatcher);
        timer.Start();

        try
        {
            var report = await EndpointFuzzer.RunAsync(plan, Send, progress, _cts.Token);
            timer.Stop();
            DrainPending();
            StatusText.Text = $"{report.Total} probed · {report.Discovered} discovered.";
            SaveCollectionButton.IsEnabled = report.Discovered > 0;
        }
        catch (System.OperationCanceledException)
        {
            timer.Stop();
            DrainPending();
            StatusText.Text = $"Stopped. {_all.Count} probed.";
        }
        finally
        {
            timer.Stop();
            RunButton.IsEnabled = true; StopButton.IsEnabled = false; _cts?.Dispose(); _cts = null;
            if (_closeWhenStopped) _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private sealed class InlineFuzzProgress(Action<FuzzProgress> report) : IProgress<FuzzProgress>
    {
        public void Report(FuzzProgress value) => report(value);
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
