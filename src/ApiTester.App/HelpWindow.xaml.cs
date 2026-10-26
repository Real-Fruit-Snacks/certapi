using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ApiTester.App;

public partial class HelpWindow : Window
{
    private const string RepoUrl = "https://github.com/Real-Fruit-Snacks/certapi";
    private const string DocsUrl = "https://real-fruit-snacks.github.io/certapi/";

    private readonly List<(string Title, Func<UIElement> Build)> _sections;
    private static readonly FontFamily Code = new("Consolas");

    public HelpWindow()
    {
        InitializeComponent();
        _sections = new()
        {
            ("Getting started", GettingStarted),
            ("Requests & tabs", RequestsAndTabs),
            ("Certificates & mTLS", Certificates),
            ("Collections & history", Collections),
            ("Discovering endpoints", Discovery),
            ("Testing responses", Testing),
            ("Live streaming", Streaming),
            ("Environments & variables", Environments),
            ("Automatic tokens", AutoTokens),
            ("Importing & exporting", Importing),
            ("Command line", CommandLine),
            ("Rendered website", Rendered),
            ("Keyboard shortcuts", Shortcuts),
            ("About", About),
        };
        var titles = new List<string>();
        foreach (var s in _sections) titles.Add(s.Title);
        SectionList.ItemsSource = titles;
        SectionList.SelectedIndex = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = SectionList.SelectedIndex;
        if (i >= 0 && i < _sections.Count) ContentHost.Content = _sections[i].Build();
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- content ----------

    private Brush B(string key) => (Brush)FindResource(key);

    private UIElement Section(string title, params UIElement[] body)
    {
        var panel = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = B("Accent"), Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap
        });
        foreach (var el in body) panel.Children.Add(el);
        return panel;
    }

    private TextBlock Sub(string text) => new()
    {
        Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B("Text.Muted"),
        Margin = new Thickness(0, 18, 0, 6)
    };

    private TextBlock P(string text) => new()
    {
        Text = text, FontSize = 13.5, Foreground = B("Text.Soft"), Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
    };

    private UIElement Bullets(params string[] items)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var text in items)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new TextBlock { Text = "▸", Foreground = B("Accent"), FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            var tx = new TextBlock { Text = text, Foreground = B("Text.Soft"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            Grid.SetColumn(tx, 1);
            g.Children.Add(dot);
            g.Children.Add(tx);
            sp.Children.Add(g);
        }
        return sp;
    }

    private UIElement CodeLine(string text) => new Border
    {
        Background = B("Bg.Input"), BorderBrush = B("Border"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(11, 7, 11, 7),
        Margin = new Thickness(0, 2, 0, 12), HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontFamily = Code, Foreground = B("Accent.Alt"), FontSize = 13 }
    };

    private UIElement NoteBox(string text) => new Border
    {
        Background = B("Bg.Panel"), BorderBrush = B("Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 8, 0, 10),
        Child = new TextBlock { Text = text, Foreground = B("Text.Soft"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight }
    };

    private UIElement KeyTable((string Key, string Action)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new Border
            {
                BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8),
                Child = new TextBlock { Text = rows[r].Key, FontFamily = Code, Foreground = B("Accent.Alt"), FontSize = 12.5 }
            };
            var act = new Border
            {
                BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8),
                Child = new TextBlock { Text = rows[r].Action, Foreground = B("Text.Soft"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
            };
            Grid.SetRow(key, r);
            Grid.SetRow(act, r);
            Grid.SetColumn(act, 1);
            grid.Children.Add(key);
            grid.Children.Add(act);
        }
        return grid;
    }

    private UIElement LinkButton(string label, string url)
    {
        var b = new Button { Content = label, Height = 30, FontSize = 12, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser / no web access — nothing to do */ }
        };
        return b;
    }

    // ---------- sections ----------

    private UIElement GettingStarted() => Section("Getting started",
        P("certapi sends HTTP requests and can authenticate them with a client certificate from your Windows certificate store (mutual TLS). The certificate is optional, so it also works as a general-purpose API client."),
        Sub("SEND YOUR FIRST REQUEST"),
        Bullets(
            "Pick a certificate in the CERTIFICATE row — or leave it on “— no certificate —” for a plain request.",
            "Choose a method and type a URL on the request line.",
            "Press Send (or Ctrl+Enter). The response appears in the panel below."),
        Sub("READING THE RESPONSE"),
        P("Pretty formats JSON and XML with syntax highlighting; Raw shows the exact bytes; Headers lists the response headers; Diagnostics shows the TLS and certificate details; Rendered opens the URL as a web page; Network traces every HTTP call — the request you sent and every resource the Rendered view fetched."),
        P("Use the “find in response…” box above the response to locate text in the body — Enter (or Find next) jumps to the next match and wraps around."),
        P("The pop-out button above the response (next to Copy body) opens either the selected view or the whole response panel — tabs and all — in its own window. Detach the entire panel to give the request editor the full main window, or pop a single view to watch, say, the Network trace beside the Pretty body. Everything stays live, and closing a popped-out window puts its content back in place."),
        P("“Copy as ▾” turns the current request into a ready-to-run snippet — cURL, PowerShell (Invoke-RestMethod), Python (requests), or C# (HttpClient) — with {{variables}} resolved and headers and body included."),
        P("The title bar carries the environment picker, a sun/moon button that toggles between the light and dark themes (your choice is remembered and applies to every window), the ? Help button (F1), and the window controls."),
        NoteBox("No client certificates on this machine? You can still test any endpoint that doesn't require one. To prove the certificate path end-to-end with no real server, click Run Self-Test at the bottom of the window — or Mock server… beside it to start a standing local endpoint you can send real requests to (http, TLS, or mTLS), or replay a captured HAR file with its From HAR… button."));

    private UIElement RequestsAndTabs() => Section("Requests & tabs",
        P("A request is built from the request line (method, URL, timeout) plus seven tabs beneath it."),
        Sub("THE REQUEST TABS"),
        Bullets(
            "Params — a key/value grid for the query string. Type a ?query in the URL and it splits into the grid; the grid is recombined onto the URL, correctly encoded, when you send.",
            "Headers — a key/value grid; tick a row to include it.",
            "Body — a request body with a content-type selector, or switch it to Form data (multipart) to add fields and upload files (tick File).",
            "Auth — Auto (use a captured token, the default), None, Bearer token, or Basic (username / password). The helper builds the Authorization header for you.",
            "Capture — save a value from the response into a {{variable}} for later requests (see Automatic tokens).",
            "Tests — assert on the response so a suite can pass/fail (see Testing responses).",
            "Transport — how the request reaches the endpoint: proxy, redirects, decompression, HTTP version, retries, and revocation checking (below)."),
        Sub("THE TRANSPORT TAB"),
        P("Transport settings belong to the request and are saved with it. Proxy: use the machine's " +
          "configured proxy (the default), ignore it altogether, or give an explicit proxy URL with a " +
          "username and password. A bypass list names hosts that skip whichever proxy is chosen and " +
          "are reached directly instead — comma-separated in the NO_PROXY style the rest of the " +
          "ecosystem uses: a host and its subdomains, an address or a range, or * for everything. It " +
          "narrows the machine's proxy or an explicit one, so it needs a proxy to narrow and cannot be " +
          "combined with ignoring the proxy entirely. Redirects: follow them or stop at the 3xx, with a " +
          "limit on how many hops are allowed. You can also switch off automatic decompression — to " +
          "keep the response bytes exactly as they arrived — and pin the HTTP version to 1.1 or 2 " +
          "instead of letting it be negotiated."),
        P("Each redirect that is followed shows up as its own row in the Network trace, so you can see " +
          "where the request really ended up. A hop that crosses to another origin is flagged: that is " +
          "where the Authorization header is dropped, and where your client certificate would be " +
          "presented to a host you didn't choose. The Diagnostics view names the proxy that was used, " +
          "and names the bypass rule when one sent the request direct instead — and remember that " +
          "behind any proxy the TLS version, cipher, and “client certificate presented” are blank, so " +
          "turning the proxy off is also how you get those back."),
        Sub("REVOCATION"),
        P("The same tab also controls certificate revocation checking: whether the server's " +
          "certificate is confirmed not to have been revoked by its issuer, and how hard to try. In a " +
          "corporate public-key infrastructure (PKI), a revoked certificate usually means a private " +
          "key was compromised or an employee left — a different finding from a merely untrusted " +
          "certificate, and revocation is where it's caught. “No revocation checking” (the default) " +
          "reproduces every earlier release's behavior exactly: nothing is asked. “Offline” consults " +
          "only certificate revocation lists (CRLs) already cached on the machine and never reaches " +
          "out to the network; “Online” may also fetch a fresh certificate revocation list (CRL) or " +
          "query the issuer's Online Certificate Status Protocol (OCSP) responder."),
        P("A status that can't be determined — an unreachable or blocked revocation endpoint — is " +
          "reported in Diagnostics but not treated as a failure by default, because that's the " +
          "ordinary state of affairs on a locked-down corporate network: failing on it would make the " +
          "tool unusable on the networks it targets. Tick “Fail when the status can't be determined” " +
          "to make that fatal instead. A certificate the issuer reports REVOKED is always refused, " +
          "even for one you have trusted in this app via “Trust & retry” or the Trusted-certificates " +
          "manager — a pin is your earlier statement, revocation is the issuer's later withdrawal, " +
          "and the later one wins. “Ignore server cert errors” still overrides all of it and accepts " +
          "the certificate regardless, but Diagnostics says so plainly — checking requested but not " +
          "enforced — rather than letting you assume the check came back clean."),
        NoteBox("Headless, the same setting is --revocation none|offline|online (default none), and " +
                "--revocation-strict makes an undetermined status fatal."),
        Sub("RETRIES"),
        P("The Retries group on the same tab handles an endpoint that fails intermittently. Set a " +
          "count (0, the default, means no retries), the statuses that earn one (429, 502, 503, and " +
          "504 to begin with), and the first delay in milliseconds. The delay doubles on each further " +
          "attempt with a little jitter, capped at 30 seconds — and if the server sends a Retry-After " +
          "header, that wins, because a server that says when to come back knows better than any " +
          "guess. “Retry connection failures and timeouts” (on) also retries a request that never " +
          "reached the server: a refused or reset connection, a name-resolution failure, a proxy " +
          "failure, or a timeout."),
        P("Only GET, HEAD, OPTIONS, PUT, and DELETE are retried unless you tick “Also retry POST and " +
          "PATCH” — re-sending a POST nobody confirmed can charge a card twice, so opting in is " +
          "explicit. A refused or untrusted certificate is never retried whatever you set: it would " +
          "only fail slower. Retry settings are saved with the request, and the response's metadata " +
          "reports how many attempts it took when it took more than one."),
        NoteBox("Headless, the same switches are --retry <n>, --retry-on <codes>, --retry-delay <ms>, " +
                "--retry-unsafe, and --no-retry-transport on certapi send, run, and fuzz. On run, a " +
                "flag overrides only what it names, so a saved request keeps its own retry settings " +
                "otherwise."),
        Sub("WORKING IN TABS"),
        Bullets(
            "Keep several requests open at once — each tab has its own website, certificate, and response.",
            "New tab: the + button or Ctrl+T. Close: the tab's ✕, middle-click, or Ctrl+W.",
            "Your open tabs are restored the next time you launch."),
        Sub("WEBSITE (BASE URL)"),
        P("Set a WEBSITE and the URL box becomes just the path after it — fire off /api/thing without retyping the host. Save frequently-used websites from the dropdown and pick them again later."));

    private UIElement Certificates() => Section("Certificates & mTLS",
        P("When a site asks for a client certificate, the app presents the one you picked and lets Windows sign the TLS handshake — the private key never leaves the store, so non-exportable and smart-card certificates work."),
        Bullets(
            "The picker lists certificates from CurrentUser\\My with subject, thumbprint, and expiry. Use the filter box to narrow a long list; press F5 to refresh.",
            "Not in the store? “From file…” loads a client certificate from a .pfx/.p12 (with an optional password) or a .pem/.crt for this session — headless, use --cert-file / --cert-password / --key-file.",
            "Certificates without a client-authentication EKU, and expired ones, are flagged in the list.",
            "“Ignore server cert errors” (off by default, clearly labelled insecure) lets you reach internal sites whose server certificate isn't publicly trusted."),
        Sub("DIAGNOSTICS"),
        P("The Diagnostics tab reports the negotiated TLS version and cipher, whether your client certificate was actually presented to the server, and the server's certificate — subject, issuer, thumbprint, expiry, and chain."),
        Sub("FAILURES"),
        P("Errors are classified so you know what went wrong: the server refused the certificate, the server's own certificate isn't trusted, a network/DNS error, or a timeout."),
        Sub("TRUSTED (PINNED) SERVER CERTIFICATES"),
        P("“Ignore server cert errors” trusts every certificate a server presents — useful, but it's " +
          "an all-or-nothing switch. Pinning is the narrower alternative: it trusts one specific " +
          "server-certificate thumbprint for one host, and nothing else. When a request to that host " +
          "hits ServerCertificateUntrusted, the app offers “Trust & retry” — accept the certificate " +
          "the server just presented and resend, without turning off checking anywhere else."),
        P("The Trusted-certificates manager (Import ▾ → “Trusted certificates…”) lists every pin and " +
          "lets you remove one. Headless, certapi trust list shows the same pins; certapi trust add " +
          "<host> --thumbprint <t> pins one you already know, or --from-url <https-url> connects once, " +
          "captures whatever certificate the server presents, and pins that thumbprint for you; " +
          "certapi trust remove <host> [--thumbprint <t>] un-pins one entry or all of them for that " +
          "host. send and run consult the store automatically and note on stderr when a pinned " +
          "certificate is what let the connection through."));

    private UIElement Collections() => Section("Collections & history",
        P("The sidebar has three modes, switched from HISTORY / COLLECTIONS / CHAINS at the top."),
        Sub("COLLECTIONS"),
        Bullets(
            "“Save current request…” stores the active request under a name.",
            "“+ Folder” groups saved requests; drag isn't needed — save into the selected folder.",
            "Double-click a saved request to open it in a new tab. Rename or Delete with the buttons.",
            "Each saved request remembers its last result: after you send it, a dot appears next to its name — mint when the last send returned a 2xx (known good), red when it failed or returned an error status. Hover the entry for when it was last checked and what it returned.",
            "Collections persist between sessions, including each request's last result."),
        P("Right-click a collection or folder and choose “Set website & certificate…” to give it " +
          "defaults: endpoints opened from it inherit that website and certificate when they don't " +
          "carry their own. The first successful send from a collection remembers the pair " +
          "automatically, so clicking through an imported API just works."),
        Sub("CHAINS"),
        P("CHAINS is the sidebar's answer to the “log in, then call the API” pattern: a chain is " +
          "saved requests in a stated order, run as one unit. “+ New chain…” creates one; “Edit " +
          "steps…” picks the requests, reorders or removes them, and sets each step's “stop the chain " +
          "if this step fails” (on by default) and the environment the chain's captures are written " +
          "into. Rename and Delete manage the list."),
        P("A chain runs the same way a suite does — variables resolved, assertions evaluated, capture " +
          "rules applied, known-good recorded per step — so a token captured by step one is available " +
          "to step two as a {{variable}}. Each step reports PASS or FAIL; when a failing step is set " +
          "to stop the chain, the steps that never ran are listed as SKIP rather than quietly " +
          "disappearing. Chains are included in an exported workspace."),
        NoteBox("A “▶ Run chain” button opens a window with one row per step — PASS, FAIL, or SKIP as " +
                "it completes — and selecting a row shows that step's actual response, its notes, and " +
                "any failing assertions; Stop (or closing the window) cancels a run in progress. The " +
                "same chain still runs headless with certapi run --chain \"<name>\", exiting non-zero " +
                "if any step failed; “Copy run command” puts that exact line on your clipboard."),
        Sub("HISTORY"),
        P("History lists your recent requests, labelled by path with the host beneath. Click one to reload the entire request — website, certificate, headers, auth, timeout, and body — and the response it returned. The app also remembers your window, last certificate, and settings between runs."));

    private UIElement Discovery() => Section("Discovering endpoints",
        P("When an API ships without documentation, use Discover to find out which endpoints exist. " +
          "Click “Discover…” in the toolbar, point it at a website, and hit Discover — it starts with a " +
          "built-in list of common endpoints (the “Use built-in list” button loads it), or choose/paste " +
          "your own. Each candidate is sent with your client certificate and any captured token."),
        Sub("READING THE RESULTS"),
        P("Each row shows the outcome: Found (2xx), Unauthorized (401/403 — it exists but needs auth), " +
          "MethodNotAllowed (405 — it exists, wrong method), Redirect (3xx), ServerError (5xx), NotFound (404), " +
          "OtherStatus (any other code), or Error (couldn't connect). Everything except NotFound and Error is " +
          "treated as a discovery. Hide the noise with the “Hide 404s / errors” toggle."),
        Sub("TURNING FINDINGS INTO REQUESTS"),
        P("Double-click a row to open that endpoint in a new request tab, or “Save discovered to collection…” " +
          "to store them all as saved requests you can run later."),
        NoteBox("The same discovery runs headless: certapi fuzz <website> (with no wordlist it uses the " +
                "built-in starter list; pass -w <file> for a thorough sweep). The starter list also ships " +
                "as wordlists/common-api-endpoints.txt."));

    private UIElement Testing() => Section("Testing responses",
        P("Add assertions on a request's Tests tab to turn it into a real test. Each assertion checks " +
          "one thing about the response and either passes or fails."),
        Sub("WHAT YOU CAN CHECK"),
        P("Target: Status · Time (ms) · a Header · a Body JSON path (like data.id) · the Body text. " +
          "Comparison: == , != , contains, matches (regex), exists, absent, < , > . For example: " +
          "Status == 200, Body data.id exists, Time < 500, Header Content-Type contains json."),
        Sub("HOW IT'S USED"),
        P("After you send, the status line shows a ✓ tests 3/3 passed summary and the Diagnostics view " +
          "lists each result. In suites, certapi run passes a request only when all its assertions pass — " +
          "a request with no assertions still passes on any 2xx, so tests are opt-in per request."),
        NoteBox("Run a suite headless with certapi run <collection>; failed assertions are printed on " +
                "stderr and included in --json output, and the exit code is non-zero if any request fails. " +
                "Add --data <file.csv|.json> to repeat the request(s) once per row, each row's columns " +
                "filling {{variables}} — table-testing an endpoint across many inputs."),
        Sub("RESPONSE DIFFING"),
        P("An assertion checks what you thought to check. The Diff view answers the other question: " +
          "did anything change at all? It compares the response you just got against a baseline and " +
          "lists what differs — the status, each header, and each value in the body."),
        P("The baseline is the saved request's own known-good response (the last 2xx it returned, " +
          "recorded for you and capped at 1 MiB), or an HTTP Archive (.har) file you choose with " +
          "“Compare with HAR…” — “Clear” goes back to the known-good one. When both sides are JSON " +
          "the comparison is structural: each changed path is named (data.items[0].id) as added, " +
          "removed, changed, or type-changed, with arrays compared by index. A body that isn't JSON " +
          "falls back to a one-line summary of the lines and bytes on each side, and a binary body " +
          "reports size and equality only — it doesn't pretend to diff a PDF."),
        P("Headers that change on every response are ignored by default, or a real difference would " +
          "drown in them: Date, Set-Cookie, ETag, Age, X-Request-Id, X-Correlation-Id, and " +
          "Server-Timing."),
        NoteBox("Headless: certapi send <url> --diff <baseline> compares against a .har file, a .json " +
                "response file (the envelope --json writes, or a saved snapshot), or the word " +
                "known-good; --diff-fail turns any difference into exit 1, the continuous integration " +
                "form. --diff-ignore <path> and --diff-ignore-header <name> are repeatable, and a named " +
                "header is added to the volatile defaults rather than replacing them. certapi run " +
                "--diff-har session.har replays a captured archive and passes an entry only when its " +
                "response is identical to the one recorded — a captured session as a regression test."));

    private UIElement Streaming() => Section("Live streaming",
        P("The Stream button on the request line opens a live console for connection-oriented endpoints — WebSockets and Server-Sent Events — reusing the client certificate you've selected and the Ignore-server-certificate-errors toggle."),
        Bullets(
            "It picks the protocol from the URL scheme: ws:// or wss:// opens a WebSocket; http:// or https:// streams Server-Sent Events (text/event-stream).",
            "WebSocket: type a message and press Enter (or Send) to send it; every message the server sends back appears in the transcript, tagged with the time and direction.",
            "Server-Sent Events: each event is appended as it arrives, with its event name (if any) and data — handy for streaming APIs and push notifications.",
            "Connect and Disconnect control the session; Clear empties the transcript. Closing the window disconnects."),
        NoteBox("The same thing headless: certapi ws <url> (send --message / stdin lines, --expect <n>) and certapi sse <url> (--max-events, --json)."));

    private UIElement Environments() => Section("Environments & variables",
        P("Define values once and reuse them anywhere with {{name}} placeholders — ideal for switching between Dev, Staging, and Prod without editing every request."),
        Bullets(
            "Click Edit next to the ENV selector (top bar) to manage environments and their key/value variables.",
            "Pick the active environment from the ENV dropdown.",
            "Use {{variable}} in the URL, query, headers, body, or auth. It is substituted when you send; saved requests keep the raw {{tokens}}.",
            "A token with no value is left untouched and reported in the status line, so nothing is sent silently wrong."),
        Sub("EXAMPLE"),
        CodeLine("{{base}}/users/{{userId}}"),
        P("With base = https://api.internal.corp and userId = 42, that request is sent to https://api.internal.corp/users/42."),
        Sub("CAPTURE A TOKEN FROM A RESPONSE"),
        P("A request's Capture tab can save a value from its response into a {{variable}}: set a Variable name, choose Body (a dotted JSON path like data.access_token) or Header (a header name), and the value is written to your active environment when you send — a “Captured” environment is created if you don't have one selected. Reuse it as {{token}} in a Bearer token or any field. This turns an auth call + token reuse into two clicks."));

    private UIElement AutoTokens() => Section("Automatic tokens",
        P("Call a login endpoint and the app spots the bearer token in the response — access_token, " +
          "id_token, token, accessToken, or jwt in the JSON body (top level or under data/result), " +
          "or an X-Auth-Token / X-Access-Token header. No setup needed."),
        Sub("SCOPED TO THE WEBSITE"),
        P("A captured token belongs to the exact website it came from (scheme, host, and port). " +
          "Requests to any other website never receive it."),
        Sub("USING IT"),
        P("Requests whose Auth type is “Auto (captured token)” — the default — attach the token " +
          "automatically. A chip in the status bar shows the active website's token and its expiry; " +
          "click it to inspect, clear, or turn automatic tokens off. Pick “None (never send auth)” " +
          "on a request to opt out."),
        Sub("OAUTH 2.0"),
        P("For proper OAuth, the Get OAuth 2.0 token… button on the Auth tab fetches a token: the " +
          "client-credentials, password, and refresh grants run directly, and the authorization-code " +
          "grant opens your browser and catches the redirect on a temporary 127.0.0.1 port with PKCE. " +
          "The token is stored for the API's host (so Auto auth attaches it) and filled into the Bearer " +
          "field. The token endpoint itself may require a client certificate. Headless: certapi token " +
          "--token-url … --client-id … (--grant password|refresh, --save --for <api-url>)."),
        Sub("WINDOWS INTEGRATED AUTH"),
        P("For internal sites that authenticate with your Windows identity, pick the Windows " +
          "(integrated) auth type. By default it signs in with your logged-in account (single " +
          "sign-on); untick that to supply an explicit DOMAIN\\user and password. Kerberos or NTLM " +
          "is negotiated with the server automatically. Headless: certapi send --windows-auth " +
          "(or --windows-user DOMAIN\\user --windows-password …)."),
        Sub("CAPTURE A BROWSER LOGIN"),
        P("For sites that log in through a web page — where the session ends up in cookies or a token " +
          "minted by JavaScript — click Capture session… in the status bar. A browser opens; you log " +
          "in on the site itself (your password is never seen or stored). On Finish it captures the " +
          "session cookies and any bearer token, scoped per website, and attaches them to later " +
          "requests automatically — in the app and headless via certapi. It can also save the API " +
          "calls it saw during login as a ready-to-run collection. The session chip lets you clear or " +
          "turn off captured cookies/tokens."),
        Sub("EVERYWHERE"),
        P("The same capture-and-reuse works headless: certapi send and certapi run print a note " +
          "when they capture or use a token (--no-auto-token disables it), and the MCP server " +
          "keeps a per-session token store so agent login flows just work."),
        Sub("SESSION COOKIES"),
        P("The app keeps a cookie jar for the session, like a browser: a Set-Cookie in any response " +
          "is stored and sent back on later requests to that host, so cookie-based logins work across " +
          "sends. Headless, add --cookies to certapi run to share a jar across a suite."),
        NoteBox("Explicit auth always wins: a Bearer/Basic setting or a manual Authorization " +
                "header is never overridden, and expired tokens are never sent. Captured tokens " +
                "are encrypted in your workspace for your Windows user, not saved in plain text " +
                "(see Importing & exporting)."));

    private UIElement Importing() => Section("Importing & exporting",
        P("Bring requests in from elsewhere with the Import ▾ menu next to the tabs."),
        Sub("PASTE CURL"),
        P("Paste a curl command and it opens a ready-to-send tab with the method, URL, query parameters, headers, body, and auth filled in. It understands -X, -H, -d / --data, -u (Basic auth), -k (insecure), an Authorization: Bearer header (mapped to the Bearer helper), quoting, and line continuations."),
        Sub("IMPORT OPENAPI / SWAGGER"),
        P("Choose a JSON OpenAPI 3.x or Swagger 2.0 file and it builds a collection of requests, grouped into folders by tag, with the server (OpenAPI) or host/basePath (Swagger) used as each request's website."),
        Sub("CAPTURE & REPLAY HAR"),
        P("Import ▾ → “Export Network trace as HAR…” writes the current tab's Network trace to an " +
          "HTTP Archive (HAR) file — every request logged, with each redirect hop kept as its own " +
          "entry. Secret values (Authorization, Cookie, Set-Cookie, and similar headers) are redacted " +
          "by default, with a choice to keep the real values when you need them. “HAR file…” imports " +
          "one back as a collection, the same way a curl command or OpenAPI file does."),
        P("Replaying a HAR is where this earns its keep: a browser can capture a HAR of a session, " +
          "but it can never resend those requests through mutual TLS, because a browser has no way to " +
          "attach a client certificate on your behalf. certapi can. Headless, certapi run session.har " +
          "detects the .har file directly and replays its entries as an ordered suite, with the " +
          "client certificate you pass on --cert / --cert-file attached to every request."),
        NoteBox("A HAR run never writes live state — no known-good markers, no captured tokens. A " +
                "malformed HAR file is a one-line data error; a well-formed one with no entries to " +
                "replay is a data error too, since there is nothing to run."),
        P("Import ▾ → “Export OpenAPI from HAR file…” turns a captured archive into an OpenAPI " +
          "document instead of a collection: repeated calls to the same endpoint collapse into one " +
          "operation, and identifier-looking path segments become {id} — conservatively, only " +
          "digits, a Universally Unique Identifier (UUID), or a long hexadecimal string, and only " +
          "when the value actually varies between calls. Responses of 400 and above are skipped, " +
          "and redacted header values are never written. Headless, the same thing is " +
          "certapi export openapi --from-har session.har -o api.json."),
        Sub("SAVE / LOAD A WORKSPACE"),
        P("“Export workspace…” in the Import ▾ menu saves everything — open tabs, collections (with their known-good results), environments, saved websites, and history — to a single JSON file. “Import workspace…” loads one back, either merging into what you have or replacing it. Use it to keep named snapshots of a project or hand a teammate a ready-to-use setup."),
        NoteBox("The export writes secrets — captured tokens/cookies, saved auth values, secret variables — encrypted for your Windows user, the same as the live workspace; there's no way to write them to disk in the clear. Moving the file to another machine, or opening it signed in as someone else, brings everything across except those secrets, which that user or machine can't decrypt (headless, certapi export workspace strips secrets by default instead — see the command line's --include-secrets)."),
        Sub("SECRETS AT REST"),
        P("Your workspace lives at %AppData%\\CertApiTester\\state.json. Most of it is plain, readable " +
          "JSON — requests, collections, chains, history, environment names — so the file stays easy " +
          "to inspect. The secrets in it are not: a captured bearer token, a browser-captured session " +
          "cookie, a saved request's auth secret (Basic password or bearer token), and any environment " +
          "variable ticked secret are encrypted with the Windows Data Protection API (DPAPI), scoped " +
          "to the Windows user who saved them."),
        NoteBox("A state.json file copied to another Windows user, or to another machine, still opens " +
                "with everything intact except those secrets — they can't be decrypted there, so each " +
                "one is reported and treated as absent rather than crashing the load. The first time an " +
                "older workspace is rewritten in the new format, the previous file is kept beside it as " +
                "a timestamped state.json.<date-time>.bak, so upgrading never costs you the copy you had " +
                "before."),
        Sub("EXPORT AS OPENAPI"),
        P("“Export as OpenAPI…” at the bottom of the collections sidebar writes the selected folder — or all collections when nothing is selected — as an OpenAPI 3.0 JSON file: folders become tags, each saved request becomes an operation with its query parameters, headers, and body example, and a request's known-good note (when it was last checked and what it returned) becomes the operation description."),
        NoteBox("Exports are safe to share: authentication is written only as a security scheme — bearer tokens, usernames, and passwords are never written to the file."));

    private UIElement CommandLine() => Section("Command line",
        P("certapi.exe — a separate download on the releases page — is the tester without the window, built for scripts and scheduled tasks."),
        Bullets(
            "certapi send <url> sends a one-off request; pick a client certificate with --cert <thumbprint or subject> (or --cert-file for a .pfx/.pem). The body goes to stdout, diagnostics to stderr. Upload files as multipart with -F \"field=value\" -F \"file=@path\".",
            "certapi run <collection or folder> runs saved requests as a pass/fail suite (a request passes when its Tests all pass, or on any 2xx if it has none) and updates their known-good markers — automatically against your live workspace, or add --record when running from an exported workspace file (--workspace).",
            "certapi fuzz <base-url> discovers endpoints from a wordlist — pass -w <file>, or omit it for the built-in starter list — and reports which paths exist on an undocumented API.",
            "certapi bench <url or saved request> measures one endpoint under load — -n <count> requests at -c <concurrency> (100 and 10 by default), or --duration <seconds> for a wall-clock run, with --warmup <seconds> discarded first. It reports how many succeeded, the rate, and the min/p50/p90/p99/max latencies (--json for a machine-readable envelope). See the note below about what those latencies include.",
            "send, run, and fuzz share the transport flags. --proxy <url> routes through a proxy you name (--proxy-user user:pass when it wants credentials); --no-proxy ignores the machine's configured proxy — which is also how you get the TLS version, cipher, and “certificate presented” back, since none of the handshake is visible through a proxy. --noproxy <list> names the hosts that bypass whichever proxy is in effect — it narrows the proxy setting rather than replacing it, and defaults to the NO_PROXY environment variable, which an explicit --noproxy overrides.",
            "--no-redirect stops at the 3xx instead of following it, --max-redirs <n> changes the limit (20 by default), and --show-redirects prints every hop — flagging a hop that crosses to another origin, where the Authorization header is dropped and your client certificate would go to a host you didn't choose.",
            "--no-decompress relays the response bytes exactly as they arrived instead of decoding them, and --http1.1 / --http2 pin the HTTP version instead of negotiating it.",
            "--resolve host:port:ip connects to the address you name while the request still carries the original hostname — one node behind a load balancer, or a DNS cutover you want to check before it happens. Repeat it for more hosts; it needs a direct connection, so it can't be combined with a proxy. certapi send --all-ips does the sweep for you across every address the host resolves to and prints a per-address comparison.",
            "certapi send also supports GraphQL (--graphql \"<query>\" --gql-variables \"{...}\") — a JSON { query, variables } POST.",
            "certapi token fetches an OAuth 2.0 access token — --grant client_credentials (default), password, or refresh — and with --save --for <api-url> stores it so later sends attach it automatically.",
            "certapi ws <url> opens a WebSocket (ws/wss) — send messages with --message or piped stdin lines, print replies, and use --expect <n> for scripts. certapi sse <url> streams Server-Sent Events (--max-events, --json).",
            "certapi certs lists client certificates; certapi selftest proves the mutual-TLS path end to end.",
            "certapi mock runs a standing local test server to fire requests at — it echoes each request and serves /status/<code>, /sse, /token, /windows-auth, /cookie-auth, and a WebSocket echo, over http, --tls, or --mtls (generating the certs). Point the app at it to try every feature without a real API. --har <file> replays a captured session instead — the built-in routes aren't served while replaying, and --no-match-status (default 404) sets what a request matching nothing gets back.",
            "certapi serve <upstream> --port <n> runs a local gateway on 127.0.0.1: point an app's base URL at the port and it reaches a certificate-protected site with your client certificate attached — no mTLS code in the app. Mount several upstreams behind the one port with --upstream /api=https://api.internal, add --browser when the caller is a web page (below), and add --tls to serve the gateway itself over HTTPS.",
            "certapi grpc list <address> discovers the services and methods a gRPC server advertises via server reflection; certapi grpc call <address> <Service/Method> -d '<json>' invokes one — unary, server-streaming, client-streaming, or bidirectional, chosen from the method's own definition — with your Windows-store client certificate attached exactly as send uses it. A response prints as JSON — one compact object per line for a streaming method as each arrives — and --max-messages <n> stops a stream early. See the note below for what this command deliberately does not do.",
            "certapi mcp runs a Model Context Protocol server so an AI agent can make mTLS calls with a certificate you pin at launch, bounded by a host allowlist — send_request, list_certificates, list_saved, run_saved, and self_test tools over stdio.",
            "certapi import / export move cURL commands, OpenAPI documents, and whole workspaces in and out.",
            "Exit codes are script-friendly: 0 success, 1 failure, 2 usage error, 3 data error. Run certapi help <command> for all options."),
        Sub("BENCHING AN ENDPOINT"),
        P("certapi bench sends the same request over and over down the same client-certificate path " +
          "the rest of the tool uses, so what it measures is what a real send does. Percentiles come " +
          "from every retained latency rather than an approximation, a bench never writes anything (no " +
          "known-good markers, no captured tokens, no state file), and it exits 0 whenever it " +
          "measured anything, however bad the failure rate — it reports numbers rather than passing " +
          "judgement. Exit 1 means no request got a response at all, where there is nothing to " +
          "report but that the endpoint could not be reached."),
        NoteBox("What the latencies include: connections are pooled and reused, so only the first " +
                "request to an origin pays the TCP connect and TLS handshake — every later request " +
                "that shares the same client certificate and trust policy reuses that connection and " +
                "measures only the request and response. --warmup discards that first-connection cost " +
                "so the figures describe a warmed-up endpoint. A request routed through a proxy still " +
                "opens its own connection every time, because the proxied path can't be pooled. " +
                "Retries are also forced off during a bench, because a " +
                "retry turns a failure into a slow success and hides the failure rate the bench exists " +
                "to measure — --bench-retries measures it anyway. There is no window for the bench: it " +
                "is a command-line concern."),
        Sub("THE GATEWAY, FROM A BROWSER"),
        P("One certapi serve can front several upstreams: --upstream /api=https://api.internal mounts " +
          "a host at a path prefix and repeats as often as you like, so GET /api/orders reaches " +
          "https://api.internal/orders with the prefix stripped before it is forwarded. The longest " +
          "matching prefix wins, and a path under no prefix is a 404 that contacts nothing."),
        P("A plain relay hands a browser exactly the headers that make it refuse the response, so " +
          "--browser turns on the four accommodations that fix that — each also usable on its own. " +
          "--cors answers Cross-Origin Resource Sharing (CORS) preflights at the gateway and adds the " +
          "headers a script needs to read the reply (give it a comma-separated list to allow only " +
          "those origins and refuse the rest with 403); --cors-max-age <seconds> controls how long " +
          "the browser may cache that preflight answer (default 600). Chrome also runs a Private " +
          "Network Access (PNA) check before a page on a public origin may reach a private or " +
          "loopback address at all, and --cors answers that too, but only for an origin the same " +
          "allowlist already accepts — never unconditionally. --rewrite-cookies drops Domain= and " +
          "Secure from each Set-Cookie and turns SameSite=None into Lax, so the browser stores the " +
          "cookie against the gateway. --rewrite-location points a 3xx Location aimed at the " +
          "upstream back at the gateway; one aimed anywhere else is left exactly as the upstream " +
          "wrote it and logged, because that hop leaves the gateway and your client certificate " +
          "with it. --allow-upgrade relays WebSocket connections to the upstream through your " +
          "certificate. Without these flags nothing changes: the gateway stays a byte-faithful " +
          "relay."),
        NoteBox("Over the default plaintext loopback origin, a cookie named __Host-… or __Secure-… " +
                "still cannot work — it requires the Secure attribute, which no browser accepts over " +
                "plaintext http://127.0.0.1 — so it is relayed and named in a warning rather than " +
                "dropped behind your back. --tls is the fix: it serves the gateway itself over HTTPS " +
                "with a generated certificate, so Secure, SameSite=None, and __Host-/__Secure- cookies " +
                "all work. The first bind needs an elevated prompt (the exact netsh command is " +
                "printed when one isn't available), and --tls-trust installs the certificate so the " +
                "browser stops warning about it — reversible with --tls-untrust."),
        P("--request-header \"Name: value\" and --response-header \"Name: value\" set a header on " +
          "forwarded traffic — replacing it if one was already there, adding it otherwise — and " +
          "--remove-request-header <name> / --remove-response-header <name> strip one; all four are " +
          "repeatable, and naming the same header to a set flag and a remove flag on the same side " +
          "removes it, since removal wins over setting. These rules are not a browser concern: they " +
          "apply with or without --browser, and on the response side after --browser's own rewrites " +
          "(CORS, cookies, Location), so a header you set here wins over one the gateway injected."),
        NoteBox("Connection, Keep-Alive, Transfer-Encoding, Content-Length, TE, Trailer, Upgrade, " +
                "Proxy-Authenticate, Proxy-Authorization, and Host are refused with a usage error " +
                "naming the header and why, rather than silently ignored: the first nine frame the " +
                "HTTP message and the HTTP stack manages them, and Host is set by the gateway's own " +
                "HTTP client from the upstream URI, so a rule for it would only ever half-apply. A " +
                "missing header name, or one carrying a character an HTTP field name cannot hold — " +
                "a space, an embedded colon — is refused the same way: the header could never " +
                "match, so the rule would be dropped rather than applied."),
        NoteBox("While the app is open, headless runs skip writing results (the app would overwrite them when it closes) — scheduled checks record normally."),
        NoteBox("certapi grpc is command-line only — there is no window for it. certapi grpc call " +
                "handles unary, server-streaming, client-streaming, and bidirectional methods, choosing " +
                "the kind from the service's own definition rather than a flag. It normally learns a " +
                "service's methods from server reflection, but a server with reflection turned off can " +
                "still be reached by supplying a compiled descriptor set with --protoset (produced by " +
                "protoc --descriptor_set_out=... --include_imports). And certapi serve does not proxy " +
                "gRPC, because HttpListener (what the gateway is built on) is HTTP/1.1-only — certapi " +
                "grpc reaches the service directly with your certificate instead of going through the " +
                "gateway."));

    private UIElement Rendered() => Section("Rendered website",
        P("The Rendered response tab opens the current request's URL as a web page instead of raw text — useful when the target is a site rather than an API."),
        Bullets(
            "Every resource the page loads — the document, CSS, JavaScript, images, and XHR — is fetched with your selected client certificate, so a certificate-protected site renders fully, not just its HTML.",
            "It loads on demand: nothing runs until you open the tab. Use Reload to fetch again.",
            "The address line shows exactly which URL is being rendered.",
            "The Network tab logs every resource the page fetches — method, status, type, size, and timing, like a browser's network panel — so you can see what a certificate-protected page loads and whether each resource succeeded.",
            "In the Network tab you can filter the trace by text or status class (2xx–5xx, errors), show only calls made with your certificate, click a row for its full details and headers, drag the divider to resize the details, and right-click a row to copy its URL or a matching curl command."),
        NoteBox("The rendered view uses the Microsoft Edge WebView2 runtime, which ships with Windows 11 (and is a standard component on up-to-date Windows 10). If it isn't available, the tab explains that and the rest of the app is unaffected."));

    private UIElement Shortcuts() => Section("Keyboard shortcuts",
        KeyTable(new[]
        {
            ("Ctrl+Enter / Enter", "Send the request (Enter works in the URL box)"),
            ("Esc", "Cancel an in-flight request"),
            ("Ctrl+L", "Focus the URL box"),
            ("Ctrl+S", "Save the response to a file"),
            ("Ctrl+H", "Toggle the sidebar"),
            ("Ctrl+T", "New request tab"),
            ("Ctrl+W", "Close the current tab"),
            ("F5", "Refresh the certificate list"),
            ("F1", "Open this help"),
        }));

    private UIElement About()
    {
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
        links.Children.Add(LinkButton("View on GitHub", RepoUrl));
        links.Children.Add(LinkButton("Documentation", DocsUrl));

        return Section("About",
            P($"certapi  •  version {AppVersion()}"),
            P("A Windows desktop API tester that authenticates to endpoints with a client certificate from the Windows certificate store (mutual TLS), and renders whatever they return — even a full web page."),
            Sub("LINKS"),
            links,
            P(RepoUrl),
            P(DocsUrl),
            Sub("PRIVACY"),
            Bullets(
                "No telemetry. The app makes no network calls other than the requests you send.",
                "Client certificates are never exported; Windows performs the signing.",
                "Window and request settings are stored locally under %AppData%\\CertApiTester."),
            Sub("LICENSE"),
            P("Released under the MIT License."));
    }

    private static string AppVersion()
    {
        var asm = typeof(HelpWindow).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var v = info ?? asm.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
