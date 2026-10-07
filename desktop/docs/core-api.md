# Myra.Core API for the desktop UI

This document lists the core (non-UI) types that bring the Windows/Linux app to parity with macOS Myra 3.0.1 (except Apple on-device AI).
All types are in the `Myra.Core` namespace. The UI owns windows, dialogs, notifications and threading.

## Rules for the UI layer

1. Call blocking core methods (`LibraryIndex`, `EntertainmentCatalogue.Prepare`) off the UI thread.
2. Core events (`EntertainmentStore.Changed`, `IndexRefreshController.StateChanged`, …) can fire on worker threads. Marshal them to the UI thread.
3. Treat `EntertainmentStore.Personal` and `EntertainmentStore.Catalogue` as read-only snapshots. Change data only through store methods.
4. Never write secrets to `Myra.json`. Use `ISecretStore`.
5. `PersonalLibraryTransfer.Import` changes `AppStore.Categories` (an `ObservableCollection`). Call it on the UI thread.

## Data locations (`AppPaths`)

| Data | Windows | Linux |
|---|---|---|
| Sources, downloads, settings | `%APPDATA%\Myra\Myra.json` | `~/.config/Myra/Myra.json` |
| Index (schema v4) | `%APPDATA%\Myra\LibraryIndex.sqlite` | `~/.config/Myra/LibraryIndex.sqlite` |
| Pre-migration index backup | `LibraryIndex.sqlite.v3-backup` (`.v{N}-backup` for an older schema; the backup before it is kept as `.v{N}-backup.previous`) | same |
| Saved Home snapshot | `LibraryIndex.sqlite.home-cache` | same |
| Personal records (`PersonalLibraryPath`) | `%APPDATA%\Myra\EntertainmentPersonal.json` | `~/.config/Myra/EntertainmentPersonal.json` |
| Folder schedules (`IndexPoliciesPath`) | `%APPDATA%\Myra\IndexPolicies.json` | `~/.config/Myra/IndexPolicies.json` |
| Player preferences and markers | `%APPDATA%\Myra\PlayerPreferences.json` | `~/.config/Myra/PlayerPreferences.json` |
| Secrets (`SecretsDirectory`) | `%APPDATA%\Myra\Secrets\*.dpapi` (DPAPI, CurrentUser) | `~/.config/Myra/Secrets/*.secret` (0600, folder 0700) |
| Subtitle cache (`SubtitleCacheDirectory`) | `%LOCALAPPDATA%\Myra\Cache\Subtitles` | `~/.cache/Myra/Subtitles` |
| Remembered subtitles per title | `title-cache.json` in the subtitle cache (at most 1000 titles, 20 files each) | same |
| AI settings (`AiSettingsPath`), never keys | `%APPDATA%\Myra\MyraAI.json` | `~/.config/Myra/MyraAI.json` |
| AI daily request count (`AiUsagePath`) | `%APPDATA%\Myra\MyraAIUsage.json` | `~/.config/Myra/MyraAIUsage.json` |

`MYRA_DATA_DIR` overrides the data and cache folders (tests use it).

## 1. Index, staged refresh and Index Management

### `LibraryIndex`

