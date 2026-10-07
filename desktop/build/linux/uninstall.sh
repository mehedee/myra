#!/usr/bin/env bash
# Removes the app and its menu entry. Your sources, index and downloads are kept
# (~/.config/Myra and ~/Downloads/Myra); delete those by hand if you want.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"
rm -rf "$data/myra" "$HOME/.local/bin/myra" "$data/applications/myra.desktop" "$data/icons/hicolor/256x256/apps/myra.png"
echo "Myra removed. Data kept in ~/.config/Myra."
