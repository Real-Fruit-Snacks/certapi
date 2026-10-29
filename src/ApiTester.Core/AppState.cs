using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>Persisted window/request state, stored under %AppData%\CertApiTester\state.json.</summary>
public sealed class AppState
{
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public string? LastCertThumbprint { get; set; }
    public bool IgnoreServerCertErrors { get; set; }
    public int TimeoutSeconds { get; set; } = 100;

    public string? LastBaseUrl { get; set; }
    public List<string> SavedBaseUrls { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();

    public List<RequestModel> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }

    public List<CollectionNode> Collections { get; set; } = new();
    public List<ApiEnvironment> Environments { get; set; } = new();
    public string? ActiveEnvironmentId { get; set; }

    /// <summary>Saved request chains: ordered sequences run as one unit, so "log in, then call the API"
    /// is a thing you can run rather than a convention you remember. Additive — a state.json written
    /// before this existed loads with an empty list.</summary>
    public List<RequestChain> Chains { get; set; } = new();

    public List<SessionToken> SessionTokens { get; set; } = new();
    public List<SessionCookie> SessionCookies { get; set; } = new();

    /// <summary>Thumbprint-pinned, per-host server-certificate trust: strictly narrower than the
    /// blanket <see cref="IgnoreServerCertErrors"/> bypass. Additive: a state.json written before
    /// this existed loads with an empty list.</summary>
    public List<TrustedServerCert> TrustedServerCerts { get; set; } = new();

    /// <summary>Master switch for automatic token capture/attach. Detection results are still
    /// stored while off, so turning it back on works immediately.</summary>
    public bool AutoTokens { get; set; } = true;

    /// <summary>Master switch for attaching browser-captured session cookies. Captured cookies
    /// are still stored while off, so turning it back on works immediately.</summary>
    public bool AutoCookies { get; set; } = true;

    /// <summary>UI theme: "Dark" (default) or "Light". Read at startup to pick the palette
    /// overlay and updated live when the user toggles it from the title bar.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Ask GitHub for a newer release when the app starts, at most once a day. Off unless
    /// the user turns it on: the app makes no network call of its own without being asked.</summary>
    public bool CheckForUpdatesOnStart { get; set; }

    /// <summary>When the opt-in startup check last asked GitHub, so it asks at most once a day.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>File-format version. 0 = files from before the Auto/None auth split. 1 = secrets
    /// (captured tokens/cookies, saved auth secrets, secret variables) stored in the clear. 2 =
    /// those same secrets encrypted per Windows user. 3 = stored response bodies (history
    /// snapshots and known-good snapshots) encrypted as well.</summary>
    public int SchemaVersion { get; set; }

    public const int CurrentSchemaVersion = 3;

    /// <summary>Secrets that could not be decrypted on this load, described for the user. In-memory
    /// only: hosts print these (the CLI on stderr, the app in its status line) and the values they
    /// name have been treated as absent.</summary>
    [JsonIgnore] public List<string> SecretWarnings { get; } = new();

    /// <summary>The live GUI state file under %AppData%. Computing the path has no side
    /// effects; <see cref="SaveTo"/> creates the directory when it first writes.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CertApiTester", "state.json");

    public static AppState Load()
    {
        try { return File.Exists(DefaultPath) ? LoadFrom(DefaultPath) : new AppState(); }
        catch (Exception ex)
        {
            // Corrupt or unreadable — start fresh, but never at the cost of the file: the next
            // save would replace it with this empty workspace. A copy is kept and the user told.
            var fresh = new AppState();
            if (PreserveUnreadable(DefaultPath) is { } kept)
                fresh.SecretWarnings.Add($"Your workspace could not be read ({ex.Message}); an empty one was opened and the file was kept as {Path.GetFileName(kept)}.");
            return fresh;
        }
    }

