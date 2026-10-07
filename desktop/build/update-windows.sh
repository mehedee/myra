#!/usr/bin/env bash
# Rebuilds the Windows app from WSL and replaces an installed portable copy.
# Usage: build/update-windows.sh [install-dir]   (default: /mnt/d/tdw/Myra-win-x64)
# User data in %APPDATA%\Myra is not touched. The previous app folder is kept as <install-dir>.previous.
set -euo pipefail
cd "$(dirname "$0")/.."

INSTALL=${1:-/mnt/d/tdw/Myra-win-x64}
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1

command -v powershell.exe >/dev/null || { echo "powershell.exe not found: run this from WSL." >&2; exit 1; }
[ -f "$INSTALL/Myra.exe" ] || { echo "No Myra.exe in $INSTALL" >&2; exit 1; }

./build/package-windows.sh
NEW=dist/Myra-win-x64
[ -f "$NEW/Myra.exe" ] || { echo "Build output is missing Myra.exe" >&2; exit 1; }

WIN_INSTALL=$(wslpath -w "$INSTALL")
echo "Closing Myra…"
powershell.exe -NoProfile -Command "\$p = Get-Process Myra -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$WIN_INSTALL*' }; if (\$p) { \$p | ForEach-Object { \$_.CloseMainWindow() | Out-Null }; Start-Sleep 5; \$p | Where-Object { -not \$_.HasExited } | Stop-Process -Force }" || true
# aria2c is started by Myra with --stop-with-process; give it time to exit.
sleep 2

rm -rf "$INSTALL.previous"
cp -a "$INSTALL" "$INSTALL.previous"
rsync -a --delete "$NEW/" "$INSTALL/"

echo "Starting Myra…"
powershell.exe -NoProfile -Command "Start-Process -FilePath '$WIN_INSTALL\\Myra.exe' -WorkingDirectory '$WIN_INSTALL'"
echo "Updated $INSTALL ($(git describe --always --dirty)). Previous copy: $INSTALL.previous"
