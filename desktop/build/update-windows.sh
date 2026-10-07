#!/usr/bin/env bash
# Rebuilds the Windows app from WSL and installs or updates a portable copy.
# Usage: build/update-windows.sh [install-dir]   (default: /mnt/d/tdw/Myra-win-x64)
# - An existing install folder is kept as <install-dir>.previous before it is replaced.
# - On the first run, when %APPDATA%\Myra does not exist yet but a former SamBD profile does,
#   the SamBD profile is copied with build/migrate-profile.py. The original stays untouched.
set -euo pipefail
cd "$(dirname "$0")/.."

INSTALL=${1:-/mnt/d/tdw/Myra-win-x64}
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1

command -v powershell.exe >/dev/null || { echo "powershell.exe not found: run this from WSL." >&2; exit 1; }
ps() { powershell.exe -NoProfile -Command "$1" | tr -d '\r'; }

# Closes every running copy of an executable name. The app can take ~15 s to stop background
# index work, so it is only force-stopped after 30 s. aria2c exits with it (--stop-with-process).
close_app() {
  ps "\$p = Get-Process $1 -ErrorAction SilentlyContinue; if (\$p) { Write-Host \"Closing $1…\"; \$p | ForEach-Object { \$_.CloseMainWindow() | Out-Null }; \$p | ForEach-Object { \$_.WaitForExit(30000) | Out-Null }; \$p | Where-Object { -not \$_.HasExited } | Stop-Process -Force }" || true
  sleep 2
}

./build/package-windows.sh
NEW=dist/Myra-win-x64
[ -f "$NEW/Myra.exe" ] || { echo "Build output is missing Myra.exe" >&2; exit 1; }

close_app Myra

APPDATA_DIR=$(wslpath "$(ps '[Environment]::GetFolderPath("ApplicationData")')")
if [ ! -e "$APPDATA_DIR/Myra" ] && [ -f "$APPDATA_DIR/SamBD/SamBD.json" ]; then
  echo "Copying the SamBD profile to $APPDATA_DIR/Myra (SamBD's own profile is not changed)."
  close_app SamBD
  python3 -I build/migrate-profile.py "$APPDATA_DIR/SamBD" "$APPDATA_DIR/Myra" --store-filename SamBD.json
fi

if [ -d "$INSTALL" ]; then
  rm -rf "$INSTALL.previous"
  cp -a "$INSTALL" "$INSTALL.previous"
fi
mkdir -p "$INSTALL"
rsync -a --delete "$NEW/" "$INSTALL/"

WIN_INSTALL=$(wslpath -w "$INSTALL")
echo "Starting Myra…"
ps "Start-Process -FilePath '$WIN_INSTALL\\Myra.exe' -WorkingDirectory '$WIN_INSTALL'"
echo "Installed $INSTALL ($(git describe --always --dirty))."
if [ -d "$INSTALL.previous" ]; then echo "Previous copy: $INSTALL.previous"; fi
