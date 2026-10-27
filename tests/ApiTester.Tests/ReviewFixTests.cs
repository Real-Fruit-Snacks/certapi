using System.IO;
using System.IO.Compression;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The points an independent review of the 1.94 work raised, each pinned.</summary>
public class ReviewFixTests
{
    private static (int Code, string Out, string Err) Run(CliServices? services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, new MemoryStream(), services ?? new CliServices
        {
            UserConfigPath = null,
            LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"),
        });
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public void Dynamic_allowed_hosts_are_named_in_the_vhost_certificate()
    {
        var names = VirtualHosts.CertificateNames("gw.localhost", new[] { "wiki.corp", "*.lab", "*" }, out bool uncovered);
        Assert.Contains("*.gw.localhost", names);
        Assert.Contains("wiki.corp.gw.localhost", names);
        Assert.Contains("*.lab.gw.localhost", names);
        Assert.True(uncovered);
        VirtualHosts.CertificateNames("gw.localhost", null, out bool none);
        Assert.False(none);
    }

    [Fact]
    public void A_sites_own_sni_through_a_proxy_is_refused_before_anything_starts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"certapi-sni-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """[{ "name": "hr", "upstream": "https://hr.corp", "sni": "hr.internal" }]""");
        try
        {
            var r = Run(null, "serve", "--check", "--port", "8819", "--routes", file, "--proxy", "http://proxy.corp:8080", "--no-config");
            Assert.Equal(2, r.Code);
            Assert.Contains("site hr", r.Err);
            Assert.Contains("\"sni\" cannot be used through a proxy", r.Err);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_body_that_inflates_past_the_limit_is_left_alone()
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (int i = 0; i < 34; i++) gz.Write(zeros);
        }
        Assert.True(packed.Length < 1 << 20);
        Assert.Null(BodyRewriter.Decode(packed.ToArray(), "gzip"));
    }

    [Fact]
    public void Json_without_a_length_may_be_a_stream_and_is_not_buffered()
    {
        Assert.False(BodyRewriter.Applies("application/json", hasLength: false));
        Assert.True(BodyRewriter.Applies("application/json", hasLength: true));
        Assert.True(BodyRewriter.Applies("text/html", hasLength: false));
    }

    [Fact]
    public void Data_binary_is_explained_not_aliased()
    {
        var r = Run(null, "send", "http://127.0.0.1:9/", "--data-binary", "@x.bin", "--no-config");
        Assert.Equal(2, r.Code);
        Assert.Contains("-F name=@file", r.Err);
        // And run's --data (an iteration file) is no longer reachable through it.
        Assert.DoesNotContain(Args.Aliases, a => a.Value.Contains("--data-binary"));
    }

    [Fact]
    public void A_capital_letter_option_is_not_mistaken_for_a_repeat_of_its_lower_case()
    {
        var r = Run(null, "send", "http://127.0.0.1:9/", "-o", "a.json", "-O", "--no-config");
        Assert.Equal(2, r.Code);
        Assert.Contains("Use -o <file>", r.Err);
        Assert.DoesNotContain("more than once", r.Err);
    }

    [Fact]
    public void An_options_value_of_dash_h_is_not_a_request_for_help()
    {
        Assert.False(CliApp.AsksForHelp(new[] { "https://x", "-d", "-h" }, SendCommand.Help));
        Assert.True(CliApp.AsksForHelp(new[] { "https://x", "-d", "body", "-h" }, SendCommand.Help));
        Assert.True(CliApp.AsksForHelp(new[] { "https://x", "--pretty", "--help" }, SendCommand.Help));
    }

    [Fact]
    public void Completion_reads_past_global_options_before_the_command()
    {
        var svc = new CliServices { UserConfigPath = null, LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()) };
        Assert.Contains("--cert", CompletionCommand.Complete(new[] { "--profile", "corp", "--debug", "send", "https://x", "--ce" }, svc));
        Assert.Contains("send", CompletionCommand.Complete(new[] { "--debug", "se" }, svc));
    }

    [Fact]
    public void Config_init_writes_where_the_global_config_option_says()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-initcfg-").FullName;
        try
        {
            var target = Path.Combine(dir, "team.json");
            var r = Run(new CliServices { UserConfigPath = Path.Combine(dir, "user.json"), WorkingDirectory = dir }, "--config", target, "config", "init");
            Assert.Equal(0, r.Code);
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(Path.Combine(dir, "user.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_typed_user_agent_header_wins_over_dash_A()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run(null, "send", srv.BaseUrl.TrimEnd('/') + "/x", "-A", "from-A", "-H", "User-Agent: from-H", "-q", "--json", "--no-config");
        Assert.Equal(0, r.Code);
        Assert.Contains("from-H", r.Out);
        Assert.DoesNotContain("from-A", r.Out);
    }

    [Fact]
    public void A_replace_that_cannot_put_the_file_in_place_leaves_nothing_behind()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-replace-").FullName;
        try
        {
            var target = Path.Combine(dir, "certapi.exe");
            Directory.CreateDirectory(target);   // something that is not a file is in the way
            Assert.ThrowsAny<IOException>(() => UpdateCommand.Replace(target, Encoding.UTF8.GetBytes("new")));
            Assert.False(File.Exists(target + ".new"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
