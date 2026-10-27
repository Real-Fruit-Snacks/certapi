# 25. Building from Source

Prefer to compile it yourself, or want to contribute? Here's the layout and the commands.

## Prerequisites

- **.NET 9 SDK** (software development kit) (Windows).
- Windows 10 / 11 (the app is WPF — Windows Presentation Foundation; the tests use the Windows
  certificate APIs — application programming interfaces).

## Solution layout

```
ApiTester.sln
├─ src/
│  ├─ ApiTester.Core/   # the engine: HTTP/mTLS, model, state, parsing, streaming, OAuth, mock…
│  ├─ ApiTester.Cli/    # certapi — the command-line client (Commands/*)
│  └─ ApiTester.App/    # the WPF desktop app
├─ tests/
│  └─ ApiTester.Tests/  # xUnit tests (Core + CLI + a few WPF load smokes)
├─ docs/                # the GitHub Pages site
├─ wiki/                # this handbook
└─ wordlists/           # the starter endpoint list
```

The **Core** library holds all the logic; the **CLI** (command-line interface) and **App** are thin
front ends over it. Most
behavior is testable without the GUI (graphical user interface).

## Build and test

```powershell
dotnet build   -c Release
dotnet test    -c Release
```

The test suite is fast and self-contained — it spins up loopback mTLS (mutual Transport Layer
Security)/HTTP (Hypertext Transfer Protocol) servers in memory (no
network, no real certificates needed).

The virtual-host tests (`serve --vhost`) bind every interface, which Windows allows only from an
elevated prompt or with a URL reservation. In an ordinary prompt they are reported as **skipped**,
with that reason; run `dotnet test` elevated to include them. CI always runs them.

## Run the two front ends

```powershell
dotnet run --project src/ApiTester.App     # the desktop app
dotnet run --project src/ApiTester.Cli -- send https://api.github.com/zen
```

(Everything after `--` is passed to `certapi`.)

## Publish self-contained executables

```powershell
dotnet publish src/ApiTester.App -c Release -r win-x64 --self-contained -o publish
dotnet publish src/ApiTester.Cli -c Release -r win-x64 --self-contained -o publish-cli
# -> publish/ApiTester.App.exe   and   publish-cli/certapi.exe
```

These bundle the .NET runtime, so they run on any Windows 10/11 x64 machine with nothing installed.

## Releases

Releases are cut by pushing a `v*` tag; CI (continuous integration) builds the self-contained
executables, attaches the starter
wordlist, and bundles a **full source zip** of the repository at that tag. See `.github/workflows/`.

## Contributing

- Keep logic in **Core** with tests; the front ends stay thin.
- The tests avoid the network and real certificates — follow the existing loopback-server pattern.
- Run `dotnet test` before opening a pull request.

---

Back to the [handbook home](README.md).
