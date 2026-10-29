# 16. Response Views

Every way the app shows you a response, plus copying, saving, and themes.

## The response tabs

| Tab | Shows |
|---|---|
| **Pretty** | JSON (JavaScript Object Notation) and XML (Extensible Markup Language) formatted with syntax highlighting; HTML (Hypertext Markup Language)/text shown as-is; binary hex-dumped. The body type is sniffed even when the `Content-Type` is missing or lying. |
| **Raw** | The exact response bytes decoded as text, with a **find** box (Enter for next match, wraps around). |
| **Headers** | Every response header. |
| **Diagnostics** | Connection details — TLS (Transport Layer Security) version and cipher, whether your client certificate was presented, and the server's certificate and chain. See [Certificates & mTLS](06-Certificates-and-mTLS.md). |
| **Diff** | What changed since the last known-good response — or since a recorded session (below). |
| **Rendered** | The request's URL (Uniform Resource Locator) opened as a **web page** (below). |
| **Network** | A browser-style trace of every HTTP (Hypertext Transfer Protocol) call (below). |

**A large body is shown in part.** Raw shows the first 1 MB and Pretty the first 512 KB, ending
with a line that says how much is left out. A body over 4 MB is shown as received, without
formatting, and a binary body's hex dump covers its first 64 KB. JSON and XML are coloured up to
64,000 characters and shown in one colour beyond that. **Copy body** and **Save…** always take the
whole body. The app lays out every character it shows on the window's own thread, so showing all
of a large body left the window not responding until it finished.

## Find in a response

Above the response, the **find** box locates and selects the next match in the body — Enter (or **Find
next**) jumps to the next occurrence and wraps around. Handy for a value buried in a large payload.
Find searches what the Raw view shows; when the term only appears further into the body, it says
so, and **Save…** gives you the whole body to search.

## Diff

An assertion checks what you thought to check. The **Diff** tab answers the other question: did
anything change at all? It compares the response you just got against a baseline and lists the
differences — the status, each header, and each value in the body.

The baseline is the saved request's **known-good** response: the last 2xx it returned, recorded
automatically (and capped at 1 MiB, so the settings file doesn't become a blob store). **Compare with
HAR…** points it at a response recorded in an HTTP Archive (HAR) `.har` file instead, and **Clear**
goes back to the known-good one.

How the body is compared depends on what the body is:

| Both sides | Comparison |
|---|---|
| JSON | Structural — each changed path is named (`data.items[0].id`) as added, removed, changed, or type-changed, with arrays compared by index. |
| Text, not JSON | A one-line summary: the lines and bytes on each side. |
| Binary (either side) | Size and equality only — it doesn't pretend to diff a PDF. |

Headers that change on every response are ignored by default, or a real difference would drown in
them: `Date`, `Set-Cookie`, `ETag`, `Age`, `X-Request-Id`, `X-Correlation-Id`, and `Server-Timing`.

On the command line, `certapi send <url> --diff <baseline>` does the same against a `.har` file, a
`.json` response file, or the word `known-good`, with `--diff-fail` to fail a build on any
difference; and `certapi run --diff-har session.har` replays a captured session and passes an entry
only when its response is identical to the recorded one. See the
[CLI Reference](21-CLI-Reference.md#send).

## Rendered website

The **Rendered** tab loads the URL as a real web page instead of raw text — useful when the target is
a site, not an API (application programming interface). Every resource the page fetches (document,
CSS (Cascading Style Sheets), JS (JavaScript), images, XHR (XMLHttpRequest)) is loaded with
**your client certificate**, so a certificate-protected site renders fully. It loads on demand (nothing
runs until you open the tab); **Reload** fetches again. It uses the Windows WebView2 runtime; if that's
unavailable the tab says so and the rest of the app is unaffected.

## Network trace

The **Network** tab is like a browser's network panel: every HTTP call is logged — the request you
sent and every resource the Rendered view fetched — with method, status, type, size, timing, and a
**client-certificate marker**. You can:

- filter by text, status class (2xx–5xx / errors), or **cert-only**,
- click a row for a resizable details pane with headers,
- right-click a row to copy its URL or a matching **curl** command.

## Pop-out views

The **pop-out** button (next to Copy body) detaches either the selected view or the whole response
panel into its own window — watch the Network trace beside the Pretty body, or give the request editor
the full main window. Everything stays live; closing a popped-out window returns its content.

## Copying and saving

- **Copy body** — copy the response body to the clipboard.
- **Copy as ▾** (in the response toolbar, next to **Copy body**) — the *request* as cURL /
  PowerShell / Python / C#.
- **Save** (`Ctrl+S`) — save any response to a file, including binary, with a sensible extension for
  the content type. On the CLI (command-line interface), `certapi send -o file` writes the body to a
  file.

## Themes

The app ships two palettes, **Nightshift** (dark) and **Dayshift** (light). Toggle with the **☀ / ☾** button in the
title bar; your choice is saved and applied to every window (and the native title bar) — the syntax
highlighting and status colors adapt too. On the CLI there's no theme; output is plain text.

Next: [Import & Export](17-Import-and-Export.md).
