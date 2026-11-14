# 18. Mock Server

A standing local server you can fire requests at — the persistent counterpart to the one-shot
[self-test](23-Troubleshooting.md#self-test). It echoes each request back as JSON (JavaScript Object
Notation) and serves a handful
of fixed routes, over plain HTTP (Hypertext Transfer Protocol), HTTPS (HTTP Secure), or **mutual TLS
(mTLS — Transport Layer Security)**. Point the app at itself to exercise nearly
every feature without a real API (application programming interface).

## Start it

**App:** click **Mock server…** in the status bar. Pick a mode (Plain HTTP / HTTPS / Mutual TLS) and a
port, press **Start**, and watch a live request log. **Copy URL** drops the address into a request;
**Open certs** reveals the generated certificates (TLS modes).

**CLI (command-line interface):**

```powershell
certapi mock                       # plain HTTP on 8770
certapi mock --port 9000 --tls     # HTTPS with a generated server cert for localhost
certapi mock --mtls --cert-dir .\c # mutual TLS; writes certs to .\c
```

It runs until `Ctrl+C` and logs each request (`-q` turns the log off). `--port 0` picks a free port.

## Routes

| Route | Response |
|---|---|
| `/` (any path) | Echoes the request as JSON: method, path, query, headers, body, and — under mTLS — the client certificate you presented |
| `/status/<code>` | Responds with that HTTP status (e.g. `/status/404`, `/status/503`) |
| `/sse` | A short `text/event-stream` — try it with `certapi sse` |
| `/token` | An OAuth 2.0 token response — try it with `certapi token` |
| `/windows-auth` | Challenges with `401 WWW-Authenticate: NTLM`, then accepts the handshake — try it with `--windows-auth` |
| `/cookie-auth` | Sets a session cookie, then reports authenticated once you send it back — try it with [Session Capture](26-Session-Capture.md) |
| *Upgrade: websocket* (any path) | A WebSocket echo — try it with `certapi ws` |

