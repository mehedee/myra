namespace Myra.Core.Tests;

/// Title parsing, release grouping, Home shelves, random picks and followed episodes
/// (ported from DiscoveryTests.swift, PlayerV2Tests.swift and the 2.0.5 pick checks).
public class EntertainmentTests
{
    private static readonly Uri Root = new("https://media.example/");

    internal static EntertainmentVersion Version(string filename, string folder = "Movies", Guid? source = null, string sourceName = "Movies")
    {
        var url = new Uri(Root, $"{folder}/{filename}");
        return new EntertainmentVersion(
            new GlobalSearchResult(source ?? Guid.NewGuid(), sourceName, Root, new DirectoryEntry(filename, url, EntryKind.File), $"{folder}/{filename}", null),
            DateTimeOffset.FromUnixTimeSeconds(100));
    }

    [Fact]
    public void ParsesTitleYearSeasonEpisodeAndQuality()
    {
        var movie = MediaIdentity.Parse("The.Matrix.1999.1080p.BluRay.x264.mkv");
        Assert.Equal(("The Matrix", "1999", (string?)null, (string?)null), (movie.Title, movie.Year, movie.Season, movie.Episode));
        Assert.Equal("1080p · BluRay · x264", MediaIdentity.Quality("The.Matrix.1999.1080p.BluRay.x264.mkv"));
        var episode = MediaIdentity.Parse("Show_Name.S01E02.720p.mkv");
        Assert.Equal(("Show Name", "1", "2"), (episode.Title, episode.Season, episode.Episode));
        Assert.Equal("show name||1|2|", episode.CacheKey);
        Assert.Equal("1917", MediaIdentity.Parse("1917.mkv").Title);
        Assert.Equal("the matrix|1999|||tt0133093", (movie with { ImdbId = "tt0133093" }).CacheKey);
    }

    [Fact]
    public void GroupsYearAndQualityVariantsButNotDifferentMovies()
    {
        var grouped = EntertainmentGrouping.Group([
            Version("Movie.2025.1080p.WEB-DL.mkv"), Version("Movie.2025.2160p.BluRay.mkv"),
            Version("Movie.1995.1080p.mkv"), Version("Other.2025.1080p.mkv"),
        ]);
        Assert.Equal(3, grouped.Count);
        var movie = grouped.Single(t => t.Year == "2025" && t.Name == "Movie");
        Assert.Equal(2, movie.Versions.Count);
        Assert.Equal("movie|movie|2025", movie.Id);
        Assert.Equal(EntertainmentKind.Movie, movie.Kind);
    }

    [Fact]
    public void YearlessQualityVariantsOnlyGroupInSameFolder()
    {
        var values = EntertainmentGrouping.Group([
            Version("Movie.1080p.mkv"), Version("Movie.2160p.mkv"), Version("Movie.720p.mkv", "Elsewhere"), Version("Movie.mkv"),
        ]);
        Assert.Equal(3, values.Count);
        Assert.Single(values, t => t.Versions.Count == 2);
        Assert.Contains(values, t => t.Id.StartsWith("file|"));
    }

    [Fact]
    public void EpisodesGroupedIntoOneSeriesWithVersionsAndNaturalOrder()
    {
        var title = EntertainmentGrouping.Group([
            Version("Show.S01E10.1080p.mkv"), Version("Show.S01E02.1080p.mkv"), Version("Show.S01E02.2160p.mkv"),
        ]).Single();
        Assert.Equal(EntertainmentKind.Series, title.Kind);
        Assert.Equal([2, 2, 10], title.Versions.Select(v => v.Episode));
    }

    [Fact]
    public void DiacriticsAndCaseDoNotSplitTitles()
    {
        var grouped = EntertainmentGrouping.Group([Version("Amélie.2001.1080p.mkv"), Version("AMELIE.2001.720p.mkv", "Other")]);
        Assert.Single(grouped);
        Assert.Equal("movie|amelie|2001", grouped[0].Id);
    }

