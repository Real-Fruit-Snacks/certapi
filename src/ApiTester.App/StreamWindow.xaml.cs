using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>
/// A live-stream console: opens a WebSocket (ws://, wss://) or a Server-Sent Events stream
/// (http://, https://) using the certificate and insecure toggle chosen in the main window.
/// WebSocket messages can be typed and sent; every received message or event is appended to the
/// transcript as it arrives. The stream runs on the UI context, so appends need no marshalling.
/// </summary>
public partial class StreamWindow : Window
{
    private readonly X509Certificate2? _cert;
    private readonly bool _insecure;
    private WebSocketSession? _ws;
    private CancellationTokenSource? _cts;

    public StreamWindow(string initialUrl, X509Certificate2? cert, bool insecure)
    {
        InitializeComponent();
        _cert = cert;
        _insecure = insecure;
        UrlBox.Text = initialUrl ?? "";
        UpdateModeHint();
        Loaded += (_, _) => { UrlBox.Focus(); UrlBox.SelectAll(); };
    }

    /// <summary>Reflect which protocol the current URL will use, so it's clear before connecting
    /// whether typing an http(s) URL gives SSE or a ws(s) URL gives a WebSocket.</summary>
    private void UpdateModeHint()
    {
        if (ModeText is null) return;   // TextChanged can fire during InitializeComponent
        string url = UrlBox.Text.Trim();
        ModeText.Text = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Scheme switch
            {
                "ws" or "wss" => "→ WebSocket — send messages, watch replies",
                "http" or "https" => "→ Server-Sent Events — watch events stream in",
                _ => $"Unsupported scheme '{uri.Scheme}' — use ws/wss or http/https"
            }
            : "WebSocket (ws/wss) or Server-Sent Events (http/https)";
    }

    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateModeHint();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _ = _ws?.DisposeAsync();
        base.OnClosed(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConnectButton.IsEnabled) { e.Handled = true; Connect_Click(sender, e); }
    }

    private void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SendButton.IsEnabled) { e.Handled = true; Send_Click(sender, e); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Log.Clear();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (url.Length == 0) { Append("!", "Enter a URL first."); return; }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Append("!", $"'{url}' is not a valid URL.");
            return;
        }

        _cts?.Dispose();   // the previous session has finished by the time Connect is enabled again
        _cts = new CancellationTokenSource();
        SetConnectingUi();

        try
        {
            switch (uri.Scheme)
            {
                case "ws":
                case "wss":
                    await RunWebSocketAsync(url);
                    break;
                case "http":
                case "https":
                    await RunSseAsync(url);
                    break;
                default:
                    Append("!", $"Unsupported scheme '{uri.Scheme}'. Use ws/wss for WebSocket or http/https for SSE.");
                    SetDisconnectedUi();
                    break;
            }
        }
        catch (OperationCanceledException) { Append("—", "Disconnected."); SetDisconnectedUi(); }
        catch (Exception ex) { Append("!", ex.Message); SetDisconnectedUi(); }
    }

    private async System.Threading.Tasks.Task RunWebSocketAsync(string url)
    {
        Append("—", $"Connecting to {url} …");
        var session = new WebSocketSession();
        _ws = session;
        try
        {
            await session.ConnectAsync(url, _cert, null, _insecure, ct: _cts!.Token);
            Append("—", "Connected. Type a message below and press Enter to send.");
            SetConnectedUi(canSend: true);

            try
            {
                await foreach (var msg in session.ReceiveAllAsync(_cts.Token))
                {
                    if (msg.IsClose) { Append("—", "Server closed the connection."); break; }
                    Append("<", msg.IsText ? msg.Text : $"[binary {msg.Bytes.Length} bytes]");
                }
            }
            catch (OperationCanceledException) { /* disconnect requested */ }
            catch (Exception ex) { Append("!", ex.Message); }
        }
        finally
        {
            // Dispose the socket for this session so a connect/disconnect cycle doesn't leak it.
            await session.DisposeAsync();
            if (ReferenceEquals(_ws, session)) _ws = null;
            SetDisconnectedUi();
        }
    }

    private async System.Threading.Tasks.Task RunSseAsync(string url)
    {
        Append("—", $"Streaming events from {url} …");
        SetConnectedUi(canSend: false);

        int count = 0;
        try
        {
            await foreach (var ev in SseClient.StreamAsync(url, _cert, null, _insecure, ct: _cts!.Token))
            {
                count++;
                string label = ev.Event is { Length: > 0 } name ? $"event: {name}" : "event";
                Append("•", $"{label}\n{ev.Data}");
            }
            Append("—", $"Stream ended ({count} event{(count == 1 ? "" : "s")}).");
        }
        catch (OperationCanceledException) { Append("—", $"Disconnected ({count} event{(count == 1 ? "" : "s")})."); }
        catch (Exception ex) { Append("!", ex.Message); }

        SetDisconnectedUi();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _cts is null) return;
        string text = MessageBox.Text;
        if (text.Length == 0) return;
        try
        {
            await _ws.SendTextAsync(text, _cts.Token);
            Append(">", text);
            MessageBox.Clear();
        }
        catch (Exception ex) { Append("!", ex.Message); }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _ = _ws?.CloseAsync();
    }

    // ---------- UI state ----------

    private void SetConnectingUi()
    {
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        UrlBox.IsEnabled = false;
    }

    private void SetConnectedUi(bool canSend)
    {
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        UrlBox.IsEnabled = false;
        MessageBox.IsEnabled = canSend;
        SendButton.IsEnabled = canSend;
    }

    private void SetDisconnectedUi()
    {
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        UrlBox.IsEnabled = true;
        MessageBox.IsEnabled = false;
        SendButton.IsEnabled = false;
        _ws = null;
    }

    private void Append(string kind, string text)
    {
        string prefix = kind switch
        {
            ">" => "→ ",   // sent
            "<" => "← ",   // received
            "•" => "• ",   // SSE event
            "!" => "! ",   // error
            _ => "  "       // info
        };
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        Log.Append($"{stamp}  {prefix}{text}");
    }

    private BoundedTranscript? _log;
    private BoundedTranscript Log => _log ??= new BoundedTranscript(Transcript);
}
