"""
Generates deterministic test images for DepthView, with a known correct answer for
each one. Pure stdlib + numpy: the PNG writer is hand rolled so the fixtures cannot
inherit a bug from the same kind of imaging library DepthView is meant to distrust.

    python make_fixtures.py

Writes into ./fixtures next to this script.
"""

import os
import struct
import zlib

import numpy as np

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures")
os.makedirs(OUT, exist_ok=True)

W, H = 640, 480


def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def write_png(name, arr, bitdepth, colortype, interlace=0, extra=b""):
    """arr: HxW (grey) or HxWx3 (rgb), dtype uint8 or uint16."""
    h, w = arr.shape[0], arr.shape[1]
    dt = ">u2" if bitdepth == 16 else "u1"

    if interlace == 0:
        rows = arr.astype(dt).reshape(h, -1)
        raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    else:
        xs = [0, 4, 0, 2, 0, 1, 0]
        ys = [0, 0, 4, 0, 2, 0, 1]
        xd = [8, 8, 4, 4, 2, 2, 1]
        yd = [8, 8, 8, 4, 4, 2, 2]
        parts = []
        for p in range(7):
            sub = arr[ys[p]::yd[p], xs[p]::xd[p]]
            if sub.shape[0] == 0 or sub.shape[1] == 0:
                continue
            sub = sub.astype(dt).reshape(sub.shape[0], -1)
            for row in range(sub.shape[0]):
                parts.append(b"\x00" + sub[row].tobytes())
        raw = b"".join(parts)

    ihdr = struct.pack(">IIBBBBB", w, h, bitdepth, colortype, 0, 0, interlace)
    blob = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + extra
            + chunk(b"IDAT", zlib.compress(raw, 6))
            + chunk(b"IEND", b""))

    path = os.path.join(OUT, name)
    with open(path, "wb") as f:
        f.write(blob)
    print("  %-26s %9d bytes" % (name, len(blob)))


def write_pgm(name, arr, maxval):
    path = os.path.join(OUT, name)
    dt = ">u2" if maxval > 255 else "u1"
    with open(path, "wb") as f:
        f.write(("P5\n# DepthView fixture\n%d %d\n%d\n" % (arr.shape[1], arr.shape[0], maxval)).encode())
        f.write(arr.astype(dt).tobytes())
    print("  %-26s %9d bytes" % (name, os.path.getsize(path)))


def write_pfm(name, arr):
    path = os.path.join(OUT, name)
    with open(path, "wb") as f:
        f.write(("Pf\n%d %d\n-1.0\n" % (arr.shape[1], arr.shape[0])).encode())
        f.write(np.flipud(arr).astype("<f4").tobytes())
    print("  %-26s %9d bytes" % (name, os.path.getsize(path)))


x = np.arange(W, dtype=np.int64)[None, :].repeat(H, axis=0)
y = np.arange(H, dtype=np.int64)[:, None].repeat(W, axis=1)
rng = np.random.default_rng(20260830)

print("Generating fixtures in", OUT)

cx, cy = W / 2.0, H / 2.0
radial = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)

# 1. Genuine 16-bit: smooth radial ramp with dither. Tens of thousands of levels, step 1.
g16 = (radial / radial.max() * 63000).astype(np.int64)
g16 = np.clip(g16 + rng.integers(0, 500, size=g16.shape), 0, 65535).astype(np.uint16)
write_png("true16.png", g16, 16, 0)
print("      expect: genuine 16-bit, unique=%d, step 1" % len(np.unique(g16)))

# 2. Imposter: 8-bit data replicated into 16 bits (v * 257).
g8 = (x / (W - 1.0) * 255).astype(np.uint16)
rep = (g8 * 257).astype(np.uint16)
write_png("imposter_x257.png", rep, 16, 0)
print("      expect: IMPOSTER Replicated257, unique=%d, step 257" % len(np.unique(rep)))

# 3. Imposter: 8-bit data shifted into the high byte (v * 256).
shift = (g8 << 8).astype(np.uint16)
write_png("imposter_shift256.png", shift, 16, 0)
print("      expect: IMPOSTER HighByteOnly, unique=%d, step 256" % len(np.unique(shift)))

# 4. Imposter: 10-bit data stretched across 16 bits (uniform ladder, step 64).
q10 = ((x / (W - 1.0) * 1023).astype(np.int64) + (y // 8)) % 1024 * 64
q10 = q10.astype(np.uint16)
write_png("imposter_ladder10bit.png", q10, 16, 0)
print("      expect: IMPOSTER QuantisedLadder, unique=%d, step 64" % len(np.unique(q10)))

# 5. Honest 8-bit map.
write_png("true8.png", g8.astype(np.uint8), 8, 0)
print("      expect: genuine 8-bit, unique=%d, step 1" % len(np.unique(g8)))

# 6. Grey data stored as 16-bit RGB.
write_png("grey_as_rgb16.png", np.dstack([g16, g16, g16]), 16, 2)
print("      expect: genuine 16-bit plus 'grey data stored as RGB'")

# 7. Colour contamination: mostly grey, a fraction of a percent off-neutral.
base = (x / (W - 1.0) * 255).astype(np.uint8)
rgb8 = np.dstack([base, base, base]).copy()
mask = rng.random((H, W)) < 0.004
rgb8[..., 0] = np.where(mask, np.clip(rgb8[..., 0].astype(int) + 30, 0, 255), rgb8[..., 0])
write_png("colour_contaminated.png", rgb8, 8, 2)
print("      expect: %d non-grey pixels" % int(mask.sum()))

# 8. Interlaced 16-bit, to exercise the Adam7 path.
write_png("true16_interlaced.png", g16, 16, 0, interlace=1)
print("      expect: identical numbers to true16.png, interlace Adam7")

# 9. 16-bit PGM.
write_pgm("true16.pgm", g16, 65535)
print("      expect: identical numbers to true16.png")

# 10. 12-bit PGM (MAXVAL 4095).
g12 = (radial / radial.max() * 4000).astype(np.uint16)
write_pgm("true12.pgm", g12, 4095)
print("      expect: 12-bit container, unique=%d" % len(np.unique(g12)))

# 11. Float depth map.
write_pfm("depth.pfm", (radial / radial.max()).astype(np.float32))
print("      expect: float32, no ladder test")

print("\nDone.")
