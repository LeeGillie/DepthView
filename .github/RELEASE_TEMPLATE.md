<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.2.0 — DepthView now fixes what it finds

Until now this tool told you a depth map was wrong and left you there. **Tune…** is the
other half.

**Set the two level points against the histogram.** Everything at or below the black point
becomes one uniform full depth — which is how a noisy floor stops engraving mottled and
starts coming out polished. Everything at or above the white point is left untouched, and
the shaded ends of the plot show exactly which pixels you are giving up. On the
`07-wasted-headroom` sample that takes **88 distinct depths to 255 at 256 passes**, and
range use from 34% to 100%.

Worth being precise about what that buys, because the first cut of these notes was not and
was corrected on the LightBurn forum: stretching a map's range **makes the relief deeper**.
It gets more depths because it is deeper, not because resolution was being wasted — levels
per unit of depth are unchanged. If a narrow range was deliberate, stretching overrides that
intent. DepthView reports what the file asks for and what changing it would mean, and leaves
the decision where it belongs.

**Leave an untouched rim for a coin blank**, in millimetres, because that is how a rim is
known — with calipers. If the artwork runs past it you are told what it costs: the
percentage of the design that overlaps, and the scale that would clear it.

**Fit artwork inside the rim without resampling it.** Rather than scaling the design down —
which interpolates, and invents grey levels that were never in the file — the canvas grows
and the original pixels are copied into the middle of it, byte for byte. Same physical
result on a 40 mm blank, with the data intact.

**A new `band spread` column**, and it is worth reading even if you never tune anything. A
slicer cuts the level range into equal bands; levels are integers, so unless the pass count
divides the range exactly, some bands hold more levels than others and terraces come out
unevenly spaced. With 256 levels only eight pass counts out of 255 divide evenly — the
powers of two — while 127 give a 2:1 spread. A genuine 16-bit map never exceeds 1.004
anywhere in that range. **An imposter inherits the 8-bit behaviour**, so it costs you even
spacing as well as depth count. Raised by Nathaniel Klumb on the LightBurn forum.

**An alignment outline.** An image frames as its bounding rectangle in LightBurn whatever is
drawn inside it, so a round design in a square PNG always frames as a square. `--outline`
writes a true-size SVG circle to put on a tool layer and frame against the rim of the blank.

**A calibration coupon** (`--calibrate`): a depth wedge, ramps at known wall angles and a
comb of shrinking gaps, sized to your blank, plus a bench worksheet. Engrave it once per
machine and material and the numbers you tune against are yours rather than someone else's.

Nothing in any of this writes over your original file. Every path produces a new one.

---

## Which file do I want?

| You are on | Download |
|---|---|
| Windows 10/11, ordinary PC | `DepthView-*-win-x64.exe` |
| Windows on ARM (Surface Pro X, Snapdragon) | `DepthView-*-win-arm64.exe` |
| Windows, 32-bit | `DepthView-*-win-x86.exe` |
| Mac with Apple silicon (M1 and later) | `DepthView-*-osx-arm64` |
| Mac with an Intel processor | `DepthView-*-osx-x64` |
| Linux, ordinary PC | `DepthView-*-linux-x64` |
| Linux on ARM (Raspberry Pi 4/5, ARM server) | `DepthView-*-linux-arm64` |

**There is nothing to install.** Each file is the whole program with the .NET runtime
inside it. No installer, no dependencies, no runtime to add first. Delete the file and
DepthView is gone.

## Running it

**Windows** — double-click it.

**macOS and Linux** — mark it executable first:

```
chmod +x DepthView-*-osx-arm64
./DepthView-*-osx-arm64
```

## These binaries are not code-signed

DepthView is a free tool and there is no certificate behind it, so your operating system
will treat it as software from an unidentified developer. That is expected, and it is worth
knowing exactly what you will see rather than being surprised by it.

**Windows** shows a blue *"Windows protected your PC"* SmartScreen dialog. Click
**More info**, then **Run anyway**.

**macOS** is stricter. A downloaded unsigned binary is quarantined, and the error message
("cannot be opened because the developer cannot be verified", or on newer versions a claim
that the file is damaged) does not tell you that quarantine is the cause. Clear it:

```
xattr -d com.apple.quarantine DepthView-*-osx-arm64
```

**Linux** does not object.

If that trade is not one you want to make, build from source instead — it is two commands
and the repository explains them.

## Checking what you downloaded

Because these are unsigned, the checksums are the only way to confirm a download is the
file this release actually built. `SHA256SUMS.txt` is attached.

```
sha256sum -c SHA256SUMS.txt --ignore-missing        # macOS and Linux
certutil -hashfile DepthView-*-win-x64.exe SHA256   # Windows, compare by eye
```

## Start here

Download **`DepthView-samples.zip`** as well. It holds eight depth maps that are the same
picture encoded eight different ways — genuine 16-bit, two byte-widening fakes, a quantised
ladder, grey stored as RGB, honest 8-bit, wasted headroom, and colour contamination.

Drop them on DepthView in order. The image never changes and the verdict does, which is the
fastest way to understand what the program is for. The included `README.md` explains what
each one demonstrates.

---
