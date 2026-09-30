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

        # --- G-code: a synthetic job whose answers are known by construction -------------
        check_gcode(exe, work)

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
