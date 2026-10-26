using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>`serve --routes <file>`: mounts from JSON, each presenting its own client certificate.</summary>
public class ServeRoutesFileTests
{
    [Fact]
    public void The_file_is_an_array_or_an_object_with_routes_and_each_mount_names_one_certificate_source()
    {
        var specs = GatewayRoutesFile.Parse("""
            { "routes": [
                { "prefix": "/api", "upstream": "https://api.corp/ignored/path", "cert": "CN=svc" },
                { "prefix": "/hr",  "upstream": "https://hr.corp:8443", "certFile": "hr.pem", "certPassword": "pw" },
                { "prefix": "/pub", "upstream": "http://pub.corp" }
            ] }
            """);
        Assert.Equal(3, specs.Count);
        Assert.Equal("https://api.corp/", specs[0].Upstream.ToString());
        Assert.Equal("CN=svc", specs[0].Cert);
        Assert.Equal("hr.pem", specs[1].CertFile);
        Assert.False(specs[2].HasCertificate);

        Assert.Single(GatewayRoutesFile.Parse("""[{ "path": "/", "url": "https://x.corp" }]"""));

        Assert.Contains("choose one", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "prefix": "/a", "upstream": "https://x", "cert": "a", "certFile": "b" }]""")).Message);
        Assert.Contains("only apply with", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "prefix": "/a", "upstream": "https://x", "keyFile": "k" }]""")).Message);
        Assert.Contains("unknown field", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "prefix": "/a", "upstream": "https://x", "insecureish": true }]""")).Message);
        Assert.Contains("credentials", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "prefix": "/a", "upstream": "https://u:p@x" }]""")).Message);
        Assert.Contains("needs a \"name\"", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "upstream": "https://x" }]""")).Message);
        Assert.Contains("not valid JSON", Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("[")).Message);
    }

    [Fact]
    public void A_missing_file_or_certificate_is_a_data_error_naming_the_mount()
    {
        var err = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "--port", "1", "--routes", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json") }, TextWriter.Null, err);
        Assert.Equal(ExitCodes.Data, code);
        Assert.Contains("does not exist", err.ToString());

        string file = Path.GetTempFileName();
        File.WriteAllText(file, """[{ "prefix": "/hr", "upstream": "https://hr.corp", "certFile": "missing.pem" }]""");
        try
        {
            var err2 = new StringWriter();
            int code2 = CliApp.Run(new[] { "serve", "--port", "1", "--routes", file }, TextWriter.Null, err2);
            Assert.Equal(ExitCodes.Data, code2);
            Assert.Contains("/hr", err2.ToString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Each_mount_presents_its_own_certificate_and_the_rest_use_the_default()
    {
        var (ca, server, client) = Certs();
        using var hrClient = SelfSignedCertificateFactory.CreateSignedCertificate("HrClient", ca, false, true);
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartKeepAliveAsync(server);
            await using var hr = await LoopbackMtlsServer.StartKeepAliveAsync(server);

            string dir = Path.Combine(Path.GetTempPath(), "certapi-routes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // The HR identity comes from a PEM next to the routes file — a relative certFile.
            File.WriteAllText(Path.Combine(dir, "hr.pem"),
                hrClient.ExportCertificatePem() + "\n" + hrClient.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
            string routes = Path.Combine(dir, "routes.json");
            File.WriteAllText(routes, $$"""
                [
                  { "prefix": "/api", "upstream": "{{api.BaseUrl}}" },
                  { "prefix": "/hr",  "upstream": "{{hr.BaseUrl}}", "certFile": "hr.pem" }
                ]
                """);
            try
            {
                await using var serve = ServeHost.Start(client, "--routes", routes);
                using var http = NewClient();

                var apiResp = await Poll(() => http.GetAsync($"{serve.Origin}/api/ping"));
                Assert.Equal(HttpStatusCode.OK, apiResp.StatusCode);
                var hrResp = await http.GetAsync($"{serve.Origin}/hr/ping");
                Assert.Equal(HttpStatusCode.OK, hrResp.StatusCode);

                Assert.Equal(new[] { client.Thumbprint }, api.ClientCertificateThumbprintsByConnection.Distinct());
                Assert.Equal(new[] { hrClient.Thumbprint }, hr.ClientCertificateThumbprintsByConnection.Distinct());

                // The routes endpoint says which identity each mount carries.
                var listed = await http.GetStringAsync($"{serve.Origin}/_certapi/routes");
                Assert.Contains("CN=HrClient", listed);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void The_gateway_pools_per_authority_and_certificate_and_picks_the_route_certificate_for_websockets()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var a = SelfSignedCertificateFactory.CreateSignedCertificate("A", ca, false, true);
        using var b = SelfSignedCertificateFactory.CreateSignedCertificate("B", ca, false, true);
        var routes = new GatewayRoutes(new[]
        {
            new GatewayRoute("/a", new Uri("https://one.corp")) { Certificate = a },
            new GatewayRoute("/b", new Uri("https://two.corp")),
        });
        using var gateway = new MtlsGateway(routes, b, true, TimeSpan.FromSeconds(5));
        Assert.Same(a, gateway.CertificateFor(new Uri("https://one.corp/ws")));
        Assert.Same(b, gateway.CertificateFor(new Uri("https://two.corp/ws")));
        Assert.Same(b, gateway.CertificateFor(new Uri("https://elsewhere.corp/")));
    }
}