    [Fact]
    public void CorrectionsMergeOrSplitFiles()
    {
        var a = Version("Weird.Name.mkv");
        var b = Version("Another.Weird.mkv", "Other");
        var corrections = new Dictionary<string, EntertainmentMatchCorrection>
        {
            [a.Id] = new("Real Title", "2010", EntertainmentKind.Movie, 27205),
            [b.Id] = new("Real Title", "2010", EntertainmentKind.Movie),
        };
        var title = EntertainmentGrouping.Group([a, b], corrections).Single();
        Assert.Equal("movie|real title|2010", title.Id);
        Assert.Equal(2, title.Versions.Count);
        // A correction without a year keeps the parsed year.
        var c = Version("Film.1999.mkv");
        var kept = EntertainmentGrouping.Group([c], new Dictionary<string, EntertainmentMatchCorrection> { [c.Id] = new("Film", null, EntertainmentKind.Movie) });
        Assert.Equal("1999", kept.Single().Year);
    }

    [Fact]
    public void ResumeChoosesMostRecentlyPlayedVersionRatherThanFurthestEpisode()
    {
        EntertainmentVersion Played(string name, double seconds, long updated, int season, int episode) => Version(name, "TV") with
        {
            ProgressSeconds = seconds, Duration = 1800, LastPlayed = DateTimeOffset.FromUnixTimeSeconds(updated), Season = season, Episode = episode,
        };
        var older = Played("Show.S02E05.1080p.mkv", 900, 1, 2, 5);
        var recent = Played("Show.S01E01.720p.mkv", 60, 2, 1, 1);
        var title = new EntertainmentTitle("show", "Show", null, EntertainmentKind.Series, [older, recent]);
        Assert.Equal(recent.Id, title.ResumeVersion?.Id);
        title = title with { Kind = EntertainmentKind.Movie };
        Assert.Equal(recent.Id, title.ResumeVersion?.Id);
        title = title with { Versions = [older, recent with { ProgressSeconds = 1795 }] };
        Assert.Null(title.ResumeVersion);
        title = title with { Versions = [older, recent with { Duration = 0, ProgressSeconds = 60 }] };
        Assert.Equal(recent.Id, title.ResumeVersion?.Id);
        title = title with { Versions = [older, recent with { LastPlayed = null }] };
        Assert.Equal(older.Id, title.ResumeVersion?.Id);
    }

    [Fact]
    public void SeasonGroupingKeepsEpisodeVersionsTogetherAndSpecialsSeparate()
    {
        var titles = EntertainmentGrouping.Group(new[]
        {
            "Show.S02E01.mkv", "Show.S01E02.mkv", "Show.S01E01.1080p.mkv", "Show.S01E01.720p.mkv",
        }.Select(n => Version(n, "TV")));
        var special = EntertainmentGrouping.Group([Version("Show.Special.mkv", "TV")]).Single().Versions;
        var seasons = EntertainmentSeason.Seasons(titles.Single().Versions.Concat(special));
        Assert.Equal([1, 2, null], seasons.Select(s => s.Number));
        Assert.Equal([1, 2], seasons[0].Episodes.Select(e => e.Episode));
        Assert.Equal(2, seasons[0].Episodes[0].Versions.Count);
        Assert.Equal("Other files", seasons[2].Label);
        Assert.Equal("Season 1 · Episode 01", seasons[0].Episodes[0].Label);
    }

    private static EntertainmentTitle Title(string id, EntertainmentKind kind = EntertainmentKind.Movie) =>
        new(id, id, "2020", kind, [Version(id + ".2020.mkv")]);

    [Fact]
    public void PickRespectsWatchedAndAvoidsRepeatingCurrentTitle()
    {
        Assert.Null(EntertainmentPick.Select([], new HashSet<string>(), null));
        var a = Title("a");
        var b = Title("b");
        Assert.Null(EntertainmentPick.Select([a, b], new HashSet<string> { "a", "b" }, null));
        Assert.False(EntertainmentPick.CanPick([a, b], new HashSet<string> { "a", "b" }));
        // One eligible title is returned even when it is the current pick.
        Assert.Equal("a", EntertainmentPick.Select([a, b], new HashSet<string> { "b" }, "a")?.Id);
        // With alternatives, the current pick is never repeated.
        for (var seed = 0; seed < 20; seed++)
            Assert.Equal("b", EntertainmentPick.Select([a, b], new HashSet<string>(), "a", new Random(seed))?.Id);
        // Watched titles are excluded.
        var c = Title("c");
        for (var seed = 0; seed < 20; seed++)
            Assert.NotEqual("b", EntertainmentPick.Select([a, b, c], new HashSet<string> { "b" }, null, new Random(seed))?.Id);
    }