    /// <summary>Copies a workspace file that exists but could not be loaded to
    /// <c>&lt;file&gt;.unreadable-&lt;time&gt;.bak</c> before anything can save over it. Returns the
    /// copy's path — an earlier copy with the same contents when there is one, so repeated loads of
    /// the same broken file keep one copy — or null when there was no file (or it could not be copied).</summary>
    public static string? PreserveUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] content = File.ReadAllBytes(path);
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null)
                foreach (string earlier in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".unreadable-*.bak"))
                {
                    try { if (new FileInfo(earlier).Length == content.Length && File.ReadAllBytes(earlier).AsSpan().SequenceEqual(content)) return earlier; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            string copy = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, copy, overwrite: false);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Load from an explicit file. Throws on missing/corrupt files — callers decide.</summary>
    public static AppState LoadFrom(string path) => LoadFrom(path, SecretProtection.Default);

    /// <summary>The file as stored, its secrets left encrypted: for a caller that needs only names
    /// or settings (the app's theme at start, tab completion), and never saves it. Decrypting is a
    /// call into lsass per secret, and a workspace can hold hundreds. Throws on missing/corrupt files.</summary>
    public static AppState PeekFrom(string path) =>
        JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState();

    /// <summary>Load with an explicit protector — the seam the tests use to prove the degraded paths.</summary>
    public static AppState LoadFrom(string path, ISecretProtector protector)
    {
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState();
        // Remembered, so a save writes back what was read for every value that has not changed.
        var reusing = new ReusingProtector(protector);
        StateSecrets.Unprotect(state, reusing, state.SecretWarnings);   // before Migrate, so migrations see plaintext
        state._protector = reusing;
        state.Migrate();
        return state;
    }

    /// <summary>The protector this state was loaded with, remembering the stored form of each
    /// value, so a save re-encrypts only what changed.</summary>
    [JsonIgnore] private ReusingProtector? _protector;

    /// <summary>Upgrade older states in place. Version 0 → 1: auth "None" predates the
    /// Auto/None split and meant "nothing configured", so it becomes "Auto". Version 1 → 2 needs no
    /// in-memory migration: a plaintext secret loads as itself (already decrypted, if possible, by
    /// <see cref="LoadFrom(string, ISecretProtector)"/> above), and the next save encrypts it.
    /// Version 2 → 3 needs no in-memory migration either, for the same reason: a plaintext stored
    /// response body loads as itself, and the next save encrypts it.</summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        if (SchemaVersion < 1)
        {
            foreach (var t in Tabs) MigrateAuth(t);
            foreach (var h in History) if (h.AuthType == "None") h.AuthType = "Auto";
            foreach (var c in Collections) MigrateNode(c);
        }
        SchemaVersion = CurrentSchemaVersion;

        static void MigrateAuth(RequestModel m) { if (m.AuthType == "None") m.AuthType = "Auto"; }
        static void MigrateNode(CollectionNode n)
        {
            if (n.Request is { } r) MigrateAuth(r);
            foreach (var child in n.Children) MigrateNode(child);
        }
    }

    public StateSaveResult Save()
    {
        try { return SaveTo(DefaultPath); }
        catch { return new StateSaveResult { BackupPath = null }; }   // best effort for the GUI
    }

    /// <summary>Write atomically: serialize to a temp file, then replace the target.</summary>
    public StateSaveResult SaveTo(string path) => SaveTo(path, SecretProtection.Default);

    /// <summary>Write atomically with an explicit protector. Backs up the previous file before the
    /// first rewrite in the encrypted format, then serializes a deep clone with its secrets
    /// encrypted — the live object graph (data-bound in the desktop application) never holds
    /// ciphertext, even briefly.</summary>
    public StateSaveResult SaveTo(string path, ISecretProtector protector)
    {
        SchemaVersion = CurrentSchemaVersion;   // a written file is by definition current

        string? backup = null;
        if (File.Exists(path) && NeedsSecretUpgrade(path))
        {
            backup = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(path, backup, overwrite: true);
        }

        var clone = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(this))!;
        // The load's own protector when this is the protector it wraps, so unchanged values keep
        // their stored form; otherwise one that remembers from here on.
        if (_protector is null || !ReferenceEquals(_protector.Inner, protector)) _protector = new ReusingProtector(protector);
        bool encrypted;
        lock (_protector)   // one save's pass at a time
        {
            _protector.BeginPass();
            try { encrypted = StateSecrets.Protect(clone, _protector); }
            finally { _protector.EndPass(); }
        }

        var json = JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true });
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);

        return new StateSaveResult { BackupPath = backup, SecretsEncrypted = encrypted };
    }

    /// <summary>True when the file at <paramref name="path"/> still needs its secrets encrypted: it
    /// exists and its stored schema version is older than the current one, or it cannot be read at all
    /// (in which case a copy is taken before anything overwrites it). False when the file does not exist.</summary>
    public static bool NeedsSecretUpgrade(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(nameof(SchemaVersion), out var v) ||
                v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                return true;
            return version < CurrentSchemaVersion;
        }
        catch { return true; }
    }
}

