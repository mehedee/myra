# Myra V2 design and historical delivery notes

This document records earlier feature contracts and verification. Its historical version numbers are not the current release. Temporary logs and previous packaging paths are no longer retained in the clean repository; current commands and artifacts are documented in README.md.


Authorized on 2026-10-02. Version 2.0.0, build 2. Preserve SwiftData categories/downloads, existing playback/fullscreen/keyboard behavior, aria2 safety, and source boundaries. No publishing, installation, commits or pushes are included.

## Acceptance criteria

- Home launches first: continue watching, rated movies, series, release dates, first-discovered titles and watchlist. Cached discovery survives provider failure; ratings are provider-labelled.
- One title contains multiple releases. Playback explicitly asks for a version. Neighbour navigation skips alternate encodes of the same movie/episode; different episodes remain separate. Uncertain filename matches require correction.
- Previous/Next enabled only for existing neighbours. Transport centered in normal/fullscreen layouts, options left and volume right. Global search default, direct footer index refresh with concurrency guard.
- Episode browser, followed-show new episode indicators (baseline and deduplication), optional notifications, source alternatives, personal collections, filtered random picks.
- Durable audio/subtitle/speed/autoplay preferences and configurable intro/outro ranges, optional automatic skips with duration validation and overrides.
- Completed local downloads with actual file availability and local resume. Missing paths clearly reported; UI record removal does not delete media.
- Export/import previews merge or replacement, backups before replacement, version and value validation. Include personal records, sources and non-secret preferences. Exclude credentials and require reconnection for imported machine-specific paths.

## Data and integration

The SQLite index remains source-scoped and actor-owned. Additive schema updates preserve playback and back up existing databases before migration. Personal data remains outside the disposable index. Metadata queries send title/year/provider IDs rather than source URLs, with HTTPS, bounded payloads, caching and bounded background work. TMDB uses Keychain and requires attribution; existing OMDb and subtitle integrations remain supported.

## Verification

Focused regression tests for grouping, navigation boundaries, search defaults, index migration, personal import/export and provider mapping; Swift format/lint, SwiftPM tests, Xcode Release build, signed DMG verification. Provider live tests require user credentials. UI checks must be distinguished from build/test evidence.

## Rollback

Keep the preceding DMG as `[historical rollback DMG]`. Index schema backup and personal-data replacement backup preserve recovery options. Never downgrade a database in place; restore a backup when using a version that rejects the newer schema. Personal records must not be deleted as part of an index reset.

## Deferred

Cloud sync, automatic intro detection and learned personalized recommendations are outside this release.

## Status

All planned feature surfaces implemented and integrated. Independent source review corrected merged-marker preservation, exact personal-state rollback, authenticated-source merge preservation, and stale playback request cancellation. Swift parser checks, strict swift-format lint and Xcode project plist validation pass. Full compilation, test execution, UI snapshot inspection and release/DMG validation are pending the consolidated verification job. Live TMDB requests and macOS notification authorization require the user’s credentials/choice and are not exercised by fixture tests.

### Verification follow-up

The first verification job compiled successfully but stopped on five test assertions before DMG packaging. Corrected concurrent Home reload completion and equivalent local-directory URL comparison. Updated VLC pause verification to measure actual playback/progress instead of its stale buffering state, and waited for inspector spring animation alignment. Native UI snapshots now render with an opaque semantic window background. Targeted failing checks pass, and Home, version selection and Offline Library snapshots were visually inspected. Full release verification is being repeated after these fixes.

### Consolidated tests and Xcode registration

The corrected verification run passed all 82 tests (8 explicitly skipped, zero failures). Xcode Release compilation then exposed incorrect source build-phase registration. Corrected project groups and build phases and independently checked all 30 app Swift files and 8 test Swift files are registered in Sources, with no Swift files in Copy Bundle Resources. Plist validation passes. Release packaging is being repeated without rerunning the already-passing SwiftPM suite. DMG packaging now verifies the ad-hoc signature before staging.

### Release completed

Final packaging completed successfully (exit 0). Confirmed the generated app reports version 2.0.0 and build 2. Its ad-hoc code signature verifies, and hdiutil verified the final 41 MB `[historical DMG]` checksum. The previous distributable remains `[historical rollback DMG]`. All 82 tests passed, with 8 explicitly skipped; representative Home, grouped-version chooser, Offline Library and centred player controls were visually inspected using isolated native snapshots. Live source/provider access, user notification permission, and optional native fullscreen/HTTP workflow checks were not exercised by this run.

