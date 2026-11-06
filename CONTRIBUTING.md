# Contributing

Issues and pull requests are welcome. This is a one-maintainer project, so small, focused changes land fastest.

## Before a pull request

- Build and test on Windows; the app is WPF and the test project targets `net9.0-windows`.
  `dotnet build -warnaserror` then `dotnet test --filter "Category!=StoreRoundTrip"`.
- `StoreRoundTrip` tests touch the real certificate store and `TlsBinding` tests touch `netsh` bindings; both are opt-in and must stay that way.
- The `serve --vhost` tests skip themselves in a prompt that cannot bind `http://+:<port>/`; run elevated to include them (CI always does). Mark a new test that binds every interface `[WildcardBindingFact]`.
- Before a release, or after a change to anything Windows-specific (console, certificate stores,
  proxy detection, Windows sign-in, the updater), run `tools\windows-checks.ps1`. It builds and
  runs the suite, publishes the exes as `release.yml` does, and checks the lone `certapi.exe`
  against local test servers; `-Admin`, `-Network` and `-State` add the checks that need an
  elevated terminal, the internet, or the live workspace. It prints PASS / FAIL and writes a
  `results.txt`, and lists what still needs a person: the desktop app, and typing at a console.
- Every behaviour lives in `ApiTester.Core` with a test. The app and `certapi` are thin layers over it.
- Anything that writes an archive, export or log must route credentials through the existing redaction (`SecretHeaders` for headers). A test fails if new code builds a HAR request without passing its URL through `RecordedUrl`; other writers are covered only by their own tests, so add one for a new writer.
- Add a `CHANGELOG.md` entry under `[Unreleased]` in the house style: what changed, what broke, nothing else.
- Bump `<Version>` in both `.csproj` files only when cutting a release; the tag is `vX.Y.Z`, the release title is the tag.

## Developing offline

Each release has `certapi-<version>-dev-windows-x64.zip`, a dev kit for a Windows x64 machine with no network. It unzips to one folder, `certapi-<version>-dev\`: the repository at the tag (no `.git`) and `offline\`, with every NuGet package that the solution, the self-contained publish of the app and `certapi`, and the local tool (Stryker.NET) restore, the .NET runtime packs among them; the wheels `docs/art/requirements.txt` names; the licence of each; and `setup.ps1`.

1. From a machine that can download, install what the kit does not carry: the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and, for the seal only, [Python 3.13](https://www.python.org/downloads/windows/). The kit's `offline\README.md` names the exact versions CI used and links their offline installers. The SDK has to bundle the same .NET runtime as the kit's runtime packs, so take the one it names. Windows PowerShell 5.1 runs the setup; git and Visual Studio are not needed.
2. In the kit: `powershell -ExecutionPolicy Bypass -File offline\setup.ps1`. It says what is missing, writes a `nuget.config` with the kit as the only package source, restores everything from it, and makes `.venv` for `docs/art` with a `pip.ini` that keeps pip offline. It needs no network.
3. Build, test and publish as CI does; `setup.ps1` prints the commands.

`tools/build-dev-kit.ps1` builds the kit (it needs the network); `tools/dev-kit/` has the setup script and the README the build fills in. Versions come from where CI reads them: the projects, `.config/dotnet-tools.json`, `docs/art/requirements.txt` and `docs/art/.python-version`. CI's Dev kit job builds the kit on every push, unzips it into a fresh folder, cuts off the internet and the package registries, and runs the setup, the build, the publish and the seal check from the kit alone; the release runs the same job on the tag and attaches the kit only when it passes. The tests run from the kit too, with the registries still cut off but without the proxy variables that cut off the rest: certapi reads `HTTPS_PROXY` as its own setting, and its tests expect a machine with no proxy. A new package needs no change to any of this, but one whose licence is not MIT, Apache-2.0 or BSD stops the build until someone has read its terms and listed it in the script.

## Style

Plain, specific, dry, honest. Sentence case in headings, no emoji, no superlatives. Palette values come from [BRAND.md](https://github.com/Real-Fruit-Snacks/rfs-brand/blob/main/BRAND.md); Mint only ever means "OK", Ember is the brand colour and never an error.

Assets under `docs/assets` are generated. The seal (the README picture, the project page's seal and the social card's): `python docs/art/make-seal.py` after `pip install -r docs/art/requirements.txt`; it draws from the `certapi doctor` transcripts in `docs/art`, and CI fails if its output is out of date. The social preview `docs/assets/social.png` is `docs/card.html` rendered at 1280 × 640. Design notes for the page are in `docs/DESIGN.md`. The app's icons: `python3 docs/tools/make-assets.py`. The RFS cover (`docs/assets/cover.toml`) is no longer on the page or in the README, but the rfs-brand gallery shows it: `python3 <path to Real-Fruit-Snacks/rfs-brand>/tools/make-cover.py docs/assets/cover.toml`.
