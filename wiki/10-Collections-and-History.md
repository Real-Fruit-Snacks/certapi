# 10. Collections & History

Your saved library of requests, and a log of everything you've sent.

## Collections

A **collection** is a tree of folders and requests in the sidebar (`Ctrl+H` to toggle it). Save a
request into a collection to keep it; click a saved request to load it into a tab.

- **Folders** organize requests and can carry **defaults** (below).
- **Rename** and **delete** from the item's context menu.
- Open a saved request in a **new tab** to keep several in flight.

### Collection defaults

Right-click a folder (or collection) and set **defaults**: a **base website** and/or a **client
certificate**. Every request inside inherits them unless it overrides them. This is the fix for
"every time I click a new endpoint I have to re-pick the website and cert" — set them once on the
folder.

### Known-good endpoints

When you run saved requests (in the app or via `certapi run`), each request records its last result
and a **known-good** marker (green when the last response was 2xx and any [tests](11-Testing-and-Assertions.md)
passed). A collection becomes a lightweight health dashboard of your endpoints.

## Saved websites (base URLs)

Save a base URL (Uniform Resource Locator) like `https://internal.corp` and the URL box becomes just
the path after it — fire
`/api/orders` without retyping the host. Saved websites also feed the [gateway](19-Local-Gateway.md):
`certapi serve <saved-website>` can resolve one from a workspace.

## History

Every request you send is logged to the **History** sidebar, labelled by path. Click an entry to
reload the *entire* request — website, certificate, headers, auth, body — and the response it
returned. History is part of the workspace, so it persists across sessions.

## Workspaces

Everything above lives in a **workspace**:

- **Live workspace** — `%AppData%\CertApiTester\state.json`, shared by the app and the CLI
  (command-line interface). This is
  what you edit day to day. Secrets in it — captured tokens/cookies, a saved request's auth secret,
  a variable marked **secret** — are encrypted for your Windows user, not stored in plain text.
- **Workspace files** — a separate `.json` you manage explicitly. Point either tool at one with
  `--workspace suite.json`. Use these to:
  - check a request suite into source control,
  - hand a reproducible set of requests + environments to a teammate,
  - run a suite in CI (continuous integration) without touching your personal state.

Export/import whole workspaces from [Import & Export](17-Import-and-Export.md):

```powershell
certapi export workspace -o team-setup.json   # strips secrets by default
```

What an export strips, and how `--include-secrets` keeps it, is in
[Export a whole workspace](17-Import-and-Export.md#export-a-whole-workspace).

> **A note on the CLI and the live workspace:** while the app is open, headless runs **skip writing
> results** back to the live state (the app would overwrite them when it closes). Scheduled/CI runs
> against a `--workspace` file record normally. See [Data-Driven Runs](13-Data-Driven-Runs.md) and
> the [CLI Reference](21-CLI-Reference.md#run).

Next: [Testing & Assertions](11-Testing-and-Assertions.md).
