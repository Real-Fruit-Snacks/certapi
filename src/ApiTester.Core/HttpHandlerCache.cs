using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Everything that decides whether two direct-connection requests may share a pooled
/// handler — and so a connection that already completed its TLS handshake and, where Windows auth
/// applies, its challenge/response. Two requests share only when every field here matches: a wrong
/// sharing key is a security defect (a connection authenticated with one client certificate or
/// trust decision leaking into a request that never asked for it), not a performance bug, so this
/// key is deliberately conservative — when in doubt, a new field means a new handler.</summary>
/// <param name="ClientCertificateHash">The certificate's SHA-256 hash, not <c>Thumbprint</c>
/// (SHA-1): a key that decides whether two requests may share an authenticated connection must not
/// rest on a hash with practical collision attacks.</param>
/// <param name="ClientCertificateHasPrivateKey">The same certificate without its private key
/// cannot complete client authentication, so it must never ride a connection that one with the key
/// established.</param>
/// <param name="Proxy">Copied straight off <see cref="TransportOptions"/>: a different proxy mode
/// changes how — or whether — the connection reaches the server at all.</param>
/// <param name="ProxyUrl">Which proxy, when one is named explicitly.</param>
/// <param name="ProxyUser">Proxy credentials are answered once per connection (Basic/NTLM/Negotiate
/// to the proxy itself), so a different user must not ride a connection authenticated as someone
/// else.</param>
/// <param name="ProxyPassword">See <see cref="ProxyUser"/>.</param>
/// <param name="NoProxy">The pooled handler now carries the bypass list inside its own
/// <c>IWebProxy</c> (see <see cref="ProxyConfiguration"/>), so two transports with different lists
/// must never share one — a request would otherwise ride a connection configured by someone else's
/// rules.</param>
/// <param name="IgnoreServerCertificateErrors">Part of the trust decision made once, at handshake
/// time, for the life of the connection — a caller who did not ask to skip validation must never
/// end up on a connection where someone else's request did.</param>
/// <param name="Revocation">Revocation checking is also part of the trust decision made once, at
/// handshake time, for the life of the connection: a request that asked for <c>online</c> checking
/// must never ride a pooled connection that was established without it, and — just as
/// important — a request that asked for no checking at all must never be silently upgraded by
/// riding one that was. This is a security-relevant sharing key, not a performance detail.</param>
/// <param name="RevocationStrict">Whether an indeterminate revocation answer was fatal is
/// negotiated at the very same handshake as <see cref="Revocation"/> itself, so it must never
/// diverge between two requests sharing a connection for the same reason <see cref="Revocation"/>
/// must not.</param>
/// <param name="TrustServerCertificate">Compared by the record's default equality, which for a
/// delegate means same method and same target reference — behavioral identity. There is no sound
/// way to fingerprint a delegate's behavior, so two independently-created closures are never equal
/// even if they happen to decide the same way; that is deliberate conservatism, not a bug.</param>
/// <param name="Decompress">Changes <c>AutomaticDecompression</c> on the handler itself, which is
/// handler-wide, not per-request.</param>
/// <param name="Version">HTTP version pinning is negotiated (via ALPN) at connection time, so a
/// pinned HTTP/2 request must never land on a connection opened for HTTP/1.1, or Auto.</param>
/// <param name="ResolveOverrides">Every <c>--resolve</c> pin, host:port=address, joined in the
/// order given. A reordered list makes a new handler; harmless, and conservative rather than trying
/// to prove two differently-ordered lists mean the same thing.</param>
/// <param name="WindowsAuth">Negotiate/NTLM authenticates the *connection*, not the request, so two
/// requests with different credentials must never share one even if everything else matches.</param>
/// <param name="ExtraRoots">Which extra roots (--ca-file) the handshake was judged against; part of
/// the trust decision, so part of the key.</param>
/// <param name="MinTls">The protocol floor is negotiated at handshake time.</param>
/// <param name="Sni">The server name sent is negotiated at handshake time.</param>
/// <param name="ConnectTimeout">Handler-wide setting.</param>
internal sealed record HandlerKey(
    string? ClientCertificateHash,
    bool ClientCertificateHasPrivateKey,
    ProxyMode Proxy,
    string? ProxyUrl,
    string? ProxyUser,
    string? ProxyPassword,
    string NoProxy,
    bool IgnoreServerCertificateErrors,
    RevocationMode Revocation,
    bool RevocationStrict,
    Func<X509Certificate2?, bool>? TrustServerCertificate,
    bool Decompress,
    HttpVersionMode Version,
    string ResolveOverrides,
    string WindowsAuth,
    string? ExtraRoots,
    TlsFloor MinTls,
    string? Sni,
    TimeSpan? ConnectTimeout,
    string? PacUrl,
    bool Offline,
    AddressFamilyPreference AddressFamily)
{
    /// <summary>The one place this key is ever built, so every caller agrees on exactly what goes
    /// into it.</summary>
    public static HandlerKey For(
        X509Certificate2? clientCertificate,
        TransportOptions transport,
        Func<X509Certificate2?, bool>? trustServerCertificate,
        WindowsAuthOptions? windowsAuth) => new(
        clientCertificate?.GetCertHashString(HashAlgorithmName.SHA256),
        clientCertificate?.HasPrivateKey ?? false,
        transport.Proxy,
        transport.ProxyUrl,
        transport.ProxyUser,
        transport.ProxyPassword,
        ProxyBypass.Format(transport.NoProxy),
        transport.IgnoreServerCertificateErrors,
        transport.Revocation,
        transport.RevocationStrict,
        trustServerCertificate,
        transport.Decompress,
        transport.Version,
        string.Join(";", transport.Resolve.Select(r => $"{r.Host.ToLowerInvariant()}:{r.Port}={r.Address}")),
        FormatWindowsAuth(windowsAuth),
        // The set's content hash, not its file names: two runs naming the same roots share, two
        // naming different roots never do — a trust decision made once per connection.
        transport.ExtraRoots?.Fingerprint,
        transport.MinTls,
        transport.Sni,
        transport.ConnectTimeout,
        transport.PacUrl,
        transport.Offline,
        transport.AddressFamily);

    /// <summary>"none" when there is no Windows auth at all, "default" for the signed-in account
    /// (indistinguishable from any other caller asking for the same thing), otherwise the explicit
    /// credentials themselves — because those, not the mere fact that Windows auth is in play, are
    /// what the connection ends up authenticated as.</summary>
    private static string FormatWindowsAuth(WindowsAuthOptions? auth)
    {
        if (auth is null) return "none";
        if (auth.UseDefaultCredentials) return "default";
        // A control character rather than a printable delimiter: domain/username/password
        // are free-form text that could otherwise collide across the join (e.g. a literal "|"
        // typed into a password producing the same joined string a different split would).
        return string.Join('\u0001', auth.Domain, auth.Username, auth.Password);
    }
}

