#!/bin/sh
# Adds DepthView to your desktop's application menu, for your user only. Nothing needs root.
#
#   ./install-menu-entry.sh            add it (run again after moving this folder)
#   ./install-menu-entry.sh --remove   take it out again
#
# DepthView runs perfectly well without this - it only saves opening a terminal.

set -e
here=$(cd "$(dirname "$0")" && pwd)
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
entry="$apps/depthview.desktop"

if [ "$1" = "--remove" ]; then
    rm -f "$entry"
    echo "Removed $entry"
    exit 0
fi

if [ ! -x "$here/DepthView" ]; then
    echo "DepthView was not found next to this script ($here)." >&2
    exit 1
fi

mkdir -p "$apps"
cat > "$entry" <<DESKTOP
[Desktop Entry]
Type=Application
Name=DepthView
GenericName=Depth map inspector
Comment=What a depth map really contains, and how it will engrave
Exec="$here/DepthView" %f
Icon=$here/depthview.png
Terminal=false
Categories=Graphics;Utility;
MimeType=image/png;image/tiff;image/bmp;
DESKTOP
chmod 644 "$entry"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" >/dev/null 2>&1 || true

echo "DepthView is now in your application menu."
echo "If you move this folder, run this script again from its new place."
