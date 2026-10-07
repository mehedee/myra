Myra for Linux
===============

1. Install the two system dependencies.
   Arch:          sudo pacman -S --needed aria2 libvlc vlc-plugins-base vlc-plugins-video-output vlc-plugin-ffmpeg vlc-plugin-matroska vlc-plugin-pulse
   Debian/Ubuntu: sudo apt install aria2 libvlc5 vlc-plugin-base vlc-plugin-video-output
2. Run ./install.sh (installs for your user only; no root).
3. Start "Myra" from your app launcher, or run: myra

Data:      ~/.config/Myra (sources, settings, search index, resume points)
Downloads: ~/Downloads/Myra (change in Settings)
Remove:    ./uninstall.sh
