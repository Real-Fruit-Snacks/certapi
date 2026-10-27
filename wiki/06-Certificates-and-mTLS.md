# 6. Certificates & mTLS

The signature feature. This chapter covers picking a certificate, loading one from a file, ignoring
server-cert errors, checking for revocation, and reading the connection diagnostics.

## Picking a certificate from the Windows store

In the app, the **CERTIFICATE** dropdown on the request line lists the client-auth certificates in
your store. Only certificates whose Extended Key Usage allows *Client Authentication* (and that have
a usable private key) are offered. Pick one and it's presented on every send until you change it.

Press **F5** to refresh the list if you've just imported a certificate.

On the CLI (command-line interface), pick by **subject** or **thumbprint**:

```powershell
certapi send https://internal.corp/api --cert "CN=My Client"
certapi send https://internal.corp/api --cert 4A8823F1C0...      # thumbprint
```

By default only your user store (`CurrentUser`) is searched. Add `--store LocalMachine` to also search
the machine store; a certificate present in both is listed once:

```powershell
certapi certs                       # list what's available
certapi certs --store LocalMachine  # include the machine store
certapi send https://internal.corp/api --cert "CN=Svc" --store LocalMachine
```

### Expiry warnings

A client certificate that is within **14 days** of expiring warns every time it is used, on
stderr, without blocking the command:

```
warning: certificate 'CN=My Client' expires in 7 days (not after 2026-08-04).
```

The desktop application badges the same rows in the certificate list — `[EXPIRES IN 7d]` beside
the existing `[EXPIRED]`. The point is lead time: fourteen days is usually enough to get a
renewal through a ticket queue, so the notice arrives while it is still an errand rather than an
outage. An already-expired certificate keeps its own, louder warning, and one that is not valid
*yet* says that instead.

The day count rounds **down**: with 23 hours left it says "today", never "in 1 day".

## Loading a certificate from a file

No store entry? Point at a file instead.

- **`.pfx` / `.p12`** (certificate + private key, often password-protected):

  ```powershell
  certapi send https://internal.corp/api --cert-file client.pfx --cert-password "secret"
  ```

- **PEM (Privacy-Enhanced Mail)** (`.crt` / `.pem`) with the key inline, or with the key in a
  separate file:

  ```powershell
  certapi send https://internal.corp/api --cert-file client.pem                 # key inline
  certapi send https://internal.corp/api --cert-file client.crt --key-file client.key
  ```

In the app, use the **From file…** button next to the certificate dropdown.

> **Why files are re-imported internally:** on Windows, SChannel — the TLS (Transport Layer Security)
> stack — can't use *ephemeral*
> private keys. The app loads file-based keys through a temporary, exportable PKCS#12 (Public-Key
> Cryptography Standards #12) container so the
> handshake works, then discards it. You don't have to do anything — it just means a PEM whose key is
> missing will fail with a clear "no private key" message rather than a cryptic handshake error.

## Trusting the server: `--insecure`

Presenting your certificate and **trusting the server's** certificate are two separate things. Internal
APIs (application programming interfaces) sit behind private CAs (certificate authorities) your
machine may not trust, which fails the handshake with
*"the server's own certificate isn't trusted."* To proceed anyway:

- **App:** tick **Ignore server certificate errors** (clearly labelled insecure).
- **CLI:** add `--insecure`.

Use it for internal/self-signed servers you already trust — not the public internet.

## Checking for revocation

Trusting the server's certificate and checking whether it has been **revoked** are two more separate
questions. `--revocation` defaults to `none` (no check), so nothing changes unless you opt in:

- **`--revocation offline`** consults cached certificate revocation lists (CRLs) only, never reaching
  the network.
- **`--revocation online`** may fetch a fresh CRL or query an Online Certificate Status Protocol (OCSP)
  responder.

A certificate the issuer has actually revoked is refused either way — as its **own** outcome, distinct
from "the server's own certificate isn't trusted" above, because in a corporate public-key
infrastructure (PKI) the two findings mean very different things: a compromised key or a departed
employee, versus usually just a missing root. **Revocation wins over a pin**, too: if you've pinned a
host's thumbprint with `certapi trust add` and its certificate is later revoked, the connection is
refused anyway — a pin is your earlier statement that the certificate was fine, revocation is the
issuer's later word that it isn't, and the later word wins. Under the default `--revocation none` this
can never come up.

A revocation check can come back **unknown** rather than good or bad — a blocked or unreachable
responder is the common case on a locked-down corporate network — and that is **not treated as a
failure by default**, because failing on it would make `--revocation online` unusable on exactly the
networks it targets. Add **`--revocation-strict`** to treat an undeterminable status as fatal instead;
it's a usage error (exit 2) if you pass it without `--revocation offline` or `--revocation online`,
since with checking off there's no unknown status for it to make fatal.

`--insecure` still overrides all of this — it means "trust anything" — but the diagnostics say so
plainly (a `note:` on stderr, `--debug`, `--json`, and the app's Diagnostics panel) rather than leaving
you to assume a clean check happened.

In the app, the request editor's **Transport** tab has a matching **REVOCATION** row: the same
three-way mode choice, plus a **Fail when the status can't be determined** checkbox for
`--revocation-strict`. See [Building Requests](07-Building-Requests.md#the-transport-tab).

## Reading the diagnostics

After a send, the **Diagnostics** tab (app) shows what actually happened in the handshake:

- **TLS protocol** and **cipher suite** negotiated.
- **Client certificate** — whether yours was *presented to the server* (the real test that mTLS —
  mutual TLS — worked), or whether the server didn't ask for one.
- **Server certificate** — subject, issuer, thumbprint, expiry, and the chain.
- **Revocation** — which mode was requested and what came back: checked-and-good, revoked, unknown, or
  not checked. Always present, whether or not checking ran.

Connections are pooled and reused, so these diagnostics describe the handshake that established the
connection your request used, not necessarily a handshake that just happened — a second send to the
same host over a reused connection shows the same protocol, cipher, and client-certificate-presented
values as the first, because there was no new handshake to observe. A connection is only ever reused
by a request presenting the same client certificate and the same trust policy, so what you're shown is
still true of the request in front of you.

On the CLI, add `--debug` to print the same TLS details (and much more) to stderr.

## Proving the whole path works

Not sure your setup is right? Run the **self-test**, which needs no real endpoint:

```powershell
certapi selftest
```

It generates a CA, a server certificate, and a client certificate in memory, stands up a loopback
mTLS server, and makes one authenticated round-trip — proving certificate loading, presentation, and
validation all work on this machine. In the app, click **Run Self-Test**.

Want a **standing** server to test against (not just a one-shot)? See the
[Mock Server](18-Mock-Server.md), which can require mTLS and even hands you a ready-to-use client
`.pfx`.

Next: [Building Requests](07-Building-Requests.md).
