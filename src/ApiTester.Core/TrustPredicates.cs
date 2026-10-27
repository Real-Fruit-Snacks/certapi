using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Memoizes the per-host server-certificate trust predicate a caller hands to
/// <see cref="ApiClient.SendAsync"/>. <see cref="HttpHandlerCache"/>'s sharing key compares
/// <c>trustServerCertificate</c> by delegate identity (same method, same target) because there is
/// no sound way to fingerprint a delegate's behavior — so a caller that builds a fresh lambda for
/// every request could never share a pooled connection with itself, even against the same host with
/// the same everything else. One <see cref="TrustPredicates"/> instance per command run (or per GUI
/// window) fixes that: the same host gets the same delegate instance every time, so repeated
/// requests to it are eligible to share a handler. Memoizing changes nothing about the trust
/// decision itself — the returned predicate still closes over the same <see cref="AppState"/>, so a
/// pin added mid-run is honored exactly as it was before this type existed.</summary>
public sealed class TrustPredicates
{
    private readonly AppState _state;
    private readonly ConcurrentDictionary<string, Func<X509Certificate2?, bool>> _byHost =
        new(StringComparer.OrdinalIgnoreCase);

    public TrustPredicates(AppState state)
    {
        _state = state;
    }

    /// <summary>The trust predicate for <paramref name="host"/>: behaviorally identical to
    /// <c>c =&gt; c is not null &amp;&amp; TrustService.IsTrusted(state, host, c.Thumbprint!)</c>, but
    /// the same delegate instance is returned for every call with the same host (compared
    /// case-insensitively), so repeated requests to it can share a pooled connection.</summary>
    /// <remarks>The pins consulted are the ones for the host this connection actually reached when
    /// the validation callback says so (<see cref="TrustContext.CurrentHost"/>): a redirect to
    /// another host is judged by that host's pins, never by the pins of the host the request
    /// started at.</remarks>
    public Func<X509Certificate2?, bool> For(string host) =>
        _byHost.GetOrAdd(host, h => c => c is not null &&
            TrustService.IsTrusted(_state, TrustContext.CurrentHost ?? h, c, TrustContext.CurrentChain, TrustContext.CurrentPolicyErrors));
}

/// <summary>How a validation callback lends the presented chain to the trust predicate without
/// every predicate in the product changing shape. The callback runs the predicate synchronously on
/// its own thread, so a thread-bound slot set just before the call and cleared just after is exact.
/// A predicate invoked outside a callback sees no chain, which an issuer pin treats as "no issuer
/// to match" rather than as trust.</summary>
public static class TrustContext
{
    [ThreadStatic] private static X509Chain? _chain;
    [ThreadStatic] private static System.Net.Security.SslPolicyErrors _errors;
    [ThreadStatic] private static string? _host;

    public static X509Chain? CurrentChain => _chain;

    /// <summary>What the platform (and <c>--ca-file</c>) found wrong with the certificate. An issuer
    /// pin waives an untrusted chain, never a certificate issued for some other name.</summary>
    public static System.Net.Security.SslPolicyErrors CurrentPolicyErrors => _errors;

    /// <summary>The host this connection was opened to, when the callback knows it and it differs
    /// from the name the predicate was made for (a redirect hop); null otherwise.</summary>
    public static string? CurrentHost => _host;

    public static bool Run(X509Chain? chain, System.Net.Security.SslPolicyErrors errors, Func<bool> predicate) =>
        Run(chain, errors, null, predicate);

    public static bool Run(X509Chain? chain, System.Net.Security.SslPolicyErrors errors, string? host, Func<bool> predicate)
    {
        var (previousChain, previousErrors, previousHost) = (_chain, _errors, _host);
        (_chain, _errors, _host) = (chain, errors, host);
        try { return predicate(); }
        finally { (_chain, _errors, _host) = (previousChain, previousErrors, previousHost); }
    }

    /// <summary>The host a TLS validation callback's <paramref name="sender"/> was connecting to —
    /// an <see cref="System.Net.Security.SslStream"/>'s target, or a request's host — unless that
    /// is <paramref name="sni"/>, a name chosen for the handshake rather than the host whose pins
    /// apply.</summary>
    public static string? HostOf(object? sender, string? sni)
    {
        string? host = sender switch
        {
            System.Net.Security.SslStream s => s.TargetHostName,
            System.Net.Http.HttpRequestMessage r => r.RequestUri?.IdnHost,
            _ => null
        };
        if (string.IsNullOrEmpty(host)) return null;
        if (sni is not null && string.Equals(host, sni, StringComparison.OrdinalIgnoreCase)) return null;
        return host;
    }
}
