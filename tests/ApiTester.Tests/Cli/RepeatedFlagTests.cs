using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Cli;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>Which flags repeat. A list-valued flag given twice adds to its list; a single-value
/// flag given twice is a usage error, because "which one did it use?" has no good answer.</summary>
public class RepeatedFlagTests
{
    [Fact]
    public void A_list_flag_given_several_times_is_one_list_in_order()
    {
        var args = new Args(new[] { "--noproxy", "a.corp,b.corp", "--noproxy", "c.corp", "--noproxy=d.corp" });
        Assert.Equal("a.corp,b.corp,c.corp,d.corp", args.ListValue("--noproxy"));
        Assert.Null(new Args(Array.Empty<string>()).ListValue("--noproxy"));
    }

    [Theory]
    [InlineData("--cert")]
    [InlineData("--timeout")]
    [InlineData("--proxy")]
    [InlineData("--cert-file")]
    public void A_single_value_flag_given_twice_is_still_refused(string flag)
    {
        var args = new Args(new[] { flag, "one", flag, "two" });
        args.Value(flag);
        Assert.Contains("more than once", Assert.Throws<CliUsageException>(() => args.Positionals()).Message);
    }

    [Fact]
    public void Noproxy_and_retry_on_accumulate_across_occurrences()
    {
        var t = TransportFlags.Parse(new Args(new[]
        {
            "--noproxy", "internal.corp", "--noproxy", ".lab,10.0.0.0/8",
            "--retry", "2", "--retry-on", "500", "--retry-on", "429,503",
        }), out _, environment: _ => null).ApplyTo(new TransportOptions());
        Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://api.internal.corp/")));
        Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://x.lab/")));
        Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://10.1.2.3/")));
        Assert.False(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://public.example/")));

        var overrides = TransportFlags.Parse(new Args(new[] { "--retry", "2", "--retry-on", "500", "--retry-on", "429,503" }), out _, environment: _ => null);
        Assert.Equal(new[] { 429, 500, 503 }, overrides.RetryOn!.OrderBy(x => x));
    }

    [Fact]
    public async Task Serve_adds_up_repeated_allow_cors_and_routes()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var one = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var two = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            string dir = Path.Combine(Path.GetTempPath(), "certapi-repeat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string a = Path.Combine(dir, "a.json"), b = Path.Combine(dir, "b.json");
            File.WriteAllText(a, $$"""[{ "prefix": "/one", "upstream": "{{one.BaseUrl}}" }]""");
            File.WriteAllText(b, $$"""[{ "prefix": "/two", "upstream": "{{two.BaseUrl}}" }]""");
            try
            {
                await using var serve = ServeHost.Start(client,
                    "--routes", a, "--routes", b,
                    "--allow", "alpha.corp", "--allow", "*.beta.corp,gamma.corp",
                    "--cors", "https://app.one", "--cors", "https://app.two");
                using var http = NewClient();

                Assert.Equal(HttpStatusCode.OK, (await Poll(() => http.GetAsync($"{serve.Origin}/one/x"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{serve.Origin}/two/x")).StatusCode);

                string routes = await http.GetStringAsync($"{serve.Origin}/_certapi/routes");
                foreach (var host in new[] { "alpha.corp", "beta.corp", "gamma.corp" }) Assert.Contains(host, routes);

                async Task<string[]> PreflightFrom(string origin)
                {
                    var req = new HttpRequestMessage(HttpMethod.Options, $"{serve.Origin}/one/x");
                    req.Headers.Add("Origin", origin);
                    req.Headers.Add("Access-Control-Request-Method", "POST");
                    return HeaderValues(await http.SendAsync(req), "Access-Control-Allow-Origin");
                }
                Assert.Equal(new[] { "https://app.one" }, await PreflightFrom("https://app.one"));
                Assert.Equal(new[] { "https://app.two" }, await PreflightFrom("https://app.two"));
                Assert.Empty(await PreflightFrom("https://other.example"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
