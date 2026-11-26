# certapi Quick Reference, Configuration & Known Issues

Install alternatives, sample CLI output, command overview, configuration summary, tested environments, known issues, and build commands for `certapi`. For the complete 21-chapter handbook and every CLI option see [`wiki/README.md`](../wiki/README.md) and [`wiki/21-CLI-Reference.md`](../wiki/21-CLI-Reference.md).

## Install Options

- **Direct release binaries:** Download `certapi.exe` (CLI) and `ApiTester.App.exe` (WPF desktop app) from the [latest release](https://github.com/Real-Fruit-Snacks/certapi/releases/latest). Both are self-contained with no installer and no admin rights. Add the folder to `PATH` to run `certapi` from any prompt; `certapi update` replaces both after verifying GitHub's published SHA-256.
- **Scoop:** `scoop install https://github.com/Real-Fruit-Snacks/certapi/releases/latest/download/certapi.json` (then `scoop update certapi`).
- **Offline:** Copy both executables (plus `msquic.dll` for `--http3`) across — zero network or `.NET` install required. To build offline, use `certapi-<version>-dev-windows-x64.zip` from the release ([Developing offline](../CONTRIBUTING.md#developing-offline)).

## What It Prints

The same request without and with a certificate (status line to stderr, body to stdout):

```text
PS> certapi send https://client.badssl.com/
400 Bad Request · 262 B · 680 ms · TLS 1.2
PS> certapi send https://client.badssl.com/ --cert-file badssl.com-client.p12 --cert-password badssl.com
200 OK · 687 B · 777 ms · TLS 1.2 · client cert presented
```

When a server turns you away, `certapi doctor` connects one stage at a time and, at the TLS stage, reads which authorities the server takes client certificates from and checks every certificate in your store against them ([full transcript](art/doctor-with-cert.txt); cover rendered by [`make-seal.py`](art/make-seal.py)):

```text
  [ok  ] tls      handshake succeeded (Tls12)  (397 ms)
           client certificate presented: CN=BadSSL Client Certificate, O=BadSSL, L=San Francisco, S=California, C=US
           the server accepts client certificates from 1 authority/authorities:
             CN=BadSSL Client Root Certificate Authority, O=BadSSL, L=San Francisco, S=California, C=US
           NONE of your 5 certificate(s) are issued by any of those authorities
           the certificate you chose IS issued by one of them
  [ok  ] http     HTTP/1.1 200 OK  (88 ms)
```

## Commands Overview

Two front ends over one engine (`ApiTester.Core`). The desktop app covers everything below with a window; `certapi` covers it from a script.

| Command | Does |
|---|---|
| `certapi send <url>` | Sends one request with a store certificate; `--capture`, `--cookie-jar`, `--diff`, `--har`, `--trace`, `--wire`, `--retry`, and curl's `-b`, `-w`, `-C`, `--data-urlencode`, `--connect-to`, `--unix-socket`, `--pinnedpubkey` |
| `certapi call <spec> <operationId>` | Calls an OpenAPI operation by name and checks the answer against the spec |
| `certapi run <name\|path\|.har>` | Runs saved requests, a collection, a chain or a HAR as a pass/fail suite with exit codes |
| `certapi doctor <url>` | Connects stage by stage and names the stage that broke, with the CAs the server accepts |
| `certapi cors <url> --origin <o>` | Preflight, redirects, cookies, CORP/Timing-Allow-Origin verdict; `--audit`, `--diff`, `--from-har` / `--from-openapi`, `--suggest` |
| `certapi tls <host[:port]>` | Handshakes with any TLS service (STARTTLS too) and shows the chain and accepted client CAs |
| `certapi serve` | Local mTLS gateway in front of one site or many: named sites, `--vhost`, `--browser` for SSO flows, profiles |
| `certapi mock` | Standing test server over HTTP, HTTPS or mTLS, or HAR replay with optional `--cors` |
| `certapi grpc list\|call` | gRPC behind mutual TLS via reflection or `--protoset`; gRPC-Web with `--grpc-web` |
| `certapi token` | OAuth 2.0 client-credentials, password, refresh, device code, auth-code + PKCE, `private_key_jwt`, `tls_client_auth` |
| `certapi ws` / `certapi sse` | WebSocket and Server-Sent Events through the same certificate |
| `certapi signalr` / `certapi mqtt` | SignalR hub (invoke, stream, listen) and MQTT broker (`mqtts://`, `wss://`) with client certificates |
| `certapi browse <url>` | Renders a certificate-protected page in WebView2: screenshot, PDF, DOM, script result |
| `certapi crawl <url>` | Walks a site's links with the certificate: broken links or offline mirror |
| `certapi fuzz <url>` | Probes a wordlist to map an undocumented API |
| `certapi bench <url>` | p50/p90/p99 latency over a pooled connection; retries forced off |
| `certapi import` / `certapi export` | cURL, Postman, Insomnia, Bruno, OpenAPI, WSDL, `.http`, HAR, workspaces, code snippets, markdown notes |
| `certapi mcp` | Model Context Protocol server for an AI agent, behind a host allowlist |

Also: `certs`, `trust`, `proxy`, `connections`, `config`, `selftest`, and `completion` for PowerShell, Bash and Zsh tab completion. Every flag is in [`wiki/21-CLI-Reference.md`](../wiki/21-CLI-Reference.md).

```powershell
certapi send https://internal.corp/api/orders --cert "CN=matt"
certapi send https://internal.corp/auth --capture token=access_token --workspace team.json
certapi run "internal api" --env Prod --json
certapi doctor https://internal.corp/api/orders --cert "CN=matt"
certapi serve https://internal.corp --port 8819 --cert "CN=matt" --browser
certapi serve --port 8819 --cert "CN=matt" --upstream api=https://api.corp --upstream hr=https://hr.corp --check
certapi serve --port 8819 --cert "CN=matt" --allow "*.corp" --proxy http://proxy.corp:8080 --ca-file corp-root.pem
certapi serve --profile corp
certapi --version
```

Body goes to stdout, diagnostics to stderr. Exit codes: `0` success · `1` failure · `2` usage · `3` data.

## Configuration

Nothing is required. Optional:

- `certapi.config.json`, found by walking up from the working directory, or named with `--config` or `CERTAPI_CONFIG`, or at `%APPDATA%\certapi\config.json`. Profiles hold certificate, proxy, bypass list, `caFile`, `tlsMin`, `connectTimeout`, revocation, retries, timeout, workspace, standing headers, and a whole `serve` gateway; `--profile corp` selects one, typed flags win, `--no-config` ignores all of it. Any value may be `${env:NAME}`. `certapi config show` prints the active profile with secrets masked.
- **Networks:** System proxy (WPAD/PAC) is the default; `HTTPS_PROXY`/`NO_PROXY` are honoured (`--no-env-proxy` ignores them); `--proxy` (http or socks), `--pac <url|file>`, `--no-proxy`, `--noproxy <list>` override per run; `--offline` touches nothing but the request; `-4`/`-6` pick an address family. A TLS-intercepting proxy's private root goes in `--ca-file`, not `--insecure`. A pin can name the leaf, issuer (`trust add --ca`), or public key (`--spki`).
- **Certificates:** `--cert` by subject or thumbprint, narrowed by `--cert-issuer`/`--cert-serial`; `--cert-file` for PEM/PFX; `--cert-pin` or `CERTAPI_CERT_PIN` for a smart card or HSM in a headless job.
- **Gateway (`serve`):** Fronts named sites (`--upstream api=https://api.corp`), per-site certs/trust/Windows auth/headers (`--routes <file>` or `serve.sites`), one origin per site (`--vhost`), and `--allow` hosts; `--check` prints the routing table without starting. Supports `--browser`, `--cookie-jar`, `--auto-token`, `--windows-auth`, and LAN sharing (`--listen` + `--token`).
- `{{env:NAME}}` in a request reads the process environment, so CI credentials are never stored.
- State lives in `%APPDATA%\CertApiTester\state.json`. Secrets (tokens, cookies, passwords, secret variables, stored bodies) are DPAPI-encrypted for the Windows user; `certapi export workspace` strips them by default.

## Tested On

- Windows 11 23H2 · .NET 10 SDK 10.0.404 or later
- Windows Server 2022 · headless `certapi` only
- GitHub Actions `windows-latest` on every push: build with warnings as errors, then `dotnet test`
- *Not* tested: Windows 10 before 22H2, ARM64 Windows. Probably works, no promises.

## Known Issues

- The Rendered view and `certapi browse` need the Edge WebView2 runtime. Windows 10 and 11 ship it; without it that one tab (or command) says so and everything else works.
- `grpc --grpc-web` carries unary and server-streaming calls only (gRPC-Web cannot stream requests) and needs `--protoset`.
- `certapi mqtt` does QoS 0 and 1, not 2; `certapi signalr` speaks the JSON hub protocol over WebSockets only.
- `certapi serve` does not proxy gRPC.
- `--protoset` needs a descriptor set you already have; certapi does not compile `.proto` files.
- `--http3` is Windows 11 / Server 2022 and direct connections only, and needs the release's `msquic.dll` beside the exe (`certapi update` and Scoop install it).
- Through a proxy, TLS version, cipher, and certificate-presented details are not visible (`--no-proxy` restores them).
- Over the plaintext gateway, `__Host-`/`__Secure-` cookies cannot work; use `serve --tls`.
- `serve --rewrite-body` rewrites absolute URLs only in readable UTF-8 text bodies up to 32 MB; `--vhost` gives each upstream its own origin and needs no rewriting.
- `serve --windows-auth` answers the upstream's challenge, not the proxy's.
- `certapi doctor` cannot probe through a SOCKS proxy; `send --debug --trace` can.
- A proxy that itself requires a client certificate (mTLS to the proxy) is not supported by .NET's HTTP stack.
- `--pac` and `--cert-pin` are Windows-only (WinHTTP, CNG).
- `--sni` works on direct connections only; through a proxy it is refused.

## Build From Source

```powershell
dotnet build
dotnet test --filter "Category!=StoreRoundTrip"
dotnet publish src/ApiTester.Cli -c Release -r win-x64 --self-contained -o publish
```

Windows only; the desktop app is WPF.
