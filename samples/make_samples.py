"""
Generates the sample depth maps in ./samples.

Unlike tests/fixtures, these are committed to the repository. The point is that someone
who has just cloned DepthView, or just downloaded a release binary, can drag a file onto
it in the first ten seconds and see the program do something real - without installing
Python, without generating anything.

Every file here is the SAME artwork, a coin-style relief, encoded eight different ways.
That is the whole idea: the picture on your screen looks identical in all of them, and
DepthView's verdict does not. Load them in order and watch the verdict change while the
image does not.

    python samples/make_samples.py

Pure stdlib + numpy. The PNG writer is hand rolled, so a sample cannot inherit a bug from
the same kind of imaging library DepthView exists to distrust - the same reasoning as
tests/make_fixtures.py, and the reason the level counts quoted in samples/README.md can
be trusted as ground truth rather than as DepthView marking its own homework.
"""

import os
import struct
import zlib

import numpy as np

# The samples live beside this script, in the folder it sits in.
OUT = os.path.dirname(os.path.abspath(__file__))

N = 900          # square, like a coin blank
FULL = 65535


def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def write_png(name, arr, bitdepth, colortype):
    """arr: HxW (grey) or HxWx3 (rgb), dtype uint8 or uint16."""
    h, w = arr.shape[0], arr.shape[1]
    dt = ">u2" if bitdepth == 16 else "u1"
    rows = arr.astype(dt).reshape(h, -1)
    raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    ihdr = struct.pack(">IIBBBBB", w, h, bitdepth, colortype, 0, 0, 0)
    blob = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(blob)
    return len(blob)


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def build():
    """A coin: rim, recessed field, central dome, bead ring, and three chamfered bars.

    Deliberately smooth. Every surface here is a curve rather than a step, which is
    exactly the content that needs more than 256 levels and that terraces visibly when
    it does not get them.
    """
    y, x = np.mgrid[0:N, 0:N].astype(np.float64)
    cx = cy = (N - 1) / 2.0
    dx, dy = (x - cx) / cx, (y - cy) / cy
    r = np.hypot(dx, dy)
    ang = np.arctan2(dy, dx)

    h = np.zeros((N, N))

    inside = r <= 1.0
    h[inside] = 0.18                                   # the recessed field

    # Rim: a rounded bank rising from the field to the edge and rolling over.
    rim = smoothstep(0.80, 0.90, r) * (1.0 - smoothstep(0.955, 1.0, r))
    h += rim * 0.62

    # Bead ring just inside the rim: 72 hemispheres.
    beads = 72
    ba = np.round(ang / (2 * np.pi) * beads) / beads * 2 * np.pi
    bd = np.hypot(dx - 0.745 * np.cos(ba), dy - 0.745 * np.sin(ba))
    h += np.clip(1.0 - (bd / 0.026) ** 2, 0, 1) ** 0.5 * 0.30

    # Central cabochon, offset slightly so the lighting is not symmetric.
    cd = np.hypot(dx + 0.05, dy + 0.16)
    dome = np.clip(1.0 - (cd / 0.42) ** 2, 0, 1)
    h += np.sqrt(dome) * 0.55

    # A softer secondary lobe, to give the shading something to do.
    ld = np.hypot(dx - 0.34, dy - 0.20)
    h += np.clip(1.0 - (ld / 0.24) ** 2, 0, 1) ** 1.5 * 0.14

    # Three chamfered bars along the bottom, like a date panel: flat tops, sloped walls.
    for i, bx in enumerate((-0.42, 0.0, 0.42)):
        u = np.abs(dx - bx) / 0.125
        v = np.abs(dy - 0.50) / 0.070
        bar = (1.0 - smoothstep(0.72, 1.0, np.maximum(u, v)))
        h += bar * (0.20 + 0.03 * i)

    h[~inside] = 0.0
    h *= smoothstep(1.002, 0.985, r)                   # clean fall to the background

    h -= h.min()
    h /= h.max()
    return h


def report(name, size, note):
    print("  %-30s %8.1f KB   %s" % (name, size / 1024.0, note))


def main():
    h = build()
    full16 = np.round(h * FULL).astype(np.uint16)
    eight = np.round(h * 255).astype(np.uint8)

    print("Writing samples to", OUT)

    n = write_png("01-genuine-16bit.png", full16, 16, 0)
    report("01-genuine-16bit.png", n, "%d levels, step 1" % len(np.unique(full16)))

    # Widened by replication: v -> v*257. The classic "saved as 16-bit" that is not.
    x257 = (eight.astype(np.uint16) * 257)
    n = write_png("02-imposter-x257.png", x257, 16, 0)
    report("02-imposter-x257.png", n, "%d levels, step 257" % len(np.unique(x257)))

    # Widened by shifting: v -> v*256, low byte left at zero.
    shift = (eight.astype(np.uint16) * 256)
    n = write_png("03-imposter-high-byte.png", shift, 16, 0)
    report("03-imposter-high-byte.png", n, "%d levels, step 256" % len(np.unique(shift)))

    # A 10-bit ladder stretched across 16 bits: subtler, and the one people miss.
    ten = np.round(h * 1023).astype(np.uint16) * 64
    n = write_png("04-quantised-1024.png", ten, 16, 0)
    report("04-quantised-1024.png", n, "%d levels, step 64" % len(np.unique(ten)))

    # Grey data in three identical channels. LightBurn reads this as 8-bit.
    rgb = np.repeat(eight[:, :, None], 3, axis=2)
    n = write_png("05-grey-stored-as-rgb.png", rgb, 8, 2)
    report("05-grey-stored-as-rgb.png", n, "3x the bytes for no extra information")

    # Genuine 8-bit, honestly declared. Not a fault - just a ceiling.
    n = write_png("06-honest-8bit.png", eight, 8, 0)
    report("06-honest-8bit.png", n, "%d levels, honest" % len(np.unique(eight)))

    # 16-bit, but the data only occupies the middle of the range: wasted passes.
    squeezed = np.round(h * (FULL * 0.34) + FULL * 0.31).astype(np.uint16)
    n = write_png("07-wasted-headroom.png", squeezed, 16, 0)
    report("07-wasted-headroom.png", n, "spans %.0f%% of the range" %
           (100.0 * (squeezed.max() - squeezed.min()) / FULL))

    # A handful of stray coloured pixels: JPEG ringing, a watermark, a stray brush.
    contaminated = rgb.copy()
    rng = np.random.default_rng(20260830)
    ys = rng.integers(N // 4, 3 * N // 4, 900)
    xs = rng.integers(N // 4, 3 * N // 4, 900)
    contaminated[ys, xs, 0] = np.clip(contaminated[ys, xs, 0].astype(int) + 9, 0, 255)
    contaminated[ys, xs, 2] = np.clip(contaminated[ys, xs, 2].astype(int) - 7, 0, 255)
    bad = int(np.sum((contaminated[:, :, 0] != contaminated[:, :, 1]) |
                     (contaminated[:, :, 1] != contaminated[:, :, 2])))
    n = write_png("08-colour-contaminated.png", contaminated, 8, 2)
    report("08-colour-contaminated.png", n, "%d non-grey pixels hiding in it" % bad)

    total = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT)
                if f.endswith(".png"))
    print("\n  %.1f MB total" % (total / 1e6))


if __name__ == "__main__":
    main()
