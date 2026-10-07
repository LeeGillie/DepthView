#!/usr/bin/env python3
"""Check the JSON interface (docs/INTEGRATION.md) the way a host program would use it.

DepthView is run directly with pipes and no shell - which is what Electron's child_process,
Python's subprocess and most other hosts do - and everything it prints is parsed as JSON.
The answers are pinned to the same known-by-construction expectations as check_report.py, so
the text report and the JSON report can never disagree about a fixture without one of the two
checks failing.

Usage:
    python tests/check_json.py <path to DepthView or DepthView.exe>

Needs tests/fixtures (python tests/make_fixtures.py). Exits 0 when every check holds, 1
otherwise, printing every failure rather than stopping at the first.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True   # importing check_report must not leave __pycache__ in tests/
sys.path.insert(0, HERE)
from check_report import EXPECTED, OPTIONAL  # noqa: E402  (same answers as the text check)

FIXTURES = os.path.join(HERE, "fixtures")
problems = []


def check(ok, message):
    if not ok:
        problems.append(message)


def same_file(a, b):
    """Paths come back absolute and normalised, which on Windows includes expanding 8.3 short
    names (a CI runner's TEMP is C:\\Users\\RUNNER~1\\...). Compare what they point at, not
    how they are spelled."""
    if not a or not b:
        return False
    return os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(b))


def run(exe, *args, cwd=None):
    p = subprocess.run([exe, *args], capture_output=True, text=True, cwd=cwd)
    try:
        doc = json.loads(p.stdout) if p.stdout.strip() else None
    except json.JSONDecodeError as e:
        problems.append(f"{' '.join(args[:2])}: stdout is not pure JSON ({e}): {p.stdout[:200]!r}")
        doc = None
    return p.returncode, doc, p.stdout, p.stderr


def write_gcode(path):
    """A small MakeIt-style job with known answers.

    Three heights (the last cut twice, identically - see below). At each, a main pass: 30 horizontal lines 0.05 mm apart, 20 samples of
    0.1 mm per line, power cycling through five levels (S 100..500). At the second height a
    cleaning pass follows: 30 vertical lines 0.05 mm apart at one power (S 300 would collide
    with the main levels, so S 250). The preamble carries the traps: M107X-105Y-105 must not
    move anything, M4S1000 must not become a power, and a Zundefined line must not become Z0.
    """
    out = [";wecreat 3.0.6", ";canvas border: 0 0 210 210",
           "M107X-105Y-105", "M4S1000", "G90", "G0X105Y105", "G0Zundefined"]

    def main_pass(z):
        out.append(f"G0Z{z}")
        out.append("M38F45")
        out.append("M39P250")
        out.append("G1F30000")          # 500 mm/s
        for i in range(30):
            y = 100 + i * 0.05
            out.append(f"G0X100Y{y:.3f}")
            for k in range(20):
                s = 100 * (1 + (k % 5))
                out.append(f"G1X{100 + (k + 1) * 0.1:.3f}S{s}")
            out.append("G1 S0")

    def cleaning_pass():
        # MakeIt repeats a height for a layer (seen on a real 10-layer job at 9 heights), so the
        # cleaning pass re-issues Z0.99: three layers, two heights.
        out.append("G0Z0.99")
        out.append("M38F100")
        out.append("M39P350")
        out.append("G1F210000")         # 3500 mm/s
        for i in range(30):
            x = 100 + i * 0.05
            out.append(f"G0X{x:.3f}Y100")
            out.append(f"G1Y102S250")
            out.append("G1 S0")

    main_pass(1.0)
    main_pass(0.99)
    cleaning_pass()
    # MakeIt 3.0.6 with Cleaning Layer on wrote each cleaning layer as a byte-identical copy of
    # the layer before it: layer 5 repeats layer 4 exactly. Then a layer that cuts nothing, and
    # a return to a travel height above the first cut, which is not a layer at all.
    main_pass(0.98)
    main_pass(0.98)
    out += ["G0Z0.97", "M38F45", "M39P250", "G0Z1.5"]
    out += ["G0X105Y105", "M5"]
    with open(path, "w", newline="\n") as f:
        f.write("\n".join(out) + "\n")


def check_gcode(exe, work):
    gc = os.path.join(work, "job.gc")
    write_gcode(gc)
    code, doc, out, _ = run(exe, "--gcode", gc, "--json", cwd=work)
    check(code == 0, f"gcode: exit {code}")
    if not doc:
        return
    check(doc.get("schema") == "depthview.gcode/1", f"gcode schema {doc.get('schema')!r}")
    check(doc.get("generator") == "wecreat 3.0.6", f"generator {doc.get('generator')!r}")
    levels = [s for s, _ in doc["powerLevels"]]
    check(levels == [100, 200, 250, 300, 400, 500], f"power levels {levels}")
    step = doc["alongLine"]["stepModeMm"]
    check(step == 0.1, f"step along line {step}, expected 0.1")
    zs = [z["z"] for z in doc["zLevels"]]
    check(zs == [1.0, 0.99, 0.98], f"cutting heights {zs} - Zundefined must not become a height")
    check(doc.get("layers") == 5, f"layers {doc.get('layers')}, expected 5 (a repeated height is a new layer)")
    lc = doc.get("layerChecks") or {}
    check(lc.get("mainGroup") == 1, f"main group {lc.get('mainGroup')}")
    check(lc.get("repeatedLayers") == [5], f"repeated layers {lc.get('repeatedLayers')}, expected [5]")
    check(lc.get("otherSettingsLayers") == [3], f"other-settings layers {lc.get('otherSettingsLayers')}, expected [3]")
    check(lc.get("emptyLayers") == 1 and lc.get("emptyLayersAtEnd") is True,
          f"empty layers {lc.get('emptyLayers')} at end {lc.get('emptyLayersAtEnd')} - the travel height must not count")
    reps = [b.get("repeatsLayer") for b in lc.get("blocks", [])]
    check(reps == [None, None, None, None, 4, None, None], f"repeatsLayer per block {reps}")
    check(doc["undefinedOperandLines"] == 1, f"undefined lines {doc['undefinedOperandLines']}")
    groups = doc["settingsGroups"]
    check(len(groups) == 2, f"expected 2 settings groups, got {len(groups)}")
    if len(groups) == 2:
        g1, g2 = groups[0]["makeIt"], groups[1]["makeIt"]
        check((g1["frequencyKHz"], g1["pulseWidthNs"], g1["speedMmPerS"]) == (45, 250, 500),
              f"main settings {g1}")
        check(g1["powerPercentMin"] == 10 and g1["powerPercentMax"] == 50, f"main power {g1}")
        check(abs(g1["lineDensityPerCm"] - 200) < 0.5, f"main line density {g1['lineDensityPerCm']}")
        check((g2["frequencyKHz"], g2["pulseWidthNs"], g2["speedMmPerS"]) == (100, 350, 3500),
              f"cleaning settings {g2}")
        check(groups[1]["rasterAnglesDeg"] == [90], f"cleaning direction {groups[1]['rasterAnglesDeg']}")
    check(doc["settingsSwitches"] == 2, f"settings switches {doc['settingsSwitches']}")
    area = doc["burnArea"]
    check(area["minX"] == 100 and area["minY"] == 100, f"burn area {area} - M107X-105Y-105 moved something")

    # The text form runs too, and says what it could not confirm.
    code, _, text, _ = run_text(exe, "--gcode", gc, cwd=work)
    check(code == 0 and "confirmed against MakeIt" in text and "6 distinct" in text
          and "5 layer(s) at 3 height(s)" in text
          and "1 layer(s) are exact copies" in text, "gcode text report")


def write_grey16(path, w, h, pixel):
    """A 16-bit greyscale PNG from pixel(x, y), with no dependency beyond the standard library."""
    import struct
    import zlib
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            rows += struct.pack(">H", pixel(x, y))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 16, 0, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(bytes(rows))))
        f.write(chunk(b"IEND", b""))


def check_fit_design(exe, work):
    """A disc of radius 200 centred at (320, 300) on a 600 x 800 black canvas.

    Centring the blank on the canvas puts the disc 101.6 px off centre - 6.8 mm on a 40 mm
    blank across the 600 px short side - which is the lopsided moat this fit exists to remove.
    With --fit design the canvas becomes a square around the disc, cropping only background.
    """
    src = os.path.join(work, "offcentre.png")
    write_grey16(src, 600, 800,
                 lambda x, y: 40000 if (x - 320) ** 2 + (y - 300) ** 2 <= 200 ** 2 else 0)

    plain = os.path.join(work, "offcentre-plain.png")
    code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", plain, "--blank", "40",
                          "--rim-mm", "1", cwd=work)
    check(code == 0, f"fit design (no fit): exit {code}")
    if doc:
        off = doc.get("designOffCentreMm")
        check(off is not None and abs(off - 6.77) < 0.1, f"designOffCentreMm {off}, expected ~6.77")
        check(doc.get("fit") is None, "no fit was asked for")

    fitted = os.path.join(work, "offcentre-design.png")
    code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", fitted, "--blank", "40",
                          "--rim-mm", "1", "--fit", "design", cwd=work)
    check(code == 0, f"fit design: exit {code}")
    if not doc:
        return
    size = doc.get("size", {})
    fit = doc.get("fit") or {}
    side = size.get("outWidth")
    check(side == size.get("outHeight"), f"fit design output not square: {size}")
    # Radius 200 plus a pixel of margin, inside 19 of 20 mm: ceil(402 / 0.95) = 424.
    check(side is not None and abs(side - 424) <= 2, f"fit design canvas {side}, expected ~424")
    check(fit.get("recentred") is True and fit.get("cropped") is True, f"fit design fit {fit}")
    check(fit.get("offsetX") in (-108, -109) and fit.get("offsetY") in (-88, -89),
          f"fit design offsets {fit.get('offsetX')}, {fit.get('offsetY')}")
    off = doc.get("designOffCentreMm")
    check(off is not None and off < 0.1, f"fit design leaves the disc {off} mm off centre")
    rim = doc.get("rim") or {}
    check(rim.get("contentPixelsClipped") == 0, f"fit design: rim clipped {rim.get('contentPixelsClipped')} px of disc")

    # The plain disc has no rim of its own, so --cover-rim must find none and change nothing.
    code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", os.path.join(work, "offcentre-norim.png"),
                          "--blank", "40", "--rim-mm", "1", "--cover-rim", cwd=work)
    check(code == 0 and doc and (doc.get("fit") or {}).get("designRim") is None,
          "cover-rim found a rim on a disc that has none")
    if doc:
        check(abs((doc.get("size") or {}).get("outWidth", 0) - 424) <= 2, "cover-rim without a rim changed the fit")

    # A coin drawn with its own raised rim: field 30000 inside r 180, a slope up to 50000 by
    # r 188, flat to r 196, then a bevel down to the black surround at r 200.
    def coin(x, y):
        r = ((x - 320) ** 2 + (y - 300) ** 2) ** 0.5
        if r < 180: return 30000
        if r < 188: return int(30000 + (r - 180) / 8 * 20000)
        if r < 196: return 50000
        if r <= 200: return int(50000 * (200 - r) / 4)
        return 0
    rimmed = os.path.join(work, "rimmed.png")
    write_grey16(rimmed, 600, 800, coin)
    code, doc, _, _ = run(exe, "--tune", rimmed, "--json", "--out", os.path.join(work, "rimmed-covered.png"),
                          "--blank", "40", "--rim-mm", "1", "--ramp-mm", "0.3", "--cover-rim", cwd=work)
    check(code == 0, f"cover-rim: exit {code}")
    if doc:
        own = (doc.get("fit") or {}).get("designRim")
        check(own is not None, "cover-rim did not find the drawn rim")
        if own:
            check(abs(own["innerPx"] - 180) <= 3, f"drawn rim foot at {own['innerPx']}, expected ~180")
        # The foot (r 180) lands on the ramp's inner edge, 18.7 of 20 mm: ceil(360 / 0.935) = 386.
        side = (doc.get("size") or {}).get("outWidth", 0)
        check(abs(side - 386) <= 3, f"cover-rim canvas {side}, expected ~386")
        check((doc.get("rim") or {}).get("contentPixelsClipped") == 0,
              "cover-rim reported the covered rim as clipped design")


def check_survey(exe, work):
    """The tuning wizard's measurements, and its --flat / --levels-from on the command line.

    A 600 px coin on a black surround: a dome in the middle (a real slope, never flat), a floor
    at 20000 with +-40 levels of pixel noise from r 60 to 170, and a drawn rim - a slope up to
    50000 by r 178, flat to 186, a bevel down to the surround by r 190. The floor is the thing
    the wizard exists for: one large area, flat but for noise that straddles layer boundaries.
    """
    def coin(x, y):
        r = ((x - 300) ** 2 + (y - 300) ** 2) ** 0.5
        if r < 60: return int(50000 - r / 60 * 20000)
        if r < 170: return 20000 + ((x * 73856093) ^ (y * 19349663)) % 81 - 40
        if r < 178: return int(20000 + (r - 170) / 8 * 30000)
        if r < 186: return 50000
        if r <= 190: return int(50000 * (190 - r) / 4)
        return 0
    src = os.path.join(work, "floorcoin.png")
    write_grey16(src, 600, 600, coin)

    code, doc, _, _ = run(exe, "--survey", src, "--json", "--passes", "72", cwd=work)
    check(code == 0 and bool(doc), f"survey: exit {code}")
    if not doc:
        return
    check(doc.get("schema") == "depthview.survey/1", f"survey schema {doc.get('schema')!r}")
    bg = doc.get("background") or {}
    check(bg.get("level") == 0 and bg.get("looksLikeFloor") is False and bg.get("shaded") is False,
          f"survey background {bg}")
    rim = doc.get("drawnRim") or {}
    check(abs(rim.get("innerPx", 0) - 170) <= 4, f"survey drawn rim foot {rim.get('innerPx')}, expected ~170")
    reading = next((r for r in doc.get("readings", [])
                    if r["backgroundIsDesign"] is False and r["drawnRimCovered"] is True), None)
    check(reading is not None, "survey has no reading for a surround with the drawn rim covered")
    floor = (reading or {}).get("floor") or {}
    check(floor.get("found") is True and str(floor.get("source", "")).startswith("flat area"),
          f"survey floor not found from a flat area: {floor}")
    check(19950 <= floor.get("low", 0) and floor.get("high", 99999) <= 20050 and floor.get("suggested", 0) >= floor.get("high", 0),
          f"survey floor band {floor.get('low')}..{floor.get('high')} -> {floor.get('suggested')}")
    areas = doc.get("flatAreas") or []
    check(bool(areas) and areas[0].get("floor") is True and areas[0].get("mostlyJitter") is True,
          f"survey: the largest flat area should be the noisy floor: {areas[:1]}")
    # Quoted at the suggested level points, which make the floor one depth already - so the
    # floor crosses no boundary. That is the black point doing the flattening.
    check(bool(areas) and areas[0].get("boundariesCrossed") == 0,
          f"survey: the floor should cross no boundary at its own black point, crosses {areas[0].get('boundariesCrossed') if areas else None}")

    out = os.path.join(work, "floorcoin-tuned.png")
    code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", out, "--blank", "40", "--rim-mm", "1",
                          "--cover-rim", "--levels-from", "design", "--flat", "flatten", "--passes", "72", cwd=work)
    check(code == 0 and bool(doc), f"tune with --flat: exit {code}")
    if doc:
        check((doc.get("applied") or {}).get("blackPoint") == floor.get("suggested"),
              f"--levels-from design black {(doc.get('applied') or {}).get('blackPoint')}, survey says {floor.get('suggested')}")
        flat = doc.get("flat") or {}
        modes = [a.get("mode") for a in flat.get("areas", [])]
        check(modes[:1] == ["flatten"] and all(m == "leave" for m in modes[1:]), f"--flat modes {modes}")
        check(flat.get("pixelsChanged", 0) > 0 and flat.get("maxChange", 99) <= 60,
              f"--flat changed {flat.get('pixelsChanged')} px by at most {flat.get('maxChange')}")


def write_rgb8(path, w, h, pixel):
    """An 8-bit RGB PNG from pixel(x, y) -> (r, g, b)."""
    import struct
    import zlib
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            rows += bytes(pixel(x, y))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(bytes(rows))))
        f.write(chunk(b"IEND", b""))


def check_surround_and_gap(exe, work):
    """A shaded surround, an empty gap below the design, and a picture that is not a depth map.

    A 600 px coin whose surround is a vignette - 1000 at the corners rising to 4000 by r 300,
    so no one level covers it - around a dome from 50000 down to 20000 at r 230.
    Three 6 px pockets at 300 sit far below the rest of the design, with nothing between them
    and 20000: an empty gap that would spend layers cutting nothing.
    """
    pockets = [(300, 200), (220, 330), (380, 340)]

    def coin(x, y):
        r = ((x - 300) ** 2 + (y - 300) ** 2) ** 0.5
        if r > 230:
            return int(1000 + 3000 * (424 - min(max(r, 300), 424)) / 124)
        if any(abs(x - px) < 3 and abs(y - py) < 3 for px, py in pockets):
            return 300
        return int(50000 - r / 230 * 30000)
    src = os.path.join(work, "vignette.png")
    write_grey16(src, 600, 600, coin)

    code, doc, _, _ = run(exe, "--survey", src, "--json", "--passes", "72", cwd=work)
    check(code == 0 and bool(doc), f"survey vignette: exit {code}")
    if doc:
        bg = doc.get("background") or {}
        check(bg.get("shaded") is True, f"survey: a vignetted surround should read as shaded: {bg}")
        reading = next((r for r in doc.get("readings", [])
                        if r["backgroundIsDesign"] is False and r["drawnRimCovered"] is False), None)
        floor = (reading or {}).get("floor") or {}
        check(floor.get("source") == "gap" and floor.get("suggested", 0) > 15000,
              f"survey: the pockets beyond the empty gap should give a 'gap' floor closing it: {floor}")

    out = os.path.join(work, "vignette-even.png")
    code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", out, "--uniform-surround", cwd=work)
    check(code == 0 and bool(doc), f"tune --uniform-surround: exit {code}")
    if doc:
        check((doc.get("applied") or {}).get("uniformSurround") is True, "applied.uniformSurround not true")
        check(doc.get("surroundPixelsEvened", 0) > 10000,
              f"--uniform-surround evened only {doc.get('surroundPixelsEvened')} px of a vignette")

    # A coloured picture of a coin is not a depth map, whatever its grey minority looks like.
    pic = os.path.join(work, "picture.png")
    write_rgb8(pic, 64, 64, lambda x, y: (255, 255, 255) if (x < 8 or y < 8)
               else (180 + x % 40, 140 + y % 30, 60 + (x + y) % 20))
    code, doc, _, _ = run(exe, "--report", pic, "--json", cwd=work)
    check(code == 1, f"a colour picture: expected exit 1, got {code}")
    if doc:
        v = doc["files"][0].get("verdict") or {}
        check(v.get("severity") == "alert" and str(v.get("title", "")).startswith("NOT A DEPTH MAP")
              and v.get("imposter") == "none", f"a colour picture's verdict: {v}")


def check_terraces(exe, work):
    """--terraces on a smooth shallow cone, where the answer follows from geometry.

    A 600 px cone on a 6 mm blank (10 um a pixel), 0.5 mm deep, using the top twelfth of the
    range across a 280 px radius - a gentle, even slope, like a cheek or a neck. Few passes
    leave wide treads that show; thousands crowd them under the spot. The same cone squeezed
    to 8 bits has about twenty levels across that slope, so it terraces on its own steps
    however many passes are run - the case a high layer count cannot rescue.
    """
    def dome(x, y):
        r = ((x - 300) ** 2 + (y - 300) ** 2) ** 0.5 / 280.0
        return min(65535, int(round(60000 + 5535 * r)))
    src = os.path.join(work, "cone16.png")
    write_grey16(src, 600, 600, dome)
    src8 = os.path.join(work, "cone8.png")
    write_grey16(src8, 600, 600, lambda x, y: (dome(x, y) // 257) * 257)

    def terr(path, passes, *extra):
        code, doc, _, _ = run(exe, "--terraces", path, "--json", "--passes", str(passes), "--blank", "6",
                              "--depth-mm", "0.5", "--spot", "30", *extra, cwd=work)
        check(code == 0 and bool(doc), f"--terraces {os.path.basename(path)} at {passes}: exit {code}")
        return doc or {}

    few, many = terr(src, 32), terr(src, 4096)
    check(few.get("schema") == "depthview.terrace/1", f"terrace schema {few.get('schema')!r}")
    fe, me = few.get("edges") or {}, many.get("edges") or {}
    check(fe.get("shareWiderThanSpot", 0) > me.get("shareWiderThanSpot", 1),
          f"terraces: 32 passes should show more steps than 4096 ({fe.get('shareWiderThanSpot')} vs {me.get('shareWiderThanSpot')})")
    check(me.get("shareWiderThanSpot", 1) < 0.1, f"terraces: 4096 passes on a 16-bit cone should blend: {me}")
    check(abs(few.get("micronsPerPixel", 0) - 10) < 0.2, f"terraces: {few.get('micronsPerPixel')} um/pixel, expected 10")
    check(few.get("limitedByLevels") is False and (few.get("passesToBlend90") or 0) > 32,
          f"terraces: a smooth 16-bit dome should blend with more passes: {few.get('passesToBlend90')}, limited {few.get('limitedByLevels')}")

    eight = terr(src8, 4096)
    check(eight.get("usedLevels", 0) <= 256 and eight.get("limitedByLevels") is True,
          f"terraces: an 8-bit dome at 4096 passes should be limited by its own levels: {eight.get('usedLevels')}, {eight.get('limitedByLevels')}")

    # Only the blank is measured. A flat coin on a shaded background terraces in contour lines
    # across the corners - which are not on the coin and must not be counted.
    corners = os.path.join(work, "shaded-corners.png")
    write_grey16(corners, 600, 600,
                 lambda x, y: 60000 if (x - 299.5) ** 2 + (y - 299.5) ** 2 <= 300 ** 2 else 20000 + 40 * y)
    cdoc = terr(corners, 64)
    check(abs(cdoc.get("blankRadiusPx", 0) - 300) < 0.01, f"terraces: blank radius {cdoc.get('blankRadiusPx')}, expected 300")
    check((cdoc.get("edges") or {}).get("pixels", -1) == 0 and cdoc.get("usedLevels") == 1,
          f"terraces: the shaded corners were counted: {cdoc.get('edges')}, {cdoc.get('usedLevels')} levels")

    overlay = os.path.join(work, "dome-terraces.png")
    terr(src, 32, "--out", overlay)
    check(os.path.exists(overlay), "--terraces --out did not write the overlay")
    code, doc, _, _ = run(exe, "--terraces", src, "--json", "--out", src, cwd=work)
    check(code == 2, f"--terraces --out naming the input: expected exit 2, got {code}")


def check_lit(exe, work):
    """A grey picture of a relief lit from one side, against the depth map it was drawn from.

    Eight raised discs with 4 px bevels on a 400 px field. The depth map is the heights; the
    render is the same heights shaded by a light from the upper left, with the shadows the
    discs cast. The depth map must read as genuine and the render as lit.
    """
    import math
    n = 400
    discs = [(100, 100, 30), (280, 120, 40), (200, 220, 50), (110, 300, 25),
             (310, 300, 32), (200, 80, 18), (60, 200, 20), (350, 200, 22)]
    H = [[0.0] * n for _ in range(n)]
    for y in range(n):
        for x in range(n):
            h = 0.0
            for cx, cy, s in discs:
                h = max(h, min(1.0, max(0.0, (s - math.hypot(x - cx, y - cy)) / 4)))
            H[y][x] = h

    def shade(x, y):
        gx = (H[y][min(n - 1, x + 1)] - H[y][max(0, x - 1)]) * 15
        gy = (H[min(n - 1, y + 1)][x] - H[max(0, y - 1)][x]) * 15
        v = max(0.0, (gx + gy + 1.0) / math.sqrt(gx * gx + gy * gy + 1) / math.sqrt(3))
        for k in range(1, 9):
            if x - k < 0 or y - k < 0:
                break
            if H[y - k][x - k] > H[y][x] + 0.06 * k:
                return v * 0.35
        return v

    depth = os.path.join(work, "discs-depth.png")
    write_grey16(depth, n, n, lambda x, y: int(round(H[y][x] * 65535)))
    lit = os.path.join(work, "discs-lit.png")
    write_grey16(lit, n, n, lambda x, y: int(round(shade(x, y) * 65535)))

    code, doc, _, _ = run(exe, "--report", depth, lit, "--json", cwd=work)
    check(bool(doc), f"lit check: report exit {code}")
    if doc:
        files = {f["name"]: f for f in doc.get("files", [])}
        d, l = files.get("discs-depth.png") or {}, files.get("discs-lit.png") or {}
        dc, lc = d.get("content") or {}, l.get("content") or {}
        check(dc.get("litScore", 1) < dc.get("litThreshold", 0),
              f"lit check: the depth map scored {dc.get('litScore')} against {dc.get('litThreshold')}")
        check(lc.get("litScore", 0) >= lc.get("litThreshold", 1)
              and str((l.get("verdict") or {}).get("title", "")).startswith("LOOKS LIT"),
              f"lit check: the render scored {lc.get('litScore')}, verdict {(l.get('verdict') or {}).get('title')!r}")


def write_synthetic(path):
    """A 400 px test map with one of each thing the inspections look for, at known places.

    A smooth dome clipped flat just below its top at (110, 110) - a flattened peak; the same
    dome left whole at (290, 110), which must not be one; a smooth slope with pixel noise on
    it at x 40-160, y 240-340; and raised lines 1, 2, 4 and 8 px wide at x 220, 260, 300, 340.
    """
    import random
    rnd = random.Random(3)
    noise = {}

    def px(x, y):
        v = 20000.0
        v = max(v, min(60000 - ((x - 110) ** 2 + (y - 110) ** 2) * 1.4, 59800))
        v = max(v, 56000 - ((x - 290) ** 2 + (y - 110) ** 2) * 1.4)
        if 40 <= x < 160 and 240 <= y < 340:
            v = 35000 + (x - 100) * 20 + noise.setdefault((x, y), rnd.gauss(0, 600))
        for k, wdt in enumerate((1, 2, 4, 8)):
            x0 = 220 + k * 40
            if x0 <= x < x0 + wdt and 220 <= y < 360:
                v = 45000
        return int(max(0, min(65535, round(v))))

    write_grey16(path, 400, 400, px)


def check_inspect(exe, work):
    """--detail, --noise and the survey's flattened peaks on the synthetic map, and a
    finished render. 400 px on a 4 mm blank is 10 um a pixel; the spot is 30 um."""
    src = os.path.join(work, "synthetic.png")
    write_synthetic(src)
    common = ("--json", "--passes", "256", "--blank", "4", "--depth-mm", "0.5")

    code, doc, _, _ = run(exe, "--detail", src, *common, "--spot", "30", cwd=work)
    doc = doc or {}
    check(code == 0 and doc.get("schema") == "depthview.detail/1", f"--detail: exit {code}, schema {doc.get('schema')!r}")
    us, u2 = doc.get("underSpot") or {}, doc.get("underTwoSpots") or {}
    check(abs(doc.get("micronsPerPixel", 0) - 10) < 0.2, f"detail: {doc.get('micronsPerPixel')} um/pixel, expected 10")
    check(us.get("raisedPixels", 0) > 0, f"detail: the 1-2 px lines should be under the spot: {us}")
    # The two bands are separate: finer than one spot, then between one and two spots.
    check(u2.get("raisedPixels", 0) > 0, f"detail: the 2-4 px lines should be under two spots: {u2}")
    check(0 < us.get("share", 1) < 0.2, f"detail: share under the spot {us.get('share')}")

    code, doc, _, _ = run(exe, "--noise", src, *common, cwd=work)
    doc = doc or {}
    check(code == 0 and doc.get("schema") == "depthview.noise/1", f"--noise: exit {code}, schema {doc.get('schema')!r}")
    check(doc.get("noisyPixels", 0) > 0 and doc.get("shareNoisy", 1) < 0.3,
          f"noise: the noisy patch should be found and nothing much else: {doc.get('noisyPixels')}, {doc.get('shareNoisy')}")
    cone = os.path.join(work, "cone16.png")
    if os.path.exists(cone):
        code, cdoc, _, _ = run(exe, "--noise", cone, *common, cwd=work)
        check((cdoc or {}).get("noisyPixels", -1) == 0, f"noise: a smooth cone is not noisy: {(cdoc or {}).get('noisyPixels')}")

    code, doc, _, _ = run(exe, "--survey", src, "--json", cwd=work)
    peaks = (doc or {}).get("flatPeaks")
    check(isinstance(peaks, list) and len(peaks) >= 1, f"survey: flatPeaks missing or empty: {peaks!r}")
    if peaks:
        p0 = peaks[0]
        check(abs(p0.get("centreX", 0) - 110) < 6 and abs(p0.get("centreY", 0) - 110) < 6,
              f"survey: the clipped dome is at (110, 110), got ({p0.get('centreX')}, {p0.get('centreY')})")
        check(all(abs(p.get("centreX", 0) - 290) > 20 for p in peaks), f"survey: the whole dome is not a flat peak: {peaks}")

    # The finishing preview, headless: a render, and a typo refused rather than ignored.
    out = os.path.join(work, "finished.png")
    code, _, text, err = run_text(exe, "--render", src, "--blank", "4", "--depth-mm", "0.5", "--size", "200",
                                  "--finish", "darken=jax_black;relieve=hardfelt_rouge;seal=wax", "--out", out, cwd=work)
    check(code == 0 and os.path.exists(out), f"--render --finish: exit {code}, {err.strip()[:200]}")
    check("JAX Black" in text and "Hard felt" in text, f"--render --finish should describe the recipe: {text[:300]!r}")
    code, _, _, err = run_text(exe, "--render", src, "--size", "100", "--finish", "darken=jax_black;relve=propad",
                               "--out", os.path.join(work, "typo.png"), cwd=work)
    check(code == 2 and "relve" in err, f"--render --finish with an unknown key: exit {code}, {err.strip()[:200]}")


def write_grey16_tagged(path, w, h, pixel, chunks):
    """As write_grey16, with extra chunks (tag, data) before the image data."""
    import struct
    import zlib
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            rows += struct.pack(">H", pixel(x, y))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 16, 0, 0, 0, 0)))
        for tag, data in chunks:
            f.write(chunk(tag, data))
        f.write(chunk(b"IDAT", zlib.compress(bytes(rows))))
        f.write(chunk(b"IEND", b""))


def shapes_pixel(ss):
    """A disc and a tilted bar on a flat field, drawn with ss x ss samples per pixel and
    averaged - ss 1 is art rendered at its final size, ss 3 art built at 3x and reduced."""
    import math
    c, s = math.cos(0.5), math.sin(0.5)

    def inside(x, y):
        if (x - 130) ** 2 + (y - 170) ** 2 < 70 ** 2:
            return 50000
        X, Y = (x - 260) * c + (y - 230) * s, -(x - 260) * s + (y - 230) * c
        if abs(X) < 60 and abs(Y) < 25:
            return 40000
        return 20000

    def px(x, y):
        total = 0
        for i in range(ss):
            for j in range(ss):
                total += inside(x + (i + 0.5) / ss - 0.5, y + (j + 0.5) / ss - 0.5)
        return int(round(total / (ss * ss)))
    return px


def letters_pixel(stroke):
    """Block letters H, I, T, L and an O ring, 60 px tall, every stroke `stroke` px wide."""
    bars = []
    for k, x0 in enumerate((80, 140, 190, 250)):
        if k == 0:    # H
            bars += [(x0, 170, x0 + stroke, 230), (x0 + 36 - stroke, 170, x0 + 36, 230),
                     (x0, 200 - stroke // 2, x0 + 36, 200 - stroke // 2 + stroke)]
        elif k == 1:  # I
            bars += [(x0 + 15, 170, x0 + 15 + stroke, 230)]
        elif k == 2:  # T
            bars += [(x0, 170, x0 + 36, 170 + stroke), (x0 + 18 - stroke // 2, 170, x0 + 18 - stroke // 2 + stroke, 230)]
        else:         # L
            bars += [(x0, 170, x0 + stroke, 230), (x0, 230 - stroke, x0 + 36, 230)]

    def px(x, y):
        for x0, y0, x1, y1 in bars:
            if x0 <= x < x1 and y0 <= y < y1:
                return 45000
        r = ((x - 300) ** 2 + (y - 290) ** 2) ** 0.5
        if 30 - stroke <= r < 30:    # O
            return 45000
        return 20000
    return px


def check_round2(exe, work):
    """Jagged edges, the display curve, the report's shape fields, and lettering against the spot."""
    hard = os.path.join(work, "shapes-hard.png")
    smooth = os.path.join(work, "shapes-3x.png")
    write_grey16(hard, 400, 400, shapes_pixel(1))
    write_grey16(smooth, 400, 400, shapes_pixel(3))

    code, h, _, _ = run(exe, "--aliasing", hard, "--json", "--blank", "4", cwd=work)
    h = h or {}
    check(code == 0 and h.get("schema") == "depthview.aliasing/1", f"--aliasing: exit {code}, schema {h.get('schema')!r}")
    check(h.get("judged") is True and h.get("jagged") is True and h.get("share", 0) >= 0.8,
          f"aliasing: art drawn at final size should be jagged: {h.get('share')}, {h.get('edgePixels')} px")
    code, s, _, _ = run(exe, "--aliasing", smooth, "--json", "--blank", "4", cwd=work)
    s = s or {}
    check(s.get("judged") is True and s.get("jagged") is False and s.get("share", 1) <= 0.45,
          f"aliasing: the same art built at 3x and reduced should be smooth: {s.get('share')}")

    code, rep, _, _ = run(exe, "--report", hard, smooth, "--json", cwd=work)
    files = {f["name"]: f for f in (rep or {}).get("files", [])}
    hc = (files.get("shapes-hard.png") or {}).get("content") or {}
    je = hc.get("jaggedEdges") or {}
    check(je.get("jagged") is True, f"report: jaggedEdges for the hard art: {je}")
    titles = [f.get("title") for f in (files.get("shapes-hard.png") or {}).get("findings", [])]
    check("Jagged edges" in titles, f"report: no Jagged edges finding: {titles}")
    sc = (files.get("shapes-3x.png") or {}).get("content") or {}
    check((sc.get("jaggedEdges") or {}).get("jagged") is False, f"report: jaggedEdges for the 3x art: {sc.get('jaggedEdges')}")
    check(hc.get("flatPeaks") == 0, f"report: flatPeaks on flat shapes should be 0: {hc.get('flatPeaks')}")
    check((hc and (files.get("shapes-hard.png") or {}).get("container", {}).get("displayCurve", "x") is None),
          "report: an untagged file has displayCurve null")

    syn = os.path.join(work, "synthetic.png")
    if os.path.exists(syn):
        code, rep, _, _ = run(exe, "--report", syn, "--json", cwd=work)
        e = ((rep or {}).get("files") or [{}])[0]
        check((e.get("content") or {}).get("flatPeaks", 0) >= 1, f"report: the clipped dome is a flattened peak: {e.get('content')}")
        check("Flattened peaks" in [f.get("title") for f in e.get("findings", [])], "report: no Flattened peaks finding")

    # A display curve: sRGB, and a plain gamma 1/2.2. Undone, half-way down is ~79% down.
    import struct
    ramp = lambda x, y: int(x * 65535 / 255) if x < 256 else 65535
    srgb = os.path.join(work, "ramp-srgb.png")
    write_grey16_tagged(srgb, 256, 64, ramp, [(b"sRGB", b"\x00"), (b"gAMA", struct.pack(">I", 45455))])
    gam = os.path.join(work, "ramp-gamma.png")
    write_grey16_tagged(gam, 256, 64, ramp, [(b"gAMA", struct.pack(">I", 45455))])
    lin = os.path.join(work, "ramp-linear.png")
    write_grey16_tagged(lin, 256, 64, ramp, [(b"gAMA", struct.pack(">I", 100000))])
    code, rep, _, _ = run(exe, "--report", srgb, gam, lin, "--json", cwd=work)
    files = {f["name"]: f for f in (rep or {}).get("files", [])}
    dc = ((files.get("ramp-srgb.png") or {}).get("container") or {}).get("displayCurve") or {}
    check(dc.get("kind") == "srgb" and dc.get("canUndo") is True and abs(dc.get("depthAtStoredHalf", 0) - 0.786) < 0.01,
          f"display curve: sRGB (overriding gAMA) expected: {dc}")
    dg = ((files.get("ramp-gamma.png") or {}).get("container") or {}).get("displayCurve") or {}
    check(dg.get("kind") == "gamma" and abs(dg.get("fileGamma", 0) - 0.45455) < 1e-4
          and abs(dg.get("depthAtStoredHalf", 0) - (1 - 0.5 ** 2.2)) < 0.01, f"display curve: gamma expected: {dg}")
    dl = ((files.get("ramp-linear.png") or {}).get("container") or {}).get("displayCurve", "x")
    check(dl is None, f"display curve: gamma 1.0 is linear, not a curve: {dl}")
    st = [f.get("title", "") for f in (files.get("ramp-srgb.png") or {}).get("findings", [])]
    check(any(x.startswith("Display curve declared") for x in st), f"display curve: no finding: {st}")
    lt = [f.get("title", "") for f in (files.get("ramp-linear.png") or {}).get("findings", [])]
    check("Linear gamma declared" in lt, f"display curve: gamma 1.0 should be reported as linear: {lt}")

    # Lettering against the spot: 30 um on 10 um pixels is 3 px. Strokes of 1-2 px are under
    # the spot, 4 px under two spots, 8 px under neither.
    common = ("--json", "--passes", "256", "--blank", "4", "--depth-mm", "0.5", "--spot", "30")
    share = {}
    for stroke in (1, 2, 4, 8):
        path = os.path.join(work, f"letters-{stroke}.png")
        fn = letters_pixel(stroke)
        write_grey16(path, 400, 400, fn)
        ink = sum(1 for y in range(400) for x in range(400) if fn(x, y) == 45000)
        code, doc, _, _ = run(exe, "--detail", path, *common, cwd=work)
        doc = doc or {}
        one = (doc.get("underSpot") or {}).get("raisedPixels", 0) / ink
        two = (doc.get("underTwoSpots") or {}).get("raisedPixels", 0) / ink
        share[stroke] = (round(one, 2), round(two, 2))
    check(share[1][0] >= 0.8 and share[2][0] >= 0.8, f"lettering: 1-2 px strokes should be under the spot: {share}")
    check(share[4][0] <= 0.15 and share[4][1] >= 0.6, f"lettering: 4 px strokes should be under two spots only: {share}")
    check(share[8][0] <= 0.1 and share[8][1] <= 0.2, f"lettering: 8 px strokes should survive: {share}")


def run_text(exe, *args, cwd=None):
    p = subprocess.run([exe, *args], capture_output=True, text=True, cwd=cwd)
    return p.returncode, None, p.stdout, p.stderr


def main(exe):
    exe = os.path.abspath(exe)
    work = tempfile.mkdtemp(prefix="dv-json-")
    try:
        # --- the folder report --------------------------------------------------------
        before = set(os.listdir(FIXTURES))
        code, doc, _, _ = run(exe, "--report", FIXTURES, "--json", cwd=work)
        check(code == 1, f"folder report: expected exit 1 (imposters present), got {code}")
        check(set(os.listdir(FIXTURES)) == before, "folder report wrote files beside the inputs")
        if doc:
            check(doc.get("schema") == "depthview.report/1", f"schema is {doc.get('schema')!r}")
            files = {f["name"]: f for f in doc.get("files", [])}
            for name, (verdict, bits, levels, step, nongrey) in {**EXPECTED, **OPTIONAL}.items():
                f = files.get(name)
                if f is None:
                    check(name in OPTIONAL, f"{name}: missing from the JSON report")
                    continue
                actual = (
                    "FAIL" if f["verdict"]["severity"] == "alert" else "OK",
                    f["content"]["bitDepth"],
                    f["content"]["uniqueGreyLevels"],
                    f["levels"]["step"],
                    f["content"]["nonGreyPixels"],
                )
                check(actual == (verdict, bits, levels, step, nongrey),
                      f"{name}: expected {(verdict, bits, levels, step, nongrey)}, got {actual}")
                check(isinstance(f.get("passCounts"), list) and len(f["passCounts"]) == 7,
                      f"{name}: expected the 7 default pass counts")

            imposters = {n: files[n]["verdict"]["imposter"] for n in
                         ("imposter_x257.png", "imposter_shift256.png", "imposter_ladder10bit.png") if n in files}
            check(imposters == {"imposter_x257.png": "replicated257",
                                "imposter_shift256.png": "highByteOnly",
                                "imposter_ladder10bit.png": "quantisedLadder"},
                  f"imposter kinds: {imposters}")

        # --- one file, chosen pass counts, histogram, --out ---------------------------
        target = os.path.join(work, "one.json")
        code, doc, out, _ = run(exe, "--report", os.path.join(FIXTURES, "true16.png"), "--json",
                                "--passes", "60,120", "--histogram", "--out", target, cwd=work)
        check(code == 0, f"single report: exit {code}")
        check(out.strip() == "", "with --out, nothing should go to stdout")
        if os.path.exists(target):
            f = json.load(open(target))["files"][0]
            check([r["passes"] for r in f["passCounts"]] == [60, 120], "custom --passes not honoured")
            hist = f.get("histogram", [])
            check(len(hist) == 56299, f"histogram should list 56,299 occupied levels, has {len(hist)}")
            check(sum(c for _, c in hist) == f["content"]["greyPixels"], "histogram counts do not sum to greyPixels")
        else:
            check(False, "--out file was not written")

        # --- output is plain ASCII, and still carries a non-ASCII name exactly ---------
        odd = os.path.join(work, "café µm + 16bit.png")
        shutil.copyfile(os.path.join(FIXTURES, "true8.png"), odd)
        code, doc, out, _ = run(exe, "--report", odd, "--json", cwd=work)
        check(all(ord(ch) < 128 for ch in out), "JSON output contains non-ASCII characters")
        check("\\u002B" not in out, "'+' is escaped - the HTML-safe encoder is back")
        check(bool(doc) and same_file(doc["files"][0].get("path"), odd),
              "a non-ASCII file name did not survive the round trip")

        # --- a file that is not there -------------------------------------------------
        code, doc, _, _ = run(exe, "--report", os.path.join(work, "nosuch.png"), "--json", cwd=work)
        check(code == 2, f"missing file: expected exit 2, got {code}")
        check(bool(doc) and doc["files"][0]["ok"] is False and doc["files"][0]["error"],
              "missing file should be an entry with ok=false and an error")

        # --- tune ---------------------------------------------------------------------
        src = os.path.join(work, "source.png")
        shutil.copyfile(os.path.join(FIXTURES, "true16.png"), src)
        tuned = os.path.join(work, "tuned.png")
        code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", tuned, "--blank", "40",
                              "--depth-mm", "0.7", "--passes", "200", cwd=work)
        check(code == 0, f"tune: exit {code}")
        check(os.path.exists(tuned), "tune did not write its output")
        if doc:
            check(doc.get("schema") == "depthview.tune/1" and doc.get("ok") is True, "tune envelope")
            check(doc.get("target", {}).get("targetMicronsPerPass") == 3.5, f"target: {doc.get('target')}")
            after = doc.get("after", {})
            check(same_file(after.get("path"), tuned), "after.path should be the written file")
            check(same_file(doc.get("output"), tuned), "output should be the written file")
            check(any(r["passes"] == 200 for r in after.get("passCounts", [])), "tune table lacks --passes 200")
            ter = doc.get("terraces") or {}
            check((ter.get("before") or {}).get("passes") == 200 and (ter.get("after") or {}).get("passes") == 200,
                  f"tune: terraces before/after at the pass count, got {ter}")

        # --- G-code: a synthetic job whose answers are known by construction -------------
        check_gcode(exe, work)

        # --- a coin drawn off-centre on a tall canvas: --fit design --------------------
        check_fit_design(exe, work)

        # --- the wizard's survey, and --flat / --levels-from -------------------------
        check_survey(exe, work)

        # --- a shaded surround, an empty gap, a picture that is not a depth map -------
        check_surround_and_gap(exe, work)

        # --- where a map will terrace ------------------------------------------------
        check_terraces(exe, work)

        # --- a lit render passed off as a depth map ---------------------------------
        check_lit(exe, work)

        # --- detail under the spot, pixel noise, flattened peaks, a finished render ---
        check_inspect(exe, work)

        # --- jagged edges, the display curve, the report's shape fields, lettering ----
        check_round2(exe, work)

        # --- the original is never written over -------------------------------------
        size = os.path.getsize(src)
        stamp = os.path.getmtime(src)
        code, doc, _, _ = run(exe, "--tune", src, "--json", "--out", src, cwd=work)
        check(code == 2, f"overwrite: expected exit 2, got {code}")
        check(bool(doc) and doc.get("ok") is False, "overwrite should report ok=false")
        check(os.path.getsize(src) == size and os.path.getmtime(src) == stamp, "the input file was modified")
    finally:
        shutil.rmtree(work, ignore_errors=True)

    if problems:
        print(f"{len(problems)} problem(s):")
        for p in problems:
            print("  " + p)
        return 1
    print("JSON interface: all checks passed.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
