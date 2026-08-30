"""
Generates sample material textures and a shape-rich demo depth map, so the relief
preview can be exercised without hunting for photographs first.

    python make_textures.py

Writes textures into ./textures and the demo depth map into ./fixtures.
"""

import os
import struct
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
TEX = os.path.join(HERE, "textures")
FIX = os.path.join(HERE, "fixtures")
os.makedirs(TEX, exist_ok=True)
os.makedirs(FIX, exist_ok=True)


def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def write_png(path, arr, bitdepth, colortype):
    h, w = arr.shape[0], arr.shape[1]
    dt = ">u2" if bitdepth == 16 else "u1"
    rows = arr.astype(dt).reshape(h, -1)
    raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    blob = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, bitdepth, colortype, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6))
            + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(blob)
    print("  %-34s %9d bytes" % (os.path.relpath(path, HERE), len(blob)))


def smooth_noise(shape, scale, rng):
    """Value noise by upsampling a coarse random grid with cosine interpolation."""
    h, w = shape
    ch, cw = max(2, int(h / scale)), max(2, int(w / scale))
    g = rng.random((ch, cw))
    yi = np.linspace(0, ch - 1, h)
    xi = np.linspace(0, cw - 1, w)
    y0 = np.floor(yi).astype(int); y1 = np.minimum(y0 + 1, ch - 1)
    x0 = np.floor(xi).astype(int); x1 = np.minimum(x0 + 1, cw - 1)
    fy = (yi - y0)[:, None]; fx = (xi - x0)[None, :]
    fy = fy * fy * (3 - 2 * fy); fx = fx * fx * (3 - 2 * fx)
    a = g[np.ix_(y0, x0)]; b = g[np.ix_(y0, x1)]
    c = g[np.ix_(y1, x0)]; d = g[np.ix_(y1, x1)]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


S = 1024
rng = np.random.default_rng(4242)
y = np.arange(S)[:, None].repeat(S, axis=1).astype(float)
x = np.arange(S)[None, :].repeat(S, axis=0).astype(float)

print("Generating textures in", TEX)

# ---- oak-ish grain: growth rings warped by low frequency noise --------------
warp = smooth_noise((S, S), 90, rng) * 70 + smooth_noise((S, S), 22, rng) * 14
rings = np.sin((y * 0.9 + warp) * 0.14) * 0.5 + 0.5
rings = rings ** 1.9
fibre = smooth_noise((S, S), 3.0, rng)
fibre = np.repeat(fibre.mean(axis=1, keepdims=True) * 0 + fibre, 1, axis=1)
grain = np.clip(rings * 0.78 + fibre * 0.22, 0, 1)

light = np.array([196, 152, 96], float)
dark = np.array([104, 68, 34], float)
oak = (dark[None, None, :] + (light - dark)[None, None, :] * grain[..., None])
oak = np.clip(oak + rng.normal(0, 3.5, oak.shape), 0, 255).astype(np.uint8)
write_png(os.path.join(TEX, "oak-colour.png"), oak, 8, 2)

# Matching surface relief: rings stand slightly proud, so raking light catches them.
oak_h = np.clip(grain * 0.85 + smooth_noise((S, S), 2.0, rng) * 0.15, 0, 1)
write_png(os.path.join(TEX, "oak-surface.png"), (oak_h * 255).astype(np.uint8), 8, 0)

# ---- slate: dark, mottled, faintly layered ---------------------------------
base = smooth_noise((S, S), 40, rng) * 0.55 + smooth_noise((S, S), 9, rng) * 0.45
layers = np.sin((y + smooth_noise((S, S), 120, rng) * 160) * 0.02) * 0.08
sl = np.clip(base * 0.5 + 0.22 + layers, 0, 1)
slate = np.dstack([sl * 96, sl * 102, sl * 112])
slate = np.clip(slate + rng.normal(0, 4, slate.shape), 0, 255).astype(np.uint8)
write_png(os.path.join(TEX, "slate-colour.png"), slate, 8, 2)
write_png(os.path.join(TEX, "slate-surface.png"),
          np.clip(smooth_noise((S, S), 6, rng) * 255, 0, 255).astype(np.uint8), 8, 0)

# ---- hammered metal: overlapping dimples -----------------------------------
hm = np.zeros((S, S))
for _ in range(900):
    cx, cy = rng.random(2) * S
    r = 22 + rng.random() * 26
    d = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)
    mask = d < r
    hm[mask] = np.maximum(hm[mask], np.cos(d[mask] / r * (np.pi / 2)))
hm = np.clip(hm * 0.8 + smooth_noise((S, S), 4, rng) * 0.2, 0, 1)
write_png(os.path.join(TEX, "hammered-surface.png"), (hm * 255).astype(np.uint8), 8, 0)

# ---- demo depth map with recognisable shapes -------------------------------
print("\nGenerating demo depth map in", FIX)
W, H = 900, 700
yy = np.arange(H)[:, None].repeat(W, axis=1).astype(float)
xx = np.arange(W)[None, :].repeat(H, axis=0).astype(float)
z = np.zeros((H, W))

# a dome
d = np.sqrt((xx - 250) ** 2 + (yy - 260) ** 2)
r = 170.0
inside = d < r
z[inside] = np.maximum(z[inside], np.sqrt(np.clip(1 - (d[inside] / r) ** 2, 0, 1)) * 0.85)

# a torus ridge
d2 = np.abs(np.sqrt((xx - 640) ** 2 + (yy - 250) ** 2) - 130)
ridge = np.clip(1 - d2 / 46, 0, 1)
z = np.maximum(z, np.sin(ridge * np.pi / 2) * 0.7)

# three raised bars with chamfered edges, to show flat tops and clean walls
for i, bx in enumerate((170, 420, 670)):
    bw, bh = 110, 130
    inx = np.clip(1 - np.abs(xx - bx) / (bw / 2), 0, 1)
    iny = np.clip(1 - np.abs(yy - 545) / (bh / 2), 0, 1)
    bar = np.clip(np.minimum(inx, iny) * 9, 0, 1) * (0.35 + 0.16 * i)
    z = np.maximum(z, bar)

# a gentle overall swell so nothing sits at exactly zero except the outer border
z = np.clip(z + np.exp(-((xx - W / 2) ** 2 / (2 * 430 ** 2) + (yy - H / 2) ** 2 / (2 * 330 ** 2))) * 0.10, 0, 1)

demo = (z * 65535).astype(np.uint16)
write_png(os.path.join(FIX, "relief_demo.png"), demo, 16, 0)
print("      dome, torus ridge, three chamfered bars, %d unique levels" % len(np.unique(demo)))

print("\nDone.")
