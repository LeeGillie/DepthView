<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.3.0 â€” the project, not just the picture

A depth map on its own cannot tell you how big it will be. Forty millimetres or four
hundred, the PNG is identical â€” so "is this map finer than my spot can cut" has had no
answer, only an assumption. That answer lives in the laser project.

**DepthView now opens LightBurn `.lbrn2` projects.** Drop one on the window and it pulls out
the depth map, analyses it exactly as it would the image alone, and keeps what only the
project knows:

```
LightBurn project, 1 layer(s)  |  layer 0 Image, passes not stated  |  40 Ã— 40 mm on the blank
```

Every layer's cut settings are read â€” speed, power, passes, interval, image mode â€” and
**every one of them is allowed to be absent**. LightBurn omits any parameter sitting at its
default, so a missing pass count is not a layer that runs zero passes. The report prints `-`
rather than a number nobody wrote, because a pass count is what every depth figure gets
quoted against.

### What it cannot do yet

**Nothing is written back.** You can load a project, tune the depth map inside it, and save
the corrected greyscale image â€” that is the whole workflow today. DepthView cannot write a
new `.lbrn2`, and it cannot change a layer's speed, power or pass count.

That is deliberate rather than unfinished. Reading came first because a wrong read costs you
a misleading message, and a wrong write costs you your project file. Editing cut settings
and writing projects back is the plan; it is not in this release, and this page will say so
until it is.

### WeCreat `.wws`

Recognised, not parsed. The container opens with a four-byte `WWS2` magic and everything
after it is opaque â€” no field names, no XML, no JSON, no archive directory anywhere in three
megabytes â€” so it is compressed, encrypted, or both. **DepthView will not attempt to defeat
that**, and that decision is written into the code rather than merely promised here.

Going further needs WeCreat's help: how to find the depth-map object in a project, which
operation is bound to it, how to read its parameters, and how to write a change back leaving
everything else untouched. That has been requested from WeCreat support. **There has been no
answer yet**, and this section will be updated honestly either way. The reader sits behind
the same interface as the LightBurn one, so a schema drops straight in.

Until then: export the depth map from MakeIt and open the image directly. Every analysis and
tuning feature works on it.

---

## Lit 3D relief while you tune

The tuning dialog now shows both panes as a lit surface instead of grey, sharing one camera
and one light â€” so any difference you see between them is the tuning and nothing else. No
more saving a file and reloading it somewhere to find out what a change did.

**Both panes terrace, and both say so.** The pass count belongs to the job rather than to the
tuning, so it slices whichever file you send â€” the untuned one included. The headers read
*"Original, cut at 64 passes"* and *"Tuned, cut at 64 passes"*, and each pane reports how many
steps it actually gets. On the sample map that is 22 against 64 at the same pass count, which
is the argument this program exists to make, drawn rather than tabulated.

**Depth is now stated in millimetres.** Enter the depth you intend the deepest cut to reach,
and exaggeration becomes doublings around *that* â€” `true scale`, `4x`, `1/16`, down to a flat
surface at the bottom of the travel. The old default drew a 40 mm blank with 5 mm of relief,
deeper than the blank is thick, and labelled it `1.00x`.

Nothing in that section changes the file. The grey levels written when you save are identical
whatever the preview is doing, and the panel says so above every control in it.

## Smaller things

- The pass count is **remembered between runs**, defaulting to 256.
- `DepthView --project <file>` reports a project's layers from the command line.
- `DepthView --lb <command>` drives a running copy of LightBurn over its UDP interface.

## Fixed

- Redirecting the CLI to a file produced an empty file. A windowed executable starts with no
  valid standard handles, so .NET bound console output to a discarding writer before the
  console was attached.
- Embedded bitmaps opened upside down. LightBurn stores them bottom-up, its bed having Y
  increasing upward. Undone by reordering rows â€” never resampling, because resampling a depth
  map invents grey levels that were never in it.
- The relief preview box-averaged its height field, which turned a one-pixel terrace riser
  into a three-pixel ramp. Shading follows slope, so averaging was erasing the staircase the
  view exists to show â€” worst at low pass counts, where terracing matters most.
- Opening a project from the command line stranded the window open instead of reporting the
  problem.

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

**Windows** â€” double-click it.

**macOS and Linux** â€” mark it executable first:

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

If that trade is not one you want to make, build from source instead â€” it is two commands
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
picture encoded eight different ways â€” genuine 16-bit, two byte-widening fakes, a quantised
ladder, grey stored as RGB, honest 8-bit, wasted headroom, and colour contamination.

Drop them on DepthView in order. The image never changes and the verdict does, which is the
fastest way to understand what the program is for. The included `README.md` explains what
each one demonstrates.

---
