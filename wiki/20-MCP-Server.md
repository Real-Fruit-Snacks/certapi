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
| `list_saved`, `list_environments`, `self_test` | none |

Bearer tokens seen in responses are captured in memory for the session and attached to later calls to
the same host (like the app's automatic tokens), unless you pass `--no-auto-token`. Values captured
by `run_saved` and `run_chain` resolve `{{variables}}` in later calls the same way. **Nothing is ever
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
failed) arrive as **logging notifications** whose minimum level the host sets with
`logging/setLevel`. Protocol revisions `2024-11-05` through `2025-06-18` are supported; an unknown
client version is answered with the newest supported one rather than echoed.

## Guardrails

The point of `mcp` is to give an agent **capability without keys**:

- **Pinned certificate** — `--cert` (or `--cert-file`) fixes the identity; the agent can't change it.
  A saved request that names its own certificate uses that one (an operator's earlier choice); one
  that names none uses the pinned certificate.
- **Host allowlist** — `--allow <host>` (repeatable). A request URL must match, or be a subdomain of,
  an allowed host. Ports are ignored, and a URL that is not `http://` or `https://` is always
  refused. **Omit `--allow` and any host is permitted — with a printed warning.** Always set
  an allowlist for anything but local experimentation. Enforced per request, including every chain
  step and every gRPC call.
- **Pinned trust, not blanket trust** — a host pinned with `certapi trust add` is reachable without
  `--insecure`, exactly as it is for `send`/`run`. `--insecure` remains the blunt launch-wide switch.
- **No redirects** — a 3xx is returned as data, never followed, so the allowlist cannot be escaped
  by a server-controlled hop.
- **No secrets in the transcript** — the agent asks the server to authenticate; the certificate and
  captured tokens stay on your side. Resources and `list_saved` withhold auth secrets, secret
  variable values, and credentials typed into headers, query strings and bodies.
- **Read-only against your files** — the workspace is never written back; captures and tokens live
  in memory for the session only.

## Options

| Option | Purpose |
|---|---|
| `--cert <thumb\|subject>` | The certificate all tools use (pinned) |
| `--cert-file` / `--cert-password` / `--key-file` | Pin a certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine`, which searches both stores |
| `--cert-issuer <name>` / `--cert-serial <hex>` | Pick the store certificate by issuer or serial number, as on [`send`](21-CLI-Reference.md#certificate-options) |
| `--cert-pin <pin>` | Smart-card or HSM PIN, supplied ahead of the prompt (or `CERTAPI_CERT_PIN`) |
| `--allow <host>` | Allowed upstream host (repeatable) |
| `--insecure` | Ignore upstream server-certificate errors (internal CAs — certificate authorities) |
| `--timeout <seconds>` | Deadline for `send_request`, `grpc_list` and `grpc_call` (default 100; a saved request keeps its own saved timeout). A gRPC call past it returns a tool error instead of holding up later calls; a stream cut off this way still lists the messages it delivered |
| `--workspace <file>` | Load saved requests / environments / chains from a workspace file (read once at launch). Without it, the app's live `state.json` is read |
| `--no-auto-token` | Don't capture/reuse bearer tokens across the session |
| `--protoset <file>` | Compiled descriptor set for `grpc_list`/`grpc_call` against a server without reflection — pinned at launch; the agent cannot name files |
| `--proxy` / `--proxy-user` / `--no-proxy` / `--noproxy` | The same proxy control as [`send`](21-CLI-Reference.md#send), applied to every call the tools make |
| `--revocation <mode>` / `--revocation-strict` | The same [revocation checking](06-Certificates-and-mTLS.md) as `send`, applied to every call |
| `--retry <n>` / `--retry-on` / `--retry-delay` | The same retry rules as `send` |
| `--ca-file` / `--tls-min` / `--connect-timeout` / `--sni` / `--resolve` | The same [trust and TLS options](21-CLI-Reference.md#trust-and-tls) as `send`, applied to every call |
| `--pac` / `--no-env-proxy` / `--offline` / `-4` / `-6` | The same network options as `send` |

## Examples

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
certapi mcp --cert "CN=Agent Client" --allow grpc.internal --protoset .\contracts.protoset
certapi mcp --cert "CN=Agent Client" --allow api.example.com --revocation online
```

The server runs until stdin closes or Ctrl+C (a second Ctrl+C quits at once). A message without an
`id` is a notification: nothing runs and nothing is answered. Exit 0 on a clean shutdown, 2 usage,
3 data error.

Next: [CLI Reference](21-CLI-Reference.md).
