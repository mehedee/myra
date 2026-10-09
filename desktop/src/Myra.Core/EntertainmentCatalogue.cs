using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myra.Core;

/// Builds the Home catalogue from the index and keeps a revision-checked snapshot beside it
/// ("{index}.home-cache"). At startup a matching snapshot restores Home before any network indexing.
/// Metadata and match corrections invalidate the snapshot; snapshots older than a day are reused for
/// grouping but metadata freshness is re-read.
public static class EntertainmentCatalogue
{
    public const int SnapshotFormat = 2;
    private const int MetadataBatch = 200;

    public sealed record Prepared(IReadOnlyList<EntertainmentTitle> Titles, IReadOnlySet<string> EnrichmentIds, int MetadataQueryCount, bool RestoredFromSnapshot);

    public static string SnapshotPath(LibraryIndex index) => index.Path + ".home-cache";

    public static Prepared Prepare(LibraryIndex index, EntertainmentPersonalData personal, CancellationToken cancellationToken = default, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var revision = index.Revision();
        var corrections = CorrectionsFingerprint(personal.MatchCorrections);
        var path = SnapshotPath(index);
        List<EntertainmentTitle>? saved = null;
        if (ReadSnapshot(path) is { } snapshot && snapshot.Format == SnapshotFormat && snapshot.Revision == revision
            && snapshot.Corrections == corrections && snapshot.Restore() is { } restored)
        {
            if (current - SwiftJson.FromReferenceSeconds(snapshot.Saved) < TimeSpan.FromDays(1))
                return new Prepared(restored, snapshot.EnrichmentIds.ToHashSet(), 0, true);
            saved = restored;
        }

        var titles = saved ?? EntertainmentGrouping.Group(index.Inventory(cancellationToken), personal.MatchCorrections);
        var enrichment = new HashSet<string>();
        var queries = 0;
        for (var start = 0; start < titles.Count; start += MetadataBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(start + MetadataBatch, titles.Count);
            var keys = new List<string>();
            for (var i = start; i < end; i++)
            {
                var (key, baseKey) = Keys(titles[i], personal);
                keys.AddRange([key, baseKey, "tmdb-attempt|" + key]);
            }
            var records = index.MetadataRecords(keys);
            queries++;
            for (var i = start; i < end; i++)
            {
                var (key, baseKey) = Keys(titles[i], personal);
                var cached = records.GetValueOrDefault(key) ?? records.GetValueOrDefault(baseKey);
                if (cached is not null) titles[i] = titles[i] with { Metadata = EntertainmentMetadata.Decode(cached.Payload) };
                if (records.GetValueOrDefault(key)?.IsFresh(current) != true && records.GetValueOrDefault("tmdb-attempt|" + key)?.IsFresh(current) != true)
                    enrichment.Add(titles[i].Id);
            }
        }

        if (index.Revision() == revision) WriteSnapshot(path, revision, corrections, titles, enrichment, current);
        return new Prepared(titles, enrichment, queries, false);
    }

    internal static (string Key, string BaseKey) Keys(EntertainmentTitle title, EntertainmentPersonalData personal)
    {
        var providerId = CorrectionFor(title, personal)?.ProviderId;
        return (title.MetadataCacheKey(providerId), $"tmdb-base|{title.Id}|{providerId ?? 0}");
    }

    internal static EntertainmentMatchCorrection? CorrectionFor(EntertainmentTitle title, EntertainmentPersonalData personal) =>
        title.Versions.FirstOrDefault() is { } first ? personal.MatchCorrections.GetValueOrDefault(first.Id) : null;

    private static string CorrectionsFingerprint(Dictionary<string, EntertainmentMatchCorrection> corrections) =>
        JsonSerializer.Serialize(new SortedDictionary<string, EntertainmentMatchCorrection>(corrections, StringComparer.Ordinal), SwiftJson.Options);

