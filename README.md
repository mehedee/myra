# Myra

**Myra 3.0.2 · build 10** — a personal native macOS entertainment library, player, and downloader. Browse h5ai, Apache, and nginx HTTP directory listings, organise movies and series, and play directly from your sources or completed downloads.

## Requirements and installation

- Apple Silicon Mac, macOS 14 or later.
- Apple on-device AI additionally requires macOS 26+, Apple Intelligence enabled, a downloaded model, and a supported language. Unsupported systems retain normal library, search, downloads, and playback.
- Install `aria2` for downloads: `brew install aria2`. Embedded playback includes VLCKit; external VLC is optional.
- Download **Myra.dmg** from [GitHub Releases](https://github.com/mehedee/h5ai-streamer/releases), then drag Myra into Applications.

This personal build is ad-hoc signed, not notarized. Finder may require **Open** on first launch. Keep the previous application for rollback. Version 3.0.1 uses Myra throughout its project, module, bundle identifier and storage. For an existing installation, close the app and use the explicit profile migration below before first launch. Keep the original profile for rollback; never run both applications against the same data.

## Home and personal library

- Continue Watching resumes the most recently played movie version or episode.
- Clear Continue Watching with confirmation; unfinished resume records are removed without deleting media or changing watched state. Current playback continues and may be recorded again on a future play.
- Home searches hide shelves with no matching titles; clearing the search restores shelves without resetting other filters or expanded-shelf choices.
- Vote-weighted Top Rated Movies, Series to Watch, Latest Releases, Recently Added, Watchlist, followed-series episode indicators, and personal collections.
- Search and a single row of genre, language, year, source, rating, and watched filters.
- **Pick something** waits for current Home filters, offers filter reset when no unwatched title matches, and opens a separate suggestion window with artwork, details, Play, and Another Pick. It does not add a Home shelf.
- Alternate quality/source releases group under one title or episode. Choose a version before playing; season cards collapse in the episode chooser.
- Correct Match resolves ambiguous names, years, types, and optional TMDB IDs without renaming files.
- Optional TMDB Read Access Token provides posters, descriptions, ratings, genres, dates, and runtimes. Metadata updates run in bounded batches of 50. Ambiguous matches need review. Home uses cached metadata offline.
- Personal JSON export/import includes collections, watchlist, watched/followed status, history, corrections, sources, playback preferences, and markers, with preview, merge/replacement, and pre-import backups. Secrets and security bookmarks are excluded; credential-bearing sources and downloaded paths may require reconnecting.
- Followed-show notifications are optional and require macOS permission.

Availability reflects the saved index; it does not guarantee that a source is reachable from the current network. Myra uses the TMDB API but is not endorsed or certified by TMDB.

## Release updates

Settings and the Myra menu include **Check for Updates…**. Daily background checks
can be enabled or disabled in Settings. There is one release stream, with no channel
selector. Sparkle verifies signed feeds and archives before extraction and asks before
installation/relaunch. Personal data and download locations are preserved.
The first updater-enabled release must be installed manually; live checks require
the signed appcast to be published. See [update preparation and recovery](docs/updates.md).

## Myra AI

Open **Myra AI** on Home or a title's Details. All generation is explicit and cancellable; startup and indexing never run AI automatically.

| Feature | What it does |
| --- | --- |
| Smart Pick | Suggests indexed titles for your mood and explicit constraints; optional metadata/runtime is not required |
| Natural-language Search | Converts requests into local catalogue filters and grounded suggestions |
| Why This Pick | Explains a selected title using catalogue facts and your request |
| Short Summary | Creates a concise, spoiler-conscious summary of cached descriptions |
| Collection Suggestions | Proposes a themed collection for review before saving |
| More Like This | Finds related indexed titles using metadata |
| Compare Titles | Compares selected titles using available facts |
| Plan Tonight | Fits known runtimes into a minute budget; series suggestions select a concrete episode |
| Preference Feedback | Saves explicit likes/dislikes after review |
| Mood Tags | Suggests interpretive labels from descriptions |
| Library Assistant | Answers questions about the catalogue and consented local history |
| Library Actions | Proposes watchlist, watched, collection, search, or playback actions |
| Filename / Grouping Suggestions | Proposes catalogue corrections from selected filenames; physical files remain unchanged |
| Episode Recap | Uses imported subtitle text for a selected completed episode only; may contain spoilers |
| Translate Description | Translates cached descriptions into the selected language |
| Viewing Insights | Describes locally calculated played/watched patterns |

**Providers:** Apple on-device is the default. OpenAI and Claude use your own API keys and selected model. Disabled mode keeps ordinary features available. Settings provides model discovery, connection checks, per-feature toggles, response language, feedback, daily generation limits, output limits, and estimated usage. Connection checks list models; they do not test generation. Cloud providers may charge your account. Model compatibility and account limits vary.

**Privacy and actions:** Cloud use is opt-in, with separate permissions for history and imported subtitles. Model requests contain a bounded shortlist of title metadata and temporary aliases, never source media URLs. Local path/link/address text is redacted. Keys are stored in device-local Keychain entries and excluded from exports. OpenAI requests disable response storage. AI text is treated as untrusted data: unknown title IDs and invalid/over-budget plans are rejected. Supported personal changes require Apply and offer Undo while the relevant state remains unchanged; playback still requires a user action. No arbitrary tools, filesystem operations, or SQL are exposed to models.

A search/pick/plan may use two model generations (intent extraction and results); both count against the daily limit. Small session caches prevent repeated identical requests. Apple has a smaller context/output budget; lengthy context may require shortening. Responses can be incomplete or inaccurate; unknown facts are reported rather than invented. Recaps summarise a bounded subtitle excerpt, not an entire episode transcript. Cloud generation has not been validated against paid live accounts by the offline suite.

## Search and indexing

- Global search is the default, with **Fuzzy** enabled and an exact-mode toggle.
- Three-character minimum, local typo tolerance, reordered tokens, Unicode normalization, filenames and matched metadata aliases, deterministic ordering, and 500-result pages.
- SQLite precomputed search grams bound candidate retrieval; broad queries return the highest-ranked candidates from a bounded set.
- Home opens from the last completed index while refreshes scan changes. New data replaces the relevant saved scope only after a successful scan; incomplete scans retain existing records.
- Per-source/folder schedules and manual selective refresh, cached listing validators where supported, force rescans, cancellation, progress, and unavailable-source guidance.
- The footer refresh control opens indexing options. Low Impact indexing reduces concurrency and UI update churn.
- Separate personal history survives rebuilding the index. Schema 4 adds search documents and backs up an older database before migration.

## Playback and subtitles

- Bundled VLC playback with seek/resume, Previous/Next, pause/stop, volume/mute capped at 100%, speed, audio/subtitle tracks, offsets, chapters, aspect/crop, deinterlace, and repeat.
- Transport controls centred, options left, volume right; compact glass header and controls preserve video space.
- Episodes / Files and grouped versions; auto-next follows natural episode/file order. The last item returns to Library; repeat retains the current item.
- Native fullscreen hides chrome and shows controls on interaction. Space toggles playback; arrows seek/adjust volume; Escape exits. Browser position and window chrome restore on return.
- English track defaults, saved language preferences, manual track overrides, intro/outro markers, optional automatic skipping, and alternate-version recovery.
- Offline Library checks completed downloads and plays local files, retaining missing records with reconnection guidance.
- Free-first subtitles: embedded tracks, nearby downloaded sidecars, owned cache, or manual SRT/ASS/SSA/VTT import. Browser search opens OpenSubtitles.org for manual discovery.
- Optional OpenSubtitles API search uses your own key/account and actual account allowance. Search/download only happens explicitly. No paid plan is required by Myra, but provider limits apply. The legacy REST API remains on OpenSubtitles.com; the `.org` browser fallback does not claim a supported free `.org` API.
- Subtitle API requests use title/year/episode, never the source URL. Downloads are user-selected, bounded, validated, and restricted to allowed HTTPS provider hosts. Owned cache expires after 30 days and is capped at 100 MB.

## Appearance and downloads

- Native translucent headers and System/Light/Dark app appearance.
- Four user-selectable icon families: **Signature, Cinema, Orbit, Minimal**, each with Light, Dark, and Glass variants or Follow System.
- A real layered primary icon supports modern system appearances. Runtime selection changes the Dock icon and persists; Finder retains the signed primary icon. Myra never rewrites the installed signed bundle.
- Recursive download selection preserves directory hierarchy and delegates to aria2. Existing completed files are preserved; matching partial downloads resume.
- Pause/Resume, Cancel, Delete, Retry, Reveal, aggregate progress, and expandable file activity. Cancel/Delete records preserve partial files; permanent removal requires confirmation.
- Copy links, external VLC, and “Play on Sam Online” Shortcut actions remain available.

## Build and verify

macOS development and builds use the **master** branch, `Myra.xcodeproj` / `Myra` scheme, Swift 6 and Xcode 27 (or a compatible SDK including Foundation Models and Icon Composer). The **desktop-windows-linux** branch preserves the .NET 10 / Avalonia / LibVLCSharp desktop application in `desktop/`; its platform-specific features and packaging are documented there. The branches have separate clean initial histories.

```sh
zsh Scripts/prepare-vlc.sh
xcodebuild -project Myra.xcodeproj -scheme Myra -destination 'platform=macOS' build
zsh Scripts/verify-update.sh
```

Verification formats/lints Swift, tests the package, builds Release, creates `dist/Myra.dmg`, verifies its signature and disk image, and prints a SHA-256 checksum. Builds run in the foreground. `MYRA_NATIVE_WINDOW_TESTS=1` enables real window/fullscreen tests. With ffmpeg, synthetic playback fixtures are generated; serve `.build/verification/workflow` locally and set `MYRA_WORKFLOW_MEDIA_ROOT` to enable streaming workflow checks. Tests use isolated stores and mock AI providers, not paid cloud inference. Optional live metadata/subtitle and large benchmarks are explicitly gated.

`zsh Scripts/build-dmg.sh` builds the DMG independently and preserves `dist/Myra.previous.dmg`. Original icon sources are in `Resources/MyraIcons`; `Scripts/generate-myra-icons.py` regenerates native icon exports.

## Storage, safety, and rollback

Myra stores data under `~/Library/Application Support/com.mehedee.Myra/`: `Myra.store`, `LibraryIndex.sqlite`, and `EntertainmentPersonal.json`. The default download destination is `~/Downloads/Myra`; explicit existing destinations are preserved by migration. API keys remain in Keychain. AI settings use `myra.ai.settings`; icon preferences use separate Myra defaults.

Directory traversal stays inside the configured scheme/host/port/path. Download destinations are sanitized and checked before aria2 receives them. Network restrictions on media sources still apply; Myra does not tunnel or spoof source access.

For an existing profile, the optional one-time utility accepts the previous bundle identifier and application name explicitly:

```sh
swift Scripts/migrate-profile.swift PREVIOUS_BUNDLE_IDENTIFIER PREVIOUS_APP_NAME
```

The utility requires the app to be closed. It copies the profile (including SQLite journal files), renames the primary store, copies preferences and Keychain services without displaying credentials, and retains originals. It refuses to overwrite an existing Myra profile or follow symbolic links. Keychain may request local permission. Credentials already present in Myra are preserved; denied access requires rerunning or entering keys in Settings. Configured download paths and personal records are retained. Profile backups contain private source and playback information: keep them private. Desktop provides its own parameterized profile-copy utility.

Before downgrading, quit Myra and retain backups of the stores. An older app cannot read schema 4: restore its matching pre-migration index backup, or rebuild that disposable cache. Preserve the personal JSON and original source/download store. Do not run old and new app instances concurrently.

VLCKit 3.7.3 is pinned and checksum-checked. Its LGPL 2.1 notice is included in the app, with source at [VideoLAN](https://code.videolan.org/videolan/VLCKit). SwiftSoup remains the directory parser. Optional OMDb information uses its applicable personal/noncommercial terms.

See [release changelog](CHANGELOG.md), [Myra 3.0 implementation contract](docs/Myra-3.0-implementation.md), and [V2 implementation contract](docs/V2-implementation.md).
