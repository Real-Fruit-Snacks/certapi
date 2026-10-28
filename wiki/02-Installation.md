# 2. Installation

## Requirements

- **Windows 10 or 11**, 64-bit.
- Nothing else for the self-contained downloads — the .NET 9 runtime is bundled.
- The **Rendered website** view uses the Microsoft Edge **WebView2** runtime, which ships with
  Windows 11 and up-to-date Windows 10. If it's missing, only that one tab is affected; everything
  else works.

## Download (recommended)

Grab the latest release from the repository's **Releases** page. Each release attaches:

| Asset | What it is |
|---|---|
| `ApiTester.App.exe` | The desktop app (self-contained, x64) |
| `certapi.exe` | The command-line client (self-contained, x64) |
| `msquic.dll` | The QUIC library HTTP/3 needs. Keep it in the same folder as the `.exe` files; everything else works without it |
| `common-api-endpoints.txt` | A starter wordlist for [endpoint discovery](14-Endpoint-Discovery.md) |
| `certapi-source-<tag>.zip` | The full repository source at that tag |
| `SHA256SUMS.txt` | The SHA-256 of each file |
| `certapi.json` | A [Scoop](https://scoop.sh) manifest: `scoop install https://github.com/Real-Fruit-Snacks/certapi/releases/latest/download/certapi.json` |

Both `.exe` files are **portable** — no installer, no admin rights. Put them anywhere (a USB
(Universal Serial Bus) stick, a locked-down VM (virtual machine)) and run them, with `msquic.dll`
beside them if you want `--http3`. To use `certapi` from any prompt, drop `certapi.exe` (and
`msquic.dll`) in a folder that's on your `PATH`.

## First run

1. Double-click `ApiTester.App.exe`. The main window opens on your last workspace (empty the first
   time).
2. If you have client certificates in your Windows store, they appear in the **CERTIFICATE** dropdown
   on the request line. No certs? That's fine — you can still test any endpoint that doesn't require
   one.
3. Prove the whole certificate path works without a real server: click **Run Self-Test** at the
   bottom of the window (or run `certapi selftest`). See [Self-Test](23-Troubleshooting.md#self-test).

## Where your data lives

The app stores its workspace — tabs, collections, environments, history, saved tokens, and your theme
choice — in:

```
%AppData%\CertApiTester\state.json
```

That's a single JSON (JavaScript Object Notation) file. Back it up, or hand it to a teammate, and
everything travels with it — except secrets (captured tokens/cookies, saved auth values, variables
marked **secret**), which are encrypted with the Windows Data Protection API (DPAPI) for the Windows
user who saved them, so they don't travel to a different user or machine. The
CLI (command-line interface) reads the same file by default, so requests you build in the app are runnable headless
immediately. You can also point either tool at a separate **workspace file** with `--workspace` — see
[Collections & History](10-Collections-and-History.md).

## Updating

- **Command line:** `certapi update` replaces `certapi.exe` (and `ApiTester.App.exe` beside it) with the latest release; `certapi update --check` only asks. See [`update`](21-CLI-Reference.md#update).
- **Desktop app:** **Help › About › Check for updates**. When a newer release exists, an **Update to x.y.z** button appears in the title bar; it downloads the app (and `certapi.exe` when it sits in the same folder), checks each file, replaces them and restarts. Tick **Check for a new release when certapi starts** to have the app ask GitHub at most once a day — it is off until you turn it on, and the app makes no other call of its own.

Both check every file against the SHA-256 GitHub publishes for it before anything is replaced; nothing changes if a check fails. A copy installed with Scoop is updated with `scoop update certapi`. The app's check goes out through Windows' own proxy settings; on a network that needs a CA file or a PAC file of your own, use `certapi update` with `--ca-file` / `--pac`, which updates the app beside it too.

## Verifying a download

The self-contained `.exe` files are large (~35–65 MB — megabytes) because they bundle the runtime — that's
expected. To verify a file by hand, compare `Get-FileHash certapi.exe` with its line in the release's
`SHA256SUMS.txt`. The executables are **not** code-signed unless your build pipeline signs them.

## Building it yourself

Prefer to compile from source? See [Building from Source](25-Building-from-Source.md).

Next: [Quick Start](03-Quick-Start.md).
