using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;
using Microsoft.Win32;

namespace ApiTester.App;

public partial class BenchWindow : Window
{
    private readonly ApiRequest _request;
    private readonly X509Certificate2? _certificate;
    private readonly TransportOptions _transport;
    private readonly Func<X509Certificate2?, bool>? _trustCert;
    private CancellationTokenSource? _cts;
    private BenchResult? _lastResult;

    public BenchWindow(
        ApiRequest request,
        X509Certificate2? certificate,
        TransportOptions transport,
        Func<X509Certificate2?, bool>? trustCert)
    {
        InitializeComponent();
        _request = request;
        _certificate = certificate;
        _transport = transport with { Retries = 0 };
        _trustCert = trustCert;
        TargetText.Text = $"{request.Method}  {request.Url}";
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
        _ = RunBenchAsync();
    }

    private async Task RunBenchAsync()
    {
        if (!int.TryParse(CountBox.Text.Trim(), out int count) || count <= 0)
        {
            ProgressText.Text = "Requests must be a positive integer.";
            return;
        }
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out int conc) || conc <= 0)
        {
            ProgressText.Text = "Workers must be a positive integer.";
            return;
        }
        conc = Math.Min(conc, count);
        double warmupSec = 0;
        if (!string.IsNullOrWhiteSpace(WarmupBox.Text) &&
            (!double.TryParse(WarmupBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out warmupSec) || warmupSec < 0))
        {
            ProgressText.Text = "Warm-up must be a non-negative number of seconds.";
            return;
        }
        double? rate = null;
        if (!string.IsNullOrWhiteSpace(RateBox.Text))
        {
            if (!double.TryParse(RateBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) || r <= 0)
            {
                ProgressText.Text = "Rate must be a positive requests/second number (or empty for max).";
                return;
            }
            rate = r;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunButton.Content = "Stop";
        ExportCsvButton.IsEnabled = false;
        ResultBox.Text = "Running benchmark…";

        var opts = new BenchOptions(
            count,
            conc,
            Duration: null,
            WarmUp: warmupSec > 0 ? TimeSpan.FromSeconds(warmupSec) : null,
            TargetRps: rate,
            CollectSamples: true);

        var lastUiTick = Environment.TickCount64;
        var progress = new Progress<BenchProgress>(p =>
        {
            long now = Environment.TickCount64;
            if (p.Completed < p.Total && now - lastUiTick < 100) return;
            lastUiTick = now;
            ProgressText.Text = $"Sent {p.Completed}/{p.Total} ({p.Elapsed.TotalSeconds:0.0} s)…";
        });

        try
        {
            var result = await Bench.RunAsync(_request, _certificate, _transport, opts, progress, ct, _trustCert);
            _lastResult = result;
            ResultBox.Text = FormatResult(result);
            ProgressText.Text = $"Done: {result.Succeeded}/{result.Sent} succeeded in {result.Elapsed.TotalSeconds:0.00} s ({result.RequestsPerSecond:0.0} req/s).";
            ExportCsvButton.IsEnabled = result.Samples is { Count: > 0 };
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Benchmark stopped.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Error: " + ex.Message;
            ResultBox.Text = ex.ToString();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunButton.Content = "Run";
        }
    }

    private static string FormatResult(BenchResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Requests:  {r.Sent} sent · {r.Succeeded} succeeded · {r.Failed} failed");
        sb.AppendLine($"Elapsed:   {r.Elapsed.TotalSeconds:0.00} s · {r.RequestsPerSecond:0.0} req/s");
        sb.AppendLine($"Latency:   min {r.MinMs:0.0} ms · p50 {r.P50Ms:0.0} ms · p90 {r.P90Ms:0.0} ms · p99 {r.P99Ms:0.0} ms · max {r.MaxMs:0.0} ms");
        if (r.StatusCounts.Count > 0)
        {
            sb.Append("Statuses:  ");
            sb.AppendLine(string.Join("  ", System.Linq.Enumerable.Select(r.StatusCounts, kv => $"{kv.Key}×{kv.Value}")));
        }
        if (r.ErrorCounts.Count > 0)
        {
            sb.Append("Errors:    ");
            sb.AppendLine(string.Join("  ", System.Linq.Enumerable.Select(r.ErrorCounts, kv => $"{kv.Key}×{kv.Value}")));
        }
        if (r.Samples is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Latency histogram:");
            sb.AppendLine(BenchHistogram.Format(r.Samples));
        }
        return sb.ToString().TrimEnd();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.Samples is not { Count: > 0 } samples) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "bench-samples.csv"
        };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("index,elapsed_ms,status,error,ok");
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            sb.Append(i + 1).Append(',')
              .Append(s.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(s.Error ?? "").Append(',')
              .AppendLine(s.Succeeded ? "true" : "false");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
        ProgressText.Text = $"Exported {samples.Count} samples to {Path.GetFileName(dlg.FileName)}.";
    }
}
