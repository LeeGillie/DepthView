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