/// <summary>The handshake facts for one connection, captured once when it is established and read
/// by every request that goes on to reuse it. A property of the connection, not of any one
/// request — see the remarks on <see cref="HandlerEntry"/> for why that is now the truthful thing
/// to report under pooling.</summary>
internal sealed record HandshakeInfo(
    string? TlsProtocol,
    string? CipherSuite,
    bool ClientCertificateSent,
    string? ServerCertificateSubject,
    string? ServerCertificateIssuer,
    string? ServerCertificateThumbprint,
    DateTime? ServerCertificateNotAfter,
    IReadOnlyList<string> ServerCertificateChain,
    RevocationStatus RevocationStatus)
{
    public string? ServerCertificateSpkiSha256 { get; init; }
    public string? ServerCertificateIssuerThumbprint { get; init; }
}

/// <summary>Carried by every server-certificate refusal so the classification in ApiClient's catch
/// block can report what the revocation check found even though the handshake never completed and
/// so recorded no <see cref="HandshakeInfo"/> of its own.</summary>
internal interface IServerCertificateRefusal
{
    RevocationStatus RevocationStatus { get; }
}

/// <summary>Thrown from the connect callback when this client's own validation refused the server
/// certificate for an ordinary trust reason (expired, untrusted root, name mismatch — anything
/// that is not revocation's business). It derives from <see cref="AuthenticationException"/> on
/// purpose: that is what the failing handshake would otherwise have thrown, so HttpClient wraps it,
/// and reports it, identically — the only difference is that it also carries the fact that *this*
/// was a trust refusal, for the classification in ApiClient's catch block.</summary>
internal sealed class ServerCertificateUntrustedException : AuthenticationException, IServerCertificateRefusal
{
    public RevocationStatus RevocationStatus { get; }

