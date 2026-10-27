# 7. Building Requests

Method, URL (Uniform Resource Locator), query parameters, headers, and the body — plus multipart
uploads, GraphQL, and the per-request Transport tab.

## Method and URL

Pick a method (GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS) and type the URL. A few conveniences:

- Type a `?query=string` directly in the URL box and it **splits into the Params grid** when you send,
  correctly encoded.
- If you've saved a **base website** (see [Collections & History](10-Collections-and-History.md)), the
  URL box holds just the path after it — fire `/api/orders` without retyping the host.
- `{{variables}}` in the URL are resolved against the active
  [environment](09-Environments-and-Variables.md).

On the CLI (command-line interface) the URL is a positional argument:

```powershell
certapi send "https://api.example.com/search?q=abc" -X GET
```

## Query parameters

The **Params** tab is a key/value grid. Ticked rows are included and recombined onto the URL, encoded,
at send time. On the CLI, just put them in the URL (quote it in PowerShell).

## Headers

The **Headers** tab is a key/value grid; tick a row to include it. On the CLI, repeat `-H`:

```powershell
certapi send https://api.example.com/x -H "Accept: application/json" -H "X-Trace: 1"
```

Some headers (like `Authorization`) are managed for you when you use an [auth type](08-Authentication.md)
— you don't have to set them by hand.

## The body

The **Body** tab offers two modes:

### Text body

Free-form text (JSON (JavaScript Object Notation), XML (Extensible Markup Language), plain text, …).
Choose the **Content-Type** (default `application/json`;
`(none)` sends no body/content-type). On the CLI:

```powershell
certapi send https://api.example.com/users -X POST -d '{"name":"Ada"}'
certapi send https://api.example.com/users -X PUT --data-file .\payload.json
certapi send https://api.example.com/x -X POST -d 'raw text' --content-type text/plain
```

`(Examples use PowerShell quoting; in cmd.exe write JSON as `"{\"name\":\"Ada\"}"`.)`

On the CLI, `-d` (or `--data-file`) implies POST unless `-X` names another method, as in curl, so
the `-X POST` above is optional. `-d @file` reads the body from a file. A file that is text — UTF-8,
or UTF-16/32 with a byte-order mark — is sent as text, with `{{variables}}` resolved; any other file
is sent byte for byte as `application/octet-stream` unless `--content-type` says otherwise. A
`-H "Content-Type: …"` sets the body's type too, as in curl (`--content-type` wins if both are
given):

```powershell
certapi send https://api.example.com/upload -d "@.\logo.png" --content-type image/png
certapi send https://api.example.com/x -d '<a/>' -H "Content-Type: application/xml"
```

### Multipart form (file uploads)

Switch the Body tab to **Form** to build a `multipart/form-data` body: a grid of parts, each a text
field or a file (tick **File** and browse). This implies POST.

On the CLI use `-F` (repeatable; `name=@path` uploads a file, `;type=` sets its content type):

```powershell
certapi send https://api.example.com/upload -F "notes=cover page" -F "file=@.\report.pdf"
certapi send https://api.example.com/upload -F "img=@.\logo.png;type=image/png"
```

Multipart and `-d`/`--data` are mutually exclusive.

## GraphQL

Send a correctly-formed GraphQL request (a JSON `{ query, variables }` POST) with `--graphql`:

```powershell
certapi send https://api.example.com/graphql `
  --graphql "query(`$id:ID!){ user(id:`$id){ name } }" `
  --gql-variables '{"id":1}'
```

`--gql-variables` must be a JSON object; the query is escaped for you.

## Copy the request as code

In the response toolbar (next to **Copy body**), **Copy as ▾** turns the current request into a
ready-to-run snippet — **cURL**,
**PowerShell** (`Invoke-RestMethod`), **Python** (`requests`), or **C#** (`HttpClient`) — with
`{{variables}}` resolved and headers/body included. A token the active environment can't resolve
stays literal (`{{tok}}`) instead of arriving percent-escaped, so the snippet is a fillable template
rather than a silently broken URL. Handy for sharing a repro or moving a call into code.

## Timeout

Set the per-request timeout (seconds) on the request line, or `--timeout <seconds>` on the CLI
(default 100).

## The Transport tab

Per-request connection settings live on the request editor's **Transport** tab, saved with the request
and round-tripped through workspaces: a proxy override or bypass (including a per-host **BYPASS**
list), redirect following with your own hop limit, automatic decompression, HTTP version pinning, and
a **Retries** group. The matching CLI (command-line interface) flags are shared across `send`, `run`,
`fuzz`, `bench`, and `serve` — see the [CLI Reference](21-CLI-Reference.md).

**REVOCATION** is a row on that tab: a three-way mode combo box (**None** / **Offline** / **Online**,
default **None** — nothing changes unless you pick one) plus a **Fail when the status can't be
determined** checkbox, the app's equivalent of `--revocation-strict`. See
[Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation) for what the modes mean and
why an unknown status isn't fatal by default.

Next: [Authentication](08-Authentication.md).
