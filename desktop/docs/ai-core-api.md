# Myra AI core API (Windows/Linux)

This document describes the non-UI part of Myra AI. It is a port of macOS Myra 3.0.1
(`MyraAIModels.swift`, `MyraAIProviders.swift`, `MyraAIStore.swift`).
All types are in the `Myra.Core` namespace. Source files are in `src/Myra.Core/Ai/` and `src/Myra.Core/EntertainmentStore.Ai.cs`.

## Rules for the UI layer

1. Create one `AiService` for the app. Run requests only when the user presses **Ask Myra**. Never start AI at launch, during indexing, or when the catalogue changes.
2. `AiService.Changed` can fire on worker threads. Marshal it to the UI thread, then read the state properties.
3. Pass `store.Catalogue`, `store.Personal` and `store.ProjectionRevision` to each request.
4. Show **Apply** for each proposed `AiAction`. Call `AiService.ApplyAsync`. Never change personal data from a model answer without Apply.
5. `ShowResults` and `Play` actions are UI-only: select the titles, or start playback after the user clicks.
6. Label token counts as estimates. Show `AiService.UsageNote` near them and `AiResponse.SourceNote` under results.
7. Show "This request uses your selected cloud provider and may incur charges." when `AiWorkspace.IsCloud(ai)` is true.

## Data locations

| Data | Path |
|---|---|
| AI settings (no keys) | `AppPaths.AiSettingsPath` → `{DataDirectory}/MyraAI.json` |
| Daily generation count | `AppPaths.AiUsagePath` → `{DataDirectory}/MyraAIUsage.json` |
| OpenAI key | `ISecretStore`, name `SecretNames.OpenAiApiKey` (`ai.openai.api-key`) |
| Claude key | `ISecretStore`, name `SecretNames.ClaudeApiKey` (`ai.claude.api-key`) |
| Result cache | Memory only, 24 entries, cleared by any settings or key change |

Keys are never written to settings, personal exports or request bodies other than the auth header.

## Setup

```csharp
var ai = new AiService(secretStore);           // OpenAI + Claude, production HTTP clients
ai.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
```

Constructor: `AiService(ISecretStore secrets, IEnumerable<IAiProvider>? providers = null, string? settingsPath = null, string? usagePath = null, Func<DateTimeOffset>? now = null)`.
Tests pass fake providers, temporary paths and a fixed clock.

## Settings (`AiSettings`, immutable record)

| Member | Default | Notes |
|---|---|---|
| `Provider` | `"disabled"` | `AiProviderIds.Disabled` / `OpenAI` (`"openAI"`) / `Claude` (`"claude"`) or a custom provider ID |
| `Model` | `""` | No built-in default model (same as macOS). The user types one or picks from `ai.Models`. |
| `CloudConsent` | false | Required before any cloud generation |
| `ShareHistory` | false | Required for cloud requests about watched, unfinished or watchlist titles, and for Viewing Insights |
| `ShareSubtitles` | false | Required to send imported subtitle text to a cloud provider |
| `Personalize` | true | Sends the saved `Feedback` text |
| `Language` | `"en"` | Response language and Translate target |
| `DailyRequestLimit` | 20 | Clamped to 1…500 |
| `MaximumOutputTokens` | 1200 | Clamped to 256…2000 |
| `Feedback` | `""` | Explicit preference text, sanitised, ≤ 1000 characters |
| `EnabledFeatures` | all 16 | `IsEnabled(f)`, `WithFeature(f, on)` |

Change settings with `ai.Settings = ai.Settings with { … }` or `ai.UpdateSettings(s => …)`.
Each change cancels the running request, saves the file, clears the cache and updates `ai.Status`.
Use `settings.WithProvider(id)` for the provider picker. It also clears `Model`.

## State (`AiService`)

