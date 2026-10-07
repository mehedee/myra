using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Myra.Core;

public enum EntertainmentKind
{
    Movie,
    Series,
}

/// Case- and diacritic-insensitive folding used for title identity (Swift: folding(.caseInsensitive, .diacriticInsensitive)).
public static class TextFolding
{
    public static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

/// TMDB details cached in the index metadata table. Key names match the macOS cache payload.
public sealed record EntertainmentMetadata
{
    [JsonPropertyName("providerID")] public int ProviderId { get; init; }
    public string Title { get; init; } = "";
    public string Overview { get; init; } = "";
    [JsonPropertyName("posterURL")] public Uri? PosterUrl { get; init; }
    public double Rating { get; init; }
    public int VoteCount { get; init; }
    public string? ReleaseDate { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string Language { get; init; } = "";
    /// "season|episode" → air date (yyyy-MM-dd), only for episodes present in the index.
    public Dictionary<string, string>? EpisodeReleaseDates { get; init; }

    /// Null for unreadable payloads (such as the "ambiguous" attempt marker).
    public static EntertainmentMetadata? Decode(string payload)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<EntertainmentMetadata>(payload, SwiftJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// One indexed file (one release/encode) of a movie or episode.
public sealed record EntertainmentVersion(GlobalSearchResult Media, DateTimeOffset FirstDiscovered)
{
    public double ProgressSeconds { get; init; }
    public double Duration { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// The media URL. History, corrections and markers are keyed by it.
    public string Id => Media.Entry.Url.AbsoluteUri;

    /// Release labels such as "1080p · WEB-DL · x265"; empty when the name has none.
    public string Quality => MediaIdentity.Quality(Media.Entry.Name);
}

/// A movie or series: every release and episode file grouped under one identity.
public sealed record EntertainmentTitle(string Id, string Name, string? Year, EntertainmentKind Kind, IReadOnlyList<EntertainmentVersion> Versions)
{
    public EntertainmentMetadata? Metadata { get; init; }

    public DateTimeOffset FirstDiscovered => Versions.Count == 0 ? DateTimeOffset.MinValue : Versions.Min(v => v.FirstDiscovered);
    public DateTimeOffset LatestDiscovered => Versions.Count == 0 ? DateTimeOffset.MinValue : Versions.Max(v => v.FirstDiscovered);
    public string DisplayName => Metadata?.Title is { Length: > 0 } title ? title : Name;
    public Uri? PosterUrl => Metadata?.PosterUrl ?? Versions.FirstOrDefault()?.Media.ArtworkUrl;

    /// Series: newest air date of an indexed episode. Movies: release date.
    public string? LatestReleaseDate => Kind == EntertainmentKind.Series
        ? Metadata?.EpisodeReleaseDates?.Values.Max(StringComparer.Ordinal)
        : Metadata?.ReleaseDate;

    /// Cache key for TMDB details; it changes when new episodes are indexed so air dates refresh.
    public string MetadataCacheKey(int? providerId = null)
    {
        var episodes = Versions.Where(v => v.Season is not null && v.Episode is not null)
            .Select(v => $"{v.Season}:{v.Episode}").Distinct().Order(StringComparer.Ordinal);
        return $"tmdb|{Id}|{providerId ?? 0}|{string.Join(',', episodes)}";
    }

    /// The most recently played file, if it is unfinished. A finished latest file never falls back
    /// to an older episode. Unknown duration keeps saved progress.
    public EntertainmentVersion? ResumeVersion
    {
        get
        {
            var latest = Versions.Where(v => v.LastPlayed is not null).MaxBy(v => v.LastPlayed);
            if (latest is null || latest.ProgressSeconds < 5) return null;
            return latest.Duration == 0 || latest.Duration > latest.ProgressSeconds + 10 ? latest : null;
        }
    }
}

public sealed class EntertainmentCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    [JsonPropertyName("titleIDs")] public HashSet<string> TitleIds { get; set; } = [];

    public EntertainmentCollection Clone() => new() { Id = Id, Name = Name, TitleIds = [.. TitleIds] };
}

public sealed class EntertainmentPreferences
{
    /// Opt-in desktop notifications for followed-series episodes (shown by the UI layer).
    public bool Notifications { get; set; }
}

/// A user's correction of a file's identity. Applies to one version (file URL).
public sealed record EntertainmentMatchCorrection(string Title, string? Year, EntertainmentKind Kind, [property: JsonPropertyName("providerID")] int? ProviderId = null);

public sealed record EntertainmentPlaybackRecord(double Seconds, double Duration, DateTimeOffset Updated);

/// EntertainmentPersonal.json. The format is shared with the macOS app.
public sealed class EntertainmentPersonalData
{
    public int SchemaVersion { get; set; } = 1;
    public HashSet<string> Watchlist { get; set; } = [];
    public HashSet<string> Watched { get; set; } = [];
    public List<EntertainmentCollection> Collections { get; set; } = [];
    public HashSet<string> Followed { get; set; } = [];
    [JsonPropertyName("knownEpisodeIDs")] public HashSet<string> KnownEpisodeIds { get; set; } = [];
    public Dictionary<string, EntertainmentMatchCorrection> MatchCorrections { get; set; } = [];
    public EntertainmentPreferences Preferences { get; set; } = new();
    public Dictionary<string, EntertainmentPlaybackRecord> History { get; set; } = [];

    public EntertainmentPersonalData Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Watchlist = [.. Watchlist],
        Watched = [.. Watched],
        Collections = Collections.Select(c => c.Clone()).ToList(),
        Followed = [.. Followed],
        KnownEpisodeIds = [.. KnownEpisodeIds],
        MatchCorrections = new Dictionary<string, EntertainmentMatchCorrection>(MatchCorrections),
        Preferences = new EntertainmentPreferences { Notifications = Preferences.Notifications },
        History = new Dictionary<string, EntertainmentPlaybackRecord>(History),
    };

    /// Same limits as macOS. Throws InvalidImportException when a document is unsafe to keep.
    public void Validate()
    {
        var valid = SchemaVersion == 1 && Collections.Count <= 5000 && History.Count <= 100_000
            && MatchCorrections.Count <= 100_000
            && Collections.Select(c => c.Id).Distinct().Count() == Collections.Count
            && Collections.All(c => c.Name.Length is > 0 and <= 200 && c.TitleIds is not null)
            && History.Values.All(r => r is not null && double.IsFinite(r.Seconds) && double.IsFinite(r.Duration) && r.Seconds >= 0 && r.Duration >= 0)
            && MatchCorrections.Values.All(c => c is not null && c.Title.Length is > 0 and <= 300 && (c.ProviderId is null || c.ProviderId > 0))
            && Watchlist is not null && Watched is not null && Followed is not null && KnownEpisodeIds is not null && Preferences is not null;
        if (!valid) throw new InvalidImportException("Invalid personal library data.");
    }
}

public sealed class InvalidImportException(string message) : Exception(message);

public static partial class EntertainmentGrouping
{
    /// Title key for an identity: "{kind}|{folded name}|{year}".
    public static string TitleId(EntertainmentKind kind, string name, string? year) =>
        $"{KindKey(kind)}|{TextFolding.Fold(name).Trim()}|{year ?? ""}";

    public static string KindKey(EntertainmentKind kind) => kind == EntertainmentKind.Movie ? "movie" : "series";

    /// Groups files into titles. Files merge across folders only with a year, an episode token or a
    /// correction; yearless files with release labels merge only within one folder; anything else stays alone.
    public static List<EntertainmentTitle> Group(
        IEnumerable<EntertainmentVersion> versions,
        IReadOnlyDictionary<string, EntertainmentMatchCorrection>? corrections = null)
    {
        var titles = new Dictionary<string, (string Name, string? Year, EntertainmentKind Kind, List<EntertainmentVersion> Versions)>();
        foreach (var original in versions)
        {
            var identity = MediaIdentity.Parse(original.Media.Entry.Name);
            var correction = corrections?.GetValueOrDefault(original.Id);
            var version = original with
            {
                Season = identity.Season is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null,
                Episode = identity.Episode is { } e ? int.Parse(e, CultureInfo.InvariantCulture) : null,
            };
            var name = correction?.Title ?? identity.Title;
            var year = correction?.Year ?? identity.Year;
            var kind = correction?.Kind ?? (version.Episode is null ? EntertainmentKind.Movie : EntertainmentKind.Series);
            var normalized = TextFolding.Fold(name).Trim();
            var confident = year is not null || version.Episode is not null || correction is not null;
            string key;
            if (confident) key = $"{KindKey(kind)}|{normalized}|{year ?? ""}";
            else if (version.Quality.Length > 0) key = $"folder|{version.Media.Entry.Url.Parent().AbsoluteUri}|{normalized}";
            else key = "file|" + version.Id;
            if (!titles.TryGetValue(key, out var title))
            {
                title = (name, year, kind, []);
                titles[key] = title;
            }
            title.Versions.Add(version);
        }
        return titles
            .Select(pair => new EntertainmentTitle(pair.Key, pair.Value.Name, pair.Value.Year, pair.Value.Kind, SortVersions(pair.Value.Versions)))
            .OrderBy(t => t.Name, NaturalStringComparer.Instance)
            .ToList();
    }

    /// Season, then episode, then natural filename order.
    public static List<EntertainmentVersion> SortVersions(IEnumerable<EntertainmentVersion> versions) => versions
        .OrderBy(v => v.Season ?? 0).ThenBy(v => v.Episode ?? 0)
        .ThenBy(v => v.Media.Entry.Name, NaturalStringComparer.Instance).ToList();

    [GeneratedRegex("^(19|20)[0-9]{2}$")]
    internal static partial Regex YearPattern();
}

/// All versions of one episode (or one standalone file) for version and episode choosers.
public sealed record EntertainmentEpisodeGroup(string Id, int? Season, int? Episode, IReadOnlyList<EntertainmentVersion> Versions)
{
    public string Label => Season is { } season && Episode is { } episode
        ? $"Season {season} · Episode {episode:00}"
        : Versions.FirstOrDefault()?.Media.Entry.Name ?? "Video";

    public static List<EntertainmentEpisodeGroup> Groups(IEnumerable<EntertainmentVersion> versions)
    {
        var grouped = new Dictionary<string, List<EntertainmentVersion>>();
        foreach (var version in versions)
        {
            var key = version.Episode is { } episode ? $"{version.Season ?? 0}|{episode}" : version.Id;
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = [];
            list.Add(version);
        }
        return grouped
            .Select(pair => new EntertainmentEpisodeGroup(pair.Key, pair.Value[0].Season, pair.Value[0].Episode, pair.Value))
            .OrderBy(g => g.Season ?? 0).ThenBy(g => g.Episode ?? 0)
            .ThenBy(g => g.Label, NaturalStringComparer.Instance).ToList();
    }
}

/// Collapsible season cards. Files without season numbers go to "Other files", listed last.
public sealed record EntertainmentSeason(int? Number, IReadOnlyList<EntertainmentEpisodeGroup> Episodes)
{
    public string Id => Number?.ToString(CultureInfo.InvariantCulture) ?? "other";
    public string Label => Number is { } number ? $"Season {number}" : "Other files";

    public static List<EntertainmentSeason> Seasons(IEnumerable<EntertainmentVersion> versions)
    {
        var episodes = EntertainmentEpisodeGroup.Groups(versions);
        return episodes.Select(e => e.Season).Distinct().OrderBy(n => n ?? int.MaxValue)
            .Select(number => new EntertainmentSeason(number, episodes.Where(e => e.Season == number).ToList()))
            .ToList();
    }
}

/// "Pick something": a random unwatched title from the filtered list, avoiding the current pick
/// when another eligible title exists. Returns null when nothing is eligible.
public static class EntertainmentPick
{
    public static EntertainmentTitle? Select(
        IReadOnlyList<EntertainmentTitle> titles, IReadOnlySet<string> watched, string? excludingId, Random? random = null)
    {
        var eligible = titles.Where(t => !watched.Contains(t.Id)).ToList();
        if (eligible.Count > 1) eligible.RemoveAll(t => t.Id == excludingId);
        return eligible.Count == 0 ? null : eligible[(random ?? Random.Shared).Next(eligible.Count)];
    }

    /// True when Pick Something has at least one candidate (the button is disabled otherwise).
    public static bool CanPick(IReadOnlyList<EntertainmentTitle> titles, IReadOnlySet<string> watched) =>
        titles.Any(t => !watched.Contains(t.Id));
}
