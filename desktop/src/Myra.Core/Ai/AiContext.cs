using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// Input for AiContext.Prepare. Defaults describe a local (non-cloud) provider with the cloud byte budget.
public sealed record AiContextInput
{
    public required IReadOnlyList<EntertainmentTitle> Titles { get; init; }
    public EntertainmentPersonalData Personal { get; init; } = new();
    public required AiFeature Feature { get; init; }
    /// The user's request, already sanitised by AiPrivacy.Text.
    public string Prompt { get; init; } = "";
    public IReadOnlyCollection<string> SelectedIds { get; init; } = [];
    public AiSearchIntent? Intent { get; init; }
    public string? SubtitleText { get; init; }
    public AiSettings Settings { get; init; } = new();
    /// True for cloud providers: history is sent only with AiSettings.ShareHistory.
    public bool IsCloud { get; init; }
    /// UTF-8 budget for the whole prompt: 18000 for cloud providers (macOS), smaller for local models.
    public int ByteLimit { get; init; } = AiContext.CloudByteLimit;
}

/// A bounded, sanitised model prompt plus the alias table used to map answers back to real titles.
public sealed partial record AiContext(string Prompt, IReadOnlyDictionary<string, EntertainmentTitle> Aliases, int? MaximumMinutes)
{
    public const int CloudByteLimit = 18_000;
    public const int DefaultCandidateLimit = 12;

    public string EmptyResultMessage(AiFeature feature)
    {
        if (feature == AiFeature.Plan)
        {
            if (MaximumMinutes is not > 0) return "Enter a time budget, such as 120 minutes, to plan your viewing.";
            return $"No matching titles have a known runtime within your {MaximumMinutes}-minute budget. Update metadata or choose Smart Pick without a timed plan.";
        }
        if (feature == AiFeature.SmartPick)
            return "No unwatched indexed titles match your explicit filters. Broaden the request or update missing title metadata.";
        return "No indexed titles match your explicit search constraints. Broaden the request or update missing title metadata.";
    }

