# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.93.0] - 2026-10-26

Reviewed the whole transport path for three networks — public, corporate (PAC, authenticating
proxy, TLS interception) and restricted (one explicit proxy, SOCKS over SSH, a private root handed
out as a file) — and fixed what the review found. Then added the flags those networks were missing.

### Security
- **A redirect that crossed the proxy boundary went out without the client certificate or any
  trust policy.** The proxy decision was made once, from the first URL. When a PAC rule said
  DIRECT (or `--noproxy` matched) and the server redirected to a host that *does* go through the
  proxy — a login redirect to an SSO host is the common shape — the hop was tunnelled by the HTTP
  stack's own TLS, which had no certificate, no `--insecure`, no pin, no revocation setting, and
  no `--ca-file`. The direct handler now carries the same certificate and the same validation on
  its own TLS options, so a hop that changes disposition is judged exactly as the first request was.
- **`serve` relayed a WebSocket upgrade to any host a caller named.** The off-host refusal that
  stops `//evil.example/x` on the HTTP path was not applied to upgrades, so a root-mounted gateway
  with `--allow-upgrade` would open `wss://evil.example/x` with the client certificate attached.
  Upgrades now resolve through the same table and the same refusal, before any socket is opened.
- **The `serve --token` secret was forwarded to every upstream and written into `--record`
  archives in the clear.** The token header is stripped before forwarding, `X-Certapi-Token` is on
  the recorder's redaction list, and the comparison is constant-time.
- **`--proxy http://user:pass@proxy:8080` ignored the credentials and then logged them.**
  `WebProxy` never reads userinfo, so the proxy was answered with Windows default credentials
  (407 on a Basic-only proxy) while the URL, password included, went to `--log-file`, `--debug` and
  the app's transport panel. Credentials are lifted out of the URL and used as `--proxy-user`
  would be; every place that prints the proxy URL redacts it.
- **`certapi config show` printed header values, including ones resolved from `${env:…}`.**
  A header whose value came from the environment, or whose name looks like a credential
  (`Authorization`, `Cookie`, anything with key/token/secret in it), now shows `(set)`. A proxy URL
  is shown without its userinfo.

### Added
- **`--ca-file <pem>`** (repeatable; profile `caFile`, a path or a list, relative to the config
  file) — trust the root(s) in a file for this run, on top of the Windows store. For the network
  whose intercepting proxy re-signs every site with a private CA that is handed out as a PEM: the
  choice used to be `--insecure` or install the root machine-wide. A chain ending in a named root
  is accepted the way a store-trusted chain is; name, expiry and revocation problems are still
  refused. Applies to `send`, `run`, `fuzz`, `bench`, `serve` (HTTP and WebSocket), `grpc`,
  `ws`, `sse`, `token`, `trust add --from-url`, and the MCP server.
- **`--tls-min 1.2|1.3`** (profile `tlsMin`) — refuse to negotiate below a TLS version.
- **`--connect-timeout <s>`** (profile `connectTimeout`) — a separate budget for the TCP connect
  and proxy tunnel, so a blackholed host fails in seconds instead of at `--timeout`.
- **`--sni <name>`** — send a different server name in the handshake (direct connections only;
  refused with a proxy, whose tunnel negotiates TLS from the URL).
- **`serve --allow <hosts>`** — one gateway for many sites. A request to
  `/_/https://host[:port]/path` is forwarded to that host with the client certificate, for any
  host on the list (exact names, `*.suffix`, or `*` — which is logged as the open relay it is).
  Anything else is 403 before a socket opens. Static `--upstream` mounts work beside it, and
  Location and cookie rewriting follow the dynamic prefix. `/_/` and `/_certapi/` are reserved.
- **`/_certapi/health`** and **`/_certapi/routes`** on the gateway: its state, mounts and allow
  list as JSON. Never forwarded; honour `--token`.
- **`serve --forwarded`** — add `X-Forwarded-For/Proto/Host` and `Via: 1.1 certapi` to forwarded
  requests, for an upstream that builds links from them. Off by default.
- **`serve --max-body <bytes>`** (k/m/g suffixes) — refuse a request body over the limit with 413.
- **Multi-step logins through the gateway.** The pieces a browser needs to complete single
  sign-on across hosts without ever leaving the certificate:
  - a 3xx `Location` to **another mount or an allowed dynamic host** comes back through the
    gateway (`/app/…`, `/_/https://app.corp/…`) instead of leaving it — a login → app → login
    hop chain stays on the gateway (`--rewrite-location`);
  - **`serve --rewrite-origin`** — `Origin` and `Referer` on a forwarded request point at the
    upstream, not at `http://127.0.0.1:<port>`, so a login form's CSRF check passes;
  - **`serve --strip-csp`** — drop `Content-Security-Policy` from responses, because a policy
    written for the upstream's origin blocks every fetch the page makes through the gateway;
  - **`serve --cookie-jar`** — keep upstream cookies on the gateway, stored against the real
    upstream URL with `Domain` intact, and send them on every forwarded request to a matching
    host. A browser stores every cookie against the gateway's one origin and so can never carry a
    `Domain=.corp` session from `login.corp` to `app.corp`; the gateway can. Memory only;
  - **`serve --auto-token`** — attach a bearer token captured earlier for the upstream's origin
    (`send --capture`, the app, a session capture) when the request carries none.
  `--rewrite-origin` and `--strip-csp` join the `--browser` bundle; `--cookie-jar` and
  `--auto-token` stay opt-in, because they make the gateway hold session state.
