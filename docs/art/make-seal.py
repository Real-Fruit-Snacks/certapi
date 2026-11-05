"""Draw the certapi seal from two real `certapi doctor` runs.

A guilloche rosette, one engraved ring per stage that `certapi doctor` checks (url, proxy, dns, tcp,
tls, http), from the outside in. Each ring's legend carries that stage's time from the run, and the
centre carries the HTTP status. The runs are the transcripts next to this file, exactly as certapi
printed them:

    doctor-with-cert.txt     certapi doctor https://client.badssl.com/ --cert-file badssl.com-client.p12 --cert-password badssl.com
    doctor-without-cert.txt  certapi doctor https://client.badssl.com/

Writes:
    docs/assets/seal-dark.svg, seal-light.svg   the README picture: the run with the certificate (200 OK),
                                                then the same URL without one (400), in an 18 s loop.
                                                Text is turned into paths from docs/fonts, because an
                                                SVG shown through <img> can't load fonts. The base
                                                styles are the finished first run, which is what a
                                                viewer that skips the animation shows.
    docs/art/seal-still-dark.svg                the finished seal alone, for docs/card.html (the social card).
    docs/index.html                             the seal on the project page, between the
                                                <!-- seal --> markers; the page's CSS colours and
                                                animates it, and its text uses the page's fonts.

Run from the repo root:

    python -m pip install -r docs/art/requirements.txt
    python docs/art/make-seal.py
"""
import html
import math
import os
import re
import sys

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.dirname(HERE)
FONTS = os.path.join(DOCS, "fonts")
ASSETS = os.path.join(DOCS, "assets")

# RFS Nightshift and Dayshift
THEMES = {
    "dark": dict(bg="#0C0D10", plate="#13151A", line="#262A33", ink="#E8E6E1", mute="#8A8F99",
                 ember="#FF6A3D", amber="#FFC247", mint="#3DDC97", rasp="#F06292"),
    "light": dict(bg="#F6F5F2", plate="#FFFFFF", line="#D6D3CB", ink="#1A1B1E", mute="#666A73",
                  ember="#C03B0A", amber="#925F00", mint="#15794D", rasp="#B0306A"),
}

# --------------------------------------------------------------------------------------------
# The runs
# --------------------------------------------------------------------------------------------

STAGE = re.compile(r"^\s*\[(\w+)\s*\]\s+(\w+)\s+(.*?)\s+\((\d+) ms\)\s*$")


