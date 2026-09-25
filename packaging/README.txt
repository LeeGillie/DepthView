DepthView
=========

An Isotope NW tool. Free, open source (MIT), from
https://github.com/LeeGillie/DepthView

DepthView reports what a depth map actually contains - its real bit depth,
how many grey levels it truly uses, and whether a "16-bit" file is really
8-bit in disguise - then shows it as a lit 3D relief and helps you tune it
for laser engraving.

It runs entirely on your own computer. The only thing it ever sends is a
once-a-day question to GitHub asking whether a newer version exists (you
can turn that off). Nothing about you or your files goes anywhere.


WHAT'S IN THIS FOLDER
---------------------

  Windows   DepthView.exe            the program
  Mac       DepthView.app            the program
  Linux     DepthView                the program
            depthview.png            its icon
            install-menu-entry.sh    adds DepthView to your application menu
  All       README.txt               this file
            LICENSE.txt              the MIT licence

Nothing else is needed. The .NET runtime is built into the program, so
there is nothing to install first.

Keep the files together in this DepthView folder, somewhere you can write
to - Documents, your home folder, or a tools folder all work. Avoid
C:\Program Files on Windows: DepthView cannot update itself there.

UNZIP FIRST. On Windows, right-click the zip, choose "Extract All...", and
pick where the folder should go. Double-clicking a zip only shows what is
inside it, and the program cannot run from there.


STARTING IT
-----------

Windows   Double-click DepthView.exe. The first time, Windows may show a
          blue "Windows protected your PC" box, because the program is
          free and not code-signed. Click "More info", then "Run anyway".
          For a desktop shortcut: right-click DepthView.exe, choose
          Show more options > Send to > Desktop (create shortcut).

Mac       Drag DepthView.app into Applications if you like (or leave it in
          this folder), then double-click it. The first time, macOS will
          refuse because it is from an unidentified developer. Open
          System Settings > Privacy & Security, scroll down, click
          "Open Anyway" next to the message about DepthView, and confirm.
          If macOS instead says the app "is damaged", open Terminal and run
              xattr -dr com.apple.quarantine /path/to/DepthView.app
          then double-click it again. It is not damaged; that is how macOS
          words the quarantine on unsigned downloads.

Linux     Double-click DepthView in your file manager, or in a terminal
          in this folder run   ./DepthView
          To add it to your application menu, run   ./install-menu-entry.sh
          (./install-menu-entry.sh --remove takes it out again).

Drop a depth map on the window, click the drop area to browse, or paste an
image with Ctrl+V. Every control has a tooltip explaining what it does.

DepthView also works from a terminal - try  DepthView --help


UPDATES
-------

When DepthView starts it asks GitHub, at most once a day, whether a newer
version has been released. If there is one, a green bar under the title
says so:

  - "Update now" downloads the new version, checks it against the
    checksum GitHub publishes for it, starts it once to be sure it runs,
    then replaces the program in this folder and restarts. If any check
    fails, nothing is changed. Your settings and anything else you keep
    in this folder are left alone.
  - "What's new" opens the release notes.
  - "Skip this version" hides the bar until the next version comes out.

To stop checking, untick "Check for updates automatically" in About.
"Check for updates" in About asks straight away.

Close the tuning and relief windows before updating - the update restarts
DepthView, and anything unsaved in them would be lost.

To update by hand instead, download the zip for your computer from
https://github.com/LeeGillie/DepthView/releases/latest
and unzip it over this folder, replacing the files.

Which zip is yours:

  Windows 10/11, ordinary PC                  DepthView-...-win-x64.zip
  Windows on ARM (Surface Pro X, Snapdragon)  DepthView-...-win-arm64.zip
  Windows, 32-bit                             DepthView-...-win-x86.zip
  Mac with Apple silicon (M1 and later)       DepthView-...-osx-arm64.zip
  Mac with an Intel processor                 DepthView-...-osx-x64.zip
  Linux, ordinary PC                          DepthView-...-linux-x64.zip
  Linux on ARM (Raspberry Pi 4/5)             DepthView-...-linux-arm64.zip


CHECKING A DOWNLOAD
-------------------

DepthView is not code-signed, so the checksums are how you confirm a
download is the file the release actually built. SHA256SUMS.txt is attached
to every release:

  macOS / Linux   sha256sum -c SHA256SUMS.txt --ignore-missing
  Windows         certutil -hashfile DepthView-...-win-x64.zip SHA256
                  (compare with the line for that file by eye)

The in-place update does this check for you.


WHERE SETTINGS ARE KEPT
-----------------------

Your preferences (pass count, blank size and depth, update settings):

  Windows       %APPDATA%\DepthView\preferences.json
  Mac, Linux    ~/.config/DepthView/preferences.json

Saved material definitions (materials.json) sit next to the program: in
this folder on Windows and Linux, inside DepthView.app on a Mac. An update
keeps them.


REMOVING IT
-----------

Delete this folder (on a Mac, DepthView.app). To remove the settings as
well, delete the DepthView folder shown above. On Linux, run
./install-menu-entry.sh --remove first if you added the menu entry.


LICENCE
-------

MIT - see LICENSE.txt. Free to use, copy and share, with no warranty.
The program includes third-party components under their own licences;
About > Licence lists them.
