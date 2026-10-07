#!/usr/bin/env bash
# Builds dist/Myra-linux-x64.tar.gz: a self-contained Linux build.
# libVLC and aria2 come from the system package manager (see build/linux/install.sh).
set -euo pipefail
cd "$(dirname "$0")/.."

OUT=dist/Myra-linux-x64
rm -rf "$OUT" "$OUT.tar.gz"
dotnet publish src/Myra.App/Myra.App.csproj -c Release -r linux-x64 --self-contained true \
  -p:DebugType=none -o "$OUT/app"

cp src/Myra.App/Assets/Myra.png "$OUT/myra.png"
cp build/linux/install.sh build/linux/uninstall.sh "$OUT/"
cp build/linux/README-Linux.txt "$OUT/README.txt"
chmod +x "$OUT/install.sh" "$OUT/uninstall.sh" "$OUT/app/Myra"

tar -C dist -czf "$OUT.tar.gz" Myra-linux-x64
du -sh "$OUT.tar.gz"
