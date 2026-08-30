"""
Generates the DepthView application icon.

The icon is a terraced dome sitting on a dark plate, with a black-to-white grey ramp
along the bottom. That says the two things the program is about: a depth map is a grey
ramp, and a real engraving of one comes out in discrete steps. It is shaded with the
same lighting model the relief preview uses - height-field normals, Blinn-Phong, and
ambient occlusion sampled from the height field - so the icon and the app agree.

    python make_icon.py

Writes PNGs and a multi-resolution .ico into this folder, and copies what the build
needs into ../src/DepthView/Assets.
"""

import os
import shutil
import struct
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.normpath(os.path.join(HERE, "..", "src", "DepthView", "Assets"))
os.makedirs(ASSETS, exist_ok=True)

WORK = 1024          # render resolution, downsampled to each icon size
TERRACES = 6         # depth steps in the dome


# --------------------------------------------------------------------------- png

def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def png_bytes(rgba):
    """rgba: HxWx4 uint8 -> PNG bytes with alpha."""
    h, w = rgba.shape[0], rgba.shape[1]
    rows = rgba.reshape(h, -1)
    raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def write_png(path, rgba):
    data = png_bytes(rgba)
    with open(path, "wb") as f:
        f.write(data)
    print("  %-38s %8d bytes" % (os.path.relpath(path, HERE), len(data)))
    return data


def write_ico(path, images):
    """images: list of (size, png_bytes). PNG-compressed entries, supported since Vista."""
    n = len(images)
    out = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    for size, blob in images:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for _, blob in images:
        out += blob
    with open(path, "wb") as f:
        f.write(out)
    print("  %-38s %8d bytes  (%s)" % (os.path.relpath(path, HERE), len(out),
                                       ", ".join(str(s) for s, _ in images)))


# --------------------------------------------------------------------------- shapes

def rounded_square_sdf(x, y, cx, cy, half, radius):
    """Signed distance to a rounded square. Negative inside."""
    dx = np.abs(x - cx) - (half - radius)
    dy = np.abs(y - cy) - (half - radius)
    dx = np.maximum(dx, 0.0)
    dy = np.maximum(dy, 0.0)
    outside = np.sqrt(dx ** 2 + dy ** 2)
    inside = np.minimum(np.maximum(np.abs(x - cx) - (half - radius),
                                   np.abs(y - cy) - (half - radius)), 0.0)
    return outside + inside - radius


def build_scene(n):
    """Returns (height 0..1, albedo HxWx3, alpha 0..1)."""
    y, x = np.mgrid[0:n, 0:n].astype(float)
    s = n / 1024.0

    # ---- tile silhouette ----
    sdf = rounded_square_sdf(x, y, (n - 1) / 2, (n - 1) / 2, half=n / 2, radius=190 * s)
    alpha = np.clip(-sdf / (2.0 * s), 0, 1)

    height = np.zeros((n, n))
    albedo = np.zeros((n, n, 3))

    # ---- dark plate ----
    plate = np.array([0.018, 0.023, 0.032])
    albedo[:] = plate
    gloss = np.full((n, n), 0.14)   # the plate is matte; the dome and ramp are not

    # A faint vertical lift so the plate is not dead flat.
    lift = np.clip(1.0 - y / (n * 1.5), 0, 1)[..., None]
    albedo += lift * 0.016

    # ---- terraced dome ----
    cx, cy, R = (n - 1) / 2, 452 * s, 322 * s
    r = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)
    inside = r < R
    dome = np.zeros((n, n))
    dome[inside] = np.sqrt(np.clip(1.0 - (r[inside] / R) ** 2, 0, 1))

    # Left half continuous, right half quantised into terraces. That split is the whole
    # point of the program - the same depth map, before and after it becomes a finite
    # number of layers - and it stops a symmetrical ringed dome reading as a stack of tins.
    stepped = np.clip(np.floor(dome * TERRACES) / (TERRACES - 1), 0, 1)
    combined = np.where(x >= cx, stepped, dome)
    height = np.maximum(height, combined * 0.78)

    steel = np.array([0.60, 0.67, 0.79])
    dome_mask = (r < R + 1.0)[..., None]
    edge2 = np.clip((R - r) / (2.0 * s), 0, 1)
    edge = edge2[..., None]
    albedo = albedo * (1 - edge) + steel * edge
    gloss = gloss * (1 - edge2) + 1.0 * edge2

    # ---- grey ramp bar along the bottom ----
    bx0, bx1 = 236 * s, (n - 236 * s)
    by0, by1 = 812 * s, 872 * s
    bsdf = rounded_square_sdf(x, np.clip(y, by0, by1), (bx0 + bx1) / 2, (by0 + by1) / 2,
                              half=(bx1 - bx0) / 2, radius=30 * s)
    bar = np.clip(-bsdf / (2.0 * s), 0, 1) * ((y > by0 - 1) & (y < by1 + 1))
    height = np.maximum(height, bar * 0.30)

    ramp = np.clip((x - bx0) / (bx1 - bx0), 0, 1)
    ramp_rgb = np.dstack([ramp, ramp, ramp]) ** 2.2 * 0.92 + 0.012
    b3 = bar[..., None]
    albedo = albedo * (1 - b3) + ramp_rgb * b3
    gloss = gloss * (1 - bar) + 0.55 * bar

    # ---- soften the height field so the terraces have shaded risers ----
    height = smooth(height, 1.0 * s)

    return height, np.clip(albedo, 0, 1), alpha, gloss


