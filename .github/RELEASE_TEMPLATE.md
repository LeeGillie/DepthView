<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.4.0 — install it once, and it keeps itself current

**Every platform now downloads as one zip.** Unzip it and there is a `DepthView` folder: the
program, a `README.txt` that walks through starting, updating and removing it, and the licence.
On a Mac the program is a proper `DepthView.app`, built and signed on a Mac so Apple silicon
will run it. On Linux there is an optional script that adds DepthView to your application menu.
No more `chmod +x`, no more bare files named after their platform.

**DepthView now updates itself.** Once a day it asks GitHub whether a newer release exists, and
if one does, a green bar says so. **Update now** downloads the zip for your computer, checks it
against the SHA-256 GitHub publishes for it, starts the new copy once to make sure it runs and
reports the right version, and only then replaces the program and restarts. If any check fails,
nothing is changed. **Skip this version** hides the bar until the next one; *About → Check for
updates* asks straight away, and the switch beside it turns checking off. Nothing is sent but the
request itself.

**1.3.0 and earlier cannot update themselves** — they have no updater. Download this release by
hand once; from here on, updates come to you. Your saved pass count carries over.

---

## True scale, everywhere

The 3D relief window and `--render` now draw depth the same way the tuning dialog does: a blank
diameter and a target depth in millimetres, with exaggeration in doublings around it, opening at
**true scale** — Z in the same millimetres as X and Y. The slider, its label and a badge on the
picture shade from green at true scale through yellow (2×) to red (8× and beyond, "inspection
only"), so a magnified view is never mistaken for the real thing.

*Correction to the 1.3.0 notes, which said the old exaggeration had been fixed: only the tuning
dialog had been. The relief window and `--exag` still drew about 5 mm of relief on a 40 mm blank
and called it 1.0. They are fixed now.* **Scripts that pass `--exag` change meaning:** it is now
doublings, so `--exag 2` draws 4×, and 0 is true scale.

## Your blank, once

Blank diameter, **thickness** and target depth are now one set of numbers shared by every
window: change one in the tuning dialog or the relief window and both move, and they are
remembered between runs. Target depth starts at 18% of the thickness and follows it until you
type your own; it warns when little floor would be left under the deepest cut. The tuning card
adds **depth per pass** — the target depth over your pass count — and the 3D view stands the
relief on a slab of the real thickness.

## A mass-loss calibration coupon

`DepthView --calibrate --mass` writes a coupon for anyone with a milligram scale: one uniform
zone per coupon, no engraved labels (anything engraved counts as removed mass), and a worksheet
with the controls and the arithmetic from weight lost to removal per joule. The depth-wedge
coupon's comb no longer draws gaps finer than the image can represent under a label that claims
they are there; it leaves them blank and says what size would draw them.

## Smaller things

- `DepthView --version`, `--check-update` and `--update` from a terminal.
- `--thick` alongside `--blank` and `--depth-mm` for `--render`, `--relief` and `--tune-ui`.
- The Visual Studio solution now shows the docs, workflows, packaging and tests.

---

## Which file do I want?

| You are on | Download |
|---|---|
| Windows 10/11, ordinary PC | `DepthView-*-win-x64.zip` |
| Windows on ARM (Surface Pro X, Snapdragon) | `DepthView-*-win-arm64.zip` |
| Windows, 32-bit | `DepthView-*-win-x86.zip` |
| Mac with Apple silicon (M1 and later) | `DepthView-*-osx-arm64.zip` |
| Mac with an Intel processor | `DepthView-*-osx-x64.zip` |
| Linux, ordinary PC | `DepthView-*-linux-x64.zip` |
| Linux on ARM (Raspberry Pi 4/5, ARM server) | `DepthView-*-linux-arm64.zip` |

Each zip holds one **DepthView** folder: the program with the .NET runtime inside it, a
`README.txt` that walks through starting, updating and removing it, and the licence. On a Mac
the program is a proper `DepthView.app`; on Linux there is an optional script that adds it to
your application menu. No installer, no dependencies, no administrator rights. Delete the
folder and DepthView is gone.

**Unzip it first**, somewhere you can write to — Documents or your home folder, not
`C:\Program Files`. On Windows, right-click the zip and choose *Extract All…*.

## Running it

**Windows** — double-click `DepthView.exe`.

**macOS** — double-click `DepthView.app` (drag it to Applications first if you like).

**Linux** — double-click `DepthView`, or run `./DepthView` in the folder.
`./install-menu-entry.sh` adds it to your application menu.

## Updating

From this version on, DepthView asks GitHub once a day whether a newer release exists and
shows a green bar when there is one. **Update now** downloads the zip for your computer,
checks it against the checksum GitHub publishes, starts the new copy once to be sure it
runs, then replaces the program and restarts. If any check fails, nothing is changed. It
can be turned off in About, and nothing but the request itself is ever sent.

## These builds are not code-signed

DepthView is a free tool and there is no certificate behind it, so your operating system
will treat it as software from an unidentified developer. That is expected, and it is worth
knowing exactly what you will see rather than being surprised by it.

**Windows** shows a blue *"Windows protected your PC"* SmartScreen dialog. Click
**More info**, then **Run anyway**.

**macOS** refuses the first time. Open *System Settings → Privacy & Security*, scroll down
and click **Open Anyway** next to the message about DepthView. If it instead claims the app
is damaged, that is how macOS words the quarantine on unsigned downloads:

```
xattr -dr com.apple.quarantine DepthView.app
```

The app is signed *ad hoc* — enough for Apple silicon to run it, not a notarised identity.

**Linux** does not object.

If that trade is not one you want to make, build from source instead — it is two commands
and the repository explains them.

## Checking what you downloaded

Because these are unsigned, the checksums are the only way to confirm a download is the
file this release actually built. `SHA256SUMS.txt` is attached. (The in-place updater does
this check for you.)

```
sha256sum -c SHA256SUMS.txt --ignore-missing        # macOS and Linux
certutil -hashfile DepthView-*-win-x64.zip SHA256   # Windows, compare by eye
```

## Start here

Download **`DepthView-samples.zip`** as well. It holds eight depth maps that are the same
picture encoded eight different ways — genuine 16-bit, two byte-widening fakes, a quantised
ladder, grey stored as RGB, honest 8-bit, wasted headroom, and colour contamination.

Drop them on DepthView in order. The image never changes and the verdict does, which is the
fastest way to understand what the program is for. The included `README.md` explains what
each one demonstrates.

---
