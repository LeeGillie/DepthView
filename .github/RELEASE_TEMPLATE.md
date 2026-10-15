<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.12.0 — the ablation study, framed in

> **Please read this first: the Ablation study is framed in, not yet functional.** The new
> **Ablation study** button opens a complete-looking section - studies, coupons, test numbers,
> the cut window, the focus window - and none of it has yet cut a single coupon. Nothing in it
> has been run against a laser. It is released now so the shape of it can be seen and talked
> about, and so this version draws a line before the bench work starts. Treat every window
> behind that button as a preview: do not plan a material study on it, and do not send a job
> from it expecting it to be right. The rest of DepthView is unaffected.

**What the ablation study is for.** DepthView's tuning and depth checks rest on what a laser
removes per pass at given settings - a number nobody publishes for a given alloy, source and
lens. The ablation study is the part of DepthView that will measure it: a study is one material
and alloy on one laser (settings are recorded against the source's rating in watts, because
50 % of a 60 W source is not 50 % of a 100 W one), on coupons of one exact size - a sheet or a
coin blank of a given thickness. The study projects how many identical coupons a design needs
before anything is committed; each coupon carries its test number as a QR code and digits, so
the later measuring step knows which settings made which patch; numbers are never reused, and a
failed coupon is retired rather than renumbered. Studies export to a file to share and import
from one, and a study can be carried from the 60 W to the 100 W MOPA source (or back) with
every setting matched by watts - a prediction to verify, labelled as such.

**Focus first.** Cutting a coupon begins with a guided focus - focus the machine as for any
job, read its height, and the first time for each lens and thickness cut a small focus ramp on
scrap to find and verify the sharpest height - and the focus is recorded with every coupon.
Not yet exercised on a machine: the height reading in particular is unconfirmed, which is why
nothing puts a Z move into a job unless you have seen a ramp prove it.

**Not built yet:** entering the depth readings. The measure window waits for the depth gauge.

**Under the bonnet:** the code that talks to the Lumos Ultra - the job writer, the energy
audit, the bed camera and its calibration - is now shared with MOPAChroma Atlas as a set of
libraries, so both programs send jobs and place them on the camera's picture the same way,
and a camera calibration made in one serves the other. DepthView now builds on .NET 10; the
zips still carry the runtime inside them, so nothing changes for installing or updating.

## Updating

**From 1.4.0 to 1.11.0:** the green bar will offer this release — click **Update now**.
When 1.12.0 starts, it offers these notes.

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