    /// <summary>The revocation status is unknown at this call site — every caller from before
    /// revocation checking existed reaches this overload, and none of them ever checked it.</summary>
    public ServerCertificateUntrustedException(string message, Exception innerException)
        : this(RevocationStatus.NotChecked, message, innerException)
    {
    }

    public ServerCertificateUntrustedException(RevocationStatus status, string message, Exception innerException)
        : base(message, innerException)
    {
        RevocationStatus = status;
    }
}

/// <summary>Thrown from the connect callback when revocation itself is what refused the server
/// certificate — either a genuine revocation (<see cref="ApiTester.Core.RevocationStatus.Revoked"/>)
/// or, under <c>--revocation-strict</c>, an indeterminate answer
/// (<see cref="ApiTester.Core.RevocationStatus.Unknown"/>) — both of which are "revocation refused
/// this", and the carried status is what tells them apart. It derives from
/// <see cref="AuthenticationException"/> on purpose, for the same reason
/// <see cref="ServerCertificateUntrustedException"/> does: that is what the failing handshake would
/// otherwise have thrown, so HttpClient wraps it, and reports it, identically.</summary>
internal sealed class ServerCertificateRevokedException : AuthenticationException, IServerCertificateRefusal
{
    public RevocationStatus RevocationStatus { get; }

    public ServerCertificateRevokedException(RevocationStatus status, string message, Exception innerException)
        : base(message, innerException)
    {
        RevocationStatus = status;
    }
}

/// <summary>One cached, shareable handler: the <see cref="SocketsHttpHandler"/> itself, the client
/// certificate copy it owns, and the handshake recorded for every origin a connection through it
/// has established. The handler is shared by every request whose <see cref="HandlerKey"/> matches,
/// so its <c>ConnectCallback</c> runs once per *connection* it ever opens rather than once per
/// request — which is exactly why the diagnostics live here, keyed by origin, instead of in a
/// closure that only one request could ever read.
/// <para>Reference-counted (<see cref="AddLease"/>/<see cref="DropLease"/>) so the bounded cache
/// that owns this entry can evict it without ever disposing a handler a send still has in
/// flight: eviction only marks the entry (<see cref="Dispose"/>), and the underlying handler is
/// disposed the moment nothing is using it any more — immediately, if that is already true.</para></summary>
internal sealed class HandlerEntry : IDisposable
{
    public SocketsHttpHandler Handler { get; }

    // The entry can outlive the send that first creates it — it is cached and reused by later
    // sends — so it must not depend on that caller's X509Certificate2 staying alive or undisposed.
    // The copy constructor duplicates the certificate context, including the private-key
    // association, so this entry owns a reference good for exactly as long as the entry itself
    // lives, independent of whatever the original caller does with theirs afterward.
    private readonly X509Certificate2? _clientCertificateCopy;

    private readonly ConcurrentDictionary<string, HandshakeInfo> _handshakesByOrigin = new();

    // A diagnostics cache, not a correctness store: bounded so a handler that ends up handling
    // requests to an unbounded number of distinct origins (many --resolve pins reusing one trust
    // configuration, say) cannot grow this dictionary without limit. Losing old diagnostics is
    // harmless; leaking memory for the life of the process is not.
    private const int MaxTrackedOrigins = 256;

    private readonly object _gate = new();
    private int _leaseCount;
    private bool _evicted;
    private bool _disposed;

    public HandlerEntry(SocketsHttpHandler handler, X509Certificate2? clientCertificateCopy)
    {
        Handler = handler;
        _clientCertificateCopy = clientCertificateCopy;
    }

    public void RecordHandshake(string origin, HandshakeInfo info)
    {
        if (_handshakesByOrigin.Count > MaxTrackedOrigins) _handshakesByOrigin.Clear();
        _handshakesByOrigin[origin] = info;
    }

    public HandshakeInfo? Lookup(string origin) =>
        _handshakesByOrigin.TryGetValue(origin, out var info) ? info : null;

