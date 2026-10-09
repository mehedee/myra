using System.Globalization;
using System.Text.Json.Serialization;

namespace Myra.Core;

/// The sixteen on-demand Myra AI experiences (macOS MyraAIFeature). JSON names match macOS raw values.
public enum AiFeature
{
    SmartPick,
    NaturalSearch,
    Explain,
    Summary,
    Collection,
    Similar,
    Compare,
    Plan,
    Preferences,
    MoodTags,
    Assistant,
    Action,
    Cleanup,
    Recap,
    Translate,
    Insights,
}

public static class AiFeatures
{
    public static IReadOnlyList<AiFeature> All { get; } = Enum.GetValues<AiFeature>();

    /// Features that work on selected titles only. The UI must ask for a selection first.
    public static bool RequiresTitles(this AiFeature feature) => feature is AiFeature.Explain or AiFeature.Summary
        or AiFeature.Similar or AiFeature.Compare or AiFeature.MoodTags or AiFeature.Cleanup or AiFeature.Recap
        or AiFeature.Translate;

    /// Features that first ask the model to extract search constraints (two generations per request).
    public static bool ExtractsIntent(this AiFeature feature) => feature is AiFeature.NaturalSearch or AiFeature.SmartPick
        or AiFeature.Plan or AiFeature.Collection or AiFeature.Assistant or AiFeature.Action;

    /// Features that only describe selected titles; candidates outside the selection are not sent.
    internal static bool SelectedOnly(this AiFeature feature) => feature is AiFeature.Compare or AiFeature.Summary
        or AiFeature.Explain or AiFeature.MoodTags or AiFeature.Cleanup or AiFeature.Recap or AiFeature.Translate;

    /// Features whose responses may carry reviewed personal action proposals.
    internal static bool AllowsActions(this AiFeature feature) => feature is AiFeature.Action or AiFeature.Collection
        or AiFeature.Cleanup or AiFeature.Preferences or AiFeature.NaturalSearch or AiFeature.Assistant;

    public static string Title(this AiFeature feature) => feature switch
    {
        AiFeature.SmartPick => "Smart Pick",
        AiFeature.NaturalSearch => "Natural-language Search",
        AiFeature.Explain => "Why This Pick",
        AiFeature.Summary => "Short Summary",
        AiFeature.Collection => "Collection Suggestions",
        AiFeature.Similar => "More Like This",
        AiFeature.Compare => "Compare Titles",
        AiFeature.Plan => "Plan Tonight",
        AiFeature.Preferences => "Preference Feedback",
        AiFeature.MoodTags => "Mood Tags",
        AiFeature.Assistant => "Library Assistant",
        AiFeature.Action => "Library Actions",
        AiFeature.Cleanup => "Filename / Grouping Suggestions",
        AiFeature.Recap => "Episode Recap",
        AiFeature.Translate => "Translate Description",
        AiFeature.Insights => "Viewing Insights",
        _ => feature.ToString(),
    };

    /// One-line guidance shown under the request box (same text as macOS).
    public static string Hint(this AiFeature feature) => feature switch
    {
        AiFeature.SmartPick => "Describe your mood, themes, or preferences. Suggestions come from your library.",
        AiFeature.NaturalSearch => "Example: unwatched Korean thrillers after 2018.",
        AiFeature.Explain => "Choose a title and explain why it fits your request.",
        AiFeature.Summary => "Choose a title for a concise, spoiler-conscious synopsis.",
        AiFeature.Collection => "Describe a themed collection; review suggested titles before saving.",
        AiFeature.Similar => "Choose a title to find related titles in your library.",
        AiFeature.Compare => "Select two or more titles to compare verified information.",
        AiFeature.Plan => "Include your time budget, e.g. 120 minutes. Unknown runtimes are excluded.",
        AiFeature.Preferences => "Describe what you enjoy or dislike; review before saving this feedback.",
        AiFeature.MoodTags => "Suggest interpretive mood tags from a title's description.",
        AiFeature.Assistant => "Ask about your library, watchlist, or unfinished shows.",
        AiFeature.Action => "Ask to add titles to your watchlist or create a collection; review the proposed action.",
        AiFeature.Cleanup => "Select a title to propose a cleaned title/year. Original media files are never renamed.",
        AiFeature.Recap => "Select a completed episode and import its subtitle text. Recaps can contain spoilers.",
        AiFeature.Translate => "Choose a title and your target language in AI Settings.",
        AiFeature.Insights => "Describe viewing patterns from locally calculated history statistics.",
        _ => "",
    };
}

