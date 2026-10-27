# 28. Settings & Toggles Reference

Every control in the desktop application: what it does, what it defaults to, and whether it is
remembered between sessions.

This is the app's counterpart to the [CLI Reference](21-CLI-Reference.md). For a guided tour of the
layout rather than an exhaustive list, read [The Interface](05-The-Interface.md) first.

> **Per-request, not global.** Most settings below belong to the **request tab** you are looking
> at, and are saved with the request when you save it into a collection. A setting that applies to
> the whole application says so.

---

## Title bar

| Control | Default | What it does |
|---|---|---|
| **ENV** selector | none | The active environment. Its `{{variables}}` are substituted into the URL, query, headers, body and auth when you send. Application-wide, and remembered |
| **Edit** (beside ENV) | — | Opens [Environments](#environments-window) |
| **Theme toggle** | dark | Switches between the dark and light themes. Application-wide, and remembered |
| **Update to x.y.z** | hidden | Appears when a newer release is found (Help › About › Check for updates, or the startup check). Installs it after checking each file's SHA-256, then restarts the app |
| **Help** | — | Opens the built-in handbook. **About** has **Check for updates** and **Check for a new release when certapi starts** (off by default; at most once a day; remembered) |
| Minimise / Maximise / Close | — | Window controls. Position, size and maximised state are remembered |

---

## The request line

| Control | Default | What it does |
|---|---|---|
| **Method** | `GET` | The HTTP method: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS`. The command line accepts any method string with `-X`, including one this list does not offer |
| **URL** | empty | The address. Paste one containing a `?query` and it splits into the Params grid automatically |
| **Saved websites** | none | Base URLs you have kept, so a host is chosen rather than retyped. **Forget** removes the selected one |
| **Certificate** | — no certificate — | The client certificate this request presents. **The default is no certificate**, which is a valid choice: the app is a general API client that happens to be very good at mutual TLS |
| **Refresh** | — | Re-read the Windows certificate store, for a certificate installed while the app was open |
| **From file…** | — | Load a certificate from a `.pfx`/`.p12` or `.pem`/`.crt` instead of the store, for one handed to you but never imported |
| **Ignore server cert errors (insecure)** | off | Accept any server certificate: expired, self-signed, wrong hostname. **This turns off the check that makes TLS meaningful** — prefer [Trusted certificates](#trusted-certificates-window), which pins one host and leaves every other host verified |
| **Send** | — | Send the request |
| **Cancel** | — | Cancel a request in flight |
| **Stream** | — | Open the URL as a stream instead — Server-Sent Events or WebSocket, chosen by scheme |

---

## Request tabs

### Params

An enable/key/value grid of query parameters.

| Control | Default | What it does |
|---|---|---|
| Row checkbox | on | Whether the parameter is sent. **Unticked rows are kept, not deleted** — that is the point of the column: park a parameter without losing it |
| **+ Add parameter** | — | Add a row |

The grid is recombined onto the URL, correctly encoded, when you send. Values are encoded *after*
`{{variables}}` resolve, so a token never ends up percent-escaped into nonsense.

### Headers

The same shape for request headers.

| Control | Default | What it does |
|---|---|---|
| Row checkbox | on | Whether the header is sent |
| **+ Add header** | — | Add a row |

### Body

| Control | Default | What it does |
|---|---|---|
| **Body mode** | raw | Raw text, or **Form data (multipart)** |
| **Content type** | `application/json` | The body's content type, chosen from the list |
| **+ Add field** | — | *(multipart)* Add a form field |
| **File** checkbox | off | *(multipart, per field)* Upload the value as a **file path** rather than as text |

### Auth

| Control | Default | What it does |
|---|---|---|
| **Auth type** | `Auto (captured token)` | `Auto (captured token)`, `None (never send auth)`, `Bearer token`, `Basic`, or `Windows (integrated)`. **`Auto` is not the same as `None`** — it attaches a captured token for this host if one exists, and does nothing otherwise, whereas `None` never sends auth at all |
| **Use my signed-in Windows account (single sign-on)** | on | *(Windows auth)* Authenticate as the account you are logged in as. Untick it to type an explicit `DOMAIN\user` and password instead |
| **Get OAuth 2.0 token…** | — | Opens the [OAuth window](#oauth-window) to fetch a token into this request |

Explicit auth always beats an automatic token — the app never overrides something you typed.

### Tests

Assertions checked against the response. See [Testing & Assertions](11-Testing-and-Assertions.md).

| Control | Default | What it does |
|---|---|---|
| Row checkbox | on | Whether this assertion is evaluated |
| **Target** | `Status` | `Status`, `Time`, `Header`, `Body` (a JSON path), or `Body text` |
| **Operator** | `==` | `==`, `!=`, `contains`, `matches`, `exists`, `absent`, `<`, `>` |
| **+ Add test** | — | Add an assertion |

A request with no assertions still passes on any 2xx, so adding the first one is opt-in strictness
rather than a cliff.

### Capture

Values pulled out of the response into `{{variables}}`. See
[Capturing Values](12-Capturing-Values.md).

| Control | Default | What it does |
|---|---|---|
| Row checkbox | on | Whether this capture runs |
| **Variable** | empty | The variable name to save into |
| **Source** | `Body` | `Body` (a JSON path) or `Header` |
| **+ Add capture** | — | Add a capture rule |

### Transport

How the request reaches the server — the app's equivalent of the CLI's
[transport options](21-CLI-Reference.md#transport-options).

| Control | Default | What it does |
|---|---|---|
| **PROXY** mode | System | `System` (including PAC), `Direct` (ignore the system proxy), or an explicit proxy URL. `http(s)` and `socks4/4a/5` are all accepted — **an `ssh -D` tunnel is a SOCKS5 proxy**, so a jump host works here with mutual TLS intact |
| **USER** / **PASS** | empty | Credentials for a proxy that authenticates |
| **BYPASS** | `NO_PROXY` | Hosts that skip the proxy, comma-separated: `internal.corp`, `.corp`, `10.0.0.0/8`, `*` |
| **REVOCATION** | No revocation checking | Whether to check the server certificate has not been revoked: none, **Offline** (cached lists only), or **Online** (may fetch a list or query the issuer) |
| **Fail when the status can't be determined** | off | Make an *undeterminable* revocation status fatal. Needs Offline or Online. Off by default because a blocked revocation endpoint is the ordinary case on a corporate network |
| **Follow redirects** | on | Follow 3xx responses |
| **MAX HOPS** | `20` | How many redirects to follow before giving up |
| **Decode gzip / deflate / brotli responses** | on | Untick to relay compressed bytes exactly as received — what you want when the *encoding* is the thing under test |
| **VERSION** | Automatic | Pin the HTTP version: Automatic, HTTP/1.1, HTTP/2, or HTTP/3. A pin is **exact** — a server that cannot speak it fails loudly rather than quietly downgrading |
| **RETRIES** | `0` | How many times to retry a failed request |
| **STATUSES** | `429,502,503,504` | Which response statuses earn a retry |
| **DELAY** | `500` ms | The first backoff delay. It doubles each attempt with jitter, capped at 30 seconds; a `Retry-After` header overrides it |
| **Also retry POST and PATCH** | off | Off by default: **re-sending a POST nobody confirmed can charge a card twice** |
| **Retry connection failures and timeouts** | on | Retry a refused connection, a reset, a DNS failure or a timeout — not only HTTP statuses |

---

## The response panel

| Tab | What it shows |
|---|---|
| **Pretty** | The body, formatted — JSON and XML reindented |
| **Raw** | The body exactly as it arrived |
| **Diagnostics** | The negotiated TLS version and cipher, **whether your client certificate was actually presented**, and the server's certificate and chain |
| **Rendered** | HTML responses rendered as a page |
| **Network** | Every call this session made, as a list |
| **Diff** | This response compared against a baseline |

| Control | Default | What it does |
|---|---|---|
| **Copy body** | — | The response body to the clipboard |
| **Copy as ▾** | — | The *request* as a runnable command: cURL, PowerShell, Python (requests), or C# (HttpClient) |
| **Save…** | — | Write the body to a file |
| **Find** / **Find next** | — | Search within the body |
| **Pop out** | — | Open this view, or the whole panel, in its own window — for a second monitor, or to keep a response while you edit the request |
| **Reload** | — | *(Rendered)* Re-render the page |
| **Compare with HAR…** | — | *(Diff)* Choose an archive as the baseline instead of the saved request's known-good response |
| **Clear** | — | *(Diff)* Drop the baseline |

### Network tab filters

| Control | Default | What it does |
|---|---|---|
| **All / 2xx / 3xx / 4xx / 5xx / ERR** | All | Show only calls with that outcome |
| **cert only** | off | Show only calls made **with your client certificate** — the quickest way to confirm the certificate is actually being used where you think |
| **Clear** | — | Empty the list |
| **Export Network trace as HAR…** | — | *(File menu)* Save the session as an archive |

---

## The sidebar

| Control | What it does |
|---|---|
| **HISTORY** | Requests you have sent, newest first. **Clear** empties it |
| **COLLECTIONS** | Saved requests in folders. **Save current request…**, **+ Folder**, **Rename**, **Delete**, **Export as OpenAPI…** |
| **CHAINS** | Ordered sequences of saved requests. **+ New chain…**, **Edit steps…**, **Rename**, **Delete**, **▶ Run chain**, **Copy run command** |
| Sidebar toggle | Collapse the sidebar for more room |

**Set website & certificate…** on a collection folder sets the defaults every request opened from
it inherits, so an endpoint is immediately sendable. The nearest ancestor with a value wins.

---

## The status bar

| Control | Default | What it does |
|---|---|---|
| Status text | — | What the last action did, including a count of unresolved `{{tokens}}` |
| **Token chip** | — | Appears when a session token has been captured. Click it for its menu |
| **Automatically use captured tokens** | on | *(token chip menu)* Attach captured tokens to later requests for the same host. Application-wide, and remembered |
| **Run Self-Test** | — | Prove the mutual-TLS path end to end against a loopback server. **If it fails, the problem is local**, not the API |
| **Mock server…** | — | Opens the [mock server window](#mock-server-window) |
| **Capture session…** | — | Opens [Session Capture](26-Session-Capture.md) |

---

## Windows

### Discover (endpoint fuzzing)

| Control | Default | What it does |
|---|---|---|
| **GET / HEAD / POST / PUT / DELETE** | GET only | Which methods to try against each path. `POST` finds endpoints that exist but reject a GET |
| **Hide 404s / errors** | on | Hide the noise a wordlist run is mostly made of. Untick to see every probe |
| Wordlist | built-in | The paths to probe |
| Concurrency, delay | 8, 0 ms | Parallel probes, and a pause between them — be polite to somebody else's server |

Results open as tabs or save as a collection.

### Environments window

| Control | Default | What it does |
|---|---|---|
| **secret** checkbox | off | Marks a variable's **value** as secret: it is encrypted at rest, hidden in the interface, and **stripped from an exported workspace** unless secrets are explicitly included. The key and the flag survive the export; only the value is withheld |

### Chain editor

| Control | Default | What it does |
|---|---|---|
| **Stop on failure** | on | Stop the chain when this step fails. On by default because calling the API after the login failed produces a confusing 401, not a useful result. Untick it for a step whose failure is not fatal |
| Step order | — | Reorder or remove steps; the order **is** the chain |
| Environment name | none | Where this chain's captures are written, created on first use |

### Chain run window

A PASS / FAIL / SKIP row per step. Selecting a step shows its actual response; **Stop** cancels a
run in progress. Steps that never ran because an earlier one failed are reported as **SKIP** rather
than dropped — an output that just stops leaves you guessing whether the rest passed.

### Mock server window

Start a local test server without leaving the app: plain HTTP, HTTPS with a generated certificate,
or HTTPS that also requires a client certificate. See [Mock Server](18-Mock-Server.md).

### Trusted certificates window

Server-certificate thumbprints pinned per host. **A pinned host is reachable without ticking
"ignore server cert errors"**, which is why this exists: pinning one endpoint is a far smaller hole
than turning verification off for everything. A pin never overrides revocation.

### OAuth window

Fetch an OAuth 2.0 token into the current request: the grant type, the endpoint, client credentials,
and scopes. Saved tokens are encrypted at rest for your Windows user.

### Session capture window

Log in once in a real browser and reuse that session here — cookies and tokens. See
[Session Capture](26-Session-Capture.md).

---

## What is remembered

Between sessions, the app keeps: your open request tabs and which was active, the window's size,
position and maximised state, the theme, the active environment, saved websites, the last
certificate used, collections and chains, history, captured tokens and cookies, and trust pins.

Secrets — captured tokens and cookies, saved auth values, and variables marked **secret** — are
**encrypted at rest for your Windows user**, so another account on the same machine cannot read
them. See [Marking a variable secret](09-Environments-and-Variables.md#marking-a-variable-secret)
and [Import & Export](17-Import-and-Export.md) for what leaves the machine and what does not.

## Where the same setting lives on the command line

Almost everything here has a flag. The mapping is deliberately one-to-one:

| In the app | On the command line |
|---|---|
| Certificate picker | [`--cert`](21-CLI-Reference.md#certificate-options) / `--cert-file` |
| Ignore server cert errors | [`--insecure`](21-CLI-Reference.md#certificate-options) |
| Transport tab | [Transport options](21-CLI-Reference.md#transport-options) |
| Tests tab | [`--assert`](21-CLI-Reference.md#send), or `certapi run` for a saved request |
| Capture tab | [`--capture`](21-CLI-Reference.md#send) |
| ENV selector | [`--env`](21-CLI-Reference.md#variable-options) |
| Run chain | [`run --chain`](21-CLI-Reference.md#run) |
| Trusted certificates | [`trust`](21-CLI-Reference.md#trust) |
| Mock server | [`mock`](21-CLI-Reference.md#mock) |
| Discover | [`fuzz`](21-CLI-Reference.md#fuzz) |
| Copy as ▾ | [`--json`](21-CLI-Reference.md#send) and the export commands |

A [configuration profile](27-Configuration.md) can supply defaults for the command-line side, so a
long invocation becomes a short one.
