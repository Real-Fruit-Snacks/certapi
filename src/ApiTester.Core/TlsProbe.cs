using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>Plain-text protocols that upgrade to TLS on the same connection.</summary>
public enum StartTls { None, Smtp, Imap, Pop3, Ftp, Ldap, Postgres }

/// <summary>What a TLS handshake with any service showed: the protocol and cipher agreed, the
/// server's chain, whether it asked for a client certificate and which issuers it would accept.</summary>
public sealed record TlsProbeResult
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? RemoteAddress { get; init; }
    public bool Connected { get; init; }
    public string? Error { get; init; }
    public string? Protocol { get; init; }
    public string? Cipher { get; init; }
    public string? Alpn { get; init; }
    public bool ClientCertificateRequested { get; init; }
    public IReadOnlyList<string> AcceptableIssuers { get; init; } = Array.Empty<string>();
    public string? ClientCertificateSent { get; init; }
    public bool ServerTrusted { get; init; }
    public string? TrustProblem { get; init; }
    public IReadOnlyList<TlsCertificateInfo> Chain { get; init; } = Array.Empty<TlsCertificateInfo>();
    public TimeSpan Elapsed { get; init; }
}

public sealed record TlsCertificateInfo(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter,
    string Thumbprint, string SpkiSha256, string KeyAlgorithm, int KeySize, IReadOnlyList<string> SubjectAlternativeNames);

/// <summary>Handshakes with any TLS service — HTTPS, LDAPS, SMTPS, a database — or upgrades a plain
/// one with STARTTLS first, and reports what it saw. The issuer list is the server's own
/// CertificateRequest, which is the answer to "which of my certificates will this server take?".</summary>
public static class TlsProbe
{
    public static async Task<TlsProbeResult> ProbeAsync(string host, int port, StartTls startTls, string? sni,
        X509Certificate2? clientCertificate, IReadOnlyList<string> alpn, TransportOptions transport, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        Stream? network = null;
        string? remote = null;
        try
        {
            network = await ConnectionDialer.DialAsync(transport with { Proxy = ProxyMode.None }, new DnsEndPoint(host, port), limit.Token);
            if (network is NetworkStream ns) remote = ns.Socket.RemoteEndPoint?.ToString();
            if (startTls != StartTls.None) await UpgradeAsync(network, startTls, host, limit.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            if (network is not null) await network.DisposeAsync();
            return new TlsProbeResult
            {
                Host = host, Port = port, Elapsed = clock.Elapsed,
                Error = ex is OperationCanceledException && !ct.IsCancellationRequested ? $"no answer within {timeout.TotalSeconds:0} s" : ex.Message
            };
        }

        bool requested = false;
        var issuers = new List<string>();
        var chain = new List<TlsCertificateInfo>();
        string? trustProblem = null;
        bool trusted = false;

        X509Certificate SelectCertificate(object sender, string targetHost, X509CertificateCollection local, X509Certificate? remoteCert, string[] acceptable)
        {
            // Called once the server's CertificateRequest has arrived (and, on some stacks, before
            // it with an empty list): only a call carrying issuers proves the request.
            if (acceptable.Length > 0) { requested = true; issuers.Clear(); issuers.AddRange(acceptable); }
            else if (remoteCert is not null) requested = true;
            return clientCertificate!;
        }

        bool Validate(object sender, X509Certificate? cert, X509Chain? x509Chain, SslPolicyErrors errors)
        {
            if (x509Chain is not null)
                foreach (var element in x509Chain.ChainElements) chain.Add(Describe(element.Certificate));
            else if (cert is not null) chain.Add(Describe(new X509Certificate2(cert)));
            var (judged, _) = ExtraRoots.Reconsider(errors, RevocationCheck.ChainFlagsOf(x509Chain), cert, x509Chain, transport.ExtraRoots);
            trusted = judged == SslPolicyErrors.None;
            if (!trusted) trustProblem = judged.ToString();
            return true;   // a probe looks at a bad certificate rather than stopping at it
        }

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = sni ?? host,
            RemoteCertificateValidationCallback = Validate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ApplicationProtocols = alpn.Count == 0 ? null : alpn.Select(a => new SslApplicationProtocol(a)).ToList()
        };
        if (clientCertificate is not null) options.LocalCertificateSelectionCallback = SelectCertificate;
        else options.LocalCertificateSelectionCallback = (s, t, l, r, acceptable) =>
        {
            if (acceptable.Length > 0) { requested = true; issuers.Clear(); issuers.AddRange(acceptable); }
            return null!;
        };
        TlsPolicy.Apply(options, transport with { Sni = null });

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(options, limit.Token);
            // TLS 1.3 sends the CertificateRequest after the server's Finished: a read nudges it
            // through, so the request (and whether ours was taken) is seen.
            if (ssl.SslProtocol == SslProtocols.Tls13 && startTls == StartTls.None)
            {
                using var nudge = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                nudge.CancelAfter(TimeSpan.FromMilliseconds(400));
                try { _ = await ssl.ReadAsync(new byte[1], nudge.Token); } catch { /* nothing to read is fine */ }
            }
            return new TlsProbeResult
            {
                Host = host, Port = port, RemoteAddress = remote, Connected = true, Elapsed = clock.Elapsed,
                Protocol = Name(ssl.SslProtocol),
                Cipher = ssl.NegotiatedCipherSuite.ToString(),
                Alpn = ssl.NegotiatedApplicationProtocol.Protocol.Length == 0 ? null : ssl.NegotiatedApplicationProtocol.ToString(),
                // A certificate is only ever sent when one was asked for, whatever the callback saw.
                ClientCertificateRequested = requested || ssl.LocalCertificate is not null,
                AcceptableIssuers = issuers.Distinct().ToList(),
                ClientCertificateSent = ssl.LocalCertificate is { } sent ? sent.Subject : null,
                ServerTrusted = trusted, TrustProblem = trustProblem, Chain = chain
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TlsProbeResult
            {
                Host = host, Port = port, RemoteAddress = remote, Connected = true, Elapsed = clock.Elapsed,
                Error = "TLS handshake failed: " + (ex.InnerException?.Message is { } inner ? $"{ex.Message} ({inner})" : ex.Message),
                ClientCertificateRequested = requested, AcceptableIssuers = issuers.Distinct().ToList(),
                ServerTrusted = trusted, TrustProblem = trustProblem, Chain = chain
            };
        }
        finally { await ssl.DisposeAsync(); }
    }

