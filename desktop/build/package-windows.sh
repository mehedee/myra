#!/usr/bin/env bash
# Builds dist/Myra-win-x64.zip: a self-contained Windows build with libVLC and aria2c.exe.
# Runs on Linux, macOS or Windows (Git Bash). Needs the .NET 10 SDK, curl, unzip and zip.
set -euo pipefail
cd "$(dirname "$0")/.."

ARIA2_VERSION=1.37.0
ARIA2_ZIP=aria2-$ARIA2_VERSION-win-64bit-build1.zip
ARIA2_SHA256=67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288
OUT=dist/Myra-win-x64

mkdir -p build/cache dist
if [ ! -f "build/cache/$ARIA2_ZIP" ]; then
  curl -fL --retry 3 -o "build/cache/$ARIA2_ZIP" \
    "https://github.com/aria2/aria2/releases/download/release-$ARIA2_VERSION/$ARIA2_ZIP"
fi
echo "$ARIA2_SHA256  build/cache/$ARIA2_ZIP" | sha256sum -c -

rm -rf "$OUT" "$OUT.zip"
dotnet publish src/Myra.App/Myra.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishReadyToRun=false -p:DebugType=none -o "$OUT"

mkdir -p "$OUT/tools" "$OUT/licenses"
unzip -q -j -o "build/cache/$ARIA2_ZIP" "*/aria2c.exe" -d "$OUT/tools"
unzip -q -j -o -p "build/cache/$ARIA2_ZIP" "*/COPYING" > "$OUT/licenses/aria2-COPYING.txt"
cp LICENSE-VLC.txt "$OUT/licenses/VLC-LGPL-2.1.txt"
cp build/README-Windows.txt "$OUT/README.txt"

(cd dist && zip -qr "Myra-win-x64.zip" "Myra-win-x64")
ls -la "$OUT/Myra.exe" "$OUT/tools/aria2c.exe" "$OUT/libvlc/win-x64/libvlc.dll"
du -sh "$OUT.zip"
