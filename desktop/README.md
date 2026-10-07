# Myra for Windows and Linux

The Windows and Linux edition of Myra, the h5ai/Apache/nginx directory browser, streamer and
downloader in this repository. The macOS app (repository root) is SwiftUI + VLCKit and runs only on macOS.
This port uses .NET 10, [Avalonia](https://avaloniaui.net/) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp),
and drives the same [aria2](https://aria2.github.io/) download engine.

## Install on Windows

1. Download `Myra-win-x64.zip` (build it with `build/package-windows.sh`).
2. Extract it, then run `Myra.exe`. libVLC and `aria2c.exe` are included; nothing else to install.
3. The build is not code-signed. If SmartScreen appears, choose **More info → Run anyway**.

Windows 10 or 11, 64-bit.

## Install on Linux

1. Install libVLC and aria2 from your distribution:
   - Arch: `sudo pacman -S --needed aria2 libvlc vlc-plugins-base vlc-plugins-video-output vlc-plugin-ffmpeg vlc-plugin-matroska vlc-plugin-pulse`
   - Debian/Ubuntu: `sudo apt install aria2 libvlc5 vlc-plugin-base vlc-plugin-video-output`
2. Build with `build/package-linux.sh`, then extract `Myra-linux-x64.tar.gz`.
3. Run `./install.sh`. It installs for your user only, adds a `myra` command and an app-menu entry.
   `./uninstall.sh` removes it.

x86-64. Runs under X11, and under Wayland through XWayland.

## Features

| Area | Status |
|---|---|
| Sources (h5ai, Apache, nginx listings), boundary checks, breadcrumbs, sort by name/date | Ported |
| Current-folder filter and Global search over a persistent SQLite index (24 h auto refresh) | Ported |
| Folder downloads through aria2: hierarchy kept, pause/resume/cancel/retry, resume after restart | Ported |
| Embedded player (libVLC): resume, English audio/subtitle defaults, natural-order auto-next, repeat, speed, subtitle files, fullscreen, keyboard | Ported |
| Open in external VLC, copy link, reveal in Explorer, light/dark theme | Ported |
| `Myra.exe <url-or-file>` plays a video directly | New |
| Home (shelves, filters, Pick Something), Details, Watchlist/Collections, followed shows | Ported |
| OMDb/TMDB metadata, Correct Match, OpenSubtitles search, release versions and chooser | Ported |
| Index Management (daily/weekly/manual schedules, staged refresh, full rescan), schema 4 index | Ported |
| Fuzzy Global search (typos and reordered words, exact mode, provider-title aliases) | Ported |
| Myra AI: the 16 on-demand experiences, reviewed actions with undo, daily limit, separate cloud/history/subtitle permissions | Ported with OpenAI and Claude (your own API key); Disabled by default |
| Apple on-device AI (Apple Intelligence) | Dropped (macOS only) |
| Myra icon families and variants (Settings → Appearance) | Ported for window, taskbar and sidebar icons; the executable icon stays the same |
| Free-first subtitles: files next to local media, remembered cache per title, manual file, OpenSubtitles.org web search | Ported |
| Offline Library, Personal Library export/import (macOS-compatible JSON), playback preferences, intro/outro markers | Ported |
| API keys (TMDB, OMDb, OpenSubtitles, OpenAI, Claude) | Stored with DPAPI on Windows (Keychain on macOS), 0600 file on Linux |
| Followed-show notifications | In-app toast on Windows, `notify-send` on Linux |
| "Play on Sam Online" (Apple Shortcuts) | Dropped (macOS only) |

## Layout

```
src/Myra.Core     Platform-neutral logic ported from the Swift sources in ../Sources/Myra (parsing, search, index, aria2, playback rules)
src/Myra.App      Avalonia desktop app (windows, view models, libVLC player)
tests/             xUnit tests for Myra.Core, plus end-to-end media and listing-server scripts
tools/Myra.UiCheck  Headless end-to-end checks against a live listing server, real aria2 and real libVLC
build/             Windows and Linux packaging, Linux installer, packaged-app smoke test
```

## Update an installed copy (from WSL)

`./build/update-windows.sh [install-dir]` (default `D:\tdw\Myra-win-x64`) builds the zip, closes Myra,
keeps an existing install as `<install-dir>.previous`, copies the new build in and starts it.
On the first run it copies a former SamBD profile (`%APPDATA%\SamBD`) to `%APPDATA%\Myra` with
`build/migrate-profile.py`; the SamBD profile and install are left unchanged for rollback.
The first start migrates the index to schema 4 and keeps a fresh `LibraryIndex.sqlite.v3-backup`
(an older backup of that name is kept as `.v3-backup.previous`). The upgrade needs free disk space of
about twice the index (index plus `-wal`); with less, Myra shows an error and changes nothing.
On a 113,840-video index the upgrade took about 1 s and the first search-document build about 5 s;
the index grew from 223 MB (+74 MB WAL) to 343 MB, plus a 223 MB backup.
Older builds cannot open a schema-4 index, so restore the v3 backup before rolling back.

## Build

Needs the .NET 10 SDK.

```sh
dotnet test tests/Myra.Core.Tests     # unit tests
python3 -m unittest discover -s tests -p '*_test.py'  # profile migration safety
./build/package-windows.sh             # builds dist/Myra-win-x64.zip (from Linux, macOS or Windows)
./build/package-linux.sh               # builds dist/Myra-linux-x64.tar.gz
```

On Windows: `powershell -ExecutionPolicy Bypass -File build\package-windows.ps1`.

## Verification

The `desktop-windows-linux` branch preserves this edition as a clean Myra snapshot;
`master` contains the macOS edition. This edition matches macOS 3.0.1 (see Features), except
Apple on-device AI and other macOS-only items ("Play on Sam Online", the Dock icon).
AI is Disabled by default; OpenAI and Claude use your own API key and may incur charges.
Verification on 2026-10-08 (Linux, .NET 10): Release build with zero warnings, all 266
.NET unit tests, and all headless checker checks (157) passed. The checker covers source
browsing, natural ordering, SQLite search/indexing, profile persistence, real aria2
pause/restart/resume, real libVLC playback, the AI workspace with a fake cloud provider
(consent before any request, no request after a declined permission), and the icon picker.

- `tests/Myra.Core.Tests`: unit tests ported from the Swift test suite, plus Windows filename rules, index upgrade and AI permission checks.
- `tools/Myra.UiCheck`: starts the real app headlessly against `tests/e2e/listing_server.py`
  serving media from `tests/e2e/make_media.py`. It checks browsing, search and indexing, real aria2
  downloads (pause, resume across an aria2 restart, content match), and real libVLC playback
  (auto-next, repeat, English tracks over French defaults, resume, command-line play).
- `build/smoke-windows.ps1`: on a Windows machine, launches the packaged `Myra.exe` with a video URL,
  checks that the main and player windows open and that the app exits cleanly, and saves a screenshot.

## Data locations

| What | Windows | Linux |
|---|---|---|
| Sources, downloads, settings | `%APPDATA%\Myra\Myra.json` | `~/.config/Myra/Myra.json` |
| Search index, resume points | `%APPDATA%\Myra\LibraryIndex.sqlite` | `~/.config/Myra/LibraryIndex.sqlite` |
| Player preferences | `%APPDATA%\Myra\PlayerPreferences.json` | `~/.config/Myra/PlayerPreferences.json` |
| Personal library, folder schedules | `%APPDATA%\Myra\EntertainmentPersonal.json`, `IndexPolicies.json` | `~/.config/Myra/` |
| API keys | `%APPDATA%\Myra\Secrets\` | `~/.config/Myra/Secrets/` |
| AI settings (provider, model, permissions, limits; never API keys) | `%APPDATA%\Myra\MyraAI.json` | `~/.config/Myra/MyraAI.json` |
| AI requests counted today | `%APPDATA%\Myra\MyraAIUsage.json` | `~/.config/Myra/MyraAIUsage.json` |
| Subtitle cache | `%LOCALAPPDATA%\Myra\Cache\Subtitles` | `~/.cache/Myra/Subtitles` |
| Remembered subtitles per title | `title-cache.json` in the subtitle cache | same |
| Downloads (default) | `Downloads\Myra` | `~/Downloads/Myra` |

## Upgrade an existing desktop profile

The app uses the Myra profile paths above. Before its first launch, close both the
previous desktop app and Myra, and back up your previous profile. Use the supplied
Python 3 utility with the **actual existing** profile directory and primary JSON
filename (do not substitute a download directory):

```sh
python3 build/migrate-profile.py /path/to/previous-profile /path/to/Myra --store-filename PREVIOUS_STORE.json
```

The utility copies the profile to a new destination, renames its primary JSON store
to `Myra.json`, and keeps the original untouched. It preserves the search index,
SQLite journal files, playback history, player settings, and configured download
paths. It refuses existing destinations, invalid JSON, and symbolic links. Run it
before Myra creates a profile; if Myra has already launched, back up its profile
and choose explicitly which profile to keep. Cache migration is optional. No
account credentials or profile contents are printed.

## Licences

libVLC is LGPL 2.1 and aria2 is GPL 2. Both are shipped unmodified as separate files, and their licence texts are in `licenses\`.
