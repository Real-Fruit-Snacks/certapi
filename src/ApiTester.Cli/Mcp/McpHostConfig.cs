using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiTester.Cli.Mcp;

/// <summary>`certapi mcp --print-config &lt;host&gt;`: the block an MCP host's configuration file needs
/// to launch this server with the options the command line was given. Writing that JSON by hand is
/// where a path loses a backslash or an option lands outside <c>args</c>.</summary>
public static class McpHostConfig
{
    /// <summary>Hosts and the shape each one's file takes. Claude Desktop, Claude Code and Cursor
    /// share <c>mcpServers</c>; VS Code uses <c>servers</c> with an explicit type.</summary>
    private static readonly Dictionary<string, (string Key, bool Typed, string File)> Hosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = ("mcpServers", false, "claude_desktop_config.json (Claude Desktop) or .mcp.json (Claude Code)"),
            ["claude-desktop"] = ("mcpServers", false, "claude_desktop_config.json"),
            ["claude-code"] = ("mcpServers", false, ".mcp.json"),
            ["vscode"] = ("servers", true, ".vscode/mcp.json"),
            ["code"] = ("servers", true, ".vscode/mcp.json"),
            ["cursor"] = ("mcpServers", false, ".cursor/mcp.json"),
        };

    /// <summary>Options whose value is a secret: written into a configuration file it would sit
    /// there in the clear, and usually in a synced folder.</summary>
    private static readonly string[] Secrets = ["--cert-password", "--cert-pin", "--proxy-user", "-U"];

    /// <summary>Options whose value is a file. An MCP host chooses the working directory, so a
    /// relative path that works here would not work there.</summary>
    private static readonly HashSet<string> PathOptions = new(StringComparer.Ordinal)
    {
        "--workspace", "--cert-file", "--key-file", "--protoset", "--ca-file", "--config"
    };

    public static string Render(string host, IReadOnlyList<string> tokens, string? processPath, string? workingDirectory = null)
    {
        if (!Hosts.TryGetValue(host, out var shape))
            throw new CliUsageException($"--print-config '{host}': give claude, vscode or cursor.");
        workingDirectory ??= Environment.CurrentDirectory;

        var args = new List<string> { "mcp" };
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            string name = t.StartsWith("--", StringComparison.Ordinal) && t.IndexOf('=') is var eq and > 2 ? t[..eq] : t;
            bool attached = name.Length != t.Length;
            if (name == "--print-config")
            {
                if (!attached) i++;
                continue;
            }
            if (Secrets.Contains(name, StringComparer.Ordinal))
                throw new CliUsageException(name switch
                {
                    "--cert-pin" => "--print-config will not write --cert-pin into a configuration file: set CERTAPI_CERT_PIN in the host's \"env\" block instead.",
                    "--cert-password" => "--print-config will not write --cert-password into a configuration file: import the certificate into the Windows store and pin it with --cert.",
                    _ => "--print-config will not write --proxy-user into a configuration file: the signed-in Windows user is used for an authenticating proxy without it."
                });
            if (PathOptions.Contains(name))
            {
                string? value = attached ? t[(name.Length + 1)..] : i + 1 < tokens.Count ? tokens[++i] : null;
                args.Add(name);
                if (value is not null) args.Add(Path.IsPathRooted(value) ? value : Path.GetFullPath(value, workingDirectory));
                continue;
            }
            args.Add(t);
        }

        // Running under `dotnet certapi.dll` there is no program to name: the installed command is.
        string command = processPath is null ||
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "certapi" : processPath;

        var server = new JsonObject();
        if (shape.Typed) server["type"] = "stdio";
        server["command"] = command;
        server["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        var root = new JsonObject { [shape.Key] = new JsonObject { ["certapi"] = server } };
        string json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return json + Environment.NewLine;
    }

    /// <summary>The file the block goes in, for the note printed beside it.</summary>
    public static string FileFor(string host) => Hosts.TryGetValue(host, out var s) ? s.File : "";
}
