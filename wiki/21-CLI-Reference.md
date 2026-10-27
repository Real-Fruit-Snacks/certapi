# 21. CLI Reference (`certapi`)

Every command, every option, every default.

`certapi.exe` is a separate download from the releases page. It shares its engine, its workspace,
and its saved requests with the desktop application, so anything you do in one is visible in the
other.

> **The tool's own help is authoritative.** `certapi <command> --help` (or `certapi help <command>`) prints the same options
> straight from the binary you are running. This page explains what they are *for*, what they
> default to, and how they interact — the parts a terse help screen cannot carry.

## How to read this page

- **`<angle brackets>`** are values you supply; **`[square brackets]`** are optional.
- **Repeatable** options may be given more than once and accumulate.
- Every option lists its **default** when it has one. "Off" means the behaviour is absent unless
  you ask for it.
- Options in [Shared option blocks](#shared-option-blocks) are documented once. Each command's
  section names which blocks it accepts, rather than repeating sixty lines twenty times.
- A **configuration profile** can supply a default for most options, so they need not be typed at
  all — see [Configuration](27-Configuration.md). The rule everywhere is the same: **an explicitly
  typed flag beats the profile, and the profile beats the built-in default.**

## Commands

| Command | Purpose |
|---|---|
| [`send <url>`](#send) | Send a one-off request |
| [`token`](#token) | Fetch an OAuth 2.0 access token (and optionally save it) |
| [`run <path>`](#run) | Run saved requests as a pass/fail suite, or a chain |
| [`fuzz <base-url>`](#fuzz) | Discover endpoints from a wordlist |
| [`bench <url>`](#bench) | Measure an endpoint's latency under load |
| [`sse <url>`](#sse) | Stream Server-Sent Events |
| [`ws <url>`](#ws) | Open a WebSocket, send messages, print what arrives |
| [`certs`](#certs) | List client certificates |
| [`selftest`](#selftest) | Prove the mutual-TLS path end-to-end against a loopback server |
| [`mock`](#mock) | Run a local test server to fire requests at |
| [`import`](#import) | Import cURL, OpenAPI, HAR, Postman, Insomnia, or WSDL |
| [`export`](#export) | Export as OpenAPI, a workspace, or markdown notes |
| [`trust`](#trust) | Pin server-certificate thumbprints per host |
| [`serve <upstream>`](#serve) | Run a local mutual-TLS gateway that forwards to an upstream |
| [`grpc`](#grpc) | Discover and call a gRPC service |
| [`mcp`](#mcp) | Run a Model Context Protocol server for AI agents |
| [`doctor <url>`](#doctor) | Diagnose a connection stage by stage |
| [`proxy [<url>]`](#proxy) | Show proxy settings, and which proxy a URL gets |
| [`connections <url>`](#connections) | Are connections actually being reused? |
| [`config`](#config) | Show the configuration file and profile in effect |
| [`completion`](#completion) | Tab completion for PowerShell |
| [`update`](#update) | Replace this certapi (and the desktop app) with the latest release |
| `help [command]` | Show help for a command |

## Global options

These work on **every** command, before or after its own options.

| Option | Default | What it does |
|---|---|---|
| `--debug` | off | Verbose diagnostics on stderr: what was resolved, which certificate was chosen, which proxy, what was negotiated. The first thing to reach for when a command does something you did not expect |
| `--log-file <path>` | none | Also append every diagnostic and all stderr output to this file; the screen still shows them |
| `--config <file>` | discovered | Use this configuration file instead of the discovered one — see [Configuration](27-Configuration.md) |
| `--profile <name>` | file's default | Use this named profile from the configuration file |
| `--no-config` | off | Ignore configuration files entirely. Every command then behaves exactly as it did before profiles existed, which is what makes it the right first step when a command behaves oddly. Not with `--config` or `--profile` |
| `--trace` | off | Report what the network stack itself did — DNS, TCP, TLS handshake, connection reuse, request lifecycle. Redacted by default; see [Watching the network stack](#watching-the-network-stack) |
| `--trace-verbose` / `--trace-file <path>` / `--trace-filter <s>` | off | More detail, a file instead of stderr, or only lines containing these substrings (repeatable). Each one turns `--trace` on by itself |
| `--trace-include-secrets` | off | Keep credential values in the trace instead of redacting them |

Global options take the `--name=value` form too (`--profile=corp`), a single-value one given twice is
a usage error, and nothing after `--` is read as one. `certapi --version` (or `-v`) prints the
version.


## Repeating an option

An option whose value is a list may be given more than once, and the occurrences add up:
`--noproxy a.corp --noproxy .lab` is `--noproxy a.corp,.lab`. That holds for `-H`, `-F`, `--var`,
`--param`, `--assert`, `--capture`, `--ca-file`, `--resolve`, `--noproxy`, `--retry-on`,
`--trace-filter`, `--diff-ignore`, `--diff-ignore-header`; `-d` on `grpc` (one message each);
`--for` on `token`; `-m`/`--message` on `ws`; `--allow` on `mcp`; and on `serve` for `--upstream`,
`--routes`, `--allow`, `--cors <origins>` and the four header-rule options.

Every other option takes one value, and giving it twice is a usage error rather than a guess at
which one you meant: `--cert a --cert b` is refused, not resolved to `b`. The global options follow
the same rule.

## Turning a profile's switch off

A switch a [configuration profile](27-Configuration.md) turns on is turned off for one run with its
`--no-` form: `--no-insecure` and `--no-revocation-strict` on every command that has the switch, and
on `serve` `--no-browser`, `--no-vhost`, `--no-tls`, `--no-rewrite-body`, `--no-forwarded`, `--no-auto-token`,
`--no-windows-auth`, `--no-cors` and `--no-cookie-jar`. Typing a switch and its `--no-` form together
is a usage error.

## Help, typos and curl habits

`certapi <command> --help` (or `-h`) prints that command's options; `certapi help <command>` is the
same. Help is printed before any configuration is read, so it works while a configuration file is
broken.

A mistyped option names the closest one the command has: `--insecur` gets
"Did you mean '--insecure'?". Single-letter options are case-sensitive (`-H` is a header, `-h` is
help); long ones are not.

curl options with the same meaning are accepted wherever the option they stand for is:

| curl | certapi |
|---|---|
| `-k` | `--insecure` |
| `-i` | `--include` |
| `-I` / `--head` | `--head` on `send`: HEAD, headers printed |
| `-s` / `--silent` | `-q, --quiet` |
| `-u` / `--user` | `--basic` |
| `-x` | `--proxy` |
| `-U` | `--proxy-user` |
| `-A` / `--user-agent` | `--user-agent` on `send` |
| `-f` | `--fail` |
| `--max-time` | `--timeout` |
| `--cacert` | `--ca-file` |
| `--key` | `--key-file` |
| `-d @file` | on `send` and `bench`, reads the body from the file — byte for byte when it is not text (on `grpc`, one JSON message) |

The ones whose meaning differs are refused with what to use instead: `-L` and `--compressed` (both
already the default), `-b`/`-c`/`--cookie` (`--cookie-jar`), `-G`/`--get`/`--data-urlencode`
(`--query`), `-v`/`--verbose` (`--include`, `--debug`, `--trace`), `-O` (`-o <file>`), `-e`/`--referer`
(`-H "Referer: …"`), `-w`/`--write-out` (`--timing` or `--json`), `--data-binary` (`-d @file` or `--data-file`, which send a binary file byte for byte), `-E` (`--cert-file`), `--tlsv1.2`/`--tlsv1.3` (`--tls-min`), `-T` (`--data-file`
with `-X PUT`) and `--data-raw` (`-d`).

## Exit codes

Script against these; they are stable.

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | The thing you asked about failed — a request failed, an assertion failed, a suite had a failing request, a diff differed under `--diff-fail`, a doctor stage broke |
| `2` | Usage error — an unknown option, a missing value, a combination that cannot work. Nothing was sent |
| `3` | Data error — a file that does not exist, a workspace that will not parse, a named request or chain that is not there. Nothing was sent |

The distinction is deliberate: **1 means the command worked and the answer was no**; 2 and 3 mean
the command never ran. A build script usually wants to treat those differently.

---

## Shared option blocks

### Certificate options

Accepted by [`send`](#send), [`run`](#run), [`fuzz`](#fuzz), [`bench`](#bench), [`sse`](#sse),
[`ws`](#ws), [`grpc`](#grpc), [`mcp`](#mcp), [`serve`](#serve), [`token`](#token),
[`doctor`](#doctor), [`connections`](#connections) and [`trust`](#trust).

| Option | Default | What it does |
|---|---|---|
| `--cert <thumbprint or subject>` | none | Client certificate from the Windows certificate store. Matches a full thumbprint, or part of the subject (`"CN=My Client"`, or just `my client`) |
| `--store <location>` | `CurrentUser` | Which store to search. `LocalMachine` searches **both** the machine store and your own, because a machine certificate is normally used alongside personal ones |
| `--cert-file <path>` | none | Load the certificate from a file instead of the store: `.pfx`/`.p12`, or `.pem`/`.crt`. Mutually exclusive with `--cert` |
| `--cert-password <password>` | none | Password for a `.pfx`/`.p12`. Not used for PEM |
| `--key-file <path>` | none | The private key for a PEM certificate whose key is in a separate file |
| `--cert-issuer <name>` | none | Pick a store certificate by who issued it (a part of the issuer name, case-insensitive). Alone, or with `--cert` to narrow a subject two certificates share. Not with `--cert-file` |
| `--cert-serial <hex>` | none | Pick a store certificate by its serial number (colons and spaces are fine). Alone or with `--cert`. Not with `--cert-file` |
| `--cert-pin <pin>` | `CERTAPI_CERT_PIN` | Unlock a smart-card or HSM key ahead of the provider's prompt, so a headless job (CI, a scheduled task) is not stopped by a PIN dialog. CNG key-storage providers on Windows; the PIN is never stored or logged. A provider that refuses the property is noted and may prompt instead |
| `--insecure` | off | Accept any server certificate: expired, self-signed, wrong hostname. **This turns off the check that makes TLS meaningful** — prefer [`trust add`](#trust), which pins one host and leaves every other host verified |

**No certificate is a valid choice.** Every command here works without one; `certapi` is a general
API client that happens to be very good at mutual TLS. See
[Certificates & mTLS](06-Certificates-and-mTLS.md).

**Where the private key stays.** With `--cert` the key never leaves the Windows store — Windows
performs the handshake signature itself. That is why smart cards, hardware tokens and
non-exportable keys work.

A certificate that expires within fourteen days produces a warning naming the date, on every
command that resolves one. Fourteen days is enough notice to get a corporate renewal through a
ticket queue, which is the process the notice exists to start.

### Transport options

How a request reaches the server. Accepted by [`send`](#send), [`run`](#run), [`fuzz`](#fuzz),
[`bench`](#bench), [`mcp`](#mcp), [`serve`](#serve) and [`connections`](#connections).
[`sse`](#sse), [`ws`](#ws), [`token`](#token) and [`doctor`](#doctor) take the
[streaming subset](#transport-options-streaming-subset) instead, and [`grpc`](#grpc) takes only
`--proxy`, `--no-proxy`, `--proxy-user`, `--noproxy`, `--revocation` and `--revocation-strict`.
On `serve`, the redirect, decompression, HTTP-version and retry options are accepted but do not
apply; `--sni` applies to every site's handshake (a site's own `"sni"` wins).

#### Proxy

| Option | Default | What it does |
|---|---|---|
| `--proxy <url>` | system/PAC | Route through this proxy: `http://`, `https://`, `socks4://`, `socks4a://` or `socks5://`. **An SSH jump host works here** — `ssh -D 1080 user@jump`, then `--proxy socks5://127.0.0.1:1080` reaches whatever the jump host can reach with mutual TLS intact, because SOCKS relays bytes and never terminates TLS |
| `--no-proxy` | off | Ignore the system and PAC proxy and connect directly. **Also restores the full TLS diagnostics** — see the note below |
| `--proxy-user <user:password>` | none | Credentials for a proxy that authenticates. Credentials written into the `--proxy` URL (`http://user:pass@proxy:8080`) are used the same way, and the URL is never logged with them |
| `--noproxy <list>` | `NO_PROXY` | Hosts that bypass the proxy, comma-separated, `NO_PROXY`-style: `internal.corp`, `.corp`, `*.corp`, `10.0.0.0/8`, or `*` for everything; append `:port` to pin one port. This **narrows** `--proxy` or the system proxy, so it is not valid with `--no-proxy`, which already bypasses everything. Defaults to the `NO_PROXY` environment variable, which an explicit list overrides |

**Why a proxy changes what you can see.** Through a proxy the HTTP handler owns the tunnel's TLS, so
the negotiated protocol, the cipher, and whether your client certificate was actually presented are
not observable — the report says so rather than guessing. `--no-proxy` brings them back, and the
same applies to [`--wire`](#send), which needs the direct path.

#### Redirects

| Option | Default | What it does |
|---|---|---|
| `--no-redirect` | off | Do not follow 3xx responses; report the redirect itself |
| `--max-redirs <n>` | `20` | How many hops to follow before giving up |
| `--show-redirects` | off | Print the hop chain to stderr, each hop with its own status and timing. A request that "takes two seconds" is often four hops of five hundred milliseconds, and the destination is innocent |

#### Protocol

| Option | Default | What it does |
|---|---|---|
| `--http1.1` | negotiated | Pin the request to HTTP/1.1 |
| `--http2` | negotiated | Pin the request to HTTP/2. Required by [`--frames`](#send) |
| `--http3` | negotiated | Pin the request to HTTP/3 (QUIC, over UDP). Needs Windows 11 or Server 2022 or later. **Cannot** be combined with a proxy or `--resolve`, and the command refuses the combination rather than silently ignoring one of them |
| `--no-decompress` | off | Relay compressed bytes exactly as received instead of decoding `gzip`/`deflate`/`br`. What you want when the *encoding* is the thing under test |
| `--resolve <host:port:ip>` | none | Pin a hostname to an address — test one node behind a load balancer, or an endpoint whose DNS is not updated yet. Repeatable. Not valid with a proxy, which does its own resolving |

Each version pin is **exact**: a server that cannot speak it fails loudly rather than quietly
downgrading, because a silent downgrade would make the flag useless for testing the thing it names.

#### Revocation

| Option | Default | What it does |
|---|---|---|
| `--revocation <mode>` | `none` | Whether to check that the server's certificate has not been revoked by its issuer. `none` — no checking. `offline` — cached certificate revocation lists only. `online` — may fetch a fresh list or query an OCSP (Online Certificate Status Protocol) responder |
| `--revocation-strict` | off | Make an *undeterminable* status fatal. Needs `--revocation offline` or `online` |

**Why undeterminable is not fatal by default.** On a corporate network the revocation endpoint is
very often blocked, so "status unknown" is the ordinary case rather than a signal. It is reported
either way. `--insecure` overrides both, and says so when it does.

#### Trust and TLS

| Option | Default | What it does |
|---|---|---|
| `--ca-file <pem>` | none | Trust the root certificate(s) in this file for this run, on top of the Windows store. Repeatable; a PEM bundle with several certificates is fine; DER is accepted. For the network whose TLS-intercepting proxy re-signs every site with a private CA that is handed out as a file: narrower than `--insecure`, which trusts anything. A chain ending in one of these roots is accepted the way a store-trusted chain is; a name mismatch and an expired certificate are still refused, and a revoked one is when `--revocation` is on. Profile key `caFile` (a path or a list, relative to the config file) |
| `--tls-min <1.2\|1.3>` | OS default | Refuse to negotiate below this TLS version. `1.3` proves a server — or a middlebox in front of it — really speaks it. Profile key `tlsMin` |
| `--connect-timeout <s>` | none | A separate budget for the TCP connect and the proxy tunnel. On a network that blackholes unknown hosts this turns a 100-second hang into "unreachable" in seconds. Profile key `connectTimeout` |
| `--sni <name>` | the URL's host | Send a different server name in the TLS handshake. Direct connections only — a proxy tunnel's TLS is negotiated by the HTTP stack from the URL, so `--sni` with any proxy that applies (explicit, PAC, system or environment) is refused. The server's certificate is then checked against the name sent, so a certificate that names only the URL's host needs a pin or `--insecure` |
| `--pac <url\|file>` | the machine's | Decide the proxy for each URL with this PAC script, through WinHTTP's own engine, when the machine's configured PAC is not the one the job needs. Windows only |
| `--no-env-proxy` | off | Ignore `HTTP_PROXY`, `HTTPS_PROXY` and `NO_PROXY` from the environment and use the machine's proxy settings — for the shell whose stale variables point at a proxy that is no longer there |
| `--offline` | off | Touch nothing on the network but the request itself: no revocation fetch, no AIA chase, no captive-portal probe, no PAC download. For the air-gapped network, and for proving a request needs nothing it should not |
| `-4` / `-6` (`--ipv4` / `--ipv6`) | both | Dial only IPv4 or only IPv6 on direct connections, for the host whose AAAA record resolves but whose IPv6 route does not |

**Not supported: a proxy that requires a client certificate.** `--proxy https://…` reaches the proxy
in plaintext and a proxy that demands mutual TLS of its own cannot be answered; .NET's HTTP stack
has no way to present a certificate to a proxy. Use a SOCKS tunnel (`ssh -D`) or a proxy that
authenticates with credentials instead.

**Which proxy applies, and in what order.** The `HTTPS_PROXY`/`HTTP_PROXY` and `NO_PROXY`
environment variables are read first (that is .NET's rule), then the system proxy from Internet
Options (static, WPAD or PAC). `--proxy` replaces both; `--no-proxy` ignores both; `--noproxy`
narrows whichever is in play. `certapi proxy <url>` shows the answer of each engine.

#### Retries

| Option | Default | What it does |
|---|---|---|
| `--retry <n>` | `0` | Retry a failed request up to *n* times |
| `--retry-on <codes>` | `429,502,503,504` | Comma-separated statuses that earn a retry |
| `--retry-delay <ms>` | `500` | The first backoff delay. It doubles each attempt with ±10% jitter, capped at 30 seconds. A `Retry-After` header from the server overrides the calculation entirely |
| `--retry-unsafe` | off | Also retry `POST` and `PATCH`. By default only `GET`/`HEAD`/`OPTIONS`/`PUT`/`DELETE` are retried, **because re-sending a POST nobody confirmed can charge a card twice** |
| `--no-retry-transport` | off | Do not retry connection failures and timeouts, which are otherwise retried whenever `--retry` is on |

### Transport options (streaming subset)

[`sse`](#sse), [`ws`](#ws), [`token`](#token), [`doctor`](#doctor) and `trust add --from-url` accept
**only** the proxy, revocation and trust options: `--proxy`, `--no-proxy`, `--proxy-user`,
`--noproxy`, `--no-env-proxy`, `--offline`, `--revocation`, `--revocation-strict`, `--ca-file`,
`--tls-min`, `--connect-timeout`.

Redirects, version pins and retries are deliberately **not** accepted there, and passing one is a
usage error rather than a silent no-op: a stream re-subscribe has side effects a retry must not
hide, and quietly accepting a flag a command ignores is a worse failure than refusing it.

### Variable options

Accepted by [`send`](#send), [`run`](#run), [`fuzz`](#fuzz) and [`bench`](#bench). See
[Environments & Variables](09-Environments-and-Variables.md).

| Option | Default | What it does |
|---|---|---|
| `--env <name>` | active | The environment whose `{{variable}}` values apply |
| `--var k=v` | none | Override or add one variable. Repeatable, and beats the environment |
| `--workspace <file>` | live state | Read environments and saved requests from a workspace file instead of the app's live state. Lets a machine that has never opened the app run someone else's suite |
| `--strict-vars` | off | An unresolved `{{token}}` becomes an error instead of a warning. What CI wants: a typo should fail the build, not silently send the literal text `{{token}}` |

`{{env:NAME}}` reads from the **process environment** rather than the workspace, so a credential in
a CI job is never written to disk, an export, or source control.

---

## send

```
certapi send <url> [options]
```

One request. The body goes to stdout, everything else to stderr, so `certapi send … | jq` works.

Accepts the [certificate](#certificate-options), [transport](#transport-options) and
[variable](#variable-options) blocks.

### Request

| Option | Default | What it does |
|---|---|---|
| `-X, --method <m>` | `GET` | HTTP method. Any method the server accepts, not a fixed list |
| `-I, --head` | off | Send `HEAD` and print the status line and headers. Not with another `-X` |
| `-H, --header "k: v"` | none | Add a header. Repeatable |
| `-A, --user-agent <ua>` | .NET's | Set the `User-Agent` header |
| `--query name=value` | none | Add a query parameter to the URL, percent-encoded, after any query the URL already has. Repeatable; `name` alone sends `name=` |
| `-d, --data <body>` | none | Request body. Implies POST unless `-X` says otherwise. As in curl, `-d @file` reads the body from a file |
| `--data-file <path>` | none | Read the body from a file instead, without interpreting a leading `@` — for a body too large or too awkward to quote on a command line |
| `-F, --form name=value` | none | A `multipart/form-data` field. `name=@path` uploads a file; `name=@path;type=<ct>` sets that part's content type. Repeatable, implies POST, and mutually exclusive with `-d` |
| `--graphql <query>` | none | Send a GraphQL query as a correctly formed `{query, variables}` POST |
| `--gql-variables <json>` | none | A JSON object of GraphQL variables to go with it |
| `--content-type <ct>` | `application/json` (`application/octet-stream` for a binary body file) | The body's content type. Ignored for `-F`, which sets its own. A `-H "Content-Type: …"` sets it too, as in curl; `--content-type` wins |
| `--timeout <seconds>` | `100` | How long to wait for the whole exchange |

A body file that is text — UTF-8, or UTF-16/32 with a byte-order mark — is sent as text, with `{{variables}}` resolved and `application/json` as the default content type. Any other file — an image, an archive, a signed payload — is sent **byte for byte**, with `application/octet-stream` unless `--content-type` says otherwise, and nothing in it is changed. `--har` records such a body base64-encoded (`"encoding": "base64"` on `postData`), and so does `serve --record`.

### Authentication

| Option | Default | What it does |
|---|---|---|
| `--bearer <token>` | none | Send `Authorization: Bearer <token>` |
| `-u, --basic <user:password>` | none | Send `Authorization: Basic …`, encoded for you |
| `--windows-auth` | off | Windows Integrated Authentication (Negotiate/NTLM) as your signed-in account. Aliases: `--ntlm`, `--negotiate` |
| `--windows-user <DOMAIN\user>` | signed-in | Use explicit Windows credentials instead of single sign-on |
| `--windows-password <password>` | none | The password to go with `--windows-user` |
| `--no-auto-token` | off | Turn off automatic bearer-token capture and reuse, and captured-cookie attachment, for this send |

**Automatic tokens.** A bearer token found in a response — `access_token`, `id_token`, `token`,
`accessToken`, `jwt`, or an `X-Auth-Token`/`X-Access-Token` header — is captured and scoped to that
host. Later sends to the same host attach it automatically, **unless** you pass explicit auth, which
always wins. Captured tokens are encrypted at rest for your Windows user.

### Addresses

| Option | Default | What it does |
|---|---|---|
| `--all-ips` | off | Send once per address the host resolves to and compare the results, one row each. The answer to "one of the load balancer's nodes is broken and I don't know which". Not combinable with `--json`, `-o`, `--capture`, `--assert` or `--diff`, each of which describes a single response |

### Capturing

| Option | Default | What it does |
|---|---|---|
| `--capture var=path` | none | Save a value out of the response into an environment variable. `path` is a JSON body path (`access_token`, `data.token`) or `header:Name` for a response header. Repeatable. See [Capturing Values](12-Capturing-Values.md) |
| `--cookie-jar <file>` | none | Load cookies from this file before the send and save every cookie the response (and each redirect hop) set back into it — a scripted multi-step login carries across invocations, like curl's `-b`/`-c` in one flag. Plain JSON; created on first use; session cookies kept, expired ones dropped |

### Testing

| Option | Default | What it does |
|---|---|---|
| `--assert "<expr>"` | none | Check the response and exit 1 if it fails. Repeatable |

Assertion syntax:

```
status == 200          status < 300           time < 500
header <name> contains <value>                body <jsonpath> exists
body-text matches <regex>
```

Operators: `==` `!=` `contains` `matches` `exists` `!exists` `<` `>`.

### Diff

| Option | Default | What it does |
|---|---|---|
| `--diff <baseline>` | none | Compare the response against a baseline and print what changed. The baseline is a `.har` file, a `.json` response file (the envelope `--json` writes), or the literal `known-good` — the stored response of the matching saved request |
| `--diff-fail` | off | Exit 1 when anything differs. The CI form |
| `--diff-ignore <path>` | none | A JSON path to ignore, e.g. `data.timestamp`. Repeatable; a trailing `*` on a segment matches by prefix |
| `--diff-ignore-header <name>` | none | One more header to ignore, on top of the volatile defaults. Repeatable |

Volatile headers are ignored by default: `Date`, `Set-Cookie`, `ETag`, `Age`, `X-Request-Id`,
`X-Correlation-Id`, `Server-Timing`. When both sides are JSON the diff is **structural** and names
each changed path; otherwise it falls back to a one-line text summary, and for binary it reports
size and equality only.

### Output

| Option | Default | What it does |
|---|---|---|
| `-o, --output <file>` | stdout | Write the body to a file |
| `-i, --include` | off | Print the status line and headers before the body |
| `--pretty` | off | Pretty-print the body: JSON and XML reformatted, binary as hex |
| `--json` | off | Print a JSON result envelope — status, headers, timing, body — instead of the raw body |
| `-f, --fail` | off | Exit 1 when the HTTP status is 400 or higher |
| `-q, -s, --quiet` | off | No metadata line on stderr |
| `--timing` | off | Where the time went, on stderr: `timing: dns 2.1 ms · connect 1.4 ms · tls 14 ms · wait 31 ms · first byte 52 ms · download 1.0 ms · total 54 ms`. A phase that did not happen is left out (`tls` on plain HTTP, `dns` for an IP address, all three on a reused connection, which says `connection reused`). Through a proxy, `connect` is to the proxy and `tls` is the handshake with the site inside the tunnel. After redirects or retries the phases describe the last request. With `--json` the same numbers are a `timing` object in the envelope (`dnsMs`, `connectMs`, `tlsMs`, `waitMs`, `firstByteMs`, `downloadMs`, `totalMs`, `reused`, `requests`). Printed even with `--quiet`. Not with `--all-ips` |

### Capturing the exchange

> **What an archive redacts, and what it cannot.** Credential *headers*, credential-looking query
> parameters, and a password in a URL's `user:password@` prefix are all redacted by default, so an
> archive is safe to attach to a ticket. **Response bodies are stored verbatim**, because they are
> the thing a replay serves and a diff compares — so a server that echoes your request back, or a
> login endpoint that returns a token, puts that value in the archive. Redacting bodies would make
> `mock --har`, `serve --replay` and `run --diff-har` useless, so the honest answer is that this is
> the one place to look before sharing a capture of an authentication flow.

| Option | Default | What it does |
|---|---|---|
| `--har <file>` | none | Write the request and response — and every redirect hop — as an HTTP Archive file when the command finishes |
| `--har-include-secrets` | off | Keep the credentials in that archive, which are redacted by default: `Authorization`, `Proxy-Authorization`, `Cookie` and `Set-Cookie` header values, credential-looking query parameters, and a `user:password@` prefix in any recorded URL. See the note below on what redaction does **not** cover |
| `--wire` | off | Print the **plaintext bytes** of the exchange: the request exactly as it was framed and the response exactly as it arrived, with hex and ASCII side by side for anything that is not text |
| `--wire-file <path>` | stdout | Write that transcript to a file instead |
| `--wire-include-secrets` | off | Keep credential header values in it. The header *name* is kept either way |
| `--frames` | off | Read the same capture as HTTP/2 frames instead of bytes — type, stream, flags, SETTINGS values, window updates, and `GOAWAY`'s error code and debug data. Implies `--wire`, and needs `--http2` |

**`--wire` and `--frames` are direct-connection only.** Through a proxy, or on HTTP/3, the TLS
belongs to the HTTP handler and there is no plaintext stream to read — the command says so in one
line and sends the request normally rather than printing nothing. This is also the one thing a
packet capture cannot give you on an encrypted connection without its keys, and it needs no driver
and no administrator rights, because the tool is one end of the connection. See
[Troubleshooting](23-Troubleshooting.md#seeing-the-actual-bytes---wire).

### Watching the network stack

`--trace` and its options work on **every** command, not only `send`:

| Option | Default | What it does |
|---|---|---|
| `--trace` | off | Report .NET's own networking events as they happen: DNS resolution, TCP connect, TLS handshake, connection established, request lifecycle |
| `--trace-filter <substrings>` | none | Keep only lines containing any of these, comma-separated. It is genuinely a firehose |
| `--trace-file <path>` | stderr | Write the trace to a file instead |
| `--trace-verbose` | off | Add the runtime's *internal* diagnostics: far more detail, far less stable. Useful, never something to parse |
| `--trace-include-secrets` | off | Keep credential values in event payloads, redacted by default |

The trace is redacted by default: every `Authorization`, `Cookie`, `Set-Cookie` and API-key-shaped header line in an event loses its value and keeps its name, and a URL in an event loses its `user:password@` and any credential-named query value (`access_token`, `sig`, `code`, …). `WWW-Authenticate` and `Proxy-Authenticate` are kept, because a failing proxy or SSO handshake is what a trace is read for.

**A reused connection emits no `ConnectStart` and no `HandshakeStart` at all** — that absence is the
signal, and the quickest way to see whether pooling is working. Two honest limits: the trace is
**process-wide**, so under `mock` or `serve` you will also see that server's own accepts; and it is
not packet capture, which needs a driver and administrator rights this tool deliberately never
requires.

---

## token

```
certapi token [options]
```

Fetch an OAuth 2.0 access token. By default it prints the token to stdout; `--save` stores it so
later [`send`](#send) calls attach it automatically.

Accepts the [certificate](#certificate-options) and
[streaming transport](#transport-options-streaming-subset) blocks — the token endpoint itself often
requires mutual TLS, and one pinned with [`trust add`](#trust) needs no `--insecure`.

### Grant type

| Option | Default | What it does |
|---|---|---|
| `--grant client_credentials` | **default** | Machine-to-machine: a client id and secret |
| `--grant password` | | Resource-owner password, with `--username` and `--password` |
| `--grant refresh` | | Exchange a `--refresh-token` for a fresh access token |
| `--grant device` | | Device-code flow (RFC 8628): prints a verification URL and code, then polls until you approve it in a browser **anywhere** — the answer when the machine running this has no browser, or when sign-in needs a phone. Needs `--device-url` and `--client-id`; Ctrl+C abandons the wait |

### Endpoint and client

| Option | Default | What it does |
|---|---|---|
| `--token-url <url>` | **required** | The token endpoint |
| `--device-url <url>` | none | The device-authorization endpoint. Device grant only |
| `--client-id <id>` | none | The client identifier |
| `--client-secret <secret>` | none | The client secret |
| `--client-auth <body\|basic>` | `body` | Where the client credentials go: form-encoded in the body, or an HTTP Basic header. Servers differ, and some accept only one |
| `--scope "<a b c>"` | none | Space-separated scopes |
| `--username <u>` | none | For the password grant |
| `--password <p>` | none | For the password grant |
| `--refresh-token <t>` | none | For the refresh grant |
| `--param k=v` | none | An extra form parameter — `audience`, `resource`, anything the server wants. Repeatable |

### Reuse

| Option | Default | What it does |
|---|---|---|
| `--save` | off | Store the access token for reuse by later sends |
| `--for <api-url>` | none | The API origin the saved token applies to. **Required with `--save`**, and repeatable, because a token is scoped to what it is for — storing one without saying where it belongs would attach it to hosts it was never meant for |
| `--workspace <file>` | live state | Save into a workspace file instead of the app's live state |

### Output

| Option | Default | What it does |
|---|---|---|
| `--json` | off | Print the full result: access token, refresh token, expiry, granted scope |
| `-q, --quiet` | off | No notes on stderr |

Saved tokens are encrypted at rest for your Windows user — see
[Authentication](08-Authentication.md).

---

## run

```
certapi run <Collection[/Folder][/Request]> [options]
certapi run --all [options]
certapi run --chain <name> [options]
```

Runs saved requests as a **pass/fail suite**. A request passes when its assertions all pass, or —
if it has none — on any 2xx. **Exit 1 if any request fails**, which is what makes this usable in a
build.

Accepts the [certificate](#certificate-options), [transport](#transport-options) and
[variable](#variable-options) blocks.

### What to run

| Option | Default | What it does |
|---|---|---|
| *(positional)* | — | A collection, folder, or single request by path: `"Orders"`, `"Orders/Get orders"` |
| `--all` | off | Run every saved request in the workspace |
| `--chain <name>` | none | Run a saved chain: its requests, in the order the chain names them, as one unit — so a token captured by one step is available to the next through its `{{variable}}`. Steps report PASS/FAIL; a failing step stops the chain unless it is marked to carry on, and the steps that never ran are reported as **SKIP** rather than dropped. Captures write into the environment the chain names, created on first use; an explicit `--env` beats it |
| `--data <file>` | none | A CSV or JSON dataset: run the request once per row, with each row's columns available as `{{variables}}`. See [Data-Driven Runs](13-Data-Driven-Runs.md) |
| `--diff-har <file.har>` | none | Replay a captured session and compare each response against the one it recorded, which turns a capture into a regression test. An entry passes only when its diff is identical — the status is part of the diff — and any difference exits 1. There is deliberately no `--diff-fail` here, because diffing the capture *is* the flag |

### Behaviour

| Option | Default | What it does |
|---|---|---|
| `--record` / `--no-record` | on for live state | Write each request's result back as its known-good state. On by default against the live workspace, off against a `--workspace` file — a file someone handed you is not yours to modify. Also forced off while the desktop app is running, which would overwrite the file when it closes |
| `--cookies` | off | Keep one cookie jar for the whole run, so a login's `Set-Cookie` carries to later requests |
| `--cookie-jar <file>` | none | Like `--cookies`, with the jar loaded from and saved to this file, so a run's login carries to the next run or to `certapi send --cookie-jar` |
| `--no-auto-token` | off | Do not attach captured session tokens **or** captured session cookies |
| `--diff-ignore <path>` | none | A JSON path to ignore when diffing. Repeatable |
| `--diff-ignore-header <name>` | none | One more header to ignore when diffing. Repeatable |

### Output

| Option | Default | What it does |
|---|---|---|
| `--json` | off | A JSON envelope of results instead of the table |
| `--har <file>` | none | Capture the whole suite — every request and every redirect hop — as one HTTP Archive |
| `--har-include-secrets` | off | Keep credential header values in that archive |
| `--md <file>` | none | Write the run as a markdown note: a pass/fail table with per-request timings, each failed assertion **and what arrived instead**, and `total`/`passed`/`failed` in frontmatter so a vault can chart a suite's health over time. A chain's report is numbered in step order, shows skipped steps, and links each step back to its request note from [`export markdown`](#export). Captured variables are listed **by name only** — a captured value is usually the credential the next step authenticates with |
| `--md-vault <folder>` | none | File that report as `certapi/runs/<name>-<timestamp>.md` instead: a new note per run, so the history a trend needs is never overwritten |
| `--md-include-secrets` | off | Keep credential-looking query values in the report |

A report that cannot be written warns without changing the exit code: a build must not turn green
or red because of a folder permission.

The [retry options](#retries) apply here too. A saved request keeps its own retry settings unless a
flag overrides them.

---

## fuzz

```
certapi fuzz <base-url> [-w <wordlist>] [options]
```

Probe a wordlist against a base URL to map an undocumented API. See
[Endpoint Discovery](14-Endpoint-Discovery.md).

Accepts the [certificate](#certificate-options), [transport](#transport-options) and
[variable](#variable-options) blocks.

### Wordlist

| Option | Default | What it does |
|---|---|---|
| `-w, --wordlist <file>` | built-in | Paths to probe, one per line. `-` reads from stdin, so it composes with any tool that produces a list. Omit it entirely for the built-in starter list |
| `-X, --methods <list>` | `GET` | Comma-separated methods to try against each path. `GET,POST,OPTIONS` finds endpoints that exist but reject a GET |

### Request

| Option | Default | What it does |
|---|---|---|
| `-H, --header "k: v"` | none | A header added to every probe. Repeatable |
| `--bearer <token>` | none | `Authorization: Bearer …` on every probe |
| `--timeout <seconds>` | `100` | Per-probe timeout |
| `--no-auto-token` | off | Do not attach or capture session tokens |

### Discovery

| Option | Default | What it does |
|---|---|---|
| `--concurrency <n>` | `8` | Parallel probes, 1–50 |
| `--delay <ms>` | `0` | Pause between probes. Be polite to somebody else's server — and to any rate limiter between you and it |
| `--match <codes>` | none | Show **only** these status codes |
| `--hide <codes>` | none | Hide these status codes, overriding the default view |
| `--all` | off | Show every probe, including 404s and connection errors |

With none of `--all`, `--match` or `--hide`, the table hides 404s and connection errors — the same
noise the desktop app hides, because a wordlist run is mostly misses by design.

### Output

| Option | Default | What it does |
|---|---|---|
| `--json` | off | A JSON `{ results, summary }` document instead of the table |
| `-o, --output <file>` | none | Write the discovered paths out as a wordlist — or the JSON report, with `--json`. Feeds the next run |
| `--save-collection <name>` | none | Save what was found as saved requests in a collection, ready to send |
| `--workspace <file>` | live state | Use a workspace file instead of the live state |
| `-q, --quiet` | off | No progress counter on stderr |
| `--har <file>` | none | Capture every probe as one HTTP Archive |
| `--har-include-secrets` | off | Keep credential header values in it |

---

## bench

```
certapi bench <url> [options]
certapi bench "<Collection/Saved request>" [options]
```

Measure an endpoint's latency under load. A bench **reports numbers rather than passing judgement**:
an endpoint that answers 503 every time has still been measured, so a high failure rate exits 0.
Exit 1 only when nothing answered at all.

Accepts the [certificate](#certificate-options), [transport](#transport-options) and
[variable](#variable-options) blocks.

### Load

| Option | Default | What it does |
|---|---|---|
| `-n, --count <n>` | `100` | Total requests to send |
| `-c, --concurrency <n>` | `10` | Parallel workers, never more than `--count` |
| `--duration <seconds>` | none | Run for a wall-clock period instead of a fixed count. `-n` is then unused, and the two cannot be combined |
| `--warmup <seconds>` | none | Send for this long first and **discard every result**, so the figures describe a warmed-up endpoint. Warm-up requests are extra — they do not come out of `--count` |
| `--bench-retries` | off | Let the [retry options](#retries) apply during the bench. They are forced off by default, because a retry hides the very failure rate a bench exists to measure |
| `--pool` | off | Also report the connections the run actually used: how many were opened and how many requests each served. Turns the pooling note below into a measurement — a server answering `Connection: close` makes every request pay a fresh handshake, and that dominates the latency being reported |

### Request

| Option | Default | What it does |
|---|---|---|
| `-X, --method <m>` | `GET` | HTTP method |
| `-H, --header "k: v"` | none | Add a header. Repeatable |
| `-d, --data <body>` | none | Request body. `-d @file` reads it from a file, byte for byte when it is not text |
| `--content-type <ct>` | `application/json` (`application/octet-stream` for a binary `-d @file`) | Body content type |
| `--bearer <token>` | none | `Authorization: Bearer …` |
| `--timeout <seconds>` | `100` | Per-request timeout |

On a saved request these **override or add to** what it already carries; everything else about it —
its own auth, headers, body and transport settings — is used as saved. A multipart saved request
cannot be benched.

### Output

| Option | Default | What it does |
|---|---|---|
| `--json` | off | A JSON envelope instead of the summary table |

The report gives requests sent / succeeded / failed, elapsed, requests per second, the min / p50 /
p90 / p99 / max latencies, and status and error counts. **Percentiles come from the full retained
latency array, not an approximation**, so p99 means p99.

**Connections are pooled and reused**, so only the first request to an origin pays the TCP connect
and the TLS handshake; `--warmup` removes that cost from the figures, and `--pool` shows whether it
happened. A request routed through a proxy opens its own connection every time — the proxied path
cannot be pooled. The command prints this caveat under every summary and carries it in the `--json`
envelope, because a latency figure without it is misleading.

---

## sse

```
certapi sse <url> [options]
```

Stream Server-Sent Events, printing each as it arrives. See
[Live Streaming](15-Live-Streaming.md).

Accepts the [certificate](#certificate-options) and
[streaming transport](#transport-options-streaming-subset) blocks.

| Option | Default | What it does |
|---|---|---|
| `-H, --header "k: v"` | none | Add a header to the subscription. Repeatable |
| `--max-events <n>` | unlimited | Stop after *n* events. What makes this usable in a script instead of a terminal you have to interrupt |
| `--json` | off | One JSON object per event as newline-delimited JSON: `{event, data, id, retry}` — pipes straight into `jq` |
| `-q, --quiet` | off | No connecting/ended notices on stderr |
| `--workspace <file>` | live state | Read trust pins and captured tokens from a workspace file |
| `--no-auto-token` | off | Do not attach a captured bearer token to the request |

Runs until the stream ends, `--max-events` is reached, or Ctrl+C.

---

## ws

```
certapi ws <url> [options]
```

Open a WebSocket, optionally send messages, and print what arrives. `ws://` or `wss://`.

Accepts the [certificate](#certificate-options) and
[streaming transport](#transport-options-streaming-subset) blocks — a `wss://` handshake can
present a client certificate like any other TLS connection.

| Option | Default | What it does |
|---|---|---|
| `-H, --header "k: v"` | none | Add a header to the handshake. Repeatable — this is where a subprotocol or an auth header goes |
| `-m, --message <text>` | none | Send this text after connecting. Repeatable, in order |
| `--expect <n>` | unlimited | Stop after receiving *n* messages. Makes a request/response exchange scriptable |
| `-q, --quiet` | off | No connect/send/close notices on stderr |
| `--workspace <file>` | live state | Read trust pins and captured tokens from a workspace file |
| `--no-auto-token` | off | Do not attach a captured bearer token to the handshake |

---

## certs

```
certapi certs [options]
```

List the client certificates available to you — the same list the app's picker shows.

| Option | Default | What it does |
|---|---|---|
| `--filter <text>` | none | Show only certificates whose subject, issuer or thumbprint contains this text |
| `--store <location>` | `CurrentUser` | `LocalMachine` searches both the machine store and your own |
| `--json` | off | Machine-readable output instead of the table |

Each row gives the subject, issuer, thumbprint and expiry, and flags whether the certificate is
marked for client authentication. Start here when `--cert` cannot find what you meant.

---

## selftest

```
certapi selftest [--json]
```

Prove the whole mutual-TLS path works on this machine, without needing a server. It generates a
certificate authority, a server certificate and a client certificate in memory, stands up a
loopback mutual-TLS server, and makes one authenticated round trip.

| Option | Default | What it does |
|---|---|---|
| `--json` | off | Machine-readable result instead of the summary |

**If this fails, the problem is local** — certificate loading, or the TLS stack — and not the API
you were trying to reach. That is the entire point: it separates "my machine cannot do mutual TLS"
from "that server will not accept me".

---

## mock

```
certapi mock [options]
```

A local test server to fire requests at. Runs until Ctrl+C. See [Mock Server](18-Mock-Server.md).

### Listening

| Option | Default | What it does |
|---|---|---|
| `--port <n>` | `8770` | Port to listen on. `0` picks a free one |
| `--http` | **default** | Plain HTTP — hit it with anything, no certificates involved |
| `--tls` | off | HTTPS with a generated self-signed server certificate |
| `--mtls` | off | HTTPS that **also requires** a client certificate. Any certificate is accepted; the point is to exercise the presenting side |
| `--cert-dir <dir>` | `./certapi-mock-certs` | Where generated certificates are written |
| `-q, --quiet` | off | Do not log each request |

With `--tls` or `--mtls`, the server certificate — and for `--mtls` a ready-to-use client `.pfx` —
are written to the certificate directory, so you can trust one and present the other.

### Misbehaving on purpose

| Option | Default | What it does |
|---|---|---|
| `--tls-mode <mode>` | `valid` | Serve a deliberately **broken** server certificate so a client's own error paths can be exercised from a terminal: `valid`, `expired`, `wrong-host`, or `self-signed`. Needs `--tls` or `--mtls` |

This is how you test what your client does when a certificate goes bad — without waiting for one to
expire, or talking a colleague into breaking a staging server.

### Serving your own shapes

| Option | Default | What it does |
|---|---|---|
| `--routes <file>` | built-in | Serve the routes declared in a JSON scenario file instead of the built-in echo routes. Each route says what it **matches** (method, path glob or regular expression, required query and headers) and what it **answers** (status, headers, an inline body or a `bodyFile`). Matched top to bottom, first match winning |
| `--har <file>` | none | Replay a captured HTTP Archive instead: each request is answered with the recorded response for that method and path — query included when it disambiguates — in recorded order |
| `--no-match-status <code>` | `404` | The status for a request that matches nothing in the archive |

With both `--routes` and `--har`, a request the routes miss **falls through to the recording**, so a
scenario can override a few endpoints of a captured session and leave the rest as recorded.

A scenario file can also inject faults (delays, dropped connections, sequences of differing
responses) and require credentials, so an endpoint can be made to fail exactly the way the real one
does. The built-in echo routes are not served while replaying a HAR.

---

## import

```
certapi import <kind> <source> [--into <folder>] [--workspace <file>]
```

Bring requests in from somewhere else. See [Import & Export](17-Import-and-Export.md).

| Kind | Source | Notes |
|---|---|---|
| `curl` | a quoted cURL command | Understands `-X`, `-H`, `-d`, `-u`, `-k`, bearer headers, quoting and line continuations |
| `openapi` | a `.json`/`.yaml` document | One saved request per operation, organised by tag |
| `har` | a `.har` archive | A captured session becomes saved requests |
| `postman` | a Collection v2.0/v2.1 export | Folders, both URL forms, query rows, headers (disabled stays disabled), raw/urlencoded/formdata bodies, bearer/basic/apikey auth with request-level beating folder and collection level, and collection variables as an environment (Postman's "secret" type stays secret here) |
| `insomnia` | a v4 export | Folders rebuilt from the flat resource list, disabled rows preserved, text and form bodies, bearer and basic auth, environments. `{{ _.name }}` is translated to `{{name}}` |
| `wsdl` | a WSDL 1.1 document (or the SOAP 1.2 binding variant) | One POST per operation at the port's address, with the right content type, `SOAPAction`, and an envelope skeleton |

| Option | Default | What it does |
|---|---|---|
| `--into <folder>` | root | Put the imported requests in this collection folder |
| `--workspace <file>` | live state | Import into a workspace file instead of the app's live state |

**Anything unsupported is a named warning, never a silent drop.** A `{% tag %}` template in an
Insomnia export has no equivalent here and is left in place with a warning; WSDL types are not
expanded, so each part is a commented placeholder and imported schemas are named rather than
fetched. You are told what did not come across.

---

## export

```
certapi export openapi [<folder>] -o <file> [--workspace <file>]
certapi export openapi --from-har <file.har> -o <file> [--host <h>] [--no-template-ids]
certapi export workspace -o <file> [--workspace <file>] [--include-secrets]
certapi export markdown -o <folder> [--workspace <file>] [--into <name>] [--index] [--include-secrets]
```

| Option | Applies to | Default | What it does |
|---|---|---|---|
| `-o, --output <path>` | all | **required** | The file to write — or, for `markdown`, the folder |
| `--workspace <file>` | all | live state | Read from a workspace file instead of the live state |
| `--from-har <file>` | `openapi` | none | Build the document from a captured archive instead of saved collections: repeated calls to one endpoint collapse into a single operation, identifier-looking path segments become `{id}`, responses of 400 and above are skipped, and redacted header values are never written |
| `--host <h>` | `openapi --from-har` | all hosts | Keep only entries for this host, case-insensitive |
| `--no-template-ids` | `openapi --from-har` | off | Keep identifier-looking path segments literal instead of turning them into `{id}` |
| `--include-secrets` | `workspace`, `markdown` | off | Keep secrets instead of stripping them |
| `--into <name>` | `markdown` | `certapi` | The subfolder inside the output folder to write into |
| `--index` | `markdown` | off | Also write `index.md`, a table of every request |

### openapi

Writes collections — optionally one root folder — as an OpenAPI 3.0 document. **Auth is exported as
a security scheme description only, never the secrets.**

### workspace

Writes the whole workspace as a portable JSON file, with window geometry stripped. Secrets —
captured tokens and cookies, saved auth values, saved proxy passwords, secret environment variables,
and stored response bodies — are **stripped by default**, since an exported workspace is a file people email to each
other. `--include-secrets` keeps them, written **encrypted for the current Windows user**, so even
then a recipient on another machine cannot read them. The command reports on stderr what was
stripped or kept.

### markdown

Writes the workspace as a folder of linked markdown notes: one per saved request, plus environments
and chains, with YAML frontmatter and `[[wikilinks]]`. **An Obsidian vault is just a folder of
markdown files**, so `-o` can point straight at one — and the same output works in Logseq, Foam, a
documentation repository or a plain wiki.

Re-exporting **overwrites the same notes in place**, so a generated note you edited by hand is
overwritten too; that is why the tree lives in its own subfolder, and why `--into` exists if
`certapi` clashes with something in your vault.

Credential values — including ones hiding in a URL query string — are redacted by default, because
**vaults sync**. The header *name* is kept, since "this request sends a bearer token" is what a
catalogue should record.

---

## trust

```
certapi trust list   [--workspace <file>] [--json]
certapi trust add    <host> (--thumbprint <t> | --ca <t> | --spki [<sha256>] | --from-url <https-url>) [--workspace <file>]
certapi trust remove <host> [--thumbprint <t>] [--workspace <file>]
```

Pinned server-certificate thumbprints, per host. **A pinned host is reachable without
`--insecure`** — which is the point: pinning one endpoint is a far smaller hole than turning
verification off everywhere.

| Option | Applies to | Default | What it does |
|---|---|---|---|
| `--thumbprint <t>` | `add` | — | Pin this exact SHA-1 thumbprint for the host. Colons, spaces and lower case are fine; it is stored as the 40 upper-case hex digits a certificate reports. Anything that is not a SHA-1 thumbprint (a SHA-256 fingerprint, say) is refused rather than stored as a pin that can never match |
| `--ca <thumbprint>` | `add` | — | Pin the **issuer** instead of the leaf: any certificate for the host that chains to this CA is accepted. Survives the routine renewal that keeps the issuer |
| `--spki [<sha256>]` | `add` | — | Pin the **public key** (SPKI SHA-256, base64 or hex) instead of the leaf: survives a renewal that keeps the key. With `--from-url` and no value, the key the server presented is pinned |
| `--from-url <https-url>` | `add` | — | Connect once, capture what the server actually presented, and pin its thumbprint — so you do not transcribe forty hex characters by hand. Exactly one of these two is required. The URL's host must be the host being pinned. Takes the [streaming transport subset](#transport-options-streaming-subset); through a proxy the TLS session is tunnelled and the certificate cannot be observed, which the command says plainly |
| `--thumbprint <t>` | `remove` | all | Remove only this pin; omit it to remove every pin for the host |
| `--workspace <file>` | all | live state | Read and write a workspace file instead of the live state |
| `--json` | `list` | off | Machine-readable output |

Also accepts the [certificate options](#certificate-options), because `--from-url` may need a client
certificate to complete the handshake it is inspecting.

`<host>` is a bare host name. A URL, a port or a path typed there is reduced to the host with a
note, because that is what pins are looked up by.

**A pin never overrides revocation.** A certificate the issuer has revoked is refused even past a
pin — pinning says "I know this certificate", not "ignore its issuer".

---

## serve

```
certapi serve <upstream> --port <n> [options]
certapi serve --port <n> --upstream <name>=<url> [--upstream …] [options]
certapi serve --profile <name>
```

A local gateway that forwards to one site or many **and presents your client certificate on the
way**. The answer for a browser, a tool, or a library that cannot do mutual TLS itself: point it at
`http://127.0.0.1:<port>` and the gateway handles the certificate. See
[Local Gateway](19-Local-Gateway.md), which also covers the `--routes` file format and the profile
`serve` section.

At start it prints a table — each site, the URL to open, its upstream, the certificate it presents
and what it does differently — and one `every site:` line for the gateway-wide settings. `--check`
prints the same table and exits without starting.

Answers **loopback callers only** unless `--listen` is given, because a gateway that presents your
certificate must not be reachable by anything but you without a token.

Accepts the [certificate](#certificate-options) and [transport](#transport-options) blocks.

### Listening and routing

| Option | Default | What it does |
|---|---|---|
| *(positional)* | — | The site at `/`: an upstream URL, or a saved website name from the workspace |
| `--upstream <name>=<url>` | none | A site called `name`, mounted at `/name` (and at `name.gw.localhost` with `--vhost`). `/path=<url>` mounts at a path instead; longest prefix wins. Repeatable |
| `--port <n>` | **required** | Local port to listen on (or `port` in the profile's `serve` section) |
| `--check` | off | Resolve and validate every site, certificate, file and flag, print the table of sites to stdout, and exit 0 — or 2/3 when the configuration is not usable. Nothing is bound, installed or contacted, so the port bind, the URL reservation for `--vhost`/`--listen` and the `--tls` binding are only tried at a real start |
| `--allow <hosts>` | off | **One gateway, many sites.** A request to `/_/https://host[:port]/path` is forwarded to that host with the client certificate, for any host on this comma-separated list: exact names, `*.suffix` wildcards, or `*` for every host (an open relay on loopback, logged as such). Anything else is `403` before a socket is opened. Static `--upstream` mounts work beside it; Location and cookie rewriting follow the `/_/https://host` prefix the same way they follow a mount. `/_/` and `/_certapi/` are reserved |
| `--timeout <seconds>` | `100` | How long to wait for the upstream's **response headers**. A body that streams for longer — events, long polls, downloads — is not cut off; a slow upstream is `504` |
| `--max-body <bytes>` | none | Refuse a request body larger than this with `413`. `k`/`m`/`g` suffixes |
| `--forwarded` | off | Add `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host` and `Via: 1.1 certapi` to forwarded requests, for an upstream that builds links from them |
| `--routes <file>` | none | Sites from a JSON file, repeatable: `[{ "name": "hr", "upstream": "https://hr.corp:8443", "cert": "CN=svc-hr", "windowsAuth": true }, …]` or `{ "routes": […] }` (or `"sites"`). Each site can set `cert` (or `certFile`/`certPassword`/`keyFile`), `insecure`, `caFile`, `revocation`, `windowsAuth`/`windowsUser`/`windowsPassword`, `headers` and `sni`, each overriding the gateway's flag for that site only; `prefix` mounts at a path instead of `/name`. Paths are relative to the file; values may be `${env:NAME}`. A missing certificate or file is a startup error naming the site. Full field list in [Local Gateway](19-Local-Gateway.md#sites-with-their-own-certificate-and-settings---routes) |
| `--vhost [<suffix>]` | off | **One origin per site** instead of one path each. Site `api` answers at `http://api.gw.localhost:<port>/`, the site at `/` at `http://gw.localhost:<port>/`, and an allowed dynamic host at `http://<host>.gw.localhost:<port>/` (`hr.corp--8443` for a port, `http--pub.corp` for plain http). Paths, cookies and origins reach the browser as the upstream wrote them; a cross-site redirect lands on the other host. Browsers resolve `*.localhost` to loopback by themselves; another suffix needs a hosts-file entry. Binds every interface (Windows needs a one-time URL reservation; the `netsh` command is printed) but answers **only loopback callers** unless `--listen` is also given. `127.0.0.1` keeps working in path form beside it. With `--tls` the gateway certificate also names the suffix, every site under it, and each `--allow` host on its default https port; an allowed host on another port or over http (or under `--allow *`) warns once in the browser |
| `--listen <address>` | loopback | Also answer on this address — a LAN address, `0.0.0.0` or `+` for every interface — so a VM, a container or a teammate can use the gateway. **Requires `--token`**, because anything that reaches the port then reaches your client certificate. Not with `--tls`. On Windows a non-loopback prefix needs a one-time URL reservation; the exact `netsh http add urlacl` command is printed when it is missing |
| `--workspace <file>` | live state | Resolve a saved-website upstream from a workspace file |
| `-q, --quiet` | off | No startup table and no per-request log |

The gateway answers two paths itself, never forwarding them: `/_certapi/health` (state as JSON)
and `/_certapi/routes` (each site's name, prefix, upstream, certificate and settings; the allow
list). Both honour `--token`.

**From a profile.** A profile's `serve` section has `port`, `vhost`, `browser`, `cors`, `token`,
`allow`, `listen`, `cookieJar`, `autoToken`, `windowsAuth`, `windowsUser`, `windowsPassword`,
`forwarded`, `maxBody`, `timeout`, `tls`, and `sites` in the `--routes` format. The other options
(`--check`, `--workspace`, `--quiet`, the individual browser flags, header rules, `--record`/`--replay`)
are command-line only. The certificate, `insecure`, proxy, CA files, revocation, `tlsMin` and
`connectTimeout` come from the profile's top-level fields; the top-level `timeout`, `workspace`,
`retry` and `headers` do not apply to `serve`. A typed flag wins: a typed list (`--allow`,
`--cors <origins>`, `--ca-file`) replaces the profile's, typed Windows credentials replace the
profile's as a pair, and sites typed on the command line replace the profile's list. See
[Configuration](27-Configuration.md).

### Access control

| Option | Default | What it does |
|---|---|---|
| `--token <value>` | none | Require callers to send this token as `Authorization: Bearer <value>` or `X-Certapi-Token: <value>`. Anything on the machine can reach a loopback port, so this is how you stop *other* local processes using your certificate. The token header is stripped before the request is forwarded and never recorded; a CORS preflight is answered without it, because a browser cannot attach a header to one |
| `--windows-auth` | off | Answer an upstream's Negotiate/NTLM challenge on the caller's behalf, as the signed-in account — so a browser on another machine, or a script with no Windows identity, reaches an IWA-protected site through the gateway. Aliases: `--ntlm`, `--negotiate` |
| `--windows-user <DOMAIN\\user>` / `--windows-password <pw>` | none | The same with named credentials instead of the signed-in account |

### Serving over HTTPS

| Option | Default | What it does |
|---|---|---|
| `--tls` | off | Serve HTTPS on 127.0.0.1 with a generated gateway certificate. Needed when the page calling it is itself HTTPS, since a secure page cannot call a plain-HTTP origin |
| `--tls-trust` | off | Also install that certificate into `CurrentUser\Root`, so the browser accepts it without a warning. Only with `--tls` |
| `--tls-untrust` | — | Remove a previously trusted gateway certificate and exit |

### Record and replay

Two ends of one HAR format: capture a session live, replay it offline.

| Option | Default | What it does |
|---|---|---|
| `--record <file.har>` | none | Append every forwarded exchange to an archive, written on Ctrl+C |
| `--record-include-secrets` | off | Keep credential values in that recording. Only valid with `--record` |
| `--replay <file.har>` | none | Answer from a recorded archive **without ever contacting the upstream** — a whole backend, offline, for a demo, a flight, or a test suite that must not touch production |

### Browser accommodations

A cross-host redirect — a 3xx whose `Location` names another mount, or a host on the `--allow`
list — comes back through that mount or `/_/https://host/…` when `--rewrite-location` is on, so a
login → app → login hop chain stays on the gateway and the certificate. A `Location` the gateway
cannot serve is left as written and logged.

So a page on another origin can call the upstream through the gateway.

| Option | Default | What it does |
|---|---|---|
| `--browser` | off | The bundle: `--cors`, `--rewrite-cookies`, `--rewrite-location`, `--allow-upgrade`, `--rewrite-origin`, `--strip-csp` and `--referer-fallback` at once. `--cookie-jar` and `--auto-token` stay opt-in |
| `--cors [<origins>]` | off | Answer CORS preflights at the gateway and add the response headers a browser needs. With no value, any origin |
| `--cors-max-age <n>` | `600` | How long a browser may cache a preflight answer, in seconds. Only with `--cors` (or `--browser`) |
| `--rewrite-cookies` | off | `Set-Cookie` loses `Domain=` and `Secure`, and `SameSite=None` becomes `Lax` — so a cookie issued for the real origin is actually kept by a browser talking to loopback |
| `--rewrite-location` | off | A 3xx `Location` (and a `Refresh` header's URL) pointing at the upstream comes back pointing at the gateway, so a redirect does not walk the browser out of the tunnel |
| `--rewrite-origin` | off | `Origin` and `Referer` on a forwarded request point at the upstream instead of at the gateway, so a login form's CSRF check sees the host it expects. In the `--browser` bundle |
| `--strip-csp` | off | Drop `Content-Security-Policy` (and `-Report-Only`) from responses: a policy written for the upstream's own origin blocks every fetch a page makes through the gateway. In the `--browser` bundle |
| `--rewrite-body` | off | Upstream URLs written into HTML, CSS, JavaScript, JSON and XML bodies point back through the gateway, mapped as a `Location` is (JSON's escaped slashes stay escaped). Compressed bodies are decompressed and sent plain; over 32 MB or not UTF-8, untouched. Not in `--browser`; profile field `rewriteBody` |
| `--referer-fallback` | off | A path nothing owns, from a page the gateway served (`/static/app.js` requested by a page under `/app`), is sent with a 307 to the same path under that page's mount, read from the `Referer`. For the application that writes absolute paths; `--vhost` makes it unnecessary. In the bundle |
| `--cookie-jar [<file>]` | off | Keep upstream cookies **on the gateway**, stored against the real upstream URL with `Domain` intact, and send them on every forwarded request (HTTP and WebSocket) to a matching host, alongside what the browser sent (the browser's value wins for a name both have). A browser stores every cookie against the gateway's one origin, so a `Domain=.corp` session set by `login.corp` never reaches `app.corp` on its own; this is how a multi-step login across hosts completes. Memory only unless a file is named: then the jar is loaded at start and written at shutdown, **encrypted for your Windows user (DPAPI)** and never in the clear, so a session survives a restart without sitting readable on disk. Where the machine cannot encrypt, the file form is refused up front. Not in the bundle |
| `--auto-token` | off | Attach `Authorization: Bearer …` from a token captured earlier for the upstream's origin (`send --capture`, the app's Capture tab, a session capture) when the request carries none of its own. Not in the bundle |
| `--allow-upgrade` | off | Relay WebSocket connections to the upstream as well. An upgrade reaches the upstream the way a request does — same proxy, bypass list, pins, `--ca-file` and certificate — and carries the browser's upgrade headers (Cookie, Authorization, Origin, custom) and every offered subprotocol |

### Header rewriting

| Option | Default | What it does |
|---|---|---|
| `--request-header "Name: value"` | none | Set a header on the request before it reaches the upstream. Repeatable |
| `--remove-request-header <name>` | none | Strip a header from the request. Repeatable. Removal wins over a `--request-header` naming the same header |
| `--response-header "Name: value"` | none | Set a header on the response before it reaches the caller. Repeatable |
| `--remove-response-header <name>` | none | Strip a header from the response. Repeatable. Removal wins over a `--response-header` naming the same header |

An upstream pinned with [`trust add`](#trust) is reachable without `--insecure`.

---

## grpc

```
certapi grpc list <address> [options]
certapi grpc list --protoset <file> [options]
certapi grpc call <address> <Service/Method> [options]
```

Discover and call a gRPC service — unary, server-streaming, client-streaming, or bidirectional —
with the same client-certificate handling as everything else here.

Accepts the [certificate](#certificate-options) and [transport](#transport-options) blocks.

| Option | Default | What it does |
|---|---|---|
| `-d, --data <json>` | `{}` | The request message as JSON. Each `-d` is **one message**, in order, so several of them make a client-streaming call. `-d @file` reads one message from a file. Repeatable |
| `--data-file <path>` | none | Read the request JSON from a file instead, supplying one message |
| `-H, --header "k: v"` | none | Add gRPC metadata. Repeatable |
| `--max-messages <n>` | unlimited | Stop a server-streaming or bidirectional call after *n* messages |
| `--timeout <seconds>` | `100` | Call timeout |
| `--protoset <file>` | reflection | Read service definitions from a **compiled descriptor set** instead of asking the server. This is how you call a service with reflection disabled — which is most production services. Build one with `protoc --descriptor_set_out` |
| `--workspace <file>` | live state | Load pins and tokens from a workspace file |
| `--no-auto-token` | off | Do not attach a captured bearer token for this call |
| `--json` | off | A JSON envelope instead of the plain rendering |
| `-q, --quiet` | off | No metadata line on stderr |

`list` with no service name lists the services; with one, it lists that service's methods and their
message shapes. Well-known Protobuf types are rendered and accepted in their canonical JSON forms,
so a `Timestamp` reads as an ISO date rather than as seconds and nanos.

---

## mcp

```
certapi mcp [options]
```

Run a Model Context Protocol server on stdio, so an AI agent can make mutual-TLS calls **through**
this tool without ever holding the certificate. See [MCP Server](20-MCP-Server.md).

Accepts the [certificate](#certificate-options) and [transport](#transport-options) blocks.

| Option | Default | What it does |
|---|---|---|
| `--cert <thumbprint or subject>` | none | The certificate every tool uses. **Pinned at startup — the agent cannot change it**, which is the security model: the agent chooses requests, you choose the identity |
| `--allow <host>` | none | An allowed upstream host. **Repeatable, and the allowlist is the boundary**: a URL must match one of these or be a subdomain of it, so an agent cannot be talked into calling somewhere else |
| `--timeout <seconds>` | `100` | Upstream timeout for `send_request`; a saved request may carry its own |
| `--workspace <file>` | live state | Load saved requests, environments and chains from a workspace file, so the agent can run things you prepared |
| `--no-auto-token` | off | Do not capture or reuse bearer tokens across the session's calls |
| `--protoset <file>` | reflection | Compiled descriptor set for `grpc_list`/`grpc_call` against a service without reflection |
| `--insecure` | off | Ignore upstream certificate errors (internal certificate authorities) |

The retry options `--retry`, `--retry-on` and `--retry-delay` work here with the same rules as
[`send`](#send).

Secrets are never handed to the agent: a captured token's *value* is withheld and only its
existence reported — the same stance [`export workspace`](#export) takes.

---

## doctor

```
certapi doctor <url> [options]
```

Diagnose a connection **one stage at a time** — URL, proxy decision, DNS, TCP, the proxy tunnel,
the TLS handshake, then an HTTP GET — and report the stage that broke rather than one error line.
Every stage is timed. See [Troubleshooting](23-Troubleshooting.md#start-here-certapi-doctor).

Accepts the [certificate](#certificate-options) and
[streaming transport](#transport-options-streaming-subset) blocks.

| Option | Default | What it does |
|---|---|---|
| `--json` | off | The whole report as JSON, for scripts |
| `-q, --quiet` | off | Only print stages that failed or carry advice |
| `--md <file>` | none | Also write the diagnosis as a markdown note: the stage table with timings, every detail line, the advice, and — the part worth keeping — the acceptable client-certificate authorities and any TLS-interception finding, **verbatim** |
| `--md-vault <folder>` | none | File it as `certapi/investigations/<host>-<timestamp>.md` instead. **A new note per run**: a diagnosis is history, so nothing overwrites a past one |
| `--md-open` | off | Open the written note afterwards. If nothing is registered for `.md` it prints the path rather than failing |
| `--include-secrets` | off | Keep credential-looking query values in the note, redacted by default because vaults sync |

**What it reports that an ordinary request cannot:**

- **The certificate authorities the server accepts client certificates from**, matched against the
  certificates you actually have — "none of your 3 are issued by any of those" is usually the whole
  answer, and it is only visible during a handshake.
- **Whether this network is decrypting TLS in the middle**, and why that matters: a client
  certificate cannot survive an intercepting proxy.
- **Which proxy the machine picks for this URL**, including one a PAC script chose, and what the
  proxy said if it refused — including the authentication schemes offered on a 407.
- On a DNS or TCP failure, **whether the internet is reachable at all**, distinguishing a captive
  portal from a host that needs the VPN.

A note that cannot be written warns without changing the exit code. Exit 0 when every stage passed,
1 when one failed.

---

## proxy

```
certapi proxy [<url>] [--json]
```

What this machine is configured to do about proxies, and — with a URL — which proxy it would
actually use for that address.

| Option | Default | What it does |
|---|---|---|
| `--json` | off | The report as JSON instead of text |

With no URL it prints the configuration: automatic detection (WPAD), the configuration-script
address, the static proxy and its bypass list.

With a URL it prints **two answers**: the one Windows' own engine computes by running your PAC
script, and the one .NET computes — which is the one `certapi` follows. **When those disagree, the
disagreement is the finding**, and it explains a request that works in a browser but not here, or
the other way round. A PAC script is JavaScript, so nothing can predict its answer by reading
configuration alone; this runs the real engine rather than guessing.

Exit 0 when the settings could be read, with or without a proxy configured.

---

## connections

```
certapi connections <url> [options]
```

Am I actually reusing connections? Makes the requests and reports which connection each one went
out on. See
[Troubleshooting](23-Troubleshooting.md#am-i-actually-reusing-connections--certapi-connections).

Accepts the [certificate](#certificate-options) and [transport](#transport-options) blocks.

| Option | Default | What it does |
|---|---|---|
| `-n, --count <n>` | `4` | How many requests to send |
| `--parallel <n>` | `1` | How many at a time. Parallel requests need a connection each, so read **requests against connections**, not connections alone |
| `--json` | off | The report as JSON |

Reusing a pooled connection skips a TCP handshake *and* a TLS handshake, which against a remote
endpoint is most of the time a small request takes — and it is otherwise invisible, because the
responses look identical either way. More requests than connections means reuse is working; one
connection per request means it is not, and the usual causes are a server answering
`Connection: close`, a proxy in the way, or a fresh client per request.

**How it knows, and its two limits.** It reads the runtime's own HTTP events — no driver, no
administrator rights, no private API. The runtime emits no connection-closed event this can see, so
the report covers every connection **observed since the command started** rather than a live count
of open sockets; and the listener is process-wide, which is why the report is narrowed to the origin
you asked about, with connections to other origins counted in a closing line rather than hidden.

Exit 0 when the requests were made, 3 when none could be sent.

---

## config

```
certapi config path
certapi config profiles
certapi config show [--profile <name>]
certapi config init [--local | --path <file>] [--name <profile>] [--cert <thumb|subject>] [--proxy <url>] [--force]
```

What configuration is actually in effect. See [Configuration](27-Configuration.md).

| Subcommand | What it does |
|---|---|
| `path` | The configuration file this invocation resolved, **and by which discovery rule** — so you can see whether it came from `--config`, the working directory, a parent directory, or your per-user path |
| `profiles` | The profiles the file defines, and which one is the default |
| `show` | The profile in effect, field by field, including its `serve` settings and one line per site. Passwords, tokens, proxy credentials and secret headers print as `(set)` |
| `init` | Write a starter file: one profile, made the default, with every other field present but commented out and a link to [Configuration](27-Configuration.md). It changes nothing until you uncomment something |

| `init` option | Default | What it does |
|---|---|---|
| `--local` | off | Write `certapi.config.json` in the current directory instead of your per-user file |
| `--path <file>` | per-user file | Write here instead |
| `--name <profile>` | `default` | The profile's name |
| `--cert <thumb\|subject>` | commented out | Fill in the profile's certificate |
| `--proxy <url>` | commented out | Fill in the profile's proxy |
| `--force` | off | Replace an existing file. Without it an existing file is never touched (exit 3) |

`init` works even while the configuration it would replace cannot be read.

Both honour the [global options](#global-options) `--config`, `--profile` and `--no-config`. When a
command behaves in a way you did not expect, `certapi config path` and then `--no-config` are the
two steps that separate "my configuration did this" from "the tool did this".

Exit 0 when the configuration could be read, 3 when a named file does not exist, 2 for usage.

## completion

```
certapi completion powershell
```

Prints a PowerShell script that adds tab completion for `certapi` (Windows PowerShell 5.1 and
PowerShell 7). Load it for the session, or add the line to your profile to have it every time:

```powershell
certapi completion powershell | Out-String | Invoke-Expression
Add-Content $PROFILE 'certapi completion powershell | Out-String | Invoke-Expression'
```

What completes:

| Where | What |
|---|---|
| First word | Commands |
| After a command with subcommands | `trust add`, `config show`, `import curl`, `grpc call`, … |
| A word starting with `-` | Every option of the command being typed, plus the global options |
| After `-X` | HTTP methods |
| After `--cert` (and `trust --ca`) | Thumbprints and subjects of the certificates in your store |
| After `--profile` | Profile names from the configuration file in effect |
| After `--env` | Environment names from your workspace |
| After `run` | Collection names |
| After `--store`, `--revocation`, `--tls-min`, `--grant`, `--client-auth`, `--tls-mode` | Their fixed choices |

Anything else, such as a path, falls through to PowerShell's own file completion. The script asks
the binary for each completion, so it stays right after an update. Only PowerShell is supported;
`certapi completion <other>` says so.

## update

```
certapi update [--check] [--version <tag>] [--no-app] [--force] [transport options]
```

Looks up the latest [release](https://github.com/Real-Fruit-Snacks/certapi/releases) and, when it is
newer than the copy you are running, downloads it and replaces `certapi.exe` — and
`ApiTester.App.exe` too, when the desktop app sits in the same folder. Every file is checked against
the SHA-256 GitHub publishes for it (the asset's digest, or the release's `SHA256SUMS.txt`) before
anything is replaced. A file that does not match is discarded, nothing changes, and the exit code is 1.

| Option | Default | What it does |
|---|---|---|
| `--check` | off | Only report. Exit 0 when this copy is current, 1 when a newer release exists |
| `--version <tag>` | latest | Install this release (`v1.93.0` or `1.93.0`), older ones included — the way back from a bad release |
| `--force` | off | Reinstall even when the release is the version already here |
| `--no-app` | off | Leave the desktop app alone |

It accepts the [transport options](#transport-options), so a proxy, a PAC file or a private root
works the same as for any request. `GITHUB_TOKEN` or `GH_TOKEN`, when set, goes to the GitHub API
only (never to the download) to lift the hourly limit on anonymous calls.

Windows will not overwrite a running program but will rename one, so the running `certapi.exe`
becomes `certapi.exe.old` and the new file takes its place; the next run deletes the `.old` file.
Close the desktop app before updating it, or pass `--no-app`. A copy installed with Scoop says so
and points at `scoop update certapi`; a copy not running as `certapi.exe` (`dotnet certapi.dll`) is
refused.
