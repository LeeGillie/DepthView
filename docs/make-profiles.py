"""Cross-sections for the tuning guide: two tuned maps of one coin, compared near the edge.

    python docs/make-profiles.py <out.png> "<title>" <blank mm> <depth mm> <span mm> \
        "<label A>" <tuned A.png> "<label B>" <tuned B.png>

Each tuned map is averaged round the centre of its canvas - where the blank's centre is once
DepthView has placed it - and drawn as depth against distance from the centre, over the last
<span mm> to the edge. Depth is the map's own: black is the target depth, white is untouched
surface. It is an average, so lettering crossing a radius is spread round the whole ring; what
survives the average is what goes all the way round - a rim, a trench, a floor.
"""
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

SURFACE, INK, MUTED, GRID = "#fcfcfb", "#1f1f1e", "#6b6a64", "#e4e3dc"
SERIES = ["#2a78d6", "#eb6834"]   # categorical slots 1 and 2, validated for this surface
STYLES = ["-", (0, (5, 2))]       # and a second channel, so colour never carries it alone


def profile(path, blank_mm, depth_mm, span_mm):
    a = np.asarray(Image.open(path)).astype(np.float64)
    if a.ndim == 3:
        a = a[..., 0]
    top = 65535.0 if a.max() > 255 else 255.0
    h, w = a.shape
    cy, cx = (h - 1) / 2, (w - 1) / 2
    ppmm = min(w, h) / blank_mm
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((yy - cy) ** 2 + (xx - cx) ** 2) / ppmm          # mm from the centre
    edge = blank_mm / 2
    keep = (r >= edge - span_mm) & (r < edge)
    bins = np.round(r[keep] * 20).astype(int)                     # 0.05 mm bins
    depth = (1 - a[keep] / top) * depth_mm
    sums = np.bincount(bins, depth)
    counts = np.bincount(bins)
    used = counts > 0
    x = np.nonzero(used)[0] / 20.0
    return x, sums[used] / counts[used]


def main(argv):
    out, title, blank_mm, depth_mm, span_mm = argv[0], argv[1], float(argv[2]), float(argv[3]), float(argv[4])
    pairs = list(zip(argv[5::2], argv[6::2]))
    fig, ax = plt.subplots(figsize=(8.2, 3.4), dpi=110)
    fig.patch.set_facecolor(SURFACE)
    ax.set_facecolor(SURFACE)
    edge = blank_mm / 2
    for i, (label, path) in enumerate(pairs):
        x, d = profile(path, blank_mm, depth_mm, span_mm)
        ax.plot(x, d, color=SERIES[i], linestyle=STYLES[i], linewidth=2, label=label, solid_capstyle="round")
    ax.set_xlim(edge - span_mm, edge)
    ax.set_ylim(depth_mm * 1.05, -depth_mm * 0.08)                 # depth goes down the page
    ax.axhline(0, color=MUTED, linewidth=0.8)
    ax.text(edge - span_mm + 0.05, -depth_mm * 0.035, "blank surface", color=MUTED, fontsize=8.5, va="center")
    ax.set_xlabel("distance from the centre of the blank, mm  (edge of the blank on the right)", color=MUTED, fontsize=9)
    ax.set_ylabel("average depth, mm", color=MUTED, fontsize=9)
    ax.set_title(title, color=INK, fontsize=11, loc="left", pad=10)
    ax.grid(True, color=GRID, linewidth=0.7)
    ax.tick_params(colors=MUTED, labelsize=8.5)
    for s in ax.spines.values():
        s.set_visible(False)
    ax.legend(frameon=False, fontsize=9, labelcolor=INK, loc="upper left", bbox_to_anchor=(0.0, 0.9))
    fig.tight_layout()
    fig.savefig(out, facecolor=SURFACE)


if __name__ == "__main__":
    if len(sys.argv) < 10:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1:])
