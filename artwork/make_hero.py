"""
Generates the depth map used as the banner hero: the icon's motif at full size.

A dome that is smooth on the left and terraced on the right - the same surface before and
after it becomes a finite number of layers. Rendering this through DepthView itself means
the banner art is produced by the program it advertises, and it ties the banner back to
the icon without redrawing anything.

    python make_hero.py
    ..\src\DepthView\bin\Debug\net8.0\DepthView.exe --render banner\hero-source.png --orbit 24 44
"""

import os
import struct
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "banner")
os.makedirs(OUT, exist_ok=True)

W, H = 1500, 1050
TERRACES = 13


def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def write_png16(path, arr):
    h, w = arr.shape
    rows = arr.astype(">u2")
    raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    blob = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 16, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6))
            + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(blob)
    print("  %-30s %9d bytes" % (os.path.relpath(path, HERE), len(blob)))


y = np.arange(H)[:, None].repeat(W, axis=1).astype(float)
x = np.arange(W)[None, :].repeat(H, axis=0).astype(float)

cx, cy, R = W * 0.5, H * 0.52, min(W, H) * 0.40
r = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)

dome = np.zeros((H, W))
inside = r < R
dome[inside] = np.sqrt(np.clip(1.0 - (r[inside] / R) ** 2, 0, 1))

# Left half continuous, right half quantised - the icon's split, at size.
stepped = np.clip(np.floor(dome * TERRACES) / (TERRACES - 1), 0, 1)
combined = np.where(x >= cx, stepped, dome)

z = combined * 0.80

# A shallow raised border, so the plate has an edge to catch the light.
m = 46.0
frame = np.minimum.reduce([x, y, W - 1 - x, H - 1 - y])
z = np.maximum(z, np.clip((m - frame) / 14.0, 0, 1) * 0.10)

write_png16(os.path.join(OUT, "hero-source.png"), np.clip(z, 0, 1) * 65535)
print("\nNow render it, for example:")
print(r"  ..\src\DepthView\bin\Debug\net8.0\DepthView.exe --render banner\hero-source.png "
      r"--material ""Polished brass"" --orbit 24 44 --exag 1.5 --size 1500")