- `new LibraryIndex(path)` opens the index and migrates it in place to schema v4 in one transaction. An older index (v1 to v3) is first copied with the SQLite backup API to `{path}.v{N}-backup` (written to `.tmp`, verified read-only with `quick_check` and `user_version`, then renamed). Every upgrade takes a fresh backup; an existing backup moves to `.v{N}-backup.previous`. Before the backup, the upgrade needs free space of twice the index plus its WAL; with less, it throws `LibraryIndexException` ("Not enough free disk space …") and changes nothing. A newer schema throws `LibraryIndexException`. Do not delete the backup automatically.
- Fuzzy Global search: `Search(query, limit = 500, offset = 0, fuzzy = true, cancellationToken = default)`. The token stops a pending document build. Fuzzy mode tolerates typos and reordered words and ranks exact phrases first. `fuzzy: false` requires the whole normalized query as a substring. Both modes are deterministic, so pages never repeat or skip a result. The query must have 3 or more characters, else `GlobalSearchException(QueryTooShort)`.
- Search documents live in `search_documents` with an FTS5 `unicode61` index of three-letter grams (`search_document_fts`). SQL triggers queue new and renamed videos in `search_dirty` and remove deleted ones. `BuildSearchDocuments()` indexes the queue in batches of 500 and releases the index lock between batches. `Search` and `SaveSearchAliases` do it on demand, so call `BuildSearchDocuments()` from a background task after migration to avoid a first-search delay (about 4 s for 114,000 videos).
- `SaveSearchAliases(titles)` adds the title name and the provider title to each version's document. Queued documents are built once, with their aliases; later calls rebuild only documents whose alias text changed. `EntertainmentStore` calls it after every catalogue reload and every enrichment. First start after an upgrade, measured on a copy of a 113,840-video v3 index: upgrade 1.0 s, document build 4.6 s, index 222.6 MB (+74.1 MB WAL) to 342.6 MB plus a 222.6 MB backup.
- `FuzzySearch` (static): `Normalize`, `MatchExpression` (at most 64 quoted grams), `IndexGrams`, `Score`, `EditDistance`.
- `AppSettings.FuzzyGlobalSearch` (default true) stores the fuzzy/exact choice in `Myra.json`.
- Existing members are unchanged: `Search`, `SynchronizeSources`, `NeedsRefresh`, `LastRefresh`, `VideoCount`, `Upsert`, `CachedMetadata`, `SaveMetadata`, `SavePlaybackPosition`, `PlaybackPosition`.
- New members:
  - `Revision()` – catalogue revision. It changes when published content, sources or metadata change.
  - `Inventory()` – every video with first-discovery date and playback progress (`EntertainmentVersion`).
  - `FolderRows(roots)` – Index Management tree (`IndexFolderRow`: scope, last check, file count with descendants). Works before any folder snapshot exists.
  - `Folder(scope)`, `SaveFolder(folder, scope)` – directory snapshots with `ETag`/`Last-Modified`.
  - `MetadataRecords(keys)` – bulk metadata read.
  - `Publish(stagingPath, scopes)` – used by `LibraryIndexer`.

### `IndexRefreshController` (port of `LibraryController`)

```csharp
var controller = new IndexRefreshController(() => services.Index, new DirectoryService());
controller.IsPlaying = () => player.IsPlaying;        // playback lowers concurrency to 1
controller.Mode = settings.IndexingMode;               // Balanced = 2 folders, LowImpact = 1
controller.StateChanged += …;                          // progress (≤ 2 per second), pause, rows
controller.IndexChanged += …;                          // reload Home and Global search

// Startup: restore Home first, then refresh only due folders.
await controller.RefreshAsync(roots, IndexRefreshMode.Due, manual: false, prepare: () => store.ReloadAsync());
// Footer refresh button:
await controller.RefreshAsync(roots, IndexRefreshMode.Due, manual: true);
// Index Management:
await controller.RefreshAsync(roots, IndexRefreshMode.Selected, manual: true, scopes: selected);
await controller.RefreshAsync(roots, IndexRefreshMode.Selected, manual: true, scopes: selected, full: true); // Full rescan
controller.Pause(); controller.Resume(); controller.Cancel();
controller.SetSchedule(scope, IndexSchedule.Weekly);   // null = inherit
await controller.LoadFolderRowsAsync(roots);           // then read controller.FolderRows
```

- `RefreshAsync` returns `IndexRefreshOutcome` (`Changed`, `Cancelled`, `Summary`, `Failures`, `Error`, `CompletionMessage`). Show `CompletionMessage` for manual refreshes.
- A new refresh cancels the running one and waits for it. `prepare` is not cancelled by `Cancel`.
- Pause takes effect between folder batches. Requests in flight finish.
- Cancel discards staged work. The published index does not change.
- A source with any failed folder keeps all its old records.
- A Manual-only folder without a snapshot fails safely with the message "Refresh this manual-only folder once to establish its snapshot."

### Scopes and schedules

- `IndexScope(root, folderUri)`; `IndexScope.ForRoot(root)`; `IndexScope.Compact(scopes)` removes nested selections.
- `IndexSchedule` = `Daily` (default) / `Weekly` / `Manual`. Labels: `schedule.Label()` → "Daily", "Weekly", "Manual only".
- `IndexPolicies.ScheduleFor(scope)` returns the inherited schedule. `OverrideFor(scope)` returns only an explicit override.
- `IndexRefreshSummary.Text` – "Checked N folders · N unchanged · N files added · N updated · N removed".

### Lower level

- `LibraryIndexer(DirectoryService)` – snapshot-aware staged refresh. `LibraryIndexer(GlobalSearchService)` still works (the current app uses it) and now also stages and publishes.
- `DirectoryService.ConditionalListingAsync(url, boundary, cached)` – conditional GET. Redirects outside the source throw.

## 2. Home, titles and personal records

### Model (`EntertainmentModels.cs`)

