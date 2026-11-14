# 11. Testing & Assertions

Turn a request into a pass/fail check. Assertions run against the response; a request with any failing
assertion **fails**, which drives exit codes and known-good markers.

## Adding assertions (app)

On the **Tests** tab, add one or more assertion rows. Each has:

- **Target** — what to check:
  - **Status** — the HTTP (Hypertext Transfer Protocol) status code
  - **Time** — the elapsed time in milliseconds
  - **Header** — a response header (name in **Path**)
  - **Body** — a value in the JSON (JavaScript Object Notation) body at a **Path** (e.g. `data.id`)
  - **Body-text** — the raw body as text
  - **CORS** — how a browser page on the origin in **Path** would take the response: Value
    `allowed`, `credentials` (also with cookies or the client certificate) or `blocked`, with
    `Equals` (or `NotEquals`). The request is sent with that `Origin` header unless it already has
    one, and the Diagnostics view shows the CORS verdict for any request that carries an `Origin`
- **Operator** — `Equals`, `NotEquals`, `Contains`, `Matches` (regex), `Exists`, `NotExists`,
  `LessThan`, `GreaterThan`, and `schema` (the JSON at the Path matches a JSON Schema — see below)
- **Value** — the expected value (not needed for `Exists`/`NotExists`)
- **Enabled** — tick to include the rule

After a send, the Tests tab shows each rule's pass/fail and the actual value.

## Examples

| Target | Op | Path | Value | Checks |
|---|---|---|---|---|
| Status | Equals | | `200` | the call returned 200 |
| Status | LessThan | | `300` | any 2xx |
| Time | LessThan | | `500` | responded within 500 ms |
| Header | Contains | `Content-Type` | `json` | JSON response |
| Body | Equals | `data.ok` | `true` | a JSON field's value |
| Body-text | Matches | | `"id":\s*\d+` | a regex over the whole body |
| CORS | Equals | `https://app.example.com` | `credentials` | a page there can read it, with credentials |

## JSON Schema assertions

A **Body** rule with the `schema` operator checks the JSON at its Path (`$` for the whole body)
against a JSON Schema given as the Value. One rule catches what a dozen field checks would: a
missing required field, a number where a string belongs, an extra property, an empty array.

```powershell
certapi send https://api.example.com/orders/7 --assert 'body $ schema {"type":"object","required":["id","total"],"properties":{"id":{"type":"integer"},"total":{"type":"number","minimum":0}}}'
certapi send https://api.example.com/orders --assert "body data.items schema @order-list.schema.json"
```

`@file` reads the schema from a file. A failure names where and why — `at $.items[2].total:
expected number, got string` — as the assertion's actual value, in the table, `--md` and `--junit`.

The checker is built in and works offline. It covers the keywords APIs actually use: `type`,
`enum`, `const`, `properties`, `required`, `additionalProperties`, `patternProperties`,
`min/maxProperties`, `items` and `prefixItems`, `min/maxItems`, `uniqueItems`, `contains`,
`min/maxLength`, `pattern`, `minimum`, `maximum`, `exclusiveMinimum/Maximum` (number and boolean
forms), `multipleOf`, `allOf`, `anyOf`, `oneOf`, `not`, `if`/`then`/`else`, boolean schemas,
local `$ref` (`#/…`, `$defs` and `definitions`), and OpenAPI 3.0's `nullable`. `format` is
informational and not checked, and a `$ref` to another file is not followed.

[Importing an OpenAPI document](17-Import-and-Export.md#import-openapi--swagger) writes a status
and a schema assertion for each operation, so an imported collection is a contract test as soon as
it runs.

## The rules never throw

Assertions are designed to **fail cleanly**, never crash the run:

- A malformed regex simply doesn't match (fails a `Matches`).
- A non-JSON body fails any Body-path check rather than erroring.
- A regex is **time-bounded** (2 seconds) so a catastrophic-backtracking pattern fails the assertion
  instead of hanging the run.

## Running assertions headless

`certapi run` evaluates a saved request's assertions:

- A request **passes** when all its assertions pass.
- A request with **no** assertions passes on any **2xx** response (a sensible default health check).
- Failed assertions are listed on **stderr**; the suite exits **1** if any request fails.

```powershell
certapi run "petstore/Get pet by id"     # pass/fail table, exit 1 if anything fails
certapi run --all --json                 # machine-readable results
```

See [Data-Driven Runs](13-Data-Driven-Runs.md) for running assertions across many rows, and the
[CLI Reference](21-CLI-Reference.md#run) for all `run` options.

## In CI (continuous integration)

Because a failing assertion exits non-zero, a saved suite is a ready-made smoke test:

```powershell
certapi run --all --workspace .\suite.json --no-record --json
if ($LASTEXITCODE -ne 0) { throw "API smoke tests failed" }
```

## JUnit XML for CI test reports

```powershell
certapi run --all --workspace .\suite.json --no-record --junit results.xml
```

`--junit` writes the run as JUnit XML, which Azure DevOps (`PublishTestResults`), GitLab
(`artifacts:reports:junit`), Jenkins and the GitHub test-report actions show as a test tab: one
test case per request, named by its path and grouped by its folder, each failed assertion with what
arrived instead, a request that got no answer as an error, and skipped chain steps as skipped. URLs
are redacted as in `--md`. Like every report, a file that cannot be written warns and never changes
the exit code.

## Keeping a run: `--md`

A run tells you what passed today. A folder of run notes tells you whether a suite is getting
better or worse — which is the thing a single terminal run can never show:

```powershell
certapi run --all --md report.md
certapi run --chain "Login then fetch" --md-vault C:\Users\me\Vault
```

The note's frontmatter carries `total`, `passed`, `failed` and the run timestamp, so a vault of
them can be charted over time. The body carries what a failure investigation actually needs: a
pass/fail table with per-request timings, and for each failure the assertion that broke **and what
arrived instead**.

A chain's report is numbered in step order, shows steps skipped after a failure rather than
dropping them, and links each step back to its request note from `certapi export markdown` — so a
chain report lands *in* the catalogue rather than beside it.

Captured variables are listed **by name only**, never by value: a captured value is usually the
credential the next step authenticates with, and these notes go into folders that sync.
Credential-looking query values are redacted too; `--md-include-secrets` keeps them.

`--md-vault` files reports as `certapi/runs/<name>-<timestamp>.md`, one note per run — the
history a trend needs is never overwritten. A second run in the same second gets `-2`, `-3`, … on its
name. If the report cannot be written the command says so and still exits on the run's own verdict:
a build must not turn green or red because of a folder permission.

Next: [Capturing Values](12-Capturing-Values.md).
