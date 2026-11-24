# AGENTS.md — Repository Guardrails (Real-Fruit-Snacks)

This repository is part of **Real-Fruit-Snacks** (**RFS**) and follows the **Cyclorama Dawn — Steel & Beacon Orange** brand system (`Real-Fruit-Snacks/rfs-brand`).

## Core Commitments
1. **Local-first by default:** Operate directly on the user's machine with zero accounts, zero telemetry, and zero third-party CDN calls.
2. **Plain, specific, honest voice:** No emoji, no hype copy, no exclamation marks in headings or descriptions.
3. **Verifiable outputs:** Every command and UI state displays plain inputs, outputs, and explicit status words (`[ok]`, `[!]`, `[x]`, `[i]`).

## Brand & UI Rules (Summary of `rfs-brand/brand/BRAND-RULES.md`)
- **Theme & Motion:** Dark mode (`#050507`, `LX 263`) is the explicit default on every page (`<meta name="color-scheme" content="dark">`); Light (`#F5F7F9`, `LX 512`) is an opt-in toggle. Keep browser animations and transitions enabled by default (do not turn them off by default).
- **Palette:** Glacial Steel `#1F547A` + Maritime Beacon Orange `#F06E2B`. Always interpolate gradients in Cartesian `in oklab` (never polar `in oklch`).
- **Typography:** Self-hosted `Big Shoulders Stencil` (display), `Albert Sans` (UI/prose), `Azeret Mono` (code/CLI).
- **Web UIs & Icons:** Load `rfs.css` then `rfs-components.css`; use the 60-icon Precision Silkscreen sprite `rfs-icons.svg` (`24×24`, `1.75px` `square`/`miter`, recolorable via `.rfs-icon-{steel,beacon,ok,warn,fail,horizon}`).
- **Code Blocks:** Always display a language header + syntax coloring (`.rfs-code-card` + `.rfs-tok-*`) and wrap by default (`pre-wrap`, `overflow-wrap: anywhere`; `.rfs-code-scroll` opt-in).
- **Badges & Chips:** `D · Terminal Token` (`4px` recessed `code-bg`, `1px` border, bracketed `.k` prefix) — zero `99px` capsule pills or status dots.
- **Scrollbars:** Never extend a vertical scrollbar to the top edge alongside a top header — use `.rfs-shell` + `.rfs-shell-header` + `.rfs-scroll-region`.
- **Modals & Toasts:** Strictly forbid `window.alert()`, `window.confirm()`, and `window.prompt()`. Use branded native `<dialog class="rfs-dialog">` modals, and always prompt destructive actions (delete/clear/reset/overwrite) with `<dialog class="rfs-dialog is-destructive">`, `autofocus` on **Cancel**, and `.rfs-btn-fault` confirm. Tint toasts by semantic status (`.rfs-toast.is-{ok,warn,fail,info}`).
- **CLIs:** Respect `NO_COLOR` and non-TTY stdout, keep bracketed ASCII prefixes (`[ok]`, `[!]`, `[x]`, `[i]`), and exit `0` (ok), `1` (runtime), `2` (usage).
