using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ConfigCommand
{
    public const string Help = """
        Usage: certapi config path
               certapi config show [--profile <name>]
               certapi config profiles
               certapi config init [--local | --path <file>] [--name <profile>] [--cert <thumb|subject>]
                                   [--proxy <url>] [--force]

        Says what configuration is actually in effect, in the same spirit as `doctor` and `proxy`:
        a default that surprises you should be traceable to the file that set it.

          path       Which configuration file was found, and by which rule
          show       The resolved profile, exactly as a command would see it
          profiles   The profile names the file defines
          init       Write a starter configuration file: one profile, every other field present
                     but commented out, with a link to the reference. Your per-user file by
                     default; --local writes certapi.config.json here, --path anywhere. --name
                     names the profile (default "default"), --cert and --proxy fill those in.
                     An existing file is never overwritten without --force

        Configuration is looked for in this order, first match winning:
          1. --config <path>            named explicitly
          2. the CERTAPI_CONFIG environment variable
          3. certapi.config.json        found by walking up from the working directory
          4. the per-user file          %APPDATA%\certapi\config.json
        --no-config ignores all four, which is how a run is made reproducible regardless of what
        happens to sit in the working directory.

        A profile supplies defaults for the options a command already understands — certificate,
        proxy, bypass list, CA files, TLS floor, revocation, retries, timeouts, workspace, and
        standing headers. An explicitly typed flag wins over the profile, and a typed list replaces
        the profile's; the profile wins over the built-in default. A switch the profile turns on
        (insecure, browser, …) is turned off for one run with its --no- form: --no-insecure.

        A profile's "serve" section is a whole gateway: port, vhost, browser, token, allow, listen,
        cookieJar, windows auth, and "sites" — each site with its own certificate and settings, in
        the same form as a `serve --routes` file. `certapi serve --profile corp` then needs nothing
        else; sites typed on the command line replace the profile's list.

        A value may contain ${env:NAME}, read from the environment when the profile is used, so a
        password or a token lives in the environment and the file stays safe to commit. An unset
        variable is an error naming the profile, the field and the variable. `show` prints
        passwords, tokens, proxy credentials and secret headers as (set); other fields are shown
        as resolved.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi config init --cert "CN=My Client"
          certapi config init --local --name corp --proxy http://proxy.corp:8080
          certapi config path
          certapi config show --profile corp
          certapi send https://api.internal/orders --profile corp
          certapi serve --profile corp --check

        Exit 0 when the configuration could be read, 2 usage, 3 when a named file is unreadable.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // --config/--profile/--no-config are global: CliApp consumed them and recorded both the
        // file it found and the profile it resolved. This command reports THAT, rather than
        // re-running discovery — which could disagree with what the invocation actually used, and
        // would ignore an explicit --config into the bargain.
        // init's options are read before Positionals() so they are never "unknown" elsewhere.
        bool local = args.Flag("--local");
        string? initPath = args.Value("--path");
        string? initName = args.Value("--name");
        string? initCert = args.Value("--cert");
        string? initProxy = args.Value("--proxy");
        bool force = args.Flag("--force");
        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        bool isInit = positionals[0].Equals("init", StringComparison.OrdinalIgnoreCase);
        if (!isInit && (local || initPath is not null || initName is not null || initCert is not null || initProxy is not null || force))
            throw new CliUsageException("--local, --path, --name, --cert, --proxy and --force belong to 'config init'.");
        if (isInit) return Init(local, initPath, initName, initCert, initProxy, force, stdout, services);

        var source = services.ProfileSource;

        switch (positionals[0].ToLowerInvariant())
        {
            case "path":
                if (source is null)
                {
                    stdout.WriteLine("no configuration file found.");
                    stdout.WriteLine($"  looked for {ConfigLoader.FileName} from {services.WorkingDirectory} upwards,");
                    stdout.WriteLine($"  the {ConfigLoader.EnvironmentVariable} variable, and {services.UserConfigPath ?? "(no per-user path)"}.");
                }
                else
                {
                    stdout.WriteLine(source.Path);
                    stdout.WriteLine($"  found by: {source.Rule}");
                }
                return ExitCodes.Ok;

            case "profiles":
            {
                var config = Load(source);
                if (config.Profiles.Count == 0) { stdout.WriteLine("no profiles are defined."); return ExitCodes.Ok; }
                foreach (var name in config.Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    stdout.WriteLine(name + (string.Equals(name, config.DefaultProfile, StringComparison.OrdinalIgnoreCase) ? "   (default)" : ""));
                return ExitCodes.Ok;
            }

            case "show":
            {
                if (source is null) { stdout.WriteLine("no configuration file found."); return ExitCodes.Ok; }
                var profile = services.Profile;
                if (profile is null)
                {
                    stdout.WriteLine($"{source.Path} has no default profile — name one with --profile.");
                    return ExitCodes.Ok;
                }
                Render(profile, stdout);
                return ExitCodes.Ok;
            }

            default:
                throw new CliUsageException(Help);
        }
    }

    private static int Init(bool local, string? path, string? name, string? cert, string? proxy, bool force,
                            TextWriter stdout, CliServices services)
    {
        if (path is not null && services.ExplicitConfigPath is not null)
            throw new CliUsageException("--path and --config both say where to write; give one.");
        path ??= services.ExplicitConfigPath;
        if (local && path is not null) throw new CliUsageException("--local and --path (or --config) both say where to write; give one.");
        string target = path is not null ? Path.GetFullPath(path)
            : local ? Path.Combine(services.WorkingDirectory, ConfigLoader.FileName)
            : services.UserConfigPath ?? throw new CliUsageException("There is no per-user configuration path here; use --local or --path <file>.");
        name ??= "default";
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c == '"'))
            throw new CliUsageException($"--name '{name}' is not a usable profile name (no spaces or quotes).");
        if (File.Exists(target) && !force)
            throw new CliDataException($"{target} already exists. Add --force to replace it, or edit it directly.");

        string text = StarterConfig(name, cert, proxy);
        // Proved before it is written: the starter must load with the same reader every command uses.
        ConfigLoader.Parse(text).Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
        stdout.WriteLine($"wrote {target}");
        stdout.WriteLine($"  profile '{name}' is the default, so every command uses it without --profile.");
        stdout.WriteLine("  Uncomment what you need, then check it with: certapi config show");
        return ExitCodes.Ok;
    }

    /// <summary>The starter file: one profile, the fields worth knowing about present but commented
    /// out, so it changes nothing until something is uncommented. JSON with comments, which the
    /// loader accepts.</summary>
    internal static string StarterConfig(string name, string? cert, string? proxy)
    {
        static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        string Line(string field, string? value, string example, string note) =>
            value is not null ? $"      \"{field}\": {Json(value)},{note}"
                              : $"      // \"{field}\": {example},{note}";
        return $$"""
            {
              // certapi configuration. Every field is optional, and a flag typed on the command line
              // always wins over what is here. Reference:
              // https://github.com/Real-Fruit-Snacks/certapi/blob/main/wiki/27-Configuration.md
              "defaultProfile": {{Json(name)}},
              "profiles": {
                {{Json(name)}}: {
            {{Line("cert", cert, "\"CN=My Client\"", "            // a subject or thumbprint from `certapi certs`")}}
                  // "store": "CurrentUser",           // LocalMachine also searches the machine store
                  // "certFile": "client.pfx",         // a file instead of the store
                  // "certPassword": "${env:CERT_PASSWORD}",
            {{Line("proxy", proxy, "\"http://proxy.corp:8080\"", " // http(s) or socks5://")}}
                  // "proxyUser": "${env:PROXY_USER}",
                  // "noProxyList": "internal.corp,.lab,10.0.0.0/8",
                  // "caFile": "corp-root.pem",         // a private root, relative to this file
                  // "tlsMin": "1.2",
                  // "revocation": "offline",            // none, offline or online
                  // "retry": 2,
                  // "timeout": 60,
                  // "connectTimeout": 10,
                  // "headers": { "X-Team": "payments" },
                  // "serve": {
                  //   "port": 8819,
                  //   "browser": true,
                  //   "sites": [ { "name": "api", "upstream": "https://api.internal" } ]
                  // }
                }
              }
            }
            """;
    }

    private static CertapiConfig Load(ConfigSource? source)
    {
        if (source is null) return CertapiConfig.Empty;
        try { return ConfigLoader.Parse(File.ReadAllText(source.Path), source); }
        catch (ConfigException ex) { throw new CliDataException(ex.Message); }
        catch (IOException ex) { throw new CliDataException($"could not read {source.Path}: {ex.Message}"); }
    }

    private static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static void Render(ConfigProfile p, TextWriter stdout)
    {
        void Line(string name, string? value)
        {
            if (value is not null) stdout.WriteLine($"  {name,-18} {value}");
        }

        // A secret is never printed. The loader has already resolved ${env:…} by the time a
        // profile exists, so what is shown is the field name and the fact that it is set —
        // printing the value would defeat the reason for keeping it in the environment.
        Line("cert", p.Cert);
        Line("store", p.Store);
        Line("certFile", p.CertFile);
        Line("certPassword", p.CertPassword is null ? null : "(set)");
        Line("keyFile", p.KeyFile);
        Line("proxy", p.Proxy is null ? null : MarkdownSecrets.RedactUrl(p.Proxy));
        Line("proxyUser", p.ProxyUser is null ? null : "(set)");
        Line("noProxy", p.NoProxy?.ToString().ToLowerInvariant());
        Line("noProxyList", p.NoProxyList);
        Line("revocation", p.Revocation);
        Line("revocationStrict", p.RevocationStrict?.ToString().ToLowerInvariant());
        Line("retry", p.Retry?.ToString());
        Line("timeout", p.Timeout?.ToString());
        Line("insecure", p.Insecure?.ToString().ToLowerInvariant());
        Line("caFile", p.CaFiles.Count == 0 ? null : string.Join(", ", p.CaFiles));
        Line("tlsMin", p.TlsMin);
        Line("connectTimeout", p.ConnectTimeout?.ToString());
        Line("workspace", p.Workspace);
        // A header value is shown only when it was written literally; one resolved from
        // ${env:…} is a secret by declaration (that is why it was put in the environment), and a
        // credential-bearing header is one by name.
        foreach (var header in p.Headers)
        {
            bool secret = p.SecretHeaderNames.Contains(header.Key) || IsCredentialHeader(header.Key);
            stdout.WriteLine($"  {"header",-18} {header.Key}: {(secret ? "(set)" : header.Value)}");
        }

        if (p.Serve is not { } s) return;
        string? B(bool? b) => b?.ToString().ToLowerInvariant();
        Line("serve.port", s.Port?.ToString());
        Line("serve.vhost", s.Vhost is null ? null : s.VhostSuffix ?? B(s.Vhost));
        Line("serve.browser", B(s.Browser));
        Line("serve.cors", s.Cors is null ? null : s.CorsOrigins.Count > 0 ? string.Join(", ", s.CorsOrigins) : B(s.Cors));
        Line("serve.token", s.Token is null ? null : "(set)");
        Line("serve.allow", s.Allow.Count == 0 ? null : string.Join(", ", s.Allow));
        Line("serve.listen", s.Listen);
        Line("serve.cookieJar", s.CookieJar is null ? null : s.CookieJarFile ?? B(s.CookieJar));
        Line("serve.autoToken", B(s.AutoToken));
        Line("serve.windowsAuth", B(s.WindowsAuth));
        Line("serve.windowsUser", s.WindowsUser);
        Line("serve.windowsPassword", s.WindowsPassword is null ? null : "(set)");
        Line("serve.forwarded", B(s.Forwarded));
        Line("serve.maxBody", s.MaxBody);
        Line("serve.timeout", s.Timeout?.ToString());
        Line("serve.tls", B(s.Tls));
        // One line per site, in the startup table's words: where it goes and what it changes.
        // Header values, passwords and certificate passwords are never shown.
        foreach (var site in s.Sites)
        {
            var notes = new List<string>();
            if (site.Cert is not null) notes.Add("cert " + site.Cert);
            if (site.CertFile is not null) notes.Add("certFile " + site.CertFile);
            if (site.Insecure is { } i) notes.Add(i ? "insecure" : "verify");
            foreach (var ca in site.CaFiles) notes.Add("ca " + ca);
            if (site.Revocation is not null) notes.Add("revocation " + site.Revocation);
            if (site.WindowsAuth == false) notes.Add("no windows-auth");
            else if (site.WindowsUser is not null) notes.Add("windows-auth " + site.WindowsUser);
            else if (site.WindowsAuth == true) notes.Add("windows-auth");
            if (site.Headers.Count > 0) notes.Add("headers " + string.Join(",", site.Headers.Select(h => h.Key)));
            string label = site.Name ?? site.Prefix;
            stdout.WriteLine($"  {"serve.site",-18} {label} -> {site.Upstream.GetLeftPart(UriPartial.Authority)}" +
                             (notes.Count == 0 ? "" : "  (" + string.Join(", ", notes) + ")"));
        }
    }
}