| Member | Use |
|---|---|
| `Response` (`AiResponse?`) | Last result: `Summary`, `Recommendations`, `Actions`, `Tags`, `Provider`, `SourceNote` |
| `IsBusy` | Disable Ask; show Cancel |
| `ErrorMessage` | User-facing text of the last failure |
| `Status` | Provider readiness, or the result of Refresh Models / Test Connection |
| `Models` | Result of `RefreshModelsAsync` |
| `RequestsToday` | Generations used today (local day) |
| `EstimatedInputTokens`, `EstimatedOutputTokens` | Characters / 4. Not billing data. |
| `CanUndo` | Show Undo for the last applied action |
| `Providers`, `CurrentProvider` | Provider picker; `CurrentProvider` is null when disabled |

## Operations

```csharp
// Settings window
ai.SaveKey(key);                    // current provider; "" deletes; throws AiException(MissingKey) for invalid keys
ai.RemoveKey(); ai.HasKey();
await ai.RefreshModelsAsync();      // lists models; Status shows the result
await ai.TestConnectionAsync();     // same call: lists models, does not test generation
ai.SaveFeedback(text); ai.ClearCache(); ai.RefreshAvailability();

// Workspace
var outcome = await ai.ExecuteAsync(new AiRequest
{
    Feature = feature,
    Prompt = AiWorkspace.ComposePrompt(feature, text, availableMinutes, recapEpisode, subtitleName),
    Titles = store.Catalogue,
    Personal = store.Personal,
    Revision = store.ProjectionRevision,
    SelectedIds = selectedIds,                       // sorted or not; order does not matter
    SubtitleText = feature == AiFeature.Recap ? subtitleText : null,
    RecapVersionId = feature == AiFeature.Recap ? episodeId : null,
});
ai.Cancel();                                         // Cancel button and window close

// Reviewed actions
await ai.ApplyAsync(action, store);                  // throws AiException(UnsafeAction) when rejected
await ai.UndoAsync(store);                           // throws AiException(MissingContext) when no longer safe
```

`ExecuteAsync` returns `AiOutcome(Status, Response, Error)`. Status is `Completed`, `Cached`, `Failed`, `Cancelled` or `Busy`.
It also updates `Response` and `ErrorMessage`. A cancelled request leaves both empty.

### Helpers (`AiWorkspace`)

- `CanAsk(ai, feature, catalogueCount, selectedCount, recapVersionId, hasSubtitles)` – enables the Ask button.
- `AiFeatures.All`, `feature.Title()`, `feature.Hint()`, `feature.RequiresTitles()`, `feature.ExtractsIntent()` (two generations).
- `MatchingTitles(catalogue, query, 60)` – title picker. `MaximumSelection` = 10. Compare needs 2; Recap allows one series.
- `CompletedEpisodes(title, personal)` – the episodes a recap may use.
- `ReadSubtitleFile(path)` – SRT/ASS/SSA/VTT/TXT, ≤ 2 MB, UTF-8 or UTF-16. Throws `AiException` with a user message. Keep the text for one request only.
- `PlaybackTitle(title, recommendation.VersionId)` – a timed plan plays the validated episode file.
- `AiContext.Runtime(title)` – "Known runtime: N minutes" on result cards (null = unknown).
- `action.Label`, `action.ChangesPersonalData` – action preview.

## The 16 experiences

| macOS feature | `AiFeature` | Selection | Notes |
|---|---|---|---|
| Smart Pick | `SmartPick` | none | Excludes watched titles when history is allowed. Unknown runtimes are eligible. |
| Natural-language Search | `NaturalSearch` | none | A specific title query stays strict. May propose actions. |
| Why This Pick | `Explain` | 1+ | |
| Short Summary | `Summary` | 1+ | Longer synopsis (1000 characters) |
| Collection Suggestions | `Collection` | optional | Proposes `Collection` actions |
| More Like This | `Similar` | 1 | Reference excluded; shared genres rank higher |
| Compare Titles | `Compare` | 2+ | |
| Plan Tonight | `Plan` | none | Needs a budget ("120 minutes") and known runtimes. Returns `VersionId` per title. |
| Preference Feedback | `Preferences` | none | Proposes a `Preference` action (saved as `Feedback`) |
| Mood Tags | `MoodTags` | 1+ | `Tags` |
| Library Assistant | `Assistant` | none | Gets local statistics when history is allowed |
| Library Actions | `Action` | optional | Proposes watchlist / watched / collection actions |
| Filename / Grouping Suggestions | `Cleanup` | 1 | Sends file names only. `Correction` changes catalogue identity, never files. |
| Episode Recap | `Recap` | 1 series | Needs a completed episode and imported subtitles; 3000-character excerpt; no overview sent |
| Translate Description | `Translate` | 1+ | Target is `Settings.Language` |
| Viewing Insights | `Insights` | none | Local statistics; cloud needs `ShareHistory` |

