# Myra 3.0 implementation contract

Approved scope: visible rename; sixteen on-demand AI experiences; Apple default plus configurable OpenAI/Claude; local fuzzy Global search; four runtime icon families across Light/Dark/Glass; free-first subtitles; full README/changelog; foreground tests, DMG, commit/tag/push and GitHub release.

## Architecture and boundaries

`MyraAIStore` orchestrates explicit requests, bounded local retrieval, temporary model-facing aliases, daily generation accounting, cancellation and cache. `MyraAIProviderClient` supports Apple Foundation Models and fixed HTTPS cloud APIs. Keys use independent device-local Keychain services. `MyraAIViews` provides task/selection/results/settings and reviewable personal actions. Existing playback and index architecture remain authoritative.

AI never starts during indexing or launch, receives no media bytes or source URLs, and cannot run SQL/tools/filesystem commands. Cloud catalogue, history and subtitle permissions are separate. Changing settings cancels outstanding generation. Imported text is untrusted input. Returned title IDs must map to the provided shortlist. Plans use known durations and a concrete episode version. Recaps require a completed selected episode and imported subtitles, exclude show-wide overview, and use a bounded excerpt.

`FuzzySearch` uses portable FTS5 unicode61 grams and bounded candidate ranking. Schema 4 backs up older indexes, maintains document changes through SQL triggers, and includes metadata aliases refreshed after enrichment. Exact mode and deterministic pagination remain available.

`MyraAppearance` uses persistent Dock selections. Native Icon Composer assets provide a layered primary system icon; runtime changes never modify the signed application. Internal bundle identifier, database names and existing credential services remain unchanged.

## Acceptance and verification

- Grounded fixture responses, consent before cloud calls, unknown-ID rejection, time budget checks, concrete episode playback, cache/quota/cancellation and privacy sanitation.
- Native AI workspace keyboard action and disabled/settings layouts; Home, episode chooser, fullscreen and media workflow regression checks.
- Fuzzy typo, token-order, metadata-alias, Unicode, exact-mode, deterministic paging, migration/index regression checks; opt-in synthetic 100k benchmark is not live performance evidence.
- Four icon families and all twelve exports; no signed-bundle mutation.
- Explicit subtitle provider calls, manual/cache/local alternatives, validated downloads and existing subtitle regression checks.
- Foreground format/lint, full SwiftPM tests, Release build, signature verification, DMG integrity and SHA-256; no paid live cloud inference.

## Rollback and deferred work

Keep the prior application and data backups. Downgrading requires restoring the matching index backup or rebuilding the disposable cache; preserve personal data and original source/download store. Do not run both applications concurrently.

Deferred: tunneling, iOS port, custom model training, automatic whole-library inference, media transcription, full-transcript recap and cross-device/cloud sync. Live cloud model compatibility/account billing and live subtitle quotas need the user's account; offline tests do not establish these. Apple availability and supported languages depend on the device. Estimated tokens are not billed usage. Distribution is personal/ad-hoc, not notarized.

## Release verification record — 2026-10-07

On macOS 27.0.1 / Xcode 27.0, the foreground verification suite ran 127 checks with zero failures and four opt-in checks skipped. Native fullscreen, real synthetic HTTP/VLC workflows, Home/chooser/AI snapshots, every AI feature via controlled fixtures, action persistence/undo, provider HTTP contracts, redirect rejection and icon exports passed. Real Apple on-device summary and Smart Pick generations passed; no paid cloud inference ran. A final episode-runtime regression then passed in the focused nine-test AI suite (one opt-in live check skipped in that rerun). The Release bundle weak-links Foundation Models and used its earlier compatibility identifiers; older macOS hardware was not directly tested. A separate opt-in synthetic 100k fuzzy benchmark passed; its selective query is not a live-library or broad-query performance claim.

Final correction review also covered whitespace normalization and an omitted year: corrections retain the existing year and use the actual regrouped identity, preserving watchlist/watched/collection links. The focused AI/action rerun covered 13 checks with zero failures and one opt-in live check skipped. Release packaging was rebuilt after these guards.


## 3.0.1 clean start and recommendation fixes

Approved scope includes macOS and Windows/Linux. Maintained code and branch history use Myra; macOS builds follow master. Windows/Linux retains its desktop application on desktop-windows-linux. Previous history and release are archived outside the repository before clean initial commits are published.

Empty/sentinel intent values and model-invented constraints must not discard indexed titles. Mood requests rank suggestions; specific natural-language title searches and explicitly requested metadata constraints remain strict. Unknown runtimes are eligible for ordinary picks and AI Smart Pick. Timed plans require known concrete-version runtimes and a valid budget, and show a feature-specific recovery message.

Ordinary Home Pick waits for the projection corresponding to the current filters, opens Details in a sheet, and offers filter reset on an empty selection. AI providers, privacy permissions, action review/undo and no-automatic-generation rules are retained.

Rename includes project, module, schemes, storage and Keychain service identifiers. Explicit generic migration utilities copy profiles and credentials, preserve originals and configured download paths, and reject overwrites. Local credentials and profile backups must never be published. Native Windows/Linux execution and older macOS hardware require separate verification; macOS-hosted desktop tests alone do not establish that coverage.


### 3.0.1 verification

Foreground macOS verification passed 134 checks with zero failures and five optional checks skipped. A focused 15-test AI suite also passed with real free Apple on-device summary and mood-based Smart Pick using a metadata/runtime-free fixture. No paid cloud generation ran. The migrated local source/download and index databases passed SQLite integrity checks and matched all original table counts; personal JSON matched exactly. Four Keychain credentials were copied without disclosure. The packaged app opened the migrated 30,095-title catalogue successfully.

Windows/Linux preservation was verified on the macOS host: 63 .NET unit tests, five profile migration safety tests, Release Avalonia and headless checker builds, and headless browse/index/search plus actual aria2 download/pause/restart/resume/content/persistence checks passed. Native Windows/Linux installers and OS-specific VLC playback were not exercised.
