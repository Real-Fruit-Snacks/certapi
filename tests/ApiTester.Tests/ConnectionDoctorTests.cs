using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Covers the doctor's pure decisions as data (issuer matching, interception heuristics)
/// and its staged run against loopback servers, including the failure paths that matter most:
/// a host that does not resolve, a port nothing listens on, and a server whose client-certificate
/// authority list does not include the certificate the user chose.</summary>
public class ConnectionDoctorTests
{
    private static readonly IReadOnlyList<X509Certificate2> NoCerts = Array.Empty<X509Certificate2>();

    private static DoctorStage Stage(DoctorReport r, string name) =>
        r.Stages.Single(s => s.Name == name);

    // ---------------------------------------------------------------- issuer matching (pure)

    [Fact]
    public void MatchIssuers_matches_on_issuer_ignoring_spacing_and_case()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // The server's spelling of the same authority, with different spacing and casing.
        string spelled = leaf.Issuer.Replace(", ", ",").ToUpperInvariant();

        var matched = DoctorReport.MatchIssuers(new[] { leaf }, new[] { spelled });

        Assert.Same(leaf, Assert.Single(matched));
    }

    [Fact]
    public void MatchIssuers_returns_nothing_when_no_certificate_shares_an_issuer()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        Assert.Empty(DoctorReport.MatchIssuers(new[] { leaf }, new[] { "CN=Some Other CA, O=Elsewhere" }));
        // An empty request means the server asked for nothing — never "everything matches".
        Assert.Empty(DoctorReport.MatchIssuers(new[] { leaf }, Array.Empty<string>()));
    }

    // ---------------------------------------------------------------- interception notes (pure)

    [Theory]
    [InlineData("CN=Zscaler Root CA, O=Zscaler Inc.")]
    [InlineData("CN=Netskope Certificate Authority")]
    [InlineData("CN=Fortinet CA, O=Fortinet")]
    public void InterceptionNote_names_a_known_inspection_vendor(string rootSubject)
    {
        string? note = DoctorReport.InterceptionNote(rootSubject, rootIsLocallyTrusted: false);

        Assert.NotNull(note);
        Assert.Contains("decrypted and re-signed", note);
    }

    [Fact]
    public void InterceptionNote_flags_a_locally_trusted_private_root_more_cautiously()
    {
        string? note = DoctorReport.InterceptionNote("CN=Acme Internal Root", rootIsLocallyTrusted: true);

        Assert.NotNull(note);
        Assert.Contains("consistent with SSL inspection", note);
    }

    [Fact]
    public void InterceptionNote_is_silent_for_an_ordinary_public_root()
    {
        Assert.Null(DoctorReport.InterceptionNote("CN=DigiCert Global Root G2, O=DigiCert Inc", false));
    }

    // ---------------------------------------------------------------- staged runs

    [Fact]
    public async Task A_url_that_is_not_absolute_http_fails_at_the_url_stage()
    {
        var report = await ConnectionDoctor.RunAsync("not-a-url", null, NoCerts, new TransportOptions());

        Assert.False(report.Ok);
        Assert.Equal("url", report.FirstFailure!.Name);
        Assert.Single(report.Stages);   // nothing else was attempted
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_fails_at_dns_and_advises_from_the_probe()
    {
        // The probe stands in for the internet-reachability check, so no test touches the network.
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("Microsoft Connect Test"));

        Assert.False(report.Ok);
        Assert.Equal("dns", report.FirstFailure!.Name);
        Assert.Contains("specific to this host", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task A_captive_portal_answer_is_reported_as_such()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("<html>Please sign in to continue</html>"));

        Assert.Contains("captive portal", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task No_internet_at_all_is_reported_when_the_probe_itself_fails()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>(null));

        Assert.Contains("no working internet connection", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task A_closed_port_resolves_but_fails_at_tcp()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://127.0.0.1:1/", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("Microsoft Connect Test"));

        Assert.True(Stage(report, "dns").Ok);
        Assert.Equal("tcp", report.FirstFailure!.Name);
    }

    [Fact]
    public async Task A_healthy_mtls_endpoint_passes_every_stage_and_reports_the_handshake()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var report = await ConnectionDoctor.RunAsync(
            server.BaseUrl, clientCert, new[] { clientCert }, new TransportOptions());

        Assert.True(report.Ok, report.FirstFailure?.Summary);
        var tls = Stage(report, "tls");
        Assert.Contains(tls.Detail, d => d.StartsWith("protocol: ", StringComparison.Ordinal));
        Assert.Contains(tls.Detail, d => d.StartsWith("cipher: ", StringComparison.Ordinal));
        Assert.Contains(tls.Detail, d => d.Contains("client certificate presented", StringComparison.Ordinal));
        Assert.Contains(report.Stages, s => s.Name == "http");
    }

    [Fact]
    public async Task The_acceptable_issuer_list_is_reported_and_matched_against_the_store()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var report = await ConnectionDoctor.RunAsync(
            server.BaseUrl, clientCert, new[] { clientCert }, new TransportOptions());

        var tls = Stage(report, "tls");
        // Whether the loopback server sends an issuer list is the platform's business; what must
        // hold is that the report SAYS which of the two situations it found, rather than staying
        // silent about the single most useful fact in an mTLS diagnosis — and that a server which
        // requires a certificate is never reported as not asking for one.
        Assert.Contains(tls.Detail, d =>
            d.Contains("asked for one without naming", StringComparison.Ordinal) ||
            d.Contains("accepts client certificates from", StringComparison.Ordinal));
        Assert.DoesNotContain(tls.Detail, d => d.Contains("did not ask for one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plain_http_endpoint_skips_tls_and_still_reports_http()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);

        var report = await ConnectionDoctor.RunAsync(
            $"http://127.0.0.1:{mock.Port}/api/x", null, NoCerts, new TransportOptions());

        Assert.True(report.Ok, report.FirstFailure?.Summary);
        Assert.DoesNotContain(report.Stages, s => s.Name == "tls");
        Assert.Contains("200", Stage(report, "http").Summary);
    }

    [Fact]
    public async Task The_proxy_stage_reports_the_decision_including_a_bypass()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        ProxyBypass.TryParse("127.0.0.1", out var rules, out _);

        var report = await ConnectionDoctor.RunAsync(
            $"http://127.0.0.1:{mock.Port}/api/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.invalid:9", NoProxy = rules });

        // The bypass wins over the explicit proxy, and the report says which rule did it.
        Assert.Contains("bypassed by '127.0.0.1'", Stage(report, "proxy").Summary);
        Assert.True(report.Ok, report.FirstFailure?.Summary);
    }

    [Fact]
    public async Task An_unreachable_proxy_blames_the_proxy_not_the_host()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://api.example.com/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://127.0.0.1:1" });

        Assert.Equal("tcp", report.FirstFailure!.Name);
        Assert.Contains("PROXY", Stage(report, "tcp").Advice);
    }

    [Fact]
    public async Task Through_a_proxy_the_target_host_is_never_resolved_locally()
    {
        // The corporate case this ordering exists for: an internal name only the proxy can see.
        // Resolving it here would fail a connection that would actually have worked, so DNS must
        // report on the PROXY's hostname instead — and the run must get as far as TCP.
        var report = await ConnectionDoctor.RunAsync(
            "https://intranet-only.invalid/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://127.0.0.1:1" });

        var dns = Stage(report, "dns");
        Assert.True(dns.Ok);
        Assert.Contains("resolved by it, not here", dns.Summary);
        Assert.Equal("tcp", report.FirstFailure!.Name);
    }
}

