# Changelog

## 3.0.1 (build 9) — 2026-10-07

- Fix sparse-library and mood-based AI picks: empty/default model filters no longer exclude titles, mood queries rank suggestions, and only request-backed constraints filter results.
- Keep explicit search constraints strict. Unknown runtimes remain eligible for picks; timed plans require a known runtime and show a specific budget/metadata message.
- Fix the first Pick window receiving an empty selection, prevent stale Home projections, and add Reset Home Filters when no unwatched title matches.
- Use Myra throughout source, namespaces, projects, schemes, tests, identifiers, scripts, documentation and both maintained platform branches.
- Add explicit, non-destructive profile migration with original data and credentials retained for rollback.
- Establish clean initial histories: `master` for macOS and `desktop-windows-linux` for the preserved desktop port. Archived history and previous release are kept outside the maintained repository.


## 3.0.0 (build 8) — 2026-10-07

- Introduce the Myra app name and AI workspace.
- Add all 16 on-demand AI experiences: picks, natural-language search, explanations, summaries, collection suggestions, similarity, comparisons, timed plans, preferences, mood tags, assistant, reviewed actions, grouping suggestions, completed-episode subtitle recaps, translations, and viewing insights.
- Add Apple on-device, OpenAI, Claude, and Disabled providers; model/settings UI, Keychain keys, separate cloud/history/subtitle permissions, cancellation, daily limits, caches, and estimated usage.
- Validate AI suggestions against indexed titles, reject unknown IDs and over-budget plans, select concrete episode versions, and preview/undo supported personal actions.
- Add local fuzzy Global search with exact toggle, Unicode normalization, typo/token tolerance, metadata aliases, bounded ranked retrieval, pagination, and backed-up schema-4 migration.
- Add four icon families with Light/Dark/Glass variants, a layered system icon, and a persistent runtime Dock selector.
- Add free-first nearby/cache/manual subtitles, explicit optional provider search, and OpenSubtitles.org browser fallback.
- Refresh the complete README, release build scripts, and native/fixture regression coverage.

### Compatibility

macOS 14+ remains the playback/library baseline; Apple AI needs macOS 26+ and available Apple Intelligence. Cloud generation requires a compatible model and the user's API account. Runtime icons affect Dock; Finder uses the primary signed icon. Personal distribution is ad-hoc signed, not notarized. Restore the pre-migration index backup or rebuild it when downgrading.

## 2.0.5 (build 7)

- Move Pick Something into a dedicated detail window with Another Pick and playback actions.
- Retain incremental/selective refresh with the existing index available during scans, per-folder scheduling, and streamlined card menus.

Earlier feature delivery and verification are documented in `docs/V2-implementation.md` as historical verification notes.