/// Saved in AppPaths.AiSettingsPath. Never contains API keys.
/// Provider is an IAiProvider.Id ("disabled", "openAI", "claude"). Model "" means "not selected yet":
/// as on macOS there is no built-in default model; the user picks one (Refresh Models lists them).
public sealed record AiSettings
{
    public string Provider { get; init; } = AiProviderIds.Disabled;
    public string Model { get; init; } = "";
    /// Permission to send compact catalogue data and the request to a cloud provider.
    public bool CloudConsent { get; init; }
    /// Separate permission to include watched / unfinished / watchlist data in cloud requests.
    public bool ShareHistory { get; init; }
    /// Separate permission to include imported subtitle text in cloud recap requests.
    public bool ShareSubtitles { get; init; }
    /// Include the saved explicit preference text (Feedback) in requests.
    public bool Personalize { get; init; } = true;
    /// Response language (for example "en" or "bn"). Also the Translate target.
    public string Language { get; init; } = "en";
    /// 1…500 generations per local day. Intent extraction counts as a generation.
    public int DailyRequestLimit { get; init; } = 20;
    /// 256…2000. A local provider may cap it lower (IAiProvider.MaximumOutputTokens).
    public int MaximumOutputTokens { get; init; } = 1200;
    /// Explicit preference text written by the user. Sanitised, at most 1000 characters.
    public string Feedback { get; init; } = "";
    public IReadOnlyList<AiFeature> EnabledFeatures { get; init; } = AiFeatures.All;

    public bool IsEnabled(AiFeature feature) => EnabledFeatures.Contains(feature);

    public AiSettings WithFeature(AiFeature feature, bool enabled) => this with
    {
        EnabledFeatures = enabled
            ? EnabledFeatures.Append(feature).Distinct().Order().ToList()
            : EnabledFeatures.Where(f => f != feature).ToList(),
    };

    /// Changing provider also clears the model, because model identifiers differ per provider.
    public AiSettings WithProvider(string provider) => provider == Provider ? this : this with { Provider = provider, Model = "" };

    /// Clamps limits and removes unsafe values. Applied when settings are loaded or changed.
    public AiSettings Normalized()
    {
        var model = (Model ?? "").Trim();
        if (model.Length > 200 || model.Contains('\n') || model.Contains('\r')) model = "";
        var language = AiPrivacy.Text((Language ?? "").Trim(), 40);
        return this with
        {
            Provider = string.IsNullOrWhiteSpace(Provider) ? AiProviderIds.Disabled : Provider,
            Model = model,
            Language = language.Length == 0 ? "en" : language,
            DailyRequestLimit = Math.Clamp(DailyRequestLimit, 1, 500),
            MaximumOutputTokens = Math.Clamp(MaximumOutputTokens, 256, 2000),
            Feedback = AiPrivacy.Text(Feedback ?? "", 1000),
            EnabledFeatures = (EnabledFeatures ?? AiFeatures.All).Where(f => Enum.IsDefined(f)).Distinct().Order().ToList(),
        };
    }
}

public sealed record AiRecommendation(
    [property: JsonPropertyName("titleID")] string TitleId,
    string Reason,
    [property: JsonPropertyName("versionID")] string? VersionId = null);

public enum AiActionKind
{
    AddWatchlist,
    MarkWatched,
    Collection,
    Correction,
    Preference,
    ShowResults,
    Play,
}

/// A reviewed proposal. Nothing runs until the user presses Apply. TitleIds are real catalogue IDs.
public sealed record AiAction(AiActionKind Kind, IReadOnlyList<string> TitleIds, string Value, string? Year = null, EntertainmentKind? MediaKind = null)
{
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public string Label => Kind switch
    {
        AiActionKind.AddWatchlist => "Add to Watchlist",
        AiActionKind.MarkWatched => "Mark Watched",
        AiActionKind.Collection => $"Create Collection: {Value}",
        AiActionKind.Correction => $"Correct Match: {Value}",
        AiActionKind.Preference => "Save Preference Feedback",
        AiActionKind.ShowResults => "Show Matching Titles",
        AiActionKind.Play => "Play Selected Title",
        _ => Kind.ToString(),
    };

