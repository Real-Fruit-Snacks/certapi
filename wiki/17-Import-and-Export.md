# 17. Import & Export

Move requests in and out — from a cURL command, an OpenAPI/Swagger document, a HAR capture, and
whole workspaces.

## Import a cURL command

Paste a `curl` command and get a request. Great for turning a "copy as cURL" from browser dev-tools or
a colleague's snippet into an editable, saved request.

```powershell
certapi import curl "curl -X POST https://api.example.com/login -H 'Content-Type: application/json' -d '{}'"
certapi import curl "curl ..." --into "auth"          # into a folder
certapi import curl "curl ..." --workspace suite.json # into a workspace file
```

The parser understands the common flags — method, headers, data, and more.

## Import OpenAPI / Swagger

Turn an OpenAPI (or Swagger) document into a collection of ready-to-send requests:

```powershell
certapi import openapi .\petstore.json
certapi import openapi .\petstore.json --into "petstore"
certapi import openapi .\petstore.json --workspace .\suite.json
```

Paths, methods, base servers, and operation names come across so you can start sending immediately.
Each operation also gets the checks its documented answer implies: a **status assertion** for its
first 2xx response and, when that response has a JSON schema, a **schema assertion**
(`body $ schema …`) carrying the schema with only the `components.schemas` (or Swagger 2
`definitions`) it reaches. A `certapi run` of the imported collection is a contract test from the
first run; edit or delete either assertion like any other.

**Calling an operation without importing anything.** `certapi call` reads the document directly and
calls one operation by its `operationId`, with each `--param` placed where the document says —
path, query, header or cookie — then checks the response's status and body against it:

```powershell
certapi call .\petstore.json getPetById --param petId=7 --cert "CN=svc"
certapi call .\petstore.json addPet -d '@pet.json' --server https://staging.example.com/v1
certapi call .\petstore.json --list
```

