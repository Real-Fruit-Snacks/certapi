# 23. Troubleshooting

Common problems and how to diagnose them.

## Start here: `certapi doctor`

When something won't connect and you don't yet know why, ask the doctor before reading anything
else on this page:

```powershell
certapi doctor https://api.example.com/health --cert "CN=My Client"
```

It makes the connection one stage at a time — URL, proxy decision, DNS (domain name system), TCP
(Transmission Control Protocol), the proxy tunnel, the TLS (Transport Layer Security) handshake,
then an HTTP (Hypertext Transfer Protocol) GET — and tells you **which stage broke**, with what it
saw at each. Every stage is timed, so "slow" is diagnosable too.

Four things it can tell you that an ordinary request never will:

- **Which certificate authorities the server accepts client certificates from**, matched against
  the certificates you actually have. Lines like these answer the most common mTLS mystery, and
  they are information only visible during a handshake:

  ```
  the server accepts client certificates from 1 authority/authorities:
    CN=Corp Issuing CA 2
  NONE of your 3 certificate(s) are issued by any of those authorities
  the certificate you chose is NOT — it is issued by CN=Old Issuing CA
  ```
- **Whether this network is decrypting TLS in the middle.** If the chain's root is an inspection
  appliance (or a private root this machine happens to trust), doctor says so — and says why it
  matters: a client certificate cannot survive an intercepting proxy.
- **Which proxy the machine picks for this URL**, including one chosen by a PAC (proxy
  auto-config) script, plus what the proxy said if it refused the tunnel — including the
  authentication schemes it offered on a 407.
- **Whether the internet is reachable at all**, or a captive portal (hotel or guest Wi-Fi
  sign-in page) is in the way, or the host simply needs the VPN (virtual private network).

`--json` prints the whole report for scripts; `-q` shows only what failed or carries advice.
Exit 0 when every stage passed, 1 when one failed.

## "It works in my browser but not here"

Nine times in ten this is the proxy — and specifically a PAC (proxy auto-config) script sending
the two down different routes. Ask:

```powershell
certapi proxy https://api.example.com/orders
```

It prints the machine's configuration (WPAD auto-detection, the configuration-script address, the
static proxy and its bypass list) and then, for that URL, **two answers**: the one Windows' own
engine computes by running your PAC script, and the one .NET computes — which is the one certapi
follows. When those disagree, the command says so, and you have your explanation.

A PAC script is JavaScript, so nothing can predict its answer by reading configuration alone;
this runs the real engine rather than guessing.

## "It's slow" — reading the timings

Three different measurements answer three different questions, and mixing them up wastes an
afternoon:

- **`certapi doctor <url>`** times each *stage* of one fresh connection — DNS, TCP, the proxy
  tunnel, the TLS handshake, the first byte. This is the one that tells you *where* the time
  goes. A slow TLS stage with fast everything else usually means revocation checking reaching for
  a network endpoint; a slow DNS stage means a resolver problem, not an API problem.
- **A redirect chain** is timed per hop (`--show-redirects`, and in the HTTP Archive (HAR) export).
  A request that "takes two seconds" is often four hops of five hundred milliseconds, and the
  destination is innocent.
- **`certapi bench`** measures a *warm* endpoint under load, which is the number to quote for
  throughput — it deliberately reuses connections.

- **`certapi send --timing`** breaks down the request you actually sent: DNS, connect, TLS, the
  wait for the server, first byte, download. A phase that did not happen is left out rather than
  shown as zero — on a reused connection there is no DNS, connect or handshake, and the line says
  `connection reused` instead. A large `wait` with small everything else is the server's own time.

## Watching what the network stack actually did: `--trace`

`doctor` diagnoses one connection it makes itself. `--trace` is different: it reports what .NET's
own networking stack did during **any** command, as it happens.

```powershell
certapi send https://api.internal/orders --trace
```

```
trace [   109.4 ms] System.Net.NameResolution    ResolutionStart    hostNameOrAddress=api.internal
trace [   155.4 ms] System.Net.NameResolution    ResolutionStop
trace [   156.4 ms] System.Net.Sockets           ConnectStart       address=…
trace [   181.8 ms] System.Net.Sockets           ConnectStop
trace [   182.7 ms] System.Net.Security          HandshakeStart     isServer=False targetHost=api.internal
trace [   221.5 ms] System.Net.Security          HandshakeStop      protocol=12288
trace [   222.7 ms] System.Net.Http              ConnectionEstablished  versionMajor=1 versionMinor=1 …
```