    private static Snapshot? ReadSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Snapshot>(stream, SwiftJson.Options);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteSnapshot(string path, long revision, string corrections, List<EntertainmentTitle> titles, HashSet<string> enrichment, DateTimeOffset now)
    {
        // Each source is stored once rather than repeating its name/root in every version.
        var sources = new Dictionary<string, CachedSource>();
        foreach (var version in titles.SelectMany(t => t.Versions))
            sources[version.Media.CategoryId.ToString()] = new CachedSource(version.Media.CategoryName, version.Media.CategoryRoot.AbsoluteUri);
        var snapshot = new Snapshot
        {
            Format = SnapshotFormat,
            Revision = revision,
            Corrections = corrections,
            Sources = sources,
            Titles = titles.Select(CachedTitle.From).ToList(),
            EnrichmentIds = [.. enrichment],
            Saved = SwiftJson.ToReferenceSeconds(now),
        };
        try
        {
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, snapshot, SwiftJson.Options);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The snapshot is an optimization; Home still works without it.
        }
    }

    private sealed record CachedSource(string Name, string Root);

    private sealed class CachedVersion
    {
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string? Artwork { get; set; }
        public long? Size { get; set; }
        public double? Modified { get; set; }
        public double Discovered { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public double Progress { get; set; }
        public double Duration { get; set; }
        public double? LastPlayed { get; set; }
    }

    private sealed class CachedTitle
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Year { get; set; }
        public EntertainmentKind Kind { get; set; }
        public EntertainmentMetadata? Metadata { get; set; }
        public List<CachedVersion> Versions { get; set; } = [];

        public static CachedTitle From(EntertainmentTitle title) => new()
        {
            Id = title.Id,
            Name = title.Name,
            Year = title.Year,
            Kind = title.Kind,
            Metadata = title.Metadata,
            Versions = title.Versions.Select(v => new CachedVersion
            {
                Source = v.Media.CategoryId.ToString(),
                Url = v.Media.Entry.Url.AbsoluteUri,
                Name = v.Media.Entry.Name,
                RelativePath = v.Media.RelativePath,
                Artwork = v.Media.ArtworkUrl?.AbsoluteUri,
                Size = v.Media.Entry.Size,
                Modified = v.Media.Entry.ModifiedAt is { } m ? LibraryIndex.ToUnix(m) : null,
                Discovered = LibraryIndex.ToUnix(v.FirstDiscovered),
                Season = v.Season,
                Episode = v.Episode,
                Progress = v.ProgressSeconds,
                Duration = v.Duration,
                LastPlayed = v.LastPlayed is { } p ? LibraryIndex.ToUnix(p) : null,
            }).ToList(),
        };
    }

    private sealed class Snapshot
    {
        public int Format { get; set; }
        public long Revision { get; set; }
        public string Corrections { get; set; } = "";
        public Dictionary<string, CachedSource> Sources { get; set; } = [];
        public List<CachedTitle> Titles { get; set; } = [];
        [JsonPropertyName("enrichmentIDs")] public List<string> EnrichmentIds { get; set; } = [];
        public double Saved { get; set; }

        /// Null when the snapshot is incomplete or inconsistent; the caller rebuilds from the index.
        public List<EntertainmentTitle>? Restore()
        {
            var roots = new Dictionary<string, (Guid Id, string Name, Uri Root)>();
            foreach (var (key, source) in Sources)
            {
                if (!Guid.TryParse(key, out var id) || !Uri.TryCreate(source.Root, UriKind.Absolute, out var root)) return null;
                roots[key] = (id, source.Name, root);
            }
            var titles = new List<EntertainmentTitle>(Titles.Count);
            foreach (var title in Titles)
            {
                var versions = new List<EntertainmentVersion>(title.Versions.Count);
                foreach (var v in title.Versions)
                {
                    if (!roots.TryGetValue(v.Source, out var root) || !Uri.TryCreate(v.Url, UriKind.Absolute, out var url)) return null;
                    Uri? artwork = v.Artwork is not null && Uri.TryCreate(v.Artwork, UriKind.Absolute, out var a) ? a : null;
                    var entry = new DirectoryEntry(v.Name, url, EntryKind.File, v.Size, v.Modified is { } m ? LibraryIndex.FromUnix(m) : null);
                    versions.Add(new EntertainmentVersion(new GlobalSearchResult(root.Id, root.Name, root.Root, entry, v.RelativePath, artwork), LibraryIndex.FromUnix(v.Discovered))
                    {
                        Season = v.Season,
                        Episode = v.Episode,
                        ProgressSeconds = v.Progress,
                        Duration = v.Duration,
                        LastPlayed = v.LastPlayed is { } p ? LibraryIndex.FromUnix(p) : null,
                    });
                }
                titles.Add(new EntertainmentTitle(title.Id, title.Name, title.Year, title.Kind, versions) { Metadata = title.Metadata });
            }
            return titles;
        }
    }
}

