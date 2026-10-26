#!/usr/bin/env python3
"""Regenerate the certapi brand assets from BRAND.md values.

usage: python3 docs/tools/make-assets.py <path-to-Real-Fruit-Snacks/.github clone>

Writes:
  docs/assets/cover-dark.svg, cover-light.svg   1200x630 README / og:image cover (+ .png)
  docs/assets/icon.svg                            product mark (document + Ember seal)
  docs/assets/favicon.ico, icon-192.png, icon-512.png, apple-touch-icon.png
  src/ApiTester.App/icon.ico                      the WPF app icon
  docs/assets/social-dark.png                     1280x640 GitHub social preview

All text is outlined with fontTools so no font has to be installed to render the SVGs.
Requires: cairosvg, fonttools, pillow. Fonts come from <dotgithub>/tools/fonts (OFL).
"""
import os, sys, io
import cairosvg
from PIL import Image
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "docs", "assets")
DOTGITHUB = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "..", "rfs-dotgithub")
MONO = TTFont(os.path.join(DOTGITHUB, "tools/fonts/IBMPlexMono-SemiBold.ttf"))
SANS = TTFont(os.path.join(DOTGITHUB, "tools/fonts/IBMPlexSans-Regular.ttf"))

# BRAND.md §1 — verbatim.
T = {
    "dark":  dict(bg="#0C0D10", surface="#13151A", raised="#1A1D24", line="#262A33", text="#E8E6E1",
                  mute="#8A8F99", ember="#FF6A3D", amber="#FFC247", mint="#3DDC97", code="#070809",
                  tile="#1A1D24", dot="#1F2229", glow=True),
    "light": dict(bg="#F6F5F2", surface="#FFFFFF", raised="#ECEAE4", line="#D6D3CB", text="#1A1B1E",
                  mute="#6B6F78", ember="#C43F11", amber="#9A6700", mint="#167A4E", code="#FFFFFF",
                  tile="#1A1B1E", dot="#DDDAD2", glow=False),
}

def path(font, text, x, y, size, ls=0, anchor="start"):
    upm = font["head"].unitsPerEm; gs = font.getGlyphSet(); cmap = font.getBestCmap(); k = size / upm
    names = [cmap.get(ord(ch)) or cmap[ord("?")] for ch in text]
    total = sum(gs[n].width * k for n in names) + ls * (len(names) - 1)
    px = {"start": x, "middle": x - total / 2, "end": x - total}[anchor]
    d = []
    for n in names:
        sp = SVGPathPen(gs); gs[n].draw(TransformPen(sp, (k, 0, 0, -k, px, y))); d.append(sp.getCommands())
        px += gs[n].width * k + ls
    return " ".join(d), total

def width(font, text, size, ls=0):
    return path(font, text, 0, 0, size, ls)[1]

def rfs_tile(x, y, s, c):
    """The RFS Caps Tile from BRAND.md §3, at side s."""
    k = s / 64
    letters, _ = path(MONO, "RFS", x + s / 2, y + 43 * k, 28 * k, -1.5 * k, "middle")
    return (f'<rect x="{x}" y="{y}" width="{s}" height="{s}" rx="{14*k}" fill="{c["tile"]}"/>'
            f'<path d="{letters}" fill="#FFFFFF"/>'
            f'<rect x="{x+14*k}" y="{y+48*k}" width="{36*k}" height="{4*k}" rx="{2*k}" fill="{c["ember"]}"/>')

def mark(x, y, s, c, on_tile=True):
    """certapi product mark: a document with a seal. Drawn on a 64-unit grid."""
    k = s / 64
    g = []
    if on_tile:
        g.append(f'<rect x="{x}" y="{y}" width="{s}" height="{s}" rx="{14*k}" fill="{c["tile"]}"/>')
    # document
    g.append(f'<rect x="{x+16*k}" y="{y+12*k}" width="{28*k}" height="{36*k}" rx="{3*k}" fill="none" stroke="#E8E6E1" stroke-width="{3*k}"/>')
    for i, w in enumerate((14, 14, 9)):
        g.append(f'<rect x="{x+22*k}" y="{y+(20+i*7)*k}" width="{w*k}" height="{3*k}" rx="{1.5*k}" fill="#E8E6E1"/>')
    # seal (Ember) with a check
    cx, cy, r = x + 44 * k, y + 46 * k, 10 * k
    g.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{c["ember"]}"/>')
    g.append(f'<path d="M{cx-4.5*k},{cy} l{3*k},{3*k} l{6*k},{-6*k}" fill="none" stroke="{c["tile"]}" stroke-width="{2.6*k}" stroke-linecap="round" stroke-linejoin="round"/>')
    return "".join(g)

