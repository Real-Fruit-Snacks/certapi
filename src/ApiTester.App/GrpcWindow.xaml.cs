using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;
using ApiTester.Grpc;
using Microsoft.Win32;

namespace ApiTester.App;

public partial class GrpcWindow : Window
{
    private readonly X509Certificate2? _cert;
    private readonly TransportOptions _transport;
    private readonly Func<X509Certificate2?, bool>? _trustCert;
    private GrpcDescriptorSet? _protoset;
    private readonly Dictionary<string, GrpcMethodInfo> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public GrpcWindow(string initialAddress, X509Certificate2? cert, TransportOptions transport, Func<X509Certificate2?, bool>? trustCert)
    {
        InitializeComponent();
        AddressBox.Text = initialAddress;
        _cert = cert;
        _transport = transport;
        _trustCert = trustCert;
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

    private void Protoset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Protobuf descriptor set (*.protoset;*.pb;*.bin)|*.protoset;*.pb;*.bin|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _protoset = GrpcDescriptorSet.Load(dlg.FileName);
            PopulateMethods(_protoset.Services);
            StatusLine.Text = $"Loaded {Path.GetFileName(dlg.FileName)}: {_discovered.Count} method(s).";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Could not load protoset: " + ex.Message;
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        string rawAddr = AddressBox.Text.Trim();
        if (!Uri.TryCreate(rawAddr, UriKind.Absolute, out var uri) || (uri.Scheme is not ("http" or "https")))
        {
            StatusLine.Text = "Enter a valid http:// or https:// gRPC address.";
            return;
        }
        DiscoverButton.IsEnabled = false;
        StatusLine.Text = $"Discovering services on {uri}…";
        try
        {
            await using var caller = new GrpcCaller(uri, _cert, _transport, _trustCert, _protoset);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var services = await caller.DiscoverAsync(cts.Token);
            PopulateMethods(services);
            StatusLine.Text = $"Discovered {services.Count} service(s), {_discovered.Count} method(s).";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Discovery failed: " + ex.Message;
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }

    private void PopulateMethods(IReadOnlyList<GrpcServiceInfo> services)
    {
        _discovered.Clear();
        var items = new List<string>();
        foreach (var s in services)
        {
            foreach (var m in s.Methods)
            {
                string key = $"{s.Name}/{m.Name}";
                _discovered[key] = m;
                items.Add(key);
            }
        }
        MethodCombo.ItemsSource = items;
        if (items.Count > 0) MethodCombo.SelectedIndex = 0;
    }

    private async void Call_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        string rawAddr = AddressBox.Text.Trim();
        if (!Uri.TryCreate(rawAddr, UriKind.Absolute, out var uri) || (uri.Scheme is not ("http" or "https")))
        {
            StatusLine.Text = "Enter a valid http:// or https:// gRPC address.";
            return;
        }

        string methodSpec = (MethodCombo.Text ?? "").Trim();
        int slash = methodSpec.LastIndexOf('/');
        if (slash <= 0 || slash == methodSpec.Length - 1)
        {
            StatusLine.Text = "Select or type a method as Service/Method.";
            return;
        }
        string service = methodSpec[..slash];
        string method = methodSpec[(slash + 1)..];
        var metadata = ParseMetadata(MetadataBox.Text);
        string reqJson = string.IsNullOrWhiteSpace(RequestBox.Text) ? "{}" : RequestBox.Text.Trim();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        CallButton.Content = "Stop";
        ResponseBox.Text = "";
        StatusLine.Text = $"Calling {service}/{method}…";

        try
        {
            await using var caller = new GrpcCaller(uri, _cert, _transport, _trustCert, _protoset);
            _discovered.TryGetValue(methodSpec, out var info);
            bool clientStream = info?.ClientStreaming == true;
            bool serverStream = info?.ServerStreaming == true;

            if (!clientStream && !serverStream)
            {
                var res = await caller.InvokeAsync(service, method, reqJson, metadata, ct);
                ResponseBox.Text = FormatCallResult(res);
                StatusLine.Text = $"{res.StatusName} ({res.StatusCode}) · {res.Elapsed.TotalMilliseconds:F0} ms";
            }
            else if (!clientStream && serverStream)
            {
                var sb = new StringBuilder();
                int count = 0;
                await foreach (var msg in caller.InvokeStreamingAsync(service, method, reqJson, metadata, ct))
                {
                    count++;
                    sb.AppendLine(msg);
                    ResponseBox.Text = sb.ToString();
                    StatusLine.Text = $"Streaming… {count} message(s)";
                }
                StatusLine.Text = $"OK · {count} streamed message(s)";
            }
            else
            {
                var messages = ParseRequestMessages(reqJson);
                if (clientStream && !serverStream)
                {
                    var res = await caller.InvokeClientStreamingAsync(service, method, ToAsync(messages), metadata, ct);
                    ResponseBox.Text = FormatCallResult(res);
                    StatusLine.Text = $"{res.StatusName} ({res.StatusCode}) · {res.Elapsed.TotalMilliseconds:F0} ms";
                }
                else
                {
                    var sb = new StringBuilder();
                    int count = 0;
                    var res = await caller.InvokeDuplexAsync(service, method, ToAsync(messages), metadata, msg =>
                    {
                        count++;
                        sb.AppendLine(msg);
                        Dispatcher.BeginInvoke(() =>
                        {
                            ResponseBox.Text = sb.ToString();
                            StatusLine.Text = $"Duplex streaming… {count} message(s)";
                        });
                        return true;
                    }, ct);
                    if (res.StatusCode != 0) sb.AppendLine($"\nStatus: {res.StatusName} ({res.StatusCode}) {res.StatusDetail}");
                    ResponseBox.Text = sb.ToString();
                    StatusLine.Text = $"{res.StatusName} ({res.StatusCode}) · {count} message(s) · {res.Elapsed.TotalMilliseconds:F0} ms";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "Call cancelled.";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Error: " + ex.Message;
            ResponseBox.Text = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CallButton.Content = "Call";
        }
    }

    private static string FormatCallResult(GrpcCallResult res)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(res.ResponseJson))
            sb.AppendLine(res.ResponseJson);
        if (res.StatusCode != 0)
            sb.AppendLine($"gRPC Status: {res.StatusName} ({res.StatusCode}) {res.StatusDetail}");
        if (res.Trailers.Count > 0)
        {
            sb.AppendLine("\nTrailers:");
            foreach (var t in res.Trailers) sb.AppendLine($"  {t.Key}: {t.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private static List<KeyValuePair<string, string>> ParseMetadata(string raw)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var line in raw.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string k = line[..colon].Trim();
            string v = line[(colon + 1)..].Trim();
            if (k.Length > 0) list.Add(new(k, v));
        }
        return list;
    }

    private static List<string> ParseRequestMessages(string raw)
    {
        if (raw.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.EnumerateArray().Select(e => e.GetRawText()).ToList();
        }
        return new List<string> { raw };
    }

    private static async IAsyncEnumerable<string> ToAsync(IEnumerable<string> items)
    {
        foreach (var i in items)
        {
            yield return i;
            await Task.Yield();
        }
    }
}