    /// Personal changes applied by AiService.ApplyAsync. ShowResults and Play are UI actions.
    public bool ChangesPersonalData => Kind is AiActionKind.AddWatchlist or AiActionKind.MarkWatched
        or AiActionKind.Collection or AiActionKind.Correction or AiActionKind.Preference;

    internal static readonly IReadOnlyDictionary<string, AiActionKind> WireKinds = new Dictionary<string, AiActionKind>(StringComparer.Ordinal)
    {
        ["addWatchlist"] = AiActionKind.AddWatchlist,
        ["markWatched"] = AiActionKind.MarkWatched,
        ["collection"] = AiActionKind.Collection,
        ["correction"] = AiActionKind.Correction,
        ["preference"] = AiActionKind.Preference,
        ["showResults"] = AiActionKind.ShowResults,
        ["play"] = AiActionKind.Play,
    };
}

/// A validated, grounded result. Every title ID is a real catalogue ID from the shortlist.
public sealed record AiResponse(
    string Summary,
    IReadOnlyList<AiRecommendation> Recommendations,
    IReadOnlyList<AiAction> Actions,
    IReadOnlyList<string> Tags,
    string Provider,
    string SourceNote);

/// Model-facing title data. Id is a temporary alias ("t1"…), never a source URL or path.
public sealed record AiCandidate
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Year { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string Synopsis { get; init; } = "";
    public double? Rating { get; init; }
    public string? Language { get; init; }
    public int? RuntimeMinutes { get; init; }
    public bool? Watched { get; init; }
    public bool? Unfinished { get; init; }
    public bool? Watchlisted { get; init; }
    public IReadOnlyList<string>? Filenames { get; init; }
}

/// Search constraints extracted by the model. Only request-backed values may exclude titles (see Normalized).
public sealed record AiSearchIntent
{
    public string? Query { get; init; }
    public string? Genre { get; init; }
    public string? Language { get; init; }
    public string? Kind { get; init; }
    public int? MinimumYear { get; init; }
    public int? MaximumYear { get; init; }
    public double? MinimumRating { get; init; }
    public bool? Unwatched { get; init; }
    public bool? Unfinished { get; init; }
    public bool? Watchlisted { get; init; }
    public int? MaximumMinutes { get; init; }

    private static readonly HashSet<string> Sentinels = ["", "any", "all", "none", "null", "unknown", "unspecified"];