    /// <summary>Called by <see cref="HttpHandlerCache.Rent"/>: a send that is about to use this
    /// entry's handler holds one lease until it finishes, so an eviction racing with it can mark
    /// the entry evicted without disposing the handler out from under an in-flight request.</summary>
    public void AddLease()
    {
        lock (_gate) _leaseCount++;
    }

    /// <summary>Called by <see cref="HttpHandlerCache.Return"/> once a send is done with this
    /// entry. Disposes the handler only if it was evicted in the meantime and this was its last
    /// lease — otherwise the entry is still live in the cache and stays open for reuse.</summary>
    public void DropLease()
    {
        bool shouldDispose;
        lock (_gate)
        {
            _leaseCount--;
            shouldDispose = _evicted && _leaseCount == 0;
        }
        if (shouldDispose) DisposeCore();
    }

    /// <summary>Marks this entry no longer reachable from the cache (evicted, or the whole cache
    /// disposing) and disposes it immediately if nothing is using it right now — otherwise the
    /// last <see cref="DropLease"/> call will, once the in-flight send finishes.</summary>
    public void Dispose()
    {
        bool shouldDispose;
        lock (_gate)
        {
            _evicted = true;
            shouldDispose = _leaseCount == 0;
        }
        if (shouldDispose) DisposeCore();
    }

    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Closes every pooled socket this handler ever opened.
        Handler.Dispose();
        _clientCertificateCopy?.Dispose();
    }
}

/// <summary>An instance-scoped, bounded LRU cache of <see cref="HandlerEntry"/> values, keyed by
/// <see cref="HandlerKey"/>. Owned by exactly one <see cref="ApiClient"/> and never static: two
/// ApiClient instances must never be able to observe — or reuse — each other's pooled
/// connections.</summary>
internal sealed class HttpHandlerCache : IDisposable
{
    // A long-running GUI session must not accumulate a handler per distinct certificate/trust
    // combination for the life of the process. 8 is generous for the realistic case — a handful of
    // endpoints and certificates open in one session — while still bounding memory and open
    // sockets for the unrealistic one.
    private const int Capacity = 8;

    private readonly object _gate = new();

    // Kept in most-recently-used order (index 0 is least-recently-used) rather than a dictionary
    // plus a linked list: the cache never holds more than Capacity entries, so a linear scan costs
    // nothing that matters, and there is a lot less code that could get the bookkeeping wrong.
    private readonly List<(HandlerKey Key, HandlerEntry Entry)> _entries = new();
    private bool _disposed;

    /// <summary>Returns the cached entry for <paramref name="key"/>, promoted to most-recently-used,
    /// or builds one with <paramref name="create"/>, caches it, and returns that. Either way the
    /// returned entry holds one lease that the caller must release with <see cref="Return"/> once
    /// the send that asked for it is done.</summary>
    public HandlerEntry Rent(HandlerKey key, Func<HandlerEntry> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Key.Equals(key))
                {
                    var entry = _entries[i].Entry;
                    _entries.RemoveAt(i);
                    _entries.Add((key, entry));
                    entry.AddLease();
                    return entry;
                }
            }

            var created = create();
            created.AddLease();
            _entries.Add((key, created));

            // Evict least-recently-used entries once over capacity. Eviction only marks an entry —
            // see HandlerEntry.Dispose — so a handler a concurrent send still has leased is never
            // disposed out from under it; it is disposed the moment that send finishes instead.
            while (_entries.Count > Capacity)
            {
                var victim = _entries[0].Entry;
                _entries.RemoveAt(0);
                victim.Dispose();
            }

            return created;
        }
    }

    /// <summary>Releases the lease a matching <see cref="Rent"/> call took out. Must be called
    /// exactly once per successful <see cref="Rent"/>, however the send that rented it ends.</summary>
    public void Return(HandlerEntry entry) => entry.DropLease();

    /// <summary>How many entries the cache holds.</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Drops every entry, as an eviction does (one a send still holds goes when that send
    /// returns it), and stays usable: the next send builds a new one.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var (_, entry) in _entries) entry.Dispose();
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Marks every entry evicted; one with a send still in flight is disposed once that
            // send calls Return, exactly as an ordinary eviction would be.
            foreach (var (_, entry) in _entries) entry.Dispose();
            _entries.Clear();
        }
    }
}
