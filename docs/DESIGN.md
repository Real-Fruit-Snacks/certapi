# Design: the certapi page (docs/) and README seal

## Concept

A client certificate is a credential, and certapi's job is to present it. So the page borrows from security printing, the craft behind banknotes, share certificates and passports: guilloche rings cut on a rose engine, microprint round the edge, labelled fields. Every one of those is filled with the data of a real mutual-TLS connection instead of ornament. A visitor should leave knowing that certapi gets a request through the TLS handshake with a certificate from the Windows store, and that it shows its work stage by stage.

## Audience and primary action

Engineers and platform teams on Windows who have to call an internal API that asks for a client certificate, often a smart-card or non-exportable one. They need to see that it works with the store as it is, what it prints when something is wrong, and then copy the install lines.

## Type

- Display: Source Serif 4, 600, optical size 60. A plain, institutional serif: the face of a printed certificate rather than a terminal. It sets the headline, section heads, the wordmark and the status code in the seal.
- Text: Source Sans 3, 400 and 600, from the same family. Its 600 in tracked capitals (0.14em) is the legend face in the seal and the field labels.
- Mono: Source Code Pro, 400 and 500, for commands, transcripts and the microprint.
- Scale: `scale.mjs`, 16px to 18px, ratio 1.2 to 1.333. The headline is `clamp(2.3rem, 1.45rem + 3.6vw, 4.4rem)`.
- Self-hosted in `fonts/` as variable woff2 (Google Fonts latin subsets) with their OFL licences. `source-code-pro-symbols.woff2` adds the → certapi prints.

## Color

The RFS Nightshift and Dayshift values and nothing else. The OS setting decides the theme, the Nightshift/Dayshift switch overrides it, and the choice is saved as `rfs-theme` like the other RFS pages.

| Role | Night | Day | Use |
|---|---|---|---|
| bg / surface / raised | `#0C0D10` / `#13151A` / `#1A1D24` | `#F6F5F2` / `#FFFFFF` / `#ECEAE4` | Page; inline code; the pressed run button |
| code | `#070809` | `#FFFFFF` | Transcripts and the install block |
| line / line-strong | `#262A33` / `#636872` | `#D6D3CB` / `#89867F` | The uncut plate under the seal, rules, the guilloche section divider, borders |
| ink / mute | `#E8E6E1` / `#8A8F99` | `#1A1B1E` / `#666A73` | Text; ring legends; the parts of a transcript that aren't the point |
| Ember | `#FF6A3D` | `#C03B0A` | The engraving (certapi's own lines), the Copy button, field labels, note numbers, focus |
| Amber | `#FFC247` | `#925F00` | The client certificate: the tls ring and "client certificate presented" |
| Mint | `#3DDC97` | `#15794D` | 200 OK and `ok` only |
| Raspberry | `#F06292` | `#B0306A` | 400 Bad Request, the refused run |

Every colour that means a state comes with words: "200 OK", "400 Bad Request", "client certificate presented: none", `[ok  ]`. Night is glowing line work on black; Day is red ink on paper.

## Layout

A 76rem wrap. The hero puts the headline and install block on the left and the seal on the right; on a phone the seal comes straight after the headline and the install block after it. Below, each section takes the form its content needs: a statement (why), a full transcript with numbered notes (doctor), two command panels side by side (command line, gateway), a field list set like the fields on a certificate (what's in it), the app screenshot, two lists, the shortcuts table. Sections are divided by a one-line guilloche border (three interlaced waves) instead of tinted bands.

## Signature

The seal. Six engraved rings, one per stage `certapi doctor` checks (url, proxy, dns, tcp, tls, http) from the outside in, each a band of one curve repeated with small rotations, the way a rose engine cuts. Each ring is broken where its legend sits; the tls ring also carries a bottom legend. The microprint round the rim is the run's own server URL, cipher suite and server certificate thumbprint.

## Wow moment

The page loads showing the uncut plate: every ring in the line colour. Then the rings engrave themselves outside in, each sweeping clockwise from its legend, with the stage's real time from the run in the legend. The tls ring cuts in Amber with "client certificate presented", and the centre stamps "200 OK" in Mint at 2.6 s. "Without it" replays the same URL from a second real run with no certificate: the tls ring cuts in plain line colour, its legend reads "client certificate presented: none", and the centre stamps "400 Bad Request" in Raspberry. On a desktop pointer the rings turn up to 6° against each other over the plate, and the engraving shimmers like a moiré on a banknote.

- **The runs:** `docs/art/doctor-with-cert.txt` and `doctor-without-cert.txt`, exactly as `certapi doctor` printed them against client.badssl.com on 2026-11-05 (certapi 1.96.0, Windows 11). The certificate is BadSSL's published test one, from a file; the caption says so.
- **No JS:** the seal plays once on load (CSS only) and ends finished; the run buttons stay hidden.
- **Reduced motion:** the finished seal, no engraving, no turning. The buttons swap runs instantly.
- **Phone:** the same seal, full width, after the headline. The legends get small there; the transcript below has the same facts as text.

## README seal

`docs/art/make-seal.py` draws the same rings into `assets/seal-{dark,light}.svg`, with the seal on the left and what the server asked for, what certapi presented and what came back on the right. Text is converted to paths from `fonts/` (an `<img>` can't load fonts). It plays the run with the certificate, then the run without, in an 18 s loop; its base styles are the finished first run. The same script writes the page's seal between the `<!-- seal -->` markers in `index.html`, and `art/seal-still-dark.svg` for `card.html`, which renders to the 1280 × 640 social preview `assets/social.png`. CI regenerates all of it and fails on a diff.

Don't wrap the README's `<picture>` in a link: GitHub then shows the light image in dark mode.

## Motion

The engraving uses a stroke-dash sweep, 0.55 s per ring (0.8 s for tls), staggered 0.3 s; the stamp scales from 1.16 with `cubic-bezier(.2, .7, .2, 1)`. The pointer turn eases over 0.7 s. The theme switch sweeps a circle out from the click point. Nothing on the page loops.

## Voice

Plain and exact, like the README: real commands, real output, limits stated. Transcripts are quoted as certapi printed them; example hosts (`internal.corp`) are labelled as examples.

## Files

- `index.html`, `styles.css`, `script.js`, `fonts/`, `card.html`, and the icons and manifest at the root of `docs/`. The favicon is the seal reduced to two broken rings, Ember over Amber, with a Mint centre, on the Nightshift tile.
- `assets/cover-*`, `assets/social-{dark,light}.png` and `assets/cover.toml` are the RFS brand cover from rfs-brand's `make-cover.py`. The page doesn't use them, but the rfs-brand site's cover gallery loads `cover-{dark,light}.svg` from here, so they stay.
- `assets/icon.svg`, `favicon.ico` and the PNGs beside them are the desktop app's product mark from `tools/make-assets.py`, which also writes the WPF icon.

## Alternatives considered

- A handshake ladder: the TLS 1.2 messages as a sequence chart between your machine and the server, with the store drawn as a sealed box that only the signature leaves. Precise, but a diagram rather than an object, and colder.
- The certificate store as the page: certmgr's tree (Current User › Personal › Certificates) as navigation, with the hero picking a row and sending it. Very Windows, but a pastiche of a UI people don't love.
