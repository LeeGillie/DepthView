<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.10.0 — before and after the laser

**The finishing preview.** A tuned coin is only half done when the laser stops. **Finishing**
in the Tune window takes it through the work that follows, for brass, copper and bronze:
clean away the oxide, pre-polish, darken, take the patina back from the high points, and
seal - stage by stage, on the coin you just tuned. Choose the product, its strength and its
time; choose the tool, and see the difference between a flat pad that keeps the recesses dark
and a soft buff that follows the surface down and strips them. Every choice carries its
advice, where the numbers come from and how sure they are, and its safety warnings: hover for
a hint, or open the guidance panel. It is a look, not a prediction, and nothing in it changes
the depth map.

**More to mark in the Tune window.** The checkbox for layer steps is now a **Mark** box, and
it offers four more, each measured at full resolution inside the blank:

- **Detail finer than the spot** - raised and recessed detail narrower than one or two spots,
  which the beam will round off or lose.
- **Pixel noise on smooth surfaces** - speckle the laser would cut faithfully.
- **Flattened peaks** - small flat tops below pure white, the clipped "flat nose tip".
- **Jagged edges** - diagonal and curved edges that jump a whole step in one pixel, the mark of
  a map drawn at its final size, which cuts curves as staircases. Built larger and reduced,
  the edges come out smooth; DepthView never resamples the map itself.

**In the analysis.** Flattened peaks and jagged edges are now findings in the main window.
So is a declared display curve: a PNG can say its values went through sRGB or a gamma curve,
and if they really did, the depth is bent. DepthView says what the curve would mean, Preview
can show the map as if it were undone, and the values are always used as stored.

**Depth along a line in the main window.** Drag across the picture (a click still browses)
to plot the depth along the line, at your saved blank, depth and pass count; the 3D preview
draws the same line on the surface in cyan.

For other programs: `--detail`, `--noise` and `--aliasing` are new (schemas
`depthview.detail/1`, `depthview.noise/1`, `depthview.aliasing/1`), `--survey` lists
`flatPeaks`, `depthview.report/1` gains `container.displayCurve`, `content.flatPeaks` and
`content.jaggedEdges`, and `--render --finish` renders a finished coin (with `--finish-maps`,
as texture maps for another renderer). Nothing existing changed. See
[docs/INTEGRATION.md](https://github.com/LeeGillie/DepthView/blob/main/docs/INTEGRATION.md).

## Updating

**From 1.4.0 to 1.9.0:** the green bar will offer this release — click **Update now**.
When 1.10.0 starts, it offers these notes.

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