/// Home filter state. Empty strings and zero mean "any".
public sealed record HomeCatalogueFilter(
    string Query = "", string Genre = "", string Language = "", string Year = "", string Source = "",
    double MinimumRating = 0, bool HideWatched = false)
{
    /// Text for the Pick Something window ("Sci-Fi, 2024, rating 7+").
    public string Description
    {
        get
        {
            var terms = new List<string>();
            if (Query.Length > 0) terms.Add($"search ‘{Query}’");
            if (Genre.Length > 0) terms.Add(Genre);
            if (Language.Length > 0) terms.Add(Language);
            if (Year.Length > 0) terms.Add(Year);
            if (Source.Length > 0) terms.Add(Source);
            if (MinimumRating > 0) terms.Add($"rating {(int)MinimumRating}+");
            return terms.Count == 0 ? "your current filters (all sources)" : string.Join(", ", terms);
        }
    }

    public static readonly double[] RatingChoices = [0, 5, 6, 7, 8, 9];
}

public sealed record HomeCatalogueProjection
{
    public IReadOnlyList<EntertainmentTitle> Filtered { get; init; } = [];
    public IReadOnlyList<EntertainmentTitle> Movies { get; init; } = [];
    public IReadOnlyList<EntertainmentTitle> Series { get; init; } = [];
    public IReadOnlyList<EntertainmentTitle> Latest { get; init; } = [];
    public IReadOnlyList<EntertainmentTitle> Recent { get; init; } = [];
    public IReadOnlyList<EntertainmentTitle> Watchlist { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> Years { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
}

public enum HomeShelfKind
{
    ContinueWatching,
    TopRatedMovies,
    SeriesToWatch,
    LatestReleases,
    RecentlyAdded,
    Watchlist,
    NewEpisodes,
    Collection,
}

/// One Home row. Rows with more than VisibleLimit titles show a "Show all" toggle.
public sealed record HomeShelf(HomeShelfKind Kind, string Name, string Subtitle, IReadOnlyList<EntertainmentTitle> Titles, Guid? CollectionId = null)
{
    public const int VisibleLimit = 60;

    public string EmptyMessage => Kind is HomeShelfKind.TopRatedMovies or HomeShelfKind.LatestReleases
        ? "No matched titles for these filters. Add a TMDB key in Settings and update metadata."
        : "No titles here yet. Try adjusting your filters.";
}

public static class HomeProjection
{
    /// TMDB rating weighted by vote count (Bayesian average toward 6.5 with weight 50).
    public static double WeightedRating(EntertainmentTitle title)
    {
        if (title.Metadata is not { } metadata) return 0;
        double votes = metadata.VoteCount;
        return (votes * metadata.Rating + 50 * 6.5) / (votes + 50);
    }

    public static HomeCatalogueProjection Prepare(
        IReadOnlyList<EntertainmentTitle> catalogue, EntertainmentPersonalData personal, HomeCatalogueFilter filter,
        CancellationToken cancellationToken = default)
    {
        var filtered = new List<EntertainmentTitle>();
        var movies = new List<EntertainmentTitle>();
        var series = new List<EntertainmentTitle>();
        var latest = new List<EntertainmentTitle>();
        var watchlist = new List<EntertainmentTitle>();
        var genres = new HashSet<string>();
        var languages = new HashSet<string>();
        var years = new HashSet<string>();
        var sources = new HashSet<string>();
        foreach (var title in catalogue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (title.Metadata is { } metadata)
            {
                genres.UnionWith(metadata.Genres);
                languages.Add(metadata.Language);
            }
            if (title.Year is { } year) years.Add(year);
            foreach (var version in title.Versions) sources.Add(version.Media.CategoryName);
            if (!Matches(title, personal, filter)) continue;
            filtered.Add(title);
            if (title.Kind == EntertainmentKind.Movie && (title.Metadata?.VoteCount ?? 0) >= 20) movies.Add(title);
            if (title.Kind == EntertainmentKind.Series && !personal.Watched.Contains(title.Id)) series.Add(title);
            if (title.LatestReleaseDate is not null) latest.Add(title);
            if (personal.Watchlist.Contains(title.Id)) watchlist.Add(title);
        }
        return new HomeCatalogueProjection
        {
            Filtered = filtered,
            Movies = movies.OrderByDescending(WeightedRating).ToList(),
            Series = series.OrderByDescending(WeightedRating).ToList(),
            Latest = latest.OrderByDescending(t => t.LatestReleaseDate ?? "", StringComparer.Ordinal).ToList(),
            Recent = filtered.OrderByDescending(t => t.LatestDiscovered).ToList(),
            Watchlist = watchlist,
            Genres = genres.Order(StringComparer.Ordinal).ToList(),
            Languages = languages.Order(StringComparer.Ordinal).ToList(),
            Years = years.OrderDescending(StringComparer.Ordinal).ToList(),
            Sources = sources.Order(StringComparer.Ordinal).ToList(),
        };
    }

    public static bool Matches(EntertainmentTitle title, EntertainmentPersonalData personal, HomeCatalogueFilter filter) =>
        (filter.Query.Length == 0 || title.DisplayName.Contains(filter.Query, StringComparison.CurrentCultureIgnoreCase))
        && (filter.Genre.Length == 0 || title.Metadata?.Genres.Contains(filter.Genre) == true)
        && (filter.Language.Length == 0 || title.Metadata?.Language == filter.Language)
        && (filter.Year.Length == 0 || title.Year == filter.Year || title.Metadata?.ReleaseDate?.StartsWith(filter.Year, StringComparison.Ordinal) == true)
        && (filter.Source.Length == 0 || title.Versions.Any(v => v.Media.CategoryName == filter.Source))
        && (filter.MinimumRating == 0 || (title.Metadata?.Rating ?? 0) >= filter.MinimumRating)
        && (!filter.HideWatched || !personal.Watched.Contains(title.Id));

    /// The Home rows in macOS order. New Episodes appears only when followed shows have new episodes.
    /// episodeId may supply cached FollowedEpisodes.EpisodeId values (parsing every file name is costly).
    public static List<HomeShelf> Shelves(
        HomeCatalogueProjection projection, EntertainmentPersonalData personal, IReadOnlySet<string> newEpisodeIds,
        Func<EntertainmentVersion, string>? episodeId = null)
    {
        episodeId ??= FollowedEpisodes.EpisodeId;
        var shelves = new List<HomeShelf>
        {
            new(HomeShelfKind.ContinueWatching, "Continue Watching", "Resume unfinished movies and episodes",
                projection.Filtered.Where(t => t.ResumeVersion is not null && !personal.Watched.Contains(t.Id)).ToList()),
            new(HomeShelfKind.TopRatedMovies, "Top Rated Movies", "TMDB ratings weighted by vote count • minimum 20 votes", projection.Movies),
            new(HomeShelfKind.SeriesToWatch, "Series to Watch", "Unwatched shows available in your sources", projection.Series),
            new(HomeShelfKind.LatestReleases, "Latest Releases", "Release dates from matched titles in your index", projection.Latest),
            new(HomeShelfKind.RecentlyAdded, "Recently Added", "First discovered by your local index", projection.Recent),
            new(HomeShelfKind.Watchlist, "Watchlist", "Saved for later", projection.Watchlist),
        };
        if (newEpisodeIds.Count > 0)
            shelves.Add(new HomeShelf(HomeShelfKind.NewEpisodes, "New Episodes", "Newly indexed episodes of shows you follow",
                projection.Filtered.Where(t => t.Versions.Any(v => newEpisodeIds.Contains(episodeId(v)))).ToList()));
        foreach (var collection in personal.Collections)
            shelves.Add(new HomeShelf(HomeShelfKind.Collection, collection.Name, "Personal collection",
                projection.Filtered.Where(t => collection.TitleIds.Contains(t.Id)).ToList(), collection.Id));
        return shelves;
    }

    public const string Attribution =
        "Availability reflects the latest index, and a source may be offline. Metadata provided by TMDB. "
        + "Myra uses the TMDB API but is not endorsed or certified by TMDB.";
}

/// Followed-show new-episode detection. The first follow records a baseline, so only episodes
/// indexed afterwards count as new; each episode is reported once.
public static class FollowedEpisodes
{
    public static string EpisodeId(EntertainmentVersion version) => MediaIdentity.Parse(version.Media.Entry.Name).CacheKey;

    /// Episode IDs of all followed series currently in the catalogue.
    public static HashSet<string> Current(IEnumerable<EntertainmentTitle> catalogue, IReadOnlySet<string> followed) =>
        catalogue.Where(t => t.Kind == EntertainmentKind.Series && followed.Contains(t.Id))
            .SelectMany(t => t.Versions).Select(EpisodeId).ToHashSet();

    /// New = current minus known. The caller adds current to the known set when new is not empty.
    public static HashSet<string> Detect(IEnumerable<EntertainmentTitle> catalogue, EntertainmentPersonalData personal)
    {
        var current = Current(catalogue, personal.Followed);
        current.ExceptWith(personal.KnownEpisodeIds);
        return current;
    }
}
