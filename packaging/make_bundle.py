#!/usr/bin/env python3
"""Turn one published DepthView binary into the zip a user downloads.

    python packaging/make_bundle.py --rid win-x64 --version 1.4.0 --binary publish/win-x64/DepthView.exe

Every platform gets the same shape - one folder called DepthView, zipped:

    DepthView-<version>-<rid>.zip
        DepthView/
            DepthView.exe            Windows: the program (the icon is inside the .exe)
            DepthView                Linux: the program, executable
            depthview.png            Linux: icon for the menu entry
            install-menu-entry.sh    Linux: adds DepthView to the application menu (optional)
            DepthView.app/           macOS: a real application bundle, so it double-clicks
            README.txt               how to start it, update it and remove it
            LICENSE.txt              MIT - it must travel with every copy

The folder name and the zip name are a contract with the in-place updater
(src/DepthView/Updates/UpdateService.cs): it looks for DepthView-<version>-<rid>.zip on the
release and refuses a zip whose entries are not all under DepthView/. Change one, change both.

The zip is written by hand rather than with a shell tool so the program keeps its executable
bit even when the zip is built on Windows, and so every platform's zip is laid out identically.

On a Mac with codesign available the .app is signed ad hoc (--sign -). Apple silicon refuses
to run unsigned native code at all, so a macOS bundle built anywhere else will not start on an
M-series Mac; the release workflow builds the macOS zips on a macOS runner for that reason.
Standard library only.
"""

from __future__ import annotations

import argparse
import os
import shutil
import stat
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGING = os.path.join(ROOT, "packaging")
FOLDER = "DepthView"

INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>                <string>DepthView</string>
  <key>CFBundleDisplayName</key>         <string>DepthView</string>
  <key>CFBundleIdentifier</key>          <string>io.github.leegillie.depthview</string>
  <key>CFBundleExecutable</key>          <string>DepthView</string>
  <key>CFBundleIconFile</key>            <string>DepthView.icns</string>
  <key>CFBundlePackageType</key>         <string>APPL</string>
  <key>CFBundleShortVersionString</key>  <string>{version}</string>
  <key>CFBundleVersion</key>             <string>{version}</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key>      <string>12.0</string>
  <key>NSHighResolutionCapable</key>     <true/>
  <key>NSHumanReadableCopyright</key>    <string>Copyright (c) Lee Gillie. MIT licence.</string>
</dict>
</plist>
"""


def write_text(src: str, dst: str, newline: str) -> None:
    with open(src, "r", encoding="utf-8") as fh:
        text = fh.read().replace("\r\n", "\n")
    with open(dst, "w", encoding="utf-8", newline="") as fh:
        fh.write(text.replace("\n", newline))


def make_executable(path: str) -> None:
    os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def stage(rid: str, version: str, binary: str, folder: str) -> set[str]:
    """Lay the bundle out in ``folder``. Returns the relative paths that must be executable."""
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    executables: set[str] = set()
    windows_text = rid.startswith("win-")
    newline = "\r\n" if windows_text else "\n"

    if rid.startswith("win-"):
        shutil.copy2(binary, os.path.join(folder, "DepthView.exe"))
    elif rid.startswith("osx-"):
        contents = os.path.join(folder, "DepthView.app", "Contents")
        os.makedirs(os.path.join(contents, "MacOS"))
        os.makedirs(os.path.join(contents, "Resources"))
        exe = os.path.join(contents, "MacOS", "DepthView")
        shutil.copy2(binary, exe)
        make_executable(exe)
        executables.add("DepthView.app/Contents/MacOS/DepthView")
        shutil.copy2(os.path.join(PACKAGING, "DepthView.icns"), os.path.join(contents, "Resources", "DepthView.icns"))
        with open(os.path.join(contents, "Info.plist"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(INFO_PLIST.format(version=version))
        with open(os.path.join(contents, "PkgInfo"), "w", encoding="ascii", newline="") as fh:
            fh.write("APPL????")
    elif rid.startswith("linux-"):
        exe = os.path.join(folder, "DepthView")
        shutil.copy2(binary, exe)
        make_executable(exe)
        executables.add("DepthView")
        shutil.copy2(os.path.join(ROOT, "src", "DepthView", "Assets", "depthview-icon-256.png"),
                     os.path.join(folder, "depthview.png"))
        script = os.path.join(folder, "install-menu-entry.sh")
        write_text(os.path.join(PACKAGING, "install-menu-entry.sh"), script, "\n")
        make_executable(script)
        executables.add("install-menu-entry.sh")
    else:
        raise SystemExit("Unknown runtime identifier: " + rid)

    write_text(os.path.join(PACKAGING, "README.txt"), os.path.join(folder, "README.txt"), newline)
    write_text(os.path.join(ROOT, "LICENSE"), os.path.join(folder, "LICENSE.txt"), newline)
    return executables


def sign_mac(folder: str) -> bool:
    """Ad-hoc sign the .app when this machine can. Returns whether it did."""
    app = os.path.join(folder, "DepthView.app")
    if sys.platform != "darwin" or shutil.which("codesign") is None:
        return False
    subprocess.run(["codesign", "--force", "--sign", "-", "--timestamp=none", app], check=True)
    subprocess.run(["codesign", "--verify", "--strict", app], check=True)
    return True


def write_zip(folder: str, archive: str, executables: set[str]) -> None:
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for base, dirs, files in os.walk(folder):
            dirs.sort()
            for name in sorted(files):
                path = os.path.join(base, name)
                rel = os.path.relpath(path, folder).replace(os.sep, "/")
                info = zipfile.ZipInfo(FOLDER + "/" + rel, time.localtime(os.path.getmtime(path))[:6])
                info.compress_type = zipfile.ZIP_DEFLATED
                # Signed code must keep its bits exactly, and the executable bit has to survive
                # a zip built on Windows, so the mode is stated rather than read from disk.
                mode = 0o755 if rel in executables else 0o644
                info.external_attr = (stat.S_IFREG | mode) << 16
                info.create_system = 3   # Unix, so unzip honours the mode
                with open(path, "rb") as fh:
                    zf.writestr(info, fh.read())


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--rid", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--binary", required=True, help="the published DepthView or DepthView.exe")
    ap.add_argument("--dist", default=os.path.join(ROOT, "dist"))
    args = ap.parse_args()

    version = args.version[1:] if args.version[:1] in "vV" else args.version
    staging = os.path.join(args.dist, "staging-" + args.rid, FOLDER)
    executables = stage(args.rid, version, args.binary, staging)

    signed = sign_mac(staging) if args.rid.startswith("osx-") else False
    if args.rid.startswith("osx-") and not signed:
        print("warning: %s built without codesign - it will not start on Apple silicon. "
              "Release macOS zips from a Mac (the release workflow does)." % args.rid, file=sys.stderr)

    os.makedirs(args.dist, exist_ok=True)
    archive = os.path.join(args.dist, "DepthView-%s-%s.zip" % (version, args.rid))
    write_zip(staging, archive, executables)
    shutil.rmtree(os.path.dirname(staging), ignore_errors=True)

    shown = os.path.relpath(archive, ROOT) if os.path.abspath(archive).startswith(ROOT + os.sep) else archive
    print("%-40s %8.1f MB%s" % (shown, os.path.getsize(archive) / 1048576,
                                "  (signed ad hoc)" if signed else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
