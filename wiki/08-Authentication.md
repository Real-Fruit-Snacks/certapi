# 8. Authentication

The app supports the full authentication story for enterprise Windows APIs (application programming
interfaces). This chapter covers every
type. (Client-certificate / mTLS — mutual Transport Layer Security — is separate — see
[Certificates & mTLS](06-Certificates-and-mTLS.md)
— and composes with everything here.)

Pick the type on the **Auth** tab. On the CLI (command-line interface), `certapi send` has one-off
flags; saved requests carry
their auth through `certapi run`.

## Auto (captured token) — the default

Call a login endpoint and the app spots the bearer token in the response — `access_token`, `id_token`,
`token`, `accessToken`, or `jwt` in the JSON (JavaScript Object Notation) body (top level or under
`data`/`result`), or an
`X-Auth-Token` / `X-Access-Token` header. It **captures** the token, scopes it to that exact website
(scheme + host + port), and attaches it automatically to later requests there. No copy-paste.

A **chip** in the status bar shows the active website's token and expiry — click it to inspect, clear,
or turn automatic tokens off. Pick **None** on a request to opt out.

Headless, the same thing happens: `certapi send` and `certapi run` print a note when they capture or
use a token; `--no-auto-token` disables it. See [Capturing Values](12-Capturing-Values.md).

## None

Never send auth for this request, even if a token exists for the site.

## Bearer token

Send `Authorization: Bearer <token>`. Paste a token into the field, or reference a captured one with
`{{token}}`. CLI:

```powershell
certapi send https://api.example.com/x --bearer "eyJhbGci..."
```

## Basic

Send `Authorization: Basic <base64(user:pass)>`. CLI:

```powershell
certapi send https://api.example.com/x --basic "alice:secret"
```

## Windows Integrated Auth (Negotiate / NTLM)

For internal sites that authenticate with your **Windows identity** (IIS (Internet Information
Services)-hosted APIs, SharePoint,
intranet services). Pick **Windows (integrated)**:

- **Use my signed-in Windows account** (default) — single sign-on with your logged-in credentials, no
  password typed.
- Untick it to supply an explicit `DOMAIN\user` + password.

The handler negotiates **Kerberos** (if the target has an SPN — service principal name — and you're
domain-joined) or falls back
to **NTLM** (NT LAN Manager) automatically. These credentials answer only a Negotiate or NTLM
challenge, never Basic or Digest. CLI:

```powershell
# single sign-on with your account
certapi send https://intranet.corp/api/me --windows-auth      # aliases: --ntlm, --negotiate

# explicit credentials
certapi send https://intranet.corp/api/me --windows-user "CORP\me" --windows-password "..."
```

Saved requests carry Windows auth through `certapi run`. You can try your setup against the mock
server's [`/windows-auth` route](18-Mock-Server.md).

## OAuth 2.0

Fetch a real OAuth token without leaving the app. On the **Auth** tab, click **Get OAuth 2.0 token…**.
Supported grants:

| Grant | Use |
|---|---|
| **Client credentials** | Machine-to-machine (client id + secret) |
| **Password** | Resource-owner password (username + password) |
| **Refresh token** | Exchange a refresh token for a new access token |
| **Authorization code** | Interactive — opens your browser, catches the redirect on a loopback port, with **PKCE** (Proof Key for Code Exchange) |

The dialog sends client credentials in the body (`client_secret_post`) or as a Basic header
(`client_secret_basic`), supports scopes and extra parameters, and the **token endpoint itself can
require mTLS**. On success the token is stored for the API's host (so **Auto** auth attaches it) and
dropped into the Bearer field.

Headless, use `certapi token`:

```powershell
# client credentials, print the token
certapi token --token-url https://auth.example.com/token --client-id app --client-secret s3cret --scope "api.read"

# fetch and store it, then send — the token attaches automatically
certapi token --token-url https://auth.example.com/token --client-id app --client-secret s3cret `
  --save --for https://api.example.com
certapi send https://api.example.com/orders
```

`--grant password|refresh`, `--client-auth basic`, `--param k=v`, and `--json` are all supported. The
interactive authorization-code grant is app-only (it needs a browser); its headless counterpart is
the **device-code flow** (RFC 8628), which was designed for exactly the machine that cannot open
one:

```powershell
certapi token --grant device --device-url https://auth.example.com/device --token-url https://auth.example.com/token `
  --client-id app --scope "api.read" --save --for https://api.example.com
```

It prints a verification URL and a short code, then polls until you approve the sign-in from a
browser anywhere — your phone included. `Ctrl+C` abandons it; the server's polling interval and
expiry are honored, including the `slow_down` back-off. See the
[CLI Reference](21-CLI-Reference.md#token).

## Capturing a browser login

When a site authenticates through a web page rather than an API call — the session ends up in cookies
or a token minted by JavaScript — use **Capture session…** to log in once in a browser and reuse the
result automatically. See [Session Capture](26-Session-Capture.md).

## How they compose

- **Client certificate (mTLS)** is orthogonal — it authenticates the TLS connection and works
  alongside any of the above.
- An **explicit** `Authorization` (Bearer/Basic, or one you set as a header) suppresses the automatic
  captured-token attach for that request.

Next: [Environments & Variables](09-Environments-and-Variables.md).