A WebSocket upgrade is echoed on any path, also under `--routes` and `--har`, unless the scenario's
[`require`](#requiring-authentication) block refuses it.

## Declaring your own routes

The built-in routes echo; `--routes` makes the mock answer like *your* API, without capturing a
session first:

```powershell
certapi mock --routes .\orders.json --port 8770
```

```jsonc
{
  "routes": [
    {
      "match": {
        "method": "GET",
        "path": "/api/orders/*",              // * within a segment, ** across them
        "query":   { "status": "open" },      // every pair listed must be present
        "headers": { "Accept": "application/json" }
      },
      "respond": {
        "status": 200,
        "headers": { "Content-Type": "application/json" },
        "body": "{\"orders\":[]}"             // or "bodyFile": "orders.json"
      }
    },
    { "match": { "pathRegex": "^/orders/[0-9]+$" }, "respond": { "status": 404 } }
  ],
  "fallback": { "status": 418, "body": "nothing declared for this" }
}
```

Rules worth knowing:

- **Top to bottom, first match wins** — so a narrow route written above a broad one shadows it
  deliberately.
- **A route says what it *requires*.** Extra query parameters and headers on the request do not
  prevent a match.
- **How values compare.** Paths match case-insensitively; a header value matches when it contains
  the declared text, ignoring case; a query value must be exactly the declared value.
- **`bodyFile` is resolved against the scenario file's own folder**, so a scenario and its bodies
  move together, and it is served byte for byte. A `bodyFile` that cannot be read is named in a
  warning; the route is kept and answers with an empty body.
- **With `--routes` the built-in routes are not served.** A request matching no route gets your
  `fallback`, or a 404 with `{"scenario":"no route"}`.
- **A route that cannot be used is dropped and named** — an uncompilable `pathRegex`, a missing
  `respond`, a status outside 100–599 — rather than silently ignored. Comments and trailing
  commas are allowed, because these files are written by hand.

Combine it with `--har` when you want both: the declared routes cover what you care about, and a
request they miss falls through to the recorded session. The `fallback` is then unused.

## Replaying a capture

`--har <file>` answers from a captured HTTP Archive instead of the built-in routes: each request
gets the recorded response for its method and path (query included when it disambiguates), in
recorded order for a repeated call.

```powershell
certapi mock --har session.har --port 8770
certapi mock --har session.har --no-match-status 501
```

`--no-match-status <code>` (default `404`, only with `--har`) is the status for a request that
matches nothing in the archive.

## Making it misbehave

A test server earns its keep by behaving *badly* on demand. Any `respond` block can carry:

```jsonc
"respond": {
  "status": 200,
  "delayMs": 500,           // pause before the first byte — trips a client timeout
  "jitterMs": 200,          // random spread on top, so calls aren't a metronome
  "dripBytesPerSec": 256,   // send the body slowly — trips a read timeout
  "then": "abort"           // "abort" closes after the headers; "reset" sends a TCP reset
}
```

An unknown `then` value is named in a warning and ignored.

And a route can answer **differently on each call**:

```jsonc
{
  "match": { "path": "/flaky" },
  "respondSequence": [
    { "status": 503, "headers": { "Retry-After": "1" } },
    { "status": 503 },
    { "status": 200, "body": "recovered" }
  ]
}
```

The first call gets the first entry; once the list runs out the last entry repeats. That is the
shape a **retry policy** has to be tested against:

```powershell
certapi mock --routes .\flaky.json --port 8770
certapi send http://127.0.0.1:8770/flaky --retry 3 --retry-on 503
```

The send succeeds after two failures — and the mock's log shows three real requests arriving, which
is the difference between a retry that happened and one you assumed happened.

With `then: "abort"` the headers promise a body that never comes, so a client must report a failure
rather than hand back a short body as if it were whole; `then: "reset"` tears the connection down
the way a middlebox or a crash does. Declaring both `respond` and `respondSequence` on one route is
refused by name — it is a contradiction, not a merge.

## Requiring authentication

A scenario can demand credentials before it routes anything, so a client meets a realistic refusal
rather than an echo:

```jsonc
{
  "require": {
    "clientCert": { "issuer": "CN=Corp Issuing CA" },   // or "thumbprint", or just true for "any"
    "bearer": "expected-token",
    "onFail": 401                                        // 401 (default), 403, or 407
  },
  "routes": [ { "match": { "path": "/secure" }, "respond": { "status": 200, "body": "admitted" } } ]
}
```

- **Checked before routes, and before a WebSocket upgrade**, so a scenario is both "requires a
  bearer" *and* "answers these paths", a refusal can never leak a route's body, and a protected
  mock refuses an unauthenticated socket.
- **The refusal carries a challenge** — `WWW-Authenticate`, or `Proxy-Authenticate` for 407 — so
  the client sees the shape a real endpoint would send, not a bare status.
- **`clientCert: true`** means a certificate is required but whose does not matter; add `issuer` or
  `thumbprint` to narrow it. The certificate is checked at the *application* layer, after the
  handshake, which is how a real service behaves.
- A `require` block that asks for nothing, or an `onFail` that is not 401/403/407, is named in a
  warning rather than silently obeyed.

## Serving a deliberately broken certificate

Every TLS error this tool reports — expired, wrong host, untrusted — is easy to *read about* and
hard to *reproduce*. `--tls-mode` makes each one happen on demand:

```powershell
certapi mock --tls --tls-mode expired --port 8443
certapi send https://localhost:8443/api/x          # refuses, as it should
certapi doctor https://localhost:8443/api/x        # and doctor explains why
certapi send https://localhost:8443/api/x --insecure   # the escape hatch still works
```

| Mode | The certificate is… |
|---|---|
| `valid` (default) | fine — issued by the mock's own certificate authority, for `localhost` |
| `expired` | issued for `localhost`, but its validity ended an hour ago |
| `wrong-host` | perfectly valid, and issued for a **different** host, so the name check fails |
| `self-signed` | its own issuer — nothing chains to the mock's authority at all |

Needs `--tls` or `--mtls`; over plain HTTP there is no certificate to spoil, and asking is a usage
error rather than a silent no-op. The mock prints a line reminding you that clients are *supposed*
to refuse this one, so a red result reads as success.

This is what turns the mock into a test bed for the client: `doctor`'s TLS stage, `send`'s
`ServerCertificateUntrusted`, and the `--insecure` override can all be exercised without a real
broken endpoint to point at.

## Behaving like a server with a CORS policy

By default the built-in routes answer `Access-Control-Allow-Origin: *`, which lets any page read
them — and hides every CORS problem a front end will meet against the real API. `--cors` makes the
mock behave like a server with a policy: it answers preflights itself and puts the exact origin,
`Access-Control-Allow-Credentials: true`, `Vary: Origin` and an `Access-Control-Expose-Headers` list
on every response to a page.

```powershell
# any origin allowed, credentials too
certapi mock --cors

# only the app's origin: everything else gets a 403 preflight and no CORS headers
certapi mock --cors http://localhost:3000 --routes .\api.json
```

Check what a browser would make of it with [`certapi cors`](21-CLI-Reference.md#cors):

```powershell
certapi cors http://127.0.0.1:8770/orders --origin http://localhost:3000 -X PUT -H "X-Api-Key: k" --credentials
certapi cors http://127.0.0.1:8770/orders --origin https://evil.example      # blocked
```

### Narrower policies, and broken ones

`--cors-headers`, `--cors-methods`, `--cors-expose` and `--cors-max-age` fix what a preflight grants
instead of granting whatever it asks; `--cors-star` answers `*` without credentials, which is what a
request with a client certificate or a cookie fails against. `--cors-fault` gets it wrong on
purpose — `double-origin`, `preflight-401`, `preflight-redirect`, `preflight-only` or `no-vary` —
to see how a client, or a check, copes with a server that is broken in a known way:

```powershell
certapi mock --cors http://localhost:3000 --cors-headers Content-Type --cors-methods GET,POST
certapi mock --cors --cors-star
certapi mock --cors --cors-fault preflight-401
```

### A policy per route

A route in a `--routes` file can say `"cors"` for itself — `false` for none, `true` for any origin,
or an object with the same settings as the flags. It replaces `--cors` for that route, so one mock
can serve a public endpoint, a locked-down one and one that is broken:

```json
{
  "routes": [
    { "match": { "path": "/public/**" }, "cors": { "star": true }, "respond": { "status": 200 } },
    { "match": { "path": "/internal/**" }, "cors": false, "respond": { "status": 200 } },
    { "match": { "method": "PUT", "path": "/admin" },
      "cors": { "origins": ["https://admin.example"], "methods": ["PUT"], "maxAge": 0 },
      "respond": { "status": 204 } },
    { "match": { "path": "/legacy" }, "cors": { "fault": "double-origin" }, "respond": { "status": 200 } }
  ]
}
```

A preflight is matched to the route its real request would reach, by the method it asks about.

## Connections

The mock keeps a connection open for the next request whenever the response can share it — one
framed by its length, not a stream, a WebSocket, a HEAD, a 204/304 or a route's deliberate fault —
so `bench` against it measures requests rather than handshakes (over HTTPS, the difference is
several times the throughput). A connection idle for 10 seconds is closed. `--no-keep-alive`
answers every response with `Connection: close`, to see how a client copes with a server that does
not keep connections:

```powershell
certapi mock --tls --no-keep-alive
certapi bench https://127.0.0.1:8770/ -n 500 --insecure    # the note says every request paid a handshake
```

## Modes and certificates

- **`--http`** (default) — plain HTTP; hit it with anything (curl, a browser, the app), no
  certificates.
- **`--tls`** — HTTPS with a generated server certificate for `localhost`, issued by the mock's own
  certificate authority (`mock-ca.cer`), not self-signed. It names `127.0.0.1` and `::1` too, and
  the mock listens on both, so `https://localhost:<port>` connects at once rather than after the
  two seconds Windows spends on a refused `::1` first.
- **`--mtls`** — HTTPS that **requires** a client certificate (any presented cert is accepted, and its
  subject is echoed back).

For the TLS modes the server writes its certificates to `--cert-dir` (default `.\certapi-mock-certs`):

- `mock-server.cer`, `mock-ca.cer` — trust these, or use `--insecure`.
- `mock-client.pfx` (mTLS only) — a ready-to-use client certificate to present.
- `mock-ca.key`, `mock-server.key` — the private keys the next start signs and serves with,
  encrypted to your Windows account (DPAPI). Another account, or another machine, cannot use them.

**The certificates are kept.** The next start in the same `--cert-dir` uses them again while they
are good, so `mock-ca.cer` stays the same: trust it once (`certutil -user -addstore Root
mock-ca.cer`) and every later start is trusted too; a `certapi trust` pin of the server certificate
and a `--cert-file mock-client.pfx` in a script keep working as well. Starting is quicker for it, since no keys are made.
They are made again — with a new `mock-ca.cer`, which the start says — when a file is missing or
unreadable, when the authority has less than about 400 days left or a certificate less than a day,
and when `--new-certs` asks for it. A `--tls-mode` certificate is made new every time and not
kept; the next ordinary start serves the kept good one again. Where the keys cannot be encrypted
(not Windows), nothing is kept and every start makes new certificates.

## Dogfood the whole app

Because the mock speaks the app's own protocols, you can drive `send`, `sse`, `ws`, and `token`
against it:

```powershell
certapi mock --port 8770
certapi send http://127.0.0.1:8770/orders -X POST -d '{"hi":1}'   # echoed back
certapi send http://127.0.0.1:8770/status/418 --include           # 418 I'm a teapot
certapi token --token-url http://127.0.0.1:8770/token --client-id demo
certapi sse http://127.0.0.1:8770/sse --max-events 3
certapi ws  ws://127.0.0.1:8770/ws -m "hello" --expect 1

# mutual TLS end to end
certapi mock --mtls --port 9443 --cert-dir .\c
certapi send https://localhost:9443/orders --cert-file .\c\mock-client.pfx --insecure
```

Next: [Local Gateway](19-Local-Gateway.md).