- `--trace-filter <substrings>` narrows it (comma-separated) — it is genuinely a firehose.
- `--trace-file <path>` writes it out instead of streaming to stderr.
- `--trace-verbose` adds the runtime's *internal* diagnostics: far more detail, far less stable
  (free-text handler messages, security-context buffers). Useful when the normal level is not
  enough; never something to parse.
- Credentials in event payloads are **redacted** — a trace is a file people paste into tickets.
  `--trace-include-secrets` keeps them, for when you are the only reader.

**Reading a reused connection.** A request that reuses a pooled connection emits *no*
`ConnectStart` and *no* `HandshakeStart` at all — that absence is the signal. It is the quickest
way to answer "is connection pooling actually working here".

**Two honest limits.** This is in-process: it observes the connections **this process** makes, so
under `certapi mock` or `certapi serve` you will also see that server's own accepts and
handshakes. And it is not packet capture — capturing packets needs a kernel driver and
administrator rights, which this tool deliberately never requires. What it gives instead is the
decrypted, structured account of connections a sniffer could not read anyway.

## Keeping a diagnosis: `doctor --md`

A diagnosis is exactly the thing worth keeping — it is what gets pasted into a ticket, argued about
with a network team, and looked up again when the same host breaks the same way six months later.
By default it scrolls off the terminal.

```powershell
certapi doctor https://api.internal/health --cert "CN=Me" --md investigation.md
certapi doctor https://api.internal/health --md-vault C:\Users\me\Vault
```

The note carries what the terminal showed — the stage table with per-stage timings, every detail
line, and the advice — plus the two findings that are hard to get any other way, verbatim: **the
certificate authorities the server said it accepts**, and **any TLS-interception finding**. Its
frontmatter records `host`, `outcome`, `failedStage` and the timestamp, so a vault accumulates a
searchable history of what was broken when.

`--md-vault` files it as `certapi/investigations/<host>-<timestamp>.md`. **A new note per run —
nothing ever overwrites a past diagnosis.** That is the opposite of `export markdown`, on purpose:
a catalogue is current state, an investigation is history, and a vault that quietly replaced last
week's failure with this week's would destroy the record exactly as a pattern became visible.

`--md-open` opens the note afterwards; if nothing is registered for `.md` it prints the path
instead of failing. Credentials in a URL are redacted, since vaults sync — both query values
(`?api_key=…`, `?token=…`) and a password in the address itself
(`https://svc:…@api.internal`, and the same in a `--proxy` URL). The **username is kept**, as a
header name is; only the secret half goes. `--include-secrets` keeps everything. If the note cannot be written the command says so and
still reports the diagnosis: the answer you asked for is not worth losing to a read-only folder.

## Seeing the actual bytes: `--wire`

`--trace` reports what the stack *did*. `--wire` shows what it actually *sent and received* —
after TLS, before any parsing:

```powershell
certapi send https://api.internal/orders --cert "CN=My Client" --wire
```

```
>> sent 73 bytes at 80.7 ms
   GET /orders HTTP/1.1
   Host: api.internal
   Accept-Encoding: gzip, deflate, br

<< received 632 bytes at 103.5 ms
   HTTP/1.1 200 OK
   Content-Type: application/json
   …
```

Anything that isn't text is shown as hex and ASCII side by side, so a binary body is still
readable. `--wire-file <path>` writes the transcript out instead; credential headers are
**redacted** (the header name stays, so you can still see it was sent) unless you pass
`--wire-include-secrets`.

**This is the thing a packet capture cannot give you.** On an encrypted connection a sniffer sees
ciphertext; this is the decrypted conversation, and it needs no driver and no administrator
rights, because the tool is one end of the connection.

**Direct connections only.** Through a proxy, or on HTTP/3, the TLS session belongs to the HTTP
handler rather than to this tool, so there is no plaintext stream to read. In those cases the
command says so in one line and sends the request normally — it never prints nothing and leaves
you guessing. Use `--trace` there instead.

