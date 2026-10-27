using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>A cookie jar the gateway keeps on the upstreams' behalf — `serve --cookie-jar`.
/// <para>A browser behind the gateway stores every cookie against the gateway's one origin, so a
/// cookie an identity provider sets for <c>Domain=.corp</c> never reaches a second host on that
/// domain: the browser does not know the two gateway paths are two hosts. This jar does. Every
/// Set-Cookie an upstream sends is stored against the real upstream URL, attributes intact, and a
/// forwarded request carries whatever the jar holds for its real target — alongside what the
/// browser sent. The cookies are still relayed to the browser (rewritten) as before, so a page that
/// reads <c>document.cookie</c> is unaffected.</para>
/// <para>Memory only, for the life of the gateway: a session a browser login established is not
/// something to write to disk by surprise.</para></summary>
public sealed class GatewayCookieJar
{
    private readonly CookieContainer _container = CookieJarFile.NewContainer();
    private readonly object _gate = new();

    /// <summary>Store the response's cookies against the request they answered.</summary>
    public int Store(Uri upstreamRequest, IEnumerable<string> setCookieValues)
    {
        int stored = 0;
        lock (_gate)
        {
            foreach (var value in setCookieValues)
            {
                // One header per call: SetCookies splits on commas, and an Expires date carries one.
                try { _container.SetCookies(upstreamRequest, value); stored++; }
                catch (CookieException) { /* a cookie the container refuses is one the browser would too */ }
            }
        }
        return stored;
    }

    /// <summary>The caller's Cookie header plus every jar cookie for <paramref name="upstreamRequest"/>
    /// the caller did not already send. Null when there is nothing to send at all.</summary>
    public string? Merge(Uri upstreamRequest, string? browserCookieHeader)
    {
        string jarHeader;
        lock (_gate) jarHeader = _container.GetCookieHeader(upstreamRequest);
        if (jarHeader.Length == 0) return browserCookieHeader;
        if (string.IsNullOrEmpty(browserCookieHeader)) return jarHeader;

        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in browserCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=');
            sent.Add(eq < 0 ? pair : pair[..eq]);
        }
        var extra = new List<string>();
        foreach (var pair in jarHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=');
            // The browser's own value wins for a name both have: it is the fresher of the two
            // from the page's point of view, and the page may have changed it.
            if (!sent.Contains(eq < 0 ? pair : pair[..eq])) extra.Add(pair);
        }
        return extra.Count == 0 ? browserCookieHeader : browserCookieHeader + "; " + string.Join("; ", extra);
    }

    public int Count
    {
        get { lock (_gate) return _container.Count; }
    }

    /// <summary>Persist the jar — `serve --cookie-jar <file>` — through the machine's secret
    /// protector, so a browser session the gateway established is as protected on disk as every
    /// other secret this product stores (DPAPI, per Windows user). Refused, never written in the
    /// clear, where the protector is unavailable: the caller says so and the jar stays in memory.</summary>
    public void Save(string path, ISecretProtector protector)
    {
        if (!protector.IsAvailable)
            throw new InvalidOperationException("The gateway cookie jar is only written to disk encrypted, and this machine cannot encrypt it (DPAPI unavailable).");
        string json;
        lock (_gate) json = CookieJarFile.Serialize(_container);
        string? stored = protector.Protect(json)
            ?? throw new InvalidOperationException("The gateway cookie jar could not be encrypted for storage.");
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        CookieJarFile.WriteAtomically(path, stored);
    }

    /// <summary>Read a jar <see cref="Save"/> wrote. A missing file is an empty jar; a file this
    /// user cannot decrypt (another user's, another machine's, or plaintext) is refused.</summary>
    public int Load(string path, ISecretProtector protector)
    {
        if (!File.Exists(path)) return 0;
        string stored = File.ReadAllText(path);
        if (!SecretProtection.LooksProtected(stored))
            throw new InvalidDataException($"{path} is not an encrypted gateway cookie jar.");
        string json = protector.Unprotect(stored)
            ?? throw new InvalidDataException($"{path} could not be decrypted: it was written by a different Windows user or on a different machine.");
        lock (_gate) return CookieJarFile.Deserialize(json, _container);
    }
}

