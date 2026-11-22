using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;
using Microsoft.Win32;

namespace ApiTester.App;

public partial class DoctorWindow : Window
{
    private readonly X509Certificate2? _clientCert;
    private readonly TransportOptions _baseTransport;
    private CancellationTokenSource? _cts;
    private DoctorReport? _lastReport;

    public DoctorWindow(string initialUrl, X509Certificate2? clientCert, TransportOptions transport, bool autoRun = false)
    {
        InitializeComponent();
        UrlBox.Text = initialUrl;
        _clientCert = clientCert;
        _baseTransport = transport;
        if (autoRun && !string.IsNullOrWhiteSpace(initialUrl))
            Loaded += (_, _) => _ = RunDiagnosisAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        _ = RunDiagnosisAsync();
    }

    private async Task RunDiagnosisAsync()
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SummaryText.Text = "Enter a URL to diagnose.";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunButton.Content = "Cancel";
        SaveCertButton.IsEnabled = false;
        SaveChainButton.IsEnabled = false;
        CopyMdButton.IsEnabled = false;
        SummaryText.Text = $"Diagnosing {url}…";
        ReportBox.Text = "Running stage-by-stage diagnostics…";

        var transport = RevocationCheck.IsChecked == true && _baseTransport.Revocation == RevocationMode.None
            ? _baseTransport with { Revocation = RevocationMode.Online }
            : _baseTransport;

        try
        {
            var report = await Task.Run(async () =>
            {
                var storeCerts = new List<X509Certificate2>();
                try
                {
                    foreach (var info in new CertificateStoreService().ListClientCertificates(includeLocalMachine: true))
                        storeCerts.Add(info.Certificate);
                }
                catch { }
                try
                {
                    return await ConnectionDoctor.RunAsync(url, _clientCert, storeCerts, transport, probe: null, ct);
                }
                finally
                {
                    foreach (var c in storeCerts) c.Dispose();
                }
            }, ct);

            _lastReport = report;
            ReportBox.Text = FormatReport(report);
            SummaryText.Text = report.Ok
                ? $"All stages passed in {report.Stages.Sum(s => s.Elapsed.TotalMilliseconds):F0} ms."
                : $"Stopped at '{report.FirstFailure?.Name}': {report.FirstFailure?.Summary}";
            bool hasPems = report.ServerChainPems is { Count: > 0 };
            SaveCertButton.IsEnabled = hasPems;
            SaveChainButton.IsEnabled = hasPems;
            CopyMdButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "Diagnosis cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Diagnosis error: " + ex.Message;
            ReportBox.Text = ex.ToString();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunButton.Content = "Diagnose";
        }
    }

    private static string FormatReport(DoctorReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"certapi doctor · {MarkdownSecrets.RedactUrl(report.Url)}");
        sb.AppendLine();
        foreach (var stage in report.Stages)
        {
            string mark = stage.Ok ? "ok  " : "FAIL";
            sb.AppendLine($"[{mark}] {stage.Name,-8} {stage.Summary}  ({stage.Elapsed.TotalMilliseconds:F0} ms)");
            foreach (var line in stage.Detail) sb.AppendLine($"         {line}");
            if (stage.Advice is not null) sb.AppendLine($"         -> {stage.Advice}");
        }
        return sb.ToString().TrimEnd();
    }

    private void SaveLeafPem_Click(object sender, RoutedEventArgs e) => SavePem(fullChain: false);
    private void SaveChainPem_Click(object sender, RoutedEventArgs e) => SavePem(fullChain: true);

    private void SavePem(bool fullChain)
    {
        if (_lastReport?.ServerChainPems is not { Count: > 0 } pems) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PEM certificate (*.pem)|*.pem|All files (*.*)|*.*",
            FileName = fullChain ? "server-chain.pem" : "server-leaf.pem"
        };
        if (dlg.ShowDialog(this) != true) return;
        string content = fullChain
            ? string.Join("\n", pems.Select(p => p.TrimEnd())) + "\n"
            : pems[0].TrimEnd() + "\n";
        File.WriteAllText(dlg.FileName, content, new UTF8Encoding(false));
        SummaryText.Text = $"Saved {(fullChain ? $"{pems.Count} chain certificates" : "server leaf certificate")} to {Path.GetFileName(dlg.FileName)}.";
    }

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        try
        {
            Clipboard.SetText(DoctorMarkdown.Render(_lastReport, DateTimeOffset.UtcNow, includeSecrets: false));
            SummaryText.Text = "Copied Markdown investigation note to clipboard.";
        }
        catch (Exception ex) { SummaryText.Text = "Clipboard error: " + ex.Message; }
    }
}
