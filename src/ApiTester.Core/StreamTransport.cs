using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>The one handler factory for connections that live outside <see cref="ApiClient"/>'s
/// request pipeline — Server-Sent Event streams, WebSocket handshakes, and OAuth token fetches.
/// Each of those used to build its own bare <see cref="SocketsHttpHandler"/> with none of the
/// transport work the rest of the product carries; this routes them through the same three shared
/// tables everything else uses (<see cref="ProxyConfiguration.Apply"/>,
/// <see cref="RevocationCheck.Apply"/>, <see cref="RevocationCheck.Decide"/>), so a proxy,
/// revocation, or trust-pin setting honored on `send` can no longer be silently ignored on a
/// stream. Retries, redirects, and HTTP-version pinning stay out on purpose: a stream re-subscribe
/// has side effects a retry must not hide, and those belong to the request pipeline that owns
/// them.</summary>
public static class StreamTransport
{
    /// <param name="trustServerCertificate">Consulted when the server's certificate fails ordinary
    /// validation, so a host pinned with `certapi trust add` is reachable without --insecure — the
    /// same seam <see cref="ApiClient"/> uses.</param>
    public static SocketsHttpHandler CreateHandler(
        X509Certificate2? clientCertificate,
        TransportOptions options,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        Uri? destination = null)
    {
        var handler = new SocketsHttpHandler
        {
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        // A stream, an upgrade or a token fetch has one destination, so the system proxy's own
        // failover list survives for a host the bypass list does not match.
        ProxyConfiguration.Apply(handler, options, destination);

        var ssl = new SslClientAuthenticationOptions
        {
            // Always installed, not only under --insecure: with RevocationMode.None, no strict
            // flag, and no pin, Decide accepts exactly when the ordinary rules would have, so the
            // default path is behaviorally the platform's own validation.
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                Judge(options, TrustContext.HostOf(sender, options.Sni), cert, chain, errors, trustServerCertificate)
        };
        RevocationCheck.Apply(ssl, options);
        TlsPolicy.Apply(ssl, options with { Sni = null });
        if (options.ConnectTimeout is { } connect) handler.ConnectTimeout = connect;
        if (clientCertificate is not null)
            ssl.ClientCertificates = new X509CertificateCollection { clientCertificate };
        handler.SslOptions = ssl;
        return handler;
    }

    /// <summary>One validation for every stream: the user's extra roots (--ca-file) reconsidered
    /// first, then the shared revocation/pin table.</summary>
    private static bool Judge(TransportOptions options, string? host, X509Certificate? cert, X509Chain? chain,
        SslPolicyErrors errors, Func<X509Certificate2?, bool>? trustServerCertificate)
    {
        var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(
            errors, RevocationCheck.ChainFlagsOf(chain), cert, chain, options.ExtraRoots);
        return RevocationCheck.Decide(
            options.Revocation, options.RevocationStrict, options.IgnoreServerCertificateErrors,
            judgedErrors, judgedFlags,
            trustServerCertificate is null
                ? null
                : () => TrustContext.Run(chain, judgedErrors, host, () => trustServerCertificate(cert as X509Certificate2))).Accepted;
    }
}
