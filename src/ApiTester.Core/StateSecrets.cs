using System.Collections.Generic;
using System.Text;

namespace ApiTester.Core;

/// <summary>What a save did that the user needs to be told about.</summary>
public sealed class StateSaveResult
{
    /// <summary>The timestamped copy taken of the previous file before the first rewrite in the
    /// encrypted format; null when no backup was needed.</summary>
    public string? BackupPath { get; init; }

    /// <summary>False when at least one secret had to be written in the clear because per-user
    /// encryption is unavailable on this system — losing the value would be worse than storing it.</summary>
    public bool SecretsEncrypted { get; init; } = true;
}

/// <summary>Encrypts and decrypts the secret-bearing fields of a workspace, leaving every other
/// field readable so the file stays diffable and debuggable.</summary>
public static class StateSecrets
{
    private const string ClosingWarning =
        "Secrets are encrypted for your Windows user — a workspace file copied from another user or machine cannot be read.";

    /// <summary>Encrypt every secret-bearing field in place. Call it on a CLONE — never on live
    /// state, whose objects are data-bound in the desktop application. Returns false when at least
    /// one non-empty secret had to be left in the clear.</summary>
    public static bool Protect(AppState state, ISecretProtector protector)
    {
        bool allEncrypted = true;

        foreach (var t in state.SessionTokens)
            t.Token = ProtectValue(t.Token, protector, ref allEncrypted);

        foreach (var c in state.SessionCookies)
            c.Value = ProtectValue(c.Value, protector, ref allEncrypted);

        foreach (var tab in state.Tabs)
        {
            tab.AuthSecret = ProtectValueOptional(tab.AuthSecret, protector, ref allEncrypted);
            tab.Transport.ProxyPassword =
                ProtectValueOptional(tab.Transport.ProxyPassword, protector, ref allEncrypted);
        }

        foreach (var h in state.History)
        {
            h.AuthSecret = ProtectValueOptional(h.AuthSecret, protector, ref allEncrypted);
            h.Transport.ProxyPassword =
                ProtectValueOptional(h.Transport.ProxyPassword, protector, ref allEncrypted);
            if (h.Response is { } resp)
            {
                resp.Body = ProtectBody(resp.Body, protector, ref allEncrypted);
                foreach (var row in resp.Headers)
                    if (SecretHeaders.IsSecret(row.Name)) row.Value = ProtectValue(row.Value, protector, ref allEncrypted);
            }
        }

        foreach (var root in state.Collections)
            ProtectNode(root, protector, ref allEncrypted);

        foreach (var env in state.Environments)
            foreach (var v in env.Variables)
                if (v.Secret)
                    v.Value = ProtectValue(v.Value, protector, ref allEncrypted);

        return allEncrypted;

        static void ProtectNode(CollectionNode node, ISecretProtector p, ref bool allEncrypted)
        {
            if (node.Request is { } r)
            {
                r.AuthSecret = ProtectValueOptional(r.AuthSecret, p, ref allEncrypted);
                r.Transport.ProxyPassword =
                    ProtectValueOptional(r.Transport.ProxyPassword, p, ref allEncrypted);
            }
            if (node.KnownGood is { } kg)
            {
                bool all = allEncrypted;
                var headers = kg.Headers.Select(hv => SecretHeaders.IsSecret(hv.Key)
                    ? new KeyValuePair<string, string>(hv.Key, ProtectValue(hv.Value, p, ref all))
                    : hv).ToList();
                allEncrypted = all;
                node.KnownGood = kg with { Body = ProtectBody(kg.Body, p, ref allEncrypted), Headers = headers };
            }
            foreach (var child in node.Children) ProtectNode(child, p, ref allEncrypted);
        }
    }

