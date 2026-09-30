<!-- Update the "What's new" section below for each release. Everything after it is
     evergreen and should not need touching. gh release create puts this file first and the
     generated commit list after it, so this is what a reader sees at the top of the page. -->

## What's new in 1.7.0 — release notes in the program, and cleaning layers in G-code

**DepthView now tells you what changed when it updates.** The first time a new version
starts, the green bar says so and **What's new** opens that version's release notes in the
program. They are built in, so this works offline. You can read them again any time from
**About → What's new**, or print them with `DepthView --whats-new`.

**G-code reports now show repeated layers and cleaning layers.** Open a `.gc` job and a new
section, *Repeated layers and cleaning*, lists:

- **Layers that are exact copies of the layer before them**, at the same height, and how
  often they recur.
- **Layers that run at settings of their own**, and how often.
- **Layers that move to a cutting height and burn nothing.**

Turn on MakeIt's *Cleaning Layer* and this is where the cleaning layers show up, along with
the power, speed, frequency, pulse width and line density the file actually runs them at.

For other programs, the same findings are in `--gcode --json` as a new `layerChecks` object
(schema `depthview.gcode/1`, fields added, none changed; see
[docs/INTEGRATION.md](https://github.com/LeeGillie/DepthView/blob/main/docs/INTEGRATION.md)).

## Updating

**From 1.4.0, 1.5.0 or 1.6.0:** the green bar will offer this release — click **Update now**.
When 1.7.0 starts, it offers these notes.

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
