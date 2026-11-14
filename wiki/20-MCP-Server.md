# 20. MCP Server (for AI agents)

`certapi mcp` runs a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server so an AI
(artificial intelligence) agent
can make **mutual-TLS (mTLS — Transport Layer Security)** API (application programming interface)
calls — using a certificate **you** pin at launch, bounded by a host
allowlist. The agent never handles the certificate itself; it just asks the server to make calls. It
speaks JSON-RPC (JavaScript Object Notation Remote Procedure Call) over **stdio** — nothing on the
network.

## Start it

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
```

Wire that command into your MCP-capable client (an agent framework, an IDE (integrated development
environment) assistant, etc.) as a
stdio server. The agent then has a small, safe toolset for talking to your API.

`--print-config` writes the configuration block for you, with the options you give it, and exits:

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com --print-config claude
```

```json
{
  "mcpServers": {
    "certapi": {
      "command": "C:\\Program Files\\certapi\\certapi.exe",
      "args": [ "mcp", "--cert", "CN=Agent Client", "--allow", "api.example.com" ]
    }
  }
}
```

Merge it into `claude_desktop_config.json` (Claude Desktop) or `.mcp.json` (Claude Code). `vscode`
gives the `servers` form for `.vscode/mcp.json`, and `cursor` the form for `.cursor/mcp.json`. File
paths such as `--workspace` are written out in full, because the host chooses the working
directory. A secret is never written into the file: `--cert-pin` is refused (put
`CERTAPI_CERT_PIN` in the host's `env` block instead), as are `--cert-password` and `--proxy-user`.

## Tools exposed

| Tool | Does |
|---|---|
| `send_request` | Make an mTLS request (method, URL (Uniform Resource Locator), headers, body) to an allowed host. Redirects are never followed — a 3xx comes back as itself, so every hop is an explicit, allowlist-checked call |
| `run_saved` | Run a saved request by its path, exactly as `certapi run` would: its saved transport settings, auth, assertions, and capture rules all apply |
| `run_chain` | Run a saved chain by name — its requests in order as one unit, each step seeing what earlier steps captured; every step's URL is checked against the allowlist, even one built from an earlier capture |
| `list_saved` | List saved requests from the workspace |
| `list_environments` | List environment names for `run_saved`/`run_chain`'s `env` argument — variable values are never returned |
| `list_certificates` | List the client certificates available |
| `grpc_list` | List a gRPC (remote procedure call) server's services and methods, via reflection or a descriptor set pinned at launch |
| `grpc_call` | Invoke a gRPC method — unary, server-streaming, client-streaming, or bidirectional — with the pinned certificate |
| `ws_exchange` | Open a WebSocket on an allowed host, send text messages, and return what comes back — until a count, a time or a close |
| `sse_read` | Read a Server-Sent Events stream on an allowed host and return the events that arrive — until a count, a time or the end of the stream |
| `self_test` | Prove the mTLS path end-to-end |

### Tool arguments

| Tool | Arguments |
|---|---|
| `send_request` | `url` (required); `method` (default `GET`); `headers` (an object); `body`; `contentType` (default `application/json` when there is a body; a `Content-Type` in `headers` sets it) |
| `run_saved` | `path` (required); `env`; `vars` (an object) |
| `run_chain` | `name` (required); `env`; `vars` (an object) |
| `list_certificates` | `filter` (matches subject, issuer or thumbprint) |
| `grpc_list` | `address` — optional when `--protoset` was given at launch |
| `grpc_call` | `address` and `method` (`Service/Method`) required; `data` (one message, or an array of messages for a streaming request); `metadata` (an object); `maxMessages` (default 100) |
| `ws_exchange` | `url` (`ws://` or `wss://`, required); `messages` (text to send, in order); `headers`; `maxMessages` (default 20; 0 sends only); `seconds` (default 10) |
| `sse_read` | `url` (required); `headers`; `lastEventId` (sent as `Last-Event-ID`, to resume); `maxEvents` (default 20); `seconds` (default 10) |
| `list_saved`, `list_environments`, `self_test` | none |

A stream tool always returns: it listens until its count is reached, its time is up (never longer
than `--timeout`), or the other side closes, and its result says which in `ended` (`count`, `time`,
`closed`, `max-body` or `broken`). Everything the stream sends counts against `--max-body` together.
`sse_read` returns the `lastEventId` to pass to the next call, so an agent can poll a feed without
missing or repeating an event. A WebSocket address is checked against the allowlist by its host,
like any other URL.

Bearer tokens seen in responses are captured in memory for the session and attached to later calls to
the same host (like the app's automatic tokens), unless you pass `--no-auto-token`. Values captured
by `run_saved` and `run_chain` resolve `{{variables}}` in later calls the same way. The `vars`
argument fills values into a saved request — an id, a query value — but cannot change its scheme,
host or port: a call whose `vars` would send a saved request (with its saved credentials) somewhere
else is refused. A saved request whose host only comes from `vars` is refused too; put the host in
an environment. **Nothing is ever
written back to disk** — the workspace is read once at launch, and the session's captures die with
the process.

## Resources and protocol features

Saved requests, environments, and chains are also published as **read-only MCP resources**
(`certapi://requests/…`, `certapi://environments/…`, and `certapi://chains` when the workspace has
chains), so a host can show them to the agent without a tool call. A resource withholds a request's
auth secret, secret variable values, and credential values typed into the request itself: a
secret-named header (`X-Api-Key`, `X-Auth-Token`, …), a credential query value (`?api_key=…`), and a
password or secret field in the body. A header or body value that is only a `{{variable}}` reference
is shown as written. `list_saved` redacts each URL the same way.

Tools carry the protocol's **behavioral annotations** (read-only, destructive, idempotent,
open-world hints) so a host's permission model has something structured to act on, results include
**structured content** alongside the text form, and server notes (tokens captured, assertions
failed) are written to stderr, and also sent as **logging notifications** just before the response
of the call that produced them. Arguments are checked against each tool's schema; a wrong type or an
unknown argument is a tool error that names the field.

The server is **dual-era**. A client that opens with `initialize` is served by revisions `2024-11-05`
through `2025-11-25` (an unknown client version is answered with the newest of those, not echoed;
`logging/setLevel` sets the notification level for the connection). A request that carries
`io.modelcontextprotocol/protocolVersion` in its `_meta` is served by revision `2026-07-28`:
`server/discover`, `resultType` and caching hints on every result, `-32022` for a version the
server does not speak, and notifications only for a request that sets
`io.modelcontextprotocol/logLevel`. Both can arrive on one process.

Calls run one at a time, in order, but the server keeps reading while one runs:
`notifications/cancelled` stops that call (and it answers nothing), and closing stdin gives queued
work two seconds to finish before it is cancelled. A malformed line is answered with a JSON-RPC
error; it never ends the session. A JSON-RPC batch (an array) is answered with an array.

## Guardrails

The point of `mcp` is to give an agent **capability without keys**:

- **Pinned certificate** — `--cert` (or `--cert-file`) fixes the identity; the agent can't change it.
  A saved request that names its own certificate uses that one (an operator's earlier choice); one
  that names none uses the pinned certificate. The same goes for a saved request's own "ignore
  server certificate" setting and its Windows sign-in: they travel with the request, which is why
  `vars` cannot move a saved request to another host.
- **Host allowlist** — `--allow <host>` (repeatable). A request URL must match, or be a subdomain of,
  an allowed host. Give a bare host name or IP address: an empty value, a scheme, port, path,
  wildcard or comma stops the launch with a usage error. Ports are ignored, and a URL that is not
  `http://` or `https://` is always refused. A `Host` header that differs from the URL's host is
  refused, as is gRPC `host` metadata, so the request cannot ask a shared front end for another
  name. A refusal names the host only, never the resolved URL. **Omit `--allow` and any host is
  permitted** — the start-up line on stderr says so, but an MCP host may never show it. Pass
  `--allow-any` to say you mean it; it cannot be combined with `--allow`. Always set
  an allowlist for anything but local experimentation. Enforced per request, including every chain
  step and every gRPC call.
- **Pinned trust, not blanket trust** — a host pinned with `certapi trust add` is reachable without
  `--insecure`, exactly as it is for `send`/`run`. `--insecure` remains the blunt launch-wide switch.
- **No redirects** — a 3xx is returned as data, never followed, so the allowlist cannot be escaped
  by a server-controlled hop.
- **Saved secrets stay out of the transcript** — the agent asks the server to authenticate; the
  certificate and the workspace's saved credentials stay on your side. Resources and `list_saved`
  withhold auth secrets, secret variable values, and credentials typed into headers, query strings
  and bodies. **What an upstream returns is not filtered**: response headers (including
  `Set-Cookie`) and bodies (including any token a login returns) reach the agent as received, up to
  `--max-body` (1 MB by default).
- **Your config file is not picked up from the project folder** — `mcp` ignores a
  `certapi.config.json` found by walking up from the working directory, which the MCP host chooses.
  `--config`, `CERTAPI_CONFIG` and the per-user file still apply, and the start-up line names the
  file and the settings it changes. `--insecure` adds a warning line of its own.
- **Read-only against your files** — the workspace is never written back; captures and tokens live
  in memory for the session only.
- **The app's logins come along by default** — without `--workspace` the desktop app's live
  `state.json` is read, so the session starts with its stored tokens and browser-captured cookies
  and attaches them to calls to the same origin. The agent never sees them but acts with them. For
  an agent that is not fully trusted, pass a dedicated `--workspace` or `--no-auto-token`.

## Options

| Option | Purpose |
|---|---|
| `--cert <thumb\|subject>` | The certificate all tools use (pinned) |
| `--cert-file` / `--cert-password` / `--key-file` | Pin a certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine`, which searches both stores |
| `--cert-issuer <name>` / `--cert-serial <hex>` | Pick the store certificate by issuer or serial number, as on [`send`](21-CLI-Reference.md#certificate-options) |
| `--cert-pin <pin>` | Smart-card or HSM PIN, supplied ahead of the prompt (or `CERTAPI_CERT_PIN`) |
| `--allow <host>` | Allowed upstream host (repeatable): a bare host name or IP address (`[::1]` for IPv6) |
| `--allow-any` | Allow every host, on purpose (the warning becomes a note); refused beside `--allow` |
| `--insecure` | Ignore upstream server-certificate errors (internal CAs — certificate authorities) |
| `--timeout <seconds>` | Deadline for `send_request`, `grpc_list` and `grpc_call` (default 100; a saved request keeps its own saved timeout). A gRPC call past it returns a tool error instead of holding up later calls; a stream cut off this way still lists the messages it delivered |
| `--workspace <file>` | Load saved requests / environments / chains from a workspace file (read once at launch). Without it, the app's live `state.json` is read, stored logins included |
| `--no-auto-token` | Don't capture/reuse bearer tokens across the session, and don't attach the workspace's stored tokens or cookies |
| `--max-body <size>` | Most of a response body a tool returns (default `1m`); the rest is not read and the result says `"truncated": true` |
| `--print-config <claude\|vscode\|cursor>` | Print the host configuration block for these options and exit |
| `--protoset <file>` | Compiled descriptor set for `grpc_list`/`grpc_call` against a server without reflection — pinned at launch; the agent cannot name files |
| `--proxy` / `--proxy-user` / `--no-proxy` / `--noproxy` | The same proxy control as [`send`](21-CLI-Reference.md#send), applied to every call the tools make |
| `--revocation <mode>` / `--revocation-strict` | The same [revocation checking](06-Certificates-and-mTLS.md) as `send`, applied to every call |
| `--retry <n>` / `--retry-on` / `--retry-delay` | The same retry rules as `send` |
| `--ca-file` / `--tls-min` / `--connect-timeout` / `--sni` / `--resolve` | The same [trust and TLS options](21-CLI-Reference.md#trust-and-tls) as `send`, applied to every call |
| `--pac` / `--no-env-proxy` / `--offline` / `-4` / `-6` | The same network options as `send` |

## Examples

```powershell
certapi mcp --no-config --cert "CN=Agent Client" --allow api.example.com
certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
certapi mcp --cert "CN=Agent Client" --allow grpc.internal --protoset .\contracts.protoset
certapi mcp --cert "CN=Agent Client" --allow api.example.com --revocation online
```

The server runs until stdin closes or Ctrl+C (a second Ctrl+C quits at once). A message without an
`id` is a notification: nothing runs and nothing is answered. A request with an `id` and a
`notifications/…` method is answered with "method not found". Exit 0 on a clean shutdown, 2 usage,
3 data error.

Next: [CLI Reference](21-CLI-Reference.md).
