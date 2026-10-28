using System.IO;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests.Cli;

/// <summary><c>certapi connections</c> — the command that answers "am I actually reusing
/// connections?" by making the requests and reporting which one each went out on.</summary>
public class ConnectionsCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-conn-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // Discovery sealed off from the machine, as elsewhere: a developer's own configuration file
    // must never be able to change an outcome here.
    private CliServices Services() => new()
    {
        WorkingDirectory = _dir,
        UserConfigPath = null,
        // A CERTAPI_CONFIG set on the machine running the suite must not reach these tests.
        Environment = name => name == ConfigLoader.EnvironmentVariable ? null : System.Environment.GetEnvironmentVariable(name),
        LiveStatePath = Path.Combine(_dir, "state.json"),
        FileExists = path => path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr, services: Services());
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task<WebApplication> StartKeepAliveAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/k", () => "ok");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task It_reports_reuse_across_several_requests()
    {
        await using var app = await StartKeepAliveAsync();

        var (code, output, _) = Run("connections", app.Urls.First() + "/k", "-n", "4");

        Assert.Equal(0, code);
        Assert.Contains("4 request(s) over 1 connection(s)", output);
        Assert.Contains("Connections are being reused", output);
    }

    [Fact]
    public async Task The_json_form_carries_the_same_answer_for_a_script()
    {
        await using var app = await StartKeepAliveAsync();

        var (code, output, _) = Run("connections", app.Urls.First() + "/k", "-n", "3", "--json");

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("sent").GetInt32());
        Assert.True(root.GetProperty("reusing").GetBoolean());
        var connection = Assert.Single(root.GetProperty("connections").EnumerateArray().ToList());
        Assert.Equal(3, connection.GetProperty("requests").GetInt32());
    }

    [Fact]
    public async Task Parallel_requests_are_sent_together()
    {
        // Parallel sends need a connection each, so this must NOT be reported as broken reuse —
        // the honest reading is requests against connections, which the help text says too.
        await using var app = await StartKeepAliveAsync();

        var (code, output, _) = Run("connections", app.Urls.First() + "/k", "-n", "4", "--parallel", "4", "--json");

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(4, document.RootElement.GetProperty("sent").GetInt32());
    }

    [Fact]
    public void An_unreachable_url_fails_rather_than_reporting_an_empty_pool()
    {
        // Port 1 refuses immediately. Printing "no connections" with exit 0 would read as success.
        var (code, _, error) = Run("connections", "http://127.0.0.1:1/", "-n", "2");

        Assert.Equal(3, code);
        Assert.Contains("could not send any request", error);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    public void A_url_that_is_not_http_is_a_usage_error(string url)
    {
        var (code, _, error) = Run("connections", url);

        Assert.Equal(2, code);
        Assert.Contains(url, error);
    }

    [Fact]
    public void A_missing_url_is_a_usage_error()
    {
        Assert.Equal(2, Run("connections").Code);
    }

    [Theory]
    [InlineData("-n", "0")]
    [InlineData("-n", "abc")]
    [InlineData("--parallel", "-1")]
    public void A_count_that_is_not_a_positive_number_is_refused(string flag, string value)
    {
        var (code, _, error) = Run("connections", "http://127.0.0.1:1/", flag, value);

        Assert.Equal(2, code);
        Assert.Contains("positive whole number", error);
    }

    [Fact]
    public async Task Bench_pool_measures_the_reuse_it_has_always_claimed()
    {
        // `bench` has always printed "connections are pooled and reused" as a note. --pool turns
        // that assertion into a measurement of the run that just happened.
        await using var app = await StartKeepAliveAsync();

        var (code, output, _) = Run("bench", app.Urls.First() + "/k", "-n", "6", "-c", "1", "--pool");

        Assert.Equal(0, code);
        Assert.Contains("request(s) over", output);
        Assert.Contains("Connections are being reused", output);
    }

    [Fact]
    public async Task Bench_pool_with_json_still_emits_one_parseable_document()
    {
        // --json promises ONE machine-readable document on stdout. Appending the human-readable
        // pool report after it made the whole thing unparseable, so a script piping into jq got a
        // syntax error rather than a result. The facts now go inside the envelope.
        await using var app = await StartKeepAliveAsync();

        var (code, output, _) = Run("bench", app.Urls.First() + "/k", "-n", "4", "-c", "1",
                                    "--pool", "--json");

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output);      // the assertion that matters
        var root = document.RootElement;
        Assert.Equal(4, root.GetProperty("sent").GetInt32());

        var connections = root.GetProperty("connections").EnumerateArray().ToList();
        Assert.NotEmpty(connections);
        Assert.Equal(4, connections.Sum(c => c.GetProperty("requests").GetInt32()));
        Assert.True(root.GetProperty("reusing").GetBoolean());
    }

    [Fact]
    public async Task Bench_json_without_the_pool_flag_omits_the_connection_keys()
    {
        // Absent is meaningfully different from empty: empty would say "we looked and found none".
        await using var app = await StartKeepAliveAsync();

        var (_, output, _) = Run("bench", app.Urls.First() + "/k", "-n", "2", "-c", "1", "--json");

        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.TryGetProperty("connections", out _));
        Assert.False(document.RootElement.TryGetProperty("reusing", out _));
    }

    [Fact]
    public async Task Bench_without_the_flag_reports_no_connections()
    {
        await using var app = await StartKeepAliveAsync();

        var (_, output, _) = Run("bench", app.Urls.First() + "/k", "-n", "2", "-c", "1");

        Assert.DoesNotContain("request(s) over", output);
    }

    [Fact]
    public void Help_is_reachable_and_describes_the_question_it_answers()
    {
        var (code, output, _) = Run("help", "connections");

        Assert.Equal(0, code);
        Assert.Contains("reusing connections", output);
        Assert.Contains("--parallel", output);
    }
}
