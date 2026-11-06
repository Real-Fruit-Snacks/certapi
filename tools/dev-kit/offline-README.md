# certapi {{version}} dev kit (Windows x64)

Everything needed to build, test and publish certapi {{version}} on a Windows x64 machine with no
network: the source at commit `{{commit}}` ({{commit-date}}) and every package a build
fetches. Compilers and runtimes are not in it; install the ones below first, from any machine that
can download.

## What's in it

| Path | What |
|---|---|
| `{{kit}}\` | The repository at that commit, as `git archive` writes it (no `.git`) |
| `offline\nuget\` | {{nuget-count}} NuGet packages ({{nuget-mb}} MB): the solution's, the .NET {{runtime}} runtime packs the self-contained publish bundles, and the local tool in `.config\dotnet-tools.json` (Stryker.NET) |
| `offline\python\` | {{wheels}}: the packages in `docs\art\requirements.txt`, for CPython {{python-minor}} on win_amd64 |
| `offline\licences\` | The licence and notices of all {{licence-count}} packages ([index](licences/README.md)) |
| `offline\setup.ps1` | Checks the toolchain, points dotnet and pip at the folders above, and restores everything from them |
| `offline\toolchain.json` | The versions below, which `setup.ps1` checks |

## Install first

These are the versions CI built and tested this kit with. Each link is the official installer that
works with no network once downloaded.

| Tool | Version | Needed for | Offline installer |
|---|---|---|---|
| .NET SDK | {{sdk}} (bundles runtime {{runtime}}) | Build, test, publish | [dotnet-sdk-{{sdk}}-win-x64.exe]({{sdk-url}}) |
| Python | {{python}} | The seal in `docs\art` only | [python-{{python}}-amd64.exe]({{python-url}}) |
| PowerShell | 5.1 (part of Windows) or {{pwsh}} | `setup.ps1` (either works) | [PowerShell {{pwsh}} for win-x64]({{pwsh-url}}) |
| Edge WebView2 Runtime | {{webview2}} | The desktop app's Rendered view only; Windows 11 has it | [Evergreen Standalone Installer, x64]({{webview2-url}}) |

The SDK has to be {{sdk}}, or another 9.0 SDK that bundles .NET runtime {{runtime}}: an SDK
restores the runtime packs and the ILLink package for its own runtime, and the kit has
{{runtime}}'s. `setup.ps1` says so when the SDK does not match. Nothing else is needed: no Visual
Studio, no git, no admin rights after the installers.

## Steps

1. Unzip `{{zip}}` anywhere. It makes one folder, `{{kit}}`.
2. In that folder: `powershell -ExecutionPolicy Bypass -File offline\setup.ps1` (or
   `pwsh -File offline\setup.ps1`). It writes `nuget.config` beside `ApiTester.sln` with
   `offline\nuget` as the only package source, restores the solution, the publish and the local
   tools from it, and makes `.venv` for `docs\art` with a `pip.ini` that keeps pip on
   `offline\python`. It needs no network and stops with what is missing if it cannot finish.
3. Then, as CI does:

   ```powershell
   dotnet build --configuration Release --no-restore -warnaserror
   dotnet test --configuration Release --no-build --filter "Category!=StoreRoundTrip"
   dotnet publish src\ApiTester.App -c Release -r win-x64 --self-contained -o publish
   dotnet publish src\ApiTester.Cli -c Release -r win-x64 --self-contained -o publish-cli
   .venv\Scripts\python docs\art\make-seal.py
   ```

   `publish\ApiTester.App.exe`, `publish-cli\certapi.exe` and `msquic.dll` beside each are the
   release's files. `make-seal.py` redraws the seal from the transcripts in `docs\art`.

After setup, every `dotnet` command in the folder restores from the kit, so a package the kit
does not have fails the restore with its name instead of reaching for nuget.org. To add one,
download its `.nupkg` on a connected machine and put it in `offline\nuget`.

## What it leaves out

- The toolchain above: the .NET SDK, Python, PowerShell 7 and the WebView2 Runtime.
- The `StoreRoundTrip` tests: CI leaves them out too, because they add a certificate to your
  Windows store. `tools\windows-checks.ps1` does the same, and its `-Network` checks need the
  internet.
- The repository's history: there is no `.git`. `git init` makes a fresh one.
