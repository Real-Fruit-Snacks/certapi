using System.IO;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CompletionTests
{
    private static CliServices Services(string? workingDirectory = null) => new()
    {
        LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"),
        UserConfigPath = null,
        WorkingDirectory = workingDirectory ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=Matt Client", Issuer = "CN=CA", Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30), HasClientAuthEku = true,
                Certificate = null!,
            },
        },
    };

    private static List<string> Complete(params string[] words) =>
        CompletionCommand.Complete(words, Services()).ToList();

    [Fact]
    public void The_first_word_completes_to_commands()
    {
        var c = Complete("se");
        Assert.Equal(new[] { "send", "selftest", "serve" }.OrderBy(x => x), c.OrderBy(x => x));
        Assert.Equal(new[] { "sse" }, Complete("ss"));
        Assert.Contains("completion", Complete("comp"));
    }

    [Fact]
    public void Options_come_from_the_commands_own_help()
    {
        var c = Complete("send", "https://x", "--ce");
        Assert.Contains("--cert", c);
        Assert.Contains("--cert-file", c);
        Assert.DoesNotContain("--upstream", Complete("send", "https://x", "--up"));
        Assert.Contains("--upstream", Complete("serve", "--up"));
    }

    [Fact]
    public void Global_options_and_help_are_offered_everywhere()
    {
        var c = Complete("doctor", "https://x", "--");
        Assert.Contains("--debug", c);
        Assert.Contains("--profile", c);
        Assert.Contains("--help", c);
    }

    [Fact]
    public void Fixed_choices_complete_after_their_option()
    {
        Assert.Equal(new[] { "POST", "PUT", "PATCH" }, Complete("send", "https://x", "-X", "P"));
        Assert.Equal(new[] { "CurrentUser", "LocalMachine" }, Complete("send", "https://x", "--store", ""));
        Assert.Equal(new[] { "none", "offline", "online" }, Complete("send", "https://x", "--revocation", ""));
        Assert.Contains("client_credentials", Complete("token", "--grant", ""));
    }

    [Fact]
    public void Certificates_complete_from_the_store()
    {
        var c = Complete("send", "https://x", "--cert", "");
        Assert.Contains("CN=Matt Client", c);
        Assert.Contains("ABCDEF0123456789ABCDEF0123456789ABCDEF01", c);
        Assert.Equal(new[] { "CN=Matt Client" }, Complete("send", "https://x", "--cert", "cn="));
    }

    [Fact]
    public void A_flag_does_not_swallow_the_next_word()
    {
        // -k takes no value, so what follows it is not completed as one.
        Assert.Empty(Complete("send", "https://x", "-k", ""));
    }

    [Fact]
    public void Subcommands_complete_after_their_command()
    {
        Assert.Equal(new[] { "list", "add", "remove" }, Complete("trust", ""));
        Assert.Equal(new[] { "path", "show", "profiles", "init" }, Complete("config", ""));
        Assert.Contains("curl", Complete("import", ""));
        Assert.Equal(new[] { "powershell" }, Complete("completion", ""));
        Assert.Empty(Complete("mock", ""));
        Assert.Empty(Complete("send", ""));
    }

    [Fact]
    public void Help_completes_command_names()
    {
        Assert.Contains("send", Complete("help", "s"));
        Assert.DoesNotContain("help", Complete("help", "h"));
    }

    [Fact]
    public void Profile_names_come_from_the_discovered_configuration()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "certapi.config.json"), """{"profiles":{"corp":{"timeout":30},"lab":{}}}""");
            var c = CompletionCommand.Complete(new[] { "send", "https://x", "--profile", "" }, Services(dir)).ToList();
            Assert.Equal(new[] { "corp", "lab" }, c.OrderBy(x => x));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("--cert", true)]
    [InlineData("-X", true)]
    [InlineData("-H", true)]
    [InlineData("--query", true)]
    [InlineData("--insecure", false)]
    [InlineData("--pretty", false)]
    public void Value_taking_options_are_read_from_the_help(string option, bool takes) =>
        Assert.Equal(takes, CompletionCommand.TakesValue(SendCommand.Help, option));

    [Fact]
    public void The_hidden_command_prints_one_completion_per_line()
    {
        var so = new StringWriter();
        Assert.Equal(0, CliApp.Run(new[] { "__complete", "--word", "--", "tru" }, so, new StringWriter(), null, Services()));
        Assert.Equal("trust", so.ToString().Trim());

        so = new StringWriter();
        CliApp.Run(new[] { "__complete", "--empty", "--", "trust" }, so, new StringWriter(), null, Services());
        Assert.Equal(new[] { "list", "add", "remove" }, so.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public void A_thumbprint_carries_its_subject_as_the_tooltip()
    {
        var so = new StringWriter();
        CliApp.Run(new[] { "__complete", "--word", "--", "send", "https://x", "--cert", "AB" }, so, new StringWriter(), null, Services());
        Assert.StartsWith("ABCDEF0123456789ABCDEF0123456789ABCDEF01\tCN=Matt Client  (expires ", so.ToString());
    }

    [Fact]
    public void Global_options_typed_as_words_are_not_acted_on()
    {
        // --profile with nothing after it would be a usage error as a real option; while
        // completing, it is just a word being typed.
        var so = new StringWriter();
        var se = new StringWriter();
        Assert.Equal(0, CliApp.Run(new[] { "__complete", "--empty", "--", "send", "https://x", "--profile" }, so, se, null, Services()));
        Assert.Equal("", se.ToString());
    }

    [Fact]
    public void The_script_registers_a_native_completer_that_calls_back()
    {
        var so = new StringWriter();
        Assert.Equal(0, CliApp.Run(new[] { "completion", "powershell" }, so, new StringWriter(), null, Services()));
        Assert.Contains("Register-ArgumentCompleter -Native", so.ToString());
        Assert.Contains("__complete", so.ToString());
    }

    [Fact]
    public void Another_shell_is_refused_with_the_one_that_works()
    {
        var se = new StringWriter();
        Assert.Equal(2, CliApp.Run(new[] { "completion", "bash" }, new StringWriter(), se, null, Services()));
        Assert.Contains("certapi completion powershell", se.ToString());
    }

    [Fact]
    public void Every_completed_option_is_one_the_parser_reads()
    {
        // Completions come from help text; an option the help names but no parser reads would be
        // completed and then refused. Ground truth is every option literal the CLI source asks for.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "wiki"))) dir = dir.Parent;
        if (dir is null) return;   // not a source checkout
        string cli = Path.Combine(dir.FullName, "src", "ApiTester.Cli");
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(cli, "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), @"""(-{1,2}[A-Za-z0-9][A-Za-z0-9.\-]*)"""))
                read.Add(m.Groups[1].Value);
        // A switch's --no- form is derived, not written out.
        foreach (var file in Directory.GetFiles(cli, "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), @"args\.(?:Switch|Negated)\(""--([a-z0-9.\-]+)"""))
                read.Add("--no-" + m.Groups[1].Value);

        var unread = new List<string>();
        foreach (var command in CompletionCommand.CommandNames)
        {
            if (CliApp.HelpFor(command) is not { } help) continue;
            foreach (var option in CompletionCommand.OptionsOf(help))
                if (option.StartsWith("--") && !read.Contains(option)) unread.Add($"{command}: {option}");
        }
        Assert.Empty(unread);
    }
}
