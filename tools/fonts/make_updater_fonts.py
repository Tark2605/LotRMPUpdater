"""Builds the static TTF fonts embedded in LotRMPUpdater.exe from the site's OFL variable fonts.

Source: LOTR/site/node_modules/@fontsource-variable/{cinzel,cormorant-garamond} (OFL-1.1, latin subset .woff2).
WPF cannot read .woff2 nor pick weights in a variable font, so each weight is instanced to a static TTF:
  Cinzel 400 + 600 (titles), Cormorant Garamond 500 + 700 (body text; 500 reads better on screen at 14 px).
Output: src/Lotr.Updater/Resources/Fonts/*.ttf + OFL.txt (both licences).

Usage:  py -3 tools/fonts/make_updater_fonts.py [--site <LOTR/site>]
Needs:  fontTools (pip install fonttools). The 'brotli' module is optional: without it the .woff2 data is
        decompressed with Node's built-in zlib (node must be on PATH).
"""
import argparse
import os
import subprocess
import sys
import types


def _install_brotli_shim():
    try:
        import brotli  # noqa: F401
        return
    except ImportError:
        pass

    def decompress(data):
        js = "process.stdout.write(require('zlib').brotliDecompressSync(require('fs').readFileSync(0)))"
        return subprocess.run(["node", "-e", js], input=data, capture_output=True, check=True).stdout

    def compress(data, **_):
        raise RuntimeError("brotli compression is not needed here")

    shim = types.ModuleType("brotli")
    shim.decompress = decompress
    shim.compress = compress
    sys.modules["brotli"] = shim


FACES = [
    # (package, woff2 file, family, weight, style name, bold bit, output)
    ("cinzel", "cinzel-latin-wght-normal.woff2", "Cinzel", 400, "Regular", False, "Cinzel-Regular.ttf"),
    ("cinzel", "cinzel-latin-wght-normal.woff2", "Cinzel", 600, "SemiBold", True, "Cinzel-SemiBold.ttf"),
    ("cormorant-garamond", "cormorant-garamond-latin-wght-normal.woff2", "Cormorant Garamond", 500, "Medium", False, "CormorantGaramond-Medium.ttf"),
    ("cormorant-garamond", "cormorant-garamond-latin-wght-normal.woff2", "Cormorant Garamond", 700, "Bold", True, "CormorantGaramond-Bold.ttf"),
]


def set_names(font, family, style, bold):
    name = font["name"]
    legacy_style = "Bold" if bold else "Regular"
    full = family + " " + style
    ps = (family.replace(" ", "") + "-" + style).replace(" ", "")
    keep = {0, 7, 8, 9, 11, 12, 13, 14}
    name.names = [n for n in name.names if n.nameID in keep]
    for nid, value in ((1, family), (2, legacy_style), (3, ps + ";lotr-updater"), (4, full), (5, "Version 1.000; static instance for LotRMPUpdater"),
                       (6, ps), (16, family), (17, style)):
        name.setName(value, nid, 3, 1, 0x409)
    for tag in ("STAT", "fvar", "gvar", "avar", "HVAR", "MVAR"):
        if tag in font:
            del font[tag]


def build(site, out_dir):
    _install_brotli_shim()
    from fontTools.ttLib import TTFont
    from fontTools.varLib.instancer import instantiateVariableFont

    os.makedirs(out_dir, exist_ok=True)
    base = os.path.join(site, "node_modules", "@fontsource-variable")
    for pkg, woff2, family, weight, style, bold, out in FACES:
        src = os.path.join(base, pkg, "files", woff2)
        font = TTFont(src)
        static = instantiateVariableFont(font, {"wght": weight}, inplace=False)
        static.flavor = None
        set_names(static, family, style, bold)
        os2 = static["OS/2"]
        os2.usWeightClass = weight
        os2.fsSelection = (os2.fsSelection & ~0b1100001) | (0b100000 if bold else 0b1000000)
        static["head"].macStyle = 1 if bold else 0
        path = os.path.join(out_dir, out)
        static.save(path)
        print("%-34s %7d bytes  (%s %d from %s)" % (out, os.path.getsize(path), family, weight, woff2))

    with open(os.path.join(out_dir, "OFL.txt"), "w", encoding="utf-8", newline="\n") as f:
        for pkg in ("cinzel", "cormorant-garamond"):
            with open(os.path.join(base, pkg, "LICENSE"), encoding="utf-8") as lic:
                f.write("==== " + pkg + " (fontsource @fontsource-variable/" + pkg + ") ====\n\n")
                f.write(lic.read().strip() + "\n\n")


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.normpath(os.path.join(here, "..", ".."))
    ap = argparse.ArgumentParser()
    ap.add_argument("--site", default=os.path.normpath(os.path.join(repo, "..", "site")))
    ap.add_argument("--out", default=os.path.join(repo, "src", "Lotr.Updater", "Resources", "Fonts"))
    a = ap.parse_args()
    build(a.site, a.out)