One consequence worth knowing: a request with `--wire` does not reuse a pooled connection (the
tapped connection is not shared), so you see the handshake as well as the exchange.

## Reading an HTTP/2 connection: `--frames`

`--wire` shows bytes. On HTTP/2 the bytes are binary framing, so there is a reading of the same
capture that matches what an analyser shows:

```powershell
certapi send https://api.internal/orders --cert "CN=My Client" --http2 --frames
```

```
>> [    0.0 ms] SETTINGS      stream=0   len=18     MAX_CONCURRENT_STREAMS=100 INITIAL_WINDOW_SIZE=65535
>> [    0.1 ms] HEADERS       stream=1   len=42     END_STREAM|END_HEADERS  4 field(s), 42 bytes  :method: GET; :scheme: https
<< [   31.4 ms] SETTINGS      stream=0   len=0      ACK
<< [   31.9 ms] HEADERS       stream=1   len=88     END_HEADERS  6 field(s), 88 bytes  :status: 200
<< [   32.1 ms] DATA          stream=1   len=512    END_STREAM
```

This is the layer that explains the failures HTTP/2 has and HTTP/1.1 does not:

- **A request that hangs** — look for a `WINDOW_UPDATE` that never arrives. Flow control is
  per-stream and per-connection; a server that stops issuing credit stalls the transfer with no
  error anywhere. The timestamps are per frame, so a four-second gap is visible as one.
- **A connection dropped without an HTTP status** — `GOAWAY` carries an error code
  (`ENHANCE_YOUR_CALM`, `PROTOCOL_ERROR`, …) and *debug data*, which is the one place a gateway
  explains itself in words.
- **One request killed while others survive** — `RST_STREAM` names the stream and the reason.
- **A server pushing resources you did not ask for** — `PUSH_PROMISE` names the stream it is
  about to push and the headers it will carry.
- **Limits you did not know applied** — `SETTINGS` carries `MAX_CONCURRENT_STREAMS` and
  `INITIAL_WINDOW_SIZE`, which is often the whole answer to "why does it slow down past N".

`--frames` implies `--wire`, and needs `--http2`. On an HTTP/1.1 connection it says so rather than
printing an empty report.

**What header decoding does and does not claim.** HTTP/2 compresses headers with HPACK, against a
table both ends build up from the connection's first byte. Because connections are pooled and
reused, a capture usually joins one already in progress, and that table cannot be reconstructed —
so certapi decodes only what is knowable without it: references into HPACK's fixed 61-entry static
table (which is where `:method`, `:scheme`, `:status` and friends normally come from) and headers
sent uncompressed. Anything else is *counted and named* rather than guessed, and the report says
how many fields it could not read. When the capture did start at the connection's first byte, it
says nothing — because then there is no caveat. Credential headers are redacted here exactly as in
the byte view, unless `--wire-include-secrets` is given.

For the header *contents*, the ordinary response output and `--diagnostics` already show them
decoded; the frame view is for the framing.

## "Am I actually reusing connections?" — `certapi connections`

Reusing a pooled connection skips a TCP handshake and a TLS handshake. Against a remote endpoint
that is most of the time a small request takes, so whether it happens dominates throughput — and it
is normally invisible, because the responses look identical either way.

```powershell
certapi connections https://api.internal/orders --cert "CN=My Client" -n 10
```

```
connection 0  https://api.internal:443  HTTP/1.1  opened at 84.2 ms  requests 10  last used at 611.9 ms  peer 10.4.2.9

10 request(s) over 1 connection(s) to 1 origin(s).
Connections are being reused, which is what you want.
```

More requests than connections means reuse is working. One connection per request means it is not,
and the usual causes are a server answering `Connection: close`, a proxy in the way, or a fresh
client per request. `--parallel <n>` sends several at a time — those genuinely need a connection
each, so read requests against connections rather than counting connections alone. `--json` gives
the same answer for a script.

`certapi bench --pool` reports the same thing for a load run. Bench has always printed a note
saying connections are pooled and reused; `--pool` measures it for the run that just happened,
which matters because a server closing connections silently makes every request pay a handshake and
changes what the latency figures mean.

