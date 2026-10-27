# certapi — Handbook

A Windows-native API (application programming interface) client built around **mutual TLS
(Transport Layer Security, or mTLS) client-certificate authentication**, with a polished WPF
(Windows Presentation Foundation) app and a matching `certapi` command-line tool. This handbook is
the complete guide to both.

> New here? Read [Introduction](01-Introduction.md) → [Installation](02-Installation.md) →
> [Quick Start](03-Quick-Start.md), then dip into whatever you need.

## Contents

**→ [Full table of contents](00-Table-of-Contents.md)** — every page, what is on it, and a way in
by what you are trying to do.

The short version:

| | |
|---|---|
| **Getting started** | [Introduction](01-Introduction.md) · [Installation](02-Installation.md) · [Quick Start](03-Quick-Start.md) · [Core Concepts](04-Concepts.md) · [The Interface](05-The-Interface.md) |
| **Sending requests** | [Certificates & mTLS](06-Certificates-and-mTLS.md) · [Building Requests](07-Building-Requests.md) · [Authentication](08-Authentication.md) · [Environments & Variables](09-Environments-and-Variables.md) · [Collections & History](10-Collections-and-History.md) |
| **Beyond a single call** | [Testing & Assertions](11-Testing-and-Assertions.md) · [Capturing Values](12-Capturing-Values.md) · [Data-Driven Runs](13-Data-Driven-Runs.md) · [Endpoint Discovery](14-Endpoint-Discovery.md) · [Live Streaming](15-Live-Streaming.md) · [Response Views](16-Response-Views.md) · [Session Capture](26-Session-Capture.md) · [Configuration](27-Configuration.md) |
| **Tooling** | [Import & Export](17-Import-and-Export.md) · [Mock Server](18-Mock-Server.md) · [Local Gateway](19-Local-Gateway.md) · [MCP Server](20-MCP-Server.md) · [CLI Reference](21-CLI-Reference.md) |
| **Reference** | [Settings & Toggles](28-Settings-and-Toggles.md) · [Keyboard Shortcuts](22-Keyboard-Shortcuts.md) · [Troubleshooting](23-Troubleshooting.md) · [FAQ](24-FAQ.md) · [Building from Source](25-Building-from-Source.md) |

## At a glance

| | |
|---|---|
| **Platform** | Windows 10 / 11 (x64) |
| **Runtime** | .NET 9 (bundled in the self-contained builds — nothing to install) |
| **Two front ends** | `ApiTester.App.exe` (GUI — graphical user interface) and `certapi.exe` (CLI) — same engine |
| **Signature feature** | Client-certificate (mTLS) auth from the Windows store or a file |
| **License** | See `LICENSE` in the repository |

Everything documented here is available in both the app and the CLI unless noted. The CLI's built-in
help (`certapi <command> --help`) is always the authoritative source for options.
