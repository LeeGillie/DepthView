#!/usr/bin/env bash
# Builds self-contained single-file DepthView binaries for every desktop target.
# Nothing needs to be installed on the target machine - the .NET runtime is inside
# the executable. All targets cross-compile from this one machine.
#
#   ./publish.sh
#   ./publish.sh win-x64 linux-x64

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/src/DepthView/DepthView.csproj"
OUT="$ROOT/publish"

if [ "$#" -gt 0 ]; then
    RIDS=("$@")
else
    RIDS=(win-x64 win-x86 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

echo "DepthView publish -> $OUT"

for rid in "${RIDS[@]}"; do
    echo
    echo "=== $rid ==="
    dotnet publish "$PROJ" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none \
        -o "$OUT/$rid" \
        --nologo -v quiet

    find "$OUT/$rid" -maxdepth 1 -type f \( -name 'DepthView' -o -name 'DepthView.exe' \) \
        -exec ls -lh {} \; | awk '{printf "  %-16s %s\n", $NF, $5}'
done

echo
echo "Done. Hand a user the single file from publish/<their platform>/."
echo "On macOS and Linux they will need: chmod +x DepthView"
