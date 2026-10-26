#!/usr/bin/env python3
"""Regenerate the certapi product icons from BRAND.md values.

usage: python3 docs/tools/make-assets.py

Writes:
  docs/assets/icon.svg                            product mark (document + Ember seal)
  docs/assets/favicon.ico, icon-192.png, icon-512.png, apple-touch-icon.png
  src/ApiTester.App/icon.ico                      the WPF app icon

The README cover, og:image and social preview are not made here: they come from
docs/assets/cover.toml through Real-Fruit-Snacks/.github tools/make-cover.py.
Requires: cairosvg, pillow.
"""
import os, sys, io
import cairosvg
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "docs", "assets")

# BRAND.md §1 — verbatim.
T = {
    "dark":  dict(bg="#0C0D10", surface="#13151A", raised="#1A1D24", line="#262A33", text="#E8E6E1",
                  mute="#8A8F99", ember="#FF6A3D", amber="#FFC247", mint="#3DDC97", code="#070809",
                  tile="#1A1D24", dot="#1F2229", glow=True),
    "light": dict(bg="#F6F5F2", surface="#FFFFFF", raised="#ECEAE4", line="#D6D3CB", text="#1A1B1E",
                  mute="#6B6F78", ember="#C43F11", amber="#9A6700", mint="#167A4E", code="#FFFFFF",
                  tile="#1A1B1E", dot="#DDDAD2", glow=False),
}

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

if __name__ == "__main__":
    icon()