User execution preference: all future builds must run in the foreground. No background build jobs.

## 2.0.1 correction release — 2026-10-02

Authorized by “Implement” after the correction plan. Version 2.0.1, build 3.
Home and title count now occupy the existing native toolbar. Search is rounded with
search/clear affordances; Pick Something, metadata and Personal Library share its
row. All filters, Hide Watched and Reset share a horizontally scrollable row.
Home actions use icons when the available width cannot accommodate labels.

The shared `SeasonEpisodeList` presents collapsible season cards, compact episode
rows, nested alternate versions, progress and current/last-played indicators.
Home, details and the player use it. The player uses the indexed title for all
available seasons, falling back to the current folder when no title is indexed.
Previous/Next retains its same-directory navigation boundary.

Resume opens the most recently played unfinished file and applies its saved
position once VLC reports seeking available. A completed newest file does not
silently resume an older episode. Explicit version selection remains available;
missing local resume files produce an actionable message to choose another version.
Player navigation, title, state and fullscreen entry moved into the native toolbar;
fullscreen retains its overlay. A slim seek row sits above one horizontal action
row with icon menus left, centered transport and volume right.

Changed areas: HomeView, Views, EntertainmentModels, EntertainmentDetailView,
AppCoordinator, EmbeddedPlayer, EmbeddedPlayerView, PlayerPersonalViews,
PlayerOptionsMenus, the Xcode version settings and verification script. Regression
coverage was extended in PlayerV2Tests, EntertainmentUISnapshotTests and
PlayerWorkflowTests. No dependency, index-schema, credential or source-boundary
changes. No installation, Git history or publishing was performed.

Foreground verification completed: strict Swift formatting/lint, SwiftPM compile,
85 tests with zero failures and one live-provider test skipped. Local synthetic
media enabled real resume seeks, auto-next, repeat, tracks and native fullscreen
entry/exit checks. Native toolbar, compact/wide Home, light/dark and season chooser
snapshots were rendered and inspected. The old click test was updated because its
normal-mode button moved from content into the window toolbar; actual fullscreen
exit remains tested by clicking the overlay button.

Xcode Release packaging produced `[historical DMG]`; bundle version/build,
ad-hoc signature, embedded framework and DMG checksum validation passed.
`[historical rollback DMG]` retains the preceding release for rollback. Builds were
supervised in foreground command sessions; no detached build jobs were used.
Live provider availability and user-specific metadata are not fixture-verified.
Existing deferred features remain outside this correction release.

## 2.0.2 native translucent headers — 2026-10-02

Authorized after the header plan. Version 2.0.2, build 4. RootView now requests a
continuous visible native toolbar background using regular system material. On
macOS 26 and later, individual toolbar title/action capsule backgrounds are hidden;
older macOS retains its native toolbar rendering. System material handles appearance
and transparency accessibility preferences. Home uses native soft scroll-edge and
background-extension effects on macOS 26+, with 36-point top content margins and
28-point side/bottom padding. The fullscreen player header uses native material
instead of a black scrim, retaining auto-hide and the native window restoration flow.

Changed files: Views.swift, HomeView.swift, EmbeddedPlayerView.swift, Xcode version
settings, verify-update.sh and EntertainmentUISnapshotTests.swift. No dependency,
data schema, credential, playback-selection or source-boundary changes.

Foreground verification passed: Swift formatting/lint, SwiftPM compilation, 85 tests
with zero failures and one live-provider check skipped, including actual local VLC
resume/playback and native fullscreen transitions. A native scroll regression moves
Home content 300 points, asserts the toolbar remains visible and the hosting height
is unchanged, and records a scrolled snapshot. Home, scrolled header and player
snapshots were inspected. Appearance snapshots cover Light/Dark; system-level
Reduce Transparency and older macOS were not manually exercised.

Xcode Release build and DMG packaging completed in supervised foreground sessions.
Bundle version 2.0.2/build 4, ad-hoc signature and DMG checksum validation passed.
The previous DMG is retained for rollback. No installation or publishing occurred.

## 2.0.3 indexing performance — 2026-10-03

Authorized after the optimization plan. Version 2.0.3, build 5. Indexing now streams
250-video batches into SQLite and sends progress without carrying the accumulated
inventory. Write failures propagate before stale-record pruning. The folder queue
uses a cursor with periodic compaction; folder/result deduplication remains in memory.
Parsing reuses regex/date formatters and avoids attempting date parsing for obviously
unrelated cell values.

