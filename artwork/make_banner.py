"""
Builds the DepthView project banner.

The hero art is rendered by DepthView itself (see make_hero.py, then --render), keyed off
its flat background, and composited under an HTML/CSS layout that is screenshotted with
headless Chromium. Doing the typography in a browser is the only way to get real font
rendering without hand-rolling a text rasteriser.

Requirements beyond the repo:
    pip install numpy pillow playwright
    playwright install chromium
    npm pack @fontsource/inter@5     (extracted next to this script, see FONT_DIR)

Steps:
    python make_hero.py
    ..\\src\\DepthView\\bin\\Debug\\net8.0\\DepthView.exe --render banner\\hero-source.png ^
        --material "Polished brass" --orbit 18 40 --exag 1.6 --size 1600 --zoom 0.74 ^
        --light 305 40 --out banner\\h-brass2.png
    python make_banner.py
"""

import base64
import math
import os
import random
import subprocess
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "banner")
FONT_DIR = os.path.join(HERE, "fonts", "package", "files")

RENDER = os.path.join(OUT, "h-brass2.png")
KEYED = os.path.join(OUT, "hero-keyed.png")
ICON = os.path.join(HERE, "depthview-icon-1024.png")

TAGLINE = ("Depth Maps", "Bit Depth", "Grey Levels", "Imposter Detection",
           "Histograms", "3D Relief", "Cross-Platform")
TAGLINE_SHORT = ("Bit Depth", "Grey Levels", "Imposter Detection", "Histograms", "3D Relief")
CREDIT = 'An <b>Isotope NW</b> tool'


def b64(path, mime):
    with open(path, "rb") as f:
        return "data:%s;base64,%s" % (mime, base64.b64encode(f.read()).decode())