def parse(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as fh:
        lines = fh.read().splitlines()
    run = {"stages": {}, "url": lines[0].split(" · ", 1)[1].strip()}
    for i, line in enumerate(lines):
        m = STAGE.match(line)
        if m:
            run["stages"][m.group(2)] = dict(status=m.group(1), detail=m.group(3), ms=int(m.group(4)))
        s = line.strip()
        if s.startswith("client certificate presented:"):
            run["presented"] = s.split(":", 1)[1].strip()
        if s.startswith("the server accepts client certificates from"):
            run["accepts"] = lines[i + 1].strip()
        if s.startswith("protocol:"):
            run["protocol"] = s.split(":", 1)[1].strip()
        m = re.match(r"All stages passed in (\d+) ms", s)
        if m:
            run["total"] = int(m.group(1))
    http = run["stages"]["http"]["detail"]           # "HTTP/1.1 200 OK"
    run["code"], run["reason"] = http.split(" ", 2)[1:]
    return run


RUNS = {"with": parse("doctor-with-cert.txt"), "without": parse("doctor-without-cert.txt")}
ORDER = ["url", "proxy", "dns", "tcp", "tls", "http"]


def tls_version(run):
    m = re.match(r"Tls(\d)(\d)", run["protocol"])
    return f"TLS {m.group(1)}.{m.group(2)}"


def legends(run):
    """The ring legends: stage name, what it found, and its time, from the transcript."""
    st = run["stages"]
    dns = re.search(r"→ (\d+ address\w*)", st["dns"]["detail"]).group(1)
    port = re.search(r":(\d+)$", st["tcp"]["detail"]).group(1)
    proxy = st["proxy"]["detail"].split(" ")[0]
    top = {
        "url": f"URL · {st['url']['ms']} MS",
        "proxy": f"PROXY · {proxy} · {st['proxy']['ms']} MS",
        "dns": f"DNS · {dns.upper()} · {st['dns']['ms']} MS",
        "tcp": f"TCP · PORT {port} · {st['tcp']['ms']} MS",
        "tls": f"{tls_version(run)} · {st['tls']['ms']} MS",
        "http": f"HTTP · {st['http']['ms']} MS",
    }
    presented = run["presented"]
    bottom = "CLIENT CERTIFICATE PRESENTED" if presented != "none" else "CLIENT CERTIFICATE PRESENTED: NONE"
    return top, bottom


# The microprint around the edge: the real run's server, cipher and server certificate.
def microprint():
    run = RUNS["with"]
    lines = open(os.path.join(HERE, "doctor-with-cert.txt"), encoding="utf-8").read().splitlines()
    cipher = next(l.split(":", 1)[1].strip() for l in lines if l.strip().startswith("cipher:"))
    leaf = next(l.strip()[3:] for l in lines if l.strip().startswith("1. "))   # "CN=*.badssl.com  F95E…"
    return f"certapi doctor {run['url']} · {cipher} · {re.sub(r'  +', ' ', leaf)} · "


# --------------------------------------------------------------------------------------------
# Geometry: an 800-unit seal centred on (400, 400)
# --------------------------------------------------------------------------------------------

C = 400.0
# stage: inner and outer radius of the band, lobes, rotated copies, (modulation depth, frequency), form
RINGS = {
    "url":   dict(rin=330, rout=362, m=40, n=9, mod=(0.0, 0), form="wave"),
    "proxy": dict(rin=290, rout=322, m=30, n=8, mod=(0.9, 6), form="wave"),
    "dns":   dict(rin=250, rout=282, m=36, n=6, mod=(0.0, 0), form="scallop"),
    "tcp":   dict(rin=210, rout=242, m=26, n=8, mod=(1.3, 4), form="wave"),
    "tls":   dict(rin=146, rout=200, m=20, n=12, mod=(0.0, 0), form="wave"),
    "http":  dict(rin=102, rout=136, m=16, n=8, mod=(0.5, 8), form="wave"),
}
RIM = (396, 370)            # the two border circles
MICRO_R = 380.5             # microprint baseline
LEGEND = dict(size=17.0, ls=0.14)
MICRO = dict(size=10.0, ls=0.0)


def rm(stage):
    r = RINGS[stage]
    return (r["rin"] + r["rout"]) / 2


def band_path(stage, start_deg):
    """One line of a ring's band, starting at start_deg so the engraving sweeps from there."""
    r = RINGS[stage]
    mid, amp = rm(stage), (r["rout"] - r["rin"]) / 2 - 1.5
    depth, freq = r["mod"]
    m = r["m"]
    n = m * 12
    t0 = math.radians(start_deg)
    pts = []
    for i in range(n):
        t = t0 + 2 * math.pi * i / n
        if r["form"] == "scallop":
            rad = mid - amp + 2 * amp * abs(math.sin(m * t / 2))
        else:
            rad = mid + amp * math.sin(m * t + depth * math.sin(freq * t))
        pts.append(f"{C + rad * math.cos(t):.1f} {C + rad * math.sin(t):.1f}")
    return "M" + " ".join(pts) + "Z"


# --------------------------------------------------------------------------------------------
# Fonts and text
# --------------------------------------------------------------------------------------------

_fonts = {}
FACES = {
    "sans": ("source-sans-3-latin.woff2", {"wght": 600}),
    "serif": ("source-serif-4-latin.woff2", {"wght": 600, "opsz": 60}),
    "mono": ("source-code-pro-latin.woff2", {"wght": 500}),
    "mono4": ("source-code-pro-latin.woff2", {"wght": 400}),
    "sans4": ("source-sans-3-latin.woff2", {"wght": 400}),
}


def font(face):
    if face not in _fonts:
        name, axes = FACES[face]
        _fonts[face] = instancer.instantiateVariableFont(TTFont(os.path.join(FONTS, name)), axes)
    return _fonts[face]


def advances(s, face, size, ls):
    f = font(face)
    cmap, gs, k = f.getBestCmap(), f.getGlyphSet(), size / f["head"].unitsPerEm
    out = []
    for ch in s:
        if ord(ch) not in cmap:
            sys.exit(f"{face} has no glyph for {ch!r}")
        out.append((cmap[ord(ch)], gs[cmap[ord(ch)]].width * k))
    width = sum(a for _, a in out) + ls * size * (len(out) - 1)
    return out, width


def width(s, face, size, ls=0.0):
    return advances(s, face, size, ls)[1]


class Glyphs:
    """Glyph outlines drawn once in <defs> and placed with <use>."""

    def __init__(self):
        self.ids = {}

    def get(self, face, name):
        key = (face, name)
        if key not in self.ids:
            f = font(face)
            pen = SVGPathPen(f.getGlyphSet(), ntos=lambda v: "%g" % round(v))
            f.getGlyphSet()[name].draw(pen)
            self.ids[key] = (f"q{len(self.ids)}", pen.getCommands())
        return self.ids[key][0] if self.ids[key][1] else None

    def defs(self):
        return "".join(f'<path id="{i}" d="{d}"/>' for i, d in self.ids.values() if d)

    def line(self, s, x, y, face, size, ls=0.0, anchor="start"):
        """Straight text with its baseline at y: one scaled group, glyphs placed along it in font units."""
        glyphs, w = advances(s, face, size, ls)
        k = size / font(face)["head"].unitsPerEm
        px = {"start": x, "middle": x - w / 2, "end": x - w}[anchor]
        uses, pos = [], 0.0
        for name, adv in glyphs:
            gid = self.get(face, name)
            if gid:
                uses.append(f'<use href="#{gid}" x="{pos / k:.0f}"/>')
            pos += adv + ls * size
        return f'<g transform="matrix({k:.5f} 0 0 {-k:.5f} {px:.1f} {y:.1f})">{"".join(uses)}</g>'

    def arc(self, s, radius, centre_deg, face, size, ls=0.0, bottom=False, fit=None):
        """Text along a circle round (C, C), centred on centre_deg (-90 is the top). Top text reads
        clockwise with its letters pointing out; bottom text reads left to right, letters pointing in.
        fit: stretch the letter spacing so the text spans exactly this many degrees."""
        glyphs, w = advances(s, face, size, ls)
        k = size / font(face)["head"].unitsPerEm
        gap = ls * size
        if fit is not None:
            gap += (math.radians(fit) * radius - w) / (len(glyphs) - 1)
            w = math.radians(fit) * radius
        span = w / radius
        sign = -1 if bottom else 1
        a = math.radians(centre_deg) - sign * span / 2
        pos, uses = 0.0, []
        for name, adv in glyphs:
            theta = a + sign * (pos + adv / 2) / radius
            rot = theta + (math.pi / 2 if not bottom else -math.pi / 2)
            cx, cy = C + radius * math.cos(theta), C + radius * math.sin(theta)
            co, si = math.cos(rot), math.sin(rot)
            # translate(cx cy) rotate(rot) translate(-adv/2 0) scale(k -k)
            e, f = cx - co * adv / 2, cy - si * adv / 2
            gid = self.get(face, name)
            if gid:
                uses.append(f'<use href="#{gid}" transform="matrix({co * k:.4f} {si * k:.4f} {si * k:.4f} {-co * k:.4f} {e:.1f} {f:.1f})"/>')
            pos += adv + gap
        return "".join(uses)


def cap_height(face, size):
    f = font(face)
    return f["OS/2"].sCapHeight * size / f["head"].unitsPerEm


# --------------------------------------------------------------------------------------------
# Layout shared by the README and the page
# --------------------------------------------------------------------------------------------

PAD = 9.0   # space either side of a legend, in units


def layout():
    """Per ring: the legend gaps (top, and the bottom one on tls), and where the sweep starts."""
    lay = {}
    for stage in ORDER:
        tops = [legends(run)[0][stage] for run in RUNS.values()]
        w = max(width(t, "sans", LEGEND["size"], LEGEND["ls"]) for t in tops) + 2 * PAD
        gaps = [(-90.0, math.degrees(w / rm(stage)))]
        if stage == "tls":
            bottoms = [legends(run)[1] for run in RUNS.values()]
            wb = max(width(b, "sans", LEGEND["size"], LEGEND["ls"]) for b in bottoms) + 2 * PAD
            gaps.append((90.0, math.degrees(wb / rm(stage))))
        lay[stage] = dict(gaps=gaps, start=-90.0 + gaps[0][1] / 2)
    return lay


LAY = layout()


def gap_mask(stage, mid):
    """A mask that hides a ring's band under its legends: an annulus with wedges cut out."""
    r = RINGS[stage]
    ro, ri = r["rout"] + 4, r["rin"] - 4
    segs = []
    gaps = sorted(LAY[stage]["gaps"], key=lambda g: g[0])
    # visible arcs run from the end of one gap to the start of the next
    edges = [(g[0] - g[1] / 2, g[0] + g[1] / 2) for g in gaps]
    for i, (_, end) in enumerate(edges):
        nxt = edges[(i + 1) % len(edges)][0] + (360 if i + 1 == len(edges) else 0)
        a0, a1 = math.radians(end), math.radians(nxt)
        large = 1 if (a1 - a0) > math.pi else 0
        p = lambda rad, a: f"{C + rad * math.cos(a):.1f} {C + rad * math.sin(a):.1f}"
        segs.append(f"M{p(ro, a0)} A{ro} {ro} 0 {large} 1 {p(ro, a1)} L{p(ri, a1)} A{ri} {ri} 0 {large} 0 {p(ri, a0)}Z")
    return f'<mask id="{mid}" maskUnits="userSpaceOnUse" x="0" y="0" width="800" height="800"><path d="{"".join(segs)}" fill="#fff"/></mask>'


def bands(prefix):
    """<defs> content: each ring's band as one path plus rotated copies, in a group."""
    o = []
    for stage in ORDER:
        r = RINGS[stage]
        pid = f"{prefix}{stage}"
        step = 360 / (r["n"] * r["m"])
        o.append(f'<g id="{pid}"><path id="{pid}-0" d="{band_path(stage, LAY[stage]["start"])}" pathLength="100"/>')
        o.append("".join(f'<use href="#{pid}-0" transform="rotate({i * step:.3f} 400 400)"/>' for i in range(1, r["n"])))
        o.append("</g>")
    return "".join(o)


def legend_radius(stage, bottom=False):
    ch = cap_height("sans", LEGEND["size"])
    return rm(stage) + (ch / 2 if bottom else -ch / 2)


# --------------------------------------------------------------------------------------------
# The README picture
# --------------------------------------------------------------------------------------------

W, H = 1000, 600
SEAL = dict(x=20, y=24, s=552 / 800)          # the seal's 800 units drawn 552 wide
RUN_NOTE = "certapi 1.96.1 · Windows 11 · 2026-11-05"   # where and when the transcripts were made
CYCLE = 18.0
RUN_AT = {"with": 0.0, "without": 9.0}
# seconds into a run that each ring starts engraving, and how long it takes
CUT = {"url": (0.4, 0.55), "proxy": (0.8, 0.55), "dns": (1.2, 0.55), "tcp": (1.6, 0.55), "tls": (2.0, 1.0), "http": (2.9, 0.5)}
STAMP, HOLD_END, FADE = 3.5, 8.5, 0.45


def pct(t):
    return f"{t / CYCLE * 100:.3f}%"


def readme(theme, still=False):
    """The README picture; still=True gives just the finished seal, unanimated, for the social card."""
    c = THEMES[theme]
    g = Glyphs()
    css = []

    # --- keyframes ---
    def show_between(cls, a, b):
        """Opacity 1 from a to b in the cycle (fading out over FADE), 0 otherwise."""
        css.append(f"@keyframes {cls}{{0%,{pct(a - 0.001)}{{opacity:0}}{pct(a + 0.15)},{pct(b)}{{opacity:1}}{pct(b + FADE)},100%{{opacity:0}}}}")
        return f".{cls}{{animation:{cls} {CYCLE}s linear infinite}}"

    def engrave(cls, a, dur, b):
        css.append(f"@keyframes {cls}{{0%,{pct(a)}{{stroke-dashoffset:100;opacity:1}}{pct(a + dur)},{pct(b)}{{stroke-dashoffset:0;opacity:1}}"
                   f"{pct(b + FADE)}{{stroke-dashoffset:0;opacity:0}}100%{{stroke-dashoffset:100;opacity:0}}}}")
        return f".{cls}{{animation:{cls} {CYCLE}s linear infinite}}"

    rules = []
    base = []   # the still frame: the first run, finished
    for run in RUNS:
        t0 = RUN_AT[run]
        end = t0 + HOLD_END
        for stage in ORDER:
            a, dur = CUT[stage]
            rules.append(engrave(f"c-{run}-{stage}", t0 + a, dur, end))
            rules.append(show_between(f"l-{run}-{stage}", t0 + a, end))
        rules.append(show_between(f"s-{run}", t0 + STAMP, end))
        rules.append(show_between(f"a-{run}", t0 + CUT["tls"][0], end))
        rules.append(show_between(f"p-{run}", t0 + CUT["tls"][0] + 0.5, end))
        rules.append(show_between(f"r-{run}", t0 + STAMP, end))
        rules.append(show_between(f"h-{run}", t0, end))
        vis = 1 if run == "with" else 0
        for cls in [f"c-{run}-{s}" for s in ORDER] + [f"l-{run}-{s}" for s in ORDER] + [f"{k}-{run}" for k in "sapr" + "h"]:
            base.append(f".{cls}{{opacity:{vis}}}")
    css_text = "".join(base) + "@media (prefers-reduced-motion:no-preference){" + "".join(css) + "".join(rules) + "}"

    # --- the seal ---
    sx, sy, s = SEAL["x"], SEAL["y"], SEAL["s"]
    seal = [f'<g transform="translate({sx} {sy}) scale({s:.4f})">']
    seal.append(f'<circle cx="400" cy="400" r="{RIM[0]}" fill="none" stroke="{c["line"]}" stroke-width="2"/>')
    seal.append(f'<circle cx="400" cy="400" r="{RIM[1]}" fill="none" stroke="{c["line"]}" stroke-width="1.4"/>')
    text = microprint()
    reps = max(1, int(2 * math.pi * MICRO_R / width(text, "mono", MICRO["size"])))
    seal.append(f'<g fill="{c["mute"]}">{g.arc(text * reps, MICRO_R, -90, "mono", MICRO["size"], fit=359.2)}</g>')
    for stage in ORDER:
        seal.append(f'<use href="#b-{stage}" mask="url(#m-{stage})" fill="none" stroke="{c["line"]}" stroke-width="1.1"/>')
    for run in RUNS:
        top, bottom = legends(RUNS[run])
        for stage in ORDER:
            col = c["ember"]
            if stage == "tls":
                col = c["amber"] if RUNS[run]["presented"] != "none" else c["mute"]
            seal.append(f'<g class="c-{run}-{stage}" mask="url(#m-{stage})" fill="none" stroke="{col}" stroke-width="1.1" stroke-dasharray="100 100">'
                        f'<use href="#b-{stage}"/></g>')
            lc = c["amber"] if (stage == "tls" and RUNS[run]["presented"] != "none") else c["mute"]
            leg = g.arc(top[stage], legend_radius(stage), -90, "sans", LEGEND["size"], LEGEND["ls"])
            if stage == "tls":
                leg += g.arc(bottom, legend_radius(stage, bottom=True), 90, "sans", LEGEND["size"], LEGEND["ls"], bottom=True)
            seal.append(f'<g class="l-{run}-{stage}" fill="{lc}">{leg}</g>')
        ok = RUNS[run]["code"].startswith("2")
        col = c["mint"] if ok else c["rasp"]
        seal.append(f'<g class="s-{run}" fill="{col}">{g.line(RUNS[run]["code"], 400, 424, "serif", 80, anchor="middle")}'
                    f'{g.line(RUNS[run]["reason"].upper(), 400, 462, "sans", 17, 0.14, anchor="middle")}</g>')
    seal.append("</g>")

    # --- beside it: what the server asked for, what certapi presented, what came back ---
    x, colw = 616, 352
    fit = lambda t, face, size, ls=0.0: min(size, size * colw / width(t, face, size, ls))
    cn = lambda dn: re.match(r"CN=([^,]+)", dn).group(1)
    side = [f'<g fill="{c["mute"]}">{g.line("CERTAPI DOCTOR, A REAL RUN", x, 84, "sans", 14, 0.14)}</g>',
            f'<g fill="{c["ink"]}">{g.line(RUNS["with"]["url"], x, 118, "mono", fit(RUNS["with"]["url"], "mono", 21))}</g>']
    for run in RUNS:
        r = RUNS[run]
        has = r["presented"] != "none"
        flag = "--cert-file badssl.com-client.p12" if has else "no certificate"
        side.append(f'<g class="h-{run}" fill="{c["mute"]}">{g.line(flag, x, 148, "mono4", fit(flag, "mono4", 16))}</g>')
        accepts = cn(r["accepts"])
        side.append(f'<g class="a-{run}">'
                    f'<g fill="{c["mute"]}">{g.line("THE SERVER ACCEPTS CERTIFICATES FROM", x, 212, "sans", 13, 0.14)}</g>'
                    f'<g fill="{c["ink"]}">{g.line(accepts, x, 240, "sans4", fit(accepts, "sans4", 19))}</g></g>')
        pres = cn(r["presented"]) if has else "none"
        side.append(f'<g class="p-{run}">'
                    f'<g fill="{c["mute"]}">{g.line("CLIENT CERTIFICATE PRESENTED", x, 298, "sans", 13, 0.14)}</g>'
                    f'<g fill="{c["amber"] if has else c["mute"]}">{g.line(pres, x, 326, "sans4", fit(pres, "sans4", 19))}</g></g>')
        ok = r["code"].startswith("2")
        answer = f'{r["code"]} {r["reason"]}'
        note = "HTTP/1.1 · all six stages in %d ms" % r["total"]
        side.append(f'<g class="r-{run}">'
                    f'<g fill="{c["mute"]}">{g.line("THE SERVER ANSWERED", x, 384, "sans", 13, 0.14)}</g>'
                    f'<g fill="{c["mint"] if ok else c["rasp"]}">{g.line(answer, x, 434, "serif", fit(answer, "serif", 46))}</g>'
                    f'<g fill="{c["mute"]}">{g.line(note, x, 466, "mono4", fit(note, "mono4", 14.5))}</g></g>')
    side.append(f'<rect x="{x}" y="518" width="{colw}" height="1" fill="{c["line"]}"/>')
    side.append(f'<g fill="{c["mute"]}">{g.line(RUN_NOTE, x, 548, "mono4", fit(RUN_NOTE, "mono4", 14))}</g>')

    defs = ("<defs>" + g.defs() + bands("b-") + "".join(gap_mask(st, f"m-{st}") for st in ORDER) + "</defs>")
    if still:
        box = f'{SEAL["x"]} {SEAL["y"]} {800 * SEAL["s"]:.0f} {800 * SEAL["s"]:.0f}'
        o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{box}" width="800" height="800">', f"<style>{''.join(base)}</style>", defs]
        o += seal
    else:
        o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img">',
             f"<style>{css_text}</style>", defs,
             f'<rect x="0.5" y="0.5" width="{W - 1}" height="{H - 1}" rx="20" fill="{c["bg"]}" stroke="{c["line"]}"/>']
        o += seal + side
    o.append("</svg>")
    return "".join(o)