    private static EntertainmentTitle Rated(string id, double rating, int votes, EntertainmentKind kind = EntertainmentKind.Movie, string? release = null) =>
        Title(id, kind) with
        {
            Metadata = new EntertainmentMetadata
            {
                ProviderId = 1, Title = id, Rating = rating, VoteCount = votes, Genres = ["Drama"], Language = "en", ReleaseDate = release,
            },
        };

    [Fact]
    public void HomeShelvesUseVoteWeightedRatingsAndFilters()
    {
        var popular = Rated("popular", 8.0, 10_000, release: "2020-01-01");
        var obscure = Rated("obscure", 10.0, 25, release: "2024-05-01");
        var tooFew = Rated("few", 9.9, 5);
        var series = Rated("series", 7.0, 100, EntertainmentKind.Series);
        var personal = new EntertainmentPersonalData();
        personal.Watchlist.Add("obscure");
        var projection = HomeProjection.Prepare([obscure, popular, tooFew, series], personal, new HomeCatalogueFilter());
        Assert.Equal(["popular", "obscure"], projection.Movies.Select(t => t.Id));
        Assert.Equal(["series"], projection.Series.Select(t => t.Id));
        Assert.Equal(["obscure", "popular"], projection.Latest.Select(t => t.Id));
        Assert.Equal(["obscure"], projection.Watchlist.Select(t => t.Id));
        Assert.Equal(["Drama"], projection.Genres);

        personal.Watched.Add("series");
        var hidden = HomeProjection.Prepare([obscure, popular, tooFew, series], personal, new HomeCatalogueFilter(HideWatched: true, MinimumRating: 8));
        Assert.DoesNotContain(hidden.Filtered, t => t.Id == "series");
        Assert.Equal(new HashSet<string> { "popular", "obscure", "few" }, hidden.Filtered.Select(t => t.Id).ToHashSet());
        Assert.Equal("rating 8+", new HomeCatalogueFilter(MinimumRating: 8).Description);

        personal.Collections.Add(new EntertainmentCollection { Name = "Weekend", TitleIds = ["popular"] });
        var shelves = HomeProjection.Shelves(projection, personal, new HashSet<string>());
        Assert.Equal(
            ["Continue Watching", "Top Rated Movies", "Series to Watch", "Latest Releases", "Recently Added", "Watchlist", "Weekend"],
            shelves.Select(s => s.Name));
        Assert.Equal(["popular"], shelves[^1].Titles.Select(t => t.Id));
    }

    [Fact]
    public void FollowedSeriesReportOnlyEpisodesIndexedAfterTheBaselineOnce()
    {
        using var store = new StoreFixture();
        var show = EntertainmentGrouping.Group([Version("Show.S01E01.mkv", "TV")]).Single();
        store.SetCatalogue([show]);
        Assert.True(store.Store.ToggleFollow(show.Id));
        Assert.Contains("show||1|1|", store.Store.Personal.KnownEpisodeIds);
        Assert.Empty(FollowedEpisodes.Detect([show], store.Store.Personal));

        var updated = EntertainmentGrouping.Group([Version("Show.S01E01.mkv", "TV"), Version("Show.S01E02.mkv", "TV")]).Single();
        var found = FollowedEpisodes.Detect([updated], store.Store.Personal);
        Assert.Equal(["show||1|2|"], found);
        Assert.Empty(FollowedEpisodes.Detect([updated], new EntertainmentPersonalData()));
    }
}

/// An EntertainmentStore over a temporary index and personal file.
internal sealed class StoreFixture : IDisposable
{
    private readonly TemporaryDirectory _temp = new();
    public LibraryIndex Index { get; }
    public EntertainmentStore Store { get; }
    public string PersonalPath => Path.Combine(_temp.Path, "EntertainmentPersonal.json");
    public string Folder => _temp.Path;

    public StoreFixture(InMemorySecretStore? secrets = null, DiscoveryService? discovery = null)
    {
        Index = new LibraryIndex(Path.Combine(_temp.Path, "LibraryIndex.sqlite"));
        Store = new EntertainmentStore(() => Index, PersonalPath, secrets ?? new InMemorySecretStore(), discovery, automaticEnrichment: false);
    }

    /// Indexes the catalogue's files and reloads the store from the index.
    public void SetCatalogue(IEnumerable<EntertainmentTitle> titles)
    {
        Index.Upsert(titles.SelectMany(t => t.Versions).Select(v => v.Media).ToList(), Guid.NewGuid());
        Store.ReloadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }
}
