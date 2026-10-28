using System.IO;
using System.Net;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

/// <summary>Follow-ups to the second sweep: what its own fixes broke or left half done.</summary>
public class Round3Tests
{
    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + extension);

    [Fact]
    public void A_data_file_with_a_byte_order_mark_loads_as_it_did_before()
    {
        string json = TempFile(".json"), csv = TempFile(".csv");
        try
        {
            // PowerShell 5.1: Out-File -Encoding utf8 writes a UTF-8 BOM, and > writes UTF-16.
            File.WriteAllText(json, "[{\"name\":\"Müller\"}]", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(csv, "name\nMüller\n", Encoding.Unicode);
            Assert.Equal("Müller", DataSet.Load(json)[0]["name"]);
            Assert.Equal("Müller", DataSet.Load(csv)[0]["name"]);
        }
        finally { File.Delete(json); File.Delete(csv); }
    }

    [Fact]
    public void A_data_file_in_a_legacy_code_page_is_still_refused_with_a_message()
    {
        string csv = TempFile(".csv");
        try
        {
            File.WriteAllBytes(csv, [.. "name\nM"u8.ToArray(), 0xFC, .. "ller\n"u8.ToArray()]);  // Windows-1252 ü
            var ex = Assert.Throws<InvalidDataException>(() => DataSet.Load(csv));
            Assert.Contains("UTF-8", ex.Message);
        }
        finally { File.Delete(csv); }
    }

    [Fact]
    public void A_quoted_csv_field_may_follow_a_space()
    {
        var rows = DataSet.ParseCsv("a, b\n1, \"x,y\"\n");
        Assert.Equal("x,y", rows[0]["b"]);
    }

    [Fact]
    public void The_same_unreadable_workspace_is_kept_once_however_often_it_is_loaded()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "state.json");
            File.WriteAllText(state, "{ not json");
            string? first = AppState.PreserveUnreadable(state);
            string? second = AppState.PreserveUnreadable(state);
            Assert.NotNull(first);
            Assert.Equal(first, second);
            Assert.Single(Directory.GetFiles(dir, "state.json.unreadable-*.bak"));

            // A different broken file is a different copy.
            File.WriteAllText(state, "{ still not json");
            Thread.Sleep(1100);  // the copy's name has one-second resolution
            Assert.NotEqual(first, AppState.PreserveUnreadable(state));
            Assert.Equal(2, Directory.GetFiles(dir, "state.json.unreadable-*.bak").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("https://h/a", "https://h/b", true)]
    [InlineData("http://h/a", "https://h/a", true)]          // upgrade to https on 443
    [InlineData("http://h:8080/a", "https://h:8080/a", true)] // upgrade on the same port
    [InlineData("https://h/a", "http://h/a", false)]         // never a downgrade
    [InlineData("https://h/a", "https://other/a", false)]
    [InlineData("https://h/a", "https://h:8443/a", false)]
    [InlineData("http://h/a", "https://h:8443/a", false)]
    public void Sse_follows_same_origin_redirects_and_https_upgrades_only(string from, string to, bool followed) =>
        Assert.Equal(followed, SseClient.SameOriginOrUpgrade(new Uri(from), new Uri(to)));

    [Fact]
    public void The_gateway_windows_credentials_never_answer_basic_or_digest()
    {
        var inner = new NetworkCredential("alice", "P@ss", "CORP");
        var creds = new IntegratedOnlyCredentials(inner);
        var uri = new Uri("https://upstream.example/");
        Assert.Null(creds.GetCredential(uri, "Basic"));
        Assert.Null(creds.GetCredential(uri, "Digest"));
        Assert.Same(inner, creds.GetCredential(uri, "Negotiate"));
        Assert.Same(inner, creds.GetCredential(uri, "NTLM"));
    }

    [Fact]
    public void Start_up_cleanup_leaves_a_fresh_download_alone_and_removes_a_stale_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, ReleaseUpdater.CliAsset);
            File.WriteAllText(exe, "running");
            File.WriteAllText(exe + ".new", "being installed by the other program");
            File.WriteAllText(exe + ".old", "left behind");
            ReleaseUpdater.CleanUpPrevious(exe);
            Assert.True(File.Exists(exe + ".new"));
            Assert.False(File.Exists(exe + ".old"));

            File.SetLastWriteTimeUtc(exe + ".new", DateTime.UtcNow - ReleaseUpdater.StaleDownload - TimeSpan.FromMinutes(1));
            ReleaseUpdater.CleanUpPrevious(exe);
            Assert.False(File.Exists(exe + ".new"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Bench_with_a_body_posts_it_unless_a_method_is_given()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "ok"));
        var services = new CliServices { LiveStatePath = TempFile(".json") };

        int code = CliApp.Run(["bench", server.BaseUrl + "/b", "-d", "hello", "-n", "1", "-c", "1"],
            new StringWriter(), new StringWriter(), new MemoryStream(), services);
        Assert.Equal(0, code);
        Assert.All(server.Requests, r => Assert.StartsWith("POST ", r.RequestLine));

        server.Requests.Clear();
        code = CliApp.Run(["bench", server.BaseUrl + "/b", "-X", "PUT", "-d", "hello", "-n", "1", "-c", "1"],
            new StringWriter(), new StringWriter(), new MemoryStream(), services);
        Assert.Equal(0, code);
        Assert.All(server.Requests, r => Assert.StartsWith("PUT ", r.RequestLine));
        Assert.NotEmpty(server.Requests);
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("update")]
    [InlineData("connections")]
    public void Embedded_transport_help_is_set_off_by_blank_lines(string command)
    {
        var so = new StringWriter();
        CliApp.Run([command, "--help"], so, new StringWriter(), new MemoryStream(), new CliServices { LiveStatePath = TempFile(".json") });
        string help = so.ToString().Replace("\r\n", "\n");
        Assert.Contains("\n\n" + TransportFlags.Help.Replace("\r\n", "\n") + "\n\n", help);
        Assert.DoesNotContain("Transport (", help);
    }

    [Fact]
    public async Task Doctor_does_not_say_a_server_that_requires_a_certificate_did_not_ask_for_one()
    {
        // The mock takes any client certificate, so it asks for one naming no authority at all.
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Doctor CA");
        // Both through a .pfx, as the mock and --cert-file load them: a freshly generated key
        // cannot take part in a TLS handshake on Linux.
        static System.Security.Cryptography.X509Certificates.X509Certificate2 ViaPfx(System.Security.Cryptography.X509Certificates.X509Certificate2 c)
        {
            using (c) return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                c.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), (string?)null);
        }
        using var serverCert = ViaPfx(SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" }));
        using var clientCert = ViaPfx(SelfSignedCertificateFactory.CreateSignedCertificate("Doctor Client", ca, false, true));
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert);

        var report = await ConnectionDoctor.RunAsync(mock.BaseUrl, clientCert, new[] { clientCert },
            new TransportOptions { IgnoreServerCertificateErrors = true });

        var tls = Assert.Single(report.Stages, s => s.Name == "tls");
        Assert.True(tls.Ok, tls.Summary);
        Assert.DoesNotContain(tls.Detail, d => d.Contains("did not ask for one", StringComparison.Ordinal));
        Assert.Contains(tls.Detail, d => d.Contains("asked for one without naming", StringComparison.Ordinal) ||
                                         d.Contains("accepts client certificates from", StringComparison.Ordinal));
    }
}
