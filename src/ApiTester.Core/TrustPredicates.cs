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
    public Func<X509Certificate2?, bool> For(string host) =>
        _byHost.GetOrAdd(host, h => c => c is not null && TrustService.IsTrusted(_state, h, c, TrustContext.CurrentChain));
}

/// <summary>How a validation callback lends the presented chain to the trust predicate without
/// every predicate in the product changing shape. The callback runs the predicate synchronously on
/// its own thread, so a thread-bound slot set just before the call and cleared just after is exact.
/// A predicate invoked outside a callback sees no chain, which an issuer pin treats as "no issuer
/// to match" rather than as trust.</summary>
public static class TrustContext
{
    [ThreadStatic] private static X509Chain? _chain;

    public static X509Chain? CurrentChain => _chain;

    public static bool Run(X509Chain? chain, Func<bool> predicate)
    {
        var previous = _chain;
        _chain = chain;
        try { return predicate(); }
        finally { _chain = previous; }
    }
}
