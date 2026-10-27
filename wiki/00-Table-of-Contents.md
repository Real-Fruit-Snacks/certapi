# 0. Table of Contents

Every page in this handbook, what each one is for, and — below — a way in by **task** when you
know what you want to do but not what it is called.

> New here? [Introduction](01-Introduction.md) → [Installation](02-Installation.md) →
> [Quick Start](03-Quick-Start.md), then come back and pick what you need.

---

## Getting started

| # | Page | What's on it |
|---|---|---|
| 1 | [Introduction](01-Introduction.md) | What this is, who it's for, and why it exists |
| 2 | [Installation](02-Installation.md) | Download, requirements, first run — nothing to install |
| 3 | [Quick Start](03-Quick-Start.md) | Your first authenticated request, in about two minutes |
| 4 | [Core Concepts](04-Concepts.md) | Certificates, mutual TLS, and the workspace model |
| 5 | [The Interface](05-The-Interface.md) | A guided tour of the desktop application |

## Sending requests

| # | Page | What's on it |
|---|---|---|
| 6 | [Certificates & mTLS](06-Certificates-and-mTLS.md) | Picking a client certificate from the Windows store or a file |
| 7 | [Building Requests](07-Building-Requests.md) | Method, URL, query parameters, headers, bodies, file uploads |
| 8 | [Authentication](08-Authentication.md) | Auto, Bearer, Basic, OAuth 2.0, Windows Integrated |
| 9 | [Environments & Variables](09-Environments-and-Variables.md) | `{{variable}}` values per environment, and `{{env:NAME}}` from the process environment |
| 10 | [Collections & History](10-Collections-and-History.md) | Saving requests into folders, and what was sent before |

## Beyond a single call

| # | Page | What's on it |
|---|---|---|
| 11 | [Testing & Assertions](11-Testing-and-Assertions.md) | Turning a collection into a pass/fail suite, and keeping the result |
| 12 | [Capturing Values](12-Capturing-Values.md) | Grabbing a token from one response for the next request — and chains |
| 13 | [Data-Driven Runs](13-Data-Driven-Runs.md) | Repeating a request once per row of a CSV or JSON dataset |
| 14 | [Endpoint Discovery](14-Endpoint-Discovery.md) | Probing a wordlist to map an undocumented API |
| 15 | [Live Streaming](15-Live-Streaming.md) | WebSocket and Server-Sent Events |
| 16 | [Response Views](16-Response-Views.md) | Pretty, Raw, Headers, Diagnostics, Diff, Rendered, Network |
| 26 | [Session Capture](26-Session-Capture.md) | Log in once in a browser, reuse that session here |
| 27 | [Configuration](27-Configuration.md) | Profiles that carry the tedious half of every command line — and whole gateways |

## Tooling

| # | Page | What's on it |
|---|---|---|
| 17 | [Import & Export](17-Import-and-Export.md) | cURL, OpenAPI, Postman, Insomnia, WSDL, HAR — and markdown notes for a vault |
| 18 | [Mock Server](18-Mock-Server.md) | A local endpoint to fire requests at, including one that misbehaves on purpose |
| 19 | [Local Gateway](19-Local-Gateway.md) | `serve` — an mTLS front door, for one site or many, for apps that can't do client certificates |
| 20 | [MCP Server](20-MCP-Server.md) | Letting AI agents make mutual-TLS calls through this tool |
| 21 | [CLI Reference](21-CLI-Reference.md) | Every `certapi` command and option |

## Reference

| # | Page | What's on it |
|---|---|---|
| 28 | [Settings & Toggles](28-Settings-and-Toggles.md) | Every control in the app: what it does, its default, whether it is remembered |
| 22 | [Keyboard Shortcuts](22-Keyboard-Shortcuts.md) | Every shortcut in the app |
| 23 | [Troubleshooting](23-Troubleshooting.md) | Why can't I reach this? — and the tools that answer it |
| 24 | [FAQ](24-FAQ.md) | Short answers to the questions that come up most |
| 25 | [Building from Source](25-Building-from-Source.md) | Building, running the tests, and cutting a release |

---

## Find it by what you're trying to do

**"It won't connect and I don't know why."**
[`certapi doctor`](23-Troubleshooting.md#start-here-certapi-doctor) makes the connection one stage
at a time and names the stage that broke — including the certificate authorities the server accepts
client certificates from, and whether the network is decrypting TLS in the middle.

**"It works in my browser but not here."**
Almost always the proxy: [`certapi proxy <url>`](23-Troubleshooting.md#it-works-in-my-browser-but-not-here)
shows which one this machine picks for that address, including one a PAC script chose.

**"I need to see what actually went over the wire."**
[`--trace`, `--wire` and `--frames`](23-Troubleshooting.md#watching-what-the-network-stack-actually-did---trace)
— the stack's own events, the plaintext bytes, and HTTP/2 framing. No driver, no administrator
rights.

**"Is it slow, and where?"**
[Reading the timings](23-Troubleshooting.md#its-slow--reading-the-timings) explains which of the
three measurements answers which question — and
[`certapi connections`](23-Troubleshooting.md#am-i-actually-reusing-connections--certapi-connections)
answers whether connection reuse is working at all.

**"What does this checkbox actually do?"**
[Settings & Toggles](28-Settings-and-Toggles.md) lists every control in the app with its default,
and [CLI Reference](21-CLI-Reference.md) does the same for every command-line option.

**"I have a `.pfx` / smart card / no certificate at all."**
[Certificates & mTLS](06-Certificates-and-mTLS.md) covers the store, files, and going without.

**"I want this to run in CI."**
[CLI Reference](21-CLI-Reference.md) for the commands and exit codes,
[Testing & Assertions](11-Testing-and-Assertions.md) for making a suite fail a build, and
[Configuration](27-Configuration.md) for keeping the command line short.

**"I already have this API described somewhere else."**
[Import & Export](17-Import-and-Export.md) reads cURL, OpenAPI, Postman, Insomnia, WSDL and HAR.

**"I want a record I can keep or share."**
[Import & Export](17-Import-and-Export.md#export-to-markdown-notes-obsidian-logseq-a-docs-repo)
writes the workspace as linked markdown notes;
[Troubleshooting](23-Troubleshooting.md#keeping-a-diagnosis-doctor---md) keeps a diagnosis, and
[Testing & Assertions](11-Testing-and-Assertions.md#keeping-a-run---md) keeps a run.

**"I need something to test against."**
[Mock Server](18-Mock-Server.md) — including expired certificates, wrong hostnames, slow responses
and failures on demand.

---

The CLI's own help (`certapi <command> --help`) is always the authoritative source for options; these
pages explain what they are for.