    // Skips empty and already-protected values; on a null result from the protector, leaves
    // the plaintext in place and flags that something was left unencrypted. For the non-nullable
    // fields (SessionToken.Token, SessionCookie.Value, Variable.Value): never returns null.
    private static string ProtectValue(string value, ISecretProtector protector, ref bool allEncrypted)
    {
        if (string.IsNullOrEmpty(value) || SecretProtection.LooksProtected(value)) return value;
        var protectedValue = protector.Protect(value);
        if (protectedValue is null)
        {
            allEncrypted = false;
            return value;
        }
        return protectedValue;
    }

    // Same rules, for the nullable auth-secret fields — null and empty both pass through unchanged.
    private static string? ProtectValueOptional(string? value, ISecretProtector protector, ref bool allEncrypted) =>
        string.IsNullOrEmpty(value) ? value : ProtectValue(value, protector, ref allEncrypted);

    // Same rules as ProtectValue, for a stored response body. An empty/absent body and an
    // already-protected one pass through unchanged. Otherwise the raw bytes are Base64-encoded
    // into a string, THAT string is handed to the (string-shaped) protector, and the resulting
    // "enc:v1:…" marker is stored back as its UTF-8 bytes — the Base64 step is what lets an
    // arbitrary binary body survive a protector built for strings.
    private static byte[] ProtectBody(byte[] body, ISecretProtector protector, ref bool allEncrypted)
    {
        if (body is null || body.Length == 0) return body ?? Array.Empty<byte>();
        if (LooksLikeProtectedBody(body)) return body;

        var protectedValue = protector.Protect(Convert.ToBase64String(body));
        if (protectedValue is null)
        {
            allEncrypted = false;
            return body;
        }
        return Encoding.UTF8.GetBytes(protectedValue);
    }

    // Cheap guard first, mirroring the one in TryUnprotectBody: only decode a (possibly large,
    // possibly binary) body as UTF-8 once its leading bytes already match the ASCII marker.
    private static bool LooksLikeProtectedBody(byte[] body) =>
        StartsWithProtectedMarker(body) && SecretProtection.LooksProtected(Encoding.UTF8.GetString(body));

    private static readonly byte[] ProtectedMarkerBytes = Encoding.ASCII.GetBytes(SecretProtection.Prefix);

    private static bool StartsWithProtectedMarker(byte[] body)
    {
        if (body.Length < ProtectedMarkerBytes.Length) return false;
        for (int i = 0; i < ProtectedMarkerBytes.Length; i++)
            if (body[i] != ProtectedMarkerBytes[i]) return false;
        return true;
    }