**How this is known, and its two limits.** It reads the runtime's own HTTP event source — no
driver, no administrator rights, no private API. `ConnectionEstablished` gives a connection's
identifier, origin, protocol version and peer address; each request reports the connection its
headers went out on, and a reused connection produces no establishment event at all. The limits:
the runtime emits no connection-closed event that this can see, so the report covers every
connection *observed since the command started* rather than a live count of open sockets; and it is
process-wide, so a `mock` or `serve` server running in the same process would appear too.

## "Can I decrypt certapi's traffic in Wireshark?" — no, and you don't need to

The usual way to read encrypted traffic in Wireshark is a **key log file**: the application writes
its TLS session secrets out in NSS (Network Security Services) key-log format, and Wireshark uses
them to decrypt what it captured. .NET appears to offer exactly that, through the
`System.Net.EnableSslKeyLogging` switch and the `SSLKEYLOGFILE` environment variable.

**On Windows it does nothing.** This was tested rather than assumed — three configurations, each
with a real HTTPS request that genuinely succeeded, so TLS certainly happened:

| What was tried | Key log written? |
|---|---|
| The switch set in code before any TLS work | no |
| The switch baked into the app's `runtimeconfig.json` | no |
| That, plus `SSLKEYLOGFILE` set in the environment before launch | no |

The reason is structural: .NET's key-log support is implemented behind OpenSSL, and Windows TLS
goes through SChannel, which does not hand out session secrets. No amount of configuration changes
that, so **certapi deliberately has no `--keylog` flag** — a switch that always produced an empty
file would imply the capability exists and merely needs coaxing.

**Use `--wire` instead, and note what you were actually after.** A key log is a means, not an end:
you wanted the decrypted conversation. `--wire` gives you that directly, because this tool is one
end of the connection and reads the bytes before they are encrypted. No keys, no capture, no
administrator rights.

**If you genuinely need packet-level facts** — retransmits, resets, MTU (maximum transmission
unit) problems, timing at the IP (Internet Protocol) layer — those live below TLS and Wireshark
reads them *without* decryption. Run your own capture alongside the request; `--trace` stamps every
connect and handshake with a millisecond offset, which is enough to line its events up against the
packets. Capturing packets needs a driver and administrator rights, which is why this tool does not
do it for you.

## Self-test

Before blaming an endpoint, prove your machine can do mTLS at all:

```powershell
certapi selftest          # CLI
```

or click **Run Self-Test** in the app. It generates a CA (certificate authority) + server + client
certificate in memory,
stands up a loopback mTLS (mutual Transport Layer Security) server, and makes one authenticated
round-trip. If this fails, the problem
is local (certificate loading, the TLS stack) — not the target API (application programming
interface).

## Turn on diagnostics

Add `--debug` (optionally `--log-file diag.log`) to any CLI (command-line interface) command for a
full trace — resolved URL (Uniform Resource Locator),
headers (Authorization masked), certificate lookup, TLS version/cipher, timings, and full stack
traces. In the app, the **Diagnostics** response tab shows the connection details.

## "The server refused the certificate"

The TLS handshake completed but the server rejected your client certificate. Check:

- You picked the **right certificate** (`certapi certs` to list; match subject/thumbprint).
- The certificate has the **Client Authentication** EKU (Extended Key Usage) and a usable
  **private key**.
- The server actually **trusts** your certificate's issuer.
- For a file certificate, that the **private key loaded** — a keyless PEM (Privacy-Enhanced Mail)
  fails with a clear message;
  supply `--key-file` or use a `.pfx`.

## "The server's own certificate isn't trusted"

Your machine doesn't trust the **server's** certificate (common with internal/private CAs, and with a
proxy that intercepts TLS and re-signs every site). This is separate from your client cert. In order
of preference:

- **`--ca-file corp-root.pem`** trusts that one root for the run (profile `caFile`), and still refuses
  a wrong name or an expired certificate (and a revoked one, with `--revocation` on).
- **`certapi trust add <host> --from-url https://<host>/`** pins what that host presents; `--ca`
  pins its issuer and `--spki` its key, so the pin survives a renewal.
- **Install the internal CA** in your trust store, which fixes it for every program.
- **`--insecure`** (or **Ignore server certificate errors** in the app) accepts anything — only for a
  server you already trust. Through the gateway it can be set per site (`"insecure": true`).