    /// Models can emit empty strings, sentinels or invented values for optional fields. A constraint
    /// survives only when the user's request backs it; moods stay ranking guidance (3.0.1 fix).
    public AiSearchIntent Normalized(string prompt)
    {
        var request = prompt.ToLowerInvariant();
        bool Mentions(params string[] words) => words.Any(w => request.Contains(w, StringComparison.OrdinalIgnoreCase));
        var numbers = new List<double>();
        foreach (var piece in SplitKeeping(request, c => char.IsDigit(c) || c == '.'))
            if (double.TryParse(piece, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) numbers.Add(number);

        var genre = Meaningful(Genre);
        if (genre is not null && !Mentions(genre)) genre = null;

        var language = Meaningful(Language)?.ToLowerInvariant();
        if (language is not null)
        {
            var requestWords = SplitKeeping(request, char.IsLetter).ToHashSet(StringComparer.Ordinal);
            if (!LanguageWords(language).Any(requestWords.Contains)) language = null;
        }

        var kind = Meaningful(Kind)?.ToLowerInvariant() switch
        {
            "movie" or "movies" or "film" or "films" => Mentions("movie", "film") ? "movie" : null,
            "series" or "tv" or "show" or "shows" or "tv series" or "tv show" => Mentions("series", "show", "tv") ? "series" : null,
            _ => null,
        };

        bool ValidYear(int? year) => year is >= 1800 and <= 9999 && numbers.Contains(year.Value);
        var rating = MinimumRating is { } r && r is >= 0 and <= 10 && numbers.Contains(r)
            && Mentions("rating", "rated", "score", "star", "/10") ? MinimumRating : null;

        return new AiSearchIntent
        {
            Query = Meaningful(Query),
            Genre = genre,
            Language = language,
            Kind = kind,
            MinimumYear = ValidYear(MinimumYear) ? MinimumYear : null,
            MaximumYear = ValidYear(MaximumYear) ? MaximumYear : null,
            MinimumRating = rating,
            Unwatched = Unwatched == true && !AiConsent.Mentions(prompt, AiConsent.UnwatchedWords) ? null : Unwatched,
            Unfinished = Unfinished == true && !AiConsent.Mentions(prompt, AiConsent.UnfinishedWords) ? null : Unfinished,
            Watchlisted = Watchlisted == true && !AiConsent.Mentions(prompt, AiConsent.WatchlistWords) ? null : Watchlisted,
            // The user's budget wins. The model may only narrow it, never raise or invent it.
            MaximumMinutes = MaximumMinutes is > 0 and var model && AiContext.TimeBudget(prompt) is { } budget && model <= budget
                ? model
                : null,
        };
    }

    private static string? Meaningful(string? value)
    {
        if (value is null) return null;
        var cleaned = value.Trim();
        return Sentinels.Contains(cleaned.ToLowerInvariant()) ? null : cleaned;
    }

    private static IEnumerable<string> SplitKeeping(string text, Func<char, bool> keep)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && keep(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }

    /// The ISO code plus its English and native names ("ko", "korean", "한국어"), lowercased.
    internal static IEnumerable<string> LanguageWords(string code)
    {
        yield return code;
        if (FallbackLanguageNames.TryGetValue(code, out var fallback)) yield return fallback;
        CultureInfo? culture = null;
        try
        {
            culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
        }
        if (culture is null || culture.Equals(CultureInfo.InvariantCulture)) yield break;
        foreach (var name in new[] { culture.EnglishName, culture.NativeName, culture.DisplayName })
        {
            var lower = name.ToLowerInvariant();
            yield return lower;
            var first = lower.Split([' ', '('], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null) yield return first;
        }
    }

    // Used when the runtime has no culture data (invariant globalization mode).
    private static readonly Dictionary<string, string> FallbackLanguageNames = new(StringComparer.Ordinal)
    {
        ["en"] = "english", ["ko"] = "korean", ["ja"] = "japanese", ["zh"] = "chinese", ["hi"] = "hindi", ["bn"] = "bengali",
        ["fr"] = "french", ["de"] = "german", ["es"] = "spanish", ["it"] = "italian", ["pt"] = "portuguese", ["ru"] = "russian",
        ["ar"] = "arabic", ["tr"] = "turkish", ["ta"] = "tamil", ["te"] = "telugu", ["ml"] = "malayalam", ["th"] = "thai",
        ["sv"] = "swedish", ["da"] = "danish", ["no"] = "norwegian", ["nl"] = "dutch", ["pl"] = "polish", ["fa"] = "persian",
        ["ur"] = "urdu", ["id"] = "indonesian", ["vi"] = "vietnamese", ["he"] = "hebrew", ["el"] = "greek", ["fi"] = "finnish",
    };
}

/// The three separate cloud permissions (AiSettings.CloudConsent, ShareHistory, ShareSubtitles).
public enum AiPermission
{
    /// Request text and a short list of catalogue titles.
    Cloud,
    /// Watched, unfinished and watchlist markers and local statistics.
    History,
    /// An excerpt of imported subtitle text for a recap.
    Subtitles,
}

/// Decides from the request alone whether a cloud request may use viewing history. The word lists are
/// the only source: AiSearchIntent.Normalized keeps a history filter only when the request uses them.
public static class AiConsent
{
    public static readonly IReadOnlyList<string> UnwatchedWords = ["unwatched", "not watched", "not seen", "haven't seen"];
    public static readonly IReadOnlyList<string> UnfinishedWords = ["unfinished", "resume", "continue", "in progress"];
    public static readonly IReadOnlyList<string> WatchlistWords = ["watchlist", "watch list", "saved to watch"];

