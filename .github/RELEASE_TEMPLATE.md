<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.6.0 — see what your laser is actually sent

**DepthView now reads G-code jobs.** Open a `.gc` file - browse to it, drop it on the window,
or run `DepthView --gcode <job.gc>` - and it reports what really reaches the machine, rather
than what the project or the depth map says:

- **How many distinct power levels the job uses.** This is the depth resolution the machine
  actually receives, however many grey levels the source map had.
- **How finely it samples**: the spacing of the scan lines, and how often the power changes
  along each line.
- **Every layer and cutting height**, and the direction each layer is scanned in.
- **The settings in MakeIt's own units** - power, speed, frequency, pulse width and line
  density - for each group of settings in the job, and the order the job switches between
  them, which is where a cleaning pass shows up.

MakeIt writes every job it sends to a G-code file on your computer; its knowledge base documents
`Ctrl+Shift+P` for getting at it. LightBurn G-code and gzipped files open too, and a 349 MB
relief job reads in a few seconds.

**How far the decoding is confirmed.** Power, speed, frequency, pulse width and line density
were read back against MakeIt 3.0.6's own settings panel for a colour test and for a 10-layer
relief job, and matched exactly. Other job types are assumed to work the same way, and every
report says so.

Two things it has already shown about MakeIt relief jobs: *Auto Planning* turns the scan
direction 27 degrees per layer, and power changes every 0.1 mm along a line.

For other programs, `--gcode --json` gives the same as a JSON document (schema
`depthview.gcode/1`, in [docs/INTEGRATION.md](https://github.com/LeeGillie/DepthView/blob/main/docs/INTEGRATION.md)).

## Updating

**From 1.4.0 or 1.5.0:** the green bar will offer this release — click **Update now**.

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
