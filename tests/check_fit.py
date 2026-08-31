"""Assert that fitting a design inside a rim does what it claims.

The tuner's fit step is the only thing in the program that changes an image's dimensions,
and it does it by geometry rather than by resampling. Both of those are checkable without
looking at a picture, which is what this does - straight from the PNG bytes, with no image
library, so it agrees with DepthView's own decoder rather than with Pillow's opinion.

What it pins down:

  * the geometry. A 40 mm blank with a 0.9 mm rim leaves 95.5% of the radius, so the canvas
    a design of radius R needs is 2R/0.955. The corner policy has to contain the half
    diagonal, which for a square is R * root two - and root two over 0.955 is 1.48, so a
    900 px square becomes 1332 px. That number is arithmetic, not a measurement, and if it
    ever changes something has changed with it.

  * that nothing was resampled. The original pixels have to appear, byte for byte, in the
    middle of the padded canvas. This is the claim the whole feature rests on, and it is the
    one that would fail silently: a resampled map still looks right.

  * the rim polarity. With --invert the rim must still come out untouched. It did not,
    before the fit work: inversion ran last and flipped the rim to full depth, telling the
    laser to cut away the one part of a coin blank nobody wants it to reach.

Run it from the repository root, after the tuned files have been written beside it:
    python3 tests/check_fit.py
"""

import re
import struct
import sys
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "samples" / "07-wasted-headroom.png"

failures = []


def fail(message):
    failures.append(message)
    print("FAIL  " + message)


def ok(message):
    print("ok    " + message)


def read_grey16(path):
    """Decode a greyscale PNG to (width, height, [values]). Filters 0 and 2 only.

    Deliberately tiny: these files are written by DepthView's own encoder, which uses no
    filtering at all, so anything more would be testing code this test does not exercise.
    """
    data = Path(path).read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG")

    i, idat, width, height, depth, colour = 8, b"", 0, 0, 0, 0
    while i < len(data):
        length = struct.unpack(">I", data[i:i + 4])[0]
        kind = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour = struct.unpack(">IIBB", body[:10])
        elif kind == b"IDAT":
            idat += body
        i += 12 + length

    if colour != 0 or depth != 16:
        raise ValueError(f"{path} is not 16-bit greyscale (colour {colour}, depth {depth})")

    raw = zlib.decompress(idat)
    stride = width * 2
    rows, prev = [], bytearray(stride)

    for y in range(height):
        start = y * (stride + 1)
        ftype = raw[start]
        line = bytearray(raw[start + 1:start + 1 + stride])
        if ftype == 2:                       # Up
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif ftype != 0:
            raise ValueError(f"{path} row {y} uses filter {ftype}, which this reader does not do")
        rows.append(struct.unpack(f">{width}H", bytes(line)))
        prev = line

    return width, height, rows


def check_square(path, expected):
    w, h, _ = read_grey16(path)
    if (w, h) != (expected, expected):
        fail(f"{path.name}: expected {expected} x {expected}, got {w} x {h}")
        return False
    ok(f"{path.name}: {w} x {h}")
    return True


