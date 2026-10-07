#!/usr/bin/env bash
# Installs Myra for the current user only (no root):
#   ~/.local/share/myra           the app
#   ~/.local/bin/myra             command-line launcher
#   ~/.local/share/applications    menu entry (app launchers such as Walker, GNOME, KDE)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
target="$data/myra"

rm -rf "$target"
mkdir -p "$target" "$HOME/.local/bin" "$data/applications" "$data/icons/hicolor/256x256/apps"
cp -r "$here/app/." "$target/"
cp "$here/myra.png" "$data/icons/hicolor/256x256/apps/myra.png"

cat > "$HOME/.local/bin/myra" <<LAUNCHER
#!/usr/bin/env sh
exec "$target/Myra" "\$@"
LAUNCHER
chmod +x "$HOME/.local/bin/myra"

cat > "$data/applications/myra.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Myra
GenericName=Media Browser and Downloader
Comment=Browse, stream and download from h5ai, Apache and nginx directories
Exec=$target/Myra %u
Icon=myra
Terminal=false
Categories=AudioVideo;Video;Player;
MimeType=video/mp4;video/x-matroska;video/webm;video/quicktime;video/x-msvideo;video/mp2t;
StartupWMClass=Myra
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" >/dev/null 2>&1 || true

echo "Installed. Start Myra from your app launcher, or run: myra"
missing=()
command -v aria2c >/dev/null || missing+=("aria2 (downloads)")
ldconfig -p 2>/dev/null | grep -q 'libvlc\.so' || [ -e /usr/lib/libvlc.so.5 ] || missing+=("libVLC (playback)")
if [ ${#missing[@]} -gt 0 ]; then
  echo
  echo "Still needed: ${missing[*]}"
  echo "  Arch:          sudo pacman -S --needed aria2 libvlc vlc-plugins-base vlc-plugins-video-output vlc-plugin-ffmpeg vlc-plugin-matroska vlc-plugin-pulse"
  echo "  Debian/Ubuntu: sudo apt install aria2 libvlc5 vlc-plugin-base vlc-plugin-video-output"
  echo "  Fedora:        sudo dnf install aria2 vlc-libs vlc-plugins-base  (RPM Fusion)"
fi