- **`send --cookie-jar <file>` / `run --cookie-jar <file>`** — a cookie jar on disk, loaded before
  and saved after, so a scripted multi-step login in one invocation is a logged-in session in the
  next (curl's `-b`/`-c` in one flag). Plain JSON; session cookies are kept, expired ones dropped.
- **`trust add --from-url` takes the proxy, bypass and `--ca-file` flags** `send` takes, so a pin
  can be captured from behind an explicit proxy; it refuses a URL whose host is not the host being
  pinned, and says plainly when a proxy tunnel made the certificate unobservable.
- **`serve --referer-fallback`** — an absolute path from a page the gateway served (`/static/app.js`
  from a page under `/app`) is sent, with a 307, to the same path under that page's mount, read
  from the `Referer`. Part of `--browser`.
- **`serve --windows-auth`** / **`--windows-user` + `--windows-password`** — answer an upstream's
  Negotiate/NTLM challenge on the caller's behalf, as the signed-in account or as named credentials,
  so a browser on another machine (or a script with no Windows identity) reaches an IWA-protected
  site through the gateway.
- **`--cert-pin <pin>`** / **`CERTAPI_CERT_PIN`** — unlock a smart-card or HSM key ahead of the
  provider's prompt, so a headless job is not stopped by a PIN dialog. CNG providers on Windows;
  the PIN is never stored or logged.
- **`--cert-issuer <name>`** and **`--cert-serial <hex>`** — pick a store certificate by who issued
  it or by its serial, alone or to narrow `--cert`, for the store that holds two certificates with
  the same subject.
- **`trust add --ca <thumbprint>`** and **`trust add --spki <sha256>`** — pin an issuer or a public
  key for a host instead of the leaf, so a pin survives the routine renewal that keeps the issuer
  (or the key). `trust list` shows the kind.
- **`--pac <url|file>`** — decide the proxy for each URL with a named PAC script, through WinHTTP's
  own engine, when the machine's configured PAC is not the one the job needs.
- **`--no-env-proxy`** — ignore `HTTP_PROXY`, `HTTPS_PROXY` and `NO_PROXY` from the environment and
  use the machine's proxy settings, for the shell whose stale variables point at a proxy that is
  no longer there.
- **`--offline`** — touch nothing on the network but the request itself: no revocation fetch, no
  AIA chase, no captive-portal probe, no PAC download. For the air-gapped network and for proving
  that a request needs nothing it should not.
- **`-4` / `-6`** — dial only one address family on direct connections, for the host whose AAAA
  record resolves but whose IPv6 route does not.
- **`serve --listen <address>`** — also answer on a LAN address, `0.0.0.0` or `+`, so a VM, a
  container or a teammate can use the gateway. Requires `--token`; not with `--tls`. On Windows a
  non-loopback prefix needs a one-time URL reservation, and the exact `netsh` command is printed.
- **`serve --cookie-jar [<file>]`** — the gateway jar can now be named a file: loaded at start,
  written at shutdown, always encrypted for your Windows user (DPAPI) and never in the clear, so a
  browser session the gateway established survives a restart without sitting readable on disk.
  Where the machine cannot encrypt, the file form is refused up front.
- **`serve --routes <file>`** — mounts from a JSON file, each with its own client certificate
  (`cert` from the Windows store, or `certFile` with `certPassword`/`keyFile`, relative to the
  file). One gateway in front of many upstreams that each expect a different identity; connection
  pools are kept per upstream and certificate, the WebSocket relay presents the route's certificate,
  and `/_certapi/routes` says which identity each mount carries. A certificate that cannot be found
  is a startup error naming the mount.
- **`serve --vhost [<suffix>]`** — one origin per upstream instead of one prefix each: the `/api`
  mount answers at `http://api.gw.localhost:<port>/`, the root mount at `http://gw.localhost:<port>/`,
  and an allowed dynamic host at `http://<host>.gw.localhost:<port>/` (`hr.corp--8443` for a port,
  `http--pub.corp` for plain http). Paths, cookies and origins reach the browser as the upstream
  wrote them, which is what an application that uses absolute paths or compares origins needs;
  a cross-mount redirect lands on the other host. Browsers resolve `*.localhost` to loopback by
  themselves. The wildcard prefix this needs answers only loopback callers unless `--listen` is
  given; `127.0.0.1` keeps working in path form beside it.
- **Profile `headers` reach every request.** A profile's standing headers are merged into `send`,
  `run` (collections and HAR replay) behind any name the command line or the saved request sets
  itself, so a typed `Authorization` replaces the profile's rather than doubling it.
- **`certapi doctor`** tells a skewed clock from an expired certificate (a chain whose dates are
  within tolerance of now is reported as a clock problem with the skew), names the per-path
  client-certificate shape (a 401/403 from a server that never asked for the certificate on this
  path), and speaks Negotiate/NTLM on `CONNECT` with the signed-in account instead of stopping at
  the proxy's 407.
- **`--cert-file` with a PEM that carries intermediates** says, once, which of them the Windows CA
  store does not have and prints the `certutil` command that imports them — the TLS stack builds
  the client chain from the store, so an intermediate that lives only in the file never reaches the
  server.
- **Named gateway sites.** `serve --upstream api=https://api.corp` names a site: mounted at `/api`
  and, under `--vhost`, at `api.gw.localhost`. The `/path=url` form is unchanged.
- **Per-site settings.** A `--routes` site (or a profile's `serve.sites` entry) can set `insecure`,
  `caFile`, `revocation`, `windowsAuth` / `windowsUser` / `windowsPassword` and `headers`, each
  overriding the gateway's flag for that site only; anything left out is inherited. Paths resolve
  against the file and values may be `${env:NAME}`. Sites on one host with different settings get
  separate connection pools, and the WebSocket relay connects as the site.
- **A startup table.** `serve` prints one aligned row per site — name, the URL to open (path or
  virtual-host form), upstream, the certificate it presents and what it does differently — and one
  `every site:` line for the gateway-wide settings. No secret appears in it. `/_certapi/routes`
  carries the same names and settings.
- **`serve --check`** resolves and validates every site, certificate, file and flag, prints the
  table, and exits 0 — or 2/3 with the error a real start would give. Nothing is bound, installed or
  contacted, so it runs while the port is in use and in CI.
- **A whole gateway in a profile.** A profile's `serve` section holds the gateway's settings (`port`,
  `vhost`, `browser`, `cors`, `token`, `allow`, `listen`, `cookieJar`, `autoToken`, Windows auth,
  `forwarded`, `maxBody`, `timeout`, `tls`) and its `sites`, in the `--routes` format.
  `certapi serve --profile corp` is then the whole command. Typed flags win; sites typed on the
  command line replace the profile's list and say so. `config show` lists the sites with every
  secret masked.
- **`--name=value`** is accepted for every long option, and **`--`** ends option parsing.
- **Encrypted PEM private keys** (`ENCRYPTED PRIVATE KEY`) load with `--cert-password`; a DER
  `.crt` or a `.pfx` with the wrong extension is recognised by content.

### Changed
- **`serve --timeout` now covers the wait for response headers only.** The `HttpClient` timeout
  used to apply to the whole exchange, so a Server-Sent Events stream, a long poll or a large
  download through the gateway was cut off at 100 seconds. A slow upstream is now 504 with the
  reason; a body streams for as long as it streams.
- **The gateway forwards a declared `Content-Length` as such** instead of re-framing every body as
  chunked, and sends `Content-Length: 0` on a bodiless POST/PUT — strict servers (IIS, WAFs,
  signed-upload endpoints) answered 411 or refused the chunked form.
- **The WebSocket relay reaches the upstream the way the HTTP relay does**: through the same
  proxy, bypass list, `--resolve` pins, revocation setting, pinned thumbprints and `--ca-file`
  (it used to use the system proxy and OS trust regardless of flags), and it carries the browser's
  upgrade headers — Cookie, Authorization, Origin, custom — and every offered subprotocol. A
  cookie-authenticated socket behind a login the browser completed through the gateway now works.
- **Cookie `Path` is re-rooted under a prefix mount.** `Path=/` from an upstream mounted at `/auth`
  was sent to every other mount; `Path=/app` was never sent back for `/auth/app/…`.
- **A CORS preflight is answered before the `--token` check.** A browser cannot attach a header to
  a preflight, so `--token` with `--cors` was 401 on every one.
- **`certapi proxy` compares the two engines by host and port.** WinHTTP answers `proxy:8080` or a
  `;`-separated failover list; .NET answers an absolute URL. They were compared as strings and
  disagreed on every machine with a proxy.
- **`certapi doctor`** copies the server chain while it is still alive (the chain lines and the
  TLS-interception note were silently missing on every successful handshake), writes plain-http
  requests through a proxy in absolute form, and says plainly that its staged probe cannot speak
  SOCKS instead of blaming the proxy for a refusal that never happened.
- **`trust add` normalises what it stores**: a thumbprint pasted with colons, spaces or in lower
  case becomes the 40 upper-case hex digits a certificate reports (anything else — a SHA-256
  fingerprint, `ABC123` — is refused rather than pinned uselessly); a host typed as a URL or with
  a port is reduced to the bare host, with a note, because that is what pins are looked up by.
- **Profile `insecure` is honoured by every command**, not only `send` and `connections`.
- **Help menus match what each command reads.** An audit of every command's help against the flags
  it parses found `--cert-issuer`, `--cert-serial` and `--cert-pin` missing from eight commands,
  `--insecure` from `doctor`, `--trace-include-secrets` from the global options, and `mcp`'s trust
  and network flags unlisted. `serve`'s help opens with its three usages and examples for each.
- **Global options take `--name=value`** (`--profile=corp`) like every other option; a single-value
  global given twice is refused, and nothing after `--` is read as one.
- **`serve` says when a site URL carries a path.** Only scheme, host and port are used, so
  `--upstream api=https://api.corp/v1` now prints how to reach `/v1` instead of dropping it unseen.
- **The handbook and site were brought up to date** for everything in this release; the README named
  the wrong per-user configuration path (`%APPDATA%\CertApiTester` — it is
  `%APPDATA%\certapi\config.json`).
- **List-valued options add up when repeated.** `--noproxy`, `--retry-on`, and on `serve`
  `--allow`, `--cors <origins>` and `--routes` refused a second occurrence; each now appends to its
  list (`--allow a.corp --allow "*.lab"`), the way `-H` and `--upstream` always have. Single-value
  options given twice are still a usage error.
- **IPv6 literals are pinned under one spelling.** `trust add [::1]`, `::1` and
  `https://[::1]:8443/` store and match the same host, and `--resolve` accepts a bracketed host
  and an IPv6 address.
- **`NO_PROXY` from the environment is read leniently.** An entry the parser cannot read is warned
  about once and skipped rather than failing every command in that shell; `--noproxy` on the
  command line is still refused outright.
- **The system proxy's PAC failover is kept when a bypass list is set.** Wrapping the system proxy
  to apply `--noproxy` had hidden the second proxy a PAC names; a destination the list does not
  bypass now reaches the system proxy unwrapped.
- **`--trace` redacts every credential header and URL secret in a payload.** Only the first
  credential header in a block was redacted (a `Cookie` after an `Authorization` went through),
  and URLs in events — a proxy's `user:pw@`, an `access_token=` query value — were not touched.
  Every `Authorization`/`Cookie`/`Set-Cookie`/API-key-shaped line now loses its value and keeps its
  name; `WWW-Authenticate` and `Proxy-Authenticate` stay readable. The `--verbose` request lines use
  the same URL rule.
- **A proxy that requires a client certificate is documented as unsupported** (the tunnel is
  negotiated by WinHTTP and the HTTP stack, neither of which presents one); `doctor` names it when
  it sees the shape.
- **A broken profile no longer breaks the others.** Profiles are read when resolved, so an unset
  `${env:…}` in `prod` does not stop `--profile dev`, `config profiles` or `config path`.
- `--key-file` with a `.pfx`/`.p12` is a usage error instead of being silently ignored.
- `--cert --insecure` is "option needs a value", not a certificate called `--insecure`; a
  repeated single-value option says it was repeated.

### Fixed
- `--wire` sends leaked a copy of the client certificate until the finalizer ran.
- A path-relative `Location` (`/orders/7`) was judged a departure from the gateway on a Unix
  build, where it parses as an absolute file URI.
- `serve` with the wildcard prefix (`--listen +`, `--vhost`) bound the port twice on the managed
  listener; the wildcard now stands alone.
- Repeated `Set-Cookie` and `WWW-Authenticate` headers are pinned end to end through the listener
  (three lines in, three lines out) so a folded header can never be reintroduced unnoticed.

## [1.92.3] - 2026-07-28

### Security
- **The other two places that build archives leaked the same way 1.92.2 fixed.** `serve --record`
  and the desktop application's **Export Network trace as HAR…** each construct their entries
  directly rather than through the send path's builder, so each redacted its headers and wrote the
  credential in the URL beside them — `?api_key=…` and `https://svc:pw@host/…` alike. Both now
  apply the same rule, and `--record-include-secrets` (and the app's equivalent) still keep
  everything.
  `serve --record` is the worse of the two: it exists specifically to produce an archive that goes
  somewhere else, replayed offline or handed to a teammate.

### Added
- **A test that stops a fourth archive builder repeating this.** The shape of the bug was never
  "one place got it wrong" — it was "a rule only some of the places applied", found three times
  over by inspecting each one individually. Anything that constructs a `HarRequest` must now route
  its URL through the one shared redaction, and the check fails if a new builder does not.

## [1.92.2] - 2026-07-28

### Security
- **A captured HTTP Archive redacted the `Authorization` header and then wrote the credential in
  the URL beside it.** `?api_key=…` and `https://svc:pw@host/…` were both recorded in full, in the
  entry's `url` field *and* again in its parsed `queryString` array — while the header two lines
  away was carefully replaced with `[redacted]`. An archive is the artifact this product most
  expects people to hand over: attached to tickets, replayed by a teammate, committed as a
  regression fixture.
  Both are now redacted by default, in the request URL, in every redirect hop, and in the final
  entry, with the query array **derived from the redacted URL** so the two halves of the archive
  cannot disagree about what the request was. `--har-include-secrets` keeps everything, as before.
  One trade, recorded because it is real: a replay matches on the recorded URL, so a request
  distinguished *only* by a secret query value can no longer be told from its sibling. That is what
  the escape hatch is for; the default stays "safe to hand to someone else".

### Documentation
- **What archive redaction does not cover, said plainly.** Response bodies are stored verbatim,
  because they are what a replay serves and a diff compares — so a login endpoint returning a
  token, or a server that echoes the request back, puts that value in the archive. Redacting bodies
  would make `mock --har`, `serve --replay` and `run --diff-har` useless, so the honest answer is
  to name the one place to check before sharing a capture of an authentication flow, which the CLI
  reference now does.

### Note
- The first attempt at the fix **did not work**, and the unit tests said it did.
  `FromExchangeWithRedirects` — the path every `--har` on the command line actually takes — calls
  the redacting builder and then overwrites the URL it produced, so the redaction was applied and
  undone a line later. The unit tests covered the other entry point. It was caught by capturing a
  real archive and reading it, which is now also a test.

## [1.92.1] - 2026-07-28

### Security
- **A password written into a URL reached the terminal, the JSON output, and the investigation
  note.** `https://svc:hunter2@api.internal/orders` and `--proxy http://svc:hunter2@proxy.corp:8080`
  are both ordinary ways to pass a credential, and both were echoed back verbatim: in `doctor`'s
  header line and its proxy stage, in `doctor --json`, and — worst — in the markdown note that
  `--md-vault` files into a folder built to **sync**. The same note is the one the documentation
  tells you to paste into a ticket.
  This is the query-string leak fixed in 1.89.0, in the one form that redactor missed. The rule now
  masks the password in a URL's `user:password@host` prefix wherever a URL is shown, and **keeps the
  username** — knowing a request authenticates as `svc` is useful, and only the secret half has to
  go. A username with no password is left alone, because there is nothing secret to hide and
  blanking it would lose real information.
  Two details worth recording. The redaction is deliberately string surgery rather than `Uri`
  parsing, because it runs over values a user typed — including ones that will not parse, which must
  still be redacted rather than passed through whole. And the shortcut that decided whether a line
  needed redacting at all was "does it contain a `?`", which was correct when a query string was the
  only way a credential rode in a URL and silently wrong once this form counted; it now asks whether
  the line contains a URL.

## [1.92.0] - 2026-07-28

### Security
- **A saved proxy password was written to disk in the clear, and survived an export that claimed to
  have stripped it.** It lives on a request's own `Transport` rather than beside `AuthSecret`, and
  that is exactly how it escaped both halves of the secret handling — every other credential was
  encrypted at rest and removed on export, and this one was neither.
  Two consequences, both fixed: `state.json` held it as plaintext where a captured token, an auth
  secret and a secret variable were all encrypted for the current Windows user; and
  `certapi export workspace` stripped the auth secrets, **reported on stderr what it had
  stripped**, and left the proxy password in the file — so a workspace emailed to a teammate
  carried a corporate proxy credential while its author had been told it was sanitised. Worse in
  the case where it was the *only* secret present: the summary said the workspace "contained no
  secrets to strip".
  It is now encrypted at rest and stripped on export in all three places it can live — an open tab,
  a history entry, and a saved request — and counted in the summary. The rest of the proxy settings
  (URL, user, mode) survive stripping, because they are configuration rather than credentials and
  removing them would break the request for whoever imports it.
  A workspace written before this change holds the password as plaintext, which does not look
  encrypted, so loading leaves it exactly as it is rather than dropping it: losing a user's setting
  to a migration would be a worse bug than the one being fixed.

### Fixed
- **A text response that changed without changing size said so uselessly.** `<status>OK</status>`
  becoming `<status>NO</status>` alters neither the byte count nor the line count, so the diff
  printed the identical summary on both sides — `1 lines, 19 bytes` → `1 lines, 19 bytes` — while
  claiming a change. The verdict was always right, so `--diff-fail` still failed the build; the
  message simply told the reader nothing. It now says the content differs **and names the line they
  first diverge on**, which text can do and the byte comparison cannot. This is squarely the XML and
  SOAP case, which this product imports contracts for.

## [1.91.2] - 2026-07-28

### Fixed
- **A malformed `PUSH_PROMISE` was reported as a well-formed empty one.** The frame's promised
  stream identifier is mandatory and four bytes long; a payload too short to hold it had those
  bytes parsed as though they were a header block, producing `0 field(s), 2 bytes` — which reads
  as "a valid frame that promised nothing" rather than "this frame is broken". It now says
  `(truncated)`, matching what every other frame type already did for a short payload.
- **The frame view now names the stream a `PUSH_PROMISE` promises** (`promised=4`), which is most
  of the point of the frame — a reader wants to know *which* stream the server is about to push,
  not merely that a push was announced.

### Added
- **Tests for `PUSH_PROMISE`, which had none.** It is the only frame where two field-strips
  combine — RFC 9113 orders them Pad Length, Promised Stream ID, header block, padding — so
  getting the order wrong shifts every header, and nothing guarded it. The order was verified
  against the specification by hand first and turned out to be correct; writing the tests is what
  turned that reading into something enforced, and is what exposed the truncation defect above.
  Also covered: that `PUSH_PROMISE` names only the flags that apply to it. It shares bit values
  with `HEADERS` but not their meanings, so reporting `END_STREAM` or `PRIORITY` there would be a
  plausible-looking lie.

## [1.91.1] - 2026-07-28

### Fixed
- **`bench --pool --json` produced output that was not valid JSON.** `--json` promises one
  machine-readable document on stdout, but `--pool` appended the human-readable connection report
  after the envelope, so a script piping into `jq` got a syntax error instead of a result — the
  exact combination a dashboard would use. The connection facts now go **inside** the envelope, as
  a `connections` array and a `reusing` boolean, and the text report is printed only when `--json`
  is absent. Found by running the two flags together and parsing the output rather than by reading
  the code; the regression test parses it too, so a future change that breaks the document fails
  rather than looks fine.
  The keys are **absent** rather than empty when `--pool` was not asked for: empty would claim
  "we looked and found none", which is a different statement.

### Removed
- **A dead public property on `ConnectionInspector`.** `MostRecent` was written for the
  `send --diagnostics` integration that v1.87.0 correctly dropped — a single send runs in a fresh
  process and has nothing to reuse — but the property itself was never removed. It was referenced
  nowhere, not even by a test, and the list backing it grew one entry per request purely to feed
  it. Public API that does nothing is worse than no API: it invites use and then has to be kept.

## [1.91.0] - 2026-07-28

### Changed
- **The CLI reference is now exhaustive: every command, every option, every default.** It was a
  summary; it is now a reference. All **149 option tokens the parser accepts** are documented in
  their command's section, each with what it does, what it defaults to, and how it interacts with
  the others — including the fourteen that appeared nowhere on the page at all, among them the
  whole HTTP-version group (`--http1.1`, `--http2`, `--http3`), `--resolve`, `--show-redirects`,
  `--max-redirs`, `--no-decompress`, and the mock's `--routes`, `--tls-mode` and
  `--no-match-status`.
  The three **shared option blocks** — certificate, transport, and the streaming subset — are now
  documented once and referenced by the twelve or thirteen commands that inherit each, instead of
  being repeated or, as had happened, omitted. The page also states the things a terse help screen
  cannot: why a proxy costs you the TLS diagnostics, why an undeterminable revocation status is not
  fatal by default, why `POST` is not retried unless you ask, and why the exit codes distinguish
  "the command worked and the answer was no" from "the command never ran".

### Added
- **A settings and toggles reference for the desktop application** (wiki page 28) — the app's
  counterpart to the CLI reference. Every control, its default, and whether it is remembered
  between sessions: the request line, all seven request tabs, the whole transport panel, the
  response panel and its network filters, the sidebar, the status bar, and every secondary window.
  It ends with a table mapping each control to the command-line flag that does the same thing.
- **A test that makes "every option is documented" a checked property rather than a claim.** It
  reads every `--flag` literal the argument parser is actually asked for — the parser, not the help
  text, because help can lag behind what a command accepts — and asserts each appears in that
  command's section or in an inherited shared block. A second test guards the first: a new command
  file with no entry in the mapping fails rather than being silently checked against the whole page.
  Verified by removing one documented option and watching the test name it.

### Fixed
- Three claims in the new settings page were **wrong and were corrected against the XAML before
  shipping**: the method and content-type pickers are not editable, and the auth-type list has a
  `None (never send auth)` entry distinct from `Auto`. Every labelled checkbox in the application
  was then checked mechanically against the page.

## [1.90.2] - 2026-07-28

### Added
- **A table of contents as wiki page 00.** It sorts first in the folder, so it reads as the front
  of the sequence rather than as a file below the numbered pages, and it lists **every** page with
  a line on what is actually on it. It also adds a way in the handbook did not have: a **find it by
  what you're trying to do** section — "it won't connect and I don't know why", "it works in my
  browser but not here", "I need to see what actually went over the wire", "is it slow, and where"
  — each pointing straight at the section that answers it.
  `wiki/README.md` keeps the welcome and the at-a-glance table but no longer repeats the page list;
  it links to page 00 instead, so there is one contents page rather than two that can drift.

### Fixed
- **The anchor rule in the new documentation guard was wrong**, which is the sort of error that
  makes a test worse than useless: it reports correct links as broken. GitHub *drops* punctuation
  from a heading rather than collapsing it, so `"It's slow" — reading the timings` keeps the spaces
  either side of the dash and becomes `its-slow--reading-the-timings`, with a double hyphen. The
  rule now matches, and the mistake was caught by the links it wrongly rejected.
- Two pages — Session Capture and Configuration — had slipped into being parenthetical "also" notes
  in the middle of the old index rather than entries in it. Both are now listed properly.

### Changed
- The gate now runs a **non-incremental** build for the warning check. An incremental one had been
  reusing a cached compilation and reporting zero warnings for a file that actually had one — which
  is a check that quietly stops checking.
- **Two more documentation guards.** Anchors that point *into another page* are now checked, not
  just anchors within a page — that link is the easiest to get wrong and the hardest to notice,
  because the page opens and simply lands at the top. And the table of contents must list every
  page in the wiki, which is what stops a page from quietly becoming undiscoverable. Both were
  confirmed to fail when a link is broken and pass when it is not.

## [1.90.1] - 2026-07-28

### Fixed
- **The README, the Pages site and the CLI reference had fallen two whole programs behind.**
  Everything from `--trace`, `--wire` and `--frames` through `certapi connections`, `bench --pool`
  and the whole markdown export family had reached the wiki's topic pages but never the three
  places people actually look first. All three now cover them.
- **Three links in the CLI reference pointed at anchors that do not exist.** `#import` and
  `#export` were broken because both commands shared one heading called "import / export", so
  neither name resolved; `#trust` was broken because `certapi trust` — a real command, linked to
  from two other sections — had no section at all. `import` and `export` are now separate sections
  and `trust` has one.

### Added
- **Three tests that hold the documentation to it**, because both kinds of rot are invisible in
  review and obvious to a reader: every link to a wiki page resolves to a page that exists, every
  `#anchor` resolves to a heading that exists, and **every command the CLI dispatch table contains
  has a section in the reference**. That last one reads the dispatch table rather than a hand-kept
  list, so a new command cannot ship undocumented. All three were confirmed to fail on the broken
  files and pass on the repaired ones.

## [1.90.0] - 2026-07-28

### Added
- **`certapi run --md <file>` writes the run as a markdown note**, and `--md-vault <folder>` files
  it as `certapi/runs/<name>-<timestamp>.md`. A run tells you what passed today; a folder of run
  notes tells you whether a suite is getting better or worse, which is the thing a single terminal
  run can never show — so the frontmatter carries `total`, `passed`, `failed` and the timestamp for
  charting, and each run writes a new note rather than overwriting the history a trend needs.
  The body carries what a failure investigation actually needs: a pass/fail table with per-request
  timings, and for each failure **the assertion that broke and what arrived instead**. A transport
  failure is reported as one rather than as a missing assertion.
- **A chain's report lands *in* the catalogue rather than beside it.** Steps are numbered in order,
  steps skipped after a failure are shown rather than dropped — an output that just stops leaves
  the reader guessing whether the rest passed — and each step links back to its request note from
  `export markdown`.
- **Captured variables are listed by name only, never by value.** A captured value is usually the
  credential the next step authenticates with, and these notes go into folders that sync. Failed
  captures are named too, with the reason. Credential-looking query values are redacted as
  elsewhere; `--md-include-secrets` keeps them.
- As with `doctor --md`, a report that cannot be written warns without changing the exit code: a
  build must not turn green or red because of a folder permission.

### Fixed
- **A chain step's wikilink pointed at a note that does not exist.** A chain labels its steps
  `<chain>/<n>. <request>`, so the obvious "take the last path segment" produced `[[1. Get
  orders]]` — a dead link. Found by running a real chain rather than by reading the format. The fix
  is deliberately not a cleverer string rule: a request genuinely named `2. Follow-up` is
  indistinguishable from a chain ordinal by inspection, so the renderer is told whether it is
  rendering a chain instead of guessing, and a suite label is left intact.

## [1.89.0] - 2026-07-28

### Added
- **`certapi doctor --md <file>` keeps the diagnosis instead of letting it scroll away.** A
  diagnosis is the thing you most want a durable record of — it gets pasted into tickets, argued
  about with a network team, and looked up again when the same host breaks the same way months
  later. The note carries the stage table with per-stage timings, every detail line and the advice,
  plus the two findings a normal request cannot produce, **verbatim**: the certificate authorities
  the server said it accepts, and any TLS-interception finding. Frontmatter records `host`,
  `outcome`, `failedStage` and the timestamp, so a vault becomes a searchable history of what was
  broken when.
- **`--md-vault <folder>` files it as `certapi/investigations/<host>-<timestamp>.md`, a new note per
  run.** Nothing ever overwrites a past diagnosis — deliberately the opposite of `export markdown`,
  which re-exports in place. A catalogue is current state; an investigation is history, and a vault
  that quietly replaced last week's failure with this week's would destroy the record exactly as a
  pattern became visible. Both the asymmetry and its reason are documented.
- `--md-open` opens the written note, and **degrades to printing the path** when nothing is
  registered for `.md` rather than turning a convenience into a failure. If the note cannot be
  written at all, the command says so and still reports the diagnosis with its real exit code: the
  answer the user asked for is not worth losing to a read-only folder.
- The renderer is pure over `DoctorReport`, so the same diagnosis now renders three ways — text,
  JSON, markdown — with no third source of truth.

### Fixed
- **A credential in a URL query string could reach an exported note.** `?api_key=…` reads as part
  of an address rather than as a secret, so it survives the review a header would not — and these
  notes are bound for folders that sync. Credential-looking query values are now redacted in both
  the investigation notes and **the 1.88.0 request catalogue**, from one shared rule rather than
  two that could drift apart. The parameter name is kept, as header names are, because "this
  endpoint is called with an api_key" is worth knowing. Matching is on the whole parameter name,
  never a substring: redacting `keyword` and `tokenCount` would teach people to pass
  `--include-secrets` reflexively, which is worse than the risk it addresses.

## [1.88.0] - 2026-07-28

### Added
- **`certapi export markdown -o <folder>` turns the workspace into a folder of linked notes** — one
  per saved request, plus environments and chains, so the APIs a team actually calls become a
  browsable internal reference instead of a JSON file nobody opens.
  **An Obsidian vault is just a folder of markdown files**, which is the whole reason this is an
  export rather than an integration: no plugin to install, no service to authenticate against, and
  the same output serves Logseq, Foam, a git-backed documentation repository or a plain wiki. `-o`
  can point straight at a vault. Obsidian's conventions are honoured because they cost nothing —
  YAML frontmatter (`method`, `host`, `url`, `auth`, `lastStatus`, `lastChecked`) that its
  properties view and Dataview read, and `[[wikilinks]]` that make the export a graph: a request
  links to its collection and to any chain using it, a chain links to each step, and a step whose
  request was deleted says so rather than linking into a void.
  `--into <name>` names the subfolder (default `certapi`), `--index` adds a table of every request.
  **Re-exporting overwrites the same notes in place** — names derive filenames, so no
  `Get orders 2.md` accumulates — and the help states the consequence plainly: a generated note
  edited by hand is overwritten too, which is why the tree lives in its own subfolder.
  **Credentials are redacted by default, and that default is firmer here than anywhere else in the
  product: vaults sync.** Obsidian Sync, iCloud, OneDrive, git — a note is likelier to leave the
  machine than any other artifact this tool writes. Credential header values, saved auth secrets
  and variables marked secret are all withheld unless `--include-secrets` is given, which then says
  so on stderr. The header *name* survives redaction, because "this request sends a bearer token"
  is exactly what a catalogue should record.
  The builder is a pure function from workspace to a list of (path, content), so every layout,
  escaping and redaction rule is tested as data with nothing written to disk. The escaping cases
  are the ones that quietly corrupt a document, and each has a test: a name illegal as a Windows
  filename (including real device names like `CON`), the difference between sanitising a *filename*
  and escaping the same name in *prose* — a request called `Orders / v2 *beta*` must not render as
  italics — a `|` inside a table cell, two requests sharing a name, and a request body containing a
  ``` fence, which would otherwise close its own code block and spill the rest of the note into
  prose. That last one is also how a document that looks redacted quietly stops being one.

## [1.87.0] - 2026-07-28

### Added
- **`certapi connections <url>` answers "am I actually reusing connections?"** — by making the
  requests and reporting which connection each one went out on: the connections opened, their
  origin, protocol version, peer address and when they opened, how many requests each served, and
  then a plain verdict. Reusing a pooled connection skips a TCP handshake and a TLS handshake,
  which against a remote endpoint is most of the time a small request takes — and whether it is
  happening is otherwise invisible, because the responses look identical either way. `-n` sets how
  many requests, `--parallel` sends several at a time (those genuinely need a connection each, and
  the help says so, because the honest reading is requests against connections rather than
  connections alone), and `--json` gives the same answer to a script. Certificate and transport
  options work exactly as for `send`.
- **`certapi bench --pool` measures the reuse that command has always asserted.** Bench prints a
  note saying connections are pooled and reused; `--pool` reports what the run that just finished
  actually did. This matters for reading the numbers: a server answering `Connection: close` makes
  every request pay a fresh handshake, which dominates the latency the command exists to report.
- The connection facts come from the runtime's own HTTP event source — no driver, no administrator
  rights, no private API — and **the two things it cannot see are stated rather than glossed**: the
  runtime emits no connection-closed event this can observe, so the report covers every connection
  seen since the command started rather than a live count of open sockets; and it is process-wide,
  so a server running in the same process would appear too.
  Both facts it *does* rest on were established by probe rather than assumed, and the tests say so:
  each request's event carries the identifier of the connection it went out on, and those
  identifiers are unique across origins within a process (two origins were observed receiving 0 and
  1, not 0 and 0 — had they collided, two connections would have merged into one wrong count).
- The long-running pooling-stall investigation recorded in `ApiClientPoolingTests` now carries a
  pointer to this: several of its eliminated hypotheses had to be argued from inference, and this
  turns the central one — was the handler cache evicting? — into something directly observable. It
  is deliberately not wired into that test, because the stall does not currently reproduce and
  instrumenting a passing test proves nothing.

- **The report is narrowed to the origin it was asked about.** The listener is process-wide by
  nature, so anything else the process connected to would otherwise land in the middle of the
  answer. Connections to other origins are still counted, in a closing line, so the narrowing hides
  nothing.

### Changed
- **`send --diagnostics` did not gain pool fields, deliberately** — the design had planned for it,
  but there is no such flag, and more to the point a single `send` runs in a fresh process where
  nothing exists to reuse: the answer would have been "new connection, 1 request" every time.
  Connection reuse only means something across several requests, which is why it lives in
  `connections` and `bench --pool` instead.

## [1.86.0] - 2026-07-28

### Added
- **`certapi send --http2 --frames` reads the exchange as HTTP/2 frames** rather than as bytes —
  type, stream, flags and length per frame, on one timeline with both directions interleaved and a
  real timestamp each. This is the layer that explains the failures HTTP/2 has and HTTP/1.1 does
  not: a `WINDOW_UPDATE` that never arrives (flow control stalls a transfer with no error anywhere,
  and a four-second gap between frames is visible as one), a `GOAWAY` with its error code *and its
  debug data* — the one place a gateway explains itself in words — a `RST_STREAM` naming the stream
  that was killed, and the `SETTINGS` values that are often the whole answer to "why does it slow
  down past N concurrent requests". It implies `--wire`, since it is a second reading of the same
  capture, and needs no driver or administrator rights for the same reason `--wire` does not.
  On an HTTP/1.1 connection it says so instead of printing an empty report, and if the request is
  pinned to another version it names the flag to add rather than silently switching protocols —
  changing the protocol would change the very thing being measured.
  **Header decoding is scoped to what is actually knowable, and says so.** HPACK compresses headers
  against a table both ends build from the connection's first byte; because connections are pooled,
  a capture usually joins one already running, and that table cannot be reconstructed. So only
  references into HPACK's fixed 61-entry static table (where `:method`, `:scheme` and `:status`
  normally come from) and uncompressed literals are reported as values. Everything else is counted
  and named rather than guessed, with the report stating how many fields it could not read — and
  stating nothing at all when the capture did start at the connection's first byte, because then
  there is no caveat. The block is still walked in full, so the field count is exact either way.
  Credential headers are redacted as in the byte view unless `--wire-include-secrets` is given.
  The frame decoder is a pure function over bytes, tested against hand-built frames with no socket
  involved — including the cases that are easy to get quietly wrong: a frame split across two
  reads, the reserved bit of a stream identifier, `0x01` meaning `END_STREAM` on DATA but `ACK` on
  SETTINGS, padding and priority fields that shift every header if misread, and multi-byte HPACK
  integers. A separate test runs a real HTTP/2 request against a live server and decodes what the
  tap recorded, which is what proves the decoder is pointed at the right bytes.

### Fixed
- **Sixteen releases had lost their own changelog entries.** Everything from 1.68.0 to 1.84.0 was
  merged under a single heading: each release was written by editing the heading already at the top
  of the file instead of inserting a new one above it, so the previous version's section was
  effectively renamed to the new version and its notes absorbed. A reader looking up what changed
  in, say, 1.80.0 found nothing, while 1.85.0 appeared to contain ten releases of work. The
  software was never affected — only the record of it.
  All seventeen sections are now restored, reconstructed from the tags themselves (each tagged
  version's file still held the merged text, and consecutive tags differ by exactly one release's
  worth of notes, so the split is exact rather than a retelling). The rewrite was verified to be
  purely additive: strip the heading lines and the file is byte-identical to what it was before.
- **A test now enforces the invariant that gave this away.** The compare-link footer was correct
  throughout — it had links for versions that had no sections — so the changelog is now checked
  against itself: every link has a section, every section has a link, no version appears twice, and
  the version being built is documented. The check fails on the old file and passes on the
  repaired one.

## [1.85.1] - 2026-07-28

### Changed
- **`--keylog` now explains itself instead of dead-ending.** Typing `--keylog`, `--key-log`, or
  `--sslkeylogfile` used to produce a bare `Unknown option`, which is useless to someone who came
  to decrypt traffic. It now names the reason and the alternative at the moment the wall is hit.
  Options that deliberately do not exist can be given this treatment generally; the key-log family
  is the first entry.

### Fixed
- **A trace test could fail because of what other tests were doing** (test-suite only, no product
  behaviour involved). The connection-reuse test asserted that no socket connect appeared during
  its traced window — but the trace is deliberately process-wide, so that is a claim about the
  whole process, and any unrelated connection opened in parallel falsified it. It passed alone and
  failed in the full suite. It now makes the reuse claim against
  `System.Net.Http/ConnectionEstablished`, which carries `port=` in plain text and can therefore
  name *its own* server; the socket-level event cannot, without decoding an opaque address blob.
  The test now also opens an unrelated connection mid-window on purpose, so the interference that
  broke it is reproduced rather than merely avoided.

### Documentation
- **Answered "can I decrypt certapi's traffic in Wireshark?" honestly: no, and you do not need to**
  (wiki page 23). TLS key logging — the usual route, via `System.Net.EnableSslKeyLogging` and
  `SSLKEYLOGFILE` — **does not work on Windows**, and this was established by testing rather than
  assumed. Three configurations were tried, each with a real HTTPS request that genuinely
  succeeded, so TLS certainly happened: the switch set in code, the switch baked into
  `runtimeconfig.json`, and that plus `SSLKEYLOGFILE` set in the process environment before launch.
  No key log was written in any of them. The cause is structural rather than a misconfiguration:
  .NET implements key logging behind OpenSSL, and Windows TLS goes through SChannel, which does not
  expose session secrets.
  **So no `--keylog` flag ships**, deliberately. A switch that always produced an empty file would
  imply the capability exists and merely needs coaxing, which would cost users more time than
  having no flag at all. `--wire` (1.85.0) already delivers what the keys were wanted *for* — the
  decrypted conversation — because this tool is one end of the connection. The page also notes what
  Wireshark is still uniquely good for (retransmits, resets, MTU, timing at the IP layer, all of
  which are readable *without* decryption) and that `--trace` timestamps let you line a capture of
  your own up against the request.

## [1.85.0] - 2026-07-28

### Added
- **`certapi send --wire` prints the plaintext bytes of the exchange** — the request exactly as it
  was framed on the wire and the response exactly as it arrived, after TLS and before any parsing,
  with hex and ASCII side by side for anything that is not text. `--wire-file` writes the
  transcript out instead of to stdout, and credential headers are redacted (the header name stays,
  so you can still see it was sent) unless `--wire-include-secrets` is given.
  **This is the one thing a packet capture cannot give you** for an encrypted connection without
  its keys — and it needs no driver and no administrator rights, because the tool is one end of the
  connection: the direct send path drives its own TLS stream, so the plaintext is simply available.
  **Direct connections only**, and it says so rather than printing nothing: through a proxy the
  tunnel's TLS belongs to the HTTP handler, and on HTTP/3 the QUIC session runs inside it, so
  neither has a plaintext stream to read. Both cases print one explanatory line, send the request
  normally, and point at `--trace`.
  Two implementation facts worth recording, because both were found by the code failing rather
  than by reading documentation:
  - **The tap must be a subclass of the TLS stream, not a wrapper around it.** `SocketsHttpHandler`
    skips its own TLS handshake only when the stream returned from its connect callback *is* an
    `SslStream`; a pass-through wrapper broke that check, so the handler negotiated a second TLS
    session inside the first and the request failed outright. The first version of this feature did
    exactly that, and the byte transcript showed a `ClientHello` instead of HTTP — which is how it
    was caught.
  - **A tapped connection is never pooled.** The direct handler is cached and shared between sends,
    so a tapped one must not enter that cache or one request's bytes would appear in another's
    transcript. A `--wire` request therefore owns its connection and shows the handshake too.
## [1.84.0] - 2026-07-28

### Added
- **`--trace` reports what the network stack itself did**, on any command: DNS resolution, the TCP
  connect, the TLS handshake, whether a connection was established or reused, and the request
  lifecycle — each line timestamped from the start of the command, so the sequence reads as a
  timeline. `--trace-filter` narrows it (it is genuinely a firehose), `--trace-file` writes it out
  instead of streaming, and `--trace-verbose` adds the runtime's internal diagnostic sources —
  much more detail, much less stable, never something to parse.
  Credentials in event payloads are **redacted by default**, including a credential embedded in a
  larger payload such as a header block; `--trace-include-secrets` keeps them. A feature for
  diagnosing problems must not itself be how a token escapes into a ticket.
  **The quickest use is answering "is pooling working".** A request that reuses a pooled
  connection emits no `ConnectStart` and no `HandshakeStart` at all — that absence is the signal,
  and it is now observable from a terminal.
  Two limits are documented rather than implied: this is **in-process**, so under `mock` or
  `serve` the trace also shows that server's own accepts and handshakes; and it is **not packet
  capture** — capturing packets needs a kernel driver and administrator rights, which this tool
  deliberately never requires. What it offers instead is the decrypted, structured account of
  connections that a sniffer could not read anyway.
  The event and source names were **observed from a running process** rather than taken from
  documentation, and two findings from that probe shaped the result: the TLS source appears only
  on an HTTPS request (a plain-HTTP probe sees no TLS events at all, which means "this request did
  not use it", not "the runtime lacks it"), and the runtime's internal sources are unstable enough
  by name to be opt-in only.
## [1.83.0] - 2026-07-28

### Added
- **A mock scenario can require credentials**, so an agent, an app, or a teammate pointed at the
  mock meets a realistic refusal instead of an echo. A `require` block asks for a client
  certificate (any, or narrowed by `issuer` or `thumbprint`), a specific bearer token, or both,
  and refuses with 401 (default), 403, or 407.
  Requirements are checked **before** routes, which means a scenario can be both "requires a
  bearer" and "answers these paths" — and a refusal can never leak a route's body. The refusal
  carries a real challenge header (`WWW-Authenticate`, or `Proxy-Authenticate` for 407), so a
  client under test sees the shape a real endpoint sends rather than a bare status. A client
  certificate is judged at the application layer, after the handshake, the way a real service
  does it: the connection succeeds and the request is refused, which is a different code path in
  the client from a handshake that fails.
  A `require` block that asks for nothing, or an `onFail` outside 401/403/407, is named in a
  warning rather than silently obeyed.

### Note
- This completes the five-release configuration and configurable-mock program that began with
  configuration profiles in v1.79.0. The mock now answers from declared routes, a recorded
  session, or both; misbehaves on demand with delays, drips, aborts, resets, and per-call
  sequences; serves deliberately broken certificates; and can demand credentials — which together
  make every client-side path this product ships reproducible from a terminal.
## [1.82.0] - 2026-07-28

### Added
- **`certapi mock --tls-mode expired | wrong-host | self-signed` serves a deliberately broken
  server certificate**, so the client-side TLS errors this product reports become reproducible at
  a terminal rather than only inside its own test suite. `expired` presents a certificate for
  `localhost` whose validity ended an hour ago; `wrong-host` presents a perfectly valid
  certificate issued for somewhere else, so the *name* check is what fails; `self-signed` presents
  a leaf that is its own issuer, chaining to nothing. `valid` (the default) is unchanged.
  This is what turns the mock into a test bed for the client: `doctor`'s TLS stage, `send`'s
  `ServerCertificateUntrusted`, and the `--insecure` override can all now be exercised without
  finding a real broken endpoint to point at. The flag needs `--tls` or `--mtls` — over plain HTTP
  there is no certificate to spoil, and asking for one is a usage error rather than a silent
  no-op — and the mock prints a line reminding you that a client refusing this certificate is the
  *correct* outcome, so a red result reads as success.
  One X.509 constraint worth recording, because it shaped the implementation: a certificate cannot
  begin before the authority that issued it. The expired certificate's whole validity window
  therefore sits inside the mock authority's own — twelve hours ago to one hour ago — rather than
  starting a year back as a first attempt did.
## [1.81.0] - 2026-07-28

### Added
- **The mock can now misbehave on demand, which is the point of a test server.** A scenario's
  `respond` block accepts `delayMs` (a pause before the first byte — the timeout exerciser),
  `jitterMs` (random spread on top, so repeated calls are not a metronome),
  `dripBytesPerSec` (send the body slowly, to trip a *read* timeout on a response whose headers
  arrived promptly), and `then: "abort" | "reset"` — the first closes the connection after
  promising a body in the headers, the second sends a TCP reset, which is what a client sees when
  a middlebox or a crash takes the connection away.
- **`respondSequence` lets a route answer differently on each call** — "fail twice with 503, then
  succeed" — with the last entry repeating once the list is exhausted. This is the shape a **retry
  policy** has to be tested against, and it was not expressible anywhere in this product before:
  retry logic could only be exercised against fixtures inside the test suite, never from a
  terminal against a real socket. Now `certapi send --retry 3 --retry-on 503` against such a route
  succeeds on the third attempt, and the route's own call counter proves three requests genuinely
  arrived rather than a retry merely being intended. The counter is thread-safe, because the mock
  serves connections concurrently and a sequence that lost count under load would make a retry
  test quietly lie.
  Declaring both `respond` and `respondSequence` on one route is refused by name — a contradiction
  rather than a merge — and an unrecognised `then` value degrades to behaving normally while
  saying so. The declared `Content-Length` deliberately stays the whole body's length even when
  the body is dripped or abandoned: that mismatch is the fault being injected, and correcting it
  would hide it.
## [1.80.0] - 2026-07-28

### Added
- **`certapi mock --routes <file>` makes the mock answer like *your* API.** Until now the mock
  echoed, or replayed a session you had already captured; there was no way to simply declare "these
  paths answer these bodies" and stand a fake backend up in a minute. A scenario file is a list of
  routes, each saying what it matches — method, path as a glob (`*` within a segment, `**` across
  them) or a regular expression, required query pairs and headers — and what it answers: status,
  headers, and an inline `body` or a `bodyFile`.
  Routes are matched **top to bottom, first match winning**, so a narrow route written above a
  broad one shadows it deliberately. A route states what it *requires*, so extra query parameters
  and headers on the request do not prevent a match. A `bodyFile` is resolved against the scenario
  file's own folder, which keeps a scenario and its bodies portable as one unit. Declared routes
  beat the built-in echo routes — they are what someone deliberately wrote — and a request matching
  none gets the scenario's own `fallback`, or a 404 that says no route matched.
  **A route that cannot be used is dropped and named**: an uncompilable `pathRegex`, a missing
  `respond` block, a status outside 100–599, or an unreadable `bodyFile` (that last one still
  answers, with an empty body, since the status is usually the point). Comments and trailing commas
  are accepted, because these files are written by hand.
  `--routes` and `--har` compose deliberately: the declared routes cover the handful of paths you
  care about, and anything they miss falls through to the recorded session. A header value carrying
  a newline cannot forge a second response, the same defence the replay path already had.
## [1.79.0] - 2026-07-28

### Added
- **Configuration files with named profiles, so a long command line becomes a short one.** A
  `certapi.config.json` carries the half of every invocation that never changes — the certificate,
  the proxy group, revocation, retries, timeout, a default workspace, and standing headers — and
  `certapi send https://api.internal/orders --profile corp` uses it. The three options that reach
  it (`--profile`, `--config`, `--no-config`) are **global**, so they work on every command rather
  than needing to be re-declared per command.
  The file is found by the first rule that matches: an explicit `--config`, the `CERTAPI_CONFIG`
  variable, `certapi.config.json` **found by walking up from the working directory** (so a
  per-repository configuration works from anywhere inside it), then a per-user file. `--no-config`
  ignores all four, which is what makes a continuous-integration run reproducible regardless of
  what sits in a parent directory. Comments and trailing commas are accepted, because these files
  are edited by people.
  **Precedence is one sentence: an explicitly typed flag always wins, the profile fills in what
  you did not type, and the built-in default stands when neither said anything.** There is no new
  precedence engine — it is the null-coalescing chain the commands already used, which is why it
  behaves predictably. Naming one of a mutually exclusive pair counts as choosing it, so
  `--cert-file` on the line suppresses a profile's `cert` and `--no-proxy` suppresses a profile's
  `proxy`, instead of colliding with it.
  **Secrets stay out of the file:** any value may contain `${env:NAME}`, resolved from the
  environment when the file is read, so the file names *which* secret it needs while the secret
  itself lives wherever the machine or pipeline keeps secrets. A reference to a variable that is
  not set is an error naming the profile, the field, and the variable — never a silently empty
  credential.
- **`certapi config path | show | profiles`** reports what is actually in effect, in the same
  spirit as `doctor` and `proxy`: which file was found and by which rule, which profiles exist and
  which is the default, and the resolved profile exactly as a command would see it. It prints
  `(set)` for a password or proxy credential rather than the value — a diagnostic must never be the
  thing that leaks a secret.
## [1.78.0] - 2026-07-28

### Added
- **`certapi import wsdl <file>` turns a SOAP contract into saved requests** — one POST per
  operation at the port's address, with the content type and action each SOAP version actually
  wants (`text/xml` plus a `SOAPAction` header for 1.1; `application/soap+xml` with the action as
  a content-type parameter for 1.2), and an envelope skeleton in the right envelope namespace with
  the operation element in the service's target namespace.
  **It is deliberately minimal, and the docs say so plainly:** types are *not* expanded from the
  schema. Each message part becomes a commented placeholder naming its element or type — "fill in
  from the schema" — because generating a full instance document from XML Schema, with its
  imports, restrictions, choices and substitution groups, is a different product, and a fabricated
  body would look authoritative while being wrong. This gets a SOAP request about ninety percent
  written and is honest about the last ten.
  An imported document or schema is **named in a warning, never fetched**: the parser reads the
  one file you name and touches no network, so a contract split across files tells you which other
  files to import rather than silently importing half of it. A port with no SOAP address, or a
  document with no SOAP service at all, is reported the same way instead of importing nothing in
  silence.

### Note
- This completes the ten-release reachability program that began with `certapi doctor` in v1.69.0.
## [1.77.0] - 2026-07-28

### Added
- **`certapi import insomnia <file>` reads an Insomnia v4 export** — the other format teams
  actually have, after Postman. Insomnia writes a *flat* list of resources that point at their
  parents by identifier rather than a nested tree, and in no guaranteed order, so the folder tree
  is rebuilt from those links: a request whose folder appears later in the file lands in the right
  place anyway. Methods, URLs, query and header rows (disabled ones staying disabled), text and
  form bodies by media type, multipart parts, and bearer/basic authentication all map across, and
  Insomnia's environments become environments here.
  The interesting part is templates, because the two products spell variables differently:
  **`{{ _.name }}` is translated to `{{name}}`** everywhere it appears — URL, query, headers, body,
  and auth — so imported requests work immediately. A **tag template** (`{% uuid 'v4' %}`,
  `{% response … %}`) is a small program rather than a value and has no equivalent here; it is left
  in the text exactly as written and named in a warning at import time, which surfaces the gap when
  the operator can act on it instead of at send time. An authentication block Insomnia has switched
  off is ignored rather than applied, and a file part imports disabled — its path came from someone
  else's machine.
## [1.76.0] - 2026-07-28

### Added
- **`{{env:NAME}}` reads a variable from the process environment**, so a credential can reach a
  request without ever being stored — not in the workspace, not in an exported file, not in source
  control. It is the answer for a continuous-integration job or a shared repository: the request
  says *which* secret it needs, and the secret lives wherever the pipeline keeps secrets. It works
  everywhere `{{variables}}` already do — the desktop application, `send`, `run`, chains, and the
  MCP server — because all of them resolve through one seam.
  Three deliberate rules: a saved workspace variable of the same name **wins**, so someone who
  genuinely stored a variable called `env:TOKEN` is not overruled by the namespace; a missing
  environment variable is **reported as unresolved and left intact**, never quietly expanded to an
  empty credential (`{{env:}}` with no name behaves the same way); and the `env:` prefix is
  case-insensitive while the variable name is passed to the operating system exactly as written.

### Note
- **The resolver change above was committed one release early.** The v1.75.0 commit was assembled
  while this work was already on disk and swept `VariableResolver.cs` in with it, so the code
  shipped in v1.75.0 under a message about certificate expiry, without its tests. Continuous
  integration built and tested that exact tree and passed, and the change is additive — the
  existing two-argument resolver is untouched — but the tests that prove it are only landing now,
  with this release. Recorded here rather than rewritten out of history, since v1.75.0 was already
  published.

## [1.75.0] - 2026-07-28

### Added
- **A client certificate that is about to stop working now says so while there is still time to
  renew it.** Every command that resolves a certificate warns when the chosen one expires within
  fourteen days — "certificate 'CN=My Client' expires in 7 days (not after 2026-08-04)" — and the
  desktop application's certificate list badges the same rows with `[EXPIRES IN 7d]` beside the
  existing `[EXPIRED]`. Fourteen days is the window because it is enough notice to get a corporate
  renewal through a ticket queue, which is the process this notice exists to start; it lives in a
  single named constant so retuning it is a one-line change.
  Until now the product only spoke up once the certificate had **already** expired — which is to
  say, once the outage had already happened. The warning goes to stderr, never blocks the command,
  and an already-expired certificate keeps its own louder message rather than being softened into
  "expiring soon". A certificate that is not valid *yet* now says that too, instead of being
  reported as expired.
  The day count floors rather than rounds, deliberately: with 23 hours left, "expires in 1 day"
  would overstate the time remaining on the one day it matters most, so it reports "today".
## [1.74.0] - 2026-07-28

### Added
- **`certapi serve --record <file.har>` captures every exchange the gateway forwards, and
  `--replay <file.har>` answers from that capture without ever contacting the upstream.** Two ends
  of one format, for the case where the real thing is not available: record while the upstream is
  up, then develop, demo, or test against the recording on a plane, after the service is
  decommissioned, or when production must not be touched. The recording is an ordinary HTTP
  Archive, so a session captured through the gateway also replays through `certapi mock --har`
  with no gateway at all — the two features were built to meet in the middle, and replay reuses
  the existing `HarReplaySource` rather than growing a second matcher.
  `Authorization` and `Cookie` are redacted by default, because a recording is a file people
  share; `--record-include-secrets` keeps them. Framing headers (`Content-Length`,
  `Transfer-Encoding`) are dropped from the capture so a replay never re-frames a response it did
  not build. The two flags are mutually exclusive — you cannot record a session you are inventing —
  and the recording is written once at shutdown, after in-flight requests finish, so a relay never
  pays a file write per request. Buffering only happens when recording; the default relay still
  streams bodies straight through, untouched.
## [1.73.0] - 2026-07-28

### Added
- **`--http3` pins a request to HTTP/3 over QUIC**, beside `--http1.1` and `--http2` (the three
  are mutually exclusive), and the desktop application's Transport tab gained the matching
  option. Pinning stays exact: a server that cannot speak the pinned version fails loudly rather
  than quietly downgrading — which is the point, since a gateway that behaves differently across
  versions is otherwise very hard to catch in the act. Two boundaries are enforced up front with
  messages that say why: HTTP/3 cannot go through a proxy (QUIC is UDP; the proxy protocols here
  are TCP), and `--resolve` cannot re-point a QUIC dial. It needs an OS with msquic (Windows 11 /
  Server 2022 or later). One honest limitation, stated in the code where it lives: an HTTP/3
  send reports the server certificate as usual, but not the negotiated cipher — only the
  hand-driven TCP path can observe that, and QUIC's handshake runs inside the handler.
  The wire proof runs against an HTTP/3-**only** loopback server, so the pinned client passes
  exactly when it really spoke QUIC, and a `--http2` control against the same server is shown to
  fail loudly. The request editor's version drop-down grew a member, which tripped the round-trip
  coverage gate built in v1.66.0 exactly as designed — the mapping had to be decided in both
  directions before the suite would pass again.
## [1.72.0] - 2026-07-28

### Added
- **`--proxy` speaks SOCKS: `socks5://`, `socks4a://`, and `socks4://` are accepted everywhere the
  flag exists.** The case this serves is the SSH jump host: `ssh -D 1080 user@jump` opens a SOCKS5
  proxy on your own machine, and `--proxy socks5://127.0.0.1:1080` then reaches whatever the jump
  host can reach — with **mutual TLS intact end to end**, because SOCKS relays bytes and never
  terminates TLS, so the client certificate arrives at the real server. (An HTTP-inspecting proxy
  cannot make that promise; it is the difference this scheme exists for.) The transport layer
  already knew how to speak SOCKS — the work was the validation gate that refused the scheme, the
  documentation, and the proof: a loopback SOCKS5 server now fronts the mutual-TLS test server in
  the suite, with the tunnel's own accept counter showing the bytes went through it and a bypass
  rule shown still sending matching hosts around it. The refusal message for an unsupported scheme
  now names what is accepted.
## [1.71.0] - 2026-07-28

### Fixed
- **Every redirect hop in an exported HTTP Archive claimed to have taken no time at all.** The
  hop entries were written with `time: 0` and `timings.wait: 0`, and every HAR viewer draws that
  as an instant bar — which hides the exact thing the export is useful for: a request that takes
  two seconds because it is four hops of five hundred milliseconds, not because the destination
  is slow. Each hop is now measured on its own and carries its real time; a hop that genuinely
  was not measured reports `-1`, which is HAR's own spelling of "not applicable", rather than
  claiming zero.
- **The redirect chain printed by `--show-redirects` now names each hop's duration** — the same
  measurement, where a person reads it rather than a tool. A hop with no measurement prints no
  duration instead of "0 ms".

### Changed
- **Documented how to read the three different timings this product reports**, because they
  answer three different questions: `doctor` times the stages of one fresh connection (where the
  time goes), a redirect chain is timed per hop (whether the hops are the problem), and `bench`
  measures a warm endpoint under load (what to quote for throughput). The troubleshooting page
  also now says plainly why `send` reports one total and no breakdown: its connections are
  pooled, so a second request has no lookup, connect, or handshake left to measure, and printing
  zeros would suggest those were instant rather than absent.
## [1.70.0] - 2026-07-28

### Added
- **`certapi proxy [<url>]` — which proxy do I actually get?** It prints how this machine is
  configured to reach the internet (WPAD auto-detection, the configuration-script address, the
  static proxy and its bypass list, as Internet Options records them) and, given a URL, which
  proxy applies to *that address*.
  The per-URL answer comes from **Windows' own engine** — `WinHttpGetProxyForUrl`, running your
  PAC script the way the operating system does. That distinction matters: a proxy auto-config
  script is a JavaScript program, so nothing can predict its answer by reading settings, and
  re-implementing it would need a script engine (and a dependency this project does not take).
  Alongside it the command prints .NET's answer for the same URL, which is the one certapi
  actually follows — **and says so when the two disagree**, because that disagreement is the
  explanation for the oldest complaint in corporate networking: "it works in my browser but not
  in this tool." A WPAD network with no script, an unreachable script, and a script with an
  error in it are each reported as themselves rather than as a generic failure. `--json` prints
  the whole report for scripts.
## [1.69.0] - 2026-07-28

### Added
- **`certapi doctor <url>` — the answer to "why can't I reach this?"** It makes the connection one
  stage at a time — URL, proxy decision, DNS, TCP, the proxy tunnel, the TLS handshake, then an
  HTTP GET — and reports *the stage that broke*, with what it saw at each and how long each took,
  instead of the single error line every other tool gives you. It deliberately owns the socket and
  the TLS stream rather than going through the ordinary request pipeline, because that is the only
  way to see four things worth more than any error message:
  - **The certificate authorities the server accepts client certificates from, matched against the
    certificates this machine actually has.** "The server accepts certificates from
    `CN=Corp Issuing CA 2`; none of your 3 are issued by any of those" is the whole answer to the
    most common mutual-TLS mystery, and it exists only inside a handshake — nothing else in this
    product could report it. When the server asks for no client certificate at all, it says that
    too, which is just as often the surprise.
  - **Evidence that the network is decrypting TLS in the middle** — a chain rooted in a known
    inspection product, or in a private root this machine happens to trust — worded as evidence
    and never as a verdict, together with why it matters: a client certificate cannot survive an
    intercepting proxy.
  - **Which proxy this URL actually goes through**, including one chosen by a PAC script or WPAD,
    why (explicit flag, bypass rule, or the system's own answer), and — when the proxy refuses the
    tunnel — the authentication schemes it offered on its 407.
  - **Whether anything is reachable at all**, when DNS or TCP fails: it distinguishes "no internet
    at all" from "a captive portal answered — sign in to this Wi-Fi" from "the internet is fine, so
    this host is either misspelled or needs the VPN".
  `--json` prints the whole report for scripts, `-q` shows only what failed or carries advice, and
  the exit code is 0 when every stage passed, 1 when one did not.

### Fixed
- **A defect found while building the above, before it ever shipped:** the first draft resolved the
  target hostname before deciding on the proxy, which would have failed an internal hostname that
  only the proxy can resolve — blaming DNS for a connection that would have worked. The proxy
  decision now comes first, and DNS reports on whichever host is actually dialled, saying plainly
  that the target is resolved by the proxy rather than here. A regression test pins it.
## [1.68.0] - 2026-07-27

### Added
- **`certapi import postman` reads a Postman Collection (v2.0/v2.1 export)** — the format most
  teams actually have in hand. Folders come across as folders; requests keep their method, both of
  Postman's URL forms, query rows, and headers, with disabled rows staying disabled. Bodies map by
  mode — raw (the declared language decides the content type), urlencoded, and formdata — and auth
  maps for bearer, basic, and apikey (as the header or query row Postman meant), with
  request-level auth beating folder-level beating collection-level, exactly as Postman resolves
  it. `{{variables}}` share their syntax between the two products, so request text imports
  unchanged, and collection-level variables become an environment named after the collection — a
  variable Postman marked `secret` is stored encrypted here like every other secret at rest. Two
  deliberate cautions: a file form part imports disabled, because its path came from someone
  else's machine and silently uploading whatever sits there would be worse than asking; and
  anything that cannot carry across faithfully — an `awsv4` auth, a `graphql` body — is a named
  warning on stderr, never a silent drop.
- **`certapi token --grant device` runs the OAuth 2.0 device-code flow (RFC 8628)** — the grant
  designed for the machine this tool lives on: one with no browser, or reached over SSH. It asks
  the `--device-url` endpoint for a code, prints the verification URL and code (preferring the
  complete-URL form when the server offers one), then polls the token endpoint until the sign-in
  is approved from a browser anywhere — honoring the server's polling interval, the `slow_down`
  back-off (+5 seconds each, per the RFC), the code's expiry, and Ctrl+C. The token flows into
  the same `--save`/`--for` reuse every other grant feeds.

### Fixed
- **`ws`, `sse`, and `token` were the last network commands living outside this year's transport
  work, and `serve`'s upstream connection could not use trust pins.** A source audit showed each
  building its own bare connection: a host pinned with `certapi trust add` still demanded
  `--insecure` on a WebSocket, an event stream, a token fetch, and behind the gateway; none of
  the three commands could name a proxy, narrow one with `--noproxy`, or check revocation. All
  four now go through the same shared transport tables as everything else: pins work without
  `--insecure`, `--proxy`/`--proxy-user`/`--no-proxy`/`--noproxy` and
  `--revocation`/`--revocation-strict` are accepted with the same strict parsing as `send`, and
  `ws`/`sse` attach a captured bearer token to the handshake automatically (`--no-auto-token`
  turns it off, `--workspace` names the state file). Retries, redirects, and HTTP-version pins
  are deliberately not among the new flags on streams: a re-subscribe has side effects a retry
  must not hide.
- **The command-line reference's import list showed `curl` and `openapi` but not `har`** — the
  same looks-complete-but-isn't defect the `serve` section had; the row is there now, next to the
  new `postman` one.

## [1.67.0] - 2026-07-27

### Added
- **The MCP server caught up with the product.** `certapi mcp` predates most of this year's
  features, and an audit against both the product and the Model Context Protocol's own revisions
  found it behind on each. Four new tools close the product side:
  - **`run_chain`** runs a saved chain by name — the "log in, then call the API" pattern that is
    exactly what an agent does — through the same engine as `certapi run --chain` and the desktop
    application, each step seeing what earlier steps captured.
  - **`list_environments`** names the workspace's environments for `run_saved`/`run_chain`'s `env`
    argument. Names and variable counts only; a variable's value is never returned.
  - **`grpc_list` and `grpc_call`** bring gRPC to agents: discovery via server reflection or a
    descriptor set the operator pins at launch with the new `--protoset` flag (the agent can never
    name files), and calls of all four method kinds — unary, server-streaming, client-streaming,
    bidirectional — with streaming responses bounded by `maxMessages` (default 100). The same
    pinned certificate and host allowlist govern every call.
- **Saved requests, environments, and chains are now published as read-only MCP resources**
  (`certapi://requests/…`, `certapi://environments/…`, `certapi://chains`), so a host can show the
  agent what exists without spending tool calls. A request's auth secret reads as `(redacted)` and
  a secret variable's value is withheld — the same stance `certapi export workspace` takes.
- **`certapi mcp` gained the transport flags every other network command already had**: the
  `--proxy`/`--proxy-user`/`--no-proxy`/`--noproxy` group, `--revocation`/`--revocation-strict`
  (it was the last network command without revocation checking), and the `--retry` group. All of
  them apply to every call the tools make.
- **The protocol layer moved up to the 2025-06-18 revision** while still accepting clients on
  2024-11-05 and 2025-03-26: tools carry behavioral annotations (read-only / destructive /
  idempotent / open-world hints) so a host's permission model has structure instead of prose,
  results include `structuredContent` alongside the text form with `outputSchema` declared where
  the shape is stable, server notes arrive as `logging` notifications gated by `logging/setLevel`,
  and an unknown client protocol version is answered with the newest supported one rather than
  echoed back as if it were understood.

### Fixed
- **The MCP server ignored pinned server certificates.** Every other network command consults
  `certapi trust add` pins when a server's certificate fails ordinary validation; `mcp` did not,
  so the only way to reach a pinned internal host was `--insecure` — the blunt instrument pinning
  exists to replace. Pins from the workspace now work exactly as they do for `send`/`run`.
- **`run_saved` now runs a saved request exactly as `certapi run` would.** It previously
  re-implemented the send: the saved request's transport settings (proxy, retries, HTTP version,
  revocation, bypass list) were silently dropped, its assertions were never evaluated, its capture
  rules never applied, and only Bearer/Basic auth survived. It now runs through the same
  `RequestRunner` path as `run`, the desktop application, and chains — saved transport honored,
  all auth types, assertions reported in the result, captures applied and visible to later calls
  in the session. A saved request that names no certificate of its own now presents the pinned
  session certificate.
- **The command-line reference's `serve` section listed some flags but not others, with nothing
  to say the list was partial** — `--upstream`, `--token`, the `--tls` group, and the `--browser`
  bundle were absent while the CORS, header-rule, and revocation rows were present, which read as
  "this is everything". The distinctive flags are all rows now; the full detail stays in the
  Local Gateway handbook, which the section already points to.

### Changed
- **The MCP session model is now explicit: the workspace is read once at launch, and nothing is
  ever written back.** Captured tokens, cookies, and `{{variables}}` live in memory for the
  session — which is what lets a chain's login serve a later tool call — and die with the
  process. Previously the workspace was re-read on each call and captures were not applied at
  all, so the model was neither fresh nor durable; now it is deliberately one thing.
- **Redirects are never followed by MCP tool calls**, whatever a saved request's own setting
  says: a 3xx comes back as data, so every hop an agent takes is an explicit call the host
  allowlist judged. Previously true for `send_request`; now guaranteed across `run_saved` and
  `run_chain` too.

## [1.66.1] - 2026-07-27

### Fixed
- **`certapi help grpc` ran two options together on one line.** The shared certificate-options
  block is spliced into three commands' help text; the gRPC command's splice was missing a line
  break, so `--insecure` was appended to the end of `--key-file`'s description instead of starting
  its own line. `bench` and `trust` were spliced correctly and are unchanged.
- **Three descriptions still said `certapi grpc` handles only unary and server-streaming calls** —
  the command-line reference's summary table, the desktop application's help window, and nothing
  else; every other mention was already right. Client-streaming and bidirectional support shipped
  in v1.60.0, and these two summaries were written before that. Both now name all four kinds.
- **`certapi help export` never mentioned that `-o` has a long form, `--output`.** The parser has
  always accepted both; the help text now says so. This came out of a sweep in the other
  direction from the usual one — not "is every documented flag accepted" (the acceptance suite
  already checks that) but "is every accepted flag documented".

### Changed
- **Beyond that one line of help text, nothing changes for a user in this release.** It closes
  out the mutation-testing pass the
  v1.66.0 assurance work set up: Stryker.NET ran to completion over the six security-critical
  files (revocation, proxy bypass, header rules, trust pins, secret protection, and the browser
  gateway's rewriter), and every surviving mutant was triaged as a real test gap, a provably
  equivalent mutation, or deliberately unpinned diagnostic wording. The real gaps are now closed
  by tests (survivors down from 67 to 48, never-covered mutations from 20 to 5, and every
  remaining one individually accounted for): every refusal path and both port boundaries of the `--noproxy`
  parser, the empty-host/empty-thumbprint guards on certificate pinning, a pinned thumbprint
  rescuing an ordinary chain problem *while revocation checking is on*, the multi-entry and
  explicitly-empty CORS origin allowlists (an empty allowlist allows nothing; before, no test
  distinguished it from "echo anything"), the gateway surviving a degenerate `Set-Cookie` with no
  `=` in it, the encrypted-secret marker's exact 32-byte boundary, and the empty string
  round-tripping through secret protection as `""` rather than being dropped as undecryptable.
  Each of these was a place where the product was right but nothing would have failed had it
  quietly stopped being right.

## [1.66.0] - 2026-07-27

### Changed
- **Nothing changes for a user in this release.** It is an assurance release: two parts of the
  desktop application that no test could reach were moved behind seams a test can reach, and one
  handbook page was finished. Behaviour is exactly what v1.65.0 shipped. The value is that a
  *future* release breaking either of those two things now fails a test instead of reaching a user.
- **The request editor's round trip — loading a saved request into the desktop editor's controls,
  and capturing those controls back into a request — now runs through a user-interface-free record
  instead of only inline inside the window.** `MainWindow.LoadIntoControls` and
  `CaptureControlsInto` map a saved request onto the editor's controls and back, and a mismatch
  between the two is invisible: a field the load side reads but the capture side never writes back
  is silently discarded the next time the request is saved, and a drop-down whose index-to-value
  mapping differs between the two silently rewrites the setting the user chose. That is not
  hypothetical — during v1.64.0 the revocation drop-down's mapping had to be hand-verified in both
  directions precisely because no test could reach it. A new record, `RequestEditorState`, now
  carries that mapping as two functions, `From(RequestModel)` and `ApplyTo(RequestModel)`, that run
  without a window, and it is tested against every field a request carries, every drop-down's full
  range of modes in both directions, all five authentication types, and the parsing and clamping of
  every numeric box — plus a coverage gate, so adding a field to a saved request or another mode to
  a drop-down fails a test until the mapping is decided in both directions. Behaviour is unchanged,
  including several long-standing quirks that were pinned by tests rather than fixed.
- **Deciding what an imported workspace does to the one already open — which collections,
  environments, saved websites, chains and history survive a merge or a replace — is now a pure
  function instead of logic inline inside the window.** Getting that decision wrong destroys a
  user's saved work, and nothing tested it before. `WorkspaceImport.Plan(current, incoming, merge)`
  now makes the decision over two workspace values, with no window involved, and is tested in both
  modes. The case that motivated pulling it out: an imported environment whose *name* collides with
  an existing one is added alongside it rather than overwriting it, because environments are matched
  by identifier and never by name; one whose *identifier* collides is skipped without overwriting the
  existing values. Behaviour is unchanged.
- **The local gateway handbook (`wiki/19-Local-Gateway.md`) gained rows for `--upstream`, for
  mounting several upstreams behind one port, and for `--tls` / `--tls-trust` / `--tls-untrust`, for
  serving the gateway itself over HTTPS (Hypertext Transfer Protocol Secure).** v1.65.0 back-filled
  the four `--browser` accommodations and deliberately left these four out rather than absorb
  unrelated debt into that release; this closes it.

## [1.65.0] - 2026-07-27

### Fixed
- **An empty or whitespace-only `-X`/`--method` no longer leaks a framework exception and exits 1 as
  if the connection itself had failed.** `certapi send <url> -X ""` (and `-X "   "`) used to print
  `The value cannot be an empty string or composed entirely of whitespace. (Parameter 'method')` — an
  internal parameter name a caller was never meant to see — and return the transport-failure exit
  code, when a malformed argument should be a usage error (exit 2), exactly like every other bad flag
  value in the same command. It now prints a written message naming the flag and what was expected,
  and exits 2. The same shared check covers `certapi bench`'s `-X`/`--method`; `certapi fuzz`'s
  `-X`/`--methods` (a comma-separated list) now refuses a value that resolves to no methods at all
  instead of silently falling back to GET; and the Model Context Protocol (MCP) server's
  `send_request` tool returns the same written message through its own error shape instead of the
  leaked one. Deliberately unchanged: arbitrary extension methods are legal Hypertext Transfer
  Protocol (HTTP), so `PATCH`, a lowercase `get`, and a non-standard verb all keep working, pinned by
  a guard test.

### Changed
- **Stored response bodies — in history entries and in a saved request's known-good snapshot — are
  now encrypted at rest, closing the gap v1.62.0 left behind.** That release encrypted captured
  tokens, cookies, saved auth secrets, and secret variables, but a login response body sitting in
  history or in a known-good snapshot could itself contain `{"access_token":"…"}`, written to the
  workspace file in the clear — exactly the exposure v1.62.0 existed to close and named as a residual
  at the time. Bodies are now protected the same way, with the Windows Data Protection API (DPAPI),
  and decrypted transparently on load. A body that can't be decrypted (a file from another Windows
  user or machine) degrades to absent with a named warning rather than throwing; the rest of the
  workspace still opens. `certapi send --diff known-good` and `certapi run --diff-har` read these
  snapshots back exactly as before.
- **The on-disk workspace format moves to schema version 3.** An existing file loads with every field
  intact and is upgraded on its next save, after the timestamped backup v1.62.0 introduced — a failed
  backup aborts the save rather than overwriting the only copy. Migration is additive and lossless,
  the same as v1.62.0's was.
- **`certapi export workspace`'s default secret-stripping now covers stored bodies too**, rather than
  leaving them a hole beside everything else it already strips. A history entry keeps its method,
  URL, and status with its body emptied; a known-good snapshot is removed outright, because a
  baseline with an emptied body would make every later diff report a spurious whole-body difference.
  The export's summary reports them in the same "stripped …" sentence as every other secret, and
  `--include-secrets` keeps them, still encrypted, exactly as it does for the rest.

## [1.64.0] - 2026-07-27

### Added
- **A new `--revocation none|offline|online` flag adds certificate revocation checking, defaulting to
  `none`** — exactly the previous behavior, so nothing changes for an existing user. `offline`
  consults cached certificate revocation lists (CRLs) only and never reaches the network; `online` may
  fetch a fresh CRL or query an Online Certificate Status Protocol (OCSP) responder. It is a
  saved-with-the-request transport setting, round-tripped through workspaces exactly like the
  `--noproxy` bypass list added in v1.63.0, shared by `send`, `run`, `fuzz`, `bench`, and `serve` (one
  shared transport-flag parser), and also accepted by `grpc`.
- **A revoked certificate is now its own outcome, `ServerCertificateRevoked`, separate from
  `ServerCertificateUntrusted`** — separating the two is the entire point of the feature. In a
  corporate public-key infrastructure (PKI), "this certificate was revoked" (a compromised key, or an
  employee who left) and "this certificate isn't trusted" (usually a missing root) are completely
  different findings, and reporting them identically left the tool unable to answer the question that
  actually mattered. The new outcome's message says plainly that the certificate was revoked by its
  issuer, rather than merely untrusted.
- **An unknown revocation status is reported and is not fatal by default.** With `--revocation online`,
  a blocked or unreachable revocation endpoint yields "revocation status unknown" — the *common* case
  on a corporate network, where failing on it would make the tool unusable on exactly the networks it
  targets. **`--revocation-strict`** makes an undeterminable status fatal for whoever needs that
  guarantee; passing it without `--revocation offline` or `--revocation online` is a usage error (exit
  2) rather than a silently ignored flag, since with checking off there is no unknown status for it to
  make fatal.
- **Revocation beats a pinned thumbprint.** When checking is enabled and a certificate is genuinely
  revoked, the connection is refused even if the host has a pin from `certapi trust add`: a pin is an
  operator's earlier statement, revocation is the issuer's later withdrawal, and the later statement
  wins. Under the default `none` this can never trigger, so pinning behaves exactly as it always has —
  and `--revocation-strict` is likewise not rescued by a pin, since the user explicitly asked for an
  indeterminate answer to be fatal.
- **`--insecure` still bypasses everything, but now says so.** It means "trust anything," so it keeps
  overriding revocation — but the diagnostics now state that revocation was **not enforced**, rather
  than leaving a reader to assume a clean check happened. `certapi send` prints a `note:` on stderr
  saying so, and it appears in `--debug`, the `--json` envelope, and the app's Diagnostics panel.
- **The outcome is reported whether or not checking ran.** Every response carries which mode was
  requested and what came back — checked-and-good, revoked, status unknown, or not checked — so a user
  can answer "was revocation actually verified?" without guessing. Surfaced in `--debug` (a
  `revocation …` term on both the transport line and the connection line), the `--json` envelope (new
  `revocationMode` and `revocationStatus` keys), and the app's Diagnostics panel (a new **Revocation**
  line in the SERVER CERTIFICATE block, always present).
- **The setting applies wherever server certificates are validated, consistently** — the `send` path,
  `certapi serve`'s gateway, and the `grpc` path all now go through one shared decision table, the same
  way v1.63.0 unified the three hand-rolled copies of the proxy switch. A revocation setting honored on
  `send` but ignored on `serve` would be exactly the inconsistency that release fixed.
- **A REVOCATION row on the request editor's Transport tab** adds a three-way mode combo box plus a
  "Fail when the status can't be determined" checkbox — saved with the request and round-tripped
  through workspaces like the other transport settings. The in-app Help window documents it.

## [1.63.0] - 2026-07-27

### Added
- **A new `--noproxy <list>` flag lists hosts to reach directly instead of through the proxy**,
  using the entry forms the widely-understood `NO_PROXY` environment-variable conventions already
  establish, so existing knowledge transfers: a bare hostname (`internal.corp`) matches that host
  and its subdomains but not a look-alike (`notinternal.corp`); a leading-dot or wildcard suffix
  (`.corp`, `*.corp`) matches the domain and its subdomains and not a domain that merely contains it
  (`internal.corp.evil.com`); an IP address literal (`10.1.2.3`, `::1`) is matched as an address,
  never as a suffix; a Classless Inter-Domain Routing (CIDR) range (`10.0.0.0/8`, `fd00::/8`) is
  accepted, but one written with host bits set (`10.0.0.5/8`) is refused rather than quietly masked;
  `*` alone bypasses everything; matching is case-insensitive; and an optional `:port`
  (`internal.corp:8443`, `[::1]:8443`, `*:8080`) must also match when present. An entry that cannot
  be understood is refused with a message naming it (exit 2) rather than silently dropped, because a
  dropped entry would leave internal traffic leaking through the proxy while the user believed it
  was bypassed. It is shared by `send`, `run`, `fuzz`, `bench`, and `serve` (they share one
  transport-flag parser), and is also accepted by `grpc`.
- **The bypass list narrows an existing proxy rather than replacing it** — with the system proxy it
  narrows the machine's proxy (including Web Proxy Auto-Discovery (WPAD) and a proxy
  auto-configuration (PAC) script), and with `--proxy` it narrows the named proxy instead. Combining
  `--noproxy` with `--no-proxy` is a usage error (exit 2): there is no proxy left for `--noproxy` to
  narrow, so the combination is refused rather than one of the two flags being silently ignored.
- **`NO_PROXY` (falling back to `no_proxy`) is honored when `--noproxy` is not given**, because that
  is what the rest of the ecosystem does and a corporate machine often already sets it. Precedence
  when more than one source could apply: an explicit `--noproxy` wins, then a saved request's own
  bypass list, then `NO_PROXY`. The environment is not consulted at all when `--no-proxy` is given,
  since the user named no bypass list in that case.
- **A request that went direct because a bypass rule matched is now distinguishable from one that
  never had a proxy at all.** `--debug` prints `proxy no (bypassed by '<rule>')`, and the app's
  Diagnostics panel gains a `Bypassed by` line naming the rule. Going direct also restores the TLS
  diagnostics a proxy hides — version, cipher, and “client certificate presented” — so a bypassed
  host reports them where a tunnelled one cannot.
- **The bypass applies everywhere the proxy applies**: `send`, `run`, `fuzz`, `bench`, `mcp`, the
  app, `certapi serve`, and `certapi grpc` — all three of the places that configure an HTTP
  handler's proxy now share one implementation, so the bypass cannot apply on one path and not
  another.
- **A BYPASS box on the request editor's Transport tab** lets the app set the same per-host list —
  saved with the request and round-tripped through workspaces like the other transport settings.

## [1.62.0] - 2026-07-27

### Added
- **Secrets in the workspace file are now encrypted for the Windows user who saved them, instead of
  sitting in plain text.** A captured bearer token, a browser-captured session cookie, the auth
  secret saved on an open tab, a history entry, or a saved collection request (the Basic-auth
  password or bearer token), and an environment variable value marked **secret** are each encrypted
  in place — as the string `enc:v1:<base64>` in the same JSON property they always occupied. Keys,
  names, URLs, headers, and bodies are left exactly as they were, on purpose: the file stays
  diffable and readable for everything that isn't a credential.
- **Encryption is per current Windows user, through the Windows Data Protection API (DPAPI)**,
  called directly against `crypt32.dll` rather than through a package, so `ApiTester.Core` still
  carries zero NuGet references. The call is always made with prompting forbidden, so a headless
  `certapi` run can never hang waiting for a dialog that will never appear, and scope is always the
  user, never the machine.
- **A new `Secret` flag on an environment variable** marks a value for encryption; tick **secret**
  on any variable in the Environments window to opt it in. A captured value gets the flag
  automatically, because capturing a value into an environment is exactly the path that lands a
  token there in the first place.
- **The first time an older workspace is rewritten in the new format, the previous file is copied
  beside itself first**, as `state.json.<yyyyMMdd-HHmmss>.bak` — so upgrading a workspace can't cost
  you the plain-text copy you had before. The app says so in its status line, both before the
  upgrade (while the file still stores secrets in the clear) and again once the rewrite has
  happened, naming the backup it kept.
- **A secret that cannot be decrypted degrades instead of crashing anything.** Because protection is
  scoped to one Windows user, a workspace file carried to a different user or a different machine
  cannot have its secrets read back. Each one is treated as absent — a captured token or cookie is
  dropped, an auth secret or secret variable is left empty — a warning names the field, and every
  other part of the workspace still loads: requests, collections, chains, history, and environments
  are all unaffected. `certapi`'s exit codes are unchanged by this: it's a warning, not a failure, so
  the run still exits 0; only a workspace that cannot be read at all is still a data error (exit 3).

### Changed
- **`certapi export workspace` now strips secrets by default**, the same way HTTP Archive (HAR)
  export already redacts them, because an exported workspace is a file people end up emailing to
  each other. Captured tokens and cookies are removed, saved auth secrets are cleared, and a secret
  environment variable keeps its key and its flag but loses its value — the exported workspace still
  opens and works, just without the credentials. Pass `--include-secrets` to keep them; they are
  still written encrypted for the current Windows user, so even then there is no way to write a
  secret to disk in the clear. The desktop app's **Export workspace…** always writes secrets through
  that same encrypting path — the strip-by-default behavior is `certapi export workspace`'s alone.
- **The on-disk workspace format changed: schema version 2.** An older file loads exactly as before
  and is upgraded — encrypted, backed up first as described above — on its next save; nothing has to
  be done by hand. The one visible consequence: a workspace file copied to another Windows user or
  another machine now loses its secrets on that load (and only its secrets — everything else in it
  is intact), because encryption that traveled with the file would not really be protecting anything.

## [1.61.1] - 2026-07-27

### Fixed
- **`certapi serve` no longer accepts a header rule with an empty name and then silently drops
  it.** `--request-header " : v"` used to start the gateway and log `listening` while the name
  parsed to the empty string: nothing refused it, and at forward time the HTTP stack discarded the
  nameless header, so the upstream never saw it and the operator had no way to know the rule had
  not applied. An empty or whitespace-only name is now a usage error (exit 2) on all four flags —
  `--request-header`, `--remove-request-header`, `--response-header`, `--remove-response-header` —
  naming the flag and the shape it expected. A name carrying a character that is not legal in an
  HTTP field name, such as a space or an embedded colon, is refused the same way and for the same
  reason: the header could never match, so the rule would be dropped rather than applied. This is
  the rule the previous release already applied to the framing headers and `Host` — a rule that is
  accepted is a rule that takes effect.
- **A future edit can no longer make the gateway's two header sets disagree in silence.** The
  hop-by-hop names the gateway never relays are stripped *after* the header rules have been
  applied, so a name added to that list but not to the list of headers a user may not manage would
  have become a rule `serve` accepts on the command line and then throws away — the defect above,
  reintroduced by an edit nobody would connect to it. The two sets stay separate on purpose,
  because they answer different questions, but the hop-by-hop names are now exposed read-only and
  a test pins every one of them as a name the header rules refuse, failing with a message that
  explains the trap rather than inviting someone to silence it.

## [1.61.0] - 2026-07-27

### Added
- **`certapi serve --browser`'s `--cors` now answers Chrome's Private Network Access (PNA)
  preflight**, closing a failure that presented as "the gateway just doesn't work" with no visible
  cause: a page on a public origin calling a private or loopback address gets a further preflight
  carrying `Access-Control-Request-Private-Network: true`, and Chrome blocks the request before any
  gateway logic runs unless the response answers `Access-Control-Allow-Private-Network: true`. Since
  `certapi serve --browser` binds `127.0.0.1` specifically so a web page can call it, this was the
  gateway's headline use case failing silently in the most common browser.
- **The PNA header is emitted only when three things hold together**: Cross-Origin Resource Sharing
  (CORS) handling is on, the preflight actually carried the PNA request header, and the request's
  `Origin` passes the existing allowlist check (`--cors <origins>`, or echo mode when no list was
  given) — never unconditionally, and never for an origin the existing policy would refuse. The
  allowlist stays the one security boundary for both concerns; an origin outside an explicit
  allowlist still gets a bare 403 with no headers at all. Letting a public origin reach a loopback
  service at all is a real exposure regardless of PNA, which is why naming the origins you develop
  from with `--cors <origins>` is the safer form than leaving it echoing whoever asks.
- **New `--cors-max-age <seconds>`** replaces a hardcoded 600-second `Access-Control-Max-Age`; 600
  is still the default, so nothing changes for an existing `--cors` user. `0` is legal — it tells the
  browser not to cache the preflight answer at all — and a non-numeric or negative value, or the flag
  given without `--cors`/`--browser`, is a usage error (exit 2).
- **Four new repeatable flags manipulate headers on forwarded traffic**: `--request-header
  "Name: value"` and `--response-header "Name: value"` replace a header if one was already sent and
  add it otherwise; `--remove-request-header <name>` and `--remove-response-header <name>` strip one.
  Naming the same header to both a set flag and a remove flag on the same side removes it — removal
  wins over setting.
- **The rules work with or without `--browser`**, because a header rule is not a browser concern:
  they live in their own pure `ApiTester.Core` type, `HeaderRules`, applied on the forwarding path
  regardless of browser mode. On the response side they apply after `--browser`'s own rewrites (CORS,
  cookies, `Location`), so a header configured explicitly here wins over one the gateway injected —
  someone overriding `Access-Control-Allow-Origin` on purpose gets their value.
