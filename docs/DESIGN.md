# Design: certapi (`docs/`, README seal, and WPF desktop palette)

## Concept

`certapi` validates mutual-TLS handshakes stage-by-stage (`url`, `proxy`, `dns`, `tcp`, `tls`, `http`). The project page and README seal render those six stages as concentric guilloche rings around the final HTTP status (`200 OK` vs `400 BAD REQUEST`).

## Brand System

Built on **Cyclorama Dawn `v2.1.0`** (`Real-Fruit-Snacks/rfs-brand`):
- **Default mode:** Beacon Horizon Dark (`LX 263`, matte `#050507`) with opt-in Alabaster Day Light (`LX 512`, `#F5F7F9`) toggle synced across tabs via `localStorage['rfs-theme']`.
- **Two chromatic poles:** Glacial Steel (`#1F547A` / `#74B8E8` / `#164669`) and Maritime Beacon Orange (`#F06E2B` / `#FF9254` / `#AD3D0A`), strictly `in oklab`.
- **Semantic signals:** Mint (`#3DDC97` / `#127247`) for `200 OK`, Amber (`#FFC247` / `#885800`) for TLS client certificate details, and **Signal Crimson** (`#FF5C6C` / `#B91C2E`) for `400 Bad Request` and error states.
- **Typography:** Self-hosted variable WOFF2 — `Big Shoulders Stencil` (display headings & status stamps), `Albert Sans` (UI and prose), and `Azeret Mono` (microprint, doctor traces, and commands).
- **WPF Desktop App:** `TerminalWorkbench.xaml`, `Palette.Light.xaml`, `NativeTheme.cs` (DWM `#050507` titlebar), `SyntaxHighlighter.cs`, and `icon.ico` share the identical Cyclorama Dawn `v2.1.0` tokens.
- **Shell & scrollbars:** Uses `.rfs-shell` + `.rfs-shell-header` + `.rfs-scroll-region` so the vertical scrollbar starts strictly below the top header bar.

## Generated Art & CI Verification

`docs/art/make-seal.py` generates `docs/assets/seal-{dark,light}.svg`, `docs/art/seal-still-dark.svg`, and the inline `<!-- seal -->` markup in `docs/index.html`. `.github/workflows/ci.yml` (`art` job) regenerates and enforces zero diff. `docs/assets/cover-*` and `social-*` are generated via `rfs-brand/tools/make-cover.py`.