/// <summary>Combines a base URL with a path, honoring absolute URLs typed in the path box.</summary>
public static class UrlHelper
{
    public static string Combine(string? baseUrl, string? path)
    {
        path = (path ?? "").Trim();
        baseUrl = (baseUrl ?? "").Trim();
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        if (string.IsNullOrEmpty(baseUrl)) return path;
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Method { get; set; } = "GET";
    public string? BaseUrl { get; set; }
    public string Url { get; set; } = "";           // the path/rest typed in the URL box
    public List<ParamRow> Params { get; set; } = new();
    public List<HeaderRow> Headers { get; set; } = new();
    public string? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    public string AuthType { get; set; } = "None";
    public string? AuthUser { get; set; }
    public string? AuthSecret { get; set; }
    public string? CertThumbprint { get; set; }
    public bool IgnoreServerCert { get; set; }
    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>The transport choices the request was sent with. Additive: its defaults are the
    /// behavior that predates it, so a state.json written without this property loads unchanged.</summary>
    public TransportSettings Transport { get; set; } = new();

    public int? StatusCode { get; set; }
    public HistorySnapshot? Response { get; set; }
    public List<CaptureRule> Captures { get; set; } = new();
    public List<AssertionRule> Assertions { get; set; } = new();
    public bool IsMultipart { get; set; }
    public List<FormPart> FormParts { get; set; } = new();

    [JsonIgnore] public string EffectiveUrl => UrlHelper.Combine(BaseUrl, Url);

    [JsonIgnore]
    public string DisplayPath
    {
        get
        {
            if (Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out var u))
                return string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
            return string.IsNullOrEmpty(Url) ? EffectiveUrl : Url;
        }
    }

    [JsonIgnore]
    public string DisplayHost =>
        Uri.TryCreate(EffectiveUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}" : "";
}