/// <summary>A cookie jar on disk — `send --cookie-jar <file>` / `run --cookie-jar <file>` — so a
/// scripted login in one invocation is a logged-in session in the next. Plain JSON, one object per
/// cookie, readable and editable; session cookies (no expiry) are kept too, because the point is to
/// carry them between invocations. Nothing in it is encrypted: it is a file the user asked for, in
/// a place they named, like curl's.</summary>
public static class CookieJarFile
{
    /// <summary>A jar with a browser's room: .NET's default keeps only 20 cookies per domain (300
    /// in all), so a single-sign-on login that sets more silently lost the oldest — and a saved jar
    /// file was cut down to 20 on its next save.</summary>
    public static CookieContainer NewContainer() => new(capacity: 3000, perDomainCapacity: 180, maxCookieSize: CookieContainer.DefaultCookieLengthLimit);

    private sealed record StoredCookie(
        string Name, string Value, string Domain, string Path,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Expires,
        bool Secure, bool HttpOnly,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HostOnly = null);

    // Cookie.Domain reads the same for "Set-Cookie: a=1" from api.example.com (host-only) and
    // "Domain=api.example.com" (that host and its subdomains); only this internal flag knows which.
    private static readonly System.Reflection.PropertyInfo? DomainImplicit =
        typeof(Cookie).GetProperty("DomainImplicit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static bool IsHostOnly(Cookie c)
    {
        if (c.Domain.StartsWith('.')) return false;
        try { return DomainImplicit?.GetValue(c) is not false; }   // unknown: the narrower reading
        catch { return true; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Read a jar into <paramref name="container"/>. A missing file is an empty jar (the
    /// first run creates it); a malformed one is an error naming the file.</summary>
    public static int Load(string path, CookieContainer container)
    {
        if (!File.Exists(path)) return 0;
        try { return Deserialize(File.ReadAllText(path), container); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or InvalidDataException)
        {
            // A hand-edited jar (an entry without Domain, a null) is a bad file, not a crash.
            throw new InvalidDataException($"--cookie-jar: {path} is not a cookie jar this tool wrote: {ex.Message}");
        }
    }

    internal static int Deserialize(string json, CookieContainer container)
    {
        var cookies = JsonSerializer.Deserialize<List<StoredCookie>>(json, Options);
        int loaded = 0;
        foreach (var c in cookies ?? new())
        {
            if (c is null || string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain))
                throw new InvalidDataException("an entry has no name or no domain");
            if (c.Expires is { } exp && exp <= DateTime.UtcNow) continue;
            // A domain with a leading dot came from a Domain= attribute and covers subdomains; one
            // without is host-only, and must come back host-only — a Cookie built with a Domain is
            // always a domain cookie, so it is added for its host's URL with no Domain at all.
            // A file written before HostOnly was recorded cannot tell the two apart (.NET keeps no
            // leading dot): it is read as it always was, as a domain cookie.
            bool hostOnly = c.HostOnly ?? false;
            var cookie = hostOnly
                ? new Cookie(c.Name, c.Value, c.Path) { Secure = c.Secure, HttpOnly = c.HttpOnly }
                : new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly };
            if (c.Expires is { } e) cookie.Expires = e;
            try
            {
                if (hostOnly) container.Add(new Uri((c.Secure ? "https://" : "http://") + HostForUri(c.Domain) + "/"), cookie);
                else container.Add(cookie);
                loaded++;
            }
            catch (Exception ex) when (ex is CookieException or UriFormatException) { }
        }
        return loaded;
    }

    private static string HostForUri(string domain) =>
        domain.Contains(':') && !domain.StartsWith('[') ? "[" + domain + "]" : domain;

    /// <summary>Write every live cookie in <paramref name="container"/> to <paramref name="path"/>.</summary>
    public static int Save(string path, CookieContainer container)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = Serialize(container, out int count);
        // Beside the jar, then moved into place: a full disk or an interrupted write never leaves
        // the session's cookies destroyed or half-written.
        WriteAtomically(path, json);
        return count;
    }

    internal static void WriteAtomically(string path, string text)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    internal static string Serialize(CookieContainer container) => Serialize(container, out _);

    internal static string Serialize(CookieContainer container, out int count)
    {
        var all = new List<StoredCookie>();
        foreach (Cookie c in container.GetAllCookies())
        {
            if (c.Expired) continue;
            all.Add(new StoredCookie(c.Name, c.Value, c.Domain, c.Path,
                c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime(), c.Secure, c.HttpOnly, IsHostOnly(c)));
        }
        count = all.Count;
        return JsonSerializer.Serialize(all, Options);
    }
}
