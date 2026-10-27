# Security

## Supported versions

The latest release only. Fixes ship as a new SemVer tag; there are no backports.

## Reporting

Use GitHub's private vulnerability reporting on this repository (Security → Report a vulnerability). Don't open a public issue for anything that could expose a credential, a private key or a captured session.

Include the `certapi --version` output, the command or window involved, and whether a proxy, a PAC script or TLS interception was in the path. A redacted `--debug` log or `--har` file helps; both redact credentials by default.

I read reports within a week. If it's real, the fix and the changelog entry name the problem plainly, as the `### Security` entries in [CHANGELOG.md](CHANGELOG.md) already do.

## What this tool handles

- Client certificates are used in place; the private key is never read or exported. Windows CNG signs the handshake.
- Secrets at rest (captured tokens, cookies, saved passwords, saved proxy passwords, secret variables, stored response bodies, credential response headers) are DPAPI-encrypted for the Windows user in `%APPDATA%\CertApiTester\state.json`.
- Exports (`export workspace`, `--har`, `export markdown`, `serve --record`) strip or redact credentials by default, including ones in a URL. `--include-secrets` variants keep them.
- The MCP server never follows redirects, checks a host allowlist on every call and never writes the workspace back.
- *Ignore server certificate errors* is off by default and labelled insecure. Prefer `certapi trust add <host>`.
- No telemetry. The network calls are the requests you send and, when you ask (`certapi update`, *Check for updates*, or the opt-in daily startup check), one call to GitHub's releases API. `doctor` also probes `www.msftconnecttest.com` after a DNS or TCP failure, unless `--offline`.