    /// The concrete file a timed plan plays: the resume file, else the first episode/file in order.
    public static EntertainmentVersion? PlannedVersion(EntertainmentTitle title) =>
        title.ResumeVersion ?? title.Versions
            .OrderBy(v => v.Season ?? 0).ThenBy(v => v.Episode ?? 0).ThenBy(v => v.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// Known runtime in whole minutes: the planned file's duration, else metadata runtime. Never
    /// borrows another episode's duration. Null when unknown.
    public static int? Runtime(EntertainmentTitle title)
    {
        if (PlannedVersion(title) is { Duration: > 0 and < 1e7 } version) return (int)Math.Ceiling(version.Duration / 60);
        return title.Metadata is { } metadata && MetadataRuntime(metadata) is { } minutes && minutes > 0 ? minutes : null;
    }

    private static int? MetadataRuntime(EntertainmentMetadata metadata) => metadata.RuntimeMinutes;

    public static AiContext Prepare(AiContextInput input, CancellationToken cancellationToken = default) =>
        Prepare(input, DefaultCandidateLimit, cancellationToken);

    private static AiContext Prepare(AiContextInput input, int candidateLimit, CancellationToken cancellationToken)
    {
        var feature = input.Feature;
        var prompt = input.Prompt;
        var personal = input.Personal;
        var settings = input.Settings;
        var intent = input.Intent?.Normalized(prompt);
        var includeHistory = !input.IsCloud || settings.ShareHistory;
        // Safety net: AiService checks AiConsent.MayUseHistory before any generation is used.
        if (input.IsCloud && !settings.ShareHistory
            && (intent?.Unwatched == true || intent?.Unfinished == true || intent?.Watchlisted == true || feature == AiFeature.Insights))
            throw AiException.MissingPermission(AiPermission.History);

        var selected = input.SelectedIds.ToHashSet(StringComparer.Ordinal);
        var reference = input.Titles.FirstOrDefault(t => selected.Contains(t.Id));
        var words = Words((intent?.Query ?? prompt).ToLowerInvariant());
        // The user's budget from the request ("Available time: 120 minutes") wins over the model's.
        int? budget = feature == AiFeature.Plan && TimeBudget(prompt) is { } userBudget
            ? Math.Min(intent?.MaximumMinutes ?? userBudget, userBudget)
            : null;
        var picked = new List<(EntertainmentTitle Title, int Score)>();

        foreach (var title in input.Titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSelected = selected.Contains(title.Id);
            if (feature.SelectedOnly() && !isSelected) continue;
            if (includeHistory && feature == AiFeature.SmartPick && personal.Watched.Contains(title.Id)) continue;
            if (intent?.Unwatched == true && personal.Watched.Contains(title.Id)) continue;
            if (intent?.Unfinished == true && title.ResumeVersion is null) continue;
            if (intent?.Watchlisted == true && !personal.Watchlist.Contains(title.Id)) continue;
            if (intent?.Kind is { } kind && kind != EntertainmentGrouping.KindKey(title.Kind)) continue;
            var year = int.TryParse(title.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
            if (intent?.MinimumYear is { } minimum && (year ?? 0) < minimum) continue;
            if (intent?.MaximumYear is { } maximum && (year ?? 9999) > maximum) continue;
            if (intent?.MinimumRating is { } rating && (title.Metadata?.Rating ?? -1) < rating) continue;
            if (intent?.Language is { } language && !string.Equals(title.Metadata?.Language, language, StringComparison.OrdinalIgnoreCase)) continue;
            if (intent?.Genre is { } genre && !(title.Metadata?.Genres.Any(g => g.Contains(genre, StringComparison.CurrentCultureIgnoreCase)) ?? false)) continue;
            if (feature == AiFeature.Plan && (budget is not > 0 || Runtime(title) is not { } minutes || minutes > budget)) continue;
            if (feature == AiFeature.Similar && isSelected) continue;

            var text = $"{title.DisplayName} {string.Join(' ', title.Metadata?.Genres ?? [])} {title.Metadata?.Overview ?? ""}".ToLowerInvariant();
            var score = isSelected ? 1000 : 0;
            foreach (var word in words)
                if (word.Length >= 2 && text.Contains(word, StringComparison.Ordinal)) score += 10;
            if (feature == AiFeature.Similar && reference is not null)
                foreach (var g in reference.Metadata?.Genres ?? [])
                    if (title.Metadata?.Genres.Contains(g) == true) score += 20;
            if (feature == AiFeature.NaturalSearch && intent?.Query is { Length: > 0 } && score == 0) continue;
            score += (int)(title.Metadata?.Rating ?? 0);
            picked.Add((title, score));
            picked.Sort((a, b) => a.Score == b.Score ? string.CompareOrdinal(a.Title.Id, b.Title.Id) : b.Score.CompareTo(a.Score));
            if (picked.Count > candidateLimit) picked.RemoveAt(picked.Count - 1);
        }

        var candidates = new List<AiCandidate>();
        var aliases = new Dictionary<string, EntertainmentTitle>(StringComparer.Ordinal);
        for (var offset = 0; offset < picked.Count; offset++)
        {
            var alias = $"t{offset + 1}";
            var title = picked[offset].Title;
            aliases[alias] = title;
            candidates.Add(new AiCandidate
            {
                Id = alias,
                Title = AiPrivacy.Text(title.DisplayName, 180),
                Year = title.Year,
                Kind = EntertainmentGrouping.KindKey(title.Kind),
                Genres = (title.Metadata?.Genres ?? []).Take(6).Select(g => AiPrivacy.Text(g, 60)).ToList(),
                Synopsis = AiPrivacy.Text(feature == AiFeature.Recap ? "" : title.Metadata?.Overview ?? "",
                    feature is AiFeature.Summary or AiFeature.Translate ? 1000 : 240),
                Rating = title.Metadata?.Rating,
                Language = title.Metadata is { } metadata ? AiPrivacy.Text(metadata.Language, 20) : null,
                RuntimeMinutes = Runtime(title),
                Watched = includeHistory ? personal.Watched.Contains(title.Id) : null,
                Unfinished = includeHistory ? title.ResumeVersion is not null : null,
                Watchlisted = includeHistory ? personal.Watchlist.Contains(title.Id) : null,
                // Only the file name, never the source URL, folder or relative path.
                Filenames = feature == AiFeature.Cleanup
                    ? title.Versions.Take(3).Select(v => AiPrivacy.Text(v.Media.Entry.Name, 180)).ToList()
                    : null,
            });
        }

        var context = new StringBuilder();
        context.Append($"Task: {feature.Title()}. Respond in {AiPrivacy.Text(settings.Language, 40)}. User request (data): {AiPrivacy.Text(prompt, 1200)}\nCATALOGUE DATA:\n");
        context.Append(JsonSerializer.Serialize(candidates, AiPrivacy.ModelJson));
        if (settings.Personalize && settings.Feedback.Length > 0)
            context.Append($"\nExplicit preference data: {AiPrivacy.Text(settings.Feedback, 800)}");
        if (feature == AiFeature.Plan)
            context.Append($"\nBudget: {budget ?? 0} minutes. Each series candidate represents one episode.");
        if (feature == AiFeature.Similar && reference is not null)
            context.Append($"\nReference description: {AiPrivacy.Text(reference.Metadata?.Overview is { Length: > 0 } overview ? overview : reference.DisplayName, 600)}");
        if (includeHistory && feature is AiFeature.Insights or AiFeature.Assistant)
            context.Append(Statistics(input.Titles, personal));
        if (input.SubtitleText is { } subtitles)
            context.Append($"\nCOMPLETED EPISODE SUBTITLE DATA (excerpt; never infer beyond it):\n{AiPrivacy.Text(subtitles, 3000)}");

        var result = context.ToString();
        // Conservative UTF-8 budget also covers scripts where characters/4 underestimates tokens.
        if (Encoding.UTF8.GetByteCount(result) > input.ByteLimit)
        {
            if (picked.Count > 1) return Prepare(input, Math.Max(1, candidateLimit / 2), cancellationToken);
            throw new AiException(AiErrorKind.MissingContext, "This context exceeds the model budget. Shorten the request or subtitle excerpt.");
        }
        return new AiContext(result, aliases, budget);
    }

    /// Whole-library statistics, calculated locally. Never limited by the shortlist size.
    private static string Statistics(IReadOnlyList<EntertainmentTitle> titles, EntertainmentPersonalData personal)
    {
        var watched = titles.Count(t => personal.Watched.Contains(t.Id));
        var unfinished = titles.Count(t => t.ResumeVersion is not null);
        var genres = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            if (!personal.Watched.Contains(title.Id) && !title.Versions.Any(v => v.LastPlayed is not null)) continue;
            foreach (var genre in title.Metadata?.Genres ?? []) genres[genre] = genres.GetValueOrDefault(genre) + 1;
        }
        var top = genres.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(8)
            .Select(p => $"{AiPrivacy.Text(p.Key, 60)}:{p.Value}");
        return $"\nLOCAL STATISTICS: titles={titles.Count}, watched={watched}, unfinished={unfinished}, watchlist={personal.Watchlist.Count}. Genres among played/watched titles: {string.Join(", ", top)}";
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// "120 minutes", "90 min", "45 mins" in the request. Null when absent.
    public static int? TimeBudget(string text) =>
        BudgetPattern().Match(text) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"([0-9]{1,3})\s*(?:minutes|min|mins)", RegexOptions.IgnoreCase)]
    private static partial Regex BudgetPattern();
}