## Request flow and privacy rules

1. Feature enabled and provider chosen, else "This AI feature is disabled in Settings."
2. Cloud: `CloudConsent` required. Subtitles: `ShareSubtitles` also required. No HTTP request is made before these checks.
3. Recap: the selected series must contain the episode, the episode must be completed, and subtitle text must not be empty.
4. Cloud: a saved key is required.
5. Cache lookup (provider, model, feature, revision, prompt, selection, episode, subtitle hash, feedback).
6. Intent features: one generation extracts constraints (350 tokens). `AiSearchIntent.Normalized` drops empty or sentinel values ("", "any", "none", …) and any constraint the request does not mention (3.0.1 fix). Moods only rank.
7. `AiContext.Prepare` builds a shortlist of at most 12 titles off the UI thread. Titles get temporary aliases `t1…t12`. No source URL, path, folder or real title ID is sent. Links, paths (macOS, Linux, Windows, UNC) and IPv4 addresses are redacted from all text. The prompt is halved until it fits 18000 UTF-8 bytes.
8. A second generation produces the answer. `AiService.Validate` rejects unknown aliases, oversized fields, unknown action kinds, actions for the wrong feature, invalid corrections, and plans over budget or with unknown runtimes. "No changes were applied."
9. Each generation counts against the daily limit before it is sent.

## Providers

| Provider | Endpoint | Auth | Body |
|---|---|---|---|
| OpenAI | `POST https://api.openai.com/v1/responses` | `Authorization: Bearer {key}` | `model`, `instructions`, `input`, `max_output_tokens`, `store: false` |
| Claude | `POST https://api.anthropic.com/v1/messages` | `x-api-key`, `anthropic-version: 2023-06-01` | `model`, `system`, `max_tokens`, `messages: [{role: user, content}]` |
| Models | `GET {base}/models` | same | `data[].id`, sorted |

- Answer text: OpenAI `output[].content[]` with `type = output_text`; Claude `content[]` with `type = text`. Parts are joined with a newline.
- No redirects (a 3xx is "HTTP 302" and is not followed). The answer must come from the fixed URL. No cookies. 2 MB response limit. 90 s timeout.
- Output tokens: `max(256, min(2000, MaximumOutputTokens))`.
- Apple on-device AI is macOS-only and is not available here. To add a local model, implement `IAiProvider` with `IsCloud = false`, `SecretName = null`, a smaller `ContextByteLimit` (macOS used 4000 for Apple) and `MaximumOutputTokens` (800), and pass it to the `AiService` constructor with the built-in providers.

## Errors (`AiException.Kind`)

`Unavailable`, `MissingKey`, `Consent`, `Limit`, `InvalidResponse`, `UnsafeAction`, `MissingContext`, `Http` (`Status`). `Message` is user-facing text.

## Store methods for reviewed actions (`EntertainmentStore.Ai.cs`)

- `ApplyReviewedAiAction(action)` – atomic write of watchlist, watched, collection or correction. Returns the new personal snapshot. Never touches files or history.
- `RestoreReviewedAiSnapshot(snapshot)` – keeps current history and known episodes. Returns true when the catalogue must be reloaded.

Use them through `AiService.ApplyAsync` / `UndoAsync`. Undo is refused when the personal data or feedback changed after the action, or for a different store.