public class PerPathCertificateDiagnosisTests
{
    [Fact]
    public async Task Doctor_names_the_per_path_certificate_shape_when_the_handshake_never_asked_and_the_server_refused()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartNoClientCertStatusAsync(serverCert, 403);

        var report = await ConnectionDoctor.RunAsync(
            server.BaseUrl, clientCert, new[] { clientCert }, new TransportOptions { IgnoreServerCertificateErrors = true, Proxy = ProxyMode.None });

        var http = report.Stages.Single(s => s.Name == "http");
        Assert.Contains("403", http.Summary);
        Assert.Equal(DoctorReport.PerPathCertificateAdvice, http.Advice);
        Assert.Contains("--http1.1", http.Advice!);

        // Without a certificate chosen there is nothing to renegotiate, so the ordinary advice stands.
        var plain = await ConnectionDoctor.RunAsync(
            server.BaseUrl, null, Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
            new TransportOptions { IgnoreServerCertificateErrors = true, Proxy = ProxyMode.None });
        Assert.NotEqual(DoctorReport.PerPathCertificateAdvice, plain.Stages.Single(s => s.Name == "http").Advice);
    }

    [Fact]
    public async Task Send_prints_the_same_note_on_a_403_with_a_certificate_the_handshake_never_sent()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartNoClientCertStatusAsync(serverCert, 403);

        var services = new ApiTester.Cli.CliServices
        {
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=Client", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };
        var stderr = new StringWriter();
        ApiTester.Cli.CliApp.Run(new[] { "send", server.BaseUrl, "--cert", "Client", "--insecure", "--no-proxy" }, TextWriter.Null, stderr, services: services);
        Assert.Contains("did not ask for a client certificate during the handshake", stderr.ToString());
    }
}