- **`Connection`, `Keep-Alive`, `Transfer-Encoding`, `Content-Length`, `TE`, `Trailer`, `Upgrade`,
  `Proxy-Authenticate`, `Proxy-Authorization`, and `Host` are refused with a usage error naming the
  header and why**, rather than silently ignored: the first nine frame the HTTP message and the HTTP
  stack manages them; `Host` is set by the gateway's own HTTP client from the upstream URI, so a rule
  for it would only ever half-apply.
- **The default relay stays byte-faithful, pinned by a characterization test**: with none of the new
  flags the gateway forwards exactly as it did before. The header rules act on forwarded HTTP traffic
  only — never on a CORS/PNA preflight the gateway answers itself, its own 404/400/502 error pages,
  or a relayed WebSocket upgrade.

## [1.60.0] - 2026-07-27

### Added
- **`certapi grpc call` now handles all four gRPC method kinds — unary, server-streaming,
  client-streaming, and bidirectional** — closing the one hole left after `--protoset` shipped in
  1.58.0. Which of the four a method is comes from the service's own definition, discovered from its
  descriptor (reflection-fetched or supplied with `--protoset`), never declared by the user with a
  flag: a flag for it would just move the tool's own bug — guessing wrong — onto the user instead of
  fixing it.
- **Multi-message input mirrors `certapi ws`, which had already solved this problem**: `-d`/`--data`
  is now repeatable, and for a client-streaming or bidirectional method each `-d` value, then a
  `--data-file` message if given, then each line read from standard input (one JSON object per line,
  a whitespace-only line skipped so a trailing newline in a pipe never becomes an empty message) is
  sent as one message, in that order. Standard input is consulted only for those two kinds — a unary
  or server-streaming call never reads it, so a single `-d` against a unary method behaves exactly as
  it did before, and piping something into one can neither hang the call nor change its request.
