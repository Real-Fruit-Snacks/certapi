# 1. Introduction

**certapi** is a desktop API (application programming interface) client for Windows
whose reason to exist is one thing the mainstream tools handle poorly: **mutual TLS (Transport Layer
Security, or mTLS)**. It authenticates HTTP (Hypertext Transfer Protocol) requests with a client
certificate — either straight from the Windows certificate store or from a `.pfx`/`.pem` file — and
does it everywhere, in the GUI (graphical user interface) and the command line, without you writing a
line of code.

If you've ever needed to call an internal, certificate-protected API and found yourself fighting with
`curl` flags, PowerShell `Invoke-WebRequest` incantations, or a general-purpose client that treats
client certs as an afterthought — this is built for you.

## What it is

Two front ends over one engine:

- **`ApiTester.App.exe`** — a WPF (Windows Presentation Foundation) desktop app: a full request
  builder, collections, environments, response views, assertions, discovery, streaming, and more.
- **`certapi.exe`** — a command-line client with the same capabilities, built for scripts, CI
  (continuous integration), and automation. Script-friendly exit codes; body to stdout, diagnostics to stderr.

They share the same request model and the same on-disk workspace, so you can build a request in the
app and run it headless — or vice versa.

## Who it's for

- **Engineers testing internal / enterprise APIs** protected by client certificates, private CAs
  (certificate authorities), or Windows Integrated Auth.
- **Security and platform teams** who need to prove an mTLS path works end to end, discover
  undocumented endpoints, or reach a cert-protected service from a tool that can't do mTLS.
- **CI pipelines** that need a saved suite of requests to pass/fail against real endpoints.
- **AI (artificial intelligence) agents** — via the built-in
  [MCP (Model Context Protocol) server](20-MCP-Server.md), an agent can make mTLS calls with a
  certificate you pin, bounded by an allowlist.

## Why not just use `curl` / Postman / Insomnia?

You can, and for public APIs they're fine. Where this app pulls ahead:

- **Windows certificate store, first-class** — pick a cert by subject or thumbprint; no exporting
  keys to disk.
- **mTLS everywhere** — every feature (send, run, fuzz, stream, OAuth, gateway, mock) can present a
  client certificate.
- **Windows-native auth** — honors your machine proxy (WPAD/PAC — Web Proxy Auto-Discovery /
  proxy auto-configuration) with your Windows credentials, narrowed per host with a `--noproxy`
  bypass list when only part of your traffic should skip it, and supports
  [Windows Integrated Auth](08-Authentication.md) (Negotiate/NTLM — NT LAN Manager).
- **Connection diagnostics** — see the negotiated TLS version, cipher, whether your cert was actually
  presented, and the server's chain.
- **Self-contained** — a single `.exe` with the .NET runtime bundled. Nothing to install, portable on
  a locked-down machine.

## What's inside (the short list)

Request builder · collections & history · environments & `{{variables}}` · response assertions ·
value capture & automatic bearer tokens · **OAuth 2.0** (client-credentials, password, refresh,
authorization-code + PKCE — Proof Key for Code Exchange — and device code on the CLI) · **Windows Integrated Auth** · multipart
uploads · GraphQL · endpoint discovery (fuzzing) · **WebSocket & SSE (Server-Sent Events)**
streaming · a local **mock server** · a local **mTLS
gateway** · an **MCP server** · cURL / OpenAPI import & export · rendered-website view · network
trace · light & dark themes.

Each has its own chapter. Next: [Installation](02-Installation.md).
