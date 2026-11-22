using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>
/// A console for the local mock server — the GUI counterpart to <c>certapi mock</c>. Starts/stops a
/// <see cref="MockServer"/> over plain HTTP, HTTPS, or mutual TLS (generating the certificates for
/// the TLS modes), shows the base URL and routes, and logs each request as it arrives.
/// </summary>
public partial class MockServerWindow : Window
{
    private MockServer? _server;
    private X509Certificate2? _serverCert;
    private string? _certDir;
    private HarReplaySource? _replay;
    private string? _replayFile;

    public MockServerWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ = _server?.DisposeAsync();
        _serverCert?.Dispose();
        base.OnClosed(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private MockTlsMode Mode => ModeCombo.SelectedIndex switch
    {
        1 => MockTlsMode.Https,
        2 => MockTlsMode.Mtls,
        _ => MockTlsMode.Http
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 0 or > 65535)
        {
            AppendLine("! enter a port between 0 and 65535 (0 picks a free one).");
            return;
        }

        var mode = Mode;
        StartButton.IsEnabled = false;
        ModeCombo.IsEnabled = false;
        PortBox.IsEnabled = false;
        HarButton.IsEnabled = false;
        try
        {
            if (mode != MockTlsMode.Http)
            {
                AppendLine("— generating TLS certificates…");
                string certDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CertApiTester", "mock-certs");
                _certDir = certDir;
                var generated = await Task.Run(() => MockCertificates.Generate(mode, certDir));
                if (!IsLoaded) { generated.ServerCertificate.Dispose(); return; }
                _serverCert = generated.ServerCertificate;
            }

            _server = MockServer.Start(port, mode, _serverCert, OnRequest, _replay);
        }
        catch (Exception ex)
        {
            AppendLine("! could not start: " + ex.Message);
            _serverCert?.Dispose();
            _serverCert = null;
            StartButton.IsEnabled = true;
            ModeCombo.IsEnabled = true;
            PortBox.IsEnabled = true;
            HarButton.IsEnabled = true;
            return;
        }

        UrlBox.Text = _server.BaseUrl;
        UrlBox.SetResourceReference(ForegroundProperty, "Signal"); // mint = live, muted = stopped
        CopyUrlButton.IsEnabled = true;
        StopButton.IsEnabled = true;

        if (mode != MockTlsMode.Http)
        {
            CertText.Visibility = Visibility.Visible;
            OpenCertsButton.Visibility = Visibility.Visible;
            CertText.Text = mode == MockTlsMode.Mtls
                ? $"certs in {_certDir} — present mock-client.pfx; trust mock-ca.cer or ignore server-cert errors."
                : $"certs in {_certDir} — trust mock-ca.cer or ignore server-cert errors.";
        }

        AppendLine($"— listening on {_server.BaseUrl} ({mode})");
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var server = _server;
        _server = null;
        if (server is not null) await server.DisposeAsync();
        _serverCert?.Dispose();
        _serverCert = null;

        UrlBox.Text = "stopped — pick a mode and press Start";
        UrlBox.SetResourceReference(ForegroundProperty, "Text.Muted");
        CopyUrlButton.IsEnabled = false;
        OpenCertsButton.Visibility = Visibility.Collapsed;
        CertText.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ModeCombo.IsEnabled = true;
        PortBox.IsEnabled = true;
        HarButton.IsEnabled = true;
        AppendLine("— stopped");
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null)
        {
            try { Clipboard.SetText(_server.BaseUrl); AppendLine("— copied the base URL"); }
            catch { /* clipboard can transiently fail */ }
        }
    }

    private void OpenCerts_Click(object sender, RoutedEventArgs e)
    {
        if (_certDir is not null && Directory.Exists(_certDir))
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_certDir}\"") { UseShellExecute = true }); }
            catch { /* best effort */ }
    }

    private async void Har_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Replay a HAR file",
            Filter = "HAR file (*.har)|*.har|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        string filePath = dialog.FileName;
        HarButton.IsEnabled = false;
        try
        {
            var replay = await Task.Run(() => new HarReplaySource(HarReader.Parse(File.ReadAllText(filePath))));
            if (!IsLoaded) return;
            _replay = replay;
            _replayFile = Path.GetFileName(filePath);
            RoutesText.Text = $"replaying {_replay.Count} recorded responses from {_replayFile}";
            AppendLine($"— replaying {_replay.Count} recorded responses from {_replayFile}");
        }
        catch (Exception ex)
        {
            if (IsLoaded) AppendLine("! could not read that HAR: " + ex.Message);
        }
        finally
        {
            if (IsLoaded && _server is null) HarButton.IsEnabled = true;
        }
    }

    // MockServer invokes this on a background thread; marshal to the UI to append.
    private void OnRequest(MockRequestLog r) =>
        Dispatcher.BeginInvoke(() =>
        {
            string who = r.ClientCertSubject is { } s ? $"  ({s})" : "";
            string matched = r.Replay switch { "exact" => "  exact-match", "path" => "  path-match", "miss" => "  miss", _ => "" };
            AppendLine($"{DateTime.Now:HH:mm:ss}  {r.Method,-6} {r.Path} → {r.Status}{who}{matched}");
        });

    private BoundedTranscript? _log;

    private void AppendLine(string text) => (_log ??= new BoundedTranscript(LogBox)).Append(text);
}
