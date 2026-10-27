using System.IO;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class ConfigInitTests
{
    private static (int Code, string Out, string Err) Run(CliServices services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, services: services);
        return (code, so.ToString(), se.ToString());
    }

    private static (CliServices Services, string Dir) Sandbox()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-init-").FullName;
        return (new CliServices
        {
            WorkingDirectory = dir,
            UserConfigPath = Path.Combine(dir, "user", "certapi", "config.json"),
            LiveStatePath = Path.Combine(dir, "state.json"),
        }, dir);
    }

    [Fact]
    public void Init_writes_the_per_user_file_and_it_loads_as_the_default_profile()
    {
        var (svc, dir) = Sandbox();
        try
        {
            var r = Run(svc, "config", "init", "--cert", "CN=My Client", "--proxy", "http://proxy.corp:8080");
            Assert.Equal(0, r.Code);
            Assert.Contains(svc.UserConfigPath!, r.Out);
            var config = ConfigLoader.Parse(File.ReadAllText(svc.UserConfigPath!));
            var p = config.Resolve(null)!;
            Assert.Equal("default", p.Name);
            Assert.Equal("CN=My Client", p.Cert);
            Assert.Equal("http://proxy.corp:8080", p.Proxy);
            Assert.Null(p.Timeout);                     // everything else stays commented out
            Assert.Null(p.Insecure);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Local_writes_in_the_working_directory_under_the_given_name()
    {
        var (svc, dir) = Sandbox();
        try
        {
            Assert.Equal(0, Run(svc, "config", "init", "--local", "--name", "corp").Code);
            var file = Path.Combine(dir, ConfigLoader.FileName);
            var config = ConfigLoader.Parse(File.ReadAllText(file));
            Assert.Equal("corp", config.DefaultProfile);
            Assert.Null(config.Resolve("corp")!.Cert);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_existing_file_is_kept_unless_forced()
    {
        var (svc, dir) = Sandbox();
        try
        {
            var file = Path.Combine(dir, "mine.json");
            File.WriteAllText(file, "{ broken");
            var r = Run(svc, "config", "init", "--path", file);
            Assert.Equal(3, r.Code);
            Assert.Contains("--force", r.Err);
            Assert.Equal("{ broken", File.ReadAllText(file));

            // --force replaces it — even though the file it replaces could not be read.
            Assert.Equal(0, Run(svc, "config", "init", "--path", file, "--force").Code);
            ConfigLoader.Parse(File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Init_works_while_the_discovered_configuration_is_broken()
    {
        var (svc, dir) = Sandbox();
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigLoader.FileName), "{ broken");
            Assert.Equal(0, Run(svc, "config", "init").Code);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Init_options_on_another_subcommand_are_a_usage_error()
    {
        var (svc, dir) = Sandbox();
        try
        {
            var r = Run(svc, "config", "path", "--force");
            Assert.Equal(2, r.Code);
            Assert.Contains("config init", r.Err);
            Assert.Equal(2, Run(svc, "config", "init", "--local", "--path", "x.json").Code);
            Assert.Equal(2, Run(svc, "config", "init", "--name", "two words").Code);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("C:\\certs\\client.pfx")]
    public void Values_are_escaped_into_valid_json(string cert)
    {
        var text = ConfigCommand.StarterConfig("default", cert, null);
        Assert.Equal(cert, ConfigLoader.Parse(text).Resolve(null)!.Cert);
    }
}