/// <summary>A node in the collections tree: either a folder (with children) or a saved request.</summary>
public sealed class CollectionNode : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private int? _lastStatusCode;
    private DateTime? _lastCheckedUtc;

    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public bool IsFolder { get; set; }
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public System.Collections.ObjectModel.ObservableCollection<CollectionNode> Children { get; set; } = new();
    public RequestModel? Request { get; set; }   // populated when this is a saved request (not a folder)

    /// <summary>Folder-level defaults: the website and client certificate a request opened from
    /// this folder inherits when it doesn't carry its own. The nearest ancestor with a value wins.</summary>
    public string? DefaultBaseUrl { get; set; }
    public string? DefaultCertThumbprint { get; set; }

    /// <summary>The status code the last send of this saved request returned (null if it failed
    /// without a response, or if it has never been sent — see <see cref="LastCheckedUtc"/>).</summary>
    public int? LastStatusCode
    {
        get => _lastStatusCode;
        set { _lastStatusCode = value; RaiseStatus(); }
    }

    /// <summary>When this saved request was last sent (UTC); null if never.</summary>
    public DateTime? LastCheckedUtc
    {
        get => _lastCheckedUtc;
        set { _lastCheckedUtc = value; RaiseStatus(); }
    }

    /// <summary>The response this saved request returned the last time it was known good (a 2xx),
    /// kept so `certapi send --diff known-good` has a baseline to compare against. Additive: null in
    /// a state.json written before this existed, and left null when a send fails.</summary>
    public ResponseSnapshot? KnownGood { get; set; }

    /// <summary>Record the outcome of sending this saved request. A snapshot is kept only for a
    /// known-good (2xx) result: a baseline that recorded a 500 would make every later diff lie.</summary>
    public void RecordResult(int? statusCode, DateTime utcNow, ResponseSnapshot? snapshot = null)
    {
        _lastStatusCode = statusCode;
        _lastCheckedUtc = utcNow;
        // Only a success replaces the baseline, and nothing ever clears it — a failing send is the
        // moment the last good response is most worth still having.
        if (snapshot is not null && statusCode is >= 200 and < 300) KnownGood = snapshot;
        RaiseStatus();
    }

    /// <summary>Known good: the last send returned a 2xx.</summary>
    [JsonIgnore] public bool IsKnownGood => LastStatusCode is >= 200 and < 300;

    /// <summary>Whether this saved request has ever been sent.</summary>
    [JsonIgnore] public bool HasResult => LastCheckedUtc is not null;

    /// <summary>Tooltip for the status dot, e.g. "Last checked 2026-07-16 14:02 — 200 (known good)".</summary>
    [JsonIgnore]
    public string? StatusSummary
    {
        get
        {
            if (IsFolder) return null;
            if (LastCheckedUtc is not { } utc) return "Never sent";
            string when = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string outcome = LastStatusCode is { } c
                ? $"{c}{(IsKnownGood ? " (known good)" : "")}"
                : "failed (no response)";
            return $"Last checked {when} — {outcome}";
        }
    }

    private void RaiseStatus()
    {
        Raise(nameof(LastStatusCode));
        Raise(nameof(LastCheckedUtc));
        Raise(nameof(IsKnownGood));
        Raise(nameof(KnownGood));
        Raise(nameof(HasResult));
        Raise(nameof(StatusSummary));
    }

    /// <summary>Method badge shown before a saved request's name; a folder mark for folders.</summary>
    [JsonIgnore] public string MethodBadge => IsFolder ? "▸" : Request?.Method ?? "";

    /// <summary>Build the UI-free collection DTO from this node (the reverse of
    /// <see cref="FromParsed"/>), e.g. to export as an OpenAPI document. Auth values ride along
    /// in memory, but the OpenAPI exporter only writes the security scheme — never the secrets.</summary>
    public ParsedCollection ToParsed()
    {
        var pc = new ParsedCollection { Name = Name };
        if (!IsFolder)
        {
            if (ToParsedRequest() is { } self) pc.Requests.Add(self);
        }
        else
        {
            foreach (var child in Children)
            {
                if (child.IsFolder) pc.Folders.Add(child.ToParsed());
                else if (child.ToParsedRequest() is { } r) pc.Requests.Add(r);
            }
        }
        pc.BaseUrl = FirstBase(pc);
        return pc;
    }

    private ParsedRequest? ToParsedRequest()
    {
        if (Request is not { } r) return null;

        // Merge any query still in the path with the enabled rows of the Params grid. This is a
        // collection export, so it keeps {{variable}} tokens literal — the export is a template
        // someone else fills in, not a request the server will actually receive.
        var (path, rawQuery) = QueryString.Split((r.Path ?? "").Trim());
        var pairs = QueryString.Parse(rawQuery);
        pairs.AddRange(r.EnabledParams());

        var req = new ParsedRequest
        {
            Method = r.Method,
            BaseUrl = string.IsNullOrWhiteSpace(r.BaseUrl) ? null : r.BaseUrl!.Trim(),
            Url = QueryString.ComposeTemplate(path, pairs),
            Name = Name,
            Description = HasResult ? StatusSummary : null,
            Body = string.IsNullOrEmpty(r.Body) ? null : r.Body,
            ContentType = r.ContentType == "(none)" ? null : r.ContentType,
            InsecureSkipVerify = r.IgnoreServerCert
        };
        foreach (var h in r.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                req.Headers.Add(new(h.Name.Trim(), h.Value ?? ""));
        switch (r.AuthType)
        {
            case "Bearer": req.BearerToken = r.AuthSecret ?? ""; break;
            case "Basic": req.BasicUser = r.AuthUser ?? ""; req.BasicPassword = r.AuthSecret; break;
        }
        return req;
    }

    private static string? FirstBase(ParsedCollection pc)
    {
        foreach (var r in pc.Requests)
            if (!string.IsNullOrWhiteSpace(r.BaseUrl)) return r.BaseUrl;
        foreach (var f in pc.Folders)
            if (FirstBase(f) is { } b) return b;
        return null;
    }

    /// <summary>Build a collections folder (and its requests) from an imported OpenAPI collection.</summary>
    public static CollectionNode FromParsed(ParsedCollection pc)
    {
        var folder = new CollectionNode { Name = pc.Name, IsFolder = true };
        foreach (var sub in pc.Folders) folder.Children.Add(FromParsed(sub));
        foreach (var req in pc.Requests)
            folder.Children.Add(new CollectionNode
            {
                Name = string.IsNullOrWhiteSpace(req.Name) ? $"{req.Method} {req.Url}" : req.Name!,
                IsFolder = false,
                Request = RequestModel.FromParsed(req)
            });
        return folder;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>A named environment holding <c>{{variable}}</c> values.</summary>
public sealed class ApiEnvironment : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public System.Collections.ObjectModel.ObservableCollection<Variable> Variables { get; set; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class Variable : System.ComponentModel.INotifyPropertyChanged
{
    private string _key = "";
    private string _value = "";
    private bool _secret;
    public string Key { get => _key; set { _key = value; Raise(nameof(Key)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    /// <summary>Marks this value as a secret: it is encrypted in the workspace file rather than
    /// stored in the clear. Additive and false by default, so a variable written before this existed
    /// loads as a plain value.</summary>
    public bool Secret { get => _secret; set { _secret = value; Raise(nameof(Secret)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class HeaderRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _name = "";
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class ParamRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _key = "";
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Key { get => _key; set { _key = value; Raise(nameof(Key)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>One row in the multipart/form-data body editor: a text field or a file upload.</summary>
public sealed class FormPart : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _name = "";
    private bool _isFile;
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }

    /// <summary>False: <see cref="Value"/> is a text field value. True: <see cref="Value"/> is a file path to upload.</summary>
    public bool IsFile { get => _isFile; set { _isFile = value; Raise(nameof(IsFile)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public enum CaptureSource { Body, Header }

/// <summary>A rule that saves a value from a response into an environment variable after a send.</summary>
public sealed class CaptureRule : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _variable = "";
    private CaptureSource _source = CaptureSource.Body;
    private string _path = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public string Variable { get => _variable; set { _variable = value; Raise(nameof(Variable)); } }
    public CaptureSource Source { get => _source; set { _source = value; Raise(nameof(Source)); } }
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }

    public CaptureSpec ToSpec() => new(Variable.Trim(), Source == CaptureSource.Header, Path.Trim());

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>A stored snapshot of the response for a history entry — what the History list replays
/// into the response panel. Named for its owner because response diffing has its own, differently
/// shaped snapshot type.</summary>
public sealed class HistorySnapshot
{
    public int? StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public double ElapsedMs { get; set; }
    public string? ContentType { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public bool BodyTruncated { get; set; }
    public List<HeaderRow> Headers { get; set; } = new();
    public string? Diagnostics { get; set; }
    public string? ErrorKind { get; set; }
    public string? ErrorMessage { get; set; }
}
