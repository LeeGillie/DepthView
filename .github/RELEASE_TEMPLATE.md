<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.13.0 — the ablation study meets the laser

> **Still a preview, one step further on.** 1.12.0's Ablation study had never touched a laser.
> In 1.13.0 its cutting side has: on a WeCreat Lumos Ultra (100 W MOPA) it found a verified
> focus and cut the first two brass study coupons, and everything that went wrong on the way
> is fixed here. The measuring side still waits for the depth gauge, so no removal figure has
> come out of it yet - do not plan a material study on it until a release says it measures.

**Precision focus - a new guide.** `docs/PRECISION-FOCUS.md` (with photos of real ramps) is
how to find the true focus height of your laser as a number the jobs use: cut a *focus ramp*
of numbered bars at different heights, read which run of bars is crisp, and record the middle
of it. It explains where the starting height really comes from (the `G0Z` in a MakeIt relief
job - not the focus-assist number), how to read a ramp (on brass the bars at focus are the
*darkest*), why it may take two or three scrap coins the first time, and why, once found, that
height holds for that lens and blank thickness. It also compares the method, honestly, with
focusing by eye or by the machine's own focus. On our machine the ramp put best focus about
2 mm away from where MakeIt focuses for depth jobs - not yet confirmed by measurement.

**Fixed from the first bench session:**

- The focus window accepts the machine's real height, which can be negative; a laser camera
  reply of all zeros is reported as "no measurement" instead of a height of 0.
- Stopping an armed focus ramp no longer reports it as cut, and lets you send again.
- The ramp's confirmation names the bed position it will cut at, as well as the heights -
  ramps go where the cut window's position says, not where the red pointer is.
- On a coin blank the test number ("DV 1") stays inside the blank's rim; it used to run off
  the edge.
- A patch planned right at the study's energy ceiling is no longer refused for the audit's own
  counting error; the audit now shows the ceiling, its limit and the reading.
- The focus window's advice on reading a ramp is corrected.

**Reference studies and settings from the evidence** (new since 1.12.0, still preview). An
empty ablation database offers studies gathered from published sources - vendor recipes,
a peer-reviewed stainless study, practitioner brass recipes - showing for each where it comes
from and how much weight it deserves before anything is added. The Tune window's **Settings
from the study...** (and `--suggest-settings`) then proposes MakeIt settings for a target depth
from the best evidence available, with its source and grade on every number and no guessing
beyond what was tested; for brass, copper and aluminium it says plainly that nothing measured
exists yet.

## Updating

**From 1.4.0 to 1.12.0:** the green bar will offer this release — click **Update now**.
When 1.13.0 starts, it offers these notes.

**From 1.3.0 or earlier:** those versions cannot update themselves. Download this release by
hand once; from then on, updates come to you.

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

**The guides:** inside the program, **Help** opens the user guide and the tuning guide for
your version. `DepthView-*-User-Guide.pdf` and `DepthView-*-Tuning-Guide.pdf` are the same
guides for reading offline or printing, made from the pages on GitHub, which stay the
authoritative version.

## Running it

**Windows** — double-click `DepthView.exe`.

**macOS** — double-click `DepthView.app` (drag it to Applications first if you like).

**Linux** — double-click `DepthView`, or run `./DepthView` in the folder.
`./install-menu-entry.sh` adds it to your application menu.

## Updating

Since 1.4.0, DepthView asks GitHub once a day whether a newer release exists and
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