A required parameter that is missing stops the call before anything is sent. Every `send` option
works too. See the [CLI Reference](21-CLI-Reference.md#call).

## Import a HAR file

Turn a captured session — a browser's DevTools export, or a `--har` capture from `certapi` — into
saved requests:

```powershell
certapi import har .\session.har
certapi import har .\session.har --into "captured" --workspace .\suite.json
```

In the app: **Import ▾ › Import HAR file…** on the request-tab strip.

Each entry in the archive becomes one saved request, in order, in a collection named after the
file; each is named after its method and path. Headers come across except HTTP/2 pseudo-headers
(`:authority`, …) and `Host`. A text request body comes across with its content type; a binary body
has no text form for a saved request and is left empty. A value the capture redacted stays
redacted, so put the real credential back before sending. To replay the archive as it was recorded
instead, use [`certapi run <file.har>`](21-CLI-Reference.md#run).

## Import a Postman collection

The format most teams already have in hand. Point at a Collection v2.0/v2.1 export:

```powershell
certapi import postman .\orders.postman_collection.json
certapi import postman .\orders.postman_collection.json --into "orders" --workspace .\suite.json
```

Folders, methods, both of Postman's URL forms, query rows, and headers come across, with disabled
rows staying disabled. Bodies map by mode — raw (with the language deciding the content type),
urlencoded, and formdata — and auth maps for bearer, basic, digest, awsv4 and apikey, with request-level auth
beating folder- and collection-level, exactly as Postman resolves it. `{{variables}}` share their
syntax between the two products, so request text imports unchanged, and collection-level variables
become an environment named after the collection — a variable Postman marked `secret` is stored
encrypted here, the same as any other secret at rest. Two deliberate cautions: a **file** form
part imports disabled (its path came from someone else's machine — point it somewhere real before
sending), and anything that cannot carry across faithfully is a **named warning** on stderr, never
a silent drop.

## Import an Insomnia export

```powershell
certapi import insomnia .\Insomnia_2026-07-28.json
certapi import insomnia .\export.json --into "orders" --workspace .\suite.json
```

Use Insomnia's **Export Data → Insomnia v4 (JSON)**. Folders, methods, URLs, query and header rows
come across with disabled rows staying disabled; text and form bodies map by their media type; and
bearer, basic, digest and IAM (AWS Signature v4) authentication map directly (a block Insomnia has switched off is ignored rather
than applied). Insomnia's environments become environments here.

Two things about templates are worth knowing, because the two products spell variables differently:

- **`{{ _.name }}` is translated to `{{name}}`** — Insomnia's variable syntax becomes this
  product's, everywhere it appears: URL, query, headers, body, and auth.
- **A tag template — `{% uuid 'v4' %}`, `{% response … %}` — is a small program, not a value**, and
  has no equivalent here. It is left in the text exactly as written and named in a warning at
  import time, which is more useful than dropping it silently and discovering the gap at send time.

## Import a Bruno collection

```powershell
certapi import bruno .\people-api                 # the collection folder Bruno keeps on disk
certapi import bruno .\people-api\users\get-user.bru
certapi import bruno .\people-api.json --into "people" --workspace .\suite.json   # Export Collection JSON
```

Bruno keeps a collection as a folder of `.bru` files, so point at the folder (the one with
`bruno.json`). Folders come across with their names from `folder.bru`, requests in their `seq`
order, and query rows, path parameters, headers (disabled rows stay disabled) and bodies — JSON,
text, XML, GraphQL (rebuilt as the `{"query", "variables"}` it sends), form and multipart. Auth maps
for bearer, basic, digest, NTLM (as Windows auth), AWS Signature v4 and API key, and **`inherit`**
is resolved the way Bruno resolves it: the nearest `folder.bru`, then `collection.bru`.
`{{variables}}` share their syntax and import unchanged; `environments/*.bru` become environments.

Two of Bruno's blocks have a counterpart here and come across as one: an **`assert`** line such as
`res.status: eq 200` or `res.body.id: isDefined` becomes an assertion, and a
**`vars:post-response`** entry that reads a body path or header (`token: res.body.access_token`)
becomes a capture. Scripts and tests are JavaScript and are named in a warning, as is an assert
operator with no equivalent (`length`, `isJson`, …). Bruno keeps **secret** environment values out of
its files, so they arrive empty — the warning says how many to fill in.

## Import a WSDL (SOAP)

```powershell
certapi import wsdl .\OrdersService.wsdl
certapi import wsdl .\OrdersService.wsdl --into "soap" --workspace .\suite.json
```

Reads a WSDL 1.1 document (and the SOAP 1.2 binding variant) and turns each operation into a saved
**POST** at the port's address, with:

- the right content type — `text/xml` plus a `SOAPAction` header for SOAP 1.1, or
  `application/soap+xml;…;action="…"` for SOAP 1.2, where the action travels in the content type
  instead;
- an **envelope skeleton** in the correct envelope namespace. For a document/literal operation
  (its message parts name elements), the body holds each part's element itself, in that element's
  own schema namespace. For an RPC operation (its parts name types), the body is an operation
  element in the service's target namespace, wrapping one placeholder per part.

**Deliberately minimal, and worth understanding before you use it.** Types are *not* expanded from
the schema: each part becomes a commented placeholder naming its element or type
(`<!-- body: element GetOrder — fill in from the schema -->`). Generating a full instance document
from XML Schema — with imports, restrictions, choices, and substitution groups — is a different
product, and a fabricated body would look authoritative while being wrong. This gets a working SOAP
request about ninety percent written; you fill in the payload.

An imported document or schema (`wsdl:import`, `xsd:import`, `xsd:include`) is **named in a
warning, never fetched** — this reads only the file you name and touches no network. If operations
seem missing, import the file the warning names as well.

## Import a `.http` file (VS Code / JetBrains)

```powershell
certapi import http .\api.http
certapi import http .\api.http --into "orders" --workspace .\suite.json
```

In the app: **Import ▾ › Import .http file…** (Postman, Insomnia v4, and WSDL imports are also
available directly under **Import ▾**).

Reads standard `###` request blocks, `@variable = value` declarations (converting `{{var}}`
references into an environment), methods, URLs, headers, and bodies.

## Export as code snippets

Generate a ready-to-run client snippet from any saved request on the command line:

```powershell
certapi export code curl "Orders/Create order" --env Staging
certapi export code powershell "Orders/Create order" -o call.ps1
certapi export code python "Orders/Create order"
certapi export code csharp "Orders/Create order"
```

## Export as OpenAPI

Export your collections (or a folder) as an OpenAPI document — useful for sharing the shape of an
API (application programming interface) or seeding another tool:

```powershell
certapi export openapi -o api.json                 # everything
certapi export openapi "petstore" -o petstore.json # one folder
```

Auth is exported as a **security scheme** description only — **never the secrets**.

## Export a whole workspace

Bundle your requests, collections, environments, and history into a portable file:

```powershell
certapi export workspace -o team-setup.json
```

Hand it to a teammate (or check it into source control) and they import it, or point their tools at it
with `--workspace team-setup.json`.

Secrets — captured tokens and cookies, a saved request's auth secret, **a saved proxy password**,
any variable marked **secret**, history response bodies, credential response headers in history,
and known-good snapshots — are **stripped by default**, since an exported workspace is a file people
end up emailing to each other. The rest of the export is unaffected: a secret variable keeps its key
and its flag, just not its value, and a history entry keeps its method, URL, status and header
names. Add `--include-secrets` to keep them instead:

```powershell
certapi export workspace -o team-setup.json --include-secrets
```

Kept secrets are written encrypted for the current Windows user (the same protection the live
workspace uses), so even `--include-secrets` never puts a credential on disk in the clear — a
recipient on another machine or signed in as someone else still can't read them. `certapi` reports on
stderr what was stripped (or kept).

## Export to markdown notes (Obsidian, Logseq, a docs repo)

Turn the workspace into a folder of linked markdown notes — one per saved request, plus
environments and chains:

```powershell
certapi export markdown -o C:\Users\me\Vault --index
```

**An Obsidian vault is just a folder of markdown files.** There is no plugin to install and no
service to authenticate against, so `-o` can point straight at a vault — and the same output works
just as well in Logseq, Foam, a git-backed documentation repository, or a plain wiki.

Each request note carries YAML (YAML Ain't Markup Language) frontmatter (`method`, `host`, `url`,
`auth`, `lastStatus`, `lastChecked`) that Obsidian's properties view and Dataview both read, then
the request itself: headers, body, assertions and captures. Notes link to each other with
`[[wikilinks]]` — a request links to its collection and to any chain that uses it, and a chain
links to each of its steps — so the export is a browsable graph rather than a pile of files.

```
Vault/
  certapi/                    <- --into, default "certapi"
    index.md                  <- --index
    Orders/
      Get orders.md
    environments/
      Staging.md
    chains/
      Login then fetch.md
```

**Secrets are redacted by default, and the default matters more here than anywhere else.** Vaults
sync — Obsidian Sync, iCloud, OneDrive, git — so a note written into one is more likely to leave
your machine than any other file this tool writes. Credential header values, saved auth secrets and
variables marked secret are all replaced with *(redacted)*; the header *name* stays, because "this
request sends a bearer token" is exactly what a catalogue should record. `--include-secrets`
overrides it, and says so on stderr.

**Re-exporting overwrites the same notes in place** — filenames are derived from names, so you get
`Get orders.md` again rather than `Get orders 2.md`. That also means a generated note you edited by
hand is overwritten, which is why the tree lives in its own subfolder; change it with
`--into <name>` if `certapi` clashes with something in your vault.

## Round-tripping with the app

Imports land in the live workspace (or a `--workspace` file) that the app reads, so anything you
import on the CLI (command-line interface) shows up in the app, and vice versa. See
[Collections & History](10-Collections-and-History.md).

Next: [Mock Server](18-Mock-Server.md).
