using System.Text;

namespace ApiTester.Core;

/// <summary>The redaction rules shared by everything this product writes into a vault.
///
/// <para>These live in one place because they must not drift apart: a catalogue note and an
/// investigation note land in the same synced folder, and a rule applied to one but not the other
/// is the same leak with extra steps.</para></summary>
public static class MarkdownSecrets
{
    public const string Redacted = "*(redacted)*";

    /// <summary>Query-string parameters whose *value* is a credential. Matching is on the whole
    /// parameter name, case-insensitively — a substring rule would redact <c>keyword</c> and
    /// <c>tokenCount</c>, and a note full of spurious redactions teaches people to pass
    /// <c>--include-secrets</c> reflexively, which is worse than the risk it addresses.</summary>
    private static readonly string[] SecretParameters =
    {
        "token", "access_token", "refresh_token", "id_token", "auth", "authorization",
        "key", "api_key", "apikey", "api-key", "secret", "client_secret", "password", "pwd",
        "sig", "signature", "code", "session", "sessionid", "session_id",
    };

    /// <summary>A URL safe to write into a note: credential-bearing query values are replaced, the
    /// rest is untouched.
    ///
    /// <para>An API key in a query string is the leak this exists for — it looks like part of the
    /// address rather than like a secret, so it survives the review a header would not. The
    /// parameter NAME is kept, as header names are, because "this endpoint is called with an
    /// api_key" is information worth having.</para>
    ///
    /// <para>Anything that does not parse as a URL with a query is returned unchanged: this must
    /// never be the reason a note is missing the address it is about.</para></summary>
    public static string RedactUrl(string url, bool includeSecrets = false)
    {
        if (includeSecrets || string.IsNullOrEmpty(url)) return url;

        url = RedactUserInfo(url);

        int mark = url.IndexOf('?');
        if (mark < 0 || mark == url.Length - 1) return url;

        // Split off a fragment first: it is not part of the query, and re-attaching it keeps the
        // URL intact for a reader who wants to paste it somewhere.
        string queryAndFragment = url[(mark + 1)..];
        int hash = queryAndFragment.IndexOf('#');
        string fragment = hash >= 0 ? queryAndFragment[hash..] : "";
        string query = hash >= 0 ? queryAndFragment[..hash] : queryAndFragment;

        var rebuilt = new StringBuilder(url[..mark]).Append('?');
        var pairs = query.Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            if (i > 0) rebuilt.Append('&');
            string pair = pairs[i];
            int equals = pair.IndexOf('=');
            if (equals <= 0) { rebuilt.Append(pair); continue; }

            string name = pair[..equals];
            rebuilt.Append(name).Append('=')
                   .Append(IsSecretParameter(name) ? "REDACTED" : pair[(equals + 1)..]);
        }
        return rebuilt.Append(fragment).ToString();
    }

    public static bool IsSecretParameter(string name) =>
        SecretParameters.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A body field whose value is a credential: a secret parameter name, or any name
    /// that says password or secret (newPassword, client_secret, x_secret_key).</summary>
    public static bool IsSecretField(string name) =>
        IsSecretParameter(name) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    /// <summary>A request body safe to write into a note or a spec: in a JSON body or a form
    /// (application/x-www-form-urlencoded) body, every credential field's value is replaced. A
    /// value that is only a <c>{{variable}}</c> is a reference, not a secret, and stays. Any other
    /// body — or one that does not parse — is returned unchanged.</summary>
    public static string RedactBody(string body, string? contentType, bool includeSecrets = false)
    {
        if (includeSecrets || string.IsNullOrWhiteSpace(body)) return body;
        string trimmed = body.TrimStart();
        bool form = contentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;
        if (!form && trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                if (node is not null && RedactJson(node))
                    return node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                return body;
            }
            catch (System.Text.Json.JsonException) { return body; }
        }
        if (form || (contentType is null && !trimmed.Contains(' ') && trimmed.Contains('=')))
        {
            var pairs = body.Split('&');
            bool changed = false;
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq <= 0) continue;
                string name = Uri.UnescapeDataString(pairs[i][..eq].Replace('+', ' '));
                string value = pairs[i][(eq + 1)..];
                if (IsSecretField(name) && !IsVariableReference(value))
                {
                    pairs[i] = pairs[i][..eq] + "=REDACTED";
                    changed = true;
                }
            }
            return changed ? string.Join("&", pairs) : body;
        }
        return body;
    }

    private static bool IsVariableReference(string value)
    {
        string v = Uri.UnescapeDataString(value).Trim();
        return v.StartsWith("{{", StringComparison.Ordinal) && v.EndsWith("}}", StringComparison.Ordinal) && v.IndexOf("}}", StringComparison.Ordinal) == v.Length - 2;
    }

    private static bool RedactJson(System.Text.Json.Nodes.JsonNode node)
    {
        bool changed = false;
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (IsSecretField(key) && child is System.Text.Json.Nodes.JsonValue v &&
                        !(v.TryGetValue<string>(out var s) && (s.Length == 0 || IsVariableReference(s))))
                    {
                        obj[key] = "REDACTED";
                        changed = true;
                    }
                    else if (child is not null) changed |= RedactJson(child);
                }
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                foreach (var item in arr)
                    if (item is not null) changed |= RedactJson(item);
                break;
        }
        return changed;
    }

    /// <summary>Mask the password in a URL's <c>user:password@host</c> prefix.
    ///
    /// <para>This is the oldest way to put a credential in a URL and the easiest to forget, because
    /// it does not look like a parameter — <c>https://svc:hunter2@api.internal/orders</c> and
    /// <c>--proxy http://svc:hunter2@proxy.corp:8080</c> both carry one, and both get printed back
    /// in reports. The username is kept for the same reason a header name is: knowing the request
    /// authenticates as <c>svc</c> is useful, and only the secret half has to go.</para>
    ///
    /// <para>Deliberately string surgery rather than <see cref="Uri"/> parsing: this runs over
    /// values a user typed, including ones that will not parse, and a URL that cannot be parsed must
    /// still be redacted rather than passed through whole.</para></summary>
    private static string RedactUserInfo(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;

        int authorityStart = schemeEnd + 3;
        // The userinfo, if any, ends at the first '@' before the authority does.
        int authorityEnd = url.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (authorityEnd < 0) authorityEnd = url.Length;

        int at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at < 0) return url;

        int colon = url.IndexOf(':', authorityStart, at - authorityStart);
        // No colon means a username with no password — nothing secret to hide.
        return colon < 0 ? url : string.Concat(url.AsSpan(0, colon + 1), "REDACTED", url.AsSpan(at));
    }

    /// <summary>Whether a header's value is a credential. The name always survives — that a request
    /// sends an <c>Authorization</c> header is exactly what a catalogue should record.</summary>
    public static bool IsSecretHeader(string name) => SecretHeaders.IsSecret(name);
}
