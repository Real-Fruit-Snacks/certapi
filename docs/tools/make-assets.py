#!/usr/bin/env python3
"""Sync certapi icons (docs + multi-size WPF app icon) from Real-Fruit-Snacks/rfs-brand
Cyclorama Dawn v2.1.0 canonical marks.

usage: python3 docs/tools/make-assets.py [path/to/rfs-brand]

Writes:
  docs/{icon.svg, favicon.ico, icon-192.png, icon-512.png, icon-maskable-512.png, apple-touch-icon.png}
  src/ApiTester.App/icon.ico   (multi-size 16..256px Windows WPF executable & window icon)

Requires: resvg-py, pillow.
"""
import io, os, shutil, sys
import resvg_py
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DOCS = os.path.join(ROOT, "docs")
ASSETS = os.path.join(DOCS, "assets")
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def main():
    brand_root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(ROOT), "rfs-brand")
    web_dir = os.path.join(brand_root, "brand", "web")
    mark_dir = os.path.join(brand_root, "brand", "mark")
    if not os.path.isdir(web_dir):
        sys.exit(f"rfs-brand not found at {brand_root}")

    for name in ("icon.svg", "favicon.ico", "icon-192.png", "icon-512.png", "icon-maskable-512.png", "apple-touch-icon.png"):
        shutil.copy2(os.path.join(web_dir, name), os.path.join(DOCS, name))

    small_svg = open(os.path.join(mark_dir, "rfs-mark-small-dark.svg"), encoding="utf-8").read()
    large_svg = open(os.path.join(mark_dir, "rfs-mark-dark.svg"), encoding="utf-8").read()
    frames = []
    for sz in ICO_SIZES:
        svg = small_svg if sz <= 48 else large_svg
        raw = bytes(resvg_py.svg_to_bytes(svg_string=svg, width=sz * 4, height=sz * 4, skip_system_fonts=True))
        frames.append(Image.open(io.BytesIO(raw)).convert("RGBA").resize((sz, sz), Image.LANCZOS))
    app_ico = os.path.join(ROOT, "src", "ApiTester.App", "icon.ico")
    frames[-1].save(app_ico, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])
    print(f"synced Cyclorama Dawn web icons and wrote {app_ico} ({len(ICO_SIZES)} sizes)")


if __name__ == "__main__":
    main()