- **Two new rules at the command line make the four-kind dispatch honest rather than silently
  wrong.** Supplying several `-d` values to a method that takes a single request message (unary or
  server-streaming) is now a usage error — exit 2, naming the method and its actual kind — rather
  than silently sending only the first. Supplying no messages at all to a client-streaming or
  bidirectional method is legal: it sends an empty stream, not an error.
- **A bidirectional call sends and receives concurrently — sending never blocks receiving.**
  Responses print as they arrive, one compact JSON object per line, exactly like a server-streaming
  call; the call ends when the server completes, `--max-messages` is reached (exit 0, not a
  failure), or Ctrl+C. It is not an interactive read-eval loop. The interleaving is proved by an
  assertion on the order of observed events — sent m0, received m0, sent m1, received m1 — rather
  than by a clock, alongside failing variants of the two new kinds added to the test fixture.
- **A non-OK gRPC status reaches exit code 1 on every one of the four kinds.** Under the hood: unary
  and client-streaming return the status as data; server-streaming throws after yielding whatever
  arrived; bidirectional returns the status as data after every message that did arrive has already
  been printed. Exit codes are unchanged otherwise — 2 for a bad command line, 3 for a data problem,
  never a stack trace.
- **`--protoset` drives all four kinds identically to server reflection**, pinned by tests that call
  a client-streaming and a bidirectional method against a server with no reflection service at all —
  and the well-known Protobuf types keep their canonical JSON rendering through any of the new
  streaming kinds (a `Timestamp` is still `"2023-11-14T22:13:20Z"`, never
  `{"seconds":...,"nanos":...}`), whether the descriptors came from reflection or from `--protoset`.