LibraryController runs indexing at utility priority, publishes changed progress at
most twice per second, and refreshes catalogue/search once after settling rather
than every half-second. Cancellation waits for the previous scan before refreshing
Home; overlapping scans still serialize. Settings offer Balanced (two folders) and
Low Impact (one folder), plus Cancel Indexing. Playback and Low Power Mode reduce
concurrency to one. Automatic enrichment pauses between requests while indexing.

Catalogue grouping and metadata decoding run on EntertainmentCatalogueWorker rather
than MainActor. Metadata lookup uses batches of 200 titles / at most 600 keys,
preserving stale display metadata and seven-day retry/freshness semantics. Publication
merges the latest playback records and skips superseded preparations. Home projection
work runs on its own actor, caches shelves/filter choices and debounces filter changes;
progress updates do not replace the displayed catalogue or reset scroll/selection.
Numeric catalogue/index duration and query counters are logged without source URLs.

Changed areas: DirectoryService, LibraryIndex, LibraryController, AppCoordinator,
EntertainmentStore, HomeView, MediaInspectorView, DiscoverySettingsView, version
settings, verification script and LibraryIndexTests. No dependencies or index schema
changed. Failed/cancelled sources retain previous rows; playback and first-discovered
data survive upserts. The preceding DMG remains available for rollback.

The 30,000/100,000-video synthetic scanner fixtures verified maximum batches of 250
and empty progress-result payloads. The 1,000-title catalogue fixture used five bulk
metadata reads, versus up to 4,000 individual reads in the former code, and verified
metadata/freshness preservation. Existing cancellation/pruning tests and a new
write-failure regression verify preservation. Concurrency-policy checks cover playback,
Low Impact and Low Power Mode.

Opt-in 100,000-video benchmark, separate identical SwiftPM test processes:
accumulator-compatible scan 0.379 s, harness maximum RSS 121,241,600 bytes (115.6 MiB);
streaming scan 0.580 s, maximum RSS 89,112,576 bytes (85.0 MiB), about 26.5% less.
This benchmark isolates scanner retention; it is not a measurement of the user's
600 MB app or live source/network indexing. Streaming trades some synthetic scan
speed for bounded allocations/yielding. Raw logs: [archived temporary log]
and [archived temporary log]

Foreground Xcode Release build/DMG packaging, version/build, strict format/lint,
ad-hoc signature and DMG checksum verification passed. Initial normal suite: 90 tests,
zero failures, eight unavailable-fixture/provider checks skipped. After adding the
opt-in benchmark, one native fullscreen transition timed out in the combined 91-test
run; its isolated rerun passed. Final combined-run result is recorded below.

Live-library CPU, frame timing, memory after repeated rebuilds, and Instruments
Time Profiler/Allocations baselines remain unmeasured. Use those measurements before
promising a resource target or expanding to catalogue pagination/conditional crawling.
No installation, source refresh against user data, publishing or Git history occurred.

Final combined foreground run passed: 91 tests, zero failures, two skips (live
provider and opt-in benchmark). The benchmark passed separately in both modes.
Native fullscreen, local VLC resume/auto-next/repeat/track tests passed on the final
run. The earlier native transition timeout was transient; no playback code was
changed to bypass it. Build artifact: [historical DMG], version 2.0.3 build 5.

## 2.0.4 / build 6 — staged refresh, folder schedules, saved Home (2026-10-07)

Home restores a revision-checked catalogue snapshot before automatic network indexing.
Source names and roots are stored once per snapshot, reducing duplicate cache data.
Watch history is merged from current personal data. Metadata and match corrections
invalidate the snapshot; daily metadata freshness checks reuse the grouped titles.
Home no longer requests a second startup preparation. Cancelling startup indexing
leaves the independently running Home restore intact.

Index Management is available through the footer refresh menu and Settings. Its
expandable source/folder tree supports inherited Daily, Weekly and Manual-only
policies, selected-scope refresh, explicit full rescan, pause and cancellation.
Existing indexed file paths supply the folder tree immediately on migration, before
any directory snapshots exist. File counts include descendants. Added/edited sources
refresh only their own scope; source deletion cancels the previous scan and removes
only the deleted source. Schedule choices persist locally.

