#!/usr/bin/env bash
# Builds self-contained single-file DepthView binaries for every desktop target, then
# packs each into the zip a user downloads (dist/DepthView-<version>-<rid>.zip) with
# packaging/make_bundle.py - the same step the release workflow runs.
# Nothing needs to be installed on the target machine - the .NET runtime is inside
# the executable. All targets cross-compile from this one machine, but a macOS zip
# built anywhere except a Mac is unsigned and will not start on Apple silicon.
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

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)"
echo "DepthView $VERSION publish -> $OUT, zips -> $ROOT/dist"

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

    bin="$OUT/$rid/DepthView"
    case "$rid" in win-*) bin="$bin.exe" ;; esac
    python3 "$ROOT/packaging/make_bundle.py" --rid "$rid" --version "$VERSION" --binary "$bin" --dist "$ROOT/dist"
done

echo
echo "Done. Hand a user the zip for their platform from dist/."