- **The one remaining true limit is unchanged**: `certapi serve` does not proxy gRPC, because
  `HttpListener` (what the gateway is built on) is HTTP/1.1-only — `certapi grpc` reaches the service
  directly with your certificate instead of going through the gateway. `--protoset` still only helps
  if you already have, or can produce, the descriptor set yourself — certapi does not compile
  `.proto` sources.

## [1.59.0] - 2026-07-26

### Added
- **Chains now run in the app, through a "▶ Run chain" button in the CHAINS sidebar**, closing a gap
  that had been there since chains first shipped: you built the thing in the window, then
  switched to a terminal to use it. A characterization suite was written first, not after —
  `tests/ApiTester.Tests/Cli/ChainCharacterizationTests.cs`, 25 tests pinning `certapi run --chain`'s
  observable behavior (the per-step PASS/FAIL line shape, the SKIP line, the footer, the verbatim
  stop-on-failure message on stderr, every exit code — 0 all passed, 1 any step failed, 3 for an
  unknown chain listing the ones that exist, 3 for an empty chain, 3 for a step naming a deleted
  request before anything is sent — the exact property set of the `--json` envelope, known-good
  recording per step, captures landing in the chain's environment, and stop-on-failure in both
  directions) before a single line of the runner moved. That file was left unedited through the whole
  refactor that followed and is still green today — it is the evidence, not an assertion, that the
  command line did not change underneath this release.
- **Chain execution was extracted out of the command line into `ApiTester.Core`**, because the
  alternative — copying the send / capture / rebuild-variables path into the app so it could have its
  own "run" button — was rejected outright: two copies of that logic drift the moment either one is
  touched, and a chain that passes in one front end and fails in the other is exactly the defect a
  shared engine exists to rule out. The new public Core surface is `RequestRunner.RunAsync` for the
  per-request path (resolve variables, attach auth, find the certificate, apply transport, send,
  capture tokens, record known-good, evaluate assertions, apply capture rules, rebuild variables),
  `RequestRunContext` and `RunVariables` for the run-wide choices and the front end's own seams,
  `ChainRunner` (`Find` / `Resolve` / `PrepareCaptureEnvironment` / `RunAsync`) for the chain itself,
  `AssertionEvaluator.RequestPassed` as the single definition of "this request passed", and
  `RedirectReport.Lines` for the shared redirect-hop text. The runner reports progress through
  `IProgress<ChainRunProgress>` and honors a `CancellationToken` — it has no WPF and no `Console`
  reference, so `ApiTester.Core` still carries zero NuGet package references, and `certapi run
  --chain` became a thin caller of it: `RunCommand.cs` lost 228 lines and gained 46, with byte-identical
  behavior proved by the characterization suite above.
- **The app's run window shows a per-step verdict as the chain executes**, not just a final result:
  `ChainRunWindow` lists one row per step in run order, each turning PASS, FAIL, or SKIP with its
  status, time, and size as that step completes, so you can watch a five-step chain land instead of
  waiting on it blind. Selecting a row shows that step's actual response — pretty-printed with the
  same formatter and syntax highlighter the main response panel uses — alongside its notes and any
  failing assertions, so "what did step 3 actually get back" is a click, not a re-run with `--debug`.
  The run is asynchronous, so the window stays responsive while it works, and a Stop button (or
  closing the window) cancels a run in progress.
- **Captures and known-good behave in the app exactly as they do headless**, because both paths are
  now the same code: the chain's capture environment is created on first use and made active, a
  value captured by step one is visible to step two as a `{{variable}}`, a newly created environment
  is folded into the environment picker rather than silently vanishing, and known-good markers are
  recorded per step onto the saved requests the sidebar already holds. The run window never writes
  `state.json` itself — the app persists on close exactly as it always has, so nothing is lost or
  double-written by adding a second place that runs a chain.
- **One deliberate difference between the two front ends, stated here because a user will hit it and
  can't be expected to guess it:** when a chain names no capture environment, `certapi run --chain`
  resolves `{{variables}}` against nothing, while the app resolves them against whichever environment
  is selected in its own picker — because in the app that picker is the user's stated choice, and
  every other send in the window already honors it.

## [1.58.0] - 2026-07-26

### Added
- **`--protoset <file>` on both `certapi grpc list` and `certapi grpc call`**, so a service that does
  not implement server reflection — common in production, where it is often turned off deliberately —
  is no longer a hard wall. The file is a compiled `FileDescriptorSet`: the binary output of
  `protoc --descriptor_set_out=<file> --include_imports <proto>`. `--include_imports` is not optional
  — without it the set carries the root file but not the types it imports, including the well-known
  types — and the flag itself is named to match `grpcurl -protoset`, which consumes the identical
  interchange format, so the knowledge transfers directly. A descriptor set wins over server
  reflection whenever both are possible; the two are never merged, not even to fill in something the
  set happens to lack. Proved by test: against a reflection-enabled server — one that, by definition,
  advertises `grpc.reflection.v1alpha.ServerReflection` as one of its own services — a `--protoset`
  listing never includes that service, because the supplied set does not declare it. `certapi grpc
  list --protoset <file>` works entirely offline: the address argument is optional in that one
  combination, and one supplied alongside `--protoset` anyway is accepted and ignored rather than
  dialed. Proved by a test in which no listener is started anywhere in the process. The headline case
  is pinned directly: one in-process gRPC server started with reflection disabled — `certapi grpc
  call` against it fails with the reflection-unavailable error, and the identical call with
  `--protoset` succeeds. Every descriptor-set problem is exit 3 with a plain message, never a stack
  trace: a missing file, an unreadable file, a file that is not a parseable `FileDescriptorSet`,
  forgetting `--include_imports` (the message names the specific missing file and tells you to
  re-run `protoc` with the flag), passing the `.proto` source by mistake (detected and named: "looks
  like a .proto source file, not a compiled descriptor set", with the `protoc` command to compile
  it), and a `Service/Method` the set does not declare (naming the services the set does declare).
  The well-known Protocol Buffers (Protobuf) types — `Timestamp`, `Duration`, the wrapper types,
  `Struct`/`Value`/`ListValue`, `FieldMask`, `Empty`, and `Any` — render and are accepted in their
  canonical JSON forms identically through this path, because that handling keys off the descriptor's
  full type name, not off where the descriptor came from; a test round-trips a `Timestamp` and a
  `Duration` through `--protoset` against a reflection-disabled server to pin exactly that.

### Changed
- **The reflection-unavailable error was corrected, because this release made the old wording false.**
  It used to end "supplying a compiled descriptor set instead is not available in this version"; it
  now reads, verbatim: "The server does not implement gRPC server reflection
  (grpc.reflection.v1alpha.ServerReflection), so certapi grpc cannot learn the service's request and
  response message types by asking it. Supply a compiled descriptor set instead: --protoset <file>,
  produced by protoc --descriptor_set_out=<file> --include_imports <proto>."
- **The desktop application's Help window gRPC note was corrected to match**: it used to say "there's
  no way to supply a compiled descriptor set instead" for a server with reflection turned off; it now
  says that server can still be reached by supplying a compiled descriptor set with `--protoset`.
- **The documented gRPC limits were rewritten, not merely trimmed.** "Server reflection is required
  (there's no descriptor-set input in this version)" is no longer true and is gone from `README.md`,
  `docs/index.html`, and `wiki/21-CLI-Reference.md`; what remains stated as a limit is what is still
  actually true — client-streaming and bidirectional methods are still out of scope, `certapi serve`
  still does not proxy gRPC (`HttpListener` is HTTP/1.1-only), and `--protoset` still requires you to
  already have, or be able to produce, the descriptor set yourself — certapi does not compile `.proto`
  sources.

## [1.57.0] - 2026-07-26

### Changed
- **`ApiClient` now pools and reuses HTTP connections instead of opening a fresh one for every
  send.** It used to construct a brand-new `SocketsHttpHandler` and `HttpClient` inside every
  `SendAsync` call, so a 20-request suite performed 20 TLS (Transport Layer Security) handshakes
  and no connection was ever reused. That was never an oversight left for later — the handshake
  diagnostics (negotiated TLS protocol, cipher suite, whether the client certificate was presented,
  the server certificate and chain) were captured into closure variables created fresh for each
  call, which bound the handler carrying the connect callback to that one call; sharing a handler
  across calls would have meant one call's diagnostics silently overwriting another's. This release
  moves the capture from *per send* to *per connection*: a connect callback keyed by origin now
  runs once, when a connection is established, rather than once for every request that happens to
  use it, so the handler no longer has to be rebuilt to keep the diagnostics honest. `ApiClient` now
  owns a bounded (capacity 8) Least Recently Used (LRU) cache of handlers, keyed by everything baked
  into a handler — the client certificate (by SHA-256 hash, plus whether it carries a private key),
  proxy configuration, `IgnoreServerCertificateErrors`, the trust callback's identity (via
  `TrustPredicates`, which now memoizes one delegate instance per host — the reason repeated
  requests to the same host can be recognised as reusable at all), decompression, HTTP version,
  `--resolve` overrides, and Windows-auth credentials. Two requests share a connection only when
  every one of those matches exactly; the key is deliberately conservative, because a wrong answer
  here — reusing a connection across two different client certificates, say — would be a security
  defect, not a performance bug. A request never reuses a connection established with a different
  client certificate or a different trust policy. A request routed through a proxy still opens its
  own connection and is never pooled: the CONNECT tunnel's TLS is established by the handler itself,
  through a handler-wide callback that can't be attributed to the one connection it belongs to, so
  there is nothing safe to key a shared handler on for that path. Proved by test, not asserted: two
  equivalent sends to one origin through one `ApiClient` cause the server to complete exactly one
  TLS handshake while answering both requests; a `certapi run` suite of two requests against one
  host now opens one connection instead of two; a second request over a pooled connection still
  presents the client certificate (verified server-side); and two different client certificates, two
  different trust policies, two different sets of Windows-auth credentials, or two different
  decompression settings each open their own connection rather than share one.
- **What `ConnectionInfo` reports changed, on purpose, and it's user-visible.** With real pooling, a
  second request over an existing connection performs no handshake, so there is nothing fresh to
  report. `ConnectionInfo` now describes *the handshake that established the connection currently in
  use*, not a handshake that just happened for this particular request — a second send to an
  already-pooled origin shows the same negotiated protocol, cipher suite, and
  client-certificate-presented value as the first, because that is what is actually true of the
  connection carrying it, not because nothing was captured this time. This is more truthful than the
  old behavior, which only ever showed fresh handshake data because it forced a fresh handshake on
  every single request whether or not one was needed. Diagnostics are now a property of the
  connection rather than of the individual request; the app's Diagnostics tab and `certapi bench`'s
  own caveat are updated to say so, in `README.md`, `wiki/06-Certificates-and-mTLS.md`, and the
  in-app Help window.
- **Cookies moved off the handler to per-send handling**, so one caller's cookie jar can never leak
  into another caller's request by way of a handler now shared across sends.
- **`ApiClient` is now `IDisposable`.** The app disposes its instance when the main window closes,
  and `Bench` and `SelfTestRunner` dispose the ones they create — the handler cache a long-lived
  instance now holds is a real resource, not a stateless helper.
- **The bench caveat changed to match.** `certapi bench`'s old caveat — "each request opens its own
  connection, so these figures include connection setup" — is no longer true and is replaced
  everywhere it appeared (the CLI summary, the `--json` envelope, `certapi bench --help`,
  `Bench.RunAsync`'s remarks, the app's Help window, `wiki/21-CLI-Reference.md`, `README.md`, and
  `docs/index.html`): connections are pooled and reused, so only the first request to an origin pays
  the TCP connect and TLS handshake, `--warmup` exists to discard exactly that cost, and a request
  routed through a proxy still opens its own connection every time.

## [1.56.0] - 2026-07-26

### Added
- **`certapi grpc call` renders and accepts the well-known Protobuf types in their canonical
  JavaScript Object Notation (JSON) forms.** Google.Protobuf for C# has no `DynamicMessage`: a
  descriptor built at runtime from server-reflection bytes has no compiled .NET type behind it,
  so Google's own `JsonFormatter`/`JsonParser` cannot operate on it, which is why `certapi grpc`
  carries its own hand-rolled, descriptor-driven converter (`ProtoJsonWriter`/`ProtoJsonReader`
  in `ApiTester.Grpc`). Until now that converter treated every well-known type as an ordinary
  nested message; this release implements the canonical mapping from the Protocol Buffers
  (Protobuf) JSON specification (protobuf.dev, "ProtoJSON Format"), in both directions — a
  response renders in canonical form, and a request body supplied in canonical form encodes
  correctly onto the wire. `Timestamp` is an RFC 3339 string, always UTC `Z`, with 0, 3, 6, or 9
  fractional digits (`"2023-11-14T22:13:20.5Z"`), and accepts a numeric UTC offset on input and
  converts it; `Duration` is seconds with an `s` suffix (`"1.500s"`, `"-3s"`); the nine wrapper
  types (`DoubleValue`, `FloatValue`, `Int64Value`, `UInt64Value`, `Int32Value`, `UInt32Value`,
  `BoolValue`, `StringValue`, `BytesValue`) render as the bare underlying value —
  `Int64Value`/`UInt64Value` as a JSON string per the existing 64-bit-integer rule, `BytesValue`
  as base64 — and a wrapper holding its type's default still renders as that default (`0`, `""`,
  `false`), which is what distinguishes a present-but-default wrapper from an absent one;
  `Struct` is a plain JSON object, `Value` is any JSON value including `null`, and `ListValue` is
  a JSON array; `FieldMask` is comma-joined lowerCamelCase paths (`"user.displayName,photo"`),
  converted to and from the wire's snake_case; and `Empty` is `{}`. `Any` renders as an object
  with `"@type"` plus the payload's fields inlined, or the payload nested under `"value"` instead
  when it is itself a well-known type — and the expansion is best-effort: when the type URL
  resolves against the descriptors reflection fetched, the payload is expanded; when it doesn't,
  or its bytes don't decode, the field degrades to `{"@type":…,"value":"<base64>"}` rather than
  failing the call, so one unresolvable `Any` never sinks a whole response. Correctness is proved
  differentially rather than against hand-written expectations: the tests compare our writer's
  output against Google's own `JsonFormatter`, and our reader's encoding against `JsonParser`,
  over descriptors rebuilt at runtime from reflection bytes — the real production condition.
  Malformed input is a clean data error, not a crash: an unparseable timestamp, duration, or
  field mask in a request body exits 3 naming the offending field, for example `'all.fTimestamp':
  'not-a-time' is not a valid RFC 3339 timestamp (google.protobuf.Timestamp expects e.g.
  "2023-11-14T22:13:20Z").`