- `EntertainmentVersion` – one file. `Id` is the media URL. `Quality` is "1080p · WEB-DL", `Season`, `Episode`, `ProgressSeconds`, `Duration`, `LastPlayed`.
- `EntertainmentTitle` – `Id` ("movie|name|year", "folder|…", "file|…"), `DisplayName`, `Year`, `Kind`, `Versions`, `Metadata`, `PosterUrl`, `ResumeVersion`, `LatestReleaseDate`.
- `EntertainmentGrouping.Group(versions, corrections)` – release grouping rules from macOS.
- `EntertainmentEpisodeGroup.Groups(versions)`, `EntertainmentSeason.Seasons(versions)` – season cards and episode rows ("Season 1 · Episode 01", "Other files").
- `EntertainmentPick.Select(filtered, watched, currentPickId)` and `CanPick(...)` – Pick Something. Home enables Pick once the projection matches the current filters (`HomeViewModel.CanPick`); when every match is watched the Pick window offers Reset Home Filters.
- `EntertainmentMetadata.RuntimeMinutes` (nullable): TMDB movie runtime or first episode runtime. Null means unknown; unknown runtimes stay eligible for picks.
- `MediaIdentity.Parse(filename)` – title, year, season, episode; `CacheKey`.

### `EntertainmentStore`

```csharp
var store = new EntertainmentStore(() => services.Index, secrets: secretStore,
    isIndexing: () => controller.IsRefreshing);
store.Changed += …;                 // re-project Home
store.NewEpisodesDetected += (s, e) => { if (e.Notify) ShowDesktopNotification("New episodes available", e.Message); };
await store.ReloadAsync();          // fast restore from the saved snapshot when valid
```

- State: `Catalogue`, `Personal`, `NewEpisodeIds`, `IsEnriching`, `ErrorMessage` (`ClearError()`), `CanWrite`, `ProjectionRevision`.
- Enrichment: `EnrichAsync()` (Update Metadata button). It processes at most 50 titles per run and waits while indexing runs. Automatic enrichment starts after a reload when a TMDB token exists.
- Lookup: `TitleContaining(media)`, `Variants(media)` (same episode, other releases), `HasNewEpisode(version)`.
- Personal actions: `ToggleWatchlist`, `ToggleWatched`, `ToggleFollow`, `CreateCollection`, `RenameCollection`, `DeleteCollection`, `ToggleCollection`, `SetNotifications`.
- Correct Match: `CorrectMatchAsync(versionIds, title, year, kind, tmdbId)`. Pass all version IDs for "apply to all versions". Returns false and sets `ErrorMessage` for invalid input.
- Playback: `RecordPlayback(media, seconds, duration)` during playback; `MarkPlaybackEnded(media)` on natural end.
- A corrupt `EntertainmentPersonal.json` is kept unchanged. `CanWrite` becomes false and `ErrorMessage` explains it.

### Home projection

```csharp
var filter = new HomeCatalogueFilter(Query: q, Genre: g, Language: l, Year: y, Source: s, MinimumRating: r, HideWatched: h);
var projection = HomeProjection.Prepare(store.Catalogue, store.Personal, filter);   // run off the UI thread, debounce ~100 ms
var shelves = HomeProjection.Shelves(projection, store.Personal, store.NewEpisodeIds);
```

- Filter choices: `projection.Genres`, `Languages`, `Years`, `Sources`, `HomeCatalogueFilter.RatingChoices`.
- `HomeShelf`: `Name`, `Subtitle`, `Titles`, `EmptyMessage`, `VisibleLimit` (60, then "Show all").
- Pick window text: `filter.Description`. Footer text: `HomeProjection.Attribution`, `DiscoveryService.AttributionText`.

### Followed shows

- `FollowedEpisodes.EpisodeId(version)`, `Detect(catalogue, personal)`. The store does this on each reload and raises `NewEpisodesDetected` once per new episode. Asking the OS for notification permission is the UI's job.

## 3. Metadata providers

- `DiscoveryService` (TMDB): `LookupAsync(title, correction, token, index)`. Errors: `DiscoveryException.Kind` = `MissingToken` / `InvalidResponse` / `Ambiguous`.
- `MetadataService` (OMDb): `LookupAsync(identity, key, index)` → `MovieMetadata`. Hide "N/A" values with `MovieMetadata.Available(value)`. Licence text: `MovieMetadata.License`.
- `OpenSubtitlesService`: `SignInAsync(credentials)` (returns allowed downloads), `SearchAsync(identity, language, credentials, page)` → `SubtitleSearchPage`, `DownloadAsync(result, credentials, cache)` → `(Path, Remaining)`. Errors: `SubtitleException.Kind`.
- `SubtitleCache` – `new SubtitleCache()` uses `AppPaths.SubtitleCacheDirectory`. Load the returned path into libVLC as a subtitle file.
- All three take an optional `HttpClient`; production uses `SafeHttp.CreateClient` (no automatic redirects, no cookies).