    /// True when this feature and request text can need history: Viewing Insights always, and features
    /// that extract search constraints when the request mentions unwatched, unfinished or watchlist titles.
    public static bool MayUseHistory(AiFeature feature, string prompt) =>
        feature == AiFeature.Insights
        || (feature.ExtractsIntent()
            && (Mentions(prompt, UnwatchedWords) || Mentions(prompt, UnfinishedWords) || Mentions(prompt, WatchlistWords)));

    /// Case-insensitive phrase match. Curly apostrophes count as straight ones ("haven’t seen").
    public static bool Mentions(string prompt, IEnumerable<string> words)
    {
        var text = Straighten(prompt);
        return words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static string Straighten(string text) =>
        text.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02BC', '\'').Replace('\uFF07', '\'');
}

/// Raw model output before validation. Title IDs are model-facing aliases.
public sealed record AiWireAction
{
    [JsonRequired] public string Kind { get; init; } = "";
    [JsonRequired, JsonPropertyName("titleIDs")] public IReadOnlyList<string> TitleIds { get; init; } = [];
    [JsonRequired] public string Value { get; init; } = "";
    public string? Year { get; init; }
    public string? MediaKind { get; init; }
}

public sealed record AiWireRecommendation
{
    [JsonRequired, JsonPropertyName("titleID")] public string TitleId { get; init; } = "";
    [JsonRequired] public string Reason { get; init; } = "";
}

public sealed record AiWireResponse
{
    [JsonRequired] public string Summary { get; init; } = "";
    [JsonRequired] public IReadOnlyList<AiWireRecommendation> Recommendations { get; init; } = [];
    [JsonRequired] public IReadOnlyList<AiWireAction> Actions { get; init; } = [];
    [JsonRequired] public IReadOnlyList<string> Tags { get; init; } = [];
}

public enum AiErrorKind
{
    Unavailable,
    MissingKey,
    Consent,
    Limit,
    InvalidResponse,
    UnsafeAction,
    MissingContext,
    Http,
}

/// Every AI failure. Message is user-facing text (same wording as macOS where it exists).
public sealed class AiException : Exception
{
    public AiException(AiErrorKind kind, string? message = null, int? status = null, Exception? inner = null, AiPermission? permission = null)
        : base(message ?? DefaultMessage(kind, status), inner)
    {
        Kind = kind;
        Status = status;
        Permission = permission;
    }

    public AiErrorKind Kind { get; }
    /// For AiErrorKind.Consent: the missing permission. Nothing was sent and no generation was used.
    public AiPermission? Permission { get; }

    /// A cloud permission is missing (Kind = Consent).
    public static AiException MissingPermission(AiPermission permission) => new(AiErrorKind.Consent, permission switch
    {
        AiPermission.History =>
            "Enable history sharing in AI Settings for cloud requests about watched titles, unfinished titles, or your watchlist.",
        AiPermission.Subtitles => "Allow selected subtitle text in cloud recap requests in AI Settings before sending subtitles.",
        _ => null,
    }, permission: permission);
    /// HTTP status for AiErrorKind.Http.
    public int? Status { get; }

    public static AiException Http(int status) => new(AiErrorKind.Http, status: status);

    private static string DefaultMessage(AiErrorKind kind, int? status) => kind switch
    {
        AiErrorKind.Unavailable => "This AI feature is unavailable.",
        AiErrorKind.MissingKey => "Save your own provider API key in AI Settings.",
        AiErrorKind.Consent => "Enable cloud requests in AI Settings before sending catalogue information.",
        AiErrorKind.Limit => "Your daily AI request limit has been reached. Adjust it in AI Settings if intended.",
        AiErrorKind.InvalidResponse => "The model returned an incomplete or ungrounded response. No changes were applied.",
        AiErrorKind.UnsafeAction => "This proposed action is unsupported or no longer matches the library.",
        AiErrorKind.MissingContext => "More information is needed for this request.",
        AiErrorKind.Http => $"AI provider request failed (HTTP {status}). Check your model, account, and API limits.",
        _ => "AI request failed.",
    };
}