def dots(w, h, c, step=28, r=1.1):
    return "".join(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{c["dot"]}"/>'
                   for x in range(step // 2, w, step) for y in range(step // 2, h, step))

def cover(theme):
    c = T[theme]; W, H = 1200, 630
    glow = (f'<radialGradient id="g" cx="0.30" cy="0.45" r="0.55"><stop offset="0" stop-color="{c["ember"]}" stop-opacity="0.22"/>'
            f'<stop offset="1" stop-color="{c["ember"]}" stop-opacity="0"/></radialGradient>') if c["glow"] else ""
    glow_rect = f'<rect width="{W}" height="{H}" fill="url(#g)"/>' if c["glow"] else ""
    title, tw = path(MONO, "certapi", 96, 318, 112, -5)
    tag, _ = path(SANS, "mTLS API client for the Windows certificate store", 96, 370, 24)
    wm, _ = path(MONO, "github.com/Real-Fruit-Snacks", 96, 556, 17, 0.5)
    # terminal card
    cx, cy, cw, ch = 716, 118, 404, 394
    lines = []
    def L(y, parts):
        px = cx + 24
        for text, col, font, size in parts:
            d, w = path(font, text, px, y, size); lines.append(f'<path d="{d}" fill="{col}"/>'); px += w
    f = MONO
    L(cy + 54, [("$ ", c["ember"], f, 14), ("certapi send ", c["text"], f, 14), ("https://api.internal/orders", c["amber"], f, 14)])
    L(cy + 78, [("    --cert ", c["text"], f, 14), ('"CN=matt"', c["mute"], f, 14)])
    L(cy + 122, [("200 OK", c["mint"], f, 14), ("  ·  48 ms  ·  1.2 kB  ·  application/json", c["mute"], f, 14)])
    L(cy + 150, [("TLS 1.3", c["text"], f, 14), ("  ·  TLS_AES_256_GCM_SHA384", c["mute"], f, 14)])
    L(cy + 174, [("client certificate ", c["mute"], f, 14), ("presented", c["mint"], f, 14)])
    L(cy + 198, [("key never left the store", c["mute"], f, 14)])
    L(cy + 242, [("{", c["mute"], f, 14)])
    L(cy + 266, [('  "order": ', c["amber"], f, 14), ('"A-10422"', c["mint"], f, 14), (",", c["mute"], f, 14)])
    L(cy + 290, [('  "status": ', c["amber"], f, 14), ('"shipped"', c["mint"], f, 14)])
    L(cy + 314, [("}", c["mute"], f, 14)])
    card = (f'<rect x="{cx}" y="{cy}" width="{cw}" height="{ch}" rx="12" fill="{c["code"]}" stroke="{c["line"]}"/>'
            f'<rect x="{cx}" y="{cy}" width="{cw}" height="34" rx="12" fill="{c["raised"]}"/>'
            f'<rect x="{cx}" y="{cy+22}" width="{cw}" height="12" fill="{c["raised"]}"/>'
            f'<line x1="{cx}" y1="{cy+34}" x2="{cx+cw}" y2="{cy+34}" stroke="{c["line"]}"/>'
            + "".join(f'<circle cx="{cx+18+i*16}" cy="{cy+17}" r="4.5" fill="{c["line"]}"/>' for i in range(3))
            + "".join(lines))
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="certapi — mTLS API client for the Windows certificate store">
<defs>{glow}</defs>
<rect width="{W}" height="{H}" fill="{c["bg"]}"/>
{dots(W, H, c)}
{glow_rect}
{rfs_tile(96, 96, 56, c)}
{mark(170, 96, 56, c)}
<path d="{title}" fill="{c["text"]}"/>
<rect x="96" y="334" width="{tw}" height="5" rx="2.5" fill="{c["ember"]}"/>
<path d="{tag}" fill="{c["mute"]}"/>
{card}
<path d="{wm}" fill="{c["mute"]}"/>
<rect x="96" y="500" width="120" height="5" rx="2.5" fill="{c["ember"]}"/>
</svg>'''
    out = os.path.join(ASSETS, f"cover-{theme}.svg"); open(out, "w").write(svg)
    cairosvg.svg2png(bytestring=svg.encode(), write_to=out[:-4] + ".png", output_width=W, output_height=H)
    print("wrote", out)

def icon():
    c = T["dark"]
    svg = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64">{mark(0, 0, 64, c)}</svg>'
    open(os.path.join(ASSETS, "icon.svg"), "w").write(svg)
    def png(size):
        return Image.open(io.BytesIO(cairosvg.svg2png(bytestring=svg.encode(), output_width=size, output_height=size))).convert("RGBA")
    for s, name in ((192, "icon-192.png"), (512, "icon-512.png"), (180, "apple-touch-icon.png")):
        png(s).save(os.path.join(ASSETS, name))
    sizes = [16, 24, 32, 48, 64, 128, 256]
    for target in (os.path.join(ASSETS, "favicon.ico"), os.path.join(ROOT, "src", "ApiTester.App", "icon.ico")):
        png(256).save(target, format="ICO", sizes=[(s, s) for s in sizes], append_images=[png(s) for s in sizes[:-1]])
    print("wrote icon.svg, favicon.ico, src/ApiTester.App/icon.ico")

def social():
    c = T["dark"]; W, H = 1280, 640
    repo, desc = "certapi", "mTLS API client for the Windows certificate store"
    t, _ = path(MONO, repo, 248, 262, 56, -2); d, _ = path(SANS, desc, 248, 318, 26)
    u, _ = path(MONO, "github.com/Real-Fruit-Snacks", 120, 560, 20)
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}">
<rect width="{W}" height="{H}" fill="{c["bg"]}"/>{rfs_tile(120, 200, 96, c)}
<path d="{t}" fill="{c["text"]}"/><path d="{d}" fill="{c["mute"]}"/><path d="{u}" fill="{c["mute"]}"/>
<rect x="120" y="500" width="120" height="6" rx="3" fill="{c["ember"]}"/>{mark(1040, 200, 120, c)}
</svg>'''
    cairosvg.svg2png(bytestring=svg.encode(), write_to=os.path.join(ASSETS, "social-dark.png"), output_width=W, output_height=H)
    print("wrote social-dark.png")

if __name__ == "__main__":
    cover("dark"); cover("light"); icon(); social()