## 4. Secrets

```csharp
ISecretStore secrets = SecretStore.CreateDefault();          // DPAPI on Windows, 0600 files on Linux
secrets.Save(SecretNames.TmdbReadToken, token);              // empty value deletes
secrets.Save(SecretNames.OmdbApiKey, key);
secrets.SaveSubtitleCredentials(new SubtitleCredentials(apiKey, user, password));
var creds = secrets.ReadSubtitleCredentials();
```

Failures throw `SecretStoreException`.

## 5. Player state

- `PlayerPersonalState` adds `AutomaticSkipping` and `Markers`. Old preference files load unchanged.
- `PlayerMarkers`: `Current(state, media)` (episode markers override series markers), `Set(state, media, markers, series, duration)` (returns false for invalid ranges or unknown duration), `Clear`, `ActiveSkip(markers, elapsed, duration)` → ("Skip Intro"/"Skip Outro", range), `AutomaticSkipTarget(state, media, elapsed, duration, seekable)` → seconds to seek to, `EditorDefaults(current, duration)`. Call `state.Save()` after changes.
- `PlaybackSequence`: `HasPrevious(url)`, `HasNext(url)`, `Previous`, `Next` (distinct titles/episodes, not encodes), `CurrentVersions(current)` for "Try another version".
- `PlaybackChoices.For(versions, requested)` → chooser data (`Versions`, `PreferredUrl`) or null to play directly. `PlaybackChoices.FromListing(requested, entries, artwork, corrections)` finds releases in an unindexed folder. Use `store.Variants(media)` first.

## 6. Offline Library

```csharp
var titles = OfflineLibrary.Titles(appStore.Items, store.Personal);
var visible = OfflineLibrary.Filter(titles, query, hideMissing);
var start = OfflineLibrary.Start(title);   // Choose / Play(Path) / Unavailable
```

`OfflineTitle.Summary` = "N available version(s) • N missing". `OfflineTitle.ReconnectHint` for titles without files. Nothing in this class deletes or changes files.

## 7. Personal Library export/import

```csharp
var transfer = new PersonalLibraryTransfer(appStore, store, () => playerState, s => { playerState = s; s.Save(); });
byte[] json = transfer.Export();                                   // save with a file dialog
var preview = PersonalLibraryTransfer.Preview(File.ReadAllBytes(path));   // counts + Summary + PreviousDownloadDirectory
var result = transfer.Import(preview.Archive, replace: false);     // merge; replace: true replaces
ShowToast(result.Message);
```

- The file format matches the macOS export. Files move between platforms in both directions.
- Export removes URL credentials, queries and fragments. It excludes API keys, passwords, aria2/VLC overrides and paths.
- Import writes `Myra-before-import-{id}.json` (full export) and `EntertainmentPersonal.backup-{id}.json` first. On failure it restores sources, settings and personal data and rethrows.
- The imported download folder is only reported (`PreviousDownloadDirectory`). It is never applied.
- Merge keeps local preferences (notifications), unions sets and keeps the newer history record.
- `PersonalLibraryTransfer.Explanation` is the dialog text.

## 8. Myra AI (`AiService`)

- Providers: Disabled (default), OpenAI and Claude with the user's own API key. Apple on-device AI is macOS only. Nothing runs without an explicit request.
- Cloud permissions are separate settings: `AiPermission.Cloud` (`CloudConsent`), `History` (`ShareHistory`), `Subtitles` (`ShareSubtitles`).
- `MissingPermissions(request)` lists the permissions a request needs and the settings do not grant. History is needed for Viewing Insights, and for search-style features (`ExtractsIntent`) when the request mentions unwatched, unfinished or watchlist titles (`AiConsent.MayUseHistory`, one word list shared with `AiSearchIntent.Normalized`). The UI asks once per missing permission and sends nothing if the user declines.
- `ExecuteAsync` checks the same permissions before it reads the key, uses the cache or counts a generation. It fails with `AiException { Kind = Consent, Permission = … }`. Retry logic switches on `Permission`, never on the message text.
- Daily limit: features with an intent step use two generations. They start only when both fit in today's limit.
- Plan Tonight: the user's time budget ("Available time: N minutes") wins. A budget from the model is used only when it is smaller.
- Recap: `AiWorkspace.CompletedEpisodes` lists exactly the episodes that `ExecuteAsync` accepts (`AiWorkspace.IsCompletedEpisode`: season and episode number, and the title is watched or the file is within 10 s of its end).