    // Decrypt a stored response body in place. Never throws: a null from Unprotect or a
    // FormatException from the Base64 decode both mean "undecryptable" — result becomes empty and
    // the caller is told via the false return so it can add a named warning. A body that does not
    // start with the marker is legacy plaintext (or genuinely not ciphertext) and is left exactly
    // as it is. Note: a stored body whose literal first bytes are the marker followed by valid
    // Base64 would be misread as ciphertext here — the same tradeoff SecretProtection.LooksProtected
    // already documents and accepts for string secrets.
    private static bool TryUnprotectBody(byte[] body, ISecretProtector protector, out byte[] result)
    {
        result = body;
        if (body is null || body.Length == 0 || !StartsWithProtectedMarker(body)) return true;

        var text = Encoding.UTF8.GetString(body);
        if (!SecretProtection.LooksProtected(text)) return true;

        var plain = protector.Unprotect(text);
        if (plain is null)
        {
            result = Array.Empty<byte>();
            return false;
        }
        try
        {
            result = Convert.FromBase64String(plain);
            return true;
        }
        catch (FormatException)
        {
            result = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>Decrypt every stored secret in place. A value that cannot be decrypted is treated
    /// as absent (per the table below) and described in <paramref name="warnings"/>.</summary>
    public static void Unprotect(AppState state, ISecretProtector protector, List<string> warnings)
    {
        bool anyDropped = false;

        var survivingTokens = new List<SessionToken>(state.SessionTokens.Count);
        foreach (var t in state.SessionTokens)
        {
            if (SecretProtection.LooksProtected(t.Token))
            {
                var plain = protector.Unprotect(t.Token);
                if (plain is null)
                {
                    warnings.Add($"Could not decrypt the captured token for {t.Origin} — it was dropped.");
                    anyDropped = true;
                    continue;
                }
                t.Token = plain;
            }
            survivingTokens.Add(t);
        }
        state.SessionTokens.Clear();
        state.SessionTokens.AddRange(survivingTokens);

        var survivingCookies = new List<SessionCookie>(state.SessionCookies.Count);
        foreach (var c in state.SessionCookies)
        {
            if (SecretProtection.LooksProtected(c.Value))
            {
                var plain = protector.Unprotect(c.Value);
                if (plain is null)
                {
                    warnings.Add($"Could not decrypt the captured cookie \"{c.Name}\" for {c.Origin} — it was dropped.");
                    anyDropped = true;
                    continue;
                }
                c.Value = plain;
            }
            survivingCookies.Add(c);
        }
        state.SessionCookies.Clear();
        state.SessionCookies.AddRange(survivingCookies);

        for (int i = 0; i < state.Tabs.Count; i++)
        {
            var tab = state.Tabs[i];

            if (SecretProtection.LooksProtected(tab.Transport.ProxyPassword))
            {
                var proxyPlain = protector.Unprotect(tab.Transport.ProxyPassword!);
                if (proxyPlain is null)
                {
                    tab.Transport.ProxyPassword = null;
                    warnings.Add($"Could not decrypt the proxy password on open tab {i + 1} ({tab.Method} {tab.Path}) — it was left empty.");
                    anyDropped = true;
                }
                else tab.Transport.ProxyPassword = proxyPlain;
            }

            if (!SecretProtection.LooksProtected(tab.AuthSecret)) continue;
            var plain = protector.Unprotect(tab.AuthSecret!);
            if (plain is null)
            {
                tab.AuthSecret = null;
                warnings.Add($"Could not decrypt the auth secret on open tab {i + 1} ({tab.Method} {tab.Path}) — it was left empty.");
                anyDropped = true;
            }
            else
            {
                tab.AuthSecret = plain;
            }
        }

        for (int i = 0; i < state.History.Count; i++)
        {
            var h = state.History[i];

            if (h.Response is { } resp)
            {
                foreach (var row in resp.Headers)
                {
                    if (!SecretProtection.LooksProtected(row.Value)) continue;
                    var plainHeader = protector.Unprotect(row.Value);
                    if (plainHeader is null)
                    {
                        row.Value = "";
                        warnings.Add($"Could not decrypt the stored {row.Name} header on history entry {i + 1} ({h.Method} {h.EffectiveUrl}) — it was left empty.");
                        anyDropped = true;
                    }
                    else row.Value = plainHeader;
                }
                bool bodyOk = TryUnprotectBody(resp.Body, protector, out var newBody);
                resp.Body = newBody;
                if (!bodyOk)
                {
                    warnings.Add($"Could not decrypt the stored response body on history entry {i + 1} ({h.Method} {h.EffectiveUrl}) — it was left empty.");
                    anyDropped = true;
                }
            }

            if (SecretProtection.LooksProtected(h.Transport.ProxyPassword))
            {
                var proxyPlain = protector.Unprotect(h.Transport.ProxyPassword!);
                if (proxyPlain is null)
                {
                    h.Transport.ProxyPassword = null;
                    warnings.Add($"Could not decrypt the proxy password on history entry {i + 1} ({h.Method} {h.EffectiveUrl}) — it was left empty.");
                    anyDropped = true;
                }
                else h.Transport.ProxyPassword = proxyPlain;
            }

            if (!SecretProtection.LooksProtected(h.AuthSecret)) continue;
            var plain = protector.Unprotect(h.AuthSecret!);
            if (plain is null)
            {
                h.AuthSecret = null;
                warnings.Add($"Could not decrypt the auth secret on history entry {i + 1} ({h.Method} {h.EffectiveUrl}) — it was left empty.");
                anyDropped = true;
            }
            else
            {
                h.AuthSecret = plain;
            }
        }

        foreach (var root in state.Collections)
            UnprotectNode(root, protector, root.Name, warnings, ref anyDropped);

        foreach (var env in state.Environments)
        {
            foreach (var v in env.Variables)
            {
                if (!v.Secret || !SecretProtection.LooksProtected(v.Value)) continue;
                var plain = protector.Unprotect(v.Value);
                if (plain is null)
                {
                    v.Value = "";
                    warnings.Add($"Could not decrypt environment variable \"{v.Key}\" in \"{env.Name}\" — it was left empty.");
                    anyDropped = true;
                }
                else
                {
                    v.Value = plain;
                }
            }
        }

        if (anyDropped) warnings.Add(ClosingWarning);

        static void UnprotectNode(CollectionNode node, ISecretProtector p, string path, List<string> warnings, ref bool anyDropped)
        {
            if (node.Request is { } pr && SecretProtection.LooksProtected(pr.Transport.ProxyPassword))
            {
                var proxyPlain = p.Unprotect(pr.Transport.ProxyPassword!);
                if (proxyPlain is null)
                {
                    pr.Transport.ProxyPassword = null;
                    warnings.Add($"Could not decrypt the proxy password on saved request \"{path}\" — it was left empty.");
                    anyDropped = true;
                }
                else pr.Transport.ProxyPassword = proxyPlain;
            }

            if (node.Request is { } r && SecretProtection.LooksProtected(r.AuthSecret))
            {
                var plain = p.Unprotect(r.AuthSecret!);
                if (plain is null)
                {
                    r.AuthSecret = null;
                    warnings.Add($"Could not decrypt the auth secret on saved request \"{path}\" — it was left empty.");
                    anyDropped = true;
                }
                else
                {
                    r.AuthSecret = plain;
                }
            }
            if (node.KnownGood is { } kg)
            {
                var headers = new List<KeyValuePair<string, string>>(kg.Headers.Count);
                foreach (var hv in kg.Headers)
                {
                    if (!SecretProtection.LooksProtected(hv.Value)) { headers.Add(hv); continue; }
                    var plainHeader = p.Unprotect(hv.Value);
                    if (plainHeader is null)
                    {
                        warnings.Add($"Could not decrypt the stored {hv.Key} header of the known-good response on saved request \"{path}\" — it was left empty.");
                        anyDropped = true;
                    }
                    headers.Add(new(hv.Key, plainHeader ?? ""));
                }
                bool bodyOk = TryUnprotectBody(kg.Body, p, out var newBody);
                node.KnownGood = kg with { Body = newBody, Headers = headers };
                if (!bodyOk)
                {
                    warnings.Add($"Could not decrypt the stored known-good response on saved request \"{path}\" — it was left empty.");
                    anyDropped = true;
                }
            }
            foreach (var child in node.Children)
                UnprotectNode(child, p, path + "/" + child.Name, warnings, ref anyDropped);
        }
    }

    /// <summary>Remove every secret from a workspace, leaving it usable with the secrets simply
    /// absent. Call it on a CLONE — never on live state. This is what an export does by default:
    /// an exported workspace is a file people email to each other.</summary>
    public static SecretStripSummary Strip(AppState state)
    {
        int tokens = state.SessionTokens.Count;
        state.SessionTokens.Clear();

        int cookies = state.SessionCookies.Count;
        state.SessionCookies.Clear();

        int authSecrets = 0;
        // A proxy password is a credential like any other, and it lives on the saved request's own
        // transport rather than beside the auth secret — which is exactly why it was missed here.
        int proxyPasswords = 0;
        foreach (var tab in state.Tabs)
        {
            if (!string.IsNullOrEmpty(tab.AuthSecret)) { tab.AuthSecret = null; authSecrets++; }
            if (!string.IsNullOrEmpty(tab.Transport.ProxyPassword))
            { tab.Transport.ProxyPassword = null; proxyPasswords++; }
        }

        int bodies = 0, headers = 0;
        foreach (var h in state.History)
        {
            if (!string.IsNullOrEmpty(h.AuthSecret)) { h.AuthSecret = null; authSecrets++; }
            if (!string.IsNullOrEmpty(h.Transport.ProxyPassword))
            { h.Transport.ProxyPassword = null; proxyPasswords++; }
            // History is a record of what happened: an entry that keeps its method, URL, status and
            // headers but not its body is still a useful record, exactly as a tab that keeps
            // everything but its auth secret is still a useful tab. Every other field is untouched.
            if (h.Response is { } resp && resp.Body is { Length: > 0 })
            {
                resp.Body = Array.Empty<byte>();
                bodies++;
            }
            // A login's Set-Cookie or a token header is the session itself; the name stays.
            if (h.Response is { } r2)
                foreach (var row in r2.Headers)
                    if (SecretHeaders.IsSecret(row.Name) && row.Value.Length > 0) { row.Value = ""; headers++; }
        }

        foreach (var root in state.Collections)
            authSecrets += StripNode(root, ref bodies, ref proxyPasswords);

        int variables = 0;
        foreach (var env in state.Environments)
            foreach (var v in env.Variables)
                if (v.Secret && !string.IsNullOrEmpty(v.Value)) { v.Value = ""; variables++; }

        return new SecretStripSummary(tokens, cookies, authSecrets, variables, bodies, proxyPasswords, headers);

        static int StripNode(CollectionNode node, ref int bodies, ref int proxyPasswords)
        {
            int count = 0;
            if (node.Request is { } r && !string.IsNullOrEmpty(r.AuthSecret)) { r.AuthSecret = null; count++; }
            if (node.Request is { } pr && !string.IsNullOrEmpty(pr.Transport.ProxyPassword))
            { pr.Transport.ProxyPassword = null; proxyPasswords++; }
            // Unlike a history body, a known-good snapshot IS the diff baseline: emptying its body
            // rather than removing it would make every later `certapi send --diff known-good` report
            // a spurious whole-body difference — it would lie, the same way CollectionNode.RecordResult
            // already refuses to record a failing send as the baseline. Removing it instead produces
            // the existing, honest "no recorded known-good response yet" data error.
            if (node.KnownGood is not null)
            {
                node.KnownGood = null;
                bodies++;
            }
            foreach (var child in node.Children) count += StripNode(child, ref bodies, ref proxyPasswords);
            return count;
        }
    }
}

/// <summary>How many secrets and stored response bodies a strip removed, so the caller can say what
/// left the building.</summary>
public sealed record SecretStripSummary(int Tokens, int Cookies, int AuthSecrets, int Variables,
                                       int ResponseBodies, int ProxyPasswords = 0, int ResponseHeaders = 0)
{
    /// <summary>True when anything at all was stripped.</summary>
    public bool Any => Tokens > 0 || Cookies > 0 || AuthSecrets > 0 || Variables > 0
                    || ResponseBodies > 0 || ProxyPasswords > 0 || ResponseHeaders > 0;

    /// <summary>A human list of what went, e.g. "2 captured tokens, 1 saved auth secret".
    /// Empty string when nothing was stripped.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Tokens > 0) parts.Add(Pluralize(Tokens, "captured token"));
        if (Cookies > 0) parts.Add(Pluralize(Cookies, "captured cookie"));
        if (AuthSecrets > 0) parts.Add(Pluralize(AuthSecrets, "saved auth secret"));
        if (Variables > 0) parts.Add(Pluralize(Variables, "secret variable"));
        if (ResponseBodies > 0) parts.Add(Pluralize(ResponseBodies, "stored response body", "stored response bodies"));
        if (ProxyPasswords > 0) parts.Add(Pluralize(ProxyPasswords, "saved proxy password"));
        if (ResponseHeaders > 0) parts.Add(Pluralize(ResponseHeaders, "stored credential header"));
        return string.Join(", ", parts);

        static string Pluralize(int count, string noun, string? plural = null) =>
            $"{count} {(count == 1 ? noun : plural ?? noun + "s")}";
    }
}