### Changed
- **This changes the shape of `certapi grpc call`'s output — a script parsing the old form will
  see something different.** A `google.protobuf.Timestamp` that rendered as
  `{"seconds":1700000000,"nanos":0}` now renders as `"2023-11-14T22:13:20Z"`; a `StringValue`
  that rendered as `{"value":"x"}` now renders as `"x"`; a `Duration` that rendered as
  `{"seconds":1,"nanos":500000000}` now renders as `"1.500s"`. The request side gains the same
  forms: a body may now supply these canonical shapes, and the old object forms are still
  accepted for backward compatibility — except for `Struct` and `Value`, where a plain JSON
  object is itself the canonical form, so `{"fStruct":{"fields":{...}}}` is now read as a
  `Struct` whose one field is literally named `"fields"`, not as the old object-wrapped shape.
  That exception isn't a shortcut taken here: it is exactly what Google's own `JsonParser` does
  for these two types, and it is worth stating plainly rather than leaving it to be discovered by
  surprise.

## [1.55.1] - 2026-07-26

### Fixed
- **Exporting a request escaped its `{{variable}}` tokens.** The collections export path and the
  app's **Copy as ▾** snippets (cURL, PowerShell, Python, C#) composed the URL through the same
  escaping query composer the wire path uses, so a saved parameter value of `{{tok}}` exported
  as `a=%7B%7Btok%7D%7D` — a token the active environment couldn't resolve arrived percent-escaped
  instead of literal, so a snippet copied for later use ran the wrong request the moment it was
  pasted and re-run. Exports now keep `{{…}}` sequences verbatim while escaping everything else
  exactly as before; a token the environment *can* resolve is still substituted first and its value
  escaped normally (resolve-then-escape, unchanged), and a value mixing a token with reserved
  characters, `{{tok}}&x=1`, exports as `{{tok}}%26x%3D1`, so the raw `&` can't split it into two
  parameters. This is the opposite-facing rule from the v1.54.0 fix in this changelog: there, an
  unresolved token had to resolve correctly before reaching the wire; here, a token that legitimately
  stays unresolved has to survive an export unescaped. The wire path — `EffectiveUrl`,
  `RequestUrl.Effective`, and the escaping `QueryString.Build`/`Compose` — is deliberately untouched,
  because a request actually being sent has no template to preserve, only a value to escape
  correctly. The emitted OpenAPI document was unaffected either way — it already percent-decodes the
  composed query before writing it — and that is now pinned by a test.
- **`certapi mock --http` was documented but not accepted.** `MockCommand.Help` has always documented
  `--http` ("Plain HTTP (default) — hit it with anything, no certificates"), but `MockCommand.Run`
  only consumed `--tls` and `--mtls`, so the leftover token reached the argument parser's
  unknown-option check and `certapi mock --http` failed with `Unknown option '--http'.` and exit code
  2 — following the tool's own documentation produced an error. `--http` is now consumed as an
  explicit selector for the default plain-HTTP mode, a no-op that resolves to the same mode as
  passing nothing, so a script can state the mode it wants instead of relying on the default by
  omission. The exclusivity check is now three-way: `--http`, `--tls`, and `--mtls` are mutually
  exclusive, and combining any two is a usage error (exit 2, `--http, --tls, and --mtls are mutually
  exclusive.`). Nothing else about the mock server changed.
- **Bench exit-code wording in the docs overstated its leniency.** `README.md` said `certapi bench`
  "exits 0 whatever the failure rate" and `docs/index.html` said it "exits 0 whatever the numbers
  say" — both omitted the one case it doesn't cover. The rule enforced by `BenchCommand.Run`,
  unchanged by this release, is: exit 0 whenever at least one request got a response, however bad —
  503s and 404s included — and exit 1 only when nothing answered at all. Both pages now state the
  exit-1 case explicitly. `wiki/21-CLI-Reference.md` and the in-app Help window (`HelpWindow.xaml.cs`)
  were checked against the same rule and were already correct, so neither was touched. The command's
  behavior did not change in this release — only the prose describing it did.

## [1.55.0] - 2026-07-26

### Added
- **gRPC calls, unary and server-streaming** — `certapi grpc` reaches a gRPC service (HTTP/2) that
  requires a client certificate, using the same Windows-store certificate handling as the rest of
  certapi, so this is the tool to reach for over `grpcurl` whenever the service sits behind mutual
  TLS. `certapi grpc list <address> --cert "CN=My Client"` discovers the services and methods a
  server advertises via server reflection (`grpc.reflection.v1alpha.ServerReflection`), printed
  indented under their service with `stream` marking a streaming request or response (`--json` for
  an array instead). `certapi grpc call <address> <Service/Method> -d '<json>'` invokes one —
  request bodies are supplied as JSON (`-d`/`--data`, default `{}`; or `--data-file <path>`),
  metadata as repeatable `-H "k: v"` headers, and the response prints back as indented JSON to
  stdout; a server-streaming method instead prints one compact JSON object per line as each message
  arrives, so it pipes straight into a line-oriented consumer, and `--max-messages <n>` stops it
  early (a clean exit 0, not a failure). A short service name resolves when it's unambiguous —
  `Echo/Unary` finds `certapi.test.Echo` — so you don't have to type the fully-qualified name every
  time. A host already pinned with `certapi trust add` is reached without `--insecure`, exactly as
  `certapi send` already allows, and a bearer token captured by an earlier `certapi send` to the
  same host is attached automatically as metadata (`--no-auto-token` opts out) — though `certapi
  grpc` never captures a *new* token itself. The proxy switches (`--proxy`/`--no-proxy`/
  `--proxy-user`) and `--insecure` apply to the channel; `--timeout <seconds>` defaults to 100.
  Exit codes follow the rest of certapi: 0 on success (including a stream stopped early by
  `--max-messages`), 1 when the gRPC status returned is not OK (`--json` carries `{status,
  statusName, detail}`), 2 on a bad command line (an address whose scheme isn't `http`/`https`, a
  malformed `Service/Method`, or a client-streaming/bidirectional method — out of scope, so asking
  for one is a usage error, not a data error), and 3 on a data problem (server reflection
  unavailable, or an unknown service/method/field, naming the offending one). A failure names its
  real cause the same way `certapi send` has since v1.50.0 — a refused or untrusted certificate
  reports `The remote certificate was rejected by the provided RemoteCertificateValidationCallback`,
  a closed port reports `No connection could be made because the target machine actively refused
  it` — rather than an unhelpful bare cancellation. **The honest limits.** Server reflection is
  required; a server that doesn't implement it can't be listed or called, and this version has no
  way to supply a compiled descriptor set instead. Client-streaming and bidirectional methods are
  out of scope for this version. `certapi serve` does not proxy gRPC — `HttpListener` is
  HTTP/1.1-only — so `certapi grpc` reaches the service directly with your certificate rather than
  going through the gateway. And the well-known Protocol Buffers (Protobuf) types
  (`google.protobuf.Timestamp`, `Duration`, `Struct`, `Any`, the wrapper types) render as ordinary
  messages rather than their special-cased JavaScript Object Notation (JSON) forms — a `Timestamp`
  shows as `{"seconds":"5","nanos":0}`, not an ISO 8601 string — and are supplied the same way on
  the way in. There is no window for this: it's a command-line concern, like `bench` and `serve`.

### Changed
- **The dependency posture, stated plainly.** This release takes `Grpc.Net.Client`, `Grpc.Reflection`,
  and `Google.Protobuf` — the one place the no-new-dependencies rule bends, because hand-rolling a
  Protocol Buffers (Protobuf) wire codec and a reflection client is a large, bug-prone surface for
  something two well-maintained packages already do correctly, and getting wire encoding subtly
  wrong in a *testing* tool would produce false results, which is the worst possible failure for
  this product. The containment is precise: the packages live in a new `src/ApiTester.Grpc` project
  referenced only by the command-line client; `ApiTester.Core` still has zero package references,
  and the desktop application is unchanged — a test (`GrpcContainmentTests`) now fails the build if
  either of those assemblies ever picks up a reference to `Grpc.*` or `Google.Protobuf`. The
  repository's "no external dependencies" claim is restated rather than quietly dropped: the
  application and `certapi` remain self-contained single-file executables with no *install*
  requirements — the packages are compiled in, exactly as the WebView2 loader already is, so there
  is still no installer, no admin rights, and no runtime to add. Measured, not guessed: the
  self-contained single-file `certapi.exe` grew from 36,732,361 to 37,242,477 bytes — **+510,116
  bytes (+0.49 MB)** — smaller than the spec's original ~5–8 MB estimate because
  `EnableCompressionInSingleFile` compresses the bundle; the added assemblies total about 1.0 MB
  (1,030,904 bytes) uncompressed. Building from source now restores these packages from NuGet in
  addition to the existing ones, which matters to anyone building offline or from an internal
  mirror.

## [1.54.0] - 2026-07-26

### Added
- **Response diffing** — the move from "did it respond" to "did it respond *the same*".
  `certapi send <url> --diff <baseline>` compares the response it just got against a baseline and
  prints what changed; the baseline is an HTTP Archive (HAR) file, a `.json` response file, or the
  literal `known-good` (the stored response of the saved request that matches this method and URL).
  A `.json` baseline may be either the envelope `certapi send --json` writes or a saved response
  snapshot, so recording one with `certapi send x --json > baseline.json` and comparing against it
  with `--diff baseline.json` works — a workflow the tool's own output could not feed would be a
  trap. `--diff-fail` turns any difference into exit 1, which is the continuous integration (CI)
  form; without it the diff is reported and the exit code is unaffected, because printing a
  difference and failing a build are different decisions. `--diff-ignore <path>` and `--diff-ignore-header <name>` are both
  repeatable, and a named header is *added to* the volatile defaults rather than replacing them —
  naming one noisy header says nothing about wanting `Date` and `Set-Cookie` compared from now on.
  The diff prints to stderr even under `--quiet` (the body still owns stdout), and `--json` gains a
  `diff` object. On `certapi run`, `--diff-har <file.har>` replays an archive and diffs every
  response against the one it recorded, which is what turns a captured session into a regression
  test: an entry passes only when its response is identical to the recorded one, status included,
  and any difference exits 1. There is deliberately no `--diff-fail` there, because a difference is
  always a failure in that mode. **The honest limits.** The body comparison picks its own mode: when
  both sides parse as JSON it is structural, naming each changed leaf path in the same `a.b[0].c`
  form capture rules use (added / removed / changed / type-changed, arrays by index); when either
  side is not JSON it falls back to a one-line text summary of lines and bytes on each side; and
  when either side is binary it reports size and equality only — it never pretends to diff a PDF.
  Seven headers that change on every response are ignored by default, or a real difference would
  drown in them: `Date`, `Set-Cookie`, `ETag`, `Age`, `X-Request-Id`, `X-Correlation-Id`, and
  `Server-Timing`. An ignore path may end a segment with `*`, so `data.token*` covers
  `data.tokenId`, and naming a container (`data`) suppresses everything beneath it. In the app, a
  **Diff** tab sits with the other response views and compares against the saved request's
  known-good response, or against an archive chosen with **Compare with HAR…** (**Clear** returns
  to the known-good baseline). Known-good baselines are recorded automatically on a successful
  (2xx) send by `certapi run` and by the app, capped at 1 MiB so the settings file does not become
  a blob store. A missing or unreadable baseline is a data error (exit 3), and `known-good` with no
  stored snapshot exits 3 naming the request that needs sending once.
