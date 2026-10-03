<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.8.0 — a tuning wizard, and a guide to tuning

**A tuning wizard.** **Tuning wizard…**, at the top of the Tune window, asks a few questions
about what you want the coin to be — which program will cut it, whether the background is a
surround or a cut-away floor, what to do with a rim drawn into the art, what should be full
depth and what should stay untouched, whether flat areas should engrave flat, how many
layers — and shows every answer on the picture as you make it, flat or as lit metal. Each
question shows what DepthView measured and why it matters, and one answer is marked
*Recommended*. That is a suggestion, never a decision: the wizard only sets the Tune window's
own controls, and **you have the final word**, there and afterwards.

**A tuning guide.** [docs/TUNING-GUIDE.md](https://github.com/LeeGillie/DepthView/blob/main/docs/TUNING-GUIDE.md)
tunes one coin for MakeIt and for LightBurn, change by change, then shows more coins with
different outcomes: a drawn rim kept or replaced, a surround or a floor, an export with grid
lines in its backdrop, and a picture of a coin that is not a depth map.

**New in the Tune window, and on the command line:**

- **Centre the blank on the design** (`--fit design`) for a coin drawn off-centre. It may
  crop background, never design, and still resamples nothing. DepthView says when a design
  sits off centre.
- **Replace the design's own rim** (`--cover-rim`). A rim drawn into the art goes under the
  new, untouched rim, so the coin's rim is the blank's own surface, with no trench beside it,
  and all of the depth goes to the design.
- **Even out a shaded surround** (`--uniform-surround`). A vignetted backdrop, or marks left
  by the program that exported the map, is made one level so none of it is taken for design.
- **Flat areas** (`--flat`). Nearly level areas can be flattened to one level or smoothed of
  pixel noise, so a floor meant to be flat does not engrave speckled.
- **Empty gaps.** When a few small pockets sit far below the rest of the design, the wizard
  offers to close the empty range between them rather than spend layers cutting nothing.
- `--survey` prints everything the wizard measures, and `--levels-from` takes its suggested
  level points.

**A picture is not a depth map.** A file that is mostly colour — a render, a tinted preview,
a photograph of a coin — is now reported as **NOT A DEPTH MAP** instead of being judged on
its few grey pixels. A batch report exits 1 for it, as it does for an imposter.

For other programs: `--survey --json` is new (schema `depthview.survey/1`), and
`depthview.tune/1` and `depthview.report/1` gain fields, none changed; see
[docs/INTEGRATION.md](https://github.com/LeeGillie/DepthView/blob/main/docs/INTEGRATION.md).

## Updating

**From 1.4.0 to 1.7.0:** the green bar will offer this release — click **Update now**.
When 1.8.0 starts, it offers these notes.

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

**The guides, as PDFs:** `DepthView-*-User-Guide.pdf` is the whole user guide and
`DepthView-*-Tuning-Guide.pdf` the tuning guide, for reading offline or printing. Both are
made from the pages on GitHub, which stay the authoritative version.

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
