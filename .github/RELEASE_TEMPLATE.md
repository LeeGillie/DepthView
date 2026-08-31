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