## "The server's certificate was revoked"

This is a **different** finding from "isn't trusted" above: the chain built and would otherwise be
trusted, but the issuer has since revoked the certificate — a compromised key, or someone who left, are
the usual reasons. It only shows up when revocation checking is turned on (`--revocation offline` or
`--revocation online`; by default it's off — `--revocation none` — so this can't happen at all). A
pinned thumbprint (`certapi trust add`) doesn't rescue it either: revocation is the issuer's later word
against the pin's earlier one, and the later word wins. See
[Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation).

## "Revocation status unknown"

Expected on a corporate network — not a bug. With `--revocation online`, the endpoint that answers the
check — the Online Certificate Status Protocol (OCSP) responder, or the certificate revocation list
(CRL) distribution point — is commonly blocked or unreachable from inside a locked-down network, and
certapi reports the status as **unknown** rather than treating it as a failure, because failing on it
would make `--revocation online` unusable on exactly the networks it targets. If you need an
indeterminate answer treated as a failure instead, add `--revocation-strict` — it only makes sense
together with `--revocation offline` or `--revocation online`; using it with checking off (the default)
is a usage error.

## A network / DNS error

The connection never reached TLS — DNS (Domain Name System) failure, wrong host/port, firewall, or
the service is down.
Verify the URL, and remember the app **honors your machine proxy** (WPAD/PAC — Web Proxy
Auto-Discovery / proxy auto-configuration) using your Windows
credentials; a misconfigured proxy shows up here. If a host should be reached directly instead —
an internal service the proxy can't route to, say — list it with `--noproxy` (or set `NO_PROXY`)
rather than turning the proxy off for everything. To tell whether a request went direct because a
bypass rule matched or because there was no proxy at all, check Diagnostics or `--debug`: a matched
rule prints `Bypassed by` naming it.

## A site through the gateway doesn't work

Start with **`certapi serve … --check`** (or `serve --profile <name> --check`): it validates every
site, certificate and file and prints the table the gateway would start with, without starting it.
The SETTINGS column shows what each site does differently; `/_certapi/routes` on a running gateway
says the same.

- **404 from the gateway** — the path is under no site. Each site lives at `/<name>/…` (or at
  `<name>.gw.localhost` with `--vhost`); a host that is not a site needs `--allow`.
- **401 from the gateway** — it runs with `--token` and the request did not send it
  (`Authorization: Bearer <token>` or `X-Certapi-Token`).
- **403 from the gateway** — the host is not on `--allow`, or the caller is not on loopback and
  no non-loopback `--listen` was given. A loopback `--listen` (`127.0.0.1`, `::1`, `localhost`)
  stays loopback-only, even under `--vhost`; another address needs `--token`, and `--tls` accepts
  only `127.0.0.1` or `::1`.
- **404 for `/_/https://…`** — the gateway was started without `--allow`, so dynamic hosts are off.
- **400** — the request target walks off the site's host (`//other.host/…`); the gateway refuses it
  before the certificate can reach that host.
- **413** — the body is larger than `--max-body`.
- **403 to a browser's preflight** — the page's origin is not in `--cors <origins>`.
- **502 for one site, others fine** — that site's own connection failed: its certificate, its trust
  (`"insecure"`, `"caFile"`), or the proxy. The per-request log line names the error.
- **504** — the upstream sent no response headers within `--timeout` (default 100 seconds).
- **The page loads, but its scripts, styles or links break** — the application uses absolute paths
  (`/static/app.js`) or compares origins. Use `--vhost`, which gives each site its own origin, or
  `--referer-fallback`.
- **A login works, then the app says you are signed out** — the session cookie was set for a
  different host (`Domain=.corp` from `login.corp`). Add `--cookie-jar`, and `--rewrite-location` so
  the redirects stay on the gateway (`--browser` includes it).
- **An intranet site asks for a password** — it wants Windows auth. Add `--windows-auth`, or
  `"windowsAuth": true` on that site.

## A timeout

The request took longer than the timeout. Raise it on the request line or with `--timeout <seconds>`.

## No certificates in the dropdown