public class ClockSkewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Within_tolerance_there_is_no_note()
    {
        Assert.Null(DoctorReport.ClockSkewNote(Now, Now.AddMinutes(4), chainNotTimeValid: false));
        Assert.Null(DoctorReport.ClockSkewNote(Now, Now.AddMinutes(-5), chainNotTimeValid: true));
    }

    [Fact]
    public void A_skewed_clock_is_named_with_its_direction_and_blamed_for_a_time_invalid_chain()
    {
        string? behind = DoctorReport.ClockSkewNote(Now, Now.AddHours(3), chainNotTimeValid: true);
        Assert.NotNull(behind);
        Assert.Contains("3 hours behind", behind);
        Assert.Contains("not the certificate", behind);
        Assert.Contains("w32tm", behind);

        string? ahead = DoctorReport.ClockSkewNote(Now.AddDays(2), Now, chainNotTimeValid: false);
        Assert.Contains("2 days ahead", ahead);
        Assert.DoesNotContain("not the certificate", ahead);
    }

    [Fact]
    public async Task Doctor_reads_the_servers_date_header_and_reports_skew()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        // A server whose clock is a day behind.
        var headers = new[] { new KeyValuePair<string, string>("Date", DateTime.UtcNow.AddDays(-1).ToString("R")) };
        await using var server = await LoopbackMtlsServer.StartWithHeadersAsync(serverCert, clientCert.Thumbprint!, headers);

        var report = await ConnectionDoctor.RunAsync(server.BaseUrl, clientCert, new[] { clientCert },
            new TransportOptions { IgnoreServerCertificateErrors = true, Proxy = ProxyMode.None });
        var http = report.Stages.Single(s => s.Name == "http");
        Assert.Contains(http.Detail, d => d.Contains("ahead of the server's"));
    }
}

public class DoctorNegotiateProxyTests
{
    /// <summary>A proxy that demands Negotiate and accepts any Negotiate token it is shown.</summary>
    private static (System.Net.Sockets.TcpListener listener, Task serving, List<string> seen) FakeNegotiateProxy()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var seen = new List<string>();
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            for (int round = 0; round < 3; round++)
            {
                var head = new System.Text.StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, n));
                }
                string request = head.ToString();
                lock (seen) seen.Add(request.Split("\r\n")[0] + (request.Contains("Proxy-Authorization: Negotiate") ? " +token" : ""));
                string reply = request.Contains("Proxy-Authorization: Negotiate ")
                    ? "HTTP/1.1 200 Connection established\r\n\r\n"
                    : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Negotiate\r\nProxy-Authenticate: Basic realm=\"corp\"\r\nContent-Length: 0\r\nProxy-Connection: Keep-Alive\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
                if (reply.StartsWith("HTTP/1.1 200")) return;
            }
        });
        return (listener, serving, seen);
    }

    [Fact]
    public async Task Doctor_answers_a_negotiate_challenge_with_the_signed_in_account_or_says_why_it_cannot()
    {
        var (listener, serving, seen) = FakeNegotiateProxy();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var report = await ConnectionDoctor.RunAsync("https://target.example:443/", null,
                Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
                new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = $"http://127.0.0.1:{port}" },
                probe: _ => Task.FromResult<string?>(null));
            var connect = report.Stages.Single(s => s.Name == "connect");

            // Either the token was produced and the fake proxy accepted it (a machine with a
            // usable credential), or the security package refused and the advice names THAT
            // rather than blaming the proxy or asking for --proxy-user as the only way out.
            if (connect.Ok)
            {
                Assert.Contains("Negotiate", connect.Summary);
                lock (seen) Assert.Contains(seen, s => s.EndsWith("+token"));
            }
            else
            {
                Assert.Contains("credential", connect.Advice ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("supply --proxy-user, or use a proxy", connect.Advice ?? "");
            }
            Assert.Contains(connect.Detail, d => d.Contains("407"));
        }
        finally { listener.Stop(); try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }

    [Fact]
    public async Task Explicit_proxy_credentials_still_take_the_basic_path()
    {
        var (listener, serving, seen) = FakeNegotiateProxy();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var report = await ConnectionDoctor.RunAsync("https://target.example:443/", null,
                Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
                new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = $"http://127.0.0.1:{port}", ProxyUser = "u", ProxyPassword = "p" },
                probe: _ => Task.FromResult<string?>(null));
            var connect = report.Stages.Single(s => s.Name == "connect");
            Assert.False(connect.Ok);
            lock (seen) Assert.DoesNotContain(seen, s => s.EndsWith("+token"));
        }
        finally { listener.Stop(); try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }
}
