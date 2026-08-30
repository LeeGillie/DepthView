#!/usr/bin/env python3
"""Assert that DepthView's summary report says exactly what it should for every fixture.

The fixtures are built by make_fixtures.py and make_textures.py with answers that are
known by construction: the generator decides how many distinct grey levels go into each
image, so the correct output is not a matter of opinion. This script is the other half
of that - it pins those answers down so a change to a decoder or to the classifier cannot
quietly alter them.

Usage:
    python tests/check_report.py tests/fixtures/depthview-report.txt

Produce the input with:
    DepthView --report tests/fixtures --summary

Exits 0 when every expectation holds, 1 otherwise, printing every mismatch rather than
stopping at the first - a decoder regression usually breaks several rows at once, and the
pattern of which ones is the useful part.
"""

import re
import sys

# filename -> (verdict, bit depth, unique grey levels, level step, non-grey pixels)
#
# 'step' is the greatest common divisor of the gaps between the levels that occur. A
# genuine map has step 1. A step of 257 means every value is v*257, which is what you get
# when an 8-bit map is widened to 16 bits by replicating the byte; 256 means the low byte
# was left at zero; 64 means a 10-bit ladder stretched over a 16-bit range.
EXPECTED = {
    "colour_contaminated.png":  ("OK",    8,    256, 1,   1211),
    "depth.pfm":                ("OK",   32,  30742, 1,      0),
    "grey_as_rgb16.png":        ("OK",   16,  56299, 1,      0),
    "imposter_ladder10bit.png": ("FAIL", 16,   1024, 64,     0),
    "imposter_shift256.png":    ("FAIL", 16,    256, 256,    0),
    "imposter_x257.png":        ("FAIL", 16,    256, 257,    0),
    "relief_demo.png":          ("OK",   16,  55195, 1,      0),
    "true12.pgm":               ("OK",   12,   3844, 1,      0),
    "true16.pgm":               ("OK",   16,  56299, 1,      0),
    "true16.png":               ("OK",   16,  56299, 1,      0),
    "true16_interlaced.png":    ("OK",   16,  56299, 1,      0),
    "true8.png":                ("OK",    8,    256, 1,      0),
}

# The same pixel data written three ways. If these ever disagree, the Adam7 or Netpbm
# path has drifted away from the plain 8-bit-per-sample PNG path, which is exactly the
# class of bug a hand-written decoder is prone to and a library would have hidden.
CROSS_CHECK = ["true16.png", "true16.pgm", "true16_interlaced.png"]

LINE = re.compile(
    r"^(OK|FAIL)\s+(\d+)x(\d+)\s+(\d+)bit\s+([\d,]+)\s+levels\s+"
    r"step\s+(\d+)\s+([\d,]+)\s+non-grey\s+(\S+)\s*$"
)


def num(text):
    return int(text.replace(",", ""))


def main(path):
    with open(path, encoding="utf-8") as handle:
        lines = handle.read().splitlines()

    seen = {}
    for line in lines:
        match = LINE.match(line.strip())
        if match:
            verdict, _w, _h, depth, levels, step, nongrey, name = match.groups()
            seen[name] = (verdict, int(depth), num(levels), int(step), num(nongrey))

    problems = []

    for name, expected in sorted(EXPECTED.items()):
        if name not in seen:
            problems.append(f"{name}: missing from the report")
            continue
        actual = seen[name]
        if actual != expected:
            problems.append(
                f"{name}:\n"
                f"    expected verdict={expected[0]} bits={expected[1]} "
                f"levels={expected[2]:,} step={expected[3]} non-grey={expected[4]:,}\n"
                f"    actual   verdict={actual[0]} bits={actual[1]} "
                f"levels={actual[2]:,} step={actual[3]} non-grey={actual[4]:,}"
            )

    for name in seen:
        if name not in EXPECTED:
            problems.append(
                f"{name}: in the report but not in EXPECTED. If this is a new fixture, "
                f"add its known-correct answer here."
            )

    present = [n for n in CROSS_CHECK if n in seen]
    if len(present) > 1:
        answers = {seen[n][1:] for n in present}
        if len(answers) != 1:
            detail = "\n".join(f"    {n}: {seen[n]}" for n in present)
            problems.append(
                "the same data stored three ways did not analyse identically, so a "
                "decoder path has drifted:\n" + detail
            )

    if problems:
        print(f"FAILED: {len(problems)} problem(s) in {path}\n")
        for problem in problems:
            print("  " + problem)
        return 1

    print(f"All {len(EXPECTED)} fixtures classified exactly as expected.")
    print(f"Cross-check passed: {', '.join(present)} agree on every measured value.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