The list shows every certificate in your store that has a private key. Ones without the
**client-auth** capability are listed last, marked `(no client-auth EKU)`. Check that the filter box
beside the list is not hiding the one you want, and press **F5** to refresh after importing one. Add the machine store with `--store LocalMachine`
(CLI) if your certificate lives there. You can always load one from a file with **From file…** /
`--cert-file`.

## Windows Integrated Auth isn't working

- For **SSO** (single sign-on), you must be signed in with an account the target accepts (usually
  domain-joined).
- **Kerberos** needs the target's SPN (service principal name) registered; otherwise it falls back to
  **NTLM** (NT LAN Manager) — try explicit
  `--windows-user DOMAIN\user --windows-password …` to isolate a credential problem.
- Test the mechanism locally against the mock's `/windows-auth` route (see [Mock Server](18-Mock-Server.md)).

## The Rendered tab is blank or errors

The Rendered view needs the **WebView2** runtime (ships with Windows 11 / current Windows 10). If it's
unavailable, the tab says so; the rest of the app is unaffected. Install the Evergreen WebView2 Runtime
from Microsoft if you need it.

If the view stops, the bar above it says which WebView2 process ended or hung; **Reload** starts it
again. WebView2 keeps its profile in `%LOCALAPPDATA%\CertApiTester\WebView2`. Before 1.96.0 it was
a `ApiTester.App.exe.WebView2` folder beside the exe, which you can delete.

## The command line or the app freezes a remote desktop (VDI, Citrix, RDP)

There are two different freezes, and they have different causes.