- **Retry and backoff** — surviving a flaky internal endpoint in CI without wrapping every call in a
  shell loop. `--retry <n>` (0, the previous behavior, is still the default) retries a failed
  request, `--retry-on <codes>` picks the statuses that earn one (default `429,502,503,504`),
  `--retry-delay <ms>` sets the first wait (default 500), and `--no-retry-transport` switches off
  retrying a request that never reached the server at all. The delay doubles on each further
  attempt with ±10% jitter — jitter so a fleet of clients that all failed at the same moment does
  not come back in one synchronized wave — capped at 30 seconds, and a `Retry-After` header (delta
  seconds or an HTTP-date) overrides the computed wait, because a server that says when to come
  back knows better than any guess. Cancellation is honored during the wait, so Ctrl+C returns
  immediately instead of after the delay. The stderr metadata line gains `N attempts` when more
  than one was needed (and a failure reads `… (3 attempts)`: "it failed" and "it failed three
  times" are different facts), and the `--json` envelope gains `attempts` when it is above 1. The
  settings are additive on the transport options and mirrored on a saved request, so a request
  carries its own; on `run` a flag overrides only what it names. The app grows a **Retries** group
  on the request editor's Transport tab — count, statuses, first delay, "Also retry POST and PATCH",
  and "Retry connection failures and timeouts". **The honest limits.** Only idempotent methods
  retry by default — GET, HEAD, OPTIONS, PUT, DELETE — because re-sending a POST nobody confirmed
  can charge a card twice; `--retry-unsafe` opts POST and PATCH in explicitly. Among transport
  failures, only the ones a second attempt could plausibly survive earn a retry: connection
  refused, connection reset, name resolution, timeouts, and proxy failures. A refused or untrusted
  client or server certificate is **never** retried — it would only fail slower — and neither is a
  redirect loop. A negative `--retry`, or a `--retry-on` code that is not a number between 100 and
  599, is a usage error (exit 2) rather than a silently dropped typo that would leave you believing
  you configured a retry you never got.
- **Request chains** — "log in, then call the API" as a first-class, saved, runnable thing instead
  of an implicit convention about the order of a collection. A chain is a name, an ordered list of
  steps (each naming a saved request, each with its own stop-on-failure, on by default), and an
  optional environment, all persisted additively on the workspace. `certapi run --chain <name>`
  runs it, reporting PASS / FAIL per step and exiting 1 if any step failed. A chain reuses the
  ordinary run path exactly — resolve variables, send, evaluate assertions, apply capture rules,
  rebuild variables — so a token captured by step 1 is available to step 2 as a `{{variable}}`, and
  known-good recording applies per step just as in a normal run; running the same way is the whole
  point of a chain. When a failing step has stop-on-failure set, the chain halts and the remaining
  steps are reported **SKIP** rather than silently vanishing, since a step that never ran is not a
  step that passed. Captures write into the environment the chain names, created on first use, and
  an explicit `--env` wins over it — a flag you typed is a more specific instruction than a stored
  default. Every step is resolved before anything is sent: an unknown chain exits 3 listing the
  chains that do exist, and a step whose saved request has been deleted exits 3 naming the step.
  `--chain` cannot be combined with `--all`, a positional, `--diff-har`, or `--data` (exit 2) — a
  chain names its own requests in its own order, and whether a data row would repeat the whole
  chain or each step within it is not defined. **The honest limit.** A **CHAINS** section joins
  HISTORY and COLLECTIONS in the app sidebar, where a chain is created, its steps picked in order,
  reordered or removed, its per-step stop-on-failure and environment set, and **Copy run command**
  puts the matching `certapi run --chain "<name>"` on the clipboard — but *running* a chain is a
  command-line concern this release. The app builds and saves them; `certapi run --chain` runs
  them. Chains travel with an exported workspace and are merged or replaced on import like
  everything else.
- **Latency and load bench** — `certapi bench <url|saved request>` answers "how fast is this
  endpoint, and does it stay up under concurrency" over the same client-certificate send path as
  everything else, so what it measures is what the rest of the tool actually does. It defaults to
  `-n 100 -c 10` and reports requests sent / succeeded / failed, elapsed, requests per second, the
  min / p50 / p90 / p99 / max latencies, and the status and error counts; `--json` emits the same as
  a machine-readable envelope for a job that tracks latency over time. `--duration <seconds>` runs
  for a wall-clock period instead of a fixed count, and `--warmup <seconds>` sends and discards
  first so the figures describe a warmed-up endpoint — warm-up requests are extra, so `-n 20` still
  measures twenty. Percentiles come from the full retained latency array rather than an
  approximation, a bench never writes state (no known-good markers, no captured tokens, no
  attached session token), and it exits 0 whenever it measured anything, whatever the statuses came
  back: it reports numbers rather than passing judgement, so an endpoint that answers 503 or 404
  every time has still been measured. Exit 1 is reserved for no request getting a response at all,
  where there is nothing to report but that the endpoint could not be reached — a CI job benching an
  endpoint whose normal answer is 401 should not be handed a failure for it. `-n` together with
  `--duration`, a concurrency of zero or less, or a concurrency greater than the count are each usage
  errors (exit 2). **The honest limits.**
  Every request opens its own connection, because the client builds a fresh handler per send in
  order to capture that request's own handshake diagnostics, so the reported latencies include the
  TCP connect and the TLS handshake. Read them as "how long one request to this endpoint takes,
  from cold", not "how fast a warm client can stream requests at it" — the command prints that note
  under every summary, carries it in the `--json` envelope, and says it in its help text rather
  than quietly letting the numbers flatter the endpoint. Retries are forced off during a bench even
  when the request or a `--retry` flag asks for them, because a retry turns a failure into a slow
  success and hides the very failure rate the bench exists to measure; `--bench-retries` measures it
  anyway. And there is deliberately **no** app user interface for the bench: it is a command-line
  concern, and adding value in the window would need a chart.

### Fixed
- **`{{variables}}` in query-parameter values never resolved.** A saved request whose parameter grid
  held `api_key={{token}}` sent the literal placeholder, percent-escaped, as
  `api_key=%7B%7Btoken%7D%7D` — the parameter values were escaped while the URL was being composed,
  and only then was the text searched for `{{…}}`, by which point the pattern no longer existed.
  Variables in the base URL, path, headers, body, and auth were unaffected. `--strict-vars` could not
  catch it either, for the same reason: nothing that looked like a variable was left to report.
  Values are now resolved *before* the query is composed, so the substituted value is what gets
  escaped — a token containing `&`, `=`, a space, or non-ASCII text is now encoded correctly rather
  than corrupting the query. Unresolved query tokens are reported like every other kind, so
  `--strict-vars` fails on them and the ordinary path lists them as unresolved.
  This mattered most to the request chains added in this release, whose whole purpose is to capture a
  token in one step and use it in the next: feeding that token as a query parameter silently sent the
  placeholder, and the endpoint answered 401 with nothing to explain why.
- **`certapi run --json` reported a URL it had not sent.** The envelope recomputed each request's URL
  from the saved model instead of carrying the one that went on the wire, so a request using a
  variable in its query was reported as `?api_key=%7B%7Btoken%7D%7D` while it had actually sent the
  resolved value. A machine-readable envelope that misreports what was sent is worse than a cosmetic
  problem — a CI log would record the wrong URL — so the sent URL is now carried through, and a test
  pins the report and the wire to each other so they cannot drift apart again.

## [1.53.0] - 2026-07-26

### Added
- **HAR → OpenAPI** — `certapi export openapi --from-har session.har -o api.json` turns a captured
  HTTP Archive (HAR) session into an OpenAPI 3.0 document, the payoff for mapping an undocumented
  internal API: `fuzz` finds the paths, a HAR capture records what a real client actually sent, and
  this turns the recording into a specification. Repeated calls to the same endpoint collapse into
  one operation — headers common to every instance are kept (varying ones dropped), the first
  non-empty request body becomes the body example, and query keys seen across instances become query
  parameters. Path templating is deliberately conservative — a wrong `{param}` is worse than a
  literal path — so a segment becomes `{id}` only when it is all digits, a Universally Unique
  Identifier (UUID), or a hexadecimal string 16 characters or longer, *and* it actually varies between
  calls; one that happens to carry the same value in every entry stays literal. Responses of 400 and
  above are skipped, and a header whose value is exactly `[redacted]` (as `--har`'s default redaction
  writes it) is never exported — the existing OpenAPI-export rule still applies too: auth becomes a
  security scheme only, never the secret value. `--host <h>` keeps only one host's entries, and
  `--no-template-ids` keeps identifier-looking segments literal. In the app, Import ▾ → **Export
  OpenAPI from HAR file…** does the same from an `.har` file picker.
- **Mock from HAR** — `certapi mock --har session.har` serves a captured session back as a fake
  backend instead of the built-in echo routes, so an application, a test suite, or a teammate's client
  can develop against a recorded API offline. Matching precedence is exact method + path + query
  first, then method + path, then `--no-match-status` (default 404) for anything that matches
  neither; repeated calls to one route replay in recorded order and then repeat the last one.
  Hop-by-hop and framing headers (`Transfer-Encoding`, `Connection`, `Content-Length`) are never
  replayed, and a recorded `Set-Cookie` replays exactly as captured — a redacted capture replays
  `[redacted]`, which is correct and visible rather than silently wrong. The built-in `/`,
  `/status/<code>`, `/sse`, `/token`, `/windows-auth`, and `/cookie-auth` routes are not served while
  replaying. In the app, the Mock server window's **From HAR…** button starts the mock in replay mode
  the same way.
- **`serve --tls`** — closes the limitation the 1.51.0 gateway shipped with. `--tls` serves the
  gateway itself over HTTPS on `127.0.0.1`, using a generated `CN=127.0.0.1` certificate (a Subject
  Alternative Name (SAN) covering `127.0.0.1` and `localhost`, cached under
  `%AppData%\CertApiTester\gateway-tls\` and reused across runs) bound to the port through the
  Windows HTTP Server API — the same mechanism `netsh http add sslcert` configures. Binding is
  machine state, so nothing happens silently: the first bind needs an elevated (Run as administrator)
  prompt, and without one `serve --tls` exits 2 printing the exact `netsh` command to run; a port
  already bound to someone else's certificate is left alone and reported by its thumbprint rather
  than clobbered. `--tls-trust` additionally installs the certificate into `CurrentUser\Root` so the
  browser accepts it silently — explicit, logged, and reversible with `--tls-untrust`. Without
  `--tls-trust` the browser just warns once, and the startup banner says which situation applies.
  With `--tls` on, the `--browser` cookie rewriter stops stripping `Secure` from `Set-Cookie`, stops
  downgrading `SameSite=None` to `Lax`, and emits no `__Host-`/`__Secure-` warning — the known
  limitation recorded in the 1.51.0 section below, that those cookies could never survive a plaintext
  `http://127.0.0.1` origin, is now closed.

## [1.52.0] - 2026-07-26

### Added
- **HTTP Archive (HAR) capture** — `--har <file>` on `certapi send`, `run`, and `fuzz` records every
  request the command performs as a well-formed HAR archive, written once at the end of the run —
  including each redirect hop as its own entry, so a chain that changed origin is visible in the
  file, not just on screen. Secret redaction is on by default: the *values* of `Authorization`,
  `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are written as `[redacted]`, so a trace is safe
  to hand to a teammate or attach to a bug report; `--har-include-secrets` opts out when you need the
  real values. In the app, Import ▾ → "Export Network trace as HAR…" writes the current tab's
  Network trace to a `.har` file with the same redact-by-default choice.
- **HAR replay, through mutual TLS** — `certapi import har <file>` turns a captured archive into a
  collection alongside `import curl`/`import openapi`, and `certapi run <file.har>` detects a `.har`
  positional directly and replays its entries as an ordered suite. The point of replay is what a
  browser's own HAR export can never do: the request is resent with a client certificate attached
  (`--cert`/`--cert-file`), so a session captured in a browser can be re-proven over mutual TLS
  without hand-rebuilding it. A HAR run never writes live state — no known-good markers, no
  captured tokens. A malformed HAR file is a one-line data error (exit 3); a well-formed HAR with
  zero entries is also exit 3, since there is nothing to replay. In the app, Import ▾ → "HAR
  file…" does the same import.
- **Per-site server-certificate trust** — a narrower alternative to the blanket "ignore server cert
  errors" toggle: `certapi trust add <host> [--thumbprint <t> | --from-url <https-url>]` pins one
  specific server-certificate thumbprint to one host, `certapi trust list` shows the pins, and
  `certapi trust remove <host> [--thumbprint <t>]` un-pins one or all of them. `--from-url` connects
  once, captures the certificate the server actually presents, and pins that thumbprint without you
  having to find it by hand. `send` and `run` consult the store automatically and note on stderr
  when a pinned certificate is what let the request through. In the app, a
  `ServerCertificateUntrusted` response offers a "Trust & retry" action, and a Trusted-certificates
  manager (Import ▾ → "Trusted certificates…") lists and removes pins. The existing blanket
  ignore-errors switch is unchanged and still available for cases pinning doesn't fit.

## [1.51.0] - 2026-07-25

### Added
- **Several upstreams behind one gateway port** — `--upstream /api=https://api.internal` mounts a host
  at a path prefix and repeats as often as you need, so a single `certapi serve` can stand in for a
  whole backend: `GET /api/orders` reaches `https://api.internal/orders`, with the prefix stripped
  before the request is forwarded. The longest matching prefix wins, a path under no prefix is a 404
  that contacts nothing, and two upstreams mounted on the same prefix is a usage error rather than a
  silent last-one-wins. The positional `certapi serve <upstream>` form still works — it is the same
  thing written `/=<url>`.
- **A gateway a browser can call — `certapi serve --browser`** — until now the gateway was usable only
  from an application whose base URL you control, because a faithful relay hands a browser exactly the
  headers that make it refuse the response. `--browser` turns on the whole bundle below at once, and
  every part of it also works on its own. None of it is on by default: without these flags the
  gateway is the same byte-faithful relay it has always been, adding and rewriting nothing.
  - `--cors` answers Cross-Origin Resource Sharing (CORS) preflights at the gateway instead of
    forwarding them upstream, and adds the response headers a browser insists on before it will let a
    script read the reply. With no value it echoes the caller's own `Origin`; given a comma-separated
    list it allows only those origins and refuses the rest with 403. Every `Access-Control-*` header
    the upstream sent is stripped before the gateway adds its own, because two allow-origin values
    are a hard browser failure rather than something it warns about.
  - `--rewrite-cookies` re-emits each `Set-Cookie` without `Domain=` and without `Secure`, and turns
    `SameSite=None` into `Lax`, so the browser will actually store the cookie against the gateway.
    `HttpOnly`, `Path`, `Max-Age`, and `Expires` pass through untouched.
  - `--rewrite-location` points a 3xx `Location` aimed at the upstream back at the gateway, so the
    browser stays on the local origin. One aimed anywhere else is relayed exactly as written and
    logged as a warning: that hop leaves the gateway, and your client certificate with it.
  - `--allow-upgrade` relays WebSocket connections through to the upstream with your client
    certificate attached, passing `Sec-WebSocket-Protocol` on so subprotocol negotiation still works.
- **Known limitation of `--rewrite-cookies`** — a cookie named `__Host-…` or `__Secure-…` requires the
  `Secure` attribute by specification, and no browser accepts `Secure` on a plaintext
  `http://127.0.0.1` origin, so such a cookie cannot be made to work through the gateway however it is
  rewritten. It is still passed on to the browser and named in a warning rather than dropped behind
  your back. Serving the gateway over HTTPS — a future `serve --tls` — is the real fix, and it does
  not exist yet.
- **Transport control on the gateway** — `certapi serve` now takes the transport flags the other
  commands gained in 1.50.0. `--proxy` / `--proxy-user` / `--no-proxy` and `--resolve` change how the
  gateway reaches an upstream; the redirect, decompression, and HTTP-version flags are accepted but do
  not apply, because the gateway always hands back the 3xx and the exact bytes the upstream sent.

## [1.50.0] - 2026-07-25

### Added
- **Proxy control** — send through a proxy you name instead of whatever the machine happens to be
  configured to use: `--proxy http://proxy.corp:8080` on `send`, `run`, and `fuzz` (with
  `--proxy-user user:pass` when it authenticates), or `--no-proxy` to ignore the configured proxy
  entirely, including an automatically detected or script-configured one. In the app, a new
  **Transport** tab on the request editor sets the same per request, and the Diagnostics view now
  names the proxy that was actually used.
- **Visible redirects** — 3xx hops are followed by the client itself and reported, so you can see
  where a request really ended up: `--show-redirects` prints the chain, `--json` includes it, and the
  app lists each hop as a row in the Network trace. `--no-redirect` stops at the 3xx,
  `--max-redirs <n>` changes the limit (default 20), and exceeding it is a clear "too many redirects"
  error carrying the hops collected so far. Each hop flags the two things that matter when a client
  certificate is in play — the `Authorization` header being dropped on a cross-origin hop, and a
  downgrade from `https` to `http` — because a redirect to another host means your certificate is
  about to be presented somewhere you didn't choose.
- **Pin a hostname to an address** — `--resolve <host>:<port>:<ip>` (repeatable) dials the address you
  give while still sending the original hostname in Server Name Indication (SNI) and the `Host`
  header, so you can test one node behind a load balancer, or verify a Domain Name System (DNS)
  cutover before it happens. `certapi send --all-ips` does the sweep for you: one send per address the
  host resolves to, with a per-address table of status, elapsed time, and body length. Pinning needs a
  direct connection, so `--resolve` together with an active proxy is a usage error rather than a
  silent no-op.
- **HTTP version pinning** — `--http1.1` or `--http2` forces the Hypertext Transfer Protocol (HTTP)
  version instead of negotiating it, which settles "is it the protocol or the endpoint?" quickly.
  Also available per request in the app's Transport tab.
- **The real reason a connection failed** — a transport error now carries the underlying exception
  chain and socket error code instead of collapsing to a single line. That chain is where the
  SChannel status for a refused client certificate actually lives, and it was previously discarded.
  `--debug` prints it, `--json` gains a structured `error` object (kind, message, detail), and the
  app's Diagnostics panel renders it. Exit codes are unchanged.

### Changed
- **Compressed responses are decoded automatically** — the client now sends `Accept-Encoding` and
  decompresses the response, matching every other HTTP client. This is a behavior change: if you
  relied on receiving the raw compressed bytes — to relay them byte-for-byte, say — add
  `--no-decompress` to get the previous behavior back.
- **A proxied connection still reports no handshake detail** — driving the handshake by hand would
  break proxy tunnelling, so the Transport Layer Security (TLS) version, the cipher, and whether your
  client certificate was presented are all blank whenever a proxy is in play. That was already true;
  what's new is the way out — `--no-proxy` bypasses the proxy *and* restores the full connection
  diagnostics.

## [1.49.0] - 2026-07-18

### Added
- **Ad-hoc assertions on `certapi send`** — `--assert "<expr>"` (repeatable) checks the response and
  exits 1 if it fails, so a single `send` is a CI smoke test without a saved workspace. Expressions:
  `status == 200`, `status < 300`, `time < 500`, `header <name> contains <v>`, `body <jsonpath> exists`,
  `body-text matches <regex>`; operators `== != contains matches exists !exists < >`. Reuses the same
  evaluator behind the GUI Tests tab and `certapi run`.

## [1.48.0] - 2026-07-18

### Changed
- **Professional starter wordlist** — the built-in endpoint list for `certapi fuzz` and the Discover
  window is now a curated ~270-entry list organized by category — operational/health, version & build,
  metrics (including Spring Actuator), API roots & versioning, API docs & specs, authentication &
  identity, users/accounts, admin, access control, common business resources, search, files/media,
  configuration, jobs/events, logs/audit, debug/dev, commonly-exposed sensitive paths
  (`.env`, `.git/config`, backups, …), and framework-specific paths — up from ~47. Key POST-only
  endpoints (login, token, register, graphql, …) are method-pinned. It's still a fast first pass; bring
  your own larger list with `-w <file>` for a thorough sweep.

## [1.47.2] - 2026-07-18

### Fixed
- **Maximize button glyph** — the title-bar maximize/restore button went blank after the window was
  first maximized or restored (its glyph was cleared on the window-state change). It now shows the
  correct maximize and restore icons.

## [1.47.1] - 2026-07-18

### Fixed
- **Session capture — address scheme** — a URL typed into the capture browser without a scheme is no
  longer forced to `https://`. Loopback/`localhost` hosts default to `http://`, so a local mock or dev
  server is reachable; other hosts still default to `https://`.
- **Session capture — visible errors** — a failed resource fetch in the capture browser now shows the
  error in the panel instead of leaving a blank page, and the window only auto-navigates to a genuine
  absolute URL.

### Changed
- **Mock route listing** — the in-app Help now lists the `/cookie-auth` route alongside the others
  (the README, Pages site, and handbook were already updated).

## [1.47.0] - 2026-07-18

### Added
- **Session capture** — a *Capture session…* button (next to *Mock server…*) opens a browser you can
  log in through; it presents your client certificate on every request, captures the resulting session
  **cookies** (including HttpOnly) and any **bearer token**, and scopes them per website. Captured
  sessions attach automatically to later requests — in the app and headless via `certapi send` /
  `certapi run`. Your password is typed into the site's own form and is never seen or stored. The
  finish step can also save the API calls observed during login as a ready-to-run collection.
- **Session chip controls** — the status-bar chip now shows captured cookies as well as tokens, with
  menu items to clear cookies for a site and to toggle *Automatically use captured cookies*.
- **Mock `/cookie-auth` route** — the local test server sets a session cookie and then reports the
  request as authenticated once the cookie comes back, so session capture can be exercised end to end.

## [1.46.1] - 2026-07-18

### Fixed
- **Theme toggle repaints the response** — switching themes now re-highlights whatever the Pretty view
  is showing. Previously the self-test detail (and anything else written outside a normal response)
  kept the old palette's colors — nearly unreadable pale-on-white after a switch to light.
- **Mock route list includes `/windows-auth`** — the route existed but was missing from the GUI mock
  console's routes line, the in-app Help, `certapi mock --help`, and the startup routes message.
- **"stopped" no longer shows in green** — the mock console's status line is muted while stopped and
  accent-green only while the server is live.

### Changed
- **Consistent window chrome** — every secondary window now uses the same title treatment (the accent
  `❯` glyph + bold title) and the same borderless caption close button. Previously the Mock server,
  Live stream, and OAuth windows had a small boxed close button, and the Environments, Discover, and
  pop-out windows were missing the brand glyph.

## [1.46.0] - 2026-07-18

### Added
- **Windows Integrated Authentication (Negotiate/NTLM)** — for internal sites that authenticate with
  your Windows identity. A new *Windows (integrated)* auth type uses your signed-in account for single
  sign-on by default, or explicit `DOMAIN\user` + password. Headless: `certapi send --windows-auth`
  (aliases `--ntlm` / `--negotiate`), or `--windows-user DOMAIN\user --windows-password …`. Saved
  requests carry it through `certapi run`. The handler negotiates Kerberos or NTLM automatically.
- **Mock `/windows-auth` route** — the local test server now emulates a Windows-auth-protected endpoint
  (challenges with `401 WWW-Authenticate: NTLM`, then accepts the handshake), so you can try your
  Windows-auth setup end to end against `certapi mock`.

## [1.45.0] - 2026-07-18

### Fixed
- **Assertion regex is bounded** — a `Matches` assertion runs a user-supplied pattern against the
  whole response body, so it now has a 2-second match timeout: a catastrophic-backtracking pattern
  fails the assertion instead of hanging `certapi run` or the GUI Tests tab.

### Changed
- **Releases include the full source** — each release now attaches `certapi-source-<tag>.zip`, a
  clean archive of the entire repository at that tag (all tracked files, no build output).

## [1.44.0] - 2026-07-18

### Added
- **Mock server in the app** — a *Mock server…* button (next to Run Self-Test) opens a console that
  starts and stops the local test server over plain HTTP, HTTPS, or mutual TLS, shows its base URL and
  routes, and logs each request as it arrives. For the TLS modes it generates the certificates and
  offers an *Open certs* shortcut; *Copy URL* drops the address straight into a request. Same server
  as `certapi mock` — now discoverable in the GUI.

## [1.43.0] - 2026-07-18

### Added
- **`certapi mock` — a standing local test server** to fire requests at (the persistent counterpart
  to `selftest`). It echoes each request back as JSON (method, path, query, headers, body, and, under
  mTLS, the client certificate you presented) and serves fixed routes: `/status/<code>`, `/sse`
  (a `text/event-stream`), `/token` (an OAuth 2.0 token), and a WebSocket echo on any path. Runs over
  plain HTTP (`--http`, default), HTTPS (`--tls`), or mutual TLS (`--mtls`, which requires and accepts
  any client certificate); `--tls`/`--mtls` write the generated server cert and a ready-to-use client
  `.pfx` to `--cert-dir`. So the app can be pointed at itself — `certapi send`, `sse`, `ws`, and
  `token` all work against it end to end.

## [1.42.0] - 2026-07-18

### Fixed
- **Stream console no longer leaks a socket per connection** — each WebSocket session is now disposed
  when it ends (and its cancellation source reused cleanly), so repeated connect/disconnect cycles
  don't accumulate `ClientWebSocket` handles.
- **`certapi token --save --workspace <file>`** now creates the workspace file when it doesn't exist
  yet, instead of warning and skipping the save.
- **`certapi ws --expect 0`** returns after sending instead of waiting for a reply that isn't coming.
- **OAuth token requests** report a clean "timed out" instead of surfacing a raw cancellation when the
  token endpoint is slow.

## [1.41.0] - 2026-07-18

### Added
- **OAuth 2.0 token acquisition** — fetch access tokens without leaving the app. A *Get OAuth 2.0
  token…* button on the Auth tab runs the client-credentials, password, and refresh-token grants
  directly, and the authorization-code grant interactively (opens your browser, catches the redirect
  on a loopback port, with PKCE). The token is stored for the API's host (so Auto auth attaches it)
  and dropped into the Bearer field. The token endpoint itself can require mTLS.
- **`certapi token`** — the same headless: `certapi token --token-url <url> --client-id … --client-secret …`
  for client-credentials (or `--grant password|refresh`), with `--save --for <api-url>` to store the
  token for later `certapi send` calls, `--client-auth basic`, `--scope`, `--param k=v`, and `--json`.

## [1.40.0] - 2026-07-18

### Fixed
- **Light theme now applies everywhere** — colours that were hardcoded for the dark palette and
  didn't follow a theme switch now do: the primary-button hover/pressed states, selected-row and
  secondary-button highlights, JSON/XML syntax highlighting (deeper, readable hues on white), the
  endpoint-discovery result colours, and the rendered-view error page. Toggling the theme also
  re-highlights the currently shown response.

### Changed
- **Stream console shows the live mode** — as you type the URL, the header indicates whether it will
  open a WebSocket (ws/wss) or stream Server-Sent Events (http/https).

## [1.39.0] - 2026-07-18

### Added
- **Live streaming (WebSocket & Server-Sent Events)** — a new *Stream* button on the request line
  opens a console that connects to a `ws://`/`wss://` endpoint (send messages, watch replies) or an
  `http(s)` `text/event-stream` endpoint (watch events arrive), using the selected client certificate
  and the insecure toggle. Two matching CLI commands: `certapi ws <url>` (send `--message`/stdin
  lines, print replies, `--expect <n>` for scripts) and `certapi sse <url>` (`--max-events`, `--json`
  ndjson output). Both honour `--cert`/`--cert-file`, `--insecure`, and custom `-H` headers.

## [1.38.0] - 2026-07-18

### Added
- **Light theme** — the Terminal Workbench palette now ships in light as well as dark. A sun/moon
  button in the title bar toggles between them; the choice is saved and restored on the next launch,
  and it applies live to the whole app (including the native window caption) without a restart.

## [1.37.0] - 2026-07-18

### Added
- **GraphQL** — `certapi send <url> --graphql "<query>" --gql-variables '{"id":1}'` posts a
  correctly-formed GraphQL request (JSON `{ query, variables }`, application/json, POST), with the
  query escaped and the variables validated.

## [1.36.0] - 2026-07-18

### Added
- **Find in response** — a search box above the response finds and selects the next match in the
  body (Enter for next, wrapping around), so you can locate a value in a large payload quickly.

## [1.35.0] - 2026-07-18

### Added
- **Session cookies** — the app now keeps a cookie jar for the session (like a browser), so a
  `Set-Cookie` in any response is sent back on later requests to that host; cookie-based logins
  carry across sends. Headless, add `--cookies` to `certapi run` to share a jar across a suite.

## [1.34.0] - 2026-07-18

### Added
- **Data-driven runs** — `certapi run <path> --data <file.csv|.json>` repeats the request(s) once
  per row of a dataset, each row's columns overriding `{{variables}}`. Results are labelled
  `[row N]`; combine with assertions to table-test an endpoint across many inputs in one command.

## [1.33.0] - 2026-07-18

### Added
- **Multipart body editor in the app** — the **Body** tab now has a **Form data (multipart)** mode:
  add fields, tick **File** to upload a file (with a file picker), and send. Multipart requests save
  into collections and run in suites like any other, so `certapi run` can exercise upload endpoints
  too. (The `certapi send -F` command line shipped in 1.31.0.)

## [1.32.0] - 2026-07-18

### Added
- **Copy as code** — the response toolbar's cURL button is now **Copy as ▾**: turn the current
  request into a snippet as cURL, PowerShell (`Invoke-RestMethod`), Python (`requests`), or C#
  (`HttpClient`), with `{{variables}}` resolved and headers and body included.

## [1.31.0] - 2026-07-18

### Added
- **File uploads (multipart/form-data)** — `certapi send -F "field=value" -F "file=@path"` posts a
  multipart form and uploads files (curl-style: `-F` is repeatable, implies POST, and
  `name=@path;type=<ct>` sets a part's content type; mutually exclusive with `-d`). Note: the CLI
  supports this today; a multipart editor in the app's Body tab is planned.

## [1.30.0] - 2026-07-18

### Added
- **Client certificate from a file** — use a certificate that isn't in the Windows store: load a
  `.pfx`/`.p12` (with an optional password) or a `.pem`/`.crt` (key in the same file or a separate
  one). In the app, **From file…** on the certificate row loads it for the session; headless, use
  `--cert-file` with `--cert-password` / `--key-file` on `send`, `fuzz`, `serve`, and `mcp`.

### Fixed
- The in-app Help was updated for the current UI: the request now has six tabs (Params, Headers,
  Body, Auth, Capture, Tests) and the command-line section documents `certapi fuzz`.

## [1.29.0] - 2026-07-18

### Added
- **Response assertions (tests)** — a **Tests** tab on any request lets you assert on the response:
  the **status**, **response time**, a **header**, a **JSON body path**, or the **body text**, with
  `==` / `!=` / contains / matches (regex) / exists / absent / `<` / `>`. `certapi run` now passes a
  request only when its assertions all pass — a request with no assertions still passes on any 2xx,
  so a collection becomes a real pass/fail test suite. Failed assertions are listed on stderr and in
  the `--json` output; the app shows a `✓ tests 3/3 passed` summary with the detail in Diagnostics.

## [1.28.1] - 2026-07-18

### Changed
- Clearer feedback while a request is in flight: the **Send** button now reads "Sending…", a slim
  indeterminate progress bar appears across the top of the response area, and the response views
  show "Waiting for response…" — so a previous response can't be mistaken for the new one.

## [1.28.0] - 2026-07-18

### Added
- **Built-in endpoint wordlist** — `certapi fuzz <base-url>` now works with no `-w`: it falls back
  to a built-in starter list of common endpoints (health, version, auth, admin, users, …), and the
  Discover window gains a **Use built-in list** button. Supply your own list with `-w` for a
  thorough sweep — the built-in list is only a quick look. The starter list also ships as
  `wordlists/common-api-endpoints.txt` and as a release asset.

### Fixed
- Endpoint discovery no longer drops a query string on a wordlist entry (e.g. `/search?q=1` used to
  be probed as `/search`).
- Saving discovered endpoints to a collection no longer reverts unsaved top-level collection changes
  made earlier in the same session.

## [1.27.0] - 2026-07-18

### Added
- **Endpoint discovery (fuzzing)** — point the tool at a website and a wordlist of candidate
  endpoints and it probes each one to show which exist. In the app, the new **Discover…** window
  streams colour-coded results (Found / Unauthorized / MethodNotAllowed / …), hides 404s and
  errors by default, opens any hit in a request tab on double-click, and can save all discoveries
  as a collection. Headless: `certapi fuzz <base-url> -w <wordlist>` with `--methods`,
  `--concurrency`, `--delay`, status `--match`/`--hide`/`--all`, `--json`, `-o`, `-w -` (stdin),
  and `--save-collection`. Captured auth tokens are attached automatically, so you can log in
  first and then discover authenticated endpoints. A starter wordlist ships in
  `wordlists/common-api-endpoints.txt`.

## [1.26.0] - 2026-07-17

### Added
- **Automatic bearer tokens** — a token returned by any response (`access_token`, `id_token`,
  `token`, `accessToken`, `jwt`, or an `X-Auth-Token`/`X-Access-Token` header) is captured with
  zero setup and scoped to the website it came from. Requests with the new **Auto** auth mode
  (the default) attach it automatically; explicit auth is never overridden, tokens never cross
  hosts, and expired tokens are never sent. Works in the app (with a status-bar token chip to
  inspect, clear, or disable), in `certapi send`/`run` (`--no-auto-token` to opt out), and in
  the MCP server (per-session store, so agent login flows chain naturally). Tokens persist in
  the workspace in plain text, like existing auth secrets.
- **Collection defaults** — a collection or folder can hold a default website and client
  certificate ("Set website & certificate…" on right-click, or auto-remembered from the first
  successful send). Endpoints opened from a collection fill their blanks from the nearest
  folder default or the active tab — no more re-picking the website and cert for every endpoint.
- **`--debug` and `--log-file <path>`** on every certapi command: resolved URLs, sent headers
  (Authorization masked), certificate lookup, TLS details, timings, and full stack traces on
  stderr and/or appended to a log file.
- **Examples in every help screen** — `certapi help <command>` now shows realistic, copy-paste
  command examples, including login-then-call token flows and CI patterns.

### Changed
- Requests saved with auth **None** by earlier versions are treated as **Auto** (that value
  used to mean "nothing configured"); the new explicit **None (never send auth)** is preserved.
  State files are stamped with a schema version so the migration runs exactly once.

## [1.25.0] - 2026-07-16

### Added
- **Capture & reuse auth tokens** — a request can now save a value from its response into an
  environment `{{variable}}`: a JSON body field (a dotted path like `data.access_token`) or a
  response header. Call an auth endpoint once and the token is stored (in the active environment,
  or a new **Captured** one); reuse it as `Authorization: Bearer {{token}}` on later requests with
  no copy-paste. Available in the app (a new **Capture** tab on each request) and headless
  (`certapi send --capture token=access_token`, and saved requests' rules are applied by
  `certapi run`).

### Changed
- The README now includes a task-oriented **Using it** guide covering sending requests,
  certificates, collections, environments, token capture, import/export, and the `certapi`
  command line, gateway, and MCP server.

## [1.24.0] - 2026-07-16

### Added
- **MCP server for AI agents** — `certapi mcp` speaks the Model Context Protocol (JSON-RPC over
  stdio) so an AI agent can use certapi as a tool: `send_request` (an mTLS call with the pinned
  certificate), `list_certificates`, `list_saved`, `run_saved`, and `self_test`. The operator pins
  one certificate with `--cert` and an allowed host set with `--allow` at launch — the agent never
  chooses a certificate, and every outbound URL is checked against the allowlist before it leaves
  the machine. Nothing is exposed on the network.

## [1.23.0] - 2026-07-16

### Added
- **Local mTLS gateway** — `certapi serve <upstream> --port <n> --cert <id>` runs a loopback
  reverse proxy: point any local application's base URL at `http://127.0.0.1:<port>` and every
  request is forwarded to the certificate-protected upstream with your Windows-store client
  certificate attached, then the response is relayed back unchanged. The application needs no
  mTLS code of its own — just a different base URL. Binds to loopback only; add `--token <value>`
  to require callers to present a shared secret. Stop with Ctrl+C.

## [1.22.1] - 2026-07-16

### Fixed
- `certapi send --timeout` now rejects non-numeric or non-positive values as a usage error
  instead of silently using the default.
- `certapi --var` rejects overrides with a blank key (e.g. `" =value"`).
- Ambiguous collection paths in `certapi run` now list the matching entries (marked folder or
  request) instead of only counting them, and a saved entry with no request reports a clear error.
- The `--json` envelope merges duplicate response headers that differ only in case.
- Help text: clarified that `--store LocalMachine` searches the machine store in addition to
  your user store.

### Internal
- Widened a short timeout in a network test that could flake under parallel test load;
  computing the state-file path no longer creates the settings directory as a side effect.

## [1.22.0] - 2026-07-16

### Added
- **Headless mode** — a new `certapi.exe` (separate download) drives the tester from the
  command line and scripts: `send` one-off requests with a client certificate from the
  Windows store, `run` saved requests and whole collections as pass/fail suites (recording
  their known-good markers — automatically for the live workspace, with `--record` for exported
  workspace files), `certs`, `selftest`, and `import`/`export` for cURL, OpenAPI, and workspaces.
  Response bodies go to stdout and diagnostics to stderr, with script-friendly exit codes
  (0 success · 1 failure · 2 usage · 3 data).

## [1.21.0] - 2026-07-16

### Added
- **Pop out the whole response panel** — the pop-out button now offers two choices: open just the
  selected view in a window (as before), or detach the **entire response panel — tabs and all —**
  into its own window. With the panel detached, the request editor gets the full main window and a
  slim bar offers “Bring it back”. The detached panel stays fully live (switch tabs, filter the
  Network trace, copy or save the body from there), and closing its window docks it back.

## [1.20.0] - 2026-07-16

### Added
- **Pop-out response views** — a pop-out button above the response opens the selected view
  (Pretty, Raw, Headers, Diagnostics, Rendered, or Network) in its own window, so you can keep —
  say — the live Network trace or a Rendered page visible beside the main window while you work.
  The popped-out view stays fully live, the tab shows a “Bring it back” shortcut meanwhile, and
  closing the window returns the view to its tab.

## [1.19.0] - 2026-07-16

### Added
- **Save / load workspaces** — “Export workspace…” in the Import ▾ menu writes everything to a
  single JSON file: open tabs, collections (including each saved request's known-good result),
  environments, saved websites, and history. “Import workspace…” loads a workspace file back and
  asks whether to **Merge** it into your current workspace or **Replace** it. Use it to move
  between machines, keep named snapshots of a project, or hand a teammate a ready-to-use setup.
  Workspace files include request auth values and history, so treat them as private.

## [1.18.0] - 2026-07-16

### Added
- **Export as OpenAPI** — a new button at the bottom of the collections sidebar writes the
  selected folder (or all collections when nothing is selected) as an **OpenAPI 3.0 JSON** file.
  Folders become tags, each saved request becomes an operation with its query parameters, headers,
  and body example, the most common website becomes the server, and a request's known-good note
  (when it was last checked and what it returned) becomes the operation description. The exported
  file re-imports cleanly — into this app or any OpenAPI-aware tool.
- Authentication is exported **as a security scheme only** — bearer tokens, usernames, and
  passwords are never written to the file, so exports are safe to share.
- Importing an OpenAPI file now also picks up each operation's `description`.

## [1.17.0] - 2026-07-16

### Added
- **Known-good endpoints** — every saved request in your collections now remembers its last
  result. Open a saved request and send it: a dot appears next to its name in the tree — **mint**
  when the send returned a 2xx (known good), **red** when it failed or returned an error status —
  and hovering shows when it was last checked and what it returned. Results persist between
  sessions, and are only recorded while the tab still targets the saved endpoint (same method and
  URL), so editing a request can't mislabel the entry it came from.

### Changed
- Tooltips throughout the app now use the dark theme instead of the light system default.

## [1.16.0] - 2026-07-16

### Changed
- **Network panel polish** — the Network tab now works like a proper browser network panel:
  - **Filter bar**: a text filter (matches URL, method, status, and content type), status-class
    filters (**All / 2xx / 3xx / 4xx / 5xx / ERR**), and a **cert only** toggle that shows just the
    calls made with your client certificate. The counter shows how many rows match and their
    combined size (e.g. “9 of 12 requests · 2.1 MB”).
  - **Details pane**: clicking a row opens a structured details pane — general facts (URL, status,
    type, size, time, start time, source, client certificate) and the request/response headers —
    with a **Copy** button and a drag handle to resize it. New rows scroll into view as they arrive.
  - **Right-click a row** to copy its URL or a matching `curl` command.

### Fixed
- Text typed into the Network filter box was invisible (clipped by the input's vertical padding).
- Opening a row's details no longer squeezes the request list to nothing at small window sizes.

## [1.15.0] - 2026-07-15

### Added
- **Network trace** — a **Network** response tab that logs every HTTP call, like a browser's
  network panel: the request you sent **and** every resource the Rendered view fetches (document,
  CSS, JS, images, XHR). Each row shows method, status, type, size, timing, and a marker when it
  was fetched with your client certificate; click a row for its request/response headers and cert
  detail. Clearable, and it keeps metadata only (no response bodies).

## [1.14.1] - 2026-07-15

### Fixed
- The new-tab **+** button now renders as a crisp, correctly-weighted plus (it was using a
  full-width character that some fonts don't have, so it looked off).

## [1.14.0] - 2026-07-15

### Added
- **In-app Help** — a **?** button in the title bar (and **F1**) opens a Help & Reference window
  covering every part of the app: getting started, requests & tabs, certificates & mTLS,
  collections & history, environments & variables, importing, the rendered website view, a full
  keyboard-shortcut reference, and an About panel (version, links, privacy, license). All content
  is built in, so it works with no web access.

## [1.13.1] - 2026-07-15

### Changed
- Documentation: added a screenshots gallery to the README and the documentation site, and
  clarified that the Rendered website view uses the Microsoft Edge WebView2 runtime included
  with Windows 11.

### Internal
- Added unit tests covering the request-model and collection mapping (cURL / OpenAPI import and
  the history round-trip). The application binary is unchanged from 1.13.0.

## [1.13.0] - 2026-07-15

### Added
- **Rendered website view** — a new **Rendered** tab in the response area opens the current
  request's URL as a web page. Every resource the page loads (the document, styles, scripts,
  images, and XHR) is fetched with your selected client certificate, so a certificate-protected
  internal site renders fully authenticated — not just the HTML. It renders on demand (nothing
  loads until you open the tab) and has its own address line and Reload button. Uses the Microsoft
  Edge WebView2 runtime that ships with Windows 11; if it isn't present, the tab explains that.

## [1.12.0] - 2026-07-15

### Added
- **Import from cURL** — paste a `curl` command (Import ▸ Paste cURL command) and it opens a new
  tab with the method, URL, query parameters, headers, body, and auth filled in. Understands
  `-X`, `-H`, `-d`/`--data`, `-u` (Basic), `-k` (insecure), an `Authorization: Bearer` header
  (mapped to the Bearer helper), quoting, and line continuations.
- **Import OpenAPI / Swagger** — import a JSON OpenAPI 3.x or Swagger 2.0 file
  (Import ▸ Import OpenAPI file) to generate a collection of requests, grouped into folders by
  tag, with the server/host used as each request's website.

## [1.11.3] - 2026-07-15

### Fixed
- The environment selector in the title bar is wider so “— no environment —” is no longer clipped.
- Gave the request area (Params / Headers / Body / Auth) more room by default so the Basic auth
  password field and additional parameter/header rows are no longer cut off; the Auth panel also
  scrolls if space is tight.

## [1.11.2] - 2026-07-15

### Fixed
- The Environments and name-prompt dialogs now render their dark themed frame all the way to the
  top edge, instead of showing a light OS-drawn strip above the window.

## [1.11.1] - 2026-07-15

### Fixed
- The Environments window's close button now shows the correct “✕” glyph instead of an empty box.
- Text in the name prompt (new environment, new folder, rename, save request) is no longer clipped.

## [1.11.0] - 2026-07-15

### Added
- **Collections** — save named requests into folders and reopen them in a tab. A
  HISTORY / COLLECTIONS switch in the sidebar, a tree with the current request's method badge,
  and buttons to save the current request, add a folder, rename, and delete. Double-click a
  saved request to open it in a new tab. Collections persist between sessions.
- **Environments & variables** — define `{{variable}}` values per environment (e.g. Dev,
  Staging, Prod) and switch between them from the **ENV** selector in the title bar. Variables
  are substituted in the URL, query parameters, headers, body, and auth **when you send**;
  stored requests keep the raw `{{tokens}}`. Any token with no value is reported in the status
  line so nothing is sent silently wrong. An Environments editor manages environments and their
  key/value variables.

## [1.10.0] - 2026-07-15

### Added
- **Request tabs** — keep several requests open at once, each with its own website, method,
  parameters, headers, body, auth, certificate, and response. New tab with the `＋` button or
  Ctrl+T; close with the tab's `✕`, middle-click, or Ctrl+W. Open tabs are remembered between
  sessions.
- **Query-parameter editor** — a new Params tab with an enable/key/value grid. Typing a `?query`
  in the URL box splits it into the grid; the grid is recombined into the URL when the request is
  sent. Values are percent-encoded correctly.

## [1.9.0] - 2026-07-14

### Changed
- New application icon — a clean, bold padlock — replacing the busier previous design so it stays
  crisp and professional at small (taskbar) sizes.
- Added spacing between the website field and the Forget button so they no longer crowd.

## [1.8.0] - 2026-07-14

### Added
- Faint placeholder / example text in the input fields (website, URL, certificate filter, header
  name/value, body, and auth) and guidance hints in the empty response tabs, to help first-time use.

## [1.7.0] - 2026-07-14

### Added
- An application icon — a terminal window with a lock badge, in the app's palette — used for the
  executable, taskbar, and Alt-Tab. Sizes below 32px use a simplified bold chevron-and-lock glyph
  so it stays crisp at 16px.

## [1.6.0] - 2026-07-14

### Added
- **Saved websites** — save a base URL and the URL box becomes just the path appended to it.

### Changed
- History entries now capture the *whole* request (website, certificate, ignore-cert toggle,
  timeout, headers, auth, body) **and the response** each one returned; clicking an entry fully
  replaces the current request and restores its stored response.
- History is labelled by path (with the host beneath) instead of the start of the URL.

## [1.5.0] - 2026-07-14

### Changed
- Documentation refresh: a comprehensive README and documentation site covering every feature,
  with an application screenshot, a quick-start walkthrough, endpoints to try, and a
  keyboard-shortcut reference.

## [1.4.0] - 2026-07-14

### Added
- **Request history** — a sidebar of recent requests (persisted); click one to reload it in full.
- **Connection diagnostics** — a Diagnostics tab and status-line summary showing the negotiated TLS
  version and cipher, whether the client certificate was actually presented, and the server
  certificate (subject, issuer, thumbprint, expiry, and chain).
- **Syntax highlighting** for JSON and XML in the Pretty response view.
- **Headers editor** as an enable/disable key-value grid.
- **Auth helpers** — Bearer-token and Basic auth that generate the `Authorization` header.
- **Request Content-Type selector** for the body.
- **Timeout field** and a **Cancel** button for in-flight requests.
- **Copy body** and **Copy as cURL** buttons; **Save** now suggests a file extension from the content type.
- **Certificate filter** box for quickly finding a certificate.
- **Keyboard shortcuts**: Ctrl+Enter / Enter to send, Ctrl+L focus URL, Ctrl+S save, Ctrl+H toggle
  history, F5 refresh certificates, Esc cancel.
- **Remembers** window size/position, the last certificate, the ignore-cert toggle, and the timeout.

## [1.3.0] - 2026-07-14

### Changed
- Replace the OS title bar with a custom in-app title bar that matches the theme, with its
  own minimize / maximize / close controls. The window still drags, snaps, resizes, and
  maximizes normally.

## [1.2.0] - 2026-07-14

### Added
- Follow the machine's configured proxy — including "Automatically detect settings" (WPAD)
  and a "Use automatic configuration script" (PAC) from Internet Options — authenticating to
  it with the signed-in user's Windows credentials when required.

### Changed
- Dark window title bar on Windows 11 so the OS caption matches the app theme.

## [1.1.0] - 2026-07-14

### Added
- A "— no certificate —" option (now the default) so the app works as a general API tester
  for endpoints that don't require mutual TLS, and can test the no-certificate path of ones
  that make a client certificate optional.

## [1.0.0] - 2026-07-14

Initial release.

### Added
- Pick a client certificate from the Windows certificate store (`CurrentUser\My`, optionally
  `LocalMachine\My`) with subject, issuer, thumbprint, and expiry; private keys are never exported.
- Mutual-TLS request engine over `SocketsHttpHandler` supporting GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS,
  custom headers, a request body, and a configurable timeout.
- Response viewer for unknown formats: pretty-prints JSON/XML, shows HTML/text, hex-dumps binary, and
  sniffs the body when the content type is missing or misleading. Pretty / Raw / Headers views.
- Distinct failure classification: certificate refused, server certificate untrusted, network, and timeout.
- Off-by-default "ignore server certificate errors" toggle for internal sites behind a private CA.
- Built-in *Run Self-Test* that stands up a local mutual-TLS server and proves the certificate path
  end to end with no real endpoint.
- Save any response (including binary) to a file.
- Self-contained single-file executable — no installer, no admin rights, no runtime dependency.

[Unreleased]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.93.0...HEAD
[1.93.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.92.3...v1.93.0
[1.92.3]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.92.2...v1.92.3
[1.92.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.92.1...v1.92.2
[1.92.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.92.0...v1.92.1
[1.92.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.91.2...v1.92.0
[1.91.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.91.1...v1.91.2
[1.91.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.91.0...v1.91.1
[1.91.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.90.2...v1.91.0
[1.90.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.90.1...v1.90.2
[1.90.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.90.0...v1.90.1
[1.90.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.89.0...v1.90.0
[1.89.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.88.0...v1.89.0
[1.88.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.87.0...v1.88.0
[1.87.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.86.0...v1.87.0
[1.86.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.85.1...v1.86.0
[1.85.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.85.0...v1.85.1
[1.85.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.84.0...v1.85.0
[1.84.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.83.0...v1.84.0
[1.83.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.82.0...v1.83.0
[1.82.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.81.0...v1.82.0
[1.81.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.80.0...v1.81.0
[1.80.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.79.0...v1.80.0
[1.79.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.78.0...v1.79.0
[1.78.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.77.0...v1.78.0
[1.77.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.76.0...v1.77.0
[1.76.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.75.0...v1.76.0
[1.75.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.74.0...v1.75.0
[1.74.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.73.0...v1.74.0
[1.73.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.72.0...v1.73.0
[1.72.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.71.0...v1.72.0
[1.71.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.70.0...v1.71.0
[1.70.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.69.0...v1.70.0
[1.69.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.68.0...v1.69.0
[1.68.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.67.0...v1.68.0
[1.67.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.66.1...v1.67.0
[1.66.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.66.0...v1.66.1
[1.66.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.65.0...v1.66.0
[1.65.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.64.0...v1.65.0
[1.64.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.63.0...v1.64.0
[1.63.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.62.0...v1.63.0
[1.62.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.61.1...v1.62.0
[1.61.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.61.0...v1.61.1
[1.61.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.60.0...v1.61.0
[1.60.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.59.0...v1.60.0
[1.59.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.58.0...v1.59.0
[1.58.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.57.0...v1.58.0
[1.57.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.56.0...v1.57.0
[1.56.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.55.1...v1.56.0
[1.55.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.55.0...v1.55.1
[1.55.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.54.0...v1.55.0
[1.54.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.53.0...v1.54.0
[1.53.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.52.0...v1.53.0
[1.52.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.51.0...v1.52.0
[1.51.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.50.0...v1.51.0
[1.50.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.49.0...v1.50.0
[1.49.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.48.0...v1.49.0
[1.48.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.2...v1.48.0
[1.47.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.1...v1.47.2
[1.47.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.0...v1.47.1
[1.47.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.1...v1.47.0
[1.46.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.0...v1.46.1
[1.46.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.45.0...v1.46.0
[1.45.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.44.0...v1.45.0
[1.44.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.43.0...v1.44.0
[1.43.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.42.0...v1.43.0
[1.42.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.41.0...v1.42.0
[1.41.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.40.0...v1.41.0
[1.40.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.39.0...v1.40.0
[1.39.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.38.0...v1.39.0
[1.38.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.37.0...v1.38.0
[1.37.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.36.0...v1.37.0
[1.36.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.35.0...v1.36.0
[1.35.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.34.0...v1.35.0
[1.34.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.33.0...v1.34.0
[1.33.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.32.0...v1.33.0
[1.32.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.31.0...v1.32.0
[1.31.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.30.0...v1.31.0
[1.30.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.29.0...v1.30.0
[1.29.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.1...v1.29.0
[1.28.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.0...v1.28.1
[1.28.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.27.0...v1.28.0
[1.27.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.26.0...v1.27.0
[1.26.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.25.0...v1.26.0
[1.25.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.24.0...v1.25.0
[1.24.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.23.0...v1.24.0
[1.23.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.1...v1.23.0
[1.22.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.0...v1.22.1
[1.22.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.21.0...v1.22.0
[1.21.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.20.0...v1.21.0
[1.20.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.19.0...v1.20.0
[1.19.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.18.0...v1.19.0
[1.18.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.17.0...v1.18.0
[1.17.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.16.0...v1.17.0
[1.16.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.15.0...v1.16.0
[1.15.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.1...v1.15.0
[1.14.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.0...v1.14.1
[1.14.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.1...v1.14.0
[1.13.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.0...v1.13.1
[1.13.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.12.0...v1.13.0
[1.12.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.3...v1.12.0
[1.11.3]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.2...v1.11.3
[1.11.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.1...v1.11.2
[1.11.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.0...v1.11.1
[1.11.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.10.0...v1.11.0
[1.10.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.9.0...v1.10.0
[1.9.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.8.0...v1.9.0
[1.8.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.0.0
