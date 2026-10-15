"""Checks the ablation study's headless path: the demo study's coupon job, built and audited.

    python tests/check_ablation.py <path-to-DepthView-exe>

Runs `--ablation-demo --json --out job.gc` through pipes (no shell), as CI does, and holds the
answer to what the demo is known to produce: a 4 x 4 in sheet holds 195 settings squares of
5 mm plus the number; the demo design is 34 settings; the hottest patch is under the study's
energy ceiling; the independent audit of the written G-code agrees with the planned dose to
within a few percent (it reads the job back, so this is the layer count, hatch and power all
checked at once); and the job is a MakeIt-shaped file with no Z moves.
"""
import json
import os
import subprocess
import sys
import tempfile


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    exe = sys.argv[1]
    with tempfile.TemporaryDirectory() as tmp:
        gc = os.path.join(tmp, "demo.gc")
        r = subprocess.run([exe, "--ablation-demo", "--json", "--out", gc], capture_output=True, text=True, timeout=300)
        if r.returncode != 0:
            fail(f"exit {r.returncode}: {r.stderr.strip()}")
        try:
            d = json.loads(r.stdout)
        except json.JSONDecodeError as e:
            fail(f"stdout is not JSON ({e}): {r.stdout[:200]!r}")
        if d.get("schema") != "depthview.ablation-demo/1":
            fail(f"schema {d.get('schema')!r}")
        lay = d["layout"]
        if (lay["Rows"], lay["Columns"], lay["Capacity"], lay["squares"]) != (14, 14, 195, 196):
            fail(f"layout {lay}")
        if d["design"] != 34 or d["couponsNeeded"] != 1:
            fail(f"design {d['design']} / coupons {d['couponsNeeded']}")
        if d["planned"]["idText"] != "DV1" or d["planned"]["patches"] != 34:
            fail(f"planned {d['planned']}")
        if d["hottestPatchJPerMm2"] > d["ceilingJPerMm2"]:
            fail("hottest patch over the ceiling")
        a = d["audit"]
        if not a["Ok"]:
            fail(f"audit refused: {a['Message']}")
        hottest = d["hottestPatchJPerMm2"]
        if abs(a["maxJPerMm2"] - hottest) > 0.05 * hottest:
            fail(f"audit reads {a['maxJPerMm2']} J/mm2 against a planned {hottest}")
        if not os.path.exists(gc):
            fail("no job file written")
        with open(gc, encoding="utf-8") as f:
            lines = f.read().split("\n")
        if not lines[0].startswith(";wecreat"):
            fail(f"job does not start like a MakeIt job: {lines[0]!r}")
        body = [ln for ln in lines if ln]
        if body[-1] != "M6":
            fail(f"job does not end with M6: {body[-1]!r}")
        if any("Z" in ln for ln in body if ln.startswith("G")):
            fail("the job moves Z; it must not (focus is the machine's)")
        if "M38F45" not in body or "M39P250" not in body:
            fail("frequency / pulse width words missing")
        powered = [ln for ln in body if ln.startswith("G1") and "S" in ln and not ln.endswith("S0")]
        if not powered:
            fail("no powered moves")
        for ln in powered:
            s = int(ln[ln.rindex("S") + 1:])
            if s < 0 or s > 1000:
                fail(f"power word out of range: {ln}")
        if d["gcode"]["lines"] != len(lines) - 1:
            fail(f"line count {len(lines) - 1} != reported {d['gcode']['lines']}")
        # The focus ramp and a job carrying a focus height: Z only where it was asked for.
        fo = d["focus"]
        z = fo["demoZ"]
        ramp = fo["ramp"]
        if ramp["bars"] != 13 or len(ramp["zMoves"]) != 13:
            fail(f"ramp bars {ramp['bars']} / Z moves {len(ramp['zMoves'])}")
        if abs(ramp["zMin"] - (z - 1.5)) > 1e-6 or abs(ramp["zMax"] - (z + 1.5)) > 1e-6:
            fail(f"ramp Z band {ramp['zMin']}..{ramp['zMax']} around {z}")
        if ramp["zMoves"] != sorted(ramp["zMoves"]) or z not in ramp["zMoves"]:
            fail(f"ramp Z order {ramp['zMoves']}")
        if not ramp["audit"]["Ok"]:
            fail("ramp refused by the audit")
        if fo["couponWithZ"]["zMoves"] != [z] or fo["couponWithZ"]["lines"] != d["gcode"]["lines"] + 1:
            fail(f"coupon job with a focus height: {fo['couponWithZ']}")
        if fo["heightParse"] != {"HeightZ": z, "Distance": 123.4}:
            fail(f"height reply parse {fo['heightParse']}")
        # The bundled reference studies: every one read-only, every patch predicted and sourced,
        # no coupon carrying a test number, and the origin saying where it came from.
        seeds = d["seeds"]
        if len(seeds) < 3:
            fail(f"only {len(seeds)} seeds")
        for s in seeds:
            if not s["Reference"] or "literature" not in s["Origin"] and "bundled" not in s["Origin"]:
                fail(f"seed not a reference study: {s['Name']} / {s['Origin']}")
            if not s["coupons"]:
                fail(f"seed without coupons: {s['Name']}")
            for c in s["coupons"]:
                if c["Number"] != 0 or not c["Label"]:
                    fail(f"reference coupon with a test number or no label: {s['Name']} {c}")
                if c["patches"] == 0 or c["predicted"] != c["patches"] or c["sourced"] != c["patches"]:
                    fail(f"seed patches not all predicted and sourced: {s['Name']} {c}")
        pri = {p["MaterialName"]: p for p in d["priors"]}
        if pri["Stainless steel"]["EtaLow"] is None or pri["Brass"]["Grade"] != "E" or pri["Titanium"]["EtaLow"] is not None:
            fail(f"priors do not say what the literature says: {pri}")
    print(f"Ablation demo: {d['design']} settings on {d['layout']['Capacity']} squares, audit {a['maxJPerMm2']} J/mm2 "
          f"against {hottest} planned (ceiling {d['ceilingJPerMm2']}), {d['gcode']['lines']} lines of G-code, no Z moves; "
          f"focus ramp {ramp['bars']} bars Z {ramp['zMin']}..{ramp['zMax']}; {len(seeds)} reference seeds, all predicted and sourced.")


if __name__ == "__main__":
    main()
