# 5. The Interface

A guided tour of the desktop app. (CLI (command-line interface) users can skip to the
[CLI Reference](21-CLI-Reference.md).)

## The title bar

Along the top, from left to right:

- **☰ history toggle** (`Ctrl+H`) — show/hide the request-history sidebar.
- **certapi** — the app title.
- **ENV** — the active [environment](09-Environments-and-Variables.md) picker, plus **Edit** to
  manage environments and variables.
- **☀ / ☾ theme toggle** — switch between [light and dark](16-Response-Views.md#themes). Your choice
  is remembered.
- **? Help** (`F1`) — the built-in Help & Reference window.
- The window minimize / maximize / close controls.

## The request line

The heart of the app:

- **CERTIFICATE** — pick the client certificate to present (or *— no certificate —*).
- **Method** — GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS.
- **URL** (Uniform Resource Locator) — the address. If you've saved a website as a base URL, the box
  becomes just the path after
  it. Type a `?query` and it splits into the Params grid.
- **Stream** — open the [WebSocket / SSE (Server-Sent Events) console](15-Live-Streaming.md) for this URL.
- **Cancel** — abort an in-flight request (`Esc`).
- **Send** (`Ctrl+Enter`) — go.
- **TIMEOUT** — per-request timeout in seconds.

## The request tabs

Beneath the request line:

| Tab | Purpose |
|---|---|
| **Params** | Query-string key/value grid |
| **Headers** | Request-header key/value grid (tick to include) |
| **Body** | Text body or a `multipart/form-data` form; see [Building Requests](07-Building-Requests.md) |
| **Auth** | Auth type: Auto, None, Bearer, Basic, Windows Integrated — plus **Get OAuth 2.0 token…** |
| **Capture** | Rules that save response values into `{{variables}}`; see [Capturing Values](12-Capturing-Values.md) |
| **Tests** | Assertions that pass/fail the response; see [Testing & Assertions](11-Testing-and-Assertions.md) |

Above the tabs sits **Copy as ▾** (turn the request into a cURL / PowerShell / Python / C# snippet).

## The response panel

After you send, the lower panel fills in:

| Tab | Shows |
|---|---|
| **Pretty** | Syntax-highlighted JSON/XML (JavaScript Object Notation / Extensible Markup Language); hex for binary |
| **Raw** | The exact response bytes as text, with a **find** box |
| **Headers** | Response headers |
| **Diagnostics** | TLS (Transport Layer Security) version, cipher, whether your client cert was presented, the server chain |
| **Rendered** | The URL loaded as a web page (with your certificate) |
| **Network** | A browser-style trace of every HTTP (Hypertext Transfer Protocol) call, including resources the Rendered view fetched |

Use the **pop-out** button to detach a view — or the whole panel — into its own window. See
[Response Views](16-Response-Views.md).

## Tabs and the sidebar

- **Request tabs** across the top let you keep several requests open at once (`Ctrl+T` new,
  `Ctrl+W` close).
- The **sidebar** (`Ctrl+H`) holds **Collections** (your saved library) and **History** (recent
  requests). See [Collections & History](10-Collections-and-History.md).

## The status bar

At the bottom: the status/result line, the **token chip** (appears when a bearer token is active for
the current website — click it to inspect or clear), **Capture session…** (log in to a site in a
browser and [capture its session](26-Session-Capture.md)), **Mock server…** (open the
[mock server console](18-Mock-Server.md)), and **Run Self-Test**.

Next: [Certificates & mTLS](06-Certificates-and-mTLS.md).