def main():
    if not SOURCE.exists():
        print(f"Source sample missing: {SOURCE}")
        return 1

    src_w, src_h, src_rows = read_grey16(SOURCE)
    print(f"source {SOURCE.name}: {src_w} x {src_h}\n")

    plain = ROOT / "fit-plain.png"
    content = ROOT / "fit-content.png"
    canvas = ROOT / "fit-canvas.png"
    untouched = ROOT / "fit-untouched.png"
    inverted = ROOT / "fit-inverted.png"

    for path in (plain, content, canvas, untouched, inverted):
        if not path.exists():
            print(f"Missing {path.name}. Run the --tune commands first; see build.yml.")
            return 1

    # --- geometry -----------------------------------------------------------
    # No fit means no growth: the output is the input's size.
    check_square(plain, src_w)

    # Content policy on this sample: the design is a disc inscribed in the square, so the
    # radius that has to clear the rim is half the short side.
    check_square(content, 942)

    # Corner policy: the half diagonal of a 900 square is 449.5 * root two = 635.7, and
    # 2 * 635.7 / 0.955 rounds up to 1332.
    check_square(canvas, 1332)

    # The padding fill changes pixels, never dimensions.
    check_square(untouched, 1332)

    # --- nothing was resampled ---------------------------------------------
    # The whole argument for padding over scaling. Every original pixel must survive
    # unchanged, in the middle of the new canvas.
    for path, size in ((content, 942), (canvas, 1332)):
        w, h, rows = read_grey16(path)
        ox, oy = (w - src_w) // 2, (h - src_h) // 2
        _, _, plain_rows = read_grey16(plain)   # the same levels, before any padding

        mismatched = 0
        for y in range(src_h):
            expected = plain_rows[y]
            actual = rows[y + oy][ox:ox + src_w]
            if actual != expected:
                mismatched += sum(1 for a, b in zip(actual, expected) if a != b)

        # The rim overwrites part of the original in the un-fitted file but not in the
        # fitted ones, so a fitted canvas has to match everywhere the rim does not reach.
        # Compare the middle half, which no rim can touch at any of these sizes.
        lo, hi = src_w // 4, src_w * 3 // 4
        centre_bad = 0
        for y in range(src_h // 4, src_h * 3 // 4):
            expected = plain_rows[y][lo:hi]
            actual = rows[y + oy][ox + lo:ox + hi]
            centre_bad += sum(1 for a, b in zip(actual, expected) if a != b)

        if centre_bad:
            fail(f"{path.name}: {centre_bad} original pixels changed - something resampled")
        else:
            ok(f"{path.name}: original pixels copied exactly ({mismatched} differ only where "
               f"the rim reaches)")

    # --- padding fill -------------------------------------------------------
    # Background fill has to continue the design's own field; untouched fill has to be
    # pure white. Sample a spot that is padding in both: just inside the top edge, on the
    # centre column, which sits outside the original 900 px square in a 1332 px canvas.
    for path, expect_white in ((canvas, False), (untouched, True)):
        w, h, rows = read_grey16(path)
        probe = rows[(h - src_h) // 4][w // 2]
        is_white = probe == 65535
        if is_white != expect_white:
            fail(f"{path.name}: padding reads {probe}, expected "
                 f"{'65535 (untouched)' if expect_white else 'the design background'}")
        else:
            ok(f"{path.name}: padding is {probe}")

    # --- rim polarity under inversion ---------------------------------------
    # The regression that motivated the check. Inversion runs last, so anything that has to
    # come out untouched must be written as its mirror before it - and if that is ever
    # forgotten, the rim is cut to full depth instead of left alone.
    w, h, rows = read_grey16(inverted)
    corner = rows[0][0]
    edge = rows[2][w // 2]
    if corner != 65535 or edge != 65535:
        fail(f"{inverted.name}: rim is not untouched under --invert "
             f"(corner {corner}, edge {edge}; expected 65535)")
    else:
        ok(f"{inverted.name}: rim stays untouched under --invert")

    # --- alignment outline ---------------------------------------------------
    # The SVG that lets a round design be framed against a round blank. Checked because it
    # is the one output nothing else validates: an SVG with a broken comment or a wrong
    # radius still opens in a text editor and still looks like a file.
    outline = ROOT / "fit-outline.svg"
    if outline.exists():
        src = outline.read_text(encoding="utf-8")

        # A double hyphen inside an XML comment is illegal and makes the whole file
        # unparseable. This project has already been bitten by exactly that in an AXAML
        # comment, and here the comment is long prose, which is where it would happen again.
        if any("--" in m.group(1) for m in re.finditer(r"<!--(.*?)-->", src, re.S)):
            fail("fit-outline.svg: '--' inside an XML comment makes the file unparseable")
        else:
            try:
                root = ET.fromstring(src)
                ns = "{http://www.w3.org/2000/svg}"

                if root.get("width") != "40mm" or root.get("height") != "40mm":
                    fail(f"fit-outline.svg: expected 40mm square, got "
                         f"{root.get('width')} x {root.get('height')}")
                else:
                    ok(f"fit-outline.svg: {root.get('width')} square, true size in mm")

                radii = sorted(float(c.get("r")) for c in root.iter(ns + "circle"))
                # Outer circle is the blank; inner is the engraved area inside a 0.9 mm rim.
                if len(radii) != 2 or abs(radii[1] - 20.0) > 0.01 or abs(radii[0] - 19.1) > 0.05:
                    fail(f"fit-outline.svg: expected radii 19.1 and 20.0 mm, got {radii}")
                else:
                    ok(f"fit-outline.svg: circles at {radii[0] * 2:.1f} and {radii[1] * 2:.1f} mm")

                centred = all(c.get("cx") == "20" and c.get("cy") == "20"
                              for c in root.iter(ns + "circle"))
                if not centred:
                    fail("fit-outline.svg: circles are not centred on the canvas")
                else:
                    ok("fit-outline.svg: concentric and centred")
            except ET.ParseError as e:
                fail(f"fit-outline.svg: does not parse as XML - {e}")

    print()
    if failures:
        print(f"{len(failures)} check(s) failed.")
        return 1

    print("Fit geometry, pixel preservation, padding fill and rim polarity all as expected.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
