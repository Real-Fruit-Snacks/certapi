namespace ApiTester.Core;

public enum ApiErrorKind
{
    None,
    CertificateRefused,
    ServerCertificateUntrusted,
    Network,
    Timeout,
    Unknown,
    // Appended so the string values already written into saved workspaces keep their meaning.
    TooManyRedirects,
    ProxyFailure,
    NameResolution,
    ConnectionRefused,
    ConnectionReset,
    // A revoked certificate is a different finding than a merely untrusted one -- separating the
    // two is the entire point of the revocation-checking feature that added this value -- so it
    // gets its own kind rather than folding into ServerCertificateUntrusted above. Appended for the
    // same reason as every value above it since the comment was written: the string already saved
    // into a workspace must keep meaning what it meant when it was written.
    ServerCertificateRevoked
}

/// <summary>One link of a failure's InnerException chain. The useful facts about a refused client
/// certificate — the AuthenticationException and the SChannel status code — are never in the
/// outermost message, so the chain is kept rather than flattened to a single string.</summary>
public sealed record ErrorDetail(string ExceptionType, string Message, int? NativeErrorCode);

public sealed record ApiError(
    ApiErrorKind Kind,
    string Message,
    IReadOnlyList<ErrorDetail>? Detail = null,
    System.Net.Sockets.SocketError? SocketErrorCode = null);

/// <summary>One hop of a redirect chain. <paramref name="AuthorizationDropped"/> and
/// <paramref name="SchemeDowngrade"/> are the facts a user cannot otherwise see: a cross-origin
/// redirect strips the Authorization header and presents the client certificate somewhere the
/// user never named.</summary>
public sealed record RedirectHop(
    int StatusCode,
    string From,
    string To,
    bool AuthorizationDropped,
    bool SchemeDowngrade)
{
    /// <summary>How long this hop itself took — the request that answered with the 3xx, not the
    /// whole chain. Default zero keeps every existing construction site compiling and truthful
    /// (an unmeasured hop claims nothing); <see cref="ApiClient"/> sets it for real.</summary>
    public TimeSpan Elapsed { get; init; }
}

public sealed record ApiResponse
{
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string? ContentType { get; init; }
    public TimeSpan Elapsed { get; init; }
    public ApiError? Error { get; init; }
    public ConnectionInfo? Connection { get; init; }

    /// <summary>The redirects followed to reach this response, oldest first. Empty when the first
    /// response was final.</summary>
    public IReadOnlyList<RedirectHop> Redirects { get; init; } = Array.Empty<RedirectHop>();

    /// <summary>How many attempts this result took. 1 when no retry happened, which is every response
    /// this client produced before retry existed.</summary>
    public int Attempts { get; init; } = 1;

    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;
}

/// <summary>Transport-level diagnostics captured during the request.</summary>
public sealed record ConnectionInfo
{
    public bool ViaProxy { get; init; }

    /// <summary>The proxy the request actually went through, null when the connection was direct.
    /// Naming it is what lets a user work out where a client certificate got stripped.</summary>
    public string? ProxyUri { get; init; }

    /// <summary>Where this request actually went, and why. Set together with <see cref="ViaProxy"/>
    /// by <see cref="ApiClient"/> — <see cref="ViaProxy"/> is true exactly when this is
    /// <see cref="ApiTester.Core.ProxyDisposition.Proxied"/>. Defaults to
    /// <see cref="ApiTester.Core.ProxyDisposition.Direct"/> so every other construction site in the
    /// repo stays truthful without change.</summary>
    public ProxyDisposition ProxyDisposition { get; init; } = ProxyDisposition.Direct;

    /// <summary>The bypass rule that sent this request direct, e.g. ".corp" — null unless
    /// <see cref="ProxyDisposition"/> is <see cref="ApiTester.Core.ProxyDisposition.BypassedByRule"/>.</summary>
    public string? ProxyBypassRule { get; init; }
    public string? TlsProtocol { get; init; }
    public string? CipherSuite { get; init; }
    public bool ClientCertificateSent { get; init; }
    public string? ClientCertificateSubject { get; init; }
    public string? ServerCertificateSubject { get; init; }
    public string? ServerCertificateIssuer { get; init; }
    public string? ServerCertificateThumbprint { get; init; }
    /// <summary>The thumbprint of the certificate directly above the leaf in the presented chain
    /// — null for a self-signed leaf. What `trust add --ca` pins.</summary>
    public string? ServerCertificateIssuerThumbprint { get; init; }
    /// <summary>Base64 SHA-256 of the leaf's SubjectPublicKeyInfo. What `trust add --spki` pins.</summary>
    public string? ServerCertificateSpkiSha256 { get; init; }
    public DateTime? ServerCertificateNotAfter { get; init; }
    public IReadOnlyList<string> ServerCertificateChain { get; init; } = Array.Empty<string>();

    /// <summary>Which revocation checking this send asked for: <see cref="ApiTester.Core.RevocationMode.None"/>
    /// (the default), <see cref="ApiTester.Core.RevocationMode.Offline"/>, or
    /// <see cref="ApiTester.Core.RevocationMode.Online"/>. Set together with
    /// <see cref="RevocationStatus"/> by <see cref="ApiClient"/> — mirrors how <see cref="ViaProxy"/>
    /// and <see cref="ProxyDisposition"/> are set together above. Defaults to
    /// <see cref="ApiTester.Core.RevocationMode.None"/> so every other construction site in the
    /// repo stays truthful without change.</summary>
    public RevocationMode RevocationMode { get; init; } = RevocationMode.None;

    /// <summary>What the check actually came back with — checked-and-good, revoked, unknown, or not
    /// checked at all. Reported whether or not checking ran, because "was revocation actually
    /// verified?" must be answerable without guessing rather than only ever appearing on a refusal.
    /// Defaults to <see cref="ApiTester.Core.RevocationStatus.NotChecked"/>, matching
    /// <see cref="RevocationMode"/>'s default of <see cref="ApiTester.Core.RevocationMode.None"/>,
    /// so every other construction site in the repo stays truthful without change.</summary>
    public RevocationStatus RevocationStatus { get; init; } = RevocationStatus.NotChecked;
}