def smooth(a, sigma):
    """Small separable box-blur stack approximating a gaussian."""
    if sigma < 0.4:
        return a
    k = max(1, int(round(sigma)))
    out = a
    for _ in range(3):
        pad = np.pad(out, k, mode="edge")
        acc = np.zeros_like(out)
        for d in range(-k, k + 1):
            acc += pad[k + d:k + d + out.shape[0], k:k + out.shape[1]]
        out = acc / (2 * k + 1)
        pad = np.pad(out, k, mode="edge")
        acc = np.zeros_like(out)
        for d in range(-k, k + 1):
            acc += pad[k:k + out.shape[0], k + d:k + d + out.shape[1]]
        out = acc / (2 * k + 1)
    return out


# --------------------------------------------------------------------------- shading

def shade(height, albedo, gloss, n):
    s = n / 1024.0
    zk = 44.0 / s          # slope scale: enough that the dome reads as a dome, not a disc

    gx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    gy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5

    nx, ny = -gx * zk, -gy * zk
    nl = np.sqrt(nx ** 2 + ny ** 2 + 1.0)
    nx, ny, nz = nx / nl, ny / nl, 1.0 / nl

    az, el = np.radians(315.0), np.radians(46.0)
    lx, ly, lz = np.cos(el) * np.sin(az), -np.cos(el) * np.cos(az), np.sin(el)
    hx, hy, hz = lx, ly, lz + 1.0
    hn = np.sqrt(hx * hx + hy * hy + hz * hz)
    hx, hy, hz = hx / hn, hy / hn, hz / hn

    ndl = np.clip(nx * lx + ny * ly + nz * lz, 0, 1)
    ndh = np.clip(nx * hx + ny * hy + nz * hz, 0, 1)

    # Ambient occlusion straight off the height field, same idea as the preview.
    occ = np.zeros_like(height)
    taken = 0
    for ang in np.arange(0, 2 * np.pi, np.pi / 4):
        for rr in (7 * s, 20 * s):
            dx, dy = int(round(np.cos(ang) * rr)), int(round(np.sin(ang) * rr))
            dh = np.roll(np.roll(height, -dy, axis=0), -dx, axis=1) - height
            occ += np.clip(dh * zk / max(rr, 1.0), 0, None)
            taken += 1
    occ /= taken
    ao = 1.0 / (1.0 + occ * 7.0)

    spec = ndh ** 46.0 * 0.55

    # Directional environment, so flat areas are not dead and tilts read clearly.
    ry = 2.0 * nz * ny
    rz = 2.0 * nz * nz - 1.0
    t = np.clip((rz * 0.55 - ry * 0.70) * 0.5 + 0.44, 0, 1)
    t = t * t * (3 - 2 * t)
    env = (0.035 + (0.88 - 0.035) * t)[..., None] * np.array([0.94, 0.97, 1.06])

    # Fresnel-weight the environment. Added flat it pours untinted grey over everything and
    # the dark plate comes out mid-grey - the same mistake the relief renderer started with.
    fres = (0.035 + 0.965 * (1.0 - nz) ** 5)[..., None]

    g = gloss[..., None]
    lit = (0.16 * ao + ndl * 0.90 * (0.34 + 0.66 * ao))[..., None]
    col = albedo * lit + (env * fres * 0.85 * ao[..., None] + spec[..., None] * 0.70) * g

    lum = col @ np.array([0.2126, 0.7152, 0.0722])
    col = col / (1.0 + lum[..., None] * 0.22)
    return np.clip(col, 0, 1) ** (1 / 2.2)