Automatic work selects due scopes instead of refreshing every source when one is
stale. Per-directory snapshots retain parsed entries and HTTP ETag/Last-Modified
validators. Conditional GET handles 304 responses without parsing HTML. Unchanged
parents still allow independent child checks. Servers without validators use fresh
listings and compare entries; they cannot provide a guaranteed change-only request
stream without a server change feed. Folder snapshots are established on the first
successful refresh. An excluded Manual-only branch without a snapshot fails safely
with an instruction to refresh that branch once; it never causes index deletion.
Full rescan bypasses schedules and validators inside the explicitly selected scope.
Pause takes effect between folder batches; in-flight requests can finish.

Every refresh writes bounded batches to a private temporary staging database. Staging
has no FTS index. Completed selected scopes promote in one SQLite transaction after
scanning finishes. Sources with any failed folder conservatively retain all their
selected scopes. Cancellation discards staged changes. Unselected sources/siblings,
first discovery timestamps, metadata and playback remain intact. Identical published
video rows are not updated, and unchanged refreshes do not rebuild Home/search.
Missing manual-only branches are retained during parent refreshes until explicitly
rescanned. No existing source synchronization receives a selected-source subset.

Schema v3 adds folder snapshots and a catalogue revision. The existing SQLite backup
API saves LibraryIndex.sqlite.v2-backup before v2 migration, including committed WAL
pages. Migration and cancellation/promotion checks are transactional. Source URL
boundaries remain enforced, and cached media URLs are not sent to metadata providers.
No new dependency, authentication change, installation, Git commit or push occurred.
The Home card overflow menu now hides its indicator and displays only the ellipsis.

Final foreground verification: 102 tests, zero failures, three skips (live provider,
opt-in scanner memory benchmark, opt-in isolated Home profile). Both native Home and
Index Management snapshots were inspected. Local VLC playback, resume, auto-next,
repeat, track defaults and native fullscreen checks passed. A startup-cancellation
regression found during the first suite was fixed and covered by the passing reruns.
Strict formatting/lint and project syntax checks passed. Release/DMG, bundle version,
signing and disk-image validation results are recorded by the verification script.

Read-only SQLite backup of the existing live index supplied an isolated profile:
112,384 videos / 30,096 grouped titles. Separate foreground test processes measured
cold preparation 10.061 seconds, peak test-process RSS 643,399,680 bytes (~614 MiB);
saved snapshot restore 0.805 seconds, peak RSS 411,484,160 bytes (~392 MiB). Snapshot
size dropped from ~125 MiB to ~80 MiB after normalizing shared source data. These are
catalogue preparation measurements, excluding native Home projection/rendering,
poster fetching and database-opening/migration time. They do not establish total app
memory, live-network refresh CPU or frame timing. Logs are [archived temporary log],
[archived temporary log] and [archived temporary log]
The installed library was not migrated or refreshed for this measurement.

Rollback: [historical rollback DMG] retains the preceding verification candidate
(also 2.0.4). For a rollback to 2.0.3 or earlier, close the app and restore the
pre-migration SQLite backup through a reviewed recovery procedure: those versions
cannot open schema v3. Personal JSON remains separate. Deferred work: provider-specific
change manifests, resumable interrupted scans, catalogue pagination, and live
Instruments profiling of complete app memory/network refresh/frame timing.

## 2.0.5 / build 7 — Pick Something window (2026-10-07)

Pick Something opens a native modal window instead of inserting a suggestion shelf
on Home. The window reuses Details: poster, synopsis, ratings, release information,
watchlist/watched/collection actions, match correction, and episode/version lists.
Another Pick replaces the content within the same window and resets per-title form
state. Suggestions respect Home filters, exclude watched titles, and avoid the
current pick when another eligible title exists. Empty results show guidance.

Play/Resume closes the suggestion; multi-file titles open the existing chooser
only after the suggestion is dismissed. Escape/Close dismisses the window. Network
routing, database schema, and dependencies are unchanged.

Verification includes selection regression checks (empty/all-watched, one eligible
title, alternate selection, watched exclusion) and native hosted snapshots for the
populated and empty suggestion views. Existing playback and version/episode tests
remain part of the foreground verification suite. No live-provider artwork is
required for the deterministic snapshot fixtures.

Final foreground suite: 102 tests, zero failures, three optional checks skipped.
Populated and empty native snapshots were inspected. The first suite stalled in an
existing VLCKit repeat callback lock wait; its stack sample was retained under
[archived temporary log] The complete rerun passed, including actual VLC
repeat/resume/episode progression and native fullscreen tests, without player changes.