    private static string Name(SslProtocols p) => p switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable SYSLIB0039
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
#pragma warning restore SYSLIB0039
        _ => p.ToString()
    };

    internal static TlsCertificateInfo Describe(X509Certificate2 c)
    {
        var sans = new List<string>();
        foreach (var ext in c.Extensions)
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                sans.AddRange(san.EnumerateDnsNames().Select(d => "DNS:" + d));
                sans.AddRange(san.EnumerateIPAddresses().Select(i => "IP:" + i));
            }
        string algorithm = c.PublicKey.Oid.FriendlyName ?? c.PublicKey.Oid.Value ?? "?";
        int size = c.GetRSAPublicKey()?.KeySize ?? c.GetECDsaPublicKey()?.KeySize ?? 0;
        return new TlsCertificateInfo(c.Subject, c.Issuer, c.NotBefore, c.NotAfter, c.Thumbprint,
            TrustService.SpkiSha256(c), algorithm, size, sans);
    }

    /// <summary>The plain-text dialogue that turns a connection into a TLS one.</summary>
    internal static async Task UpgradeAsync(Stream s, StartTls protocol, string host, CancellationToken ct)
    {
        switch (protocol)
        {
            case StartTls.Smtp:
                await Expect(s, "220", ct);
                await Send(s, "EHLO certapi\r\n", ct);
                await Expect(s, "250", ct, multiline: true);
                await Send(s, "STARTTLS\r\n", ct);
                await Expect(s, "220", ct);
                break;
            case StartTls.Imap:
                await Expect(s, "* OK", ct);
                await Send(s, "a1 STARTTLS\r\n", ct);
                await Expect(s, "a1 OK", ct, skipUntagged: true);
                break;
            case StartTls.Pop3:
                await Expect(s, "+OK", ct);
                await Send(s, "STLS\r\n", ct);
                await Expect(s, "+OK", ct);
                break;
            case StartTls.Ftp:
                await Expect(s, "220", ct, multiline: true);
                await Send(s, "AUTH TLS\r\n", ct);
                await Expect(s, "234", ct);
                break;
            case StartTls.Ldap:
            {
                // ExtendedRequest, messageID 1, requestName 1.3.6.1.4.1.1466.20037 (RFC 4511 §4.14).
                byte[] oid = Encoding.ASCII.GetBytes("1.3.6.1.4.1.1466.20037");
                byte[] request = [0x30, (byte)(oid.Length + 7), 0x02, 0x01, 0x01, 0x77, (byte)(oid.Length + 2), 0x80, (byte)oid.Length, .. oid];
                await s.WriteAsync(request, ct);
                var answer = new byte[512];
                int n = await s.ReadAsync(answer, ct);
                // ExtendedResponse: ... 0x78 len 0x0a 0x01 <resultCode>; 0 is success.
                int at = Array.IndexOf(answer, (byte)0x78, 0, n);
                if (at < 0 || at + 4 >= n || answer[at + 2] != 0x0a || answer[at + 4] != 0x00)
                    throw new IOException("The LDAP server refused StartTLS.");
                break;
            }
            case StartTls.Postgres:
            {
                await s.WriteAsync(new byte[] { 0, 0, 0, 8, 0x04, 0xd2, 0x16, 0x2f }, ct);
                var one = new byte[1];
                if (await s.ReadAsync(one, ct) != 1 || one[0] != (byte)'S')
                    throw new IOException("The PostgreSQL server does not accept TLS on this port.");
                break;
            }
        }
    }

    private static Task Send(Stream s, string line, CancellationToken ct) => s.WriteAsync(Encoding.ASCII.GetBytes(line), ct).AsTask();

    private static async Task Expect(Stream s, string prefix, CancellationToken ct, bool multiline = false, bool skipUntagged = false)
    {
        while (true)
        {
            string line = await ReadLine(s, ct);
            if (multiline && line.Length > 3 && line[3] == '-') continue;          // "250-..." continues
            if (skipUntagged && line.StartsWith('*')) continue;
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException($"Expected '{prefix}' from the server, got: {line}");
        }
    }

    private static async Task<string> ReadLine(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("The server closed the connection.");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
            if (sb.Length > 8192) throw new IOException("The server's line is too long.");
        }
    }
}