def key_background(src, dst, thresh=34.0):
    """
    The renderer fills the background with one flat colour, so it keys cleanly. Edge pixels
    are un-mixed rather than just made translucent, otherwise the relief carries a grey halo
    onto a much darker banner.
    """
    a = np.asarray(Image.open(src).convert("RGB")).astype(np.float32)
    bg = a[2, 2].copy()
    alpha = np.clip(np.sqrt(((a - bg) ** 2).sum(axis=2)) / thresh, 0, 1)
    al = np.maximum(alpha, 1e-3)[..., None]
    rgb = np.clip((a - bg * (1 - al)) / al, 0, 255)
    rgba = np.dstack([rgb, alpha * 255]).astype(np.uint8)

    ys, xs = np.where(alpha > 0.04)
    rgba = rgba[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    Image.fromarray(rgba, "RGBA").save(dst)
    print("  keyed %s -> %dx%d" % (os.path.basename(src), rgba.shape[1], rgba.shape[0]))


def font_faces():
    faces = []
    for w in (400, 600, 700, 800, 900):
        p = os.path.join(FONT_DIR, "inter-latin-%d-normal.woff2" % w)
        if not os.path.exists(p):
            sys.exit("Missing %s - see the requirements note at the top of this file." % p)
        faces.append("@font-face{font-family:Inter;font-style:normal;font-weight:%d;"
                     "font-display:block;src:url(%s) format('woff2');}"
                     % (w, b64(p, "font/woff2")))
    return "".join(faces)


def histogram(n, bw, bh, comb_every=4):
    """A plausible depth-map distribution, with the imposter comb beneath it."""
    random.seed(7)
    bars = []
    for i in range(n):
        t = i / (n - 1)
        v = (math.exp(-((t - 0.40) ** 2) / 0.05) * 0.92
             + math.exp(-((t - 0.73) ** 2) / 0.013) * 0.42 + 0.05)
        bars.append(min(1.0, v * (0.86 + random.random() * 0.28)))

    w = bw / n
    b = "".join('<rect x="%.2f" y="%.2f" width="%.2f" height="%.2f"/>'
                % (i * w, bh - max(1.6, v * bh), w - 0.95, max(1.6, v * bh))
                for i, v in enumerate(bars))
    c = "".join('<rect x="%.2f" y="0" width="%.2f" height="%d"/>' % (i * w, w - 0.95,
                                                                     10 if bw > 500 else 7)
                for i in range(0, n, comb_every))
    return b, c


def shoot(html, png, w, h):
    from playwright.sync_api import sync_playwright
    path = os.path.join(OUT, "_tmp.html")
    with open(path, "w", encoding="utf-8") as f:
        f.write(html)
    with sync_playwright() as p:
        br = p.chromium.launch()
        pg = br.new_page(viewport={"width": w, "height": h}, device_scale_factor=1)
        pg.goto("file:///" + path.replace("\\", "/"))
        pg.wait_for_timeout(900)
        pg.screenshot(path=png)
        br.close()
    os.remove(path)
    print("  wrote %s  (%dx%d)" % (os.path.relpath(png, HERE), w, h))


BASE_CSS = """
*{margin:0;padding:0;box-sizing:border-box}
html,body{width:%(W)dpx;height:%(H)dpx;overflow:hidden}
body{font-family:Inter,sans-serif;position:relative;
  background:
    radial-gradient(%(g1)dpx %(g2)dpx at 70%% 56%%, rgba(80,180,235,.20), transparent 60%%),
    radial-gradient(%(g3)dpx %(g4)dpx at 9%% 15%%, rgba(63,169,229,.10), transparent 62%%),
    linear-gradient(158deg,#0f1e2d 0%%,#0a121c 54%%,#060a10 100%%);}
.grid{position:absolute;inset:0;
  background-image:
    repeating-linear-gradient(0deg,rgba(125,185,230,.05) 0 1px,transparent 1px %(gs)dpx),
    repeating-linear-gradient(90deg,rgba(125,185,230,.05) 0 1px,transparent 1px %(gs)dpx);
  mask-image:radial-gradient(%(m1)dpx %(m2)dpx at 34%% 44%%,#000 0%%,transparent 80%%);}
.vig{position:absolute;inset:0;box-shadow:inset 0 0 %(v1)dpx %(v2)dpx rgba(0,0,0,.5)}
.word{font-weight:900;color:#fff}
.badge{background:#070c12;color:#fff;font-weight:800;
  border:1px solid rgba(150,190,220,.18);align-self:flex-start}
.rule{position:absolute;background:rgba(255,255,255,.40);height:1px}
.tag{position:absolute;font-weight:600;color:#eef4fa;white-space:nowrap}
.tag b{font-weight:600;color:#4bb2e8}
.blurb{position:absolute;font-weight:400;color:#9cb2c7}
.blurb em{font-style:normal;color:#eaf2f9;font-weight:600}
.mlabel{font-weight:700;color:#5f7a92;text-transform:uppercase}
.mnote{font-weight:400;color:#5f778d}
.foot{position:absolute;font-weight:600;color:#8ea6bd}
.foot span{color:#5f7a92}
.credit{position:absolute;font-weight:400;color:#dce7f1;text-shadow:0 2px 18px rgba(0,0,0,.92)}
.credit b{font-weight:700;color:#4bb2e8}
.footrule{position:absolute;height:1px;
  background:linear-gradient(90deg,rgba(75,178,232,.55),rgba(120,160,190,.17) 55%%,transparent)}
.heroglow{position:absolute;background:radial-gradient(closest-side,
  rgba(90,190,240,.22),transparent 70%%);filter:blur(6px)}
.hero{position:absolute}
.icon{filter:drop-shadow(0 14px 30px rgba(0,0,0,.62))}
.lock{position:absolute;display:flex;align-items:center}
"""


def build_wide(faces, hero, icon):
    bars, comb = histogram(128, 600, 88)
    css = BASE_CSS % dict(W=1920, H=1080, g1=1100, g2=820, g3=820, g4=620, gs=72,
                          m1=1500, m2=950, v1=190, v2=55)
    css += """
.heroglow{right:-10px;top:196px;width:1180px;height:760px}
.hero{right:8px;top:200px;width:1148px;filter:drop-shadow(0 44px 62px rgba(0,0,0,.62))}
.lock{left:96px;top:96px;gap:26px}
.icon{width:142px;height:142px}
.word{font-size:86px;line-height:.84;letter-spacing:-2.4px}
.badge{font-size:29px;padding:13px 17px 15px;margin-top:12px;letter-spacing:.4px}
.rule{left:264px;top:246px;width:742px}
.tag{left:264px;top:263px;font-size:21px}
.tag b{padding:0 6px}
.blurb{left:98px;top:396px;width:610px;font-size:27px;line-height:1.44}
.meter{position:absolute;left:98px;top:636px;width:600px}
.mlabel{font-size:14px;letter-spacing:2.8px}
.mnote{margin-top:12px;font-size:16.5px}
.footrule{left:96px;right:66px;bottom:118px}
.foot{left:98px;bottom:52px;font-size:21px}
.foot span{padding:0 10px}
.credit{right:66px;bottom:50px;font-size:28px}
"""
    body = """
<div class="grid"></div><div class="heroglow"></div>
<img class="hero" src="%(hero)s"><div class="vig"></div>
<div class="lock"><img class="icon" src="%(icon)s"><div class="word">DEPTHVIEW</div>
  <div class="badge">v1.0</div></div>
<div class="rule"></div><div class="tag">%(tag)s</div>
<div class="blurb">Tells you what a depth map <em>actually contains</em> &mdash; not what its
header claims. Catches 8-bit data hiding in a 16-bit file before it reaches the laser.</div>
<div class="meter"><div class="mlabel">Grey-level histogram</div>
  <svg width="600" height="116" viewBox="0 0 600 116" style="margin-top:13px">
  <defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#93C9F2"/><stop offset="1" stop-color="#39699A"/>
  </linearGradient></defs>
  <g fill="url(#bg)">%(bars)s</g>
  <g transform="translate(0,100)" fill="#8BD49C">%(comb)s</g></svg>
  <div class="mnote">Evenly spaced teeth below the plot &mdash; the signature of an imposter.</div></div>
<div class="footrule"></div>
<div class="foot">Windows<span>&bull;</span>macOS<span>&bull;</span>Linux<span>&bull;</span>
  one self-contained executable, nothing to install</div>
<div class="credit">%(credit)s</div>
""" % dict(hero=hero, icon=icon, bars=bars, comb=comb, credit=CREDIT,
           tag='<b>&bull;</b>'.join(TAGLINE))
    return "<!doctype html><html><head><meta charset='utf-8'><style>%s%s</style></head>" \
           "<body>%s</body></html>" % (faces, css, body)


def build_compact(faces, hero, icon):
    bars, comb = histogram(96, 400, 52)
    css = BASE_CSS % dict(W=1280, H=640, g1=760, g2=560, g3=560, g4=420, gs=48,
                          m1=980, m2=620, v1=130, v2=38)
    css += """
.heroglow{right:-16px;top:108px;width:760px;height:470px}
.hero{right:-8px;top:118px;width:734px;filter:drop-shadow(0 28px 42px rgba(0,0,0,.62))}
.lock{left:58px;top:56px;gap:18px}
.icon{width:94px;height:94px}
.word{font-size:58px;line-height:.84;letter-spacing:-1.7px}
.badge{font-size:19px;padding:8px 11px 10px;margin-top:8px}
.rule{left:170px;top:156px;width:474px}
.tag{left:170px;top:168px;font-size:14.5px}
.tag b{padding:0 5px}
.blurb{left:60px;top:246px;width:420px;font-size:19px;line-height:1.45}
.meter{position:absolute;left:60px;top:392px;width:400px}
.mlabel{font-size:10.5px;letter-spacing:2px}
.footrule{left:58px;right:44px;bottom:74px}
.foot{left:60px;bottom:34px;font-size:14px}
.foot span{padding:0 7px}
.credit{right:44px;bottom:33px;font-size:19px}
"""
    body = """
<div class="grid"></div><div class="heroglow"></div>
<img class="hero" src="%(hero)s"><div class="vig"></div>
<div class="lock"><img class="icon" src="%(icon)s"><div class="word">DEPTHVIEW</div>
  <div class="badge">v1.0</div></div>
<div class="rule"></div><div class="tag">%(tag)s</div>
<div class="blurb">Tells you what a depth map <em>actually contains</em> &mdash; not what its
header claims.</div>
<div class="meter"><div class="mlabel">Grey-level histogram</div>
  <svg width="400" height="70" viewBox="0 0 400 70" style="margin-top:9px">
  <defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#93C9F2"/><stop offset="1" stop-color="#39699A"/>
  </linearGradient></defs>
  <g fill="url(#bg)">%(bars)s</g>
  <g transform="translate(0,60)" fill="#8BD49C">%(comb)s</g></svg></div>
<div class="footrule"></div>
<div class="foot">Windows<span>&bull;</span>macOS<span>&bull;</span>Linux</div>
<div class="credit">%(credit)s</div>
""" % dict(hero=hero, icon=icon, bars=bars, comb=comb, credit=CREDIT,
           tag='<b>&bull;</b>'.join(TAGLINE_SHORT))
    return "<!doctype html><html><head><meta charset='utf-8'><style>%s%s</style></head>" \
           "<body>%s</body></html>" % (faces, css, body)


if __name__ == "__main__":
    print("Building DepthView banner")
    if not os.path.exists(RENDER):
        sys.exit("Missing %s - render the hero first (see the note at the top)." % RENDER)

    key_background(RENDER, KEYED)
    faces = font_faces()
    hero = b64(KEYED, "image/png")
    icon = b64(ICON, "image/png")

    shoot(build_wide(faces, hero, icon),
          os.path.join(OUT, "depthview-banner-1920x1080.png"), 1920, 1080)
    shoot(build_compact(faces, hero, icon),
          os.path.join(OUT, "depthview-banner-1280x640.png"), 1280, 640)
    print("Done.")