# --------------------------------------------------------------------------------------------
# The page's seal: same geometry, styled and animated by docs/styles.css
# --------------------------------------------------------------------------------------------

def arc_path(pid, radius, bottom=False):
    a, b = f"{C - radius:.1f} {C:.1f}", f"{C + radius:.1f} {C:.1f}"
    return f'<path id="{pid}" d="M{a} A{radius:.1f} {radius:.1f} 0 0 {0 if bottom else 1} {b}"/>'


def page():
    o = ['<svg class="seal-art" viewBox="0 0 800 800" aria-hidden="true" focusable="false">', "<defs>", bands("pb-")]
    o += [gap_mask(st, f"pm-{st}") for st in ORDER]
    o.append(f'<path id="pa-micro" d="M400 {C + MICRO_R:.1f} A{MICRO_R} {MICRO_R} 0 1 1 400 {C - MICRO_R:.1f} A{MICRO_R} {MICRO_R} 0 1 1 400 {C + MICRO_R:.1f}"/>')
    for stage in ORDER:
        o.append(arc_path(f"pa-{stage}", legend_radius(stage)))
    o.append(arc_path("pa-tls-b", legend_radius("tls", bottom=True), bottom=True))
    o.append("</defs>")
    o.append(f'<circle class="rim" cx="400" cy="400" r="{RIM[0]}"/><circle class="rim rim-in" cx="400" cy="400" r="{RIM[1]}"/>')
    text = microprint()
    full = 2 * math.pi * MICRO_R
    reps = max(1, int(full / width(text, "mono", MICRO["size"])))
    ls = (full - width(text * reps, "mono", MICRO["size"])) / len(text * reps)
    o.append(f'<text class="micro" letter-spacing="{ls:.3f}"><textPath href="#pa-micro">{html.escape(text * reps)}</textPath></text>')
    for stage in ORDER:
        o.append(f'<use class="ghost" href="#pb-{stage}" mask="url(#pm-{stage})"/>')
    for i, stage in enumerate(ORDER):
        o.append(f'<g class="cut cut-{stage}" style="--i:{i}" mask="url(#pm-{stage})"><use href="#pb-{stage}"/></g>')
    for run in RUNS:
        top, bottom = legends(RUNS[run])
        for i, stage in enumerate(ORDER):
            o.append(f'<text class="lg lg-{stage} v-{run}" style="--i:{i}"><textPath href="#pa-{stage}" startOffset="50%">{top[stage]}</textPath></text>')
        o.append(f'<text class="lg lg-tls lg-tls-b v-{run}" style="--i:4"><textPath href="#pa-tls-b" startOffset="50%">{bottom}</textPath></text>')
        r = RUNS[run]
        state = "ok" if r["code"].startswith("2") else "refused"
        o.append(f'<g class="stamp stamp-{state} v-{run}"><text class="code" x="400" y="424">{r["code"]}</text>'
                 f'<text class="reason" x="400" y="462">{r["reason"].upper()}</text></g>')
    o.append("</svg>")
    for run, r in RUNS.items():
        st = r["stages"]
        cert = "the client certificate presented" if r["presented"] != "none" else "no client certificate presented"
        stages = ", ".join(f"{name} {st[name]['ms']} ms" for name in ORDER)
        o.append(f'<p class="sr-only v-{run}">The seal of a certapi doctor run against {r["url"]}, one ring per stage: {stages}; '
                 f'{tls_version(r)} with {cert}; the server answered {st["http"]["detail"]}.</p>')
    return "".join(o)


def write_page():
    path = os.path.join(DOCS, "index.html")
    with open(path, encoding="utf-8") as fh:
        html = fh.read()
    new, n = re.subn(r"(<!-- seal -->).*?(<!-- /seal -->)", lambda m: m.group(1) + page() + m.group(2), html, flags=re.S)
    if n != 1:
        sys.exit("docs/index.html needs exactly one <!-- seal --> ... <!-- /seal --> block")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(new)
    print(f"wrote the seal into {os.path.relpath(path)}")


if __name__ == "__main__":
    for theme in THEMES:
        out = os.path.join(ASSETS, f"seal-{theme}.svg")
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(readme(theme) + "\n")
        print(f"wrote {os.path.relpath(out)} ({os.path.getsize(out) // 1024} KB)")
    out = os.path.join(HERE, "seal-still-dark.svg")
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(readme("dark", still=True) + "\n")
    print(f"wrote {os.path.relpath(out)} (for card.html)")
    if "--readme-only" not in sys.argv:
        write_page()
