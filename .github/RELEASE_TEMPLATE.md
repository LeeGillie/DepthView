<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.14.0 — engraving settings, for any shape and any material

> **The ablation study is still a preview**, but it now measures: readings are entered by hand
> (the gauge interface comes later), and a real job's result counts as evidence. Nothing in
> this release has been measured on a Lumos Ultra yet - the first measured coupons are the
> next bench session - so the bundled figures are still what other people reported, graded as
> such, until your own numbers replace them.

**Engraving settings, on the front page.** A green **Engraving settings** button at the top
right of the main window gives you settings for a material on your laser - no depth map
needed. Pick the material, the laser (60 W or 100 W MOPA, or the UV module), the lens, the
target depth, the number of passes and the program you type into, and you get a complete
sheet in that program's words and units - MakeIt relief, LightBurn grayscale or a slicer:
power, speed, line density, frequency, pulse width, layers, descent per layer, cleaning layer.
Every line is marked with what it rests on: *evidence* at its grade, *yours*, *fixed*, or
**DEFAULT** in amber when it is not derived from this material or laser at all. The descent
per layer in particular is never a bare number: without an ablation rate for your material it
is WeCreat's 0.01 mm default and the sheet says so. **Copy the recipe** puts the sheet on the
clipboard. The Tune window's JOB section opens the same window with that job's figures. The
README has a new **How to get engraving settings** section walking through all of it.

**Record what this job did.** Once a job has run and you have measured how deep it went,
one button in the settings window records the settings it ran with and the depth it reached.
That becomes grade-A evidence - measured on this bench - in a *Jobs - material on laser*
study, and the next time you ask for settings for that material the advice rests on your own
figure ahead of every reported one. Two or three jobs at different speeds give it something
to interpolate between. This is how the advice is corrected after the settings were used, and
how a material with no evidence at all gets its first number.

**The ablation study measures (by hand).** **Measure...** on an engraved coupon opens one box
per patch - micrometres from the untouched surface, several readings per box - and saving
them makes those patches grade-A evidence; the same dialog edits readings later. **Delete
coupon record** removes a record and its readings for good (a failed coupon is still
*retired*, not deleted, so its number and record stay). The study list shows every study's
status in the words the advice uses - reported recipes, depths and grades for a reference
study; coupons planned, cut and measured for your own; results recorded for a Jobs study -
and marks imported studies with where they came from. If the settings advice finds the
bundled reference studies missing from your database (they were only ever offered to an
empty one), it says so and offers to add them right there.

**Every shape, every material.** The workpiece need not be a round coin: a rectangular plate
or tag (with corner radius and holes), or any outline from an SVG or a mask image, and the
design is fitted, previewed, finished and reported on that shape. The Blank panel asks for the
material from a catalogue of sixteen (`--materials` prints it), each carrying what a fibre,
UV, CO2 or diode source can do to it - *works*, *marginal* or *cannot*, in words under the
Laser box, in the job report, and in the settings advice, which refuses a pairing the source
cannot do rather than inventing a recipe. Lenses are a list everywhere one is asked for: the
Lumos Ultra's own first, third-party f-theta lenses under their own heading, or type your own.

**Also:** a second literature sweep (`docs/research/laser-ablation-sweep-2.md`) found graded
removal figures for bronze, copper, titanium, mild steel and more stainless, and still none in
readable text for brass or for UV on any metal - the rows it marks fit to seed are the next
additions to the bundled set. Cleaning layers are noted as working from MakeIt 3.0.7. The JSON
interfaces gained the workpiece's shape and material (`docs/INTEGRATION.md`).

## Updating

**From 1.4.0 to 1.13.0:** the green bar will offer this release — click **Update now**.
When 1.14.0 starts, it offers these notes.

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
