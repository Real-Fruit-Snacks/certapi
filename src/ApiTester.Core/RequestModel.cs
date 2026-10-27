using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ApiTester.Core;

/// <summary>A single request's editable state — the value a tab, history entry, or saved
/// request is built from. Bindable so the editor reflects changes both ways.</summary>
public sealed class RequestModel : INotifyPropertyChanged
{
    private string _method = "GET";
    private string? _baseUrl;
    private string _path = "";
    private string? _body;
    private string _contentType = "application/json";
    private string _authType = "Auto";
    private string? _authUser;
    private string? _authSecret;
    private string? _certThumbprint;
    private bool _ignoreServerCert;
    private int _timeoutSeconds = 100;
    private string? _sourceCollectionId;

    public string Method { get => _method; set { _method = value; Raise(nameof(Method)); } }
    public string? BaseUrl { get => _baseUrl; set { _baseUrl = value; Raise(nameof(BaseUrl)); } }
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }
    public string? Body { get => _body; set { _body = value; Raise(nameof(Body)); } }
    public string ContentType { get => _contentType; set { _contentType = value; Raise(nameof(ContentType)); } }
    public string AuthType { get => _authType; set { _authType = value; Raise(nameof(AuthType)); } }
    public string? AuthUser { get => _authUser; set { _authUser = value; Raise(nameof(AuthUser)); } }
    public string? AuthSecret { get => _authSecret; set { _authSecret = value; Raise(nameof(AuthSecret)); } }
    public string? CertThumbprint { get => _certThumbprint; set { _certThumbprint = value; Raise(nameof(CertThumbprint)); } }
    public bool IgnoreServerCert { get => _ignoreServerCert; set { _ignoreServerCert = value; Raise(nameof(IgnoreServerCert)); } }
    public int TimeoutSeconds { get => _timeoutSeconds; set { _timeoutSeconds = value; Raise(nameof(TimeoutSeconds)); } }

    /// <summary>The collection entry this request was opened from (or saved to), so a send can
    /// record the endpoint's last result back on that entry. Null for unlinked requests.</summary>
    public string? SourceCollectionId
    {
        get => _sourceCollectionId;
        set { _sourceCollectionId = value; Raise(nameof(SourceCollectionId)); }
    }

    public ObservableCollection<HeaderRow> Headers { get; set; } = new();
    public ObservableCollection<ParamRow> QueryParams { get; set; } = new();
    public ObservableCollection<CaptureRule> Captures { get; set; } = new();
    public ObservableCollection<AssertionRule> Assertions { get; set; } = new();

    /// <summary>*How* this request reaches the server (proxy, redirects, compression, HTTP version).
    /// A settable property, like the collections above, so System.Text.Json can round-trip it.</summary>
    public TransportSettings Transport { get; set; } = new();

    private bool _isMultipart;
    /// <summary>When true, the body is sent as multipart/form-data built from <see cref="FormParts"/>.</summary>
    public bool IsMultipart { get => _isMultipart; set { _isMultipart = value; Raise(nameof(IsMultipart)); } }
    public ObservableCollection<FormPart> FormParts { get; set; } = new();

    /// <summary>The enabled, named multipart parts as core parts (files by path, else text values).</summary>
    public IEnumerable<MultipartPart> EnabledParts() =>
        FormParts.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name))
                 .Select(p => p.IsFile
                     ? new MultipartPart(p.Name.Trim(), null, p.Value)
                     : new MultipartPart(p.Name.Trim(), p.Value ?? "", null));

    /// <summary>The enabled, non-empty query parameters as key/value pairs.</summary>
    public IEnumerable<KeyValuePair<string, string>> EnabledParams() =>
        QueryParams.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
                   .Select(p => new KeyValuePair<string, string>(p.Key.Trim(), p.Value ?? ""));

    /// <summary>Base URL + path + enabled query parameters, composed into the URL to send.
    /// <para>With a <paramref name="resolve"/> callback, <c>{{variable}}</c> tokens are expanded in the
    /// base URL, the path, and each enabled parameter's key and value *before* the query is composed —
    /// so what gets percent-encoded is the resolved value, not the token. Resolving afterwards, on the
    /// finished URL, cannot work: escaping has already turned "{{tok}}" into "%7B%7Btok%7D%7D".
    /// Without a callback the raw fields are composed exactly as stored.</para></summary>
    public string EffectiveUrl(Func<string, string>? resolve = null)
    {
        if (resolve is null) return RequestUrl.Effective(BaseUrl, Path, EnabledParams());
        var resolved = EnabledParams()
            .Select(p => new KeyValuePair<string, string>(resolve(p.Key).Trim(), resolve(p.Value)))
            .Where(p => p.Key.Length > 0);   // a key that resolves to nothing is dropped, as a blank one is
        return RequestUrl.Effective(resolve(BaseUrl ?? ""), resolve(Path), resolved);
    }

    /// <summary>The URL as an *export template*. Like <see cref="EffectiveUrl"/> — a
    /// <paramref name="resolve"/> callback still expands what it can, before composing — except that
    /// any <c>{{variable}}</c> still standing afterwards stays literal instead of being escaped into
    /// "%7B%7B...%7D%7D". An export is a template a human or another tool fills in later, so escaped
    /// braces would make a copied cURL command silently wrong when re-run. The wire keeps
    /// <see cref="EffectiveUrl"/>: there, a token that survived resolution is not a template, it is a
    /// value the server will actually receive.</summary>
    public string ExportUrl(Func<string, string>? resolve = null)
    {
        if (resolve is null) return RequestUrl.EffectiveTemplate(BaseUrl, Path, EnabledParams());
        var resolved = EnabledParams()
            .Select(p => new KeyValuePair<string, string>(resolve(p.Key).Trim(), resolve(p.Value)))
            .Where(p => p.Key.Length > 0);   // a key that resolves to nothing is dropped, as a blank one is
        return RequestUrl.EffectiveTemplate(resolve(BaseUrl ?? ""), resolve(Path), resolved);
    }

    /// <summary>Build a history entry from this request and the response it produced.</summary>
    public HistoryEntry ToHistoryEntry(int? statusCode, HistorySnapshot? snapshot)
    {
        var entry = new HistoryEntry
        {
            Method = Method,
            BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl!.Trim(),
            Url = Path.Trim(),
            Params = QueryParams.Select(p => new ParamRow { Enabled = p.Enabled, Key = p.Key, Value = p.Value }).ToList(),
            Headers = Headers.Select(h => new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value }).ToList(),
            Captures = Captures.Select(c => new CaptureRule { Enabled = c.Enabled, Variable = c.Variable, Source = c.Source, Path = c.Path }).ToList(),
            Assertions = Assertions.Select(a => new AssertionRule { Enabled = a.Enabled, Target = a.Target, Op = a.Op, Path = a.Path, Value = a.Value }).ToList(),
            IsMultipart = IsMultipart,
            FormParts = FormParts.Select(p => new FormPart { Enabled = p.Enabled, Name = p.Name, IsFile = p.IsFile, Value = p.Value }).ToList(),
            Body = string.IsNullOrEmpty(Body) ? null : Body,
            ContentType = ContentType,
            AuthType = AuthType,
            AuthUser = AuthUser,
            AuthSecret = AuthSecret,
            CertThumbprint = CertThumbprint,
            IgnoreServerCert = IgnoreServerCert,
            TimeoutSeconds = TimeoutSeconds,
            Transport = Transport.Clone(),
            StatusCode = statusCode,
            Response = snapshot
        };
        return entry;
    }

    /// <summary>Build a request model from an imported (cURL or OpenAPI) request.</summary>
    public static RequestModel FromParsed(ParsedRequest p)
    {
        var m = new RequestModel { Method = p.Method, BaseUrl = p.BaseUrl ?? "" };
        var (path, query) = RequestUrl.SplitForEditing(p.Url);
        m.Path = path;
        foreach (var kv in query) m.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        foreach (var h in p.Headers) m.Headers.Add(new HeaderRow { Name = h.Key, Value = h.Value });
        m.Body = string.IsNullOrEmpty(p.Body) ? null : p.Body;
        if (!string.IsNullOrEmpty(p.ContentType)) m.ContentType = p.ContentType;
        if (p.BearerToken is not null) { m.AuthType = "Bearer"; m.AuthSecret = p.BearerToken; }
        else if (p.BasicUser is not null || p.BasicPassword is not null)
        {
            m.AuthType = "Basic"; m.AuthUser = p.BasicUser; m.AuthSecret = p.BasicPassword;
        }
        m.IgnoreServerCert = p.InsecureSkipVerify;
        if (p.FormParts.Count > 0)
        {
            m.IsMultipart = true;
            foreach (var f in p.FormParts) m.FormParts.Add(new FormPart { Name = f.Name, Value = f.Value, IsFile = f.IsFile });
        }
        return m;
    }

    /// <summary>Rebuild a request model from a stored history entry.</summary>
    public static RequestModel FromHistoryEntry(HistoryEntry e)
    {
        var m = new RequestModel();
        m.LoadFrom(e);
        return m;
    }

    /// <summary>Copy a stored history entry's fields into this model in place (keeps the
    /// same instance so open tabs/bindings update rather than being replaced).</summary>
    public void LoadFrom(HistoryEntry e)
    {
        Method = e.Method;
        BaseUrl = e.BaseUrl ?? "";
        Path = e.Url;
        Body = e.Body ?? "";
        ContentType = e.ContentType;
        AuthType = e.AuthType switch { "Bearer" => "Bearer", "Basic" => "Basic", "None" => "None", _ => "Auto" };
        AuthUser = e.AuthUser;
        AuthSecret = e.AuthSecret;
        CertThumbprint = e.CertThumbprint;
        IgnoreServerCert = e.IgnoreServerCert;
        TimeoutSeconds = e.TimeoutSeconds;
        Transport.CopyFrom(e.Transport);   // in place, so the editor's bindings survive
        Headers.Clear();
        foreach (var h in e.Headers)
            Headers.Add(new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value });
        QueryParams.Clear();
        foreach (var p in e.Params)
            QueryParams.Add(new ParamRow { Enabled = p.Enabled, Key = p.Key, Value = p.Value });
        Captures.Clear();
        foreach (var c in e.Captures)
            Captures.Add(new CaptureRule { Enabled = c.Enabled, Variable = c.Variable, Source = c.Source, Path = c.Path });
        Assertions.Clear();
        foreach (var a in e.Assertions)
            Assertions.Add(new AssertionRule { Enabled = a.Enabled, Target = a.Target, Op = a.Op, Path = a.Path, Value = a.Value });
        IsMultipart = e.IsMultipart;
        FormParts.Clear();
        foreach (var p in e.FormParts)
            FormParts.Add(new FormPart { Enabled = p.Enabled, Name = p.Name, IsFile = p.IsFile, Value = p.Value });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