def add_border(rgb, alpha, n):
    """A thin rim so the icon holds an edge on both light and dark taskbars."""
    s = n / 1024.0
    y, x = np.mgrid[0:n, 0:n].astype(float)
    sdf = rounded_square_sdf(x, y, (n - 1) / 2, (n - 1) / 2, half=n / 2, radius=190 * s)
    rim = np.clip(1.0 - np.abs(sdf + 3.0 * s) / (3.0 * s), 0, 1)[..., None]
    rim_col = np.array([0.30, 0.35, 0.42])
    return rgb * (1 - rim * 0.9) + rim_col * rim * 0.9


def _resample_axis0(a, size):
    """Exact area average along axis 0, via an integral image, for any scale factor."""
    n = a.shape[0]
    c = np.concatenate([np.zeros((1,) + a.shape[1:]), np.cumsum(a, axis=0)], axis=0)
    edges = np.linspace(0, n, size + 1)
    lo = np.floor(edges).astype(int)
    hi = np.minimum(lo + 1, n)
    frac = (edges - lo).reshape((-1,) + (1,) * (a.ndim - 1))
    vals = c[lo] + (c[hi] - c[lo]) * frac
    width = (edges[1:] - edges[:-1]).reshape((-1,) + (1,) * (a.ndim - 1))
    return (vals[1:] - vals[:-1]) / width


def box_down(rgba, size):
    """Area-average downsample. 1024 does not divide evenly by 48 or 24, so this has to
    handle fractional boxes rather than reshaping into equal blocks."""
    a = _resample_axis0(rgba.astype(np.float64), size)
    a = _resample_axis0(np.swapaxes(a, 0, 1), size)
    return np.clip(np.swapaxes(a, 0, 1) + 0.5, 0, 255).astype(np.uint8)


# --------------------------------------------------------------------------- main

print("Building DepthView icon")

height, albedo, alpha, gloss = build_scene(WORK)
rgb = shade(height, albedo, gloss, WORK)
rgb = add_border(rgb, alpha, WORK)

rgba = np.dstack([np.clip(rgb * 255, 0, 255), np.clip(alpha * 255, 0, 255)]).astype(np.uint8)

# Premultiply-safe: force fully transparent pixels to the edge colour so downsampling
# does not drag black halos into the rounded corners.
edge = rgba[..., :3].astype(float)
faded = alpha[..., None] < 0.02
edge[np.repeat(faded, 3, axis=2)] = 30
rgba[..., :3] = edge.astype(np.uint8)

write_png(os.path.join(HERE, "depthview-icon-1024.png"), rgba)

sizes = [256, 128, 64, 48, 32, 24, 16]
blobs = []
for sz in sizes:
    small = box_down(rgba, sz)
    path = os.path.join(HERE, "depthview-icon-%d.png" % sz)
    data = write_png(path, small)
    blobs.append((sz, data))

write_ico(os.path.join(HERE, "depthview.ico"), blobs)

shutil.copyfile(os.path.join(HERE, "depthview-icon-256.png"),
                os.path.join(ASSETS, "depthview-icon-256.png"))
shutil.copyfile(os.path.join(HERE, "depthview.ico"),
                os.path.join(ASSETS, "depthview.ico"))
print("\nCopied depthview-icon-256.png and depthview.ico into src/DepthView/Assets")
print("Done.")
