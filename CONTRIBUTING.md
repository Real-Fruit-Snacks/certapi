# Contributing

Issues and pull requests are welcome. This is a one-maintainer project, so small, focused changes land fastest.

## Before a pull request

- Build and test on Windows; the app is WPF and the test project targets `net9.0-windows`.
  `dotnet build -warnaserror` then `dotnet test --filter "Category!=StoreRoundTrip"`.
- `StoreRoundTrip` tests touch the real certificate store and `TlsBinding` tests touch `netsh` bindings; both are opt-in and must stay that way.
- Every behaviour lives in `ApiTester.Core` with a test. The app and `certapi` are thin layers over it.
- Anything that writes an archive, export or log must route credentials through the existing redaction; there is a test that fails if a new builder doesn't.
- Add a `CHANGELOG.md` entry under `[Unreleased]` in the house style: what changed, what broke, nothing else.
- Bump `<Version>` in both `.csproj` files only when cutting a release; the tag is `vX.Y.Z`, the release title is the tag.

## Style

Plain, specific, dry, honest. Sentence case in headings, no emoji, no superlatives. Palette values come from [BRAND.md](https://github.com/Real-Fruit-Snacks/.github/blob/main/BRAND.md); Mint only ever means "OK", Ember is the brand colour and never an error.

Brand assets under `docs/assets` are generated: `python3 docs/tools/make-assets.py <path to Real-Fruit-Snacks/.github>`.
