# 14. Endpoint Discovery (Fuzzing)

When an API (application programming interface) has no documentation, discovery probes a list of
candidate paths against a base URL (Uniform Resource Locator) and
reports which ones exist — so you can map an undocumented service. It presents your client certificate
like any other request, so it works against APIs protected by mTLS (mutual Transport Layer Security).

> Use this only against systems you're authorized to test.

## In the app

Open the discovery window with the **Discover…** button at the right of the request-tab strip, pick
a base website and certificate, choose (or accept the built-in) wordlist, and run. Results are a filterable grid of paths and outcomes; promising
ones can be opened as requests or saved into a collection.

## On the command line

```powershell
# Probe with the built-in starter list
certapi fuzz https://api.example.com --cert "CN=My Client"

# Use your own wordlist and try multiple methods
certapi fuzz https://api.example.com -w .\paths.txt -X GET,POST --cert "CN=My Client"
```

- **`-w, --wordlist <file|->`** — endpoints to probe, one per line (`-` reads stdin). **Omit it** to
  use the built-in starter list — a curated ~270 paths organized by category (operational, API roots &
  docs, authentication, common resources, debug, and commonly-exposed sensitive paths). Handy for a
  fast first pass, but bring your own larger list for exhaustive mapping. A line is `PATH` or
  `METHOD PATH`, and a full `https://` URL on a line overrides the base URL. Lines starting with `#`
  are comments. With `-w -`, a second Ctrl+C quits at once.
- **`-X, --methods <list>`** — comma-separated methods per path (default `GET`).

## Reading the results

By default the output hides the noise (404s and connection errors) and shows the interesting outcomes,
grouped by class:

| Outcome | Meaning |
|---|---|
| **Found** | 2xx — the path exists and responded |
| **Unauthorized** | 401 — exists but needs auth |
| **Method not allowed** | 405 — path exists, wrong verb (try another `-X`) |
| **Redirect** | 3xx |
| **Server error** | 5xx |
| **Error** | a transport/connection failure |

Control the view:

- `--all` — show everything, including 404s and errors.
- `--match <codes>` — show only these status codes.
- `--hide <codes>` — hide only these.

## Speed and politeness

- `--concurrency <n>` — parallel probes, 1–50 (default 8).
- `--delay <ms>` — pause between probes; be polite to the target.
- `--timeout <seconds>` — per-probe timeout.

## Auth, variables, and headers

Discovery is a real request under the hood, so it accepts the usual options: `--cert` / `--cert-file`,
`--insecure`, `-H` headers, `--bearer`, and `{{variables}}` via `--env` / `--var` in the base URL and
paths. `--no-auto-token` opts out of token capture/attach.

## Saving what you find

- `--save-collection <name>` — save discovered endpoints as requests in a collection you can open in
  the app.
- `-o <file>` — write discovered paths as a wordlist (or the full JSON (JavaScript Object Notation)
  report with `--json`).
- `--json` — emit `{ results, summary }` instead of the table.

```powershell
certapi fuzz https://api.example.com -w .\paths.txt --save-collection "discovered" --json -o report.json
```

Next: [Live Streaming](15-Live-Streaming.md).