**Only the sites through the gateway stop.** Everything else on the machine works. That is the
gateway waiting on something. Before 1.96.0 the usual cause was a click in its console window,
which pauses every write to it; press Esc. From 1.96.0 the gateway never waits on its console
([the request log](19-Local-Gateway.md#the-request-log)).

**The whole session stops.** Every work site, not only those through the gateway, stops
answering. New programs (Task Manager, Notepad) don't start, although open windows still react,
and nothing recovers until a restart. That is Windows itself stuck, or something every program
waits on:

- the security software under the network stack and under every program start (antivirus and
  endpoint protection, data-loss and web filters);
- `lsass.exe`, which signs with your software certificate's key, answers Windows sign-in
  (Kerberos, NTLM) and runs DPAPI;
- the machine running out of ports for new connections.

A program like certapi cannot do this directly, but its traffic can set it off. So from 1.96.0
the gateway keeps what it asks of all of them bounded
([limits and health](19-Local-Gateway.md#limits-and-health)):

- at most 16 connections per site and 256 requests at once;
- a pause for an address that keeps failing, instead of dialling it again for every retry;
- at most 15 seconds for a name lookup, and for each address a connect tries;
- with a PAC script or WPAD, the system proxy asked once per site, not on every request;
- with `--insecure`, nothing downloaded for the certificate chain (each fetch, on a network
  without internet, waits about 15 seconds per connection).

Every command also stops leaving the key of a `--cert-file` certificate behind in your profile,
and stops looking your user name up in lsass for each request through a proxy; a workspace save
re-encrypts only what changed (each DPAPI call goes through lsass), tab completion no longer
decrypts the whole workspace, and the app decrypts it once at start instead of twice.

When it happens:

1. **Is it everything, or only the gateway's sites?** Try a site that doesn't go through certapi,
   and try to start Notepad. If Notepad won't start either, program starts are being held up, most
   often by security software. If only Task Manager won't start, that can be its elevation: for an
   administrator it starts elevated, which goes through lsass.
2. **Keep the gateway's log in a file that survives a restart**:
   `certapi serve … 2> "$env:LOCALAPPDATA\certapi-gateway.log"`, or a folder IT says is kept on
   the remote desktop. A network drive such as `H:` is the last choice: when the network is what
   stops, the last lines may never arrive. Once a minute (with `-q` too) the gateway writes a
   health line: requests, connections, threads, handles, memory, and the machine's TCP
   connections. The lines before the freeze show whether the gateway was piling something up, or
   sat quietly while the machine went down around it. `--trace` writes from each request as it
   happens; on a remote desktop give it `--trace-file`.
3. **Ports.** If the machine ran out, the System event log has Tcpip events 4227 or 4231, and
   `(Get-NetTCPConnection -State TimeWait).Count` is in the thousands. The health line warns
   from 10,000 TCP connections on the machine. `netstat -ano` shows which process holds them.
4. **Ask IT what their security software recorded at that time.** Its console shows whether it
   blocked, quarantined or isolated something, or reported itself overloaded. If certapi.exe is
   involved, the usual remedies are theirs to apply:
   - an exclusion for certapi.exe from real-time scanning and from network inspection. Each
     release lists the exe's SHA-256 in `SHA256SUMS.txt`, so they can name exactly the file.
   - not inspecting loopback traffic (127.0.0.1). The gateway's browser side is plain HTTP there
     and carries every page in the clear.
   - for Citrix, turning off its API hooks for certapi.exe (Citrix documents a
     `UviProcessExcludes` value for this), and leaving it out of Workspace Environment
     Management's memory trimming.

   certapi.exe is not code-signed, so security software has no publisher to trust it by and, on
   a network without internet, cannot look up its reputation either. An allow-list entry by hash
   is the way around that.

   Some certapi features do exactly what security software is built to notice, and are best
   cleared with IT first: `serve --tls-trust` installs a root certificate, `serve --allow "*"`
   relays to any host, `fuzz` probes a site for endpoints (a scan), and `bench` is a load test.
5. **On a network without internet, add `--offline`** to a gateway or a `send` that checks
   certificates. It makes a missing intermediate an error instead of a download that waits
   15 seconds.

Things that slow a remote desktop without freezing it:

- **Console output.** `serve` shows at most 20 requests a second in a console window, and `send`
  does not print a binary body there and stops a text body after 1 MB.
- **The app's views.** The app shows the first 1 MB of a body (Copy and Save… still take all of
  it), keeps the stream and mock-server logs bounded, sends off the UI thread, and starts
  WebView2 with `--disable-gpu` in a remote session. An administrator's
  `WEBVIEW2_USER_DATA_FOLDER` and `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` take precedence over the
  app's choices; `CERTAPI_WEBVIEW2_GPU=1` keeps the GPU in a remote session that has one.
- **Copying a large body.** On Citrix the clipboard is copied to your own computer too. Use Save…
  for anything large.
- **Many `send` calls in a loop.** Each one is a new process with its own connection, handshake
  and workspace load. `certapi run` sends a whole collection from one process over pooled
  connections.

When reporting a freeze, send the gateway log written with `2>`, what Task Manager showed for
certapi.exe and `lsass.exe` (CPU, memory, handles, threads) if it would open, and whether Notepad
started.

## Headless runs don't save results

While the app is open, `certapi run` against the **live** workspace deliberately skips writing results
back (the app would overwrite them on close). Use a `--workspace` file with `--record`, or close the
app, for headless runs that persist.

## Where's my data?

The workspace — requests, collections, history, environments, tokens, settings — is in
`%AppData%\CertApiTester\state.json`. Back it up before experimenting; delete it to start fresh (the
app recreates it). Two things live elsewhere: the per-user configuration file,
`%APPDATA%\certapi\config.json` (see [Configuration](27-Configuration.md)), and the gateway's
certificate, cached in `%AppData%\CertApiTester\gateway-tls`.

## My tokens disappeared after I copied state.json to another machine / logged in as another user

This is expected. Secrets in the workspace — captured tokens and cookies, a saved request's auth
secret, a variable marked **secret** — are encrypted with the Windows Data Protection API (DPAPI) for
the Windows user who saved them, and DPAPI deliberately can't decrypt a value for anyone else. Loading
the file as a different user, or on a different machine, names each secret it couldn't read (as a
`warning:` on stderr, or in the app's status line) and treats it as absent — dropped if it was a
captured token/cookie, left empty otherwise. Everything else in the workspace — requests, collections,
chains, history, environments — loads intact; just log back in / re-capture / re-enter the secret and
carry on.

## What is this `state.json.20260727-143005.bak` file?

That's a one-time backup of your previous workspace file, taken automatically right before the first
save that rewrites it in the new encrypted format — so upgrading to the encrypted format can't lose
the copy you had before. It's a plain snapshot of what the file looked like right before that save.
Safe to keep for a while as a fallback, or delete once you're satisfied the upgraded file is fine.

Next: [FAQ](24-FAQ.md).
