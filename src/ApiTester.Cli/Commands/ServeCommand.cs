using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ServeCommand
{
    public const string Help = """
        Usage: certapi serve <upstream> --port <n> [options]
               certapi serve --port <n> --upstream <name>=<url> [--upstream …] [options]
               certapi serve --profile <name>              (everything from a config profile)

        Runs a local gateway on http://127.0.0.1:<port> that forwards requests to one or many
        sites with your client certificate attached. Point an app or a browser at the local port
        and it reaches certificate-protected sites with no mTLS code of its own.

        Sites come from the positional <upstream>, --upstream, --routes files, or a profile's
        "serve" section; any host on --allow is reachable as well. At start the gateway prints a
        table: each site, the URL to open, where it goes, the certificate it presents, and what it
        does differently. --check prints the same table and exits without starting.

          <upstream>              A full https:// URL, or the name of a saved website
          --upstream <name>=<url> A site, repeatable: api=https://api.corp is mounted at /api (and,
                                  with --vhost, at api.gw.localhost). A GET /api/orders reaches
                                  https://api.corp/orders; a path under no site is a 404 that
                                  contacts nothing. Write /<path>=<url> to mount at a path
                                  instead (longest prefix wins). The positional <upstream> above
                                  is the site at /
          --routes <file>         Sites from a JSON file, repeatable. Each one can carry its own
                                  settings, which override the gateway's flags for that site only:
                                    [{ "name": "hr", "upstream": "https://hr.corp:8443",
                                       "cert": "CN=svc-hr",        or "certFile" (+ "certPassword",
                                                                   "keyFile")
                                       "insecure": true|false,     "caFile": "hr-root.pem" (or list),
                                       "revocation": "online",     "windowsAuth": true|false,
                                       "windowsUser": "CORP\\svc", "windowsPassword": "${env:HR_PW}",
                                       "headers": { "X-Team": "people" },
                                       "sni": "hr.internal" }, …]
                                  "prefix": "/path" mounts at a path instead of /<name>. Paths are
                                  relative to the file; ${env:NAME} reads the environment. A site
                                  with no "cert" presents the gateway's --cert
          --port <n>              Local port to listen on (127.0.0.1 unless --listen says otherwise)
          --vhost [<suffix>]      One origin per site instead of one path each: site "api"
                                  answers at http://api.gw.localhost:<port>/ (the site at / on
                                  http://gw.localhost:<port>/), and an allowed dynamic host at
                                  http://<host>.gw.localhost:<port>/ (hr.corp--8443 for a port,
                                  http--pub.corp for plain http). Paths, cookies and origins reach
                                  the browser as the upstream wrote them, which is what an app
                                  that uses absolute paths or compares origins needs. Browsers
                                  resolve *.localhost to loopback by themselves; another suffix
                                  needs a hosts-file entry. Location and cookie rewriting speak
                                  host form. Binds every interface (Windows needs a one-time URL
                                  reservation; the netsh command is printed), but answers only
                                  loopback unless --listen is also given. 127.0.0.1 keeps working
                                  in path form beside it
          --listen <address>      Also answer on this address — a LAN address, or 0.0.0.0 / + for
                                  every interface — so a VM, a container or a teammate can use the
                                  gateway. Needs --token, because anything that reaches the port
                                  then reaches your client certificate. Not with --tls (the
                                  generated certificate is bound to 127.0.0.1 only). On Windows a
                                  non-loopback prefix needs a one-time URL reservation; the exact
                                  netsh command is printed when it is missing
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is separate
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          --insecure              Ignore upstream server-certificate errors (internal CAs)
          --windows-auth          Answer the upstream's Negotiate/NTLM (Kerberos) challenge with
                                  your signed-in account, on every caller's behalf — an intranet
                                  site that wants Windows auth and a certificate at once.
                                  Aliases: --ntlm, --negotiate
          --windows-user <u>      Explicit Windows credentials instead (DOMAIN\user); pair with
          --windows-password <p>  the password
          --token <value>         Require callers to send this token (Authorization: Bearer
                                  <value> or X-Certapi-Token: <value>); off by default. The
                                  token header is stripped before the request is forwarded and
                                  is never recorded; a CORS preflight is answered without it,
                                  because a browser cannot attach headers to one
          --timeout <seconds>     How long to wait for the upstream's response headers (default
                                  100). A body that streams longer than that — events, downloads —
                                  is not cut off
          --max-body <bytes>      Refuse a request body larger than this with 413 (default: no
                                  limit). Suffixes k/m/g are accepted
          --forwarded             Add X-Forwarded-For, X-Forwarded-Proto, X-Forwarded-Host and
                                  Via: 1.1 certapi to forwarded requests, for an upstream that
                                  builds links from them. Off by default (byte-faithful)

        One gateway, many sites:
          --allow <hosts>         Repeatable. Turn on dynamic targets: a request to
                                  /_/https://host[:port]/path is forwarded to that host with the
                                  client certificate, for any host on this comma-separated list
                                  (exact names, *.suffix wildcards, or * for every host — which
                                  makes this an open relay on loopback and is logged as such).
                                  Anything else is refused with 403 before a socket is opened.
                                  Static --upstream mounts still work beside it, and Location /
                                  cookie rewriting follows the dynamic prefix the same way.
                                  /_/ and /_certapi/ are reserved and cannot be mounted
          /_certapi/health        Answers 200 with the gateway's state as JSON; /_certapi/routes
                                  lists the mounts and the allow list. Both honour --token
          --workspace <file>      Resolve a saved-website <upstream> from a workspace file
          -q, --quiet             No startup table or per-request log
          --check                 Resolve every site, certificate and file, print the table of
                                  sites, and exit — nothing is bound, installed or contacted. Exit 0
                                  when the configuration is usable, 2/3 with the reason when not
          --tls                   Serve over HTTPS on 127.0.0.1 with a generated gateway certificate,
                                  so Secure and __Host-/__Secure- cookies work. Binding the port needs
                                  an elevated prompt the first time; the exact netsh command is printed
                                  if it isn't available
          --tls-trust             Also install that certificate into CurrentUser\Root so the browser
                                  accepts it silently (explicit, logged, and reversible)
          --tls-untrust           Remove a previously trusted gateway certificate and exit

        Record / replay (two ends of one HAR format — capture a session live, replay it offline):
          --record <file.har>     Record every forwarded exchange to a HAR file, written on Ctrl+C
                                  (an existing file is replaced with this session). Credential
                                  headers (Authorization, Cookie, tokens) are redacted by default
          --record-include-secrets   Keep the secrets in the recording (only with --record)
          --replay <file.har>     Answer from a recorded HAR without ever contacting the upstream —
                                  develop against a captured session when the real one is gone.
                                  Mutually exclusive with --record

        Browser (so a page on another origin can call the upstream through the gateway):
          --browser               The bundle: --cors, --rewrite-cookies, --rewrite-location,
                                  --allow-upgrade, --rewrite-origin, --strip-csp and
                                  --referer-fallback at once.
                                  --cookie-jar and --auto-token stay opt-in: they make the gateway
                                  hold session state on your behalf
          --cors [<origins>]      Answer CORS preflights here and add the response headers a
                                  browser needs. With no value the caller's own Origin is
                                  echoed; a comma-separated list allows only those origins and
                                  refuses the rest with 403
          --cors-max-age <n>      How long a browser may cache a preflight answer, in seconds
                                  (default 600). Only with --cors
          --rewrite-cookies       Set-Cookie loses Domain= and Secure, and SameSite=None becomes
                                  Lax, so the browser can store the cookie against the gateway
          --rewrite-location      A 3xx Location (or a Refresh header's URL) pointing at the
                                  upstream comes back pointing at this gateway. One pointing at another mount, or at a host on the
                                  --allow list, comes back through that mount or /_/… — a login →
                                  app → login hop chain stays on the certificate. One pointing
                                  anywhere else is left alone and logged
          --allow-upgrade         Relay WebSocket connections to the upstream as well
          --rewrite-origin        Origin and Referer on a forwarded request point at the upstream
                                  instead of at this gateway, so a login form's CSRF check sees
                                  the host it expects
          --referer-fallback      A path no route owns, asked for by a page the gateway served, is
                                  redirected (307) to the same path under that page's mount or
                                  /_/host: a page from /_/https://app.corp/ that references
                                  /static/app.js by absolute path then works. Only when nothing
                                  else matched; a real mount is never shadowed
          --strip-csp             Drop Content-Security-Policy from responses: a policy written for
                                  the upstream's own origin blocks every fetch a page makes through
                                  the gateway. Loopback only; the browser's other protections stay
          --rewrite-body          Upstream URLs written into HTML, CSS, JavaScript, JSON and XML
                                  bodies (links, script sources, a meta refresh, an API base in a
                                  config file) point back through the gateway, the same way a
                                  Location does. Compressed bodies are decompressed and sent plain;
                                  bodies over 32 MB pass untouched. Not part of --browser; under
                                  --vhost most sites need neither

          Multi-step logins across hosts (a login host that redirects to an app host and back):
          --cookie-jar [<file>]   Keep upstream cookies on the gateway, stored against the real
                                  upstream URL with their Domain intact, and send them on every
                                  forwarded request to a matching host. The browser cannot do this
                                  itself: it stores every cookie against the gateway's one origin,
                                  so a Domain=.corp session cookie set by login.corp never reaches
                                  app.corp. Memory only unless a file is named: then the jar is
                                  loaded at start and written at shutdown, encrypted for your
                                  Windows user (DPAPI) — never in the clear, so a session survives
                                  a restart without sitting readable on disk
          --auto-token            Attach a bearer token captured earlier for the upstream's origin
                                  (certapi send --capture, the app's Capture tab, a session capture)
                                  when the request carries no Authorization of its own

          Without these the gateway stays a byte-faithful relay: nothing is added or rewritten.
          A cookie named __Host-… or __Secure-… needs the Secure attribute, which a plaintext
          http://127.0.0.1 origin cannot carry, so run with --tls and it works; without --tls
          such cookies are relayed and logged as a warning.

          Chrome's Private Network Access (PNA) check is a further gate before a page on a
          public origin can reach a private or loopback address: its preflight carries
          Access-Control-Request-Private-Network: true, and the gateway answers
          Access-Control-Allow-Private-Network: true only when --cors is on and the request's
          Origin passes that same allowlist. Letting a public origin reach a loopback service is
          a real exposure, which is why that answer follows the allowlist, and why naming the
          origins you develop from with --cors <origins> is the safer form than leaving it
          echoing whoever asks.

        Headers (apply to forwarded HTTP traffic, with or without --browser — a header rule is not
        a browser concern):
          --request-header "Name: value"    Set a header on the request before it reaches the
                                             upstream: replaces it if the caller already sent one,
                                             adds it if not. Repeatable
          --remove-request-header <name>    Strip a header from the request before it reaches the
                                             upstream. Repeatable. Removal wins over
                                             --request-header naming the same header
          --response-header "Name: value"   Set a header on the response before it reaches the
                                             caller, the same replace-or-add rule as above.
                                             Repeatable, and applied after --browser's own rewrites
                                             (CORS, cookies, Location), so a header you set here
                                             wins over one the gateway injected
          --remove-response-header <name>   Strip a header from the response before it reaches the
                                             caller. Repeatable. Removal wins over
                                             --response-header naming the same header

          Connection, Keep-Alive, Transfer-Encoding, Content-Length, TE, Trailer, Upgrade,
          Proxy-Authenticate, Proxy-Authorization and Host are refused with a usage error rather
          than silently ignored: the first nine frame the HTTP message and the HTTP stack manages
          them, and Host is set by the gateway's HTTP client from the upstream URI, so a rule here
          would only ever half-apply.

          A missing header name, or one carrying a character an HTTP field name cannot hold — a
          space, an embedded colon — is refused the same way: the header could never match, so the
          rule would be dropped rather than applied.

          These rules act on forwarded HTTP traffic only: never on a CORS/PNA preflight the gateway
          answers itself, its own 404/400/502 error pages, or a relayed WebSocket upgrade. With
          none of the four flags the gateway stays the byte-faithful relay it has always been.


        """ + TransportFlags.Help + """


          These apply to the gateway's upstream connections: the proxy options, --resolve,
          --ca-file, --revocation / --revocation-strict, --tls-min, --connect-timeout, --offline
          -4 / -6 and --sni (every site's handshake; a site's own "sni" wins). The redirect,
          decompression, HTTP-version and retry flags are accepted but do not apply: the gateway
          hands back the 3xx and the exact bytes the upstream sent, and does not retry on a
          caller's behalf.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443 --insecure

          # Two upstreams behind one port, with every browser accommodation on
          certapi serve --port 8443 --cert "CN=My Client" --browser --upstream api=https://api.internal --upstream auth=https://login.internal

          # Just CORS, and only for the page you are developing
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --cors http://localhost:3000

          # HTTPS on the gateway itself, so Secure and __Host-/__Secure- cookies survive
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --browser --tls --tls-trust

          # Inject an API key the calling app never has to know
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --request-header "X-Api-Key: s3cret"

          # Named sites, each on its own origin, and the table checked before starting
          certapi serve --port 8819 --cert "CN=My Client" --vhost --browser --upstream api=https://api.corp --upstream auth=https://login.corp --check

          # Sites with their own certificates and settings, from a file
          certapi serve --port 8819 --cert "CN=My Client" --routes sites.json

          # The whole gateway from a config profile (see `certapi config --help`)
          certapi serve --profile corp

          # One gateway for every internal site, through the corporate proxy, root CA from a file
          certapi serve --port 8819 --cert "CN=My Client" --allow "*.corp,*.internal" --proxy http://proxy.corp:8080 --ca-file corp-root.pem --browser
          # then: http://127.0.0.1:8819/_/https://wiki.corp/page   http://127.0.0.1:8819/_/https://api.internal:8443/v1/orders

        A switch a --profile turns on is turned off for this run with its --no- form: --no-browser,
        --no-vhost, --no-tls, --no-cors, --no-cookie-jar, --no-auto-token, --no-windows-auth,
        --no-forwarded, --no-rewrite-body, --no-insecure.

        Answers loopback callers only unless --listen is given. Stop with Ctrl+C.
        Exit 0 clean shutdown (or a valid --check), 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // The profile's serve section supplies anything not typed: a flag on the command line
        // always wins, the same rule every other command follows.
        var sp = services.Profile?.Serve;
        string? portRaw = args.Value("--port") ?? sp?.Port?.ToString();
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        bool windowsAuthTyped = args.Flag("--windows-auth", "--ntlm", "--negotiate");
        bool windowsAuthOff = args.Negated("--windows-auth");
        if (windowsAuthTyped && windowsAuthOff)
            throw new CliUsageException("--windows-auth and --no-windows-auth contradict each other; give one.");
        string? winUserTyped = args.Value("--windows-user");
        string? winPassTyped = args.Value("--windows-password");
        // Windows credentials are one choice: typed ones replace the profile's as a pair.
        bool windowsCredsTyped = windowsAuthTyped || windowsAuthOff || winUserTyped is not null || winPassTyped is not null;
        bool windowsAuth = windowsCredsTyped ? windowsAuthTyped : sp?.WindowsAuth == true;
        string? winUser = windowsCredsTyped ? winUserTyped : sp?.WindowsUser;
        string? winPass = windowsCredsTyped ? winPassTyped : sp?.WindowsPassword;
        if (winPass is not null && winUser is null)
            throw new CliUsageException("--windows-password needs --windows-user.");
        string? token = args.Value("--token") ?? sp?.Token;
        string? timeoutRaw = args.Value("--timeout") ?? sp?.Timeout?.ToString();
        string? workspace = args.Value("--workspace");
        bool quiet = args.Flag("-q", "--quiet");
        var upstreamSpecs = args.Values("--upstream");
        var routesFiles = args.Values("--routes");
        // --browser is a bundle over the four accommodations, each of which still works on its own;
        // the flag is read first so every one of them is consumed either way.
        bool browserBundle = args.Switch("--browser", sp?.Browser);
        // --cors may be repeated with origins, which add up; a bare --cors among them changes nothing.
        bool corsTyped = false;
        var corsOriginList = new List<string>();
        while (args.OptionalValue(out var oneCors, "--cors"))
        {
            corsTyped = true;
            if (oneCors is not null) corsOriginList.Add(oneCors);
        }
        bool corsOff = args.Negated("--cors");
        if (corsTyped && corsOff) throw new CliUsageException("--cors and --no-cors contradict each other; give one.");
        if (!corsTyped && !corsOff && sp?.Cors == true) { corsTyped = true; corsOriginList.AddRange(sp.CorsOrigins); }
        string? corsOrigins = corsOriginList.Count == 0 ? null : string.Join(",", corsOriginList);
        bool cors = corsTyped || (browserBundle && !corsOff);
        bool rewriteCookies = args.Flag("--rewrite-cookies") || browserBundle;
        bool rewriteLocation = args.Flag("--rewrite-location") || browserBundle;
        bool allowUpgrade = args.Flag("--allow-upgrade") || browserBundle;
        bool rewriteOrigin = args.Flag("--rewrite-origin") || browserBundle;
        bool stripCsp = args.Flag("--strip-csp") || browserBundle;
        // Not part of --browser: it buffers and decompresses page bodies, which the relay never does
        // unless asked by name.
        bool rewriteBody = args.Switch("--rewrite-body", sp?.RewriteBody);
        bool refererFallback = args.Flag("--referer-fallback") || browserBundle;
        bool cookieJar = args.OptionalValue(out var cookieJarFile, "--cookie-jar");
        bool cookieJarOff = args.Negated("--cookie-jar");
        if (cookieJar && cookieJarOff) throw new CliUsageException("--cookie-jar and --no-cookie-jar contradict each other; give one.");
        if (!cookieJar && !cookieJarOff && sp?.CookieJar == true) { cookieJar = true; cookieJarFile = sp.CookieJarFile; }
        bool autoToken = args.Switch("--auto-token", sp?.AutoToken);
        string? corsMaxAgeRaw = args.Value("--cors-max-age");
        // Header rules are not a browser concern (they work with or without --browser), but they are
        // read alongside the browser flags here so every repeatable option is consumed before
        // Positionals() runs.
        var requestHeaderSets = args.Values("--request-header");
        var requestHeaderRemovals = args.Values("--remove-request-header");
        var responseHeaderSets = args.Values("--response-header");
        var responseHeaderRemovals = args.Values("--remove-response-header");
        var transportOverrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        string? allowRaw = args.ListValue("--allow") ?? (sp is { Allow.Count: > 0 } ? string.Join(",", sp.Allow) : null);
        string? listen = args.Value("--listen") ?? sp?.Listen;
        bool vhost = args.OptionalValue(out var vhostSuffix, "--vhost");
        bool vhostOff = args.Negated("--vhost");
        if (vhost && vhostOff) throw new CliUsageException("--vhost and --no-vhost contradict each other; give one.");
        if (!vhost && !vhostOff && sp?.Vhost == true) { vhost = true; vhostSuffix = sp.VhostSuffix; }
        bool forwarded = args.Switch("--forwarded", sp?.Forwarded);
        string? maxBodyRaw = args.Value("--max-body") ?? sp?.MaxBody;

        bool check = args.Flag("--check");
        bool tls = args.Switch("--tls", sp?.Tls);
        bool tlsTrust = args.Flag("--tls-trust");
        bool tlsUntrust = args.Flag("--tls-untrust");
        string? recordPath = args.Value("--record");
        string? replayPath = args.Value("--replay");
        bool recordIncludeSecrets = args.Flag("--record-include-secrets");

        // Record captures what the upstream really said; replay answers WITHOUT an upstream. Doing
        // both at once has no meaning — you cannot record a session you are inventing.
        if (recordPath is not null && replayPath is not null)
            throw new CliUsageException("--record and --replay are mutually exclusive: one captures the upstream, the other answers without it.");
        if (recordIncludeSecrets && recordPath is null)
            throw new CliUsageException("--record-include-secrets only applies with --record.");

        if (tlsUntrust)
        {
            // A standalone maintenance action: it removes a certificate this tool installed and has
            // nothing to do with starting a listener.
            if (tls || tlsTrust)
                throw new CliUsageException("--tls-untrust is a standalone action; run 'certapi serve --tls-untrust' on its own.");
            return UntrustGatewayCertificate(stderr);
        }
        if (tlsTrust && !tls)
            throw new CliUsageException("--tls-trust only applies together with --tls.");
        if (listen is not null)
        {
            listen = listen.Trim();
            if (listen.Length == 0) throw new CliUsageException("--listen needs an address, 0.0.0.0, or +.");
            if (listen is not ("+" or "*" or "0.0.0.0" or "::" or "localhost") && !System.Net.IPAddress.TryParse(listen, out _))
                throw new CliUsageException($"--listen expects an IP address, 0.0.0.0, or +, got '{listen}'.");
            if (!IsLoopbackListen(listen) && token is null)
                throw new CliUsageException("--listen beyond loopback needs --token: anything that reaches the port reaches your client certificate.");
            if (tls && listen is not ("127.0.0.1" or "::1"))
                throw new CliUsageException("--listen cannot be combined with --tls: the gateway certificate is bound to 127.0.0.1 only.");
        }

        // Resolve the certificate before Positionals() rejects its options (store or a file). The
        // standalone --tls-untrust path above returns before this, so it never touches the store to
        // resolve a client certificate it will never use.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        // The positional upstream is sugar for --upstream /=<url>, so one form, the other, or both
        // is fine — but a gateway with nothing to forward to, or a second bare URL with no mount
        // point to tell it apart from the first, has no sensible reading.
        if (positionals.Count > 1) throw new CliUsageException(Help);
        // Sites are one choice too: any typed on the command line replace the profile's list
        // whole, rather than mixing two lists nobody wrote together.
        bool sitesTyped = positionals.Count > 0 || upstreamSpecs.Count > 0 || routesFiles.Count > 0;
        var profileSites = !sitesTyped && sp is { Sites.Count: > 0 } ? sp.Sites : null;
        if (sitesTyped && sp is { Sites.Count: > 0 })
            stderr.WriteLine($"note: sites given on the command line replace the {sp.Sites.Count} in profile '{services.Profile!.Name}'.");
        if (!sitesTyped && profileSites is null && allowRaw is null) throw new CliUsageException(Help);
        if (portRaw is null) throw new CliUsageException("serve needs --port <n> (or \"port\" in the profile's serve section).");
        if (!int.TryParse(portRaw, out int port) || port is < 1 or > 65535)
            throw new CliUsageException($"--port expects 1-65535, got '{portRaw}'.");

        int timeoutSeconds = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeoutSeconds) || timeoutSeconds <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        long? maxBody = null;
        if (maxBodyRaw is not null)
        {
            maxBody = ParseSize(maxBodyRaw)
                ?? throw new CliUsageException($"--max-body expects a byte count (suffixes k/m/g allowed), got '{maxBodyRaw}'.");
        }

        UpstreamAllowlist? allow = null;
        if (allowRaw is not null)
        {
            var entries = allowRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length == 0)
                throw new CliUsageException("--allow expects a comma-separated list of hosts (exact, *.suffix, or *).");
            foreach (var e in entries)
                if (e.Contains('/') || e.Contains(':') || e.Contains(' '))
                    throw new CliUsageException($"--allow entries are host names, not URLs or host:port, got '{e}'.");
            allow = new UpstreamAllowlist(entries);
        }

        int corsMaxAge = BrowserOptions.DefaultCorsMaxAgeSeconds;
        if (corsMaxAgeRaw is not null)
        {
            if (!int.TryParse(corsMaxAgeRaw, out corsMaxAge) || corsMaxAge < 0)
                throw new CliUsageException(
                    $"--cors-max-age expects a whole number of seconds (0 or more), got '{corsMaxAgeRaw}'.");
            if (!cors)
                throw new CliUsageException("--cors-max-age only applies together with --cors (or --browser).");
        }

        // Built here, before the listener binds or the gateway is constructed, rather than lazily on
        // the first request that would need them: a refused header is a mistake in the command line,
        // and the operator should learn about it from a usage error, not by discovering it later from
        // traffic that already went through.
        var requestSets = ParseHeaderSets(requestHeaderSets, "--request-header");
        var requestRemovals = ParseHeaderRemovals(requestHeaderRemovals, "--remove-request-header");
        var responseSets = ParseHeaderSets(responseHeaderSets, "--response-header");
        var responseRemovals = ParseHeaderRemovals(responseHeaderRemovals, "--remove-response-header");
        var headerRules = HeaderRules.TryCreate(
                              requestSets, requestRemovals, responseSets, responseRemovals,
                              out var headerRuleProblem)
                          ?? throw new CliUsageException(headerRuleProblem!);

        var routeList = new List<GatewayRoute>();
        // Resolve the positional upstream: an absolute http(s) URL, or a saved-website name.
        if (positionals.Count == 1)
            routeList.Add(new GatewayRoute("/", ResolveUpstream(positionals[0], workspace, services, stderr)));
        foreach (var spec in upstreamSpecs)
        {
            var site = ParseUpstream(spec);
            routeList.Add(site);
            // Only scheme, host and port are kept; a path in the URL would otherwise vanish unseen.
            if (PathIn(spec[(spec.IndexOf('=') + 1)..]) is { } dropped)
                stderr.WriteLine($"note: --upstream {spec}: only the scheme, host and port are used; request {site.PathPrefix.TrimEnd('/')}{dropped}… to reach {dropped}….");
        }
        if (positionals.Count == 1 && PathIn(positionals[0]) is { } droppedRoot)
            stderr.WriteLine($"note: {positionals[0]}: only the scheme, host and port are used; request {droppedRoot}… to reach it.");
        foreach (var routesFile in routesFiles) routeList.AddRange(LoadRoutesFile(routesFile, store, services, stderr, transportOverrides.ExtraRoots));
        if (profileSites is not null)
            routeList.AddRange(BuildSites(profileSites, $"profile '{services.Profile!.Name}'", store, services, stderr, transportOverrides.ExtraRoots));

        GatewayRoutes routes;
        // Two upstreams on one mount point is a configuration mistake with no right answer, so it is
        // refused here rather than quietly sending that prefix's traffic to whichever came last.
        try { routes = new GatewayRoutes(routeList); }
        // The parameter-name suffix ArgumentException appends is about the API it guards, not about
        // the command line the user typed; the sentence before it is the part they need.
        catch (ArgumentException ex) { throw new CliUsageException(ex.Message.Split(" (Parameter")[0]); }

        // Only the settings that decide how the gateway *reaches* an upstream matter here; the rest
        // are accepted (the shared parser consumes them) and documented as not applying. Validated
        // per route before the listener binds, so an unusable combination is a usage error rather
        // than a setting silently dropped on the floor.
        var transport = transportOverrides.ApplyTo(new TransportOptions());
        foreach (var route in routes.All)
        {
            var siteTransport = route.Settings?.Sni is { } siteSni ? transport with { Sni = siteSni } : transport;
            if (ApiClient.ValidateTransport(siteTransport, route.Upstream.ToString()) is { } transportProblem)
                throw new CliUsageException(route.Settings?.Sni is null ? transportProblem : $"site {route.Name ?? route.PathPrefix}: {transportProblem.Replace("--sni", "\"sni\"")}");
        }

        var logLock = new object();
        void Log(string line) { if (!quiet) lock (logLock) stderr.WriteLine(line); }

        // The origin the browser is actually talking to, which is what a rewritten Location and an
        // allow-origin decision are measured against. https when --tls is on, so Location rewriting
        // targets the right scheme and Secure/SameSite=None survive as the upstream sent them.
        string scheme = tls ? "https" : "http";
        var browser = new BrowserOptions(
            cors, ParseOrigins(corsOrigins), rewriteCookies, rewriteLocation, allowUpgrade,
            LocalOrigin: new Uri($"{scheme}://127.0.0.1:{port}"))
        {
            SecureOrigin = tls, CorsMaxAgeSeconds = corsMaxAge, RewriteOrigin = rewriteOrigin, StripCsp = stripCsp,
            RewriteBody = rewriteBody
        };

        // Pins come from the same state file saved-website names resolve against; loaded here (not
        // lazily) because the gateway's upstream connections need them for their whole lifetime.
        // With this, an upstream pinned via `certapi trust add` is reachable without --insecure —
        // the gateway was the last connection in the product that could not say that.
        var trustState = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var trustPredicates = new TrustPredicates(trustState);
        using var gateway = services.GatewayFactory(
            routes, cert, insecure, TimeSpan.FromSeconds(timeoutSeconds), transport, trustPredicates.For);
        if (allow is not null) gateway.EnableDynamicUpstreams(allow);
        if (winUser is not null || windowsAuth)
            gateway.UseWindowsAuth(winUser is not null ? WindowsAuthOptions.FromCredentials(winUser, winPass) : new WindowsAuthOptions(true));
        // Now that the gateway exists, a Location to any host it can reach is rewritten back
        // through it rather than left to leave.
        browser = browser with { GatewayPathFor = gateway.PathFor };
        // An upgrade cannot go through the gateway at all — HttpClient cannot carry one — so it
        // gets its own relay, built only when upgrades are actually allowed. It connects through
        // the gateway's own handler configuration, so proxy, pins, roots and the certificate apply.
        var relay = allowUpgrade ? new GatewayWebSocketRelay(gateway) : null;
        VirtualHosts? vhosts = null;
        if (vhost)
        {
            try { vhosts = new VirtualHosts(routes, vhostSuffix ?? VirtualHosts.DefaultSuffix, scheme, port); }
            catch (ArgumentException ex) { throw new CliUsageException("--vhost: " + ex.Message.Split(" (Parameter")[0]); }
        }
        var policy = new ServePolicy(token, forwarded, maxBody, $"127.0.0.1:{port}", scheme)
        {
            VirtualHosts = vhosts,
            // A loopback --listen is still loopback-only: --vhost binds the wildcard, and without
            // this a --listen 127.0.0.1 (which needs no --token) would answer the whole network.
            LoopbackOnly = IsLoopbackListen(listen),
            CookieJar = cookieJar ? LoadGatewayJar(cookieJarFile, stderr) : null,
            TokenState = autoToken ? trustState : null,
            RefererFallback = refererFallback
        };

        // Replay answers from a recorded HAR and never contacts the upstream; record captures every
        // forwarded exchange and writes the HAR on shutdown. Both reuse the same format the rest of
        // the product reads and writes, so a session captured here replays here (or through `mock`).
        HarReplaySource? replay = null;
        if (replayPath is not null)
        {
            if (!File.Exists(replayPath)) throw new CliDataException($"File not found: {replayPath}");
            Har replayHar;
            try { replayHar = HarReader.Parse(File.ReadAllText(replayPath)); }
            catch (HarFormatException ex) { throw new CliDataException(ex.Message); }
            replay = new HarReplaySource(replayHar);
        }
        var recorder = recordPath is not null ? new GatewayRecorder(recordIncludeSecrets) : null;
        string localBase = $"{scheme}://127.0.0.1:{port}";

        // The table of sites and what the gateway does to all of them — the startup banner and the
        // whole output of --check, built once so the two can never disagree.
        var browserParts = new List<string>();
        if (cors) browserParts.Add("cors");
        if (rewriteCookies) browserParts.Add("rewrite-cookies");
        if (rewriteLocation) browserParts.Add("rewrite-location");
        if (allowUpgrade) browserParts.Add("allow-upgrade");
        if (rewriteOrigin) browserParts.Add("rewrite-origin");
        if (stripCsp) browserParts.Add("strip-csp");
        if (refererFallback) browserParts.Add("referer-fallback");
        if (rewriteBody) browserParts.Add("rewrite-body");
        var summary = new List<string>
        {
            ServeSummary.Render(ServeSummary.Rows(routes, gateway, vhosts, allow, scheme, port)),
            ServeSummary.Gateway(new ServeSummary.GatewayFacts
            {
                Certificate = cert, Insecure = insecure,
                CaFiles = transport.ExtraRoots is { } roots ? string.Join(", ", roots.Sources.Select(Path.GetFileName)) : null,
                Proxy = transport.Proxy switch
                {
                    ProxyMode.None => "none",
                    ProxyMode.System => transport.PacUrl is { } pac ? "pac " + pac : null,
                    _ => transport.ProxyUrl is { } url ? MarkdownSecrets.RedactUrl(url) : null
                },
                Revocation = transport.Revocation,
                BrowserBundle = browserBundle, Browser = browserParts,
                CookieJar = cookieJar ? cookieJarFile ?? "memory" : null, AutoToken = autoToken,
                WindowsAuth = gateway.WindowsAuth, Token = token is not null, Listen = listen,
                Forwarded = forwarded, MaxBody = maxBody,
                HeaderRules = requestSets.Count + requestRemovals.Count + responseSets.Count + responseRemovals.Count > 0,
                Tls = tls,
            }),
        };
        if (vhosts is not null)
        {
            summary.Add($"  127.0.0.1:{port} answers in path form too (/<site>/…).");
            if (listen is null) summary.Add("  virtual hosts answer loopback callers only; add --listen (and --token) to reach them from elsewhere.");
            if (tls)
            {
                VirtualHosts.CertificateNames(vhosts.Suffix, allow?.Entries, out bool uncovered);
                summary.Add($"  the gateway certificate names {vhosts.Suffix}, every site under it" +
                            (allow is null ? "." : " and each allowed host on its default https port.") +
                            (uncovered || allow is not null ? " An allowed host on another port or over http (or under --allow *) warns once in the browser." : ""));
            }
        }
        if (allow is { AllowsAnyHost: true })
            summary.Add("  warning: --allow * makes this an open relay carrying your client certificate to any host a local caller names.");
        if (cookieJarFile is not null) summary.Add($"  the cookie jar is written to {cookieJarFile} (encrypted) at shutdown.");
        if (replay is not null)
            summary.Add($"  replay mode: answering from {replayPath} ({replay.Count} recorded exchange(s)) — the upstream is never contacted.");
        if (recorder is not null)
            summary.Add($"  recording every forwarded exchange to {recordPath}" + (recordIncludeSecrets ? " (secrets included)." : " (secrets redacted; --record-include-secrets keeps them)."));

        // --check stops here: every site, certificate, file and flag has been resolved and
        // validated, and nothing has been bound, installed or contacted.
        if (check)
        {
            int siteCount = routes.All.Count;
            stdout.WriteLine($"serve configuration is valid: {siteCount} site{(siteCount == 1 ? "" : "s")}" +
                             (allow is null ? "" : $" and dynamic hosts ({string.Join(", ", allow.Entries)})") +
                             $" on {scheme}://127.0.0.1:{port}/" + (vhosts is null ? "" : $" and {scheme}://*.{vhosts.Suffix}:{port}/") + ".");
            foreach (var line in summary) stdout.WriteLine(line);
            if (tls) stdout.WriteLine("  --tls: the certificate binding for the port is checked when the gateway starts.");
            stdout.WriteLine("Nothing was bound or contacted. Run the same command without --check to start it.");
            return ExitCodes.Ok;
        }

        X509Certificate2? gatewayCert = null;
        string? gatewayThumb = null;
        if (tls)
        {
            // Under --vhost the certificate also names the suffix and *.suffix, so every site's
            // host (api.gw.localhost) and every dynamic one is covered and the browser accepts it.
            string? untrustAfterTrust = null;
            gatewayCert = GatewayCertificate.LoadOrCreate(null,
                vhosts is null ? null : VirtualHosts.CertificateNames(vhosts.Suffix, allow?.Entries, out _), out var replacedThumb);
            gatewayThumb = LoopbackTlsBinding.NormalizeThumbprint(gatewayCert.Thumbprint!);
            if (replacedThumb is not null)
            {
                Log(vhosts is not null
                    ? $"the gateway certificate was regenerated ({gatewayThumb}): it now names the virtual hosts under {vhosts.Suffix}, or the old one was close to expiring."
                    : $"the gateway certificate was close to expiring and was regenerated ({gatewayThumb}).");
                var oldBinding = LoopbackTlsBinding.Describe(port);
                if (oldBinding is not null && oldBinding.Thumbprint.Equals(LoopbackTlsBinding.NormalizeThumbprint(replacedThumb), StringComparison.OrdinalIgnoreCase))
                {
                    // The binding is ours, to the certificate just replaced: move it along.
                    if (!LoopbackTlsBinding.IsElevated())
                        throw new CliUsageException(
                            $"The gateway certificate was regenerated, and 127.0.0.1:{port} is still bound to the old one. " +
                            "From an elevated prompt run:\n  " + LoopbackTlsBinding.BuildDeleteCommand(port) + "\n  " +
                            LoopbackTlsBinding.BuildAddCommand(port, gatewayThumb) + "\nthen start the gateway again.");
                    LoopbackTlsBinding.Unbind(port);
                }
                bool oldTrusted = GatewayCertificate.IsTrusted(LoopbackTlsBinding.NormalizeThumbprint(replacedThumb));
                // With --tls-trust the old one goes once the new one is trusted (below), so roots
                // never pile up — and a declined prompt for the new one never leaves neither.
                if (oldTrusted && tlsTrust) untrustAfterTrust = replacedThumb;
                else if (oldTrusted)
                    Log("  the old certificate is trusted in CurrentUser\\Root and the new one is not yet: add --tls-trust once (it also removes the old one).");
            }

            if (tlsTrust)
            {
                // Explicit, logged, and reversible — never done silently.
                try { GatewayCertificate.Trust(gatewayCert); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Could not install the gateway certificate into CurrentUser\\Root.", ex);
                }
                Log($"trusted the gateway certificate {gatewayThumb} in CurrentUser\\Root (undo with certapi serve --tls-untrust).");
                if (untrustAfterTrust is { } old && GatewayCertificate.IsTrusted(gatewayThumb))
                {
                    try
                    {
                        if (GatewayCertificate.Untrust(old))
                            Log($"removed the old gateway certificate {LoopbackTlsBinding.NormalizeThumbprint(old)} from CurrentUser\\Root.");
                    }
                    catch (Exception ex) { Log($"  could not remove the old gateway certificate from CurrentUser\\Root: {ex.Message}"); }
                }
            }

            var bound = LoopbackTlsBinding.Describe(port);
            if (bound is null)
            {
                if (!LoopbackTlsBinding.IsElevated())
                    throw new CliUsageException(LoopbackTlsBinding.NotElevatedMessage(port, gatewayThumb));
                var result = LoopbackTlsBinding.Bind(port, gatewayCert);
                Log($"bound the gateway certificate {result.Thumbprint} to 127.0.0.1:{port}");
                Log($"  {result.Command}");
            }
            else if (!bound.Thumbprint.Equals(gatewayThumb, StringComparison.OrdinalIgnoreCase))
            {
                // Someone else's binding: report it and stop, never replace it.
                throw new CliUsageException(LoopbackTlsBinding.AlreadyBoundMessage(port, bound));
            }
            else
            {
                Log($"reusing the certificate already bound to 127.0.0.1:{port} ({bound.Thumbprint}).");
            }
        }

        var listener = new HttpListener();
        string? extraPrefix = ListenPrefix(listen, scheme, port);
        // Host names are matched by the listener, so a virtual host needs a prefix that admits any
        // Host: the wildcard. Remote callers are then turned away in HandleAsync unless --listen
        // asked for them, so the wildcard widens what is bound, not what is answered.
        if (vhosts is not null) extraPrefix = $"{scheme}://+:{port}/";
        // The wildcard already covers loopback; binding both would claim the port twice.
        if (extraPrefix != $"{scheme}://+:{port}/") listener.Prefixes.Add($"{scheme}://127.0.0.1:{port}/");
        if (extraPrefix is not null && extraPrefix != $"{scheme}://127.0.0.1:{port}/") listener.Prefixes.Add(extraPrefix);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            // Error 5 is access denied: on Windows a non-loopback prefix needs a URL reservation,
            // which is a one-time elevated command — printed exactly, like the TLS binding's is.
            if (extraPrefix is not null && ex.ErrorCode == 5)
                throw new CliDataException(
                    $"Could not listen on {extraPrefix}: access denied. Reserve the URL once from an elevated prompt:\n" +
                    $"  netsh http add urlacl url={extraPrefix} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"\n" +
                    "(netsh http delete urlacl url=… removes it again.)");
            throw new CliDataException($"Could not listen on port {port}: {ex.Message}");
        }

        Log($"listening on {scheme}://127.0.0.1:{port}/" + (vhosts is null ? "" : $" and {scheme}://*.{vhosts.Suffix}:{port}/"));
        foreach (var line in summary) Log(line);
        if (extraPrefix is not null && extraPrefix != $"{scheme}://127.0.0.1:{port}/" && listen is not null)
            Log($"also listening on {extraPrefix} — token required; every caller that reaches it uses your client certificate.");
        if (tls)
        {
            Log(tlsTrust || GatewayCertificate.IsTrusted(gatewayThumb!)
                ? "the gateway certificate is trusted in CurrentUser\\Root, so the browser accepts it silently."
                : "the gateway certificate is self-signed; the browser will warn once. Run with " +
                  "--tls-trust to install it into CurrentUser\\Root (reversible with --tls-untrust).");
        }

        var ct = services.Cancel;
        using var stopReg = ct.Register(() => { try { listener.Stop(); } catch { } });

        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = listener.GetContextAsync().GetAwaiter().GetResult(); }
                catch (Exception) when (ct.IsCancellationRequested) { break; }   // Stop() unblocked us

                var task = HandleAsync(context, gateway, routes, browser, headerRules, relay, policy,
                    replay, recorder, localBase, Log, ct);
                inFlight[task] = 0;
                _ = task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        finally
        {
            // Let requests already accepted finish before we tear down the gateway/listener.
            try { Task.WhenAny(Task.WhenAll(inFlight.Keys), Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult(); }
            catch { }
            try { listener.Close(); } catch { }
            gatewayCert?.Dispose();
            if (cookieJarFile is not null && policy.CookieJar is { } jarToSave)
            {
                try { jarToSave.Save(cookieJarFile, SecretProtection.Default); Log($"saved {jarToSave.Count} cookie(s) to {cookieJarFile} (encrypted)."); }
                catch (Exception ex) { Log("warning: the cookie jar was not written: " + ex.Message); }
            }
            // Written once, at shutdown, after the in-flight requests above have finished appending.
            if (recorder is not null)
            {
                try { recorder.Save(recordPath!); Log($"recorded {recorder.Count} exchange(s) to {recordPath}."); }
                catch (Exception ex) { Log("warning: could not write the recording: " + ex.Message); }
            }
        }

        Log("stopped.");
        return ExitCodes.Ok;
    }

    /// <summary>`serve --tls-untrust`: remove a previously trusted gateway certificate and exit. A
    /// standalone maintenance action — it never generates a certificate or binds a port, so it reads
    /// only the cached one on disk, if any.</summary>
    private static int UntrustGatewayCertificate(TextWriter stderr)
    {
        using var cert = GatewayCertificate.TryLoadCached();
        if (cert is null)
        {
            stderr.WriteLine("no gateway certificate is cached, so there is nothing to untrust.");
            return ExitCodes.Ok;
        }

        string thumb = LoopbackTlsBinding.NormalizeThumbprint(cert.Thumbprint!);
        if (GatewayCertificate.Untrust(thumb))
            stderr.WriteLine($"removed the gateway certificate {thumb} from CurrentUser\\Root.");
        else
            stderr.WriteLine($"the gateway certificate {thumb} was not in CurrentUser\\Root; nothing to remove.");
        // And any earlier one this tool generated and later replaced, still trusted from before.
        foreach (var previous in GatewayCertificate.PreviousThumbprints())
        {
            string p = LoopbackTlsBinding.NormalizeThumbprint(previous);
            if (!p.Equals(thumb, StringComparison.OrdinalIgnoreCase) && GatewayCertificate.Untrust(p))
                stderr.WriteLine($"removed an earlier gateway certificate {p} from CurrentUser\\Root.");
        }
        return ExitCodes.Ok;
    }

    /// <summary>Parses a repeatable "Name: value" flag using the same first-colon rule as the request
    /// commands: a header value is allowed to contain a colon of its own, so only the first one
    /// separates the name from the value.</summary>
    private static List<KeyValuePair<string, string>> ParseHeaderSets(IReadOnlyList<string> raw, string flag)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var entry in raw)
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"{flag} expects \"Name: value\", got '{entry}'.");
            result.Add(new(entry[..colon].Trim(), entry[(colon + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>Parses a repeatable bare-header-name flag: an empty or whitespace-only name is a
    /// usage error rather than a removal rule that silently matches nothing.</summary>
    private static List<string> ParseHeaderRemovals(IReadOnlyList<string> raw, string flag)
    {
        var result = new List<string>();
        foreach (var entry in raw)
        {
            string name = entry.Trim();
            if (name.Length == 0) throw new CliUsageException($"{flag} needs a header name.");
            result.Add(name);
        }
        return result;
    }

    /// <summary>One --upstream mount: <c>name=url</c> (a site called <c>name</c>, mounted at
    /// <c>/name</c> and, under --vhost, at <c>name.gw.localhost</c>) or <c>/path=url</c> (a mount at
    /// a path). The URL must be spelled out in full: a mount point is a routing decision, and
    /// resolving a saved-website name per site would make it ambiguous which half of the spec a
    /// bare word belonged to.</summary>
    internal static GatewayRoute ParseUpstream(string spec)
    {
        // The first '=' only: a URL is allowed to contain more of them, in a query string.
        int eq = spec.IndexOf('=');
        if (eq <= 0)
            throw new CliUsageException(
                $"--upstream expects <name>=<url> or /<path>=<url> (for example api=https://api.internal), got '{spec}'.");

        string prefix = spec[..eq].Trim();
        string url = spec[(eq + 1)..].Trim();
        if (prefix.Length == 0)
            throw new CliUsageException($"--upstream needs a name or a path before the '=', got '{spec}'.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var upstream) ||
            (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps))
            throw new CliUsageException($"--upstream needs an absolute http(s) URL after the '=', got '{spec}'.");

        // Scheme + host + port only, as the positional form is: the forwarded path always replaces
        // whatever path was written here, so keeping one would only misdescribe where traffic goes.
        var authority = new Uri(upstream.GetLeftPart(UriPartial.Authority));
        if (prefix.StartsWith('/')) return new GatewayRoute(prefix, authority);
        if (!GatewayRoute.IsValidName(prefix))
            throw new CliUsageException(
                $"--upstream: a site name is letters, digits and inner hyphens (it is also a host name), got '{prefix}'. Write /{prefix}=… to mount it at a path instead.");
        string name = prefix.ToLowerInvariant();
        return new GatewayRoute("/" + name, authority) { Name = name };
    }

    /// <summary>The sites in a --routes file, each with its certificate and settings resolved up
    /// front: a certificate or file that cannot be found is a startup error naming the site, not a
    /// handshake failure discovered from the browser later.</summary>
    internal static List<GatewayRoute> LoadRoutesFile(string path, string store, CliServices services, TextWriter stderr,
        ExtraRootSet? gatewayRoots = null)
    {
        if (!File.Exists(path)) throw new CliDataException($"--routes: {path} does not exist.");
        IReadOnlyList<GatewayRouteSpec> specs;
        try { specs = GatewayRoutesFile.Parse(File.ReadAllText(path), path, Path.GetDirectoryName(Path.GetFullPath(path))); }
        catch (InvalidDataException ex) { throw new CliDataException(ex.Message); }
        return BuildSites(specs, "--routes", store, services, stderr, gatewayRoots);
    }

    /// <summary>Sites from their written form — a routes file or a profile — into the gateway's
    /// routes: certificate loaded, CA files merged with the gateway's own, Windows credentials and
    /// headers validated. One place, so the two sources can never mean different things.</summary>
    internal static List<GatewayRoute> BuildSites(IReadOnlyList<GatewayRouteSpec> specs, string where, string store,
        CliServices services, TextWriter stderr, ExtraRootSet? gatewayRoots)
    {
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        var routes = new List<GatewayRoute>();
        foreach (var spec in specs)
        {
            string label = $"{where} {spec.Name ?? spec.Prefix}";
            X509Certificate2? cert = null;
            if (spec.CertFile is not null)
            {
                try { cert = CertificateFileLoader.Load(spec.CertFile, spec.CertPassword, spec.KeyFile); }
                catch (CertificateFileException ex) { throw new CliDataException($"{label}: {ex.Message}"); }
                CliCert.NoteMissingIntermediates(spec.CertFile, cert.Thumbprint!, stderr);
            }
            else if (spec.Cert is not null)
            {
                try { cert = CertPicker.Resolve(services.ListCertificates(localMachine), spec.Cert.Length == 0 ? null : spec.Cert, null, null, stderr).Certificate; }
                catch (CliUsageException ex) { throw new CliDataException($"{label}: {ex.Message}"); }
                catch (CliDataException ex) { throw new CliDataException($"{label}: {ex.Message}"); }
            }

            SiteSettings? settings = null;
            if (spec.HasSettings)
            {
                ExtraRootSet? roots = null;
                if (spec.CaFiles.Count > 0)
                {
                    var all = (gatewayRoots?.Sources ?? Array.Empty<string>()).Concat(spec.CaFiles).Distinct().ToList();
                    try { roots = TransportFlags.LoadExtraRoots(all); }
                    catch (CliUsageException ex) { throw new CliDataException($"{label}: {ex.Message}"); }
                }
                if (spec.Headers.Count > 0 &&
                    HeaderRules.TryCreate(spec.Headers, Array.Empty<string>(), Array.Empty<KeyValuePair<string, string>>(), Array.Empty<string>(), out var problem) is null)
                    throw new CliUsageException($"{label}: {problem}");
                settings = new SiteSettings
                {
                    Insecure = spec.Insecure,
                    ExtraRoots = roots,
                    CaFiles = spec.CaFiles,
                    Revocation = spec.Revocation is null ? null : TransportFlags.ParseRevocation(spec.Revocation),
                    WindowsAuth = spec.WindowsUser is not null ? WindowsAuthOptions.FromCredentials(spec.WindowsUser, spec.WindowsPassword)
                                : spec.WindowsAuth == true ? new WindowsAuthOptions(true) : null,
                    WindowsAuthOff = spec.WindowsAuth == false,
                    RequestHeaders = spec.Headers,
                    Sni = spec.Sni,
                };
            }
            routes.Add(new GatewayRoute(spec.Prefix, spec.Upstream) { Certificate = cert, Name = spec.Name, Settings = settings });
        }
        return routes;
    }

    private static string? HeaderValue(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Up to <paramref name="limit"/> bytes, and whether that was the whole stream.</summary>
    private static async Task<(byte[] Bytes, bool Complete)> ReadUpToAsync(Stream source, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length <= limit)
        {
            int read = await source.ReadAsync(chunk, ct);
            if (read == 0) return (buffer.ToArray(), true);
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    /// <summary>The path a URL carries beyond "/", or null — what a site URL would silently lose.</summary>
    private static string? PathIn(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.AbsolutePath.TrimEnd('/').Length > 0
            ? u.AbsolutePath.TrimEnd('/') + "/" : null;

    /// <summary>A site's own headers, set (replace-or-add) after the gateway-wide rules, so the
    /// more specific setting wins.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> WithSiteHeaders(
        IReadOnlyList<KeyValuePair<string, string>> headers, GatewayRoute? site)
    {
        if (site?.Settings is not { RequestHeaders.Count: > 0 } s) return headers;
        var result = new List<KeyValuePair<string, string>>(headers.Count + s.RequestHeaders.Count);
        foreach (var h in headers)
            if (!s.RequestHeaders.Any(set => set.Key.Equals(h.Key, StringComparison.OrdinalIgnoreCase))) result.Add(h);
        result.AddRange(s.RequestHeaders);
        return result;
    }

    private static Uri ResolveUpstream(string value, string? workspace, CliServices services, TextWriter stderr)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return new Uri(abs.GetLeftPart(UriPartial.Authority));   // scheme + host + port only

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var saved = state.SavedBaseUrls.FirstOrDefault(b =>
            b.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            (Uri.TryCreate(b, UriKind.Absolute, out var u) && u.Host.Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (saved is null || !Uri.TryCreate(saved, UriKind.Absolute, out var savedUri))
            throw new CliDataException($"'{value}' is not an absolute URL or a saved website. Use https://… or a saved website name.");
        return new Uri(savedUri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>The origins named by --cors, or null when it was given without a value — which means
    /// "echo whoever asks", the loopback-only default.</summary>
    private static IReadOnlyList<string>? ParseOrigins(string? raw) =>
        raw is null
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Relay a WebSocket upgrade to the matched route's upstream. This path never enters
    /// MtlsGateway.ForwardAsync — HttpClient cannot carry an upgrade — so the plain HTTP relay keeps
    /// its exact semantics.</summary>
    private static async Task RelayUpgradeAsync(
        HttpListenerContext context, MtlsGateway gateway, GatewayWebSocketRelay relay,
        IReadOnlyList<KeyValuePair<string, string>> headers, string pathAndQuery, Action<string> log, CancellationToken ct)
    {
        var req = context.Request;
        string method = req.HttpMethod;

        // The same resolution — and the same off-host refusal — the HTTP relay applies: an upgrade
        // whose target walks off the route's upstream ("//evil.example/x") is refused before any
        // socket is opened, exactly as a request would be.
        GatewayRoute? route;
        Uri? target;
        try { route = gateway.ResolveTarget(pathAndQuery, out target, out _); }
        catch (GatewayTargetException)
        {
            try { context.Response.StatusCode = 400; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 400 (off-host target)");
            return;
        }
        catch (GatewayHostNotAllowedException ex)
        {
            try { context.Response.StatusCode = 403; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 403 ({ex.Message})");
            return;
        }
        if (route is null || target is null)
        {
            // Nothing is contacted and nothing is upgraded: there is no upstream under this path.
            try { context.Response.StatusCode = 404; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 404 (no route)");
            return;
        }

        // Every subprotocol the browser offered, in its order: picking is the upstream's job, and
        // the browser is told the one it picked.
        var subProtocols = (req.Headers["Sec-WebSocket-Protocol"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var upstream = GatewayWebSocketRelay.ToWebSocketUri(target);
        HttpListenerWebSocketContext accepted;
        // Still an ordinary HTTP exchange until the accept succeeds, so a refusal is an ordinary
        // HTTP answer rather than a close frame. The subprotocol the browser is told is decided
        // after the upstream answers in a stricter relay; here the first offered is echoed when
        // any was, which is what every upstream that accepts the first one expects.
        string? acceptedProtocol = subProtocols.Length > 0 ? subProtocols[0] : null;
        try { accepted = await context.AcceptWebSocketAsync(acceptedProtocol); }
        catch (Exception ex)
        {
            try { context.Response.StatusCode = 400; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 400 (upgrade refused: {ex.Message})");
            return;
        }

        try
        {
            await relay.RelayAsync(accepted.WebSocket, upstream, subProtocols, headers, ct);
            log($"{method,-6} {pathAndQuery}  -> 101 (websocket closed)");
        }
        catch (Exception ex)
        {
            // The relay has already closed the browser with 1011 and told it why, so there is
            // nothing left to send: the reason is only worth writing down.
            log($"{method,-6} {pathAndQuery}  -> websocket failed ({ex.Message})");
        }
        // The socket belongs to the context that accepted it, so it is disposed here; the relay
        // never disposes what it was handed.
        finally { accepted.WebSocket.Dispose(); }
    }

    /// <summary>The per-gateway knobs the request handler needs that are not routing, browser or
    /// header-rule concerns.</summary>
    private sealed record ServePolicy(string? Token, bool Forwarded, long? MaxBody, string LocalHost, string Scheme)
    {
        public GatewayCookieJar? CookieJar { get; init; }
        public AppState? TokenState { get; init; }
        public bool RefererFallback { get; init; }
        /// <summary>`--vhost`: Host-header routing beside the path form. Null in path mode.</summary>
        public VirtualHosts? VirtualHosts { get; init; }
        /// <summary>No --listen: a caller from beyond loopback is refused even where the bound
        /// prefix (the wildcard --vhost needs) would have let it in.</summary>
        public bool LoopbackOnly { get; init; }
    }

    private static async Task HandleAsync(
        HttpListenerContext context, MtlsGateway gateway, GatewayRoutes routes,
        BrowserOptions browser, HeaderRules headerRules, GatewayWebSocketRelay? relay, ServePolicy policy,
        HarReplaySource? replay, GatewayRecorder? recorder, string localBase,
        Action<string> log, CancellationToken ct)
    {
        var req = context.Request;
        var res = context.Response;
        string method = req.HttpMethod;
        string pathAndQuery = req.RawUrl ?? "/";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (policy.LoopbackOnly && req.RemoteEndPoint is { } remote && !IPAddress.IsLoopback(remote.Address))
            {
                res.StatusCode = 403;
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> 403 (caller {remote.Address} is not loopback; --listen admits it)");
                return;
            }

            // A request to a virtual host is the same request to its mount: the Host header picks
            // the prefix, and from here on the path-based gateway answers it. What the browser then
            // sees — Location, cookies, Origin — is measured against that host's own origin, with
            // the mount at its root, so nothing it is told carries the prefix.
            string? vhostMount = policy.VirtualHosts?.MountFor(req.Headers["Host"]);
            if (vhostMount is not null)
            {
                pathAndQuery = VirtualHosts.GatewayPath(vhostMount, pathAndQuery);
                var vhostRoute = new GatewayRoute(vhostMount, new Uri("https://unused.invalid"));
                var self = policy.VirtualHosts!;
                var pathFor = browser.GatewayPathFor;
                browser = browser with
                {
                    LocalOrigin = self.OriginFor(vhostRoute),
                    GatewayPathFor = pathFor is null ? null : target => self.UrlFor(target, pathFor)
                };
            }
            // The route a rewrite is told about: under a virtual host the mount is the origin's root.
            GatewayRoute ForBrowser(GatewayRoute r) => vhostMount is null ? r : r with { PathPrefix = "/" };
            // A gateway path from a browser-relative one (a Referer's path, say).
            string FromBrowser(string p) => vhostMount is null ? p : VirtualHosts.GatewayPath(vhostMount, p);

            // A CORS preflight is answered before the token check: a browser cannot attach
            // Authorization or a custom header to a preflight, so checking first would make
            // --token and --cors mutually exclusive. The preflight itself carries nothing and
            // reaches no upstream; the real request that follows it is still token-checked.
            if (browser.Cors && method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                req.Headers["Origin"] is not null && req.Headers["Access-Control-Request-Method"] is not null)
            {
                var earlyHeaders = HeaderList(req);
                var earlyReq = new GatewayRequest(method, pathAndQuery, earlyHeaders, null, req.ContentType);
                if (BrowserRewriter.TryPreflight(earlyReq, browser) is { } early)
                {
                    res.StatusCode = early.StatusCode;
                    foreach (var h in early.Headers) res.Headers.Add(h.Key, h.Value);
                    res.Close();
                    log($"{method,-6} {pathAndQuery}  -> {early.StatusCode} (preflight)");
                    return;
                }
            }

            if (policy.Token is not null && !TokenOk(req, policy.Token))
            {
                res.StatusCode = 401;
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> 401 (token)");
                return;
            }

            // The gateway's own endpoints: never forwarded, never recorded.
            // Read from the path as sent, before a virtual host's mount is put in front of it, so
            // http://api.gw.localhost:8819/_certapi/health answers as the path form does.
            string asSent = req.RawUrl ?? "/";
            if (asSent.StartsWith("/_certapi/", StringComparison.OrdinalIgnoreCase) ||
                asSent.Equals("/_certapi", StringComparison.OrdinalIgnoreCase))
            {
                await AnswerSelfAsync(res, asSent, gateway, routes, policy, ct);
                log($"{method,-6} {pathAndQuery}  -> {res.StatusCode} (gateway)");
                return;
            }

            // A path nothing owns, from a page the gateway served: send the browser to the same
            // path under that page's mount. A redirect rather than a silent forward, so the browser
            // ends up on the canonical gateway path and everything relative to it keeps working.
            if (policy.RefererFallback &&
                gateway.FallbackPathFor(pathAndQuery, req.Headers["Referer"], browser.LocalOrigin) is { } fallback)
            {
                res.StatusCode = 307;
                res.Headers.Add("Location", browser.LocalOrigin.GetLeftPart(UriPartial.Authority) + fallback);
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> 307 {fallback} (referer fallback)");
                return;
            }

            if (policy.MaxBody is { } limit && req.ContentLength64 > limit)
            {
                res.StatusCode = 413;
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> 413 (body {req.ContentLength64} > {limit})");
                return;
            }

            // Replay short-circuits before anything is forwarded: the recorded response is the
            // answer, and no upstream is contacted. A miss is the source's own no-match status.
            if (replay is not null)
            {
                var recorded = replay.Match(method, pathAndQuery, out var matchKind);
                if (recorded is not null)
                {
                    res.StatusCode = recorded.Status;
                    if (recorded.StatusText is { } st) res.StatusDescription = st;
                    foreach (var h in recorded.Headers)
                        try { res.Headers.Add(h.Key, h.Value); } catch (ArgumentException) { /* restricted header HttpListener manages */ }
                    res.ContentLength64 = recorded.Body.Length;
                    await res.OutputStream.WriteAsync(recorded.Body, ct);
                    res.Close();
                    log($"{method,-6} {pathAndQuery}  -> {recorded.Status} (replay {matchKind})  {sw.ElapsedMilliseconds} ms");
                }
                else
                {
                    res.StatusCode = replay.Options.NoMatchStatus;
                    res.Close();
                    log($"{method,-6} {pathAndQuery}  -> {replay.Options.NoMatchStatus} (replay miss)");
                }
                return;
            }

            // Without --allow-upgrade an upgrade request falls through to the plain relay exactly as
            // it always has, so nothing changes for callers who never asked for WebSockets.
            // The caller's headers, less the gateway's own secret: the token authenticates the
            // caller to the gateway and has no business reaching an upstream (or a recording).
            var headers = HeaderList(req);
            if (policy.Token is not null) headers = StripToken(headers, policy.Token);
            if (policy.Forwarded) headers = AddForwarded(headers, req, policy);

            if (relay is not null && browser.AllowUpgrade && req.IsWebSocketRequest)
            {
                var upgradeHeaders = headerRules.ApplyToRequest(headers);
                try
                {
                    if (gateway.ResolveTarget(pathAndQuery, out var wsTarget, out _) is { } wsRoute && wsTarget is not null)
                    {
                        upgradeHeaders = WithSiteHeaders(upgradeHeaders, wsRoute);
                        upgradeHeaders = BrowserRewriter.RewriteRequestHeaders(upgradeHeaders, ForBrowser(wsRoute), browser,
                            p => { try { return gateway.ResolveTarget(FromBrowser(p), out var u, out _) is null ? null : u; } catch { return null; } });
                        if (policy.CookieJar is { } wsJar)
                        {
                            var mutable = new List<KeyValuePair<string, string>>(upgradeHeaders);
                            string? sent = mutable.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
                            if (wsJar.Merge(wsTarget, sent) is { } merged)
                            {
                                mutable.RemoveAll(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
                                mutable.Add(new("Cookie", merged));
                            }
                            upgradeHeaders = mutable;
                        }
                    }
                }
                catch (GatewayTargetException) { } catch (GatewayHostNotAllowedException) { }   // RelayUpgradeAsync answers these
                await RelayUpgradeAsync(context, gateway, relay, upgradeHeaders, pathAndQuery, log, ct);
                return;
            }

            // When recording, the request body is buffered up front so it can be both forwarded and
            // written into the HAR; otherwise it streams straight through as it always has.
            byte[] recordedRequestBody = Array.Empty<byte>();
            Stream? requestBody = req.HasEntityBody ? req.InputStream : null;
            long? declaredLength = req.HasEntityBody && req.ContentLength64 >= 0 ? req.ContentLength64 : null;
            if (requestBody is not null && policy.MaxBody is { } cap)
                requestBody = new LimitedStream(requestBody, cap);
            if (recorder is not null && requestBody is not null)
            {
                using var buffer = new MemoryStream();
                await requestBody.CopyToAsync(buffer, ct);
                recordedRequestBody = buffer.ToArray();
                requestBody = new MemoryStream(recordedRequestBody);
                declaredLength = recordedRequestBody.Length;
            }

            var gwReq = new GatewayRequest(method, pathAndQuery, headers, requestBody, req.ContentType) { ContentLength = declaredLength };

            // A preflight is the browser asking the gateway's own permission, so the gateway answers
            // it. Forwarding it would ask the upstream a question about a policy that is not theirs.
            if (BrowserRewriter.TryPreflight(gwReq, browser) is { } preflight)
            {
                res.StatusCode = preflight.StatusCode;
                foreach (var h in preflight.Headers) res.Headers.Add(h.Key, h.Value);
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> {preflight.StatusCode} (preflight)");
                return;
            }

            // The rules describe what reaches the upstream, so they are applied to the forwarded
            // request and not to the preflight decision above — a preflight is answered here and
            // never forwarded, and a user rule must not be able to change the Origin that decision is
            // made on. With no request rules this hands back the very same header list, so the
            // forwarded request is identical to today's.
            var forwardHeaders = headerRules.ApplyToRequest(headers);
            // Where this request is going, for the per-upstream accommodations below. A path that
            // resolves nowhere is left for ForwardAsync to answer 404/403 exactly as before.
            GatewayRoute? targetRoute = null;
            Uri? targetUri = null;
            try { targetRoute = gateway.ResolveTarget(pathAndQuery, out targetUri, out _); } catch (GatewayTargetException) { } catch (GatewayHostNotAllowedException) { }
            if (targetRoute is not null && targetUri is not null)
            {
                forwardHeaders = WithSiteHeaders(forwardHeaders, targetRoute);
                forwardHeaders = BrowserRewriter.RewriteRequestHeaders(forwardHeaders, ForBrowser(targetRoute), browser,
                    p => { try { return gateway.ResolveTarget(FromBrowser(p), out var u, out _) is null ? null : u; } catch { return null; } });
                if (policy.CookieJar is { } jar)
                {
                    var mutable = new List<KeyValuePair<string, string>>(forwardHeaders);
                    string? browserCookie = string.Join("; ", mutable.Where(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
                    if (browserCookie.Length == 0) browserCookie = null;
                    string? merged = jar.Merge(targetUri, browserCookie);
                    if (merged is not null)
                    {
                        mutable.RemoveAll(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
                        mutable.Add(new("Cookie", merged));
                    }
                    forwardHeaders = mutable;
                }
                if (policy.TokenState is { } tokenState)
                {
                    var mutable = new List<KeyValuePair<string, string>>(forwardHeaders);
                    if (TokenService.AutoAttach(tokenState, targetUri.ToString(), mutable, out _) is not null)
                        forwardHeaders = mutable;
                }
            }
            var forwardReq = gwReq with { Headers = forwardHeaders };

            var gwResp = await gateway.ForwardAsync(forwardReq, ct);
            if (policy.CookieJar is { } storeJar && targetUri is not null)
                storeJar.Store(targetUri, gwResp.Headers.Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
            using (gwResp.Lifetime)
            {
                res.StatusCode = gwResp.StatusCode;
                if (gwResp.ReasonPhrase is { } reason) res.StatusDescription = reason;

                // The forward above refused a path that matches nothing, so this route is the one
                // the response came from. With every accommodation off the rewriter hands back the
                // upstream's headers as they were, which is what keeps the default relay faithful.
                var responseHeaders = gwResp.Headers;
                GatewayRoute? responseRoute = gateway.ResolveTarget(pathAndQuery, out _, out _);
                if (responseRoute is not null)
                {
                    responseHeaders = BrowserRewriter.RewriteResponseHeaders(
                        gwResp.Headers, req.Headers["Origin"], ForBrowser(responseRoute), browser, out var warnings);
                    foreach (var warning in warnings) log("warning: " + warning);
                }

                // After the browser rewrites, so a header the operator configured explicitly wins
                // over one the gateway injected — someone overriding Access-Control-Allow-Origin
                // deliberately gets their value. Applied whether or not any browser accommodation is
                // on: a header rule is not a browser concern.
                responseHeaders = headerRules.ApplyToResponse(responseHeaders);

                // --rewrite-body: a page's own URLs back through the gateway. Read whole (up to the
                // limit) because a URL can straddle any read boundary; a body that cannot be read
                // or names nothing to rewrite goes out byte for byte, headers untouched.
                byte[]? bodyPrefix = null;
                byte[]? rewrittenBody = null;
                if (browser.RewriteBody && responseRoute is not null &&
                    !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) &&
                    gwResp.StatusCode is not (204 or 206 or 304) &&
                    HeaderValue(responseHeaders, "Content-Range") is null &&
                    BodyRewriter.Applies(HeaderValue(responseHeaders, "Content-Type"), HeaderValue(responseHeaders, "Content-Length") is not null))
                {
                    var (buffered, complete) = await ReadUpToAsync(gwResp.Body, BodyRewriter.MaxBytes, ct);
                    bodyPrefix = buffered;
                    if (complete && BodyRewriter.Rewrite(buffered, HeaderValue(responseHeaders, "Content-Encoding"),
                            HeaderValue(responseHeaders, "Content-Type"), ForBrowser(responseRoute), browser) is { } rewritten)
                    {
                        rewrittenBody = rewritten;
                        responseHeaders = responseHeaders
                            .Where(h => !h.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
                                        !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            .Append(new("Content-Length", rewritten.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                            .ToList();
                    }
                }
                async Task CopyBodyAsync(Stream destination)
                {
                    if (rewrittenBody is not null) { await destination.WriteAsync(rewrittenBody, ct); return; }
                    if (bodyPrefix is not null) await destination.WriteAsync(bodyPrefix, ct);
                    await gwResp.Body.CopyToAsync(destination, ct);
                }

                long? contentLength = null;
                foreach (var h in responseHeaders)
                {
                    if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    { if (long.TryParse(h.Value, out var cl)) contentLength = cl; continue; }
                    try { res.Headers.Add(h.Key, h.Value); } catch (ArgumentException) { /* restricted header HttpListener manages itself — skip */ }
                }
                if (contentLength is { } len) res.ContentLength64 = len;
                else res.SendChunked = true;

                if (recorder is null)
                {
                    await CopyBodyAsync(res.OutputStream);
                }
                else
                {
                    // Tee the body: read it whole so the same bytes go to the client and into the
                    // HAR. Only when recording — the default relay still streams without buffering.
                    using var captured = new MemoryStream();
                    await CopyBodyAsync(captured);
                    byte[] body = captured.ToArray();
                    await res.OutputStream.WriteAsync(body, ct);
                    recorder.Record(
                        method, localBase + pathAndQuery, headers, recordedRequestBody, req.ContentType,
                        gwResp.StatusCode, gwResp.ReasonPhrase, responseHeaders, body, sw.Elapsed.TotalMilliseconds);
                }
            }
            res.Close();
            log($"{method,-6} {pathAndQuery}  -> {gwResp.StatusCode}  {sw.ElapsedMilliseconds} ms" + (recorder is not null ? " (recorded)" : ""));
        }
        catch (GatewayRouteNotFoundException ex)
        {
            // Nothing was contacted: there is no upstream mounted under this path to contact.
            try
            {
                res.StatusCode = 404;
                var bytes = System.Text.Encoding.UTF8.GetBytes("404 Not Found: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 404 (no route)");
            return;
        }
        catch (GatewayTargetException ex)
        {
            try
            {
                res.StatusCode = 400;
                var bytes = System.Text.Encoding.UTF8.GetBytes("400 Bad Request: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 400 (off-host target)");
            return;
        }
        catch (GatewayHostNotAllowedException ex)
        {
            try
            {
                res.StatusCode = 403;
                var bytes = System.Text.Encoding.UTF8.GetBytes("403 Forbidden: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 403 (host not allowed)");
            return;
        }
        catch (BodyTooLargeException ex)
        {
            try { res.StatusCode = 413; res.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 413 ({ex.Message})");
            return;
        }
        catch (TimeoutException ex)
        {
            try
            {
                res.StatusCode = 504;
                var bytes = System.Text.Encoding.UTF8.GetBytes("504 Gateway Timeout: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 504 ({ex.Message})");
            return;
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 502;
                var bytes = System.Text.Encoding.UTF8.GetBytes("502 Bad Gateway: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 502 ({ex.Message})");
        }
    }

    /// <summary>The gateway jar, pre-loaded from an encrypted file when one is named. A file this
    /// user cannot read is a data error up front, not a surprise at shutdown.</summary>
    private static GatewayCookieJar LoadGatewayJar(string? file, TextWriter stderr)
    {
        var jar = new GatewayCookieJar();
        if (file is null) return jar;
        if (!SecretProtection.Default.IsAvailable)
            throw new CliDataException("--cookie-jar <file>: the jar is only written encrypted (DPAPI), which this machine cannot do. Use --cookie-jar without a file to keep it in memory.");
        try
        {
            int loaded = jar.Load(file, SecretProtection.Default);
            if (loaded > 0) stderr.WriteLine($"loaded {loaded} cookie(s) from {file}.");
        }
        catch (InvalidDataException ex) { throw new CliDataException(ex.Message); }
        return jar;
    }

    /// <summary>No --listen, or one naming this machine's loopback: only local callers are answered.</summary>
    internal static bool IsLoopbackListen(string? listen) =>
        listen is null || listen.Trim() is "127.0.0.1" or "::1" or "[::1]" or "localhost";

    /// <summary>The HttpListener prefix for --listen: "+" and 0.0.0.0 become the wildcard prefix,
    /// an IPv6 literal gets its brackets, and loopback spellings collapse to the one already added.</summary>
    internal static string? ListenPrefix(string? listen, string scheme, int port)
    {
        if (listen is null) return null;
        return listen switch
        {
            "+" or "*" or "0.0.0.0" or "::" => $"{scheme}://+:{port}/",
            "localhost" or "127.0.0.1" => $"{scheme}://127.0.0.1:{port}/",
            "::1" => $"{scheme}://[::1]:{port}/",
            _ when listen.Contains(':') => $"{scheme}://[{listen}]:{port}/",
            _ => $"{scheme}://{listen}:{port}/"
        };
    }

    /// <summary>Constant-time on the token itself: a loopback timing side channel is a small thing,
    /// and so is the cost of not having one.</summary>
    private static bool TokenOk(HttpListenerRequest req, string token)
    {
        string? auth = req.Headers["Authorization"];
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.Ordinal) && FixedTimeEquals(auth[7..], token)) return true;
        string? custom = req.Headers["X-Certapi-Token"];
        return custom is not null && FixedTimeEquals(custom, token);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static List<KeyValuePair<string, string>> HeaderList(HttpListenerRequest req)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (string? key in req.Headers.AllKeys)
            if (key is not null)
                foreach (var v in req.Headers.GetValues(key) ?? Array.Empty<string>())
                    headers.Add(new(key, v));
        return headers;
    }

    /// <summary>The gateway's own credential never travels upstream. Only the matching header goes:
    /// an app's own Authorization (when the token arrived as X-Certapi-Token) is left alone.</summary>
    private static List<KeyValuePair<string, string>> StripToken(List<KeyValuePair<string, string>> headers, string token)
    {
        var kept = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var h in headers)
        {
            if (h.Key.Equals("X-Certapi-Token", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                h.Value.StartsWith("Bearer ", StringComparison.Ordinal) && FixedTimeEquals(h.Value[7..], token)) continue;
            kept.Add(h);
        }
        return kept;
    }

    /// <summary>X-Forwarded-For/Proto/Host plus Via, appended to any the caller already sent so a
    /// chain of proxies stays a chain.</summary>
    private static List<KeyValuePair<string, string>> AddForwarded(
        List<KeyValuePair<string, string>> headers, HttpListenerRequest req, ServePolicy policy)
    {
        var result = new List<KeyValuePair<string, string>>(headers);
        string caller = req.RemoteEndPoint?.Address.ToString() ?? "127.0.0.1";
        Append(result, "X-Forwarded-For", caller, ", ");
        if (!result.Any(h => h.Key.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase)))
            result.Add(new("X-Forwarded-Proto", policy.Scheme));
        if (!result.Any(h => h.Key.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase)))
            result.Add(new("X-Forwarded-Host", req.Headers["Host"] ?? policy.LocalHost));
        Append(result, "Via", "1.1 certapi", ", ");
        return result;

        static void Append(List<KeyValuePair<string, string>> list, string name, string value, string separator)
        {
            int i = list.FindIndex(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) list.Add(new(name, value));
            else list[i] = new(list[i].Key, list[i].Value + separator + value);
        }
    }

    /// <summary>/_certapi/health and /_certapi/routes — the gateway describing itself. JSON, small,
    /// and never a forward.</summary>
    private static async Task AnswerSelfAsync(HttpListenerResponse res, string pathAndQuery,
        MtlsGateway gateway, GatewayRoutes routes, ServePolicy policy, CancellationToken ct)
    {
        string path = pathAndQuery.Split('?')[0].TrimEnd('/').ToLowerInvariant();
        object body;
        switch (path)
        {
            case "/_certapi/health":
            case "/_certapi":
                body = new { status = "ok", version = CliApp.ProductVersion, routes = routes.All.Count, dynamic = gateway.DynamicAllowlist is not null };
                break;
            case "/_certapi/routes":
                body = new
                {
                    routes = routes.All.Select(r => new
                    {
                        name = r.DisplayName, prefix = r.PathPrefix, upstream = r.Upstream.ToString(),
                        cert = (r.Certificate ?? gateway.CertificateFor(r.Upstream))?.Subject,
                        settings = r.Settings?.Describe().ToArray() ?? Array.Empty<string>()
                    }),
                    allow = gateway.DynamicAllowlist?.Entries,
                    dynamicPrefix = gateway.DynamicAllowlist is null ? null : MtlsGateway.DynamicPrefix + "<scheme>://<host>/",
                    forwarded = policy.Forwarded,
                    maxBody = policy.MaxBody,
                    cookieJar = policy.CookieJar is null ? null : (int?)policy.CookieJar.Count,
                    autoToken = policy.TokenState is not null
                };
                break;
            default:
                res.StatusCode = 404;
                res.Close();
                return;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    /// <summary>k/m/g suffixes, case-insensitive; a bare number is bytes.</summary>
    internal static long? ParseSize(string raw)
    {
        string s = raw.Trim().ToLowerInvariant();
        long mult = 1;
        if (s.EndsWith('k')) { mult = 1024; s = s[..^1]; }
        else if (s.EndsWith('m')) { mult = 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1]; }
        // A size too large to count is refused rather than wrapped into a negative limit that
        // would answer every request 413.
        return long.TryParse(s, out var n) && n > 0 && n <= long.MaxValue / mult ? n * mult : null;
    }

    private sealed class BodyTooLargeException(long limit) : Exception($"request body exceeds --max-body {limit}");

    /// <summary>Counts what passes through and stops at the cap — for chunked bodies, which declare
    /// no length up front.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if ((_read += n) > limit) throw new BodyTooLargeException(limit);
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(buffer, cancellationToken);
            if ((_read += n) > limit) throw new BodyTooLargeException(limit);
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
