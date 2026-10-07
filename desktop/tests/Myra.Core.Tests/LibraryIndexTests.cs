using Myra.Core;

namespace Myra.Core.Tests;

public sealed class LibraryIndexTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    private string DatabasePath() => Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"), "LibraryIndex.sqlite");

    private static GlobalSearchRoot Root(string name = "Movies") => new(Guid.NewGuid(), name, new Uri($"https://media.example/{name}/"));

    private static GlobalSearchResult Video(GlobalSearchRoot root, string name, string folder = "")
    {
        var path = folder.Length == 0 ? name : folder + "/" + name;
        var url = new Uri(root.Url, Uri.EscapeDataString(path).Replace("%2F", "/"));
        return new GlobalSearchResult(root.Id, root.Name, root.Url,
            new DirectoryEntry(name, url, EntryKind.File, 12345, DateTimeOffset.FromUnixTimeSeconds(100).ToLocalTime()),
            path, new Uri(url, "poster.jpg"));
    }

    private static GlobalSearchService Scanner(Func<Uri, DirectoryListing> listing) =>
        new((url, _, _) => Task.FromResult(listing(url)));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void PersistentCaseInsensitiveSubstringSearchPreservesPathsAndArtwork()
    {
        var path = DatabasePath();
        var first = Root();
        var second = Root("Series");
        GlobalSearchResult[] expected = [Video(first, "The.Matrix.1999.mkv", "1999"), Video(second, "Matrix.S01E01.mp4", "Season 1")];
        using (var index = new LibraryIndex(path)) index.Upsert(expected, Guid.NewGuid());

        using var reopened = new LibraryIndex(path);
        var results = reopened.Search("aTRi");
        Assert.Equal(expected.ToHashSet(), results.ToHashSet());
        var groups = GlobalDownloadManifestBuilder.Groups(results);
        Assert.Equal(2, groups.Count);
        Assert.Equal(
            new HashSet<string> { "Movies/1999/The.Matrix.1999.mkv", "Series/Season 1/Matrix.S01E01.mp4" },
            groups.SelectMany(g => g.Manifest.Select(m => m.RelativePath)).ToHashSet());
    }

    [Fact]
    public void ThreeCharacterMinimumAndLiteralQuery()
    {
        using var index = new LibraryIndex(DatabasePath());
        index.Upsert([Video(Root(), "100%_Movie.mkv")], Guid.NewGuid());
        Assert.Equal(GlobalSearchErrorKind.QueryTooShort, Assert.Throws<GlobalSearchException>(() => index.Search("ab")).Kind);
        Assert.Single(index.Search("0%_"));
        Assert.Empty(index.Search("\" OR 1=1 --"));
    }

    [Fact]
    public void InvalidMediaAndBoundaryAreRejectedAtomically()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var invalid = new GlobalSearchResult(source.Id, source.Name, source.Url,
            new DirectoryEntry("Movie.mkv", new Uri("https://other.example/Movie.mkv"), EntryKind.File), "Movie.mkv", null);
        Assert.ThrowsAny<Exception>(() => index.Upsert([Video(source, "Movie.mkv"), invalid], Guid.NewGuid()));
        Assert.Empty(index.Search("Movie"));
        Assert.ThrowsAny<Exception>(() => index.Upsert([Video(source, "poster.jpg")], Guid.NewGuid()));
    }

    [Fact]
    public void DailyRefreshThresholdAndSourceChanges()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.True(index.NeedsRefresh([source], now));
        index.CompleteSource(source, Guid.NewGuid(), now);
        Assert.False(index.NeedsRefresh([source], now.AddSeconds(86399)));
        Assert.True(index.NeedsRefresh([source], now.AddSeconds(86400)));
        Assert.True(index.NeedsRefresh([source with { Url = new Uri("https://media.example/New/") }], now));
    }

    [Fact]
    public async Task FailedScanKeepsRecordsAndSuccessfulScanPrunesMissingFiles()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        index.Upsert([Video(source, "Old.Movie.mkv")], Guid.NewGuid());
        var failing = new GlobalSearchService((_, _, _) => throw new DirectoryException(DirectoryErrorKind.BadResponse, 503));
        var failure = await new LibraryIndexer(failing).RefreshAsync([source], index, Guid.NewGuid(), _ => Task.CompletedTask);
        Assert.Single(failure.Failures);
        Assert.Single(index.Search("Old"));

        var successful = Scanner(url => new DirectoryListing(url, [], null));
        await new LibraryIndexer(successful).RefreshAsync([source], index, Guid.NewGuid(), _ => Task.CompletedTask);
        Assert.Empty(index.Search("Old"));
    }

    [Fact]
    public async Task RecursiveIndexerStoresAllVideosNotJustQueryMatches()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var child = new Uri(source.Url, "Folder/");
        var expected = Video(source, "Nested.Movie.mkv", "Folder");
        var scanner = Scanner(requested => requested == source.Url
            ? new DirectoryListing(requested,
            [
                new DirectoryEntry("Folder", child, EntryKind.Folder),
                new DirectoryEntry("Another.mp4", new Uri(source.Url, "Another.mp4"), EntryKind.File),
            ], null)
            : new DirectoryListing(requested, [expected.Entry, new DirectoryEntry("poster.jpg", expected.ArtworkUrl!, EntryKind.File)], expected.ArtworkUrl));

        await new LibraryIndexer(scanner).RefreshAsync([source], index, Guid.NewGuid(), _ => Task.CompletedTask);
        Assert.Equal([expected], index.Search("Nested"));
        Assert.Single(index.Search("Another"));
        Assert.Equal(2, index.VideoCount());
    }

    [Fact]
    public void CategoryDeletionOnlyRemovesItsCache()
    {
        using var index = new LibraryIndex(DatabasePath());
        var first = Root();
        var second = Root("Series");
        index.Upsert([Video(first, "Movie1.mkv"), Video(second, "Movie2.mkv")], Guid.NewGuid());
        index.SynchronizeSources([second]);
        Assert.Equal([second.Id], index.Search("Movie").Select(r => r.CategoryId));
    }

    [Fact]
    public void MetadataCacheExpiryAndFilenameParsing()
    {
        var identity = MediaIdentity.Parse("The.Matrix.1999.1080p.BluRay.x264.mkv");
        Assert.Equal("The Matrix", identity.Title);
        Assert.Equal("1999", identity.Year);
        var episode = MediaIdentity.Parse("Some.Show.S02E03.1080p.mkv");
        Assert.Equal("Some Show", episode.Title);
        Assert.Equal("2", episode.Season);
        Assert.Equal("3", episode.Episode);

        using var index = new LibraryIndex(DatabasePath());
        index.SaveMetadata("key", "{}");
        Assert.Equal("{}", index.CachedMetadata("key"));
        Assert.Null(index.CachedMetadata("key", now: DateTimeOffset.UtcNow.AddDays(8)));
    }

    [Fact]
    public void PaginationDoesNotLoseMatches()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var videos = Enumerable.Range(0, 501).Select(n => Video(source, $"Movie{n:0000}.mkv")).ToList();
        index.Upsert(videos, Guid.NewGuid());
        var first = index.Search("Movie");
        var second = index.Search("Movie", offset: first.Count);
        Assert.Equal(500, first.Count);
        Assert.Single(second);
        Assert.Equal(videos.ToHashSet(), first.Concat(second).ToHashSet());
    }

    [Fact]
    public void PlaybackPositionPersistsAndCompletedFilesDoNotResume()
    {
        var path = DatabasePath();
        var url = new Uri("https://media.example/Movies/Film.mkv");
        using (var index = new LibraryIndex(path))
        {
            index.SavePlaybackPosition(url, 120, 3600);
            Assert.Equal(120, index.PlaybackPosition(url));
        }
        using var reopened = new LibraryIndex(path);
        Assert.Equal(120, reopened.PlaybackPosition(url));
        reopened.SavePlaybackPosition(url, 3595, 3600);
        Assert.Null(reopened.PlaybackPosition(url));
        reopened.SavePlaybackPosition(url, 2, 3600);
        Assert.Null(reopened.PlaybackPosition(url));
    }

    [Fact]
    public async Task StreamingTenThousandVideosIndexesEverything()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var folders = Enumerable.Range(0, 100).Select(i => new Uri(source.Url, $"F{i}/")).ToList();
        var scanner = Scanner(url => url == source.Url
            ? new DirectoryListing(url, folders.Select((f, i) => new DirectoryEntry($"F{i}", f, EntryKind.Folder)).ToList(), null)
            : new DirectoryListing(url, Enumerable.Range(0, 100).Select(n => new DirectoryEntry($"Clip {n}.mp4", new Uri(url, $"Clip{n}.mp4"), EntryKind.File)).ToList(), null));
        var snapshot = await new LibraryIndexer(scanner).RefreshAsync([source], index, Guid.NewGuid(), _ => Task.CompletedTask);
        Assert.Equal(10_000, snapshot.Progress.MatchesFound);
        Assert.Empty(snapshot.Results);
        Assert.Equal(10_000, index.VideoCount());
        Assert.False(index.NeedsRefresh([source]));
    }
}
