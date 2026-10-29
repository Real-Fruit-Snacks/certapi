# 19. Local Gateway (`serve`)

Some apps and tools can't present a client certificate — a browser tab, a quick script, a legacy
utility. The **local gateway** bridges that: it listens on a local port and forwards requests to
certificate-protected sites **with your client certificate attached**. The calling app just talks
plain HTTP (Hypertext Transfer Protocol) to `127.0.0.1`.

## Start a gateway

```powershell
certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client"
```

Now point any tool at `http://127.0.0.1:8443/...` and it reaches the upstream protected by mTLS
(mutual Transport Layer Security):

```powershell
curl http://127.0.0.1:8443/api/orders            # curl needs no cert
# or set an app's base URL to http://127.0.0.1:8443
```

The gateway answers **loopback callers only** unless you ask otherwise with `--listen`
([below](#add-a-shared-secret-share-the-gateway)).

## Many sites on one gateway

A restricted network is rarely one site: the work is a login host, a few APIs, an intranet portal and
whatever each of them redirects to. One gateway fronts all of them. Give each site a name:

```powershell
certapi serve --port 8819 --cert "CN=matt" --browser `
    --upstream api=https://api.corp `
    --upstream auth=https://login.corp `
    --upstream hr=https://hr.corp:8443
```

and the gateway prints what it built:

```text
listening on http://127.0.0.1:8819/
  SITE  OPEN                         UPSTREAM              CERT  SETTINGS
  api   http://127.0.0.1:8819/api/   https://api.corp      matt  -
  auth  http://127.0.0.1:8819/auth/  https://login.corp    matt  -
  hr    http://127.0.0.1:8819/hr/    https://hr.corp:8443  matt  -
  every site: cert matt · browser · no token · loopback only
```

- **`name=url`** mounts the site at `/name`. `/path=url` mounts it at a path instead (longest prefix
  wins), and the positional `<upstream>` is the site at `/`. A path under no site is a 404 that
  contacts nothing. Only the URL's scheme, host and port are used, and the gateway says so when a URL
  carries a path: with `api=https://api.corp/v1`, request `/api/v1/orders` to reach
  `https://api.corp/v1/orders`.
- **`--allow "*.corp"`** reaches any matching host without listing it: `/_/https://host/path` is
  forwarded with the certificate, anything else is a 403 before a socket opens. A redirect to an
  allowed host comes back as `/_/https://host/…`, so a chain through hosts you never listed still
  works. `*` allows every host and is logged as the open relay it is.
- **`--check`** builds and validates everything — sites, certificates, files, flags — prints the same
  table, and exits without binding the port or contacting anything. Exit 0 means the configuration
  is valid; the port bind, the URL reservation for `--vhost`/`--listen` and the `--tls` binding are
  only tried at a real start.
- **`--upstream`, `--allow`, `--routes` and `--cors <origins>` repeat**, and repeats add up.

### One origin per site: `--vhost`

Under a path, an application that writes absolute paths (`/static/app.js`, `Path=/` cookies,
`<base href="/">`) or compares origins (postMessage, CSP, SameSite) trips over the prefix. `--vhost`
gives each site its own origin instead:

```powershell
certapi serve --port 8819 --cert "CN=matt" --vhost --browser `
    --upstream api=https://api.corp --upstream auth=https://login.corp --allow "*.corp"
# http://api.gw.localhost:8819/      -> https://api.corp
# http://auth.gw.localhost:8819/     -> https://login.corp
# http://hr.corp.gw.localhost:8819/  -> https://hr.corp   (allowed dynamic host; hr.corp--8443 for a port,
#                                                         http--pub.corp for plain http)
```

Browsers resolve every `*.localhost` name to loopback without a hosts-file entry; another suffix
(`--vhost gw.test`) needs one. Rewrites speak host form — a redirect from `auth` to `api` lands on
`api.gw.localhost` — and cookies keep the `Path=` the upstream wrote. The site at `/` answers on the
bare suffix (`gw.localhost`), and a site mounted at a path with no name is named after its path
(`/auth/sso` → `auth-sso.gw.localhost`). `--vhost` binds every interface, which on Windows needs a one-time URL
reservation (the `netsh` command is printed), but answers only loopback callers unless `--listen` is
also given. `127.0.0.1` keeps working in path form beside it. With `--tls` the gateway certificate also names the suffix, every site under it, and each `--allow` host on its default https port; an allowed host on another port or over http (or under `--allow *`) warns once in the browser.

### Sites with their own certificate and settings: `--routes`

When sites need different identities or different trust, describe them in a file:

```json
[
  { "name": "api", "upstream": "https://api.corp", "cert": "CN=svc-api" },
  { "name": "hr",  "upstream": "https://hr.corp:8443", "certFile": "hr.pem", "keyFile": "hr.key",
    "windowsAuth": true },
  { "name": "lab", "upstream": "https://lab.corp", "insecure": false, "caFile": "lab-root.pem",
    "revocation": "online" },
  { "name": "billing", "upstream": "https://billing.corp", "headers": { "X-Api-Key": "${env:BILLING_KEY}" } },
  { "prefix": "/", "upstream": "https://portal.corp" }
]
```

```powershell
certapi serve --port 8819 --cert "CN=matt" --insecure --routes sites.json
```

Each setting overrides the gateway's flag **for that site only**; anything a site leaves out it
inherits. Here `lab` refuses an untrusted server certificate even though the gateway runs with
`--insecure`, and trusts its own lab root; `hr` answers Windows auth while the others do not.

| Field | Meaning |
|---|---|
| `name` | The site's name: mounted at `/name`, and at `name.gw.localhost` under `--vhost`. Letters, digits and inner hyphens |
| `prefix` | Mount at this path instead of `/name`. With a `name` too, the name still labels the site in the table and is its `--vhost` host. `path` is accepted as a synonym |
| `upstream` | The site's absolute `https://` (or `http://`) URL; only scheme, host and port are used, and credentials in it are refused. `url` is accepted as a synonym |
| `cert` | A client certificate from the Windows store (subject or thumbprint), instead of the gateway's `--cert` |
| `certFile`, `certPassword`, `keyFile` | A client certificate from a file instead |
| `insecure` | `true` accepts any server certificate for this site; `false` insists on a valid one even under `--insecure`. A JSON boolean |
| `caFile` | Extra root(s) this site trusts, on top of the Windows store and the gateway's `--ca-file`. A path or a list |
| `revocation` | `none`, `offline` or `online` for this site |
| `windowsAuth` | `true` answers this site's Negotiate/NTLM challenge as you; `false` never does, even with `--windows-auth` (and cannot be combined with `windowsUser`). A JSON boolean |
| `windowsUser`, `windowsPassword` | Answer it as this account instead |
| `headers` | Headers set (replace or add) on every request to this site, after the gateway's `--request-header` rules |
| `sni` | The server name sent in this site's TLS handshake instead of its host — for a site reached by address, or one whose front end routes by a different name. Direct connections only; the certificate is then checked against this name |

Paths are relative to the file. Any value may be `${env:NAME}`, read from the environment, so a
password or key stays out of a file you share; an unset variable is an error naming the field. Two
sites on one host with different settings get separate connection pools. The startup table's
SETTINGS column and `/_certapi/routes` show what each site changes — never a secret's value.
`--routes` repeats, and its sites add to any typed with `--upstream`.

### Keep the whole gateway in a profile

A gateway you start every day belongs in the [configuration file](27-Configuration.md), so the
command is one word long:

```json
{
  "profiles": {
    "corp": {
      "cert": "CN=matt",
      "caFile": "corp-root.pem",
      "serve": {
        "port": 8819, "vhost": true, "browser": true, "token": "${env:GW_TOKEN}",
        "allow": ["*.corp"],
        "sites": [
          { "name": "api",  "upstream": "https://api.corp" },
          { "name": "auth", "upstream": "https://login.corp" },
          { "name": "hr",   "upstream": "https://hr.corp:8443", "cert": "CN=svc-hr", "windowsAuth": true }
        ]
      }
    }
  }
}
```

```powershell
certapi serve --profile corp --check     # see the table, start nothing
certapi serve --profile corp
```

`serve` takes `port`, `vhost` (`true` or a suffix), `browser`, `cors` (`true`, an origin or a list),
`token`, `allow` (a host or a list), `listen`, `cookieJar` (`true` or a file), `autoToken`,
`windowsAuth`, `windowsUser`, `windowsPassword`, `forwarded`, `maxBody`, `timeout`, `tls`,
`rewriteBody` and `sites` (exactly the `--routes` format). The certificate, `insecure`, proxy, CA files, revocation,
`tlsMin` and `connectTimeout` come from the profile's top-level fields, as for every command; the
top-level `timeout`, `workspace`, `retry` and `headers` do not apply to `serve`. Options with no field
(`--check`, `--quiet`, the individual browser flags, header rules, `--record`/`--replay`) are typed on
the command line alongside `--profile`.

A flag typed on the command line wins. A typed list (`--allow`, `--cors <origins>`, `--ca-file`)
replaces the profile's rather than adding to it; typed Windows credentials replace the profile's as
a pair; sites typed on the command line replace the profile's list whole, and the gateway says so. A
switch the profile turns on is turned off for one run with its `--no-` form (`--no-browser`,
`--no-tls`, `--no-vhost`, `--no-cors`, `--no-rewrite-body`, …). `certapi config show --profile corp` lists the sites with every
secret masked.

## Options

| Option | Purpose |
|---|---|
| `<upstream>` | The site at `/`: a full `https://` URL, or the name of a saved website |
| `--upstream <name>=<url>` | A site called `name`, mounted at `/name` (and `name.gw.localhost` with `--vhost`). `/path=<url>` mounts at a path instead. Repeatable |
| `--routes <file>` | Sites from a JSON file, each with its own certificate and settings ([above](#sites-with-their-own-certificate-and-settings---routes)). Repeatable |
| `--allow <hosts>` | Reach any matching host at `/_/https://host/path` (`*.corp`, exact names, `*`). Repeatable, comma-separated |
| `--vhost [<suffix>]` | One origin per site under `*.gw.localhost` (or `*.<suffix>`) instead of one path each |
| `--port <n>` | Local port to listen on |
| `--check` | Validate everything, print the table of sites, exit without starting |
| `--profile <name>` | Take the gateway from a profile's `serve` section (a global option) |
| `--cert <thumb\|subject>` | Client certificate from the Windows store |
| `--cert-issuer` / `--cert-serial` | Pick a store certificate by issuer or serial number (alone or with `--cert`) |
| `--cert-pin <pin>` | Smart-card / HSM PIN, supplied ahead of the prompt (or `CERTAPI_CERT_PIN`). Windows only |
| `--cert-file <path>` / `--cert-password` / `--key-file` | Certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine` |
| `--insecure` | Ignore the upstream's server-certificate errors (a site can turn this back off) |
| `--ca-file <pem>` | Trust the root(s) in this file on top of the Windows store. Repeatable |
| `--revocation none\|offline\|online` | Check whether the upstream's certificate has been revoked by its issuer (default `none`) |
| `--revocation-strict` | Treat an undeterminable revocation status as fatal; needs `--revocation offline` or `online` |
| `--windows-auth` | Answer the upstream's Negotiate/NTLM challenge as you. Aliases `--ntlm`, `--negotiate` |
| `--windows-user` + `--windows-password` | Answer it as this account instead |
| `--token <value>` | Require callers to send `Authorization: Bearer <value>` or `X-Certapi-Token: <value>` |
| `--listen <address>` | Also answer on this address: an IP address, `0.0.0.0`, `::`, `+`, `*` or `localhost`. A loopback address (`127.0.0.1`, `::1`, `localhost`) needs no token and stays loopback-only, even under `--vhost`. Any other address needs `--token`. With `--tls` only `127.0.0.1` or `::1` is accepted |
| `--timeout <seconds>` | How long to wait for the upstream's **response headers** (default 100). A body that streams longer is not cut off |
| `--max-body <bytes>` | Refuse a request body over the limit with 413 (`k`/`m`/`g` suffixes) |
| `--forwarded` | Add `X-Forwarded-For/Proto/Host` and `Via: 1.1 certapi` to forwarded requests |
| `--workspace <file>` | Resolve a saved-website `<upstream>` from a workspace file |
| `-q, --quiet` | No startup table or per-request log ([the log](#the-request-log)) |
| `--tls` | Serve the gateway itself over HTTPS on 127.0.0.1 with a generated gateway certificate, so `Secure` and `__Host-`/`__Secure-` cookies work. The certificate is cached in `%AppData%\CertApiTester\gateway-tls` and reused. Binding the port needs an elevated prompt the first time; the exact `netsh` command is printed if it isn't available. With `--vhost` the certificate also names the suffix, every site under it and each `--allow` host on its default https port. It is regenerated within 7 days of expiry, or when it lacks a name the run needs; that run moves the `127.0.0.1:<port>` binding to the new certificate (from an elevated prompt; otherwise it prints the `netsh` commands and stops). With `--tls-trust` the new root is installed and then the old one removed; without it, a note says to add `--tls-trust` once |
| `--tls-trust` | Also install that certificate into `CurrentUser\Root` so the browser accepts it silently — explicit, logged, and reversible. Only with `--tls` |
| `--tls-untrust` | Remove the current gateway certificate, and every earlier one this tool generated, from `CurrentUser\Root`, then exit; a standalone action. It does not remove the HTTP.sys port binding |
| `--browser` | The bundle: the seven browser accommodations below at once |
| `--cors [<origins>]` | Answer CORS (Cross-Origin Resource Sharing) preflights and add the headers a script needs; echoes the caller's `Origin` with no value, or takes an allowlist. Repeatable |
| `--cors-max-age <seconds>` | How long a browser may cache a preflight answer (default 600). Only with `--cors` (or `--browser`) |
| `--rewrite-cookies` | Strip `Domain=` and `Secure` from each `Set-Cookie` and turn `SameSite=None` into `Lax`, so the browser stores the cookie against the gateway |
| `--rewrite-location` | Bring a 3xx `Location` (and a `Refresh` header's URL) aimed at the upstream, another site or an allowed host back through the gateway |
| `--rewrite-origin` | `Origin` and `Referer` on a forwarded request point at the upstream |
| `--strip-csp` | Drop `Content-Security-Policy` from responses |
| `--rewrite-body` | Upstream URLs written into HTML, CSS, JavaScript, JSON and XML bodies — links, script sources, a meta refresh, an API base in a config file — point back through the gateway, mapped exactly as a `Location` is. JSON's escaped `https:\/\/` form is kept escaped. JSON and XML are rewritten only when the response has a `Content-Length`; streamed JSON or XML is relayed as it arrives. Only `gzip`, `deflate` and `br` are decoded, and a body with a rewritten URL is sent decompressed. A body that is unchanged, over 32 MB, not UTF-8, corrupt, or in another or stacked encoding passes untouched. Not part of `--browser`: it buffers bodies the relay otherwise streams |
| `--referer-fallback` | A path no site owns, from a page the gateway served, is sent (307) to the same path under that page's site |
| `--allow-upgrade` | Relay WebSocket connections through your certificate |
| `--cookie-jar [<file>]` | Keep upstream cookies on the gateway and send them to matching hosts; with a file, kept across restarts encrypted (DPAPI) |
| `--auto-token` | Attach a bearer token captured earlier for the upstream's origin |
| `--request-header "Name: value"` | Set a header on the request before it reaches the upstream. Repeatable |
| `--remove-request-header <name>` | Strip a header from the request. Repeatable |
| `--response-header "Name: value"` | Set a header on the response before it reaches the caller. Repeatable |
| `--remove-response-header <name>` | Strip a header from the response. Repeatable |
| `--record <file.har>` / `--replay <file.har>` | Capture every exchange, or answer from a capture ([below](#record-a-session-replay-it-offline)) |

The proxy options (`--proxy`, `--no-proxy`, `--noproxy`, `--pac`, `--no-env-proxy`, `--proxy-user`),
`--resolve`, `--tls-min`, `--connect-timeout`, `--offline` and `-4`/`-6` apply to the gateway's
connections exactly as they do on [`send`](21-CLI-Reference.md#transport-options). The redirect,
decompression, HTTP-version and retry options are accepted but do not apply. `--sni` names the server
in every site's handshake (a site's own `"sni"` wins), direct connections only. The gateway answers
`/_certapi/health` (state as JSON) and `/_certapi/routes` (sites, settings, allow list) itself, on
`127.0.0.1` and on every virtual host alike; both honour `--token`.

## The request log

`serve` writes a line per request to stderr. A thread of its own writes the log, so a request
never waits for the console:

- **To a console window**, at most 20 request lines a second are shown. The rest of that second
  is counted on one line: `… and 140 more requests in that second`. A page load through ten sites
  is hundreds of requests, and on a remote desktop (a VDI, RDP, Citrix) drawing that much
  scrolling text costs the session more than relaying the requests. Warnings, 5xx answers and
  failed requests have a limit of their own, 100 a second, so ordinary traffic does not crowd
  them out. With `--log-file`, the file still gets every line.
- **QuickEdit is turned off while the gateway runs** (even with stdin redirected), and back on when
  it stops. With QuickEdit
  on, a click in a classic console window starts a selection, and every write to the window waits
  until the selection ends. Before 1.96.0 that stopped the gateway and every page relayed through
  it. Windows Terminal never paused output that way.
- **If the console still cannot keep up**, lines that do not fit in the log's queue (10,000) are
  counted and reported (`… 312 log lines not written: the console could not keep up`), not waited
  for. With `--log-file`, they still reach the file.
- **To a file or a pipe**, every line is written: `certapi serve … 2> gateway.log`. `-q` turns the
  log off. `--trace` is different: it writes each network event as it happens, on the request's
  own thread, so on a remote desktop send it to a file (`--trace-file`).

## Revocation checking

`--revocation none|offline|online` (default `none`) and `--revocation-strict` apply to the gateway's
own connection to the upstream, exactly as they do on [`certapi send`](21-CLI-Reference.md#send): a
certificate the upstream's issuer has revoked is refused even past a pinned thumbprint, an
indeterminate status is not fatal unless `--revocation-strict` asks for that, and `--insecure` still
overrides both. The gateway **enforces** the setting on every forwarded call — it just has no per-call
diagnostics object of its own to report a status back through, the way `send`'s `--json` envelope does.
See [Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation) for what the modes mean.

## Header rules

`--request-header`, `--remove-request-header`, `--response-header`, and `--remove-response-header`
apply to forwarded HTTP traffic, with or without `--browser` — a header rule is not a browser
concern. Setting a header replaces it if one was already present and adds it otherwise; naming the
same header to both a set flag and a remove flag on the same side removes it, since removal wins over
setting. On the response side these rules apply *after* `--browser`'s own rewrites (CORS, cookies,
`Location`), so a header you set here wins over one the gateway injected.

`Connection`, `Keep-Alive`, `Transfer-Encoding`, `Content-Length`, `TE`, `Trailer`, `Upgrade`,
`Proxy-Authenticate`, `Proxy-Authorization`, and `Host` are refused with a usage error naming the
header and why, rather than silently ignored — the first nine frame the HTTP message and the HTTP
stack manages them, and `Host` is set by the gateway's own HTTP client from the upstream URI, so a
rule for it would only ever half-apply. A header name that is missing, or that carries a character
an HTTP field name cannot hold — a space, an embedded colon — is a usage error too, for the same
reason: the header could never match, so the rule would be dropped rather than applied. These rules
never touch a CORS or PNA (Private Network Access) preflight the gateway answers itself, its own
error pages, or a relayed WebSocket upgrade.

## Browser accommodations

A plain relay hands a browser exactly the headers that make it refuse the response — a tab checks
things curl and a script never do, like origin policy, cookie attributes, and where a redirect
actually leads. Without the flags below nothing changes: the gateway stays a byte-faithful relay,
which is exactly what protects every existing non-browser caller. `--browser` is the bundle that turns
on seven accommodations at once — `--cors`, `--rewrite-cookies`, `--rewrite-location`,
`--allow-upgrade`, `--rewrite-origin`, `--strip-csp` and `--referer-fallback` — and each one is also
usable on its own. `--cookie-jar` and `--auto-token` stay opt-in, because they make the gateway hold
session state on your behalf.

`--cors [<origins>]` answers a preflight at the gateway and adds the response headers a script needs
in order to read the reply. With no value the caller's own `Origin` is echoed back; give it a
comma-separated list to allow only those origins instead. See
[Browsers and Private Network Access](#browsers-and-private-network-access), below, for the further
check Chrome runs on top of CORS.

`--rewrite-cookies` is what lets the browser actually keep the cookie: a `Set-Cookie` loses its
`Domain=` attribute and its `Secure` attribute, and `SameSite=None` becomes `SameSite=Lax`, so the
cookie can be stored against the gateway rather than the upstream the browser never directly talked
to.

`--rewrite-location` keeps a redirect on the gateway: a 3xx `Location` aimed at the upstream comes
back pointing at the gateway instead, and one aimed at another site or an `--allow`ed host comes back
through that site (or `/_/https://host/…`), so a login → app → login chain stays on the certificate.
One aimed anywhere else is left exactly as the upstream wrote it, and logged — because that hop
leaves the gateway, and your client certificate leaves with it.

`--rewrite-origin` points `Origin` and `Referer` at the upstream, for the login form whose CSRF check
compares them with its own host. `--strip-csp` drops `Content-Security-Policy`, which names hosts the
page can no longer reach through the gateway. `--referer-fallback` sends a path no site owns, asked
for by a page the gateway served, to the same path under that page's site — for the application that
writes absolute paths (`--vhost` removes the need entirely).

**Logins that cross hosts.** A browser stores every cookie against the gateway's one origin, so a
`Domain=.corp` session cookie set by `login.corp` never reaches `app.corp` on its own. `--cookie-jar`
keeps upstream cookies on the gateway against the real upstream URL and sends them to every matching
host; with a file (`--cookie-jar session.bin`) the jar survives a restart, encrypted for your Windows
user and never written in the clear. `--auto-token` attaches a bearer token captured earlier for the
upstream's origin. `--windows-auth` answers an upstream's Negotiate/NTLM challenge as you (or as
`--windows-user`), so a browser or script with no Windows identity gets through an IWA site.

`--allow-upgrade` relays WebSocket connections to the upstream through your certificate, the same way
every other forwarded request is.

Over the default plaintext loopback origin, a cookie named `__Host-…` or `__Secure-…` cannot work at
all: it requires the `Secure` attribute, which no browser accepts over plaintext `http://127.0.0.1`.
Rather than dropping such a cookie behind your back, the gateway still relays it and names it in a
warning. `--tls` is the fix, because it serves the gateway itself over HTTPS.

## Record a session, replay it offline

Two ends of one HTTP Archive (HAR) format: capture what the real upstream says while it is up,
then answer from that capture when it is gone — a plane, a demo, an upstream that has been
decommissioned, or a test suite that must not hit production.

```powershell
# capture: every forwarded exchange of this session, written on Ctrl+C (replacing session.har)
certapi serve https://api.internal --port 8443 --cert "CN=My Client" --record session.har

# replay: the upstream is never contacted; answers come from the file
certapi serve https://api.internal --port 8443 --replay session.har
```

`--record` redacts credential headers by default — `Authorization`, `Proxy-Authorization`,
`Cookie`, `Set-Cookie`, `X-Api-Key`, `X-Auth-Token`, and any header named like a token, secret,
password, API key, session or credential (CORS `Access-Control-*` headers and `WWW-Authenticate`
are never redacted) — since a recording is a file people share; `--record-include-secrets` keeps them. `--replay` matches a request the way `mock --har`
does — method and path, query included when it disambiguates, in recorded order for a repeated
call — and answers a path the session never saw with 404. The two flags are mutually exclusive:
you cannot record a session you are inventing. Because the format is the same one `mock --har`
reads, a session recorded here can also be replayed by `certapi mock --har session.har` without a
gateway at all.

The session is held in memory until the gateway stops, so the bodies it keeps are bounded: at most
32 MB per body, and about 128 MB of memory for all of them together, counting the requests still
in flight. (A kept body is held as text, two bytes a character, so that is roughly 64 MB of
bodies.) Bodies are copied as they stream through, so recording does not hold up the relay. An
exchange past either limit is still recorded (method, URL, headers, status) with an empty body and
a `comment` saying why, with the body's size where it is known. When the gateway stops it says how
many bodies were left out. Replaying such an exchange answers with an empty body and drops its
`Content-Encoding`.

## Browsers and Private Network Access

Chrome runs a further check, PNA, before it lets a page on a public origin
reach a private or loopback address at all: its preflight carries
`Access-Control-Request-Private-Network: true`, and Chrome blocks the request unless the response
answers `Access-Control-Allow-Private-Network: true`. `--cors` answers it, but only for an origin the
same allowlist already accepts — an origin outside an explicit `--cors <origins>` list still gets a
bare 403. Letting a public origin reach a loopback service at all is a real exposure even with PNA
answered, which is why naming the origins you develop from with `--cors <origins>` is safer than
leaving it echoing whoever asks.

## Add a shared secret, share the gateway

Because anything on your machine could hit the loopback port, you can require a token so only your own
tools get through:

```powershell
certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --token s3cret
# callers send:  Authorization: Bearer s3cret   (or X-Certapi-Token: s3cret)
```

The token header is stripped before the request is forwarded and never recorded. A CORS preflight is
answered without it, because a browser cannot attach a header to one.

`--listen 0.0.0.0 --token s3cret` (or a LAN address) lets a VM, a container or a teammate use the
gateway. The token is required, because anything that reaches the port then reaches your client
certificate. On Windows a non-loopback address needs a one-time URL reservation; the exact
`netsh http add urlacl` command is printed when it is missing.

## Gateway vs. mock

- **`serve`** forwards to a **real** upstream, adding your certificate — for reaching a cert-protected
  service from a tool that can't do mTLS.
- **`mock`** *is* the endpoint — a fake server that echoes requests, for testing without a real API
  (application programming interface).
  See [Mock Server](18-Mock-Server.md).

Next: [MCP Server](20-MCP-Server.md).
