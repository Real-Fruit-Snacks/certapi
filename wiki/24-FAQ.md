# 24. FAQ

Frequently asked questions (FAQ), short answers.

**Is it free / open source?**
See the `LICENSE` file in the repository.

**Do I need to install anything?**
No. The released `.exe` files are self-contained — the .NET runtime is bundled. They're portable; no
installer, no admin rights. The only optional dependency is the WebView2 runtime, used solely by the
Rendered tab.

**Windows only?**
Yes. It's built on WPF (Windows Presentation Foundation) and the Windows certificate store — that's
the whole point. There's no
macOS/Linux build.

**Does it store my private keys?**
No. Certificates in the Windows store are used in place — the private key never leaves the store. For
file certificates, the app loads the key through a temporary, exportable PKCS#12 container (a
temporary per-user key container) so the handshake works, then discards it; nothing is written back
to the file or imported into the store.

**Can I use it without any certificates?**
Yes. Leave the certificate on *— no certificate —* and it's an ordinary API (application programming
interface) client. mTLS (mutual Transport Layer Security) is there when
you need it.

**How do the app and the CLI (command-line interface) relate?**
They're two front ends over one engine, sharing the workspace at
`%AppData%\CertApiTester\state.json`. Build a request in the app and run it with `certapi run`, or the
reverse. See [Core Concepts](04-Concepts.md).

**Where are secrets stored, and are they encrypted?**
In the same workspace file as everything else, but a captured token/cookie, a saved request's auth
secret, a saved proxy password, any variable marked **secret**, and stored response bodies and
credential response headers (in history and known-good snapshots) are encrypted with the Windows
Data Protection API (DPAPI), scoped to the Windows user who saved them — never readable by a
different Windows user or a different machine. They stay in clear text only when per-user
encryption is unavailable on the system, and the app reports that when it happens. Everything else in the file (requests, headers, URLs, names) is
plain JSON so it stays diffable. See [Troubleshooting](23-Troubleshooting.md) for what happens when a
secret can't be decrypted.

**Can I keep a request suite in source control?**
Yes — export a workspace (`certapi export workspace -o suite.json`) or manage a `--workspace` file, and
check it in. By default secrets are stripped from an export (`--include-secrets` keeps them, encrypted
for you), so a checked-in workspace doesn't hand out credentials.

**Does it do OAuth?**
Yes — client-credentials, password, refresh, and interactive authorization-code with PKCE (Proof
Key for Code Exchange) in the app; client-credentials, password, refresh and device code via
`certapi token` (device code is CLI-only). See [Authentication](08-Authentication.md).

**Does it do Windows Integrated Auth (NTLM (NT LAN Manager) / Kerberos)?**
Yes — a *Windows (integrated)* auth type with SSO (single sign-on) or explicit credentials, and
`--windows-auth` on the
CLI.

**Can an AI (artificial intelligence) agent use it?**
Yes — `certapi mcp` exposes a safe toolset over MCP (Model Context Protocol) with a pinned
certificate and a host allowlist. See
[MCP Server](20-MCP-Server.md).

**The site logs in through a web page, not an API — can I still reuse the session?**
Yes — **Capture session…** opens a browser, you log in on the site itself, and it captures the
resulting cookies and any bearer token, then attaches them automatically to later requests (app and
CLI). See [Session Capture](26-Session-Capture.md).

**Can I test without a real API?**
Yes — `certapi mock` (or **Mock server…** in the app) is a standing local server that echoes requests
and serves `/status`, `/sse`, `/token`, `/windows-auth`, and a WebSocket echo, over HTTP (Hypertext
Transfer Protocol)/TLS/mTLS. See
[Mock Server](18-Mock-Server.md).

**How do I reach a cert-protected API from a tool that can't do mTLS?**
Run `certapi serve <upstream> --port 8819 --cert …` and point the tool at the local port. See
[Local Gateway](19-Local-Gateway.md).

**Can one gateway front many sites?**
Yes. Name each one — `--upstream api=https://api.corp --upstream hr=https://hr.corp` — and each is
at `http://127.0.0.1:8819/<name>/`, or on its own origin (`http://api.gw.localhost:8819/`) with
`--vhost`. `--allow "*.corp"` reaches any matching host without listing it. The gateway prints a
table of what it built; `--check` prints it without starting.

**What if the sites need different certificates or settings?**
Put them in a `--routes` file (or a profile's `serve.sites`): each site can have its own `cert`,
`insecure`, `caFile`, `revocation`, `windowsAuth` and `headers`, and inherits everything it leaves
out from the gateway's flags.

**How do I keep a gateway I start every day?**
Put it in a profile's `serve` section in `certapi.config.json`; then `certapi serve --profile corp`
is the whole command. See [Configuration](27-Configuration.md#a-gateway-in-a-profile).

**Is `--insecure` dangerous?**
It skips **server** certificate validation, so only use it for internal/self-signed servers you trust.
It has nothing to do with your client certificate, which is still presented.

**Does it check certificate revocation?**
Only if you ask: `--revocation none|offline|online` defaults to `none` (no check).
`offline` checks cached certificate revocation lists (CRLs) only; `online` may also query an Online
Certificate Status Protocol (OCSP) responder. A revoked certificate is refused even past a pinned
thumbprint; an unreachable or blocked check (common on a corporate network) is reported but not fatal
unless you add `--revocation-strict`. See [Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation).

**Where do I report a bug or ask for a feature?**
Open an issue on the repository.

Next: [Building from Source](25-Building-from-Source.md).
