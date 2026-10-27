using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;
using Microsoft.Web.WebView2.Core;

namespace ApiTester.App;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certService = new();
    private readonly ApiClient _apiClient = new();
    // One instance for the whole GUI session, alongside _apiClient: the handler cache keys the
    // trust predicate by delegate identity, so a fresh lambda built per send could never let two
    // requests to the same host share a pooled connection. Assigned in the constructor, after
    // _state's field initializer has run (see below), rather than inline here.
    private readonly TrustPredicates _trustPredicates;
    private readonly System.Net.CookieContainer _cookieJar = new();   // browser-like session cookies across sends
    private readonly ResponseFormatter _formatter = new();
    private readonly AppState _state = AppState.Load();

    private readonly ObservableCollection<RequestTab> _tabs = new();
    private RequestTab? _loadedTab;   // the tab currently loaded into the editor controls
    private bool _switching;          // guards re-entrancy during tab switches
    private bool _loading;            // true while pushing a model into the controls

    private readonly ObservableCollection<CollectionNode> _collections = new();
    private readonly ObservableCollection<RequestChain> _chains = new();
    private readonly ObservableCollection<ApiEnvironment> _environments = new();
    private List<string> _unresolvedVars = new();
    private bool _envLoading;

    private MtlsBrowserSession? _browserSession;
    private CancellationTokenSource? _browserCts;
    private bool _browserReady;
    private RequestTab? _renderTraceTarget;   // the tab whose Network trace a render populates
    private string? _renderCertSubject;       // the client cert the browser session offers

    private IReadOnlyList<CertificateInfo> _certs = new List<CertificateInfo>();
    private List<CertOption> _allOptions = new();
    private List<CertOption> _visibleOptions = new();

    private ApiResponse? _lastResponse;
    // Whatever the Pretty view currently shows (response, error, or self-test detail), so a theme
    // toggle can re-highlight it with the new palette.
    private (string Text, BodyKind Kind)? _lastPretty;
    private string _lastRawText = "";
    private CancellationTokenSource? _cts;

    /// <summary>A HAR baseline the user chose for the Diff view, and the file it came from. Null means
    /// compare against the saved request's own known-good response instead. Per window rather than per
    /// tab: a chosen baseline is a comparison the user is running right now, not a property of a request.</summary>
    private ResponseSnapshot? _diffBaseline;
    private string? _diffBaselineName;

    private sealed record CertOption(string Label, X509Certificate2? Cert, string? Thumbprint);

    private RequestTab? ActiveTab => TabStrip.SelectedItem as RequestTab;
    private RequestModel? ActiveRequest => ActiveTab?.Request;

    public MainWindow()
    {
        InitializeComponent();
        _trustPredicates = new TrustPredicates(_state);

        // Restore persisted window bounds.
        if (_state.WindowWidth is > 400) Width = _state.WindowWidth.Value;
        if (_state.WindowHeight is > 300) Height = _state.WindowHeight.Value;
        if (_state.WindowLeft is { } l && _state.WindowTop is { } t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        LoadCertificates();
        SelectCertByThumbprint(_state.LastCertThumbprint);
        RefreshSavedBases();
        RefreshHistoryList();
        InitializeTabs();
        InitializeCollections();
        InitializeChains();
        InitializeEnvironments();

        PreviewKeyDown += Window_PreviewKeyDown;
        UpdateTokenChip();
        UpdateThemeToggleGlyph();

        // Tell the user what happened to their secrets: either some were just dropped on this
        // load (per-user decryption failed), or the file is still waiting for its first encrypted
        // save — whichever is more pressing to know about right now.
        if (_state.SecretWarnings.Count > 0)
        {
            StatusText.Text = _state.SecretWarnings[0] +
                (_state.SecretWarnings.Count > 1 ? $" (+{_state.SecretWarnings.Count - 1} more)" : "");
        }
        else if (AppState.NeedsSecretUpgrade(AppState.DefaultPath))
        {
            StatusText.Text = "Your workspace still stores secrets in the clear — they will be encrypted " +
                "the next time it saves, and a timestamped backup of the current file will be kept beside it.";
        }
    }

    /// <summary>Flip between the dark and light palettes, persist the choice, and repaint the
    /// native title bar to match. Palette references are DynamicResource, so the swap is live.</summary>
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        string next = app.CurrentTheme == "Light" ? "Dark" : "Light";
        app.ApplyTheme(next);
        _state.Theme = next;
        NativeTheme.ApplyTitleBar(this, next == "Light");
        UpdateThemeToggleGlyph();

        // Re-highlight whatever the Pretty view is showing — a response, an error, or the
        // self-test detail — so it swaps to the new theme's colours (the highlighter builds a
        // static document, so it doesn't repaint on its own).
        if (_lastPretty is { } p) SetPretty(p.Text, p.Kind);

        StatusText.Text = $"{next} theme applied.";
        SaveState();
    }

    /// <summary>Show the glyph/tooltip for the theme the button would switch to.</summary>
    private void UpdateThemeToggleGlyph()
    {
        bool light = ((App)Application.Current).CurrentTheme == "Light";
        ThemeToggle.Content = ((char)(light ? 0xE708 : 0xE706)).ToString(); // moon (switch to dark) / sun (switch to light)
        ThemeToggle.ToolTip = light ? "Switch to dark theme" : "Switch to light theme";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
        if (_state.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ActiveRequest is { } active) CaptureControlsInto(active);
        CaptureStateForSave();
        SaveState();

        _browserSession?.Dispose();
        _browserCts?.Cancel();
        _browserCts?.Dispose();
        // _apiClient lives for the whole session; FuzzWindow only borrows it (ShowDialog, so it is
        // always closed again before this window can close), so nothing else can still be using it.
        _apiClient.Dispose();

        base.OnClosing(e);
    }

    /// <summary>Bring <see cref="_state"/> up to date with the UI (window, tabs, collections,
    /// environments) so it can be persisted or exported.</summary>
    private void CaptureStateForSave()
    {
        if (WindowState == WindowState.Normal)
        {
            _state.WindowLeft = Left; _state.WindowTop = Top;
            _state.WindowWidth = Width; _state.WindowHeight = Height;
        }
        _state.WindowMaximized = WindowState == WindowState.Maximized;

        _state.Tabs = _tabs.Select(t => t.Request).ToList();
        _state.ActiveTabIndex = Math.Max(0, TabStrip.SelectedIndex);
        _state.Collections = _collections.ToList();
        _state.Chains = _chains.ToList();
        _state.Environments = _environments.ToList();

        // Keep the single-value globals in step with the active tab for first-launch continuity.
        _state.LastCertThumbprint = ActiveRequest?.CertThumbprint;
        _state.IgnoreServerCertErrors = ActiveRequest?.IgnoreServerCert ?? false;
        _state.TimeoutSeconds = ActiveRequest?.TimeoutSeconds ?? 100;
        _state.LastBaseUrl = string.IsNullOrWhiteSpace(ActiveRequest?.BaseUrl) ? null : ActiveRequest!.BaseUrl!.Trim();
    }

    /// <summary>Persist the workspace and surface anything the user needs to know about the write.</summary>
    // Callers write a routine confirmation to StatusText BEFORE calling this, so a rare one-time
    // notice about the user's secrets (below) wins the status line rather than losing it.
    private void SaveState()
    {
        var result = _state.Save();
        if (result.BackupPath is not null)
            StatusText.Text = $"Secrets are now encrypted — your previous workspace file was copied to {Path.GetFileName(result.BackupPath)} first.";
        else if (!result.SecretsEncrypted)
            StatusText.Text = "Per-user encryption is unavailable on this system, so secrets were saved in the clear.";
    }

    private static bool IsOnScreen(double l, double t) =>
        l >= -50 && t >= -50 &&
        l < SystemParameters.VirtualScreenWidth - 100 && t < SystemParameters.VirtualScreenHeight - 100;

    // ---------- window chrome ----------

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = ((char)(maximized ? 0xE923 : 0xE922)).ToString(); // restore / maximize glyph (Segoe MDL2 Assets)
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        RootContainer.Margin = maximized ? new Thickness(7) : new Thickness(0);
    }

    private void HistoryToggle_Click(object sender, RoutedEventArgs e) => ToggleHistory();

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    // ---------- updates ----------

    private ReleaseInfo? _availableUpdate;
    private string? _restartInto;

    /// <summary>Whether the opt-in startup check is on — read and set by Help › About.</summary>
    internal bool CheckForUpdatesOnStart
    {
        get => _state.CheckForUpdatesOnStart;
        set { _state.CheckForUpdatesOnStart = value; SaveState(); }
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!AppUpdate.StartupCheckDue(_state, DateTime.UtcNow)) return;
        try
        {
            var newer = await AppUpdate.CheckAsync(_apiClient);
            _state.LastUpdateCheckUtc = DateTime.UtcNow;
            SaveState();
            if (newer is not null) ShowUpdateAvailable(newer);
        }
        catch (Exception)
        {
            // A startup check that fails says nothing — the user did not ask for it just now — and
            // must never take the app down with it (this is an async void handler).
        }
    }

    /// <summary>Help › About's "Check for updates": asks now and reports in words.</summary>
    internal async Task<string> CheckForUpdateNowAsync()
    {
        try
        {
            var newer = await AppUpdate.CheckAsync(_apiClient);
            _state.LastUpdateCheckUtc = DateTime.UtcNow;
            SaveState();
            if (newer is null) return $"certapi {AppUpdate.CurrentVersion} is the latest release.";
            ShowUpdateAvailable(newer);
            return $"certapi {newer.Version} is available (this is {AppUpdate.CurrentVersion}). Use the Update button in the title bar to install it.";
        }
        catch (Exception ex) { return "Could not check for updates: " + ex.Message; }
    }

    private void ShowUpdateAvailable(ReleaseInfo release)
    {
        _availableUpdate = release;
        UpdateButton.Content = $"Update to {release.Version}";
        UpdateButton.ToolTip = $"certapi {release.Version} is available — this is {AppUpdate.CurrentVersion}. Release notes: {release.Page}";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } release) return;
        // Installing ends with a restart, which would cut off a request that is still running.
        if (_cts is not null)
        {
            StatusText.Text = "A request is still running — let it finish (or cancel it), then install the update.";
            return;
        }
        string? exe = Environment.ProcessPath;
        if (AppUpdate.CannotInstall(exe) is { } why) { StatusText.Text = why; return; }
        bool withCli = File.Exists(Path.Combine(Path.GetDirectoryName(exe!) ?? ".", ReleaseUpdater.CliAsset));
        if (!ChoiceDialog.Confirm(this, $"Install certapi {release.Version}",
                $"Downloads ApiTester.App.exe{(withCli ? " and certapi.exe" : "")} from release {release.Tag}, checks each against " +
                "the SHA-256 GitHub publishes for it, and replaces the copies in this folder. Nothing changes if a check fails. " +
                $"The app restarts afterwards.\n\nRelease notes: {release.Page}",
                "Install and restart"))
            return;

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            await AppUpdate.InstallAsync(_apiClient, release, exe!, progress);
            StatusText.Text = $"certapi {release.Version} is installed. Restarting…";
            _restartInto = exe;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Update not installed: " + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // After the workspace is saved (OnClosing), so the new copy opens on everything as it was.
        if (_restartInto is { } exe)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { /* the new copy is in place; it opens next time */ }
        }
    }

    private void ShowHelp()
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
    }

    private void ToggleHistory() =>
        HistoryPanel.Visibility = HistoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // ---------- tabs ----------

    private void InitializeTabs()
    {
        IEnumerable<RequestModel> models = _state.Tabs.Count > 0
            ? _state.Tabs
            : new[]
            {
                new RequestModel
                {
                    IgnoreServerCert = _state.IgnoreServerCertErrors,
                    TimeoutSeconds = _state.TimeoutSeconds,
                    BaseUrl = _state.LastBaseUrl ?? "",
                    CertThumbprint = _state.LastCertThumbprint
                }
            };

        foreach (var m in models) _tabs.Add(new RequestTab(m));
        TabStrip.ItemsSource = _tabs;
        TabStrip.SelectedIndex = Math.Clamp(_state.ActiveTabIndex, 0, _tabs.Count - 1);
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        if (TabStrip.SelectedItem is not RequestTab newTab) return; // transient -1 during removal
        _switching = true;
        try
        {
            if (_loadedTab is { } old && !ReferenceEquals(old, newTab)) CaptureControlsInto(old.Request);
            LoadIntoControls(newTab.Request);
            _loadedTab = newTab;
            ShowTabResponse(newTab);
            BindNetwork(newTab);
            UpdateTokenChip();
        }
        finally { _switching = false; }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => AddNewTab();

    private RequestTab AddNewTab()
    {
        var m = new RequestModel
        {
            // Inherit the current base URL / cert / timeout so a new tab is ready to use.
            BaseUrl = ActiveRequest?.BaseUrl,
            CertThumbprint = ActiveRequest?.CertThumbprint,
            IgnoreServerCert = ActiveRequest?.IgnoreServerCert ?? false,
            TimeoutSeconds = ActiveRequest?.TimeoutSeconds ?? 100
        };
        var tab = new RequestTab(m);
        _tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        UrlBox.Focus();
        return tab;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RequestTab tab }) CloseTab(tab);
    }

    private void CloseTab(RequestTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        bool closingActive = ReferenceEquals(tab, ActiveTab);
        if (ReferenceEquals(tab, _loadedTab)) _loadedTab = null; // do not capture a tab we're discarding

        _tabs.Remove(tab);
        if (_tabs.Count == 0) _tabs.Add(new RequestTab(new RequestModel()));

        if (closingActive)
            TabStrip.SelectedIndex = Math.Clamp(idx, 0, _tabs.Count - 1);
    }

    /// <summary>Push a request model's values into the editor controls, via the UI-free
    /// <see cref="RequestEditorState"/> record.</summary>
    private void LoadIntoControls(RequestModel m)
    {
        _loading = true;
        try
        {
            var s = RequestEditorState.From(m);
            SelectMethod(s.Method);
            BaseUrlBox.Text = s.BaseUrlText;
            UrlBox.Text = s.UrlText;
            HeadersItems.ItemsSource = m.Headers;
            CaptureItems.ItemsSource = m.Captures;
            AssertItems.ItemsSource = m.Assertions;
            ParamsItems.ItemsSource = m.QueryParams;
            BodyBox.Text = s.BodyText;
            SelectContentType(s.ContentType);
            FormItems.ItemsSource = m.FormParts;
            BodyModeForm.IsChecked = s.BodyModeForm;
            BodyModeText.IsChecked = !s.BodyModeForm;
            UpdateBodyPanels();
            AuthTypeCombo.SelectedIndex = s.AuthTypeIndex;
            BearerTokenBox.Text = s.BearerTokenText;
            BasicUserBox.Text = s.BasicUserText;
            BasicPassBox.Text = s.BasicPassText;
            WindowsDefaultCheck.IsChecked = s.WindowsDefault;
            WindowsUserBox.Text = s.WindowsUserText;
            WindowsPassBox.Text = s.WindowsPassText;
            IgnoreServerCertCheck.IsChecked = s.IgnoreServerCert;
            TimeoutBox.Text = s.TimeoutText;
            SelectCertByThumbprint(s.CertThumbprint);
            TransportProxyCombo.SelectedIndex = s.ProxyIndex;
            TransportProxyUrlBox.Text = s.ProxyUrlText;
            TransportProxyUserBox.Text = s.ProxyUserText;
            TransportProxyPassBox.Text = s.ProxyPassText;
            TransportNoProxyBox.Text = s.NoProxyText;
            TransportRevocationCombo.SelectedIndex = s.RevocationIndex;
            TransportRevocationStrictCheck.IsChecked = s.RevocationStrict;
            TransportFollowRedirectsCheck.IsChecked = s.FollowRedirects;
            TransportMaxRedirsBox.Text = s.MaxRedirectsText;
            TransportDecompressCheck.IsChecked = s.Decompress;
            TransportVersionCombo.SelectedIndex = s.VersionIndex;
            TransportRetriesBox.Text = s.RetriesText;
            TransportRetryOnBox.Text = s.RetryOnText;
            TransportRetryDelayBox.Text = s.RetryDelayText;
            TransportRetryUnsafeCheck.IsChecked = s.RetryUnsafeMethods;
            TransportRetryTransportCheck.IsChecked = s.RetryOnTransportError;
            UpdateTransportPanels();
        }
        finally { _loading = false; }
    }

    /// <summary>The editor's controls as a UI-free value, ready for <see cref="RequestEditorState.ApplyTo"/>.</summary>
    private RequestEditorState CurrentEditorState() => new()
    {
        Method = SelectedMethod(),
        BaseUrlText = BaseUrlBox.Text,
        UrlText = UrlBox.Text,
        CertThumbprint = SelectedThumbprint(),
        BodyText = BodyBox.Text,
        ContentType = SelectedContentType(),
        BodyModeForm = BodyModeForm.IsChecked == true,
        AuthTypeIndex = AuthTypeCombo.SelectedIndex,
        BearerTokenText = BearerTokenBox.Text,
        BasicUserText = BasicUserBox.Text,
        BasicPassText = BasicPassBox.Text,
        WindowsDefault = WindowsDefaultCheck.IsChecked == true,
        WindowsUserText = WindowsUserBox.Text,
        WindowsPassText = WindowsPassBox.Text,
        IgnoreServerCert = IgnoreServerCertCheck.IsChecked == true,
        TimeoutText = TimeoutBox.Text,
        ProxyIndex = TransportProxyCombo.SelectedIndex,
        ProxyUrlText = TransportProxyUrlBox.Text,
        ProxyUserText = TransportProxyUserBox.Text,
        ProxyPassText = TransportProxyPassBox.Text,
        NoProxyText = TransportNoProxyBox.Text,
        RevocationIndex = TransportRevocationCombo.SelectedIndex,
        RevocationStrict = TransportRevocationStrictCheck.IsChecked == true,
        FollowRedirects = TransportFollowRedirectsCheck.IsChecked == true,
        MaxRedirectsText = TransportMaxRedirsBox.Text,
        Decompress = TransportDecompressCheck.IsChecked == true,
        VersionIndex = TransportVersionCombo.SelectedIndex,
        RetriesText = TransportRetriesBox.Text,
        RetryOnText = TransportRetryOnBox.Text,
        RetryDelayText = TransportRetryDelayBox.Text,
        RetryUnsafeMethods = TransportRetryUnsafeCheck.IsChecked == true,
        RetryOnTransportError = TransportRetryTransportCheck.IsChecked == true
    };

    /// <summary>Read the editor controls back into a request model.</summary>
    private void CaptureControlsInto(RequestModel m) => CurrentEditorState().ApplyTo(m);

    private void TransportProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;   // a programmatic load updates the panels itself
        UpdateTransportPanels();
    }

    private void TransportFollowRedirects_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateTransportPanels();
    }

    /// <summary>Show the explicit-proxy fields only for "Specific proxy…", and the hop limit only
    /// while redirects are actually being followed.</summary>
    private void UpdateTransportPanels()
    {
        if (TransportProxyPanel is null || TransportMaxRedirsPanel is null) return;   // during init
        TransportProxyPanel.Visibility =
            TransportProxyCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        TransportMaxRedirsPanel.Visibility =
            TransportFollowRedirectsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTabResponse(RequestTab tab)
    {
        if (tab.LastResponse is { } r) RenderResponse(r);
        else if (tab.Snapshot is { } s) RenderSnapshot(s, fromHistory: false);
        else ClearResponse();
    }

    // ---------- sidebar mode ----------

    private enum SidebarMode { History, Collections, Chains }

    private void ShowHistory_Click(object sender, RoutedEventArgs e) => SetSidebarMode(SidebarMode.History);
    private void ShowCollections_Click(object sender, RoutedEventArgs e) => SetSidebarMode(SidebarMode.Collections);
    private void ShowChains_Click(object sender, RoutedEventArgs e) => SetSidebarMode(SidebarMode.Chains);

    /// <summary>Show exactly one of the sidebar's three sections and mark its tab active. Clear is a
    /// history-only action, so it appears with the History section and nowhere else.</summary>
    private void SetSidebarMode(SidebarMode mode)
    {
        HistoryList.Visibility = mode == SidebarMode.History ? Visibility.Visible : Visibility.Collapsed;
        CollectionsArea.Visibility = mode == SidebarMode.Collections ? Visibility.Visible : Visibility.Collapsed;
        ChainsArea.Visibility = mode == SidebarMode.Chains ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = mode == SidebarMode.History ? Visibility.Visible : Visibility.Collapsed;
        HistoryTabButton.Tag = mode == SidebarMode.History ? "active" : null;
        CollectionsTabButton.Tag = mode == SidebarMode.Collections ? "active" : null;
        ChainsTabButton.Tag = mode == SidebarMode.Chains ? "active" : null;
    }

    // ---------- collections ----------

    private void InitializeCollections()
    {
        foreach (var n in _state.Collections) _collections.Add(n);
        CollectionsTree.ItemsSource = _collections;
        UpdateCollectionsHint();
    }

    private void UpdateCollectionsHint() =>
        CollectionsEmptyHint.Visibility = _collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The collection to add into: the selected folder, a selected request's parent, else the root.</summary>
    private ObservableCollection<CollectionNode> TargetFolder()
    {
        if (CollectionsTree.SelectedItem is CollectionNode sel)
            return sel.IsFolder ? sel.Children : FindParent(sel) ?? _collections;
        return _collections;
    }

    private ObservableCollection<CollectionNode>? FindParent(CollectionNode target, ObservableCollection<CollectionNode>? scope = null)
    {
        scope ??= _collections;
        foreach (var n in scope)
        {
            if (ReferenceEquals(n, target)) return scope;
            if (n.IsFolder)
            {
                var r = FindParent(target, n.Children);
                if (r is not null) return r;
            }
        }
        return null;
    }

    private static RequestModel CloneRequest(RequestModel m) =>
        RequestModel.FromHistoryEntry(m.ToHistoryEntry(null, null));

    private void SaveToCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRequest is not { } m) return;
        CaptureControlsInto(m);
        var def = string.IsNullOrWhiteSpace(m.Path) ? m.Method : $"{m.Method} {m.Path}";
        var name = InputDialog.Show(this, "Save request", "Name for this saved request", def);
        if (string.IsNullOrWhiteSpace(name)) return;

        var node = new CollectionNode { Name = name, IsFolder = false, Request = CloneRequest(m) };
        TargetFolder().Add(node);
        m.SourceCollectionId = node.Id;   // future sends of this tab record the endpoint's result
        SetSidebarMode(SidebarMode.Collections);
        UpdateCollectionsHint();
        StatusText.Text = $"Saved “{name}” to collections.";
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "New folder", "Folder name", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        TargetFolder().Add(new CollectionNode { Name = name, IsFolder = true });
        UpdateCollectionsHint();
    }

    private void RenameNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode sel) { StatusText.Text = "Select a collection item to rename."; return; }
        var name = InputDialog.Show(this, "Rename", sel.IsFolder ? "Folder name" : "Request name", sel.Name);
        if (!string.IsNullOrWhiteSpace(name)) sel.Name = name;
    }

    private void DeleteNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode sel) { StatusText.Text = "Select a collection item to delete."; return; }
        (FindParent(sel) ?? _collections).Remove(sel);
        UpdateCollectionsHint();
    }

    // ---------- chains ----------

    private void InitializeChains()
    {
        foreach (var c in _state.Chains) _chains.Add(c);
        ChainsList.ItemsSource = _chains;
        UpdateChainsHint();
    }

    private void UpdateChainsHint() =>
        ChainsEmptyHint.Visibility = _chains.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary><c>certapi run --chain &lt;name&gt;</c> resolves a chain by name, so two chains sharing one
    /// would make that command ambiguous. Reject the collision here rather than at the shell.</summary>
    private bool ChainNameTaken(string name, RequestChain? except = null) =>
        _chains.Any(c => !ReferenceEquals(c, except) &&
                         string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private void NewChainButton_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "New chain", "Name for this chain", "New chain");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ChainNameTaken(name)) { StatusText.Text = $"A chain named “{name}” already exists."; return; }

        var chain = new RequestChain { Name = name };
        _chains.Add(chain);
        ChainsList.SelectedItem = chain;
        UpdateChainsHint();

        // Straight into the step editor: an empty chain has no obvious next action otherwise.
        EditChain(chain);
    }

    private void RenameChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainsList.SelectedItem is not RequestChain chain) { StatusText.Text = "Select a chain to rename."; return; }
        var name = InputDialog.Show(this, "Rename chain", "Chain name", chain.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ChainNameTaken(name, chain)) { StatusText.Text = $"A chain named “{name}” already exists."; return; }

        chain.Name = name;
        ChainsList.Items.Refresh();   // RequestChain isn't observable
        StatusText.Text = $"Renamed chain to “{name}”.";
    }

    private void DeleteChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainsList.SelectedItem is not RequestChain chain) { StatusText.Text = "Select a chain to delete."; return; }
        if (!ChoiceDialog.Confirm(this, "Delete chain",
                $"Delete the chain “{chain.Name}”? The saved requests it runs are not affected.",
                "Delete")) return;

        _chains.Remove(chain);
        UpdateChainsHint();
        StatusText.Text = $"Deleted chain “{chain.Name}”.";
    }

    private void EditChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainsList.SelectedItem is RequestChain chain) EditChain(chain);
        else StatusText.Text = "Select a chain to edit its steps.";
    }

    private void ChainsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChainsList.SelectedItem is RequestChain chain) EditChain(chain);
    }

    /// <summary>Open the step editor on this chain. The editor edits the same instance the sidebar holds,
    /// so there is nothing to copy back — but RequestChain raises no change notification, so the row's
    /// step count has to be refreshed by hand once the editor closes.</summary>
    private void EditChain(RequestChain chain)
    {
        // The picker offers everything in _state.Collections, so sync the live sidebar into state first
        // (as Discover does) — otherwise requests saved this session wouldn't be offered.
        _state.Collections = _collections.ToList();
        _state.Chains = _chains.ToList();

        new ChainEditorWindow(chain, _state) { Owner = this }.ShowDialog();

        ChainsList.Items.Refresh();
        StatusText.Text = chain.Steps.Count == 0
            ? $"Chain “{chain.Name}” has no steps yet."
            : $"Chain “{chain.Name}”: {chain.Steps.Count} step(s). Run it with the Run chain button, or certapi run --chain \"{chain.Name}\".";
    }

    /// <summary>Run the selected chain in a window of its own, through the same Core runner
    /// <c>certapi run --chain</c> uses — there is one execution path, not an app copy of one.</summary>
    private void RunChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainsList.SelectedItem is not RequestChain chain) { StatusText.Text = "Select a chain to run."; return; }
        if (chain.Steps.Count == 0) { StatusText.Text = $"Chain “{chain.Name}” has no steps yet."; return; }
        if (ActiveRequest is { } m) CaptureControlsInto(m);

        // The runner resolves steps out of _state.Collections and writes captures into
        // _state.Environments, so sync the live sidebar into state first (as Discover does) —
        // otherwise requests or environments edited this session would not be seen.
        _state.Collections = _collections.ToList();
        _state.Chains = _chains.ToList();
        _state.Environments = _environments.ToList();

        // ShowDialog (modal) is deliberate: the runner mutates _state from a background thread, and
        // modality bounds that the same way the existing Discover window does.
        var win = new ChainRunWindow(_state, _apiClient, _trustPredicates, chain,
                                     thumbprint => _certService.FindByThumbprint(thumbprint, includeLocalMachine: true))
                  { Owner = this };
        win.ShowDialog();

        // A capture can lazily create an environment on the persisted list; fold it into the
        // UI-bound collection BEFORE the combo refreshes, or it silently vanishes from the picker.
        foreach (var env in _state.Environments)
            if (_environments.All(x => x.Id != env.Id)) _environments.Add(env);
        RefreshEnvCombo();
        // Known-good markers were recorded on the same CollectionNode instances the tree holds, but
        // those properties raise no change notification, so the dots need a nudge.
        CollectionsTree.Items.Refresh();
        if (win.Summary is { } summary) StatusText.Text = summary;
    }

    private void CopyChainCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainsList.SelectedItem is not RequestChain chain) { StatusText.Text = "Select a chain to copy its command."; return; }
        var command = $"certapi run --chain \"{chain.Name}\"";
        TrySetClipboard(command, "Copied: " + command);
    }

    private void CollectionsTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CollectionsTree.SelectedItem is CollectionNode { IsFolder: false, Request: { } req } node)
        {
            var clone = CloneRequest(req);
            clone.SourceCollectionId = node.Id;   // sends from this tab record the endpoint's result

            // Fill only the blanks: the saved request wins, then folder defaults, then the current tab.
            var (defBase, defCert) = CollectionDefaults.For(_collections, node);
            if (string.IsNullOrWhiteSpace(clone.BaseUrl))
                clone.BaseUrl = defBase ?? ActiveRequest?.BaseUrl;
            if (string.IsNullOrEmpty(clone.CertThumbprint))
                clone.CertThumbprint = defCert ?? SelectedThumbprint();

            var tab = new RequestTab(clone);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = $"Opened “{node.Name}” in a new tab.";
        }
    }

    private void CollectionsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CollectionsTree, e.OriginalSource as DependencyObject) is TreeViewItem item)
            item.IsSelected = true;
    }

    private void SetCollectionDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode { IsFolder: true } folder)
        {
            StatusText.Text = "Select a collection or folder to set defaults on.";
            return;
        }
        var options = _allOptions.Select(o => (o.Label, o.Thumbprint)).ToList();
        var (ok, baseUrl, thumb) = CollectionDefaultsDialog.Show(
            this, folder.Name, folder.DefaultBaseUrl, folder.DefaultCertThumbprint, options, _state.SavedBaseUrls);
        if (!ok) return;
        folder.DefaultBaseUrl = baseUrl;
        folder.DefaultCertThumbprint = thumb;
        StatusText.Text = baseUrl is null && thumb is null
            ? $"Cleared defaults for “{folder.Name}”."
            : $"Defaults for “{folder.Name}” saved.";
    }

    // ---------- response pop-out windows ----------

    private readonly Dictionary<TabItem, PopOutWindow> _popOuts = new();
    private PopOutWindow? _panelPopOut;

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (PopOutButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = PopOutButton;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void PopOutPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_panelPopOut is { } existing) { existing.Activate(); return; }
        if (ResponseHost.Content is not UIElement panel) return;

        ResponseHost.Content = BuildPanelPlaceholder();

        // Give the request editor the reclaimed space while the panel is away.
        ResponseSplitter.Visibility = Visibility.Collapsed;
        ResponseRow.MinHeight = 0;
        ResponseRow.Height = GridLength.Auto;

        var window = new PopOutWindow("Response", panel) { Owner = this };
        _panelPopOut = window;
        window.Closed += (_, _) =>
        {
            _panelPopOut = null;
            if (window.DetachContent() is { } returned) ResponseHost.Content = returned;
            ResponseSplitter.Visibility = Visibility.Visible;
            ResponseRow.MinHeight = 190;
            ResponseRow.Height = new GridLength(1.5, GridUnitType.Star);
            StatusText.Text = "The response panel is back in the main window.";
        };
        window.Show();
        StatusText.Text = "The response panel is now its own window — close it to bring it back.";
    }

    private UIElement BuildPanelPlaceholder()
    {
        var text = new TextBlock
        {
            Text = "The response panel is open in its own window.",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        var restore = new Button
        {
            Content = "Bring it back",
            Height = 24,
            FontSize = 11,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        restore.Click += (_, _) => _panelPopOut?.Close();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(text);
        row.Children.Add(restore);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Child = row
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        return border;
    }

    private void PopOutView_Click(object sender, RoutedEventArgs e)
    {
        if (ResponseTabs.SelectedItem is not TabItem tab) return;
        if (_popOuts.TryGetValue(tab, out var existing)) { existing.Activate(); return; }
        if (tab.Content is not UIElement content) return;

        string title = tab.Header?.ToString() ?? "Response";
        tab.Content = BuildPopOutPlaceholder(title, tab);   // replacing disconnects the old content

        var window = new PopOutWindow(title, content) { Owner = this };
        _popOuts[tab] = window;
        window.Closed += (_, _) =>
        {
            _popOuts.Remove(tab);
            if (window.DetachContent() is { } returned) tab.Content = returned;
            StatusText.Text = $"{title} view returned to the main window.";
        };
        window.Show();
        StatusText.Text = $"{title} is now its own window — close it to bring the view back.";
    }

    private UIElement BuildPopOutPlaceholder(string title, TabItem tab)
    {
        var text = new TextBlock
        {
            Text = $"The {title} view is open in its own window.",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        var restore = new Button
        {
            Content = "Bring it back",
            Height = 26,
            FontSize = 11,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        restore.Click += (_, _) => { if (_popOuts.TryGetValue(tab, out var w)) w.Close(); };

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        panel.Children.Add(restore);
        return panel;
    }

    // ---------- workspace save/load ----------

    private void ExportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRequest is { } active) CaptureControlsInto(active);
        CaptureStateForSave();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "cert-api-tester-workspace.json",
            DefaultExt = ".json",
            Filter = "Workspace JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Clone so the exported file carries no window geometry; SaveTo stamps the schema version.
            var clone = System.Text.Json.JsonSerializer.Deserialize<AppState>(
                System.Text.Json.JsonSerializer.Serialize(_state))!;
            clone.WindowWidth = clone.WindowHeight = clone.WindowLeft = clone.WindowTop = null;
            clone.WindowMaximized = false;
            clone.SaveTo(dialog.FileName);
            StatusText.Text = $"Workspace exported to {System.IO.Path.GetFileName(dialog.FileName)} — secrets in it are encrypted for your Windows user, so another user or machine will not be able to read them.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private void ImportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Workspace JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        AppState ws;
        try { ws = AppState.LoadFrom(dialog.FileName); }
        catch (Exception ex) { StatusText.Text = "Could not read the workspace file: " + ex.Message; return; }
        if (ws.Tabs.Count == 0 && ws.Collections.Count == 0 && ws.Environments.Count == 0 && ws.History.Count == 0
            && ws.Chains.Count == 0)
        {
            StatusText.Text = "That file doesn't contain a workspace.";
            return;
        }

        int saved = ws.Collections.Sum(CountNodes);
        var choice = ChoiceDialog.Show(this, "Import workspace",
            $"“{System.IO.Path.GetFileName(dialog.FileName)}” contains {ws.Tabs.Count} open tab(s), {saved} saved request(s), " +
            $"{ws.Environments.Count} environment(s), {ws.Chains.Count} chain(s), and {ws.History.Count} history " +
            $"{(ws.History.Count == 1 ? "entry" : "entries")}.\n\n" +
            "Merge adds them to what you have. Replace discards your current tabs, collections, environments, chains, and history first.",
            "Merge", "Replace");
        if (choice == DialogChoice.Cancel) return;

        ApplyWorkspace(ws, merge: choice == DialogChoice.Primary);
        if (ws.SecretWarnings.Count > 0)
            StatusText.Text += " " + ws.SecretWarnings[0] +
                (ws.SecretWarnings.Count > 1 ? $" (+{ws.SecretWarnings.Count - 1} more)" : "");
    }

    private static int CountNodes(CollectionNode n) => n.IsFolder ? n.Children.Sum(CountNodes) : 1;

    /// <summary>The workspace as it stands right now, as a plain value the pure merge can reason about.
    /// The live collections are the window's own, so this is a snapshot, not a copy of their contents.</summary>
    private AppState CurrentWorkspace() => new()
    {
        Tabs = _tabs.Select(t => t.Request).ToList(),
        Collections = _collections.ToList(),
        Environments = _environments.ToList(),
        Chains = _chains.ToList(),
        History = _state.History,
        SavedBaseUrls = _state.SavedBaseUrls,
        ActiveEnvironmentId = _state.ActiveEnvironmentId
    };

    private void ApplyWorkspace(AppState ws, bool merge)
    {
        var plan = WorkspaceImport.Plan(CurrentWorkspace(), ws, merge);

        if (plan.ClearExisting)
        {
            _loadedTab = null;
            _tabs.Clear();
            _collections.Clear();
            _environments.Clear();
            _chains.Clear();
        }

        foreach (var m in plan.TabsToAdd) _tabs.Add(new RequestTab(m));
        if (plan.NeedsBlankTab) AddNewTab();
        else if (plan.SelectTabIndex is { } idx) TabStrip.SelectedIndex = idx;

        foreach (var c in plan.CollectionsToAdd) _collections.Add(c);
        foreach (var env in plan.EnvironmentsToAdd) _environments.Add(env);
        // Export writes chains, so import has to read them back or a workspace moved between machines
        // loses every chain silently.
        foreach (var chain in plan.ChainsToAdd) _chains.Add(chain);

        _state.History.Clear();
        _state.History.AddRange(plan.History);
        _state.SavedBaseUrls.Clear();
        _state.SavedBaseUrls.AddRange(plan.SavedBaseUrls);
        _state.ActiveEnvironmentId = plan.ActiveEnvironmentId;

        RefreshHistoryList();
        RefreshSavedBases();
        RefreshEnvCombo();
        UpdateCollectionsHint();
        UpdateChainsHint();
        StatusText.Text = merge ? "Workspace merged into your current one." : "Workspace loaded.";
    }

    private void ExportCollectionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collections.Count == 0)
        {
            StatusText.Text = "Nothing to export — save a request to collections first.";
            return;
        }

        // Export the selected folder as its own document; otherwise everything.
        ParsedCollection pc;
        string defaultName;
        if (CollectionsTree.SelectedItem is CollectionNode { IsFolder: true } folder)
        {
            pc = folder.ToParsed();
            defaultName = folder.Name;
        }
        else
        {
            var wrapper = new CollectionNode { Name = "API collection", IsFolder = true, Children = _collections };
            pc = wrapper.ToParsed();
            defaultName = "collections";
        }

        int count = CountRequests(pc);
        if (count == 0)
        {
            StatusText.Text = "Nothing to export — the selection contains no saved requests.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName(defaultName) + ".openapi.json",
            DefaultExt = ".json",
            Filter = "OpenAPI JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, OpenApiExporter.ToJson(pc));
            StatusText.Text = $"Exported {count} request{(count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private static int CountRequests(ParsedCollection pc) =>
        pc.Requests.Count + pc.Folders.Sum(CountRequests);

    private static string SafeFileName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "collections" : name.Trim();
    }

    /// <summary>Find a collection node by id anywhere in the tree.</summary>
    private CollectionNode? FindNodeById(string id, ObservableCollection<CollectionNode>? scope = null)
    {
        scope ??= _collections;
        foreach (var n in scope)
        {
            if (n.Id == id) return n;
            if (n.IsFolder && FindNodeById(id, n.Children) is { } hit) return hit;
        }
        return null;
    }

    // ---------- environments & variables ----------

    private void InitializeEnvironments()
    {
        foreach (var env in _state.Environments) _environments.Add(env);
        RefreshEnvCombo();
    }

    private void RefreshEnvCombo()
    {
        _envLoading = true;
        var items = new List<string> { "— no environment —" };
        items.AddRange(_environments.Select(env => string.IsNullOrWhiteSpace(env.Name) ? "(unnamed)" : env.Name));
        EnvCombo.ItemsSource = items;

        int idx = 0;
        if (_state.ActiveEnvironmentId is { } id)
        {
            int found = _environments.ToList().FindIndex(env => env.Id == id);
            if (found >= 0) idx = found + 1;
            else _state.ActiveEnvironmentId = null; // active env was deleted
        }
        EnvCombo.SelectedIndex = idx;
        _envLoading = false;
    }

    private void EnvCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_envLoading) return;
        int i = EnvCombo.SelectedIndex;
        if (i <= 0 || i - 1 >= _environments.Count)
        {
            _state.ActiveEnvironmentId = null;
            StatusText.Text = "Environment: none.";
        }
        else
        {
            var env = _environments[i - 1];
            _state.ActiveEnvironmentId = env.Id;
            StatusText.Text = $"Environment: {(string.IsNullOrWhiteSpace(env.Name) ? "(unnamed)" : env.Name)}.";
        }
    }

    private void ManageEnvButton_Click(object sender, RoutedEventArgs e)
    {
        new EnvironmentsWindow(_environments) { Owner = this }.ShowDialog();
        RefreshEnvCombo();
    }

    // ---------- endpoint discovery ----------

    private void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRequest is { } m) CaptureControlsInto(m);
        // The Discover window appends any saved discoveries to _state.Collections, and we rebuild
        // the sidebar from it afterwards. Sync the live UI collections into _state first so that
        // rebuild reflects this session's edits too — otherwise unsaved top-level collection
        // changes would be reverted.
        _state.Collections = _collections.ToList();
        _state.Chains = _chains.ToList();
        var certs = _allOptions.Select(o => new FuzzWindow.CertChoice(o.Label, o.Cert, o.Thumbprint)).ToList();
        var win = new FuzzWindow(_state, _apiClient,
            ActiveRequest?.BaseUrl ?? BaseUrlBox.Text.Trim(),
            SelectedThumbprint(), certs,
            IgnoreServerCertCheck.IsChecked == true, ParseTimeout()) { Owner = this };
        win.ShowDialog();

        if (win.OpenRequested is { } d)
        {
            var model = new RequestModel { Method = d.Method, BaseUrl = d.BaseUrl, Path = d.Path, CertThumbprint = d.CertThumbprint };
            var tab = new RequestTab(model);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = $"Opened {d.Method} {d.Path} from discovery.";
        }
        if (win.DiscoveredSaved)
        {
            // Rebuild the collections view from the shared state so saved endpoints appear.
            _collections.Clear();
            foreach (var c in _state.Collections) _collections.Add(c);
            UpdateCollectionsHint();
            SetSidebarMode(SidebarMode.Collections);
        }
    }

    // ---------- import ----------

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = ImportButton;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void ImportCurl_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.ShowMultiline(this, "Import cURL",
            "Paste a curl command (Ctrl+Enter to import):");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var model = RequestModel.FromParsed(CurlParser.Parse(text));
            var tab = new RequestTab(model);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = "Imported request from cURL.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't parse that cURL command: " + ex.Message;
        }
    }

    private void ImportOpenApi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import OpenAPI / Swagger file",
            Filter = "OpenAPI / Swagger (JSON)|*.json|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var parsed = OpenApiImporter.Parse(System.IO.File.ReadAllText(dlg.FileName));
            var node = CollectionNode.FromParsed(parsed);
            _collections.Add(node);
            UpdateCollectionsHint();
            SetSidebarMode(SidebarMode.Collections);
            var count = CountRequests(node);
            StatusText.Text = count == 0
                ? $"Imported “{parsed.Name}”, but it defined no operations."
                : $"Imported {count} request(s) from “{parsed.Name}”.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't import that file: " + ex.Message;
        }
    }

    private static int CountRequests(CollectionNode n) =>
        (n.IsFolder ? 0 : 1) + n.Children.Sum(CountRequests);

    private void ImportHar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import HAR file",
            Filter = "HAR file (*.har)|*.har|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var har = HarReader.Parse(File.ReadAllText(dlg.FileName));
            var pc = new ParsedCollection { Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) };
            foreach (var entry in har.Log.Entries) pc.Requests.Add(HarReader.ToParsedRequest(entry));
            var node = CollectionNode.FromParsed(pc);
            _collections.Add(node);
            UpdateCollectionsHint();
            SetSidebarMode(SidebarMode.Collections);
            StatusText.Text = $"Imported {CountRequests(node)} request(s) from HAR.";
        }
        catch (HarFormatException ex)
        {
            StatusText.Text = "Couldn't import that HAR: " + ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't read that file: " + ex.Message;
        }
    }

    private void ExportNetworkHar_Click(object sender, RoutedEventArgs e)
    {
        var entries = NetworkList.Items.OfType<NetworkEntry>().ToList();
        if (entries.Count == 0)
        {
            StatusText.Text = "No network trace to export — send a request first.";
            return;
        }
        var choice = ChoiceDialog.Show(this, "Export Network trace as HAR",
            "Include Authorization/Cookie header values? They are redacted by default so the file is safe to share.",
            "Redact (recommended)", "Include values");
        if (choice == DialogChoice.Cancel) return;
        bool includeSecrets = choice == DialogChoice.Secondary;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "network-trace.har",
            Filter = "HAR file (*.har)|*.har|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, HarNetworkExport.ToHar(entries, includeSecrets, AppVersion()));
            StatusText.Text = $"Exported {entries.Count} call(s) to {System.IO.Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't write that file: " + ex.Message;
        }
    }

    private void ExportOpenApiFromHar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Export OpenAPI from HAR file",
            Filter = "HAR file (*.har)|*.har|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var har = HarReader.Parse(File.ReadAllText(dlg.FileName));
            var pc = HarToCollection.Group(har);
            int count = CountRequests(pc);
            if (count == 0)
            {
                StatusText.Text = "Nothing to export — every entry in that HAR was filtered out (responses of 400 and above are skipped).";
                return;
            }

            var saveDlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) + ".openapi.json",
                DefaultExt = ".json",
                Filter = "OpenAPI JSON (*.json)|*.json|All files (*.*)|*.*"
            };
            if (saveDlg.ShowDialog(this) != true) return;

            File.WriteAllText(saveDlg.FileName, OpenApiExporter.ToJson(pc));
            StatusText.Text = $"Exported {count} operation{(count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(saveDlg.FileName)}.";
        }
        catch (HarFormatException ex)
        {
            StatusText.Text = "Couldn't read that HAR: " + ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private void ManageTrustedCerts_Click(object sender, RoutedEventArgs e)
    {
        new TrustedCertsWindow(_state) { Owner = this }.ShowDialog();
    }

    private static string AppVersion()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var v = info ?? asm.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    // ---------- response diff ----------

    /// <summary>Populate the Diff view: the last response compared against a baseline — the HAR entry the
    /// user chose, else the saved request's own known-good response. Computed on demand when the tab is
    /// opened rather than after every send, because a diff nobody is looking at is wasted work.</summary>
    private void ShowDiff()
    {
        // An empty box shows its Tag placeholder, which already says what to do next.
        if (_lastResponse is null)
        {
            DiffBox.Text = "";
            DiffBaselineText.Text = _diffBaselineName is { } waiting
                ? $"Baseline: {waiting}. Send a request to compare it."
                : "Comparing against the saved request's known-good response.";
            return;
        }

        // The saved entry this tab came from, found the way the send path finds it to record a result.
        var node = ActiveRequest?.SourceCollectionId is { } srcId &&
                   FindNodeById(srcId) is { IsFolder: false, Request: not null } leaf
            ? leaf
            : null;

        var baseline = _diffBaseline ?? node?.KnownGood;
        if (baseline is null)
        {
            // A blank pane with no explanation is the worst outcome here: say which baseline is missing.
            DiffBox.Text = node is null
                ? "Nothing to compare against yet.\n\n" +
                  "This tab's request is not linked to a saved collection entry, so there is no known-good " +
                  "response on file. Save it into a collection and send it once — a 2xx response is kept as " +
                  "the baseline — or choose an HTTP Archive (.har) baseline with the button above."
                : $"Nothing to compare against yet.\n\n" +
                  $"“{node.Name}” has no known-good response recorded. Send it once from the " +
                  "collection, or run it with 'certapi run', and its 2xx response becomes the baseline. " +
                  "You can also choose an HTTP Archive (.har) baseline with the button above.";
            DiffBaselineText.Text = "No baseline recorded for this request yet.";
            return;
        }

        // Default options: the volatile header set ignored, no ignore paths. The app has no ignore-path
        // editor this release — that lives on the command line, so the default is the only choice here.
        var diff = ResponseDiff.Compare(baseline, ResponseSnapshot.From(_lastResponse), new DiffOptions());
        DiffBox.Text = DiffText.Format(diff);
        DiffBaselineText.Text = _diffBaselineName is { } name
            ? $"Compared against {name}."
            : $"Compared against known-good ({node!.Name}).";
    }

    /// <summary>Recompute the diff when the Diff tab is the one on screen. A send, or a click on a history
    /// entry, replaces the response without firing the tab's selection handler — and a diff describing the
    /// response that used to be there is worse than an empty pane.</summary>
    private void RefreshDiffIfVisible()
    {
        if (ResponseTabs.SelectedItem is TabItem { Header: "Diff" }) ShowDiff();
    }

    /// <summary>Pick a recorded exchange out of an HTTP Archive to diff this response against.</summary>
    private void DiffChooseHarButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a HAR baseline",
            Filter = "HTTP Archive (*.har)|*.har|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        Har har;
        try
        {
            har = HarReader.Parse(File.ReadAllText(dlg.FileName));
        }
        catch (HarFormatException ex)
        {
            StatusText.Text = "Couldn't read that HAR: " + ex.Message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Couldn't read that file: " + ex.Message;
            return;
        }

        var entries = har.Log.Entries;
        string method = ActiveRequest?.Method ?? "";
        string url = ActiveRequest?.EffectiveUrl() ?? "";

        // Prefer the entry that recorded this very request; fall back to the only entry a single-exchange
        // archive can mean. Anything else would be a guess, so it leaves the previous baseline alone.
        var match = entries.FirstOrDefault(en =>
                        string.Equals(en.Request.Method, method, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(en.Request.Url, url, StringComparison.OrdinalIgnoreCase))
                    ?? (entries.Count == 1 ? entries[0] : null);
        if (match is null)
        {
            StatusText.Text = $"That archive has no {method} entry for {url} — keeping the previous baseline.";
            return;
        }

        _diffBaseline = ResponseSnapshot.From(match);
        _diffBaselineName = System.IO.Path.GetFileName(dlg.FileName);
        ShowDiff();
    }

    private void DiffClearBaseline_Click(object sender, RoutedEventArgs e)
    {
        _diffBaseline = null;
        _diffBaselineName = null;
        ShowDiff();
    }

    /// <summary>The snapshot to keep as this request's known-good baseline, or null when the body is too
    /// large to belong in state.json — a settings file is not a blob store. RecordResult already refuses
    /// anything that is not a 2xx, so that check is not repeated here.</summary>
    private static ResponseSnapshot? KnownGoodSnapshot(ApiResponse response) =>
        response.Error is not null || response.Body.LongLength > MaxKnownGoodBody
            ? null
            : ResponseSnapshot.From(response);

    /// <summary>Deliberately separate from <c>MaxStoredBody</c>: history keeps thirty entries and wants a
    /// tight cap, while a baseline is one response per saved request and can afford more.</summary>
    private const long MaxKnownGoodBody = 1024 * 1024;   // 1 MiB

    // ---------- rendered website view ----------

    private void ResponseTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ResponseTabs)) return;
        if (ResponseTabs.SelectedItem is TabItem { Header: "Rendered" }) _ = ShowRenderedAsync();
        else if (ResponseTabs.SelectedItem is TabItem { Header: "Diff" }) ShowDiff();
    }

    private void RenderReloadButton_Click(object sender, RoutedEventArgs e) => _ = ShowRenderedAsync();

    private async Task ShowRenderedAsync()
    {
        if (ActiveRequest is not { } model) return;
        CaptureControlsInto(model);
        var (url, _, _, _) = ResolveActive();
        if (string.IsNullOrWhiteSpace(url))
        {
            RenderHint.Text = "Enter a URL in the request above, then reopen this tab to render the page.";
            RenderHint.Visibility = Visibility.Visible;
            return;
        }
        if (!await EnsureBrowserAsync()) return;

        _renderTraceTarget = ActiveTab;
        RebuildBrowserSession();
        RenderUrlText.Text = url;
        try
        {
            RenderHint.Visibility = Visibility.Collapsed;
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            RenderHint.Text = "Couldn't render that URL: " + ex.Message;
            RenderHint.Visibility = Visibility.Visible;
        }
    }

    private async Task<bool> EnsureBrowserAsync()
    {
        if (_browserReady) return true;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Browser_WebResourceRequested;
            _browserReady = true;
            return true;
        }
        catch (Exception ex)
        {
            RenderHint.Text = "The rendered view needs the Microsoft Edge WebView2 runtime, " +
                              "which couldn't be started on this machine:\n" + ex.Message;
            RenderHint.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void RebuildBrowserSession()
    {
        _browserSession?.Dispose();
        _browserCts?.Cancel();
        _browserCts?.Dispose();
        _browserCts = new CancellationTokenSource();
        var cert = SelectedCert();
        _renderCertSubject = cert?.Subject;
        _browserSession = new MtlsBrowserSession(cert, IgnoreServerCertCheck.IsChecked == true);
    }

    private void Browser_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_browserSession is not { } session || _browserCts is not { } cts) return;

        var deferral = e.GetDeferral();
        var request = e.Request;
        string method = request.Method;
        string uri = request.Uri;
        var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
        byte[]? body = ReadRequestContent(request.Content);
        _ = HandleResourceAsync(session, cts.Token, e, deferral, method, uri, headers, body);
    }

    private async Task HandleResourceAsync(
        MtlsBrowserSession session, CancellationToken ct,
        CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Deferral deferral,
        string method, string uri, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        try
        {
            var result = await session.FetchAsync(method, new Uri(uri), headers, body, null, ct);
            var headerLines = string.Join("\r\n", result.Headers
                .Where(h => Forwardable(h.Key))
                .Select(h => $"{h.Key}: {h.Value}"));
            e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(result.Body), result.StatusCode,
                string.IsNullOrEmpty(result.ReasonPhrase) ? "OK" : result.ReasonPhrase, headerLines);
            RecordNetwork(_renderTraceTarget, new NetworkEntry
            {
                Method = method,
                Url = uri,
                StatusCode = result.StatusCode,
                ReasonPhrase = result.ReasonPhrase,
                ContentType = result.ContentType,
                Size = result.Body?.LongLength ?? 0,
                ElapsedMs = result.ElapsedMs,
                ClientCertSubject = _renderCertSubject,
                Source = "Rendered",
                RequestHeaders = headers,
                ResponseHeaders = result.Headers ?? new List<KeyValuePair<string, string>>()
            });
        }
        catch (Exception ex)
        {
            bool light = ((App)Application.Current).CurrentTheme == "Light";
            string bg = light ? "#FFFFFF" : "#13151A";
            string fg = light ? "#C8324C" : "#FF6E7A";
            var bytes = Encoding.UTF8.GetBytes(
                $"<html><body style='font-family:'IBM Plex Mono',Consolas,monospace;background:{bg};color:{fg};padding:24px'>" +
                "<h2>Could not load this resource</h2><pre>" +
                System.Net.WebUtility.HtmlEncode(ex.Message) + "</pre></body></html>");
            e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes), 502, "Bad Gateway", "Content-Type: text/html; charset=utf-8");
            if (!ct.IsCancellationRequested)
                RecordNetwork(_renderTraceTarget, new NetworkEntry
                {
                    Method = method, Url = uri, Error = ex.Message,
                    ClientCertSubject = _renderCertSubject, Source = "Rendered", RequestHeaders = headers
                });
        }
        finally { deferral.Complete(); }
    }

    // Hop-by-hop / content-coding headers must not be forwarded (the body is already decompressed).
    private static bool Forwardable(string name) =>
        !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadRequestContent(Stream? content)
    {
        if (content is null) return null;
        try
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            return ms.Length == 0 ? null : ms.ToArray();
        }
        catch { return null; }
    }

    // ---------- network trace ----------

    private const int MaxNetworkEntries = 500;

    private string _networkClass = "All";
    private NetworkEntry? _networkDetailEntry;

    private void RecordNetwork(RequestTab? tab, NetworkEntry entry)
    {
        if (tab is null) return;
        tab.Network.Add(entry);
        while (tab.Network.Count > MaxNetworkEntries) tab.Network.RemoveAt(0);
        if (ReferenceEquals(tab, ActiveTab))
        {
            UpdateNetworkCount();
            if (NetworkList.Items.Count > 0)
                NetworkList.ScrollIntoView(NetworkList.Items[NetworkList.Items.Count - 1]);
        }
    }

    private void BindNetwork(RequestTab tab)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(tab.Network);
        view.Filter = NetworkRowVisible;
        NetworkList.ItemsSource = view;
        CloseNetworkDetail();
        UpdateNetworkCount();
    }

    private bool NetworkRowVisible(object item) =>
        item is NetworkEntry en &&
        en.Matches(NetworkSearchBox.Text, _networkClass, NetworkCertOnly.IsChecked == true);

    private void NetworkClass_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;
        _networkClass = clicked.Content?.ToString() ?? "All";
        foreach (var chip in new[] { NetClassAll, NetClass2xx, NetClass3xx, NetClass4xx, NetClass5xx, NetClassErr })
            chip.Tag = ReferenceEquals(chip, clicked) ? "active" : null;
        RefreshNetworkFilter();
    }

    private void NetworkSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshNetworkFilter();

    private void NetworkFilter_Changed(object sender, RoutedEventArgs e) => RefreshNetworkFilter();

    private void RefreshNetworkFilter()
    {
        if (NetworkList?.ItemsSource is System.ComponentModel.ICollectionView view) view.Refresh();
        UpdateNetworkCount();
    }

    private void UpdateNetworkCount()
    {
        if (NetworkCountText is null) return;
        int total = ActiveTab?.Network.Count ?? 0;
        if (total == 0)
        {
            NetworkCountText.Text = "No requests yet — send a request or open the Rendered tab.";
            return;
        }
        var shown = NetworkList.Items.OfType<NetworkEntry>().ToList();
        long size = shown.Sum(en => en.Error is null ? en.Size : 0);
        string counts = shown.Count == total
            ? $"{total} request{(total == 1 ? "" : "s")}"
            : $"{shown.Count} of {total} requests";
        NetworkCountText.Text = $"{counts} · {NetworkEntry.FormatSize(size)}";
    }

    private void ClearNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        ActiveTab?.Network.Clear();
        CloseNetworkDetail();
        UpdateNetworkCount();
    }

    private void NetworkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkList.SelectedItem is not NetworkEntry entry)
        {
            CloseNetworkDetail();
            return;
        }
        _networkDetailEntry = entry;
        NetworkDetailTitle.Text = $"{entry.Method}  {entry.StatusLabel}  ·  {entry.Url}";
        BuildNetworkDetailPane(entry);
        if (NetworkDetailPane.Visibility != Visibility.Visible)
        {
            NetworkDetailRow.MinHeight = 90;
            NetworkDetailRow.Height = new GridLength(190);
        }
        NetworkSplitter.Visibility = Visibility.Visible;
        NetworkDetailPane.Visibility = Visibility.Visible;
    }

    private void CloseNetworkDetail()
    {
        _networkDetailEntry = null;
        NetworkDetailPane.Visibility = Visibility.Collapsed;
        NetworkSplitter.Visibility = Visibility.Collapsed;
        NetworkDetailRow.MinHeight = 0;
        NetworkDetailRow.Height = GridLength.Auto;
        NetworkDetailHost.Children.Clear();
    }

    private void NetworkDetailClose_Click(object sender, RoutedEventArgs e) => NetworkList.SelectedItem = null;

    private void NetworkDetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_networkDetailEntry is { } entry) TrySetClipboard(BuildNetworkDetail(entry), "Copied request details.");
    }

    private void NetworkList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the row under the cursor so the context menu acts on it.
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not ListBoxItem)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        if (d is ListBoxItem item) item.IsSelected = true;
    }

    private void NetworkCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkList.SelectedItem is NetworkEntry entry) TrySetClipboard(entry.Url, "Copied URL.");
    }

    private void NetworkCopyCurl_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkList.SelectedItem is NetworkEntry entry) TrySetClipboard(entry.ToCurl(), "Copied cURL command.");
    }

    private void BuildNetworkDetailPane(NetworkEntry entry)
    {
        var host = NetworkDetailHost;
        host.Children.Clear();

        var statusBrush = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(
            entry, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.CurrentCulture);

        host.Children.Add(DetailCaption("GENERAL", first: true));
        host.Children.Add(DetailRow("URL", entry.Url));
        host.Children.Add(DetailRow("Status",
            entry.Error is not null ? "ERROR — " + entry.Error : $"{entry.StatusCode} {entry.ReasonPhrase}".Trim(),
            statusBrush));
        host.Children.Add(DetailRow("Type", string.IsNullOrEmpty(entry.ContentType) ? "—" : entry.ContentType));
        if (entry.Error is null)
        {
            host.Children.Add(DetailRow("Size", entry.SizeLabel));
            host.Children.Add(DetailRow("Time", entry.TimeLabel));
        }
        host.Children.Add(DetailRow("Started", entry.Timestamp.ToString("HH:mm:ss.fff")));
        host.Children.Add(DetailRow("Source", entry.Source == "Rendered" ? "Rendered page resource" : "Request you sent"));
        host.Children.Add(DetailRow("Client cert", ClientCertLine(entry)));

        if (entry.RequestHeaders.Count > 0)
        {
            host.Children.Add(DetailCaption($"REQUEST HEADERS ({entry.RequestHeaders.Count})"));
            foreach (var h in entry.RequestHeaders) host.Children.Add(DetailRow(h.Key, h.Value));
        }
        if (entry.ResponseHeaders.Count > 0)
        {
            host.Children.Add(DetailCaption($"RESPONSE HEADERS ({entry.ResponseHeaders.Count})"));
            foreach (var h in entry.ResponseHeaders) host.Children.Add(DetailRow(h.Key, h.Value));
        }
    }

    private static string ClientCertLine(NetworkEntry e)
    {
        if (e.ClientCertSubject is null) return "none";
        string how = e.Source == "Request"
            ? (e.ClientCertPresented ? " (presented to the server)" : " (offered; the server did not request it)")
            : " (via the mutual-TLS session)";
        return e.ClientCertSubject + how;
    }

    private TextBlock DetailCaption(string text, bool first = false) => new()
    {
        Text = text,
        Style = (Style)FindResource("Caption"),
        FontSize = 10,
        Margin = new Thickness(0, first ? 0 : 12, 0, 4)
    };

    private FrameworkElement DetailRow(string label, string value, System.Windows.Media.Brush? valueBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var key = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        };
        key.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        var val = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        if (valueBrush is not null) val.Foreground = valueBrush;
        else val.SetResourceReference(TextBlock.ForegroundProperty, "Text.Soft");
        Grid.SetColumn(val, 1);
        grid.Children.Add(key);
        grid.Children.Add(val);
        return grid;
    }

    private static string BuildNetworkDetail(NetworkEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Method} {e.Url}");
        sb.AppendLine();
        sb.AppendLine("Status      : " + (e.Error is not null ? "ERROR — " + e.Error : $"{e.StatusCode} {e.ReasonPhrase}"));
        sb.AppendLine("Type        : " + (e.ContentType ?? "—"));
        if (e.Error is null)
        {
            sb.AppendLine("Size        : " + e.SizeLabel);
            sb.AppendLine("Time        : " + e.TimeLabel);
        }
        sb.AppendLine("Started     : " + e.Timestamp.ToString("HH:mm:ss.fff"));
        sb.AppendLine("Source      : " + e.Source);
        sb.AppendLine("Client cert : " + ClientCertLine(e));

        if (e.RequestHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REQUEST HEADERS");
            foreach (var h in e.RequestHeaders) sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        if (e.ResponseHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RESPONSE HEADERS");
            foreach (var h in e.ResponseHeaders) sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        return sb.ToString();
    }

    // ---------- certificates ----------

    private void LoadCertificates()
    {
        _certs = _certService.ListClientCertificates();
        _allOptions = new List<CertOption> { new("— no certificate —", null, null) };
        foreach (var c in _certs)
        {
            // Expired is the loud badge it always was; expiring soon gets its own, so the row that
            // stops working in a fortnight says so while there is still time to renew it.
            var expiry = c.IsExpired() ? " [EXPIRED]"
                : c.IsExpiringSoon() ? $" [EXPIRES IN {c.DaysUntilExpiry()}d]"
                : "";
            var eku = c.HasClientAuthEku ? "" : " (no client-auth EKU)";
            _allOptions.Add(new CertOption($"{c.Subject}  —  {c.Thumbprint}{eku}{expiry}", c.Certificate, c.Thumbprint));
        }
        ApplyCertFilter();
        StatusText.Text = _certs.Count == 0
            ? "No client certificates found — you can still test APIs that don't require one."
            : $"{_certs.Count} certificate(s) available — pick one for mutual-TLS, or keep “no certificate”.";
    }

    private void ApplyCertFilter()
    {
        var f = CertFilter.Text?.Trim() ?? "";
        _visibleOptions = string.IsNullOrEmpty(f)
            ? _allOptions
            : _allOptions.Where((o, i) => i == 0 || o.Label.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        CertCombo.ItemsSource = _visibleOptions.Select(o => o.Label).ToList();
        if (CertCombo.SelectedIndex < 0 && _visibleOptions.Count > 0) CertCombo.SelectedIndex = 0;
    }

    private void CertFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allOptions.Count == 0) return;
        ApplyCertFilter();
    }

    private void RefreshCertsButton_Click(object sender, RoutedEventArgs e) => LoadCertificates();

    /// <summary>Load a client certificate from a file (.pfx/.p12 or .pem/.crt) into the picker for
    /// this session, for endpoints whose certificate isn't in the Windows store.</summary>
    private void LoadCertFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load client certificate",
            Filter = "Certificate (*.pfx;*.p12;*.pem;*.crt)|*.pfx;*.p12;*.pem;*.crt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;

        X509Certificate2 cert;
        try
        {
            cert = CertificateFileLoader.Load(path);
        }
        catch (CertificateFileException ex)
        {
            // A .pfx/.p12 usually needs a password — prompt once and retry.
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".pfx" or ".p12")) { StatusText.Text = ex.Message; return; }
            var pw = InputDialog.Show(this, "Certificate password",
                $"Password for {System.IO.Path.GetFileName(path)}", "");
            if (string.IsNullOrEmpty(pw)) { StatusText.Text = ex.Message; return; }
            try { cert = CertificateFileLoader.Load(path, pw); }
            catch (CertificateFileException ex2) { StatusText.Text = ex2.Message; return; }
        }

        string eku = HasClientAuthEku(cert) ? "" : " (no client-auth EKU)";
        var option = new CertOption($"{cert.Subject}  —  {cert.Thumbprint}{eku}  (from file)", cert, cert.Thumbprint);
        _allOptions.Add(option);
        ApplyCertFilter();
        SelectCertByThumbprint(cert.Thumbprint);
        StatusText.Text = $"Loaded {System.IO.Path.GetFileName(path)} for this session — it stays until you press Refresh.";
    }

    private static bool HasClientAuthEku(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            foreach (var oid in ext.EnhancedKeyUsages)
                if (oid.Value == "1.3.6.1.5.5.7.3.2") return true;
        return false;
    }

    private X509Certificate2? SelectedCert()
    {
        int i = CertCombo.SelectedIndex;
        return i >= 0 && i < _visibleOptions.Count ? _visibleOptions[i].Cert : null;
    }

    private string? SelectedThumbprint()
    {
        int i = CertCombo.SelectedIndex;
        return i >= 0 && i < _visibleOptions.Count ? _visibleOptions[i].Thumbprint : null;
    }

    private void SelectCertByThumbprint(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) { CertCombo.SelectedIndex = 0; return; }
        int idx = _visibleOptions.FindIndex(o => o.Thumbprint == thumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ---------- session token chip ----------

    /// <summary>The URL the editor currently points at (base + path), without touching the model.</summary>
    private string CurrentEditorUrl() => UrlHelper.Combine(BaseUrlBox.Text, UrlBox.Text);

    private void UpdateTokenChip()
    {
        var url = CurrentEditorUrl();
        var origin = TokenService.OriginOf(url);
        var token = origin is null ? null : _state.SessionTokens.FirstOrDefault(t => t.Origin == origin);
        int cookieCount = origin is null ? 0 : _state.SessionCookies.Count(c => c.Origin == origin && !c.IsExpired);
        if (token is null && cookieCount == 0) { TokenChip.Visibility = Visibility.Collapsed; return; }

        TokenChip.Visibility = Visibility.Visible;
        if (token is null)
        {
            string cookieSuffix = _state.AutoCookies ? "" : " · auto off";
            TokenChipText.Text = $"Session: {new Uri(url).Host} · {cookieCount} cookie(s){cookieSuffix}";
            return;
        }

        string suffix =
            !_state.AutoTokens ? " · auto off"
            : token.IsExpired ? " · expired"
            : token.ExpiresUtc is { } e ? $" · expires in {Math.Max(1, (int)(e - DateTime.UtcNow).TotalMinutes)}m"
            : "";
        string cookieBadge = cookieCount > 0 ? $" + {cookieCount} cookie(s)" : "";
        TokenChipText.Text = $"Token: {new Uri(url).Host}{suffix}{cookieBadge}";
    }

    private void TokenChip_Click(object sender, MouseButtonEventArgs e)
    {
        var url = CurrentEditorUrl();
        var origin = TokenService.OriginOf(url);
        var token = origin is null ? null : _state.SessionTokens.FirstOrDefault(t => t.Origin == origin);
        int cookieCount = origin is null ? 0 : _state.SessionCookies.Count(c => c.Origin == origin && !c.IsExpired);
        if (token is null && cookieCount == 0) { UpdateTokenChip(); return; }

        var menu = new ContextMenu();

        if (token is not null)
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"{token.Source} · captured {token.CapturedUtc.ToLocalTime():HH:mm}" +
                         (token.ExpiresUtc is { } ex ? $" · expires {ex.ToLocalTime():HH:mm}" : ""),
                IsEnabled = false
            });
            menu.Items.Add(new Separator());

            var clearOne = new MenuItem { Header = $"Clear token for {new Uri(url).Host}" };
            clearOne.Click += (_, _) => { _state.SessionTokens.Remove(token); UpdateTokenChip(); };
            menu.Items.Add(clearOne);

            var clearAll = new MenuItem { Header = "Clear all captured tokens" };
            clearAll.Click += (_, _) => { _state.SessionTokens.Clear(); UpdateTokenChip(); };
            menu.Items.Add(clearAll);

            menu.Items.Add(new Separator());
            var toggle = new MenuItem { Header = "Automatically use captured tokens", IsCheckable = true, IsChecked = _state.AutoTokens };
            toggle.Click += (_, _) => { _state.AutoTokens = toggle.IsChecked; UpdateTokenChip(); };
            menu.Items.Add(toggle);
        }

        if (cookieCount > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var clearCookies = new MenuItem { Header = $"Clear {cookieCount} captured cookie(s) for {new Uri(url).Host}" };
            clearCookies.Click += (_, _) => { _state.SessionCookies.RemoveAll(c => c.Origin == origin); UpdateTokenChip(); };
            menu.Items.Add(clearCookies);

            var cookieToggle = new MenuItem { Header = "Automatically use captured cookies", IsCheckable = true, IsChecked = _state.AutoCookies };
            cookieToggle.Click += (_, _) => { _state.AutoCookies = cookieToggle.IsChecked; UpdateTokenChip(); };
            menu.Items.Add(cookieToggle);
        }

        menu.PlacementTarget = TokenChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ---------- website (base URL) ----------

    private void RefreshSavedBases()
    {
        var items = new List<string> { "— saved websites —" };
        items.AddRange(_state.SavedBaseUrls);
        SavedBasesCombo.ItemsSource = items;
        SavedBasesCombo.SelectedIndex = 0;
    }

    private void SavedBasesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedBasesCombo.SelectedIndex > 0 && SavedBasesCombo.SelectedItem is string s)
        {
            BaseUrlBox.Text = s;
            SavedBasesCombo.SelectedIndex = 0;
        }
    }

    private void ForgetBaseButton_Click(object sender, RoutedEventArgs e)
    {
        var b = BaseUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(b) && _state.SavedBaseUrls.Remove(b))
        {
            RefreshSavedBases();
            StatusText.Text = $"Forgot website {b}.";
        }
    }

    private void SaveBaseUrl()
    {
        var b = BaseUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(b) && !_state.SavedBaseUrls.Contains(b, StringComparer.OrdinalIgnoreCase))
        {
            _state.SavedBaseUrls.Insert(0, b);
            if (_state.SavedBaseUrls.Count > 20) _state.SavedBaseUrls.RemoveRange(20, _state.SavedBaseUrls.Count - 20);
            RefreshSavedBases();
        }
    }

    private void BaseUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TokenChip is null) return; // during init
        UpdateTokenChip();
    }

    // ---------- headers / params / auth ----------

    private void AddHeaderButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.Headers.Add(new HeaderRow());

    private void RemoveHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HeaderRow row }) ActiveRequest?.Headers.Remove(row);
    }

    private void AddCaptureButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.Captures.Add(new CaptureRule());

    private void RemoveCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CaptureRule row }) ActiveRequest?.Captures.Remove(row);
    }

    /// <summary>After a send, evaluate the request's assertions: append a pass/fail summary to the
    /// status line and list each result at the top of the Diagnostics view.</summary>
    private void ShowAssertionResults(RequestModel model, ApiResponse response)
    {
        if (!model.Assertions.Any(a => a.Enabled)) return;
        var results = AssertionEvaluator.Evaluate(model.Assertions, response);
        int passed = results.Count(r => r.Passed);
        int total = results.Count;
        StatusText.Text += $"   {(passed == total ? "✓" : "✗")} tests {passed}/{total} passed";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ASSERTIONS  ({passed}/{total} passed)");
        foreach (var r in results)
            sb.AppendLine($"  {(r.Passed ? "✓" : "✗")} {r.Description}" + (r.Passed ? "" : $"   → got {r.Actual ?? "∅"}"));
        DiagnosticsBox.Text = sb.ToString().TrimEnd() +
            (string.IsNullOrEmpty(DiagnosticsBox.Text) ? "" : "\n\n" + DiagnosticsBox.Text);
    }

    // ---------- multipart form body ----------

    private void BodyMode_Changed(object sender, RoutedEventArgs e) => UpdateBodyPanels();

    private void UpdateBodyPanels()
    {
        if (TextBodyPanel is null || FormBodyPanel is null) return;   // during initial load
        bool form = BodyModeForm.IsChecked == true;
        TextBodyPanel.Visibility = form ? Visibility.Collapsed : Visibility.Visible;
        FormBodyPanel.Visibility = form ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFormPart_Click(object sender, RoutedEventArgs e) => ActiveRequest?.FormParts.Add(new FormPart());

    private void RemoveFormPart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FormPart row }) ActiveRequest?.FormParts.Remove(row);
    }

    private void BrowseFormFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FormPart row }) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a file to upload" };
        if (dlg.ShowDialog(this) == true) { row.Value = dlg.FileName; row.IsFile = true; }
    }

    private void AddAssertionButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.Assertions.Add(new AssertionRule());

    private void RemoveAssertionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AssertionRule row }) ActiveRequest?.Assertions.Remove(row);
    }

    private void AddParamButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.QueryParams.Add(new ParamRow());

    private void RemoveParamButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ParamRow row }) ActiveRequest?.QueryParams.Remove(row);
    }

    private void AuthTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BearerPanel is null) return; // during init
        AutoAuthHint.Visibility = AuthTypeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        BearerPanel.Visibility = AuthTypeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        BasicPanel.Visibility = AuthTypeCombo.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        WindowsPanel.Visibility = AuthTypeCombo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WindowsDefaultCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (WindowsCredsPanel is null) return; // during init
        WindowsCredsPanel.Visibility = WindowsDefaultCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- send ----------

    private int ParseTimeout() => RequestEditorState.ParseTimeoutSeconds(TimeoutBox.Text);

    private List<KeyValuePair<string, string>> BuildHeaders()
    {
        var m = ActiveRequest!;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in m.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                headers.Add(new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? ""));

        switch (m.AuthType)
        {
            case "Bearer" when !string.IsNullOrWhiteSpace(m.AuthSecret):
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + m.AuthSecret!.Trim()));
                break;
            case "Basic":
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{m.AuthUser}:{m.AuthSecret}"));
                headers.Add(new KeyValuePair<string, string>("Authorization", "Basic " + basic));
                break;
        }
        return headers;
    }

    private ApiRequest BuildRequest()
    {
        var m = ActiveRequest!;
        var (url, headers, body, unresolved) = ResolveActive();
        if (m.AuthType == "Auto")
            TokenService.AutoAttach(_state, url, headers, out _);
        _unresolvedVars = unresolved;
        string? contentType = !m.IsMultipart && body is not null && m.ContentType != "(none)" ? m.ContentType : null;

        var vars = CurrentVars();
        string R(string s) => VariableResolver.Resolve(s ?? "", vars).Result;
        var parts = m.IsMultipart
            ? m.EnabledParts().Select(p => p with { Name = R(p.Name), Value = p.Value is null ? null : R(p.Value) }).ToList()
            : null;

        return new ApiRequest
        {
            Method = new HttpMethod(m.Method),
            Url = url,
            Headers = headers,
            Body = m.IsMultipart ? null : body,
            Parts = parts,
            ContentType = contentType,
            WindowsAuth = m.AuthType == "Windows"
                ? WindowsAuthOptions.FromCredentials(R(m.AuthUser ?? ""), R(m.AuthSecret ?? ""))
                : null,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds)
        };
    }

    /// <summary>Build the current environment's variable map.</summary>
    private Dictionary<string, string> CurrentVars()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = _environments.FirstOrDefault(e => e.Id == _state.ActiveEnvironmentId);
        if (env is not null)
            foreach (var v in env.Variables)
                if (!string.IsNullOrWhiteSpace(v.Key)) d[v.Key.Trim()] = v.Value ?? "";
        return d;
    }

    /// <summary>Resolve the active request's URL, headers, and body against the current
    /// environment's <c>{{variables}}</c>, returning any tokens that couldn't be resolved. With
    /// <paramref name="preserveVariables"/>, a token the environment could not resolve stays literal,
    /// because the caller is producing an export rather than a request to send.</summary>
    private (string Url, List<KeyValuePair<string, string>> Headers, string? Body, List<string> Unresolved) ResolveActive(bool preserveVariables = false)
    {
        var m = ActiveRequest!;
        var vars = CurrentVars();
        var unresolved = new List<string>();
        string R(string s)
        {
            var (r, u) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var x in u) if (!unresolved.Contains(x)) unresolved.Add(x);
            return r;
        }

        var url = preserveVariables ? m.ExportUrl(R) : m.EffectiveUrl(R);
        var headers = BuildHeaders().Select(h => new KeyValuePair<string, string>(R(h.Key), R(h.Value))).ToList();
        var body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body);
        return (url, headers, body, unresolved);
    }

    private void StreamButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new StreamWindow(CurrentEditorUrl(), SelectedCert(), IgnoreServerCertCheck.IsChecked == true)
        {
            Owner = this
        };
        win.Show();
    }

    private void GetOAuthToken_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OAuthWindow(SelectedCert(), IgnoreServerCertCheck.IsChecked == true, CurrentEditorUrl()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result?.AccessToken is not { } access) return;

        // Store it for the target origin so Auto auth attaches it to later sends there.
        string? host = null;
        if (TokenService.OriginOf(dlg.ApplyToUrl) is { } origin)
        {
            _state.SessionTokens.RemoveAll(t => t.Origin == origin);
            _state.SessionTokens.Add(new SessionToken
            {
                Origin = origin, Token = access, Source = "oauth",
                CapturedUtc = DateTime.UtcNow, ExpiresUtc = dlg.Result.ExpiresUtc
            });
            host = TokenService.HostOf(dlg.ApplyToUrl);
            UpdateTokenChip();
        }

        // Also surface it in the Bearer field for immediate use.
        BearerTokenBox.Text = access;
        AuthTypeCombo.SelectedIndex = 2; // Bearer token
        StatusText.Text = host is not null
            ? $"OAuth token stored for {host} and set as the bearer token."
            : "OAuth token set as the bearer token.";
    }

    private MockServerWindow? _mockWindow;

    private void MockServerButton_Click(object sender, RoutedEventArgs e)
    {
        // Reuse the existing window if it's already open rather than stacking servers.
        if (_mockWindow is { IsLoaded: true })
        {
            _mockWindow.Activate();
            return;
        }
        _mockWindow = new MockServerWindow { Owner = this };
        _mockWindow.Closed += (_, _) => _mockWindow = null;
        _mockWindow.Show();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendRequestAsync();

    private async System.Threading.Tasks.Task SendRequestAsync()
    {
        if (!SendButton.IsEnabled) return;
        if (ActiveRequest is not { } model) return;
        CaptureControlsInto(model);
        if (string.IsNullOrWhiteSpace(model.EffectiveUrl())) { StatusText.Text = "Enter a URL."; return; }
        SaveBaseUrl();

        var cert = SelectedCert();
        var request = BuildRequest();
        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        SendButton.Content = "Sending…";
        CancelButton.IsEnabled = true;
        StatusText.Text = "Sending…";
        SendProgress.Visibility = Visibility.Visible;
        ShowWaitingHint();
        ApiResponse? response = null;
        try
        {
            // Attach any browser-captured session cookies for this origin before sending.
            CookieService.SeedContainer(_state, request.Url, _cookieJar);
            string host = TokenService.HostOf(request.Url);
            response = await _apiClient.SendAsync(
                request, cert,
                transport: model.Transport.ToOptions(model.IgnoreServerCert),
                trustServerCertificate: _trustPredicates.For(host),
                cookies: _cookieJar, cancellationToken: _cts.Token);
            RenderResponse(response);
            ShowAssertionResults(model, response);
            if (_unresolvedVars.Count > 0)
                StatusText.Text += "   ⚠ unresolved " +
                    string.Join(", ", _unresolvedVars.Select(v => "{{" + v + "}}"));
            if (ActiveTab is { } tab)
            {
                tab.LastResponse = response;
                tab.LastRawText = _lastRawText;
                tab.Snapshot = BuildSnapshot(response);
                // Each redirect was a request of its own: trace them oldest-first, ahead of the
                // final response, so the Network view reads the way a browser's does.
                foreach (var hop in TransportDiagnostics.RedirectEntries(request, response))
                    RecordNetwork(tab, hop);
                RecordNetwork(tab, new NetworkEntry
                {
                    Method = request.Method.Method,
                    Url = request.Url,
                    StatusCode = response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    ContentType = response.ContentType,
                    Size = response.Body?.LongLength ?? 0,
                    ElapsedMs = response.Elapsed.TotalMilliseconds,
                    ClientCertSubject = response.Connection?.ClientCertificateSubject,
                    ClientCertPresented = response.Connection?.ClientCertificateSent ?? false,
                    Error = response.Error?.Message,
                    Source = "Request",
                    RequestHeaders = (request.Headers ?? new List<KeyValuePair<string, string>>())
                        .Select(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                            ? new KeyValuePair<string, string>(h.Key, TokenService.MaskAuthorization(h.Value))
                            : h).ToList(),
                    ResponseHeaders = response.Headers?.ToList() ?? new List<KeyValuePair<string, string>>()
                });
            }
            // Record the outcome on the collection entry this tab came from — but only while the
            // tab still targets that saved endpoint (same method and URL).
            if (model.SourceCollectionId is { } srcId &&
                FindNodeById(srcId) is { IsFolder: false, Request: { } saved } srcNode &&
                saved.Method == model.Method && saved.EffectiveUrl() == model.EffectiveUrl())
                srcNode.RecordResult(response.Error is null ? response.StatusCode : null, DateTime.UtcNow,
                                     KnownGoodSnapshot(response));
            // First successful send from a collection whose root has no defaults yet: remember
            // the website and certificate that worked, so sibling endpoints inherit them.
            if (response.Error is null && model.SourceCollectionId is { } rememberId &&
                FindNodeById(rememberId) is { IsFolder: false } rememberLeaf &&
                CollectionDefaults.RootOf(_collections, rememberLeaf) is { } rootFolder &&
                string.IsNullOrWhiteSpace(rootFolder.DefaultBaseUrl) &&
                string.IsNullOrEmpty(rootFolder.DefaultCertThumbprint) &&
                (!string.IsNullOrWhiteSpace(model.BaseUrl) || !string.IsNullOrEmpty(model.CertThumbprint)))
            {
                rootFolder.DefaultBaseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? null : model.BaseUrl!.Trim();
                rootFolder.DefaultCertThumbprint = string.IsNullOrEmpty(model.CertThumbprint) ? null : model.CertThumbprint;
                StatusText.Text += $"   Remembered website & certificate for “{rootFolder.Name}”.";
            }
            if (response.Error is null)
                TokenService.Capture(_state, request.Url, response.Body ?? Array.Empty<byte>(),
                    response.ContentType, response.Headers ?? new List<KeyValuePair<string, string>>());
            UpdateTokenChip();
            if (response.Error is null && model.Captures.Count > 0)
                ApplyCaptures(model, response);
            AddToHistory(response);
        }
        finally
        {
            SendButton.IsEnabled = true;
            SendButton.Content = "Send";
            CancelButton.IsEnabled = false;
            SendProgress.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }

        // The server presented a certificate the trust store doesn't recognize — offer to pin it
        // for this host and retry once. The IsTrusted guard prevents a retry loop: after Trust()
        // this thumbprint is trusted, so a second failure could only be a *different* certificate.
        if (response?.Error?.Kind == ApiErrorKind.ServerCertificateUntrusted &&
            response.Connection?.ServerCertificateThumbprint is { } thumb)
        {
            string host = TokenService.HostOf(request.Url);
            if (!TrustService.IsTrusted(_state, host, thumb))
            {
                var c = ChoiceDialog.Show(this, "Untrusted server certificate",
                    $"{host} presented a certificate that isn't trusted:\n{response.Connection.ServerCertificateSubject}\n{thumb}\n\nTrust it for this host and retry?",
                    "Trust & retry", "Cancel");
                if (c == DialogChoice.Primary)
                {
                    TrustService.Trust(_state, host, thumb, response.Connection.ServerCertificateSubject);
                    SaveState();
                    await SendRequestAsync();
                }
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- response rendering ----------

    private void RenderResponse(ApiResponse response)
    {
        _lastResponse = response;
        RefreshDiffIfVisible();
        DiagnosticsBox.Text = TransportDiagnostics.Format(response);

        if (response.Error is not null)
        {
            _lastRawText = response.Error.Message;
            StatusText.Text = response.Error.Kind == ApiErrorKind.None
                ? response.Error.Message
                : $"Error [{response.Error.Kind}]: {response.Error.Message}";
            SetPretty(response.Error.Message, BodyKind.Text);
            RawBox.Text = response.Error.Message;
            ResponseHeadersBox.Text = "";
            return;
        }

        var formatted = _formatter.Format(response);
        SetPretty(formatted.Text, formatted.Kind);
        _lastRawText = Encoding.UTF8.GetString(response.Body);
        RawBox.Text = _lastRawText;
        ResponseHeadersBox.Text = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {h.Value}"));

        var tls = response.Connection?.TlsProtocol is { } p ? $"  •  {p}" : "";
        StatusText.Text =
            $"{response.StatusCode} {response.ReasonPhrase}  •  {response.Body.Length} bytes  •  " +
            $"{response.Elapsed.TotalMilliseconds:F0} ms  •  {formatted.Kind}{tls}";
    }

    private void SetPretty(string text, BodyKind kind)
    {
        _lastPretty = (text, kind);
        PrettyRich.Document = SyntaxHighlighter.Build(text, kind);
    }

    // ---------- copy / save ----------

    private void CopyBodyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastRawText)) { StatusText.Text = "Nothing to copy."; return; }
        TrySetClipboard(_lastRawText, "Copied response body.");
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } cm } b)
        {
            cm.PlacementTarget = b;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void CopyAs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string lang } item) return;
        if (CurrentCodeRequest() is not { } req) { StatusText.Text = "Enter a URL first."; return; }
        TrySetClipboard(CodeGenerator.Generate(lang, req), $"Copied as {item.Header}.");
    }

    /// <summary>The active request, as a language-agnostic code spec, with variables resolved where
    /// possible; any token that could not be resolved stays literal.</summary>
    private CodeRequest? CurrentCodeRequest()
    {
        if (ActiveRequest is not { } model) return null;
        CaptureControlsInto(model);
        var (url, headers, body, _) = ResolveActive(preserveVariables: true);
        if (string.IsNullOrWhiteSpace(url)) return null;
        return new CodeRequest(model.Method, url, headers, body, model.IgnoreServerCert, model.CertThumbprint);
    }

    private void TrySetClipboard(string text, string ok)
    {
        try { Clipboard.SetText(text); StatusText.Text = ok; }
        catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
    }

    private void SaveResponseButton_Click(object sender, RoutedEventArgs e) => SaveResponse();

    private void ResponseFindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; FindInResponse(); }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindInResponse();

    /// <summary>Find the next occurrence of the search term in the response body (the Raw view holds
    /// the full text), select it, and scroll it into view — wrapping around at the end.</summary>
    private void FindInResponse()
    {
        var term = ResponseFindBox.Text;
        if (string.IsNullOrEmpty(term)) return;
        var text = RawBox.Text;
        if (string.IsNullOrEmpty(text)) { StatusText.Text = "No response to search yet."; return; }

        int from = RawBox.SelectionStart + Math.Max(RawBox.SelectionLength, 0);
        int idx = text.IndexOf(term, Math.Min(from, text.Length), StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);   // wrap to the top
        if (idx < 0) { StatusText.Text = $"“{term}” not found in the response."; return; }

        ResponseTabs.SelectedIndex = 1;   // the Raw view, where the selection is visible
        RawBox.Focus();
        RawBox.Select(idx, term.Length);
        RawBox.ScrollToLine(RawBox.GetLineIndexFromCharacterIndex(idx));
        StatusText.Text = $"Found “{term}”.";
    }

    private void SaveResponse()
    {
        if (_lastResponse is null || _lastResponse.Body.Length == 0)
        {
            StatusText.Text = "No response body to save.";
            return;
        }
        var ext = ExtForContentType(_lastResponse.ContentType);
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "response" + ext, DefaultExt = ext };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                System.IO.File.WriteAllBytes(dialog.FileName, _lastResponse.Body);
                StatusText.Text = $"Saved {_lastResponse.Body.Length} bytes to {dialog.FileName}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed: " + ex.Message;
            }
        }
    }

    private static string ExtForContentType(string? ct)
    {
        ct = ct?.ToLowerInvariant() ?? "";
        if (ct.Contains("json")) return ".json";
        if (ct.Contains("xml")) return ".xml";
        if (ct.Contains("html")) return ".html";
        if (ct.Contains("png")) return ".png";
        if (ct.Contains("jpeg") || ct.Contains("jpg")) return ".jpg";
        if (ct.Contains("gif")) return ".gif";
        if (ct.Contains("pdf")) return ".pdf";
        if (ct.StartsWith("text/")) return ".txt";
        return ".bin";
    }

    // ---------- history ----------

    private const int MaxStoredBody = 256 * 1024;

    private void ApplyCaptures(RequestModel model, ApiResponse response)
    {
        // Capture writes into _state.Environments; keep it in step with the UI-bound _environments
        // both before (so the active env is found) and after (so an auto-created env shows up).
        _state.Environments = _environments.ToList();
        var outcome = CaptureApplier.Apply(
            _state, model.Captures, response.Body, response.ContentType, response.Headers);
        if (outcome.Count == 0) return;

        foreach (var env in _state.Environments)
            if (_environments.All(e => e.Id != env.Id)) _environments.Add(env);
        RefreshEnvCombo();

        var ok = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
        var bad = outcome.Where(o => !o.Ok).ToList();
        var parts = new List<string>();
        if (ok.Count > 0) parts.Add("Captured " + string.Join(", ", ok));
        foreach (var b in bad) parts.Add($"{b.Variable} ✗ ({b.Error})");
        if (parts.Count > 0) StatusText.Text += "   •   " + string.Join("; ", parts);
    }

    private void AddToHistory(ApiResponse response)
    {
        if (ActiveRequest is not { } model) return;
        var entry = model.ToHistoryEntry(response.StatusCode, BuildSnapshot(response));
        _state.History.RemoveAll(h => h.Method == entry.Method && h.EffectiveUrl == entry.EffectiveUrl);
        _state.History.Insert(0, entry);
        if (_state.History.Count > 30) _state.History.RemoveRange(30, _state.History.Count - 30);
        RefreshHistoryList();
    }

    private HistorySnapshot BuildSnapshot(ApiResponse r)
    {
        var body = r.Body;
        bool truncated = body.Length > MaxStoredBody;
        return new HistorySnapshot
        {
            StatusCode = r.StatusCode,
            ReasonPhrase = r.ReasonPhrase,
            ElapsedMs = r.Elapsed.TotalMilliseconds,
            ContentType = r.ContentType,
            Body = truncated ? Array.Empty<byte>() : body,
            BodyTruncated = truncated,
            Headers = r.Headers.Select(h => new HeaderRow { Name = h.Key, Value = h.Value }).ToList(),
            Diagnostics = TransportDiagnostics.Format(r),
            ErrorKind = r.Error?.Kind.ToString(),
            ErrorMessage = r.Error?.Message
        };
    }

    private void RefreshHistoryList()
    {
        HistoryList.SelectionChanged -= HistoryList_SelectionChanged;
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _state.History;
        HistoryList.SelectionChanged += HistoryList_SelectionChanged;
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _state.History.Clear();
        RefreshHistoryList();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry) LoadEntry(entry);
    }

    private void LoadEntry(HistoryEntry entry)
    {
        if (ActiveTab is not { } tab) return;

        // Replace the active tab's request with the stored one (kept as the same instance).
        _loading = true;
        try { tab.Request.LoadFrom(entry); }
        finally { _loading = false; }

        LoadIntoControls(tab.Request);
        tab.UpdateTitle();

        // Restore that request's own response into this tab.
        tab.LastResponse = null;
        tab.LastRawText = "";
        tab.Snapshot = entry.Response;
        if (entry.Response is not null) RenderSnapshot(entry.Response, fromHistory: true);
        else ClearResponse();
    }

    private void RenderSnapshot(HistorySnapshot s, bool fromHistory)
    {
        var suffix = fromHistory ? "   (from history)" : "";
        DiagnosticsBox.Text = s.Diagnostics ?? "No connection details available.";

        if (s.ErrorMessage is not null)
        {
            _lastResponse = null;
            _lastRawText = s.ErrorMessage;
            SetPretty(s.ErrorMessage, BodyKind.Text);
            RawBox.Text = s.ErrorMessage;
            ResponseHeadersBox.Text = "";
            StatusText.Text = $"Error [{s.ErrorKind}]: {s.ErrorMessage}{suffix}";
            RefreshDiffIfVisible();
            return;
        }

        var recon = new ApiResponse
        {
            StatusCode = s.StatusCode,
            ReasonPhrase = s.ReasonPhrase,
            ContentType = s.ContentType,
            Body = s.Body,
            Headers = s.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(),
            Elapsed = TimeSpan.FromMilliseconds(s.ElapsedMs)
        };
        _lastResponse = recon;
        RefreshDiffIfVisible();

        if (s.BodyTruncated)
        {
            SetPretty("(the response body was too large to keep — re-send to see it)", BodyKind.Text);
            _lastRawText = "";
        }
        else
        {
            var formatted = _formatter.Format(recon);
            SetPretty(formatted.Text, formatted.Kind);
            _lastRawText = Encoding.UTF8.GetString(s.Body);
        }
        RawBox.Text = _lastRawText;
        ResponseHeadersBox.Text = string.Join("\n", recon.Headers.Select(h => $"{h.Key}: {h.Value}"));
        StatusText.Text = $"{s.StatusCode} {s.ReasonPhrase}  •  {s.Body.Length} bytes  •  {s.ElapsedMs:F0} ms{suffix}";
    }

    private void ClearResponse()
    {
        ShowPrettyHint();
        RawBox.Text = "";
        ResponseHeadersBox.Text = "";
        DiagnosticsBox.Text = "";
        // A stale diff on screen would describe a response that is no longer shown. The chosen baseline
        // deliberately survives: comparing send after send against one baseline is the whole workflow.
        DiffBox.Text = "";
        _lastResponse = null;
        _lastRawText = "";
    }

    private void ShowPrettyHint()
    {
        var run = new System.Windows.Documents.Run("The formatted response appears here after you send a request.");
        run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text.Faint");
        PrettyRich.Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(run))
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5
        };
    }

    /// <summary>Clear the response views to a "waiting…" state while a request is in flight, so a
    /// previous response can't be mistaken for the new one whichever tab is showing.</summary>
    private void ShowWaitingHint()
    {
        var run = new System.Windows.Documents.Run("Waiting for response…");
        run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Accent");
        PrettyRich.Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(run))
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5
        };
        RawBox.Text = "Waiting for response…";
        ResponseHeadersBox.Text = "";
        DiagnosticsBox.Text = "";
        DiffBox.Text = "";
    }

    private string SelectedMethod() => ((ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!;

    private string SelectedContentType() =>
        (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "application/json";

    private void SelectMethod(string method)
    {
        foreach (ComboBoxItem item in MethodCombo.Items)
            if ((item.Content?.ToString() ?? "") == method) { MethodCombo.SelectedItem = item; return; }
    }

    private void SelectContentType(string contentType)
    {
        foreach (ComboBoxItem item in ContentTypeCombo.Items)
            if ((item.Content?.ToString() ?? "") == contentType) { ContentTypeCombo.SelectedItem = item; return; }
    }

    // ---------- URL / method live edits ----------

    private void MethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        ActiveRequest.Method = SelectedMethod();
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        ActiveRequest.Path = UrlBox.Text; // keeps the tab title in step as you type
        UpdateTokenChip();
    }

    private void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        if (!UrlBox.Text.Contains('?')) return;

        var (path, ps) = RequestUrl.SplitForEditing(UrlBox.Text.Trim());
        _loading = true;
        try
        {
            UrlBox.Text = path;
            ActiveRequest.Path = path;
            ActiveRequest.QueryParams.Clear();
            foreach (var kv in ps) ActiveRequest.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        }
        finally { _loading = false; }
    }

    // ---------- keyboard ----------

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendRequestAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.Enter) { e.Handled = true; _ = SendRequestAsync(); }
        else if (ctrl && e.Key == Key.L) { e.Handled = true; UrlBox.Focus(); UrlBox.SelectAll(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; SaveResponse(); }
        else if (ctrl && e.Key == Key.H) { e.Handled = true; ToggleHistory(); }
        else if (ctrl && e.Key == Key.T) { e.Handled = true; AddNewTab(); }
        else if (ctrl && e.Key == Key.W) { e.Handled = true; if (ActiveTab is { } t) CloseTab(t); }
        else if (e.Key == Key.F5) { e.Handled = true; LoadCertificates(); }
        else if (e.Key == Key.F1) { e.Handled = true; ShowHelp(); }
        else if (e.Key == Key.Escape && CancelButton.IsEnabled) { e.Handled = true; _cts?.Cancel(); }
    }

    // ---------- self-test ----------

    private async void SelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        SelfTestButton.IsEnabled = false;
        StatusText.Text = "Running self-test…";
        try
        {
            var result = await new SelfTestRunner().RunAsync();
            StatusText.Text = (result.Passed ? "Self-test PASSED  •  " : "Self-test FAILED  •  ") + result.Detail;
            SetPretty(result.Detail, BodyKind.Text);
        }
        finally
        {
            SelfTestButton.IsEnabled = true;
        }
    }

    // ---------- session capture ----------

    private void CaptureSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var start = CurrentEditorUrl();
        var win = new SessionCaptureWindow(_state, SelectedCert(),
            IgnoreServerCertCheck.IsChecked == true,
            string.IsNullOrWhiteSpace(start) ? null : start)
        { Owner = this };

        if (win.ShowDialog() == true)
        {
            if (win.SaveCollectionName is { } cn) ImportObservedCalls(win.CapturedCalls, cn);
            UpdateTokenChip();
            StatusText.Text = "Session captured.";
            SaveState();
        }
    }

    /// <summary>Turn calls observed during a capture into a new collection of Auto-auth requests,
    /// so replaying them uses the just-captured cookies/token.</summary>
    public void ImportObservedCalls(IReadOnlyList<ObservedCall> calls, string collectionName)
    {
        if (calls.Count == 0) return;
        var folder = new CollectionNode { Name = collectionName, IsFolder = true };
        foreach (var call in calls)
        {
            var model = new RequestModel { Method = call.Method, Path = call.Url };
            folder.Children.Add(new CollectionNode
            {
                Name = $"{call.Method} {ShortPath(call.Url)}", IsFolder = false, Request = model
            });
        }
        _collections.Add(folder);
        UpdateCollectionsHint();
        SetSidebarMode(SidebarMode.Collections);
    }

    private static string ShortPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
}
