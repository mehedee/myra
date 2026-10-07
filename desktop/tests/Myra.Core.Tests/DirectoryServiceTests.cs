using Myra.Core;

namespace Myra.Core.Tests;

internal sealed class ListingFixture(Dictionary<string, DirectoryListing> listings, HashSet<string>? failing = null)
{
    private readonly Dictionary<string, int> _calls = [];

    public Task<DirectoryListing> Load(Uri url, UrlBoundary boundary, CancellationToken token)
    {
        lock (_calls) _calls[url.AbsoluteUri] = _calls.GetValueOrDefault(url.AbsoluteUri) + 1;
        if (failing?.Contains(url.AbsoluteUri) == true || !boundary.Contains(url) || !listings.TryGetValue(url.AbsoluteUri, out var listing))
            throw new InvalidOperationException("Fixture unavailable");
        return Task.FromResult(listing);
    }

    public int CallCount(Uri url)
    {
        lock (_calls) return _calls.GetValueOrDefault(url.AbsoluteUri);
    }
}

public class DirectoryServiceTests
{
    private readonly Uri _root = new("http://172.16.50.14/DHAKA-FLIX-14/English%20Movies%20%281080p%29/");

    [Fact]
    public void ParsesH5aiFallbackTable()
    {
        const string html = """
            <html><body><table>
              <tr><td><img alt="folder-parent"></td><td><a href="..">Parent Directory</a></td></tr>
              <tr><td><img alt="folder"></td><td><a href="%282026%29%201080p/">(2026) 1080p</a></td><td>2026-07-01 15:17</td><td></td></tr>
              <tr><td><img alt="file"></td><td><a href="Movie%20One.mkv">Movie One.mkv</a></td><td>2026-07-02 10:20</td><td>1.5 GB</td></tr>
            </table></body></html>
            """;
        var entries = DirectoryService.Parse(html, _root, new UrlBoundary(_root));

        Assert.Equal(2, entries.Count);
        Assert.Equal(EntryKind.Folder, entries[0].Kind);
        Assert.Equal("(2026) 1080p", entries[0].Name);
        Assert.Equal(EntryKind.File, entries[1].Kind);
        Assert.Equal(1_610_612_736, entries[1].Size);
        Assert.NotNull(entries[1].ModifiedAt);
    }

    [Fact]
    public void ParsesCommonAutoIndexAnchors()
    {
        const string html = """
            <html><body><pre>
            <a href="../">../</a>
            <a href="Hindi%20Movies/">Hindi Movies/</a>
            <a href="sample.mp4">sample.mp4</a>
            </pre></body></html>
            """;
        var entries = DirectoryService.Parse(html, _root, new UrlBoundary(_root));
        Assert.Equal(["Hindi Movies", "sample.mp4"], entries.Select(e => e.Name));
    }

    [Fact]
    public void ParsesNginxAutoIndexWithSizes()
    {
        const string html = """
            <html><head><title>Index of /movies/</title></head><body><h1>Index of /movies/</h1><hr><pre>
            <a href="../">../</a>
            <a href="Action/">Action/</a>                                            01-Jan-2026 10:00       -
            <a href="Film%20(2025).mkv">Film (2025).mkv</a>                          02-Jan-2026 11:30   734003200
            </pre><hr></body></html>
            """;
        var entries = DirectoryService.Parse(html, _root, new UrlBoundary(_root));
        Assert.Equal(["Action", "Film (2025).mkv"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsFolder);
    }

    [Fact]
    public void ListingMediaTypesMatchSamOnlineRules()
    {
        DirectoryEntry File(string name) => new(name, new Uri(_root, name), EntryKind.File);
        Assert.True(MediaFileType.IsVideo(File("Movie.mkv")));
        Assert.True(MediaFileType.IsArtwork(File("poster.jpg")));
        Assert.False(MediaFileType.IsVideo(File("poster.jpg")));
        Assert.False(MediaFileType.IsVideo(File("notes.txt")));
        Assert.False(MediaFileType.IsVideo(new DirectoryEntry("Season 1", new Uri(_root, "Season%201/"), EntryKind.Folder)));
    }

    [Fact]
    public void BoundaryRejectsParentAndCrossOrigin()
    {
        var boundary = new UrlBoundary(_root);
        Assert.True(boundary.Contains(new Uri("http://172.16.50.14/DHAKA-FLIX-14/English%20Movies%20%281080p%29/2026/")));
        Assert.True(boundary.Contains(_root));
        Assert.False(boundary.Contains(new Uri("http://172.16.50.14/DHAKA-FLIX-14/")));
        Assert.False(boundary.Contains(new Uri("http://example.com/DHAKA-FLIX-14/English%20Movies%20%281080p%29/")));
        Assert.False(boundary.Contains(new Uri("https://172.16.50.14/DHAKA-FLIX-14/English%20Movies%20%281080p%29/")));
        Assert.False(boundary.Contains(new Uri("http://172.16.50.14:8080/DHAKA-FLIX-14/English%20Movies%20%281080p%29/")));
    }

    [Fact]
    public void BoundaryRejectsNonHttpRoots()
    {
        Assert.Throws<DirectoryException>(() => new UrlBoundary(new Uri("ftp://media.local/")));
        Assert.Throws<DirectoryException>(() => new UrlBoundary(new Uri("file:///C:/movies/")));
    }

    [Theory]
    [InlineData("%2E%2E/secret/")]
    [InlineData("%252E%252E/secret/")]
    [InlineData("..%2Fsecret/")]
    [InlineData("..%5Csecret/")]
    [InlineData("../../secret/")]
    [InlineData("http://evil.example/x.mkv")]
    public void EncodedTraversalIsRejectedByParser(string href)
    {
        var entries = DirectoryService.Parse($"<a href=\"{href}\">secret</a>", _root, new UrlBoundary(_root));
        Assert.Empty(entries);
    }

    [Fact]
    public void DirectorySortingKeepsFoldersGroupedAndHandlesDates()
    {
        var older = DateTimeOffset.FromUnixTimeSeconds(100);
        var newer = DateTimeOffset.FromUnixTimeSeconds(200);
        DirectoryEntry[] entries =
        [
            new("Zulu.mkv", new Uri(_root, "Zulu.mkv"), EntryKind.File, ModifiedAt: newer),
            new("Archive", new Uri(_root, "Archive/"), EntryKind.Folder, ModifiedAt: older),
            new("Alpha.mkv", new Uri(_root, "Alpha.mkv"), EntryKind.File),
        ];
        Assert.Equal(["Archive", "Alpha.mkv", "Zulu.mkv"], DirectoryEntrySorter.Sorted(entries, DirectorySortField.Name, true).Select(e => e.Name));
        Assert.Equal(["Archive", "Zulu.mkv", "Alpha.mkv"], DirectoryEntrySorter.Sorted(entries, DirectorySortField.Date, false).Select(e => e.Name));
    }

    [Fact]
    public void NaturalOrderMatchesFinder()
    {
        string[] names = ["Episode 10.mkv", "episode 2.mkv", "Episode 1.mkv", "Episode 02b.mkv"];
        Assert.Equal(["Episode 1.mkv", "episode 2.mkv", "Episode 02b.mkv", "Episode 10.mkv"], names.Order(NaturalStringComparer.Instance));
    }

    [Theory]
    [InlineData("1.5 GB", 1_610_612_736L)]
    [InlineData("700 MiB", 734_003_200L)]
    [InlineData("12KB", 12_288L)]
    [InlineData("-", null)]
    public void ParsesByteSizes(string text, long? expected) => Assert.Equal(expected, DirectoryService.ParseByteSize(text));

    [Fact]
    public async Task GlobalSearchRecursesAcrossSourcesAndCarriesArtwork()
    {
        var firstRoot = new Uri("http://media.local/first/");
        var movies = new Uri(firstRoot, "Movies/");
        var secondRoot = new Uri("http://media.local/second/");
        var firstPoster = new Uri(movies, "poster.jpg");
        var secondPoster = new Uri(secondRoot, "cover.webp");
        var fixture = new ListingFixture(new()
        {
            [firstRoot.AbsoluteUri] = new(firstRoot, [new("Movies", movies, EntryKind.Folder)], null),
            [movies.AbsoluteUri] = new(movies,
            [
                new("Dune Part Two.mkv", new Uri(movies, "Dune.mkv"), EntryKind.File),
                new("Dune notes.txt", new Uri(movies, "notes.txt"), EntryKind.File),
            ], firstPoster),
            [secondRoot.AbsoluteUri] = new(secondRoot, [new("DUNE.mp4", new Uri(secondRoot, "DUNE.mp4"), EntryKind.File)], secondPoster),
        });
        var updates = 0;
        var service = new GlobalSearchService(fixture.Load);

        var snapshot = await service.SearchAsync("dune",
        [
            new GlobalSearchRoot(Guid.NewGuid(), "First", firstRoot),
            new GlobalSearchRoot(Guid.NewGuid(), "Second", secondRoot),
        ], _ =>
        {
            Interlocked.Increment(ref updates);
            return Task.CompletedTask;
        });

        Assert.Equal(["Movies/Dune Part Two.mkv", "DUNE.mp4"], snapshot.Results.Select(r => r.RelativePath));
        Assert.Equal(new HashSet<Uri> { firstPoster, secondPoster }, snapshot.Results.Select(r => r.ArtworkUrl!).ToHashSet());
        Assert.Equal(2, snapshot.Progress.SourcesCompleted);
        Assert.Equal(3, snapshot.Progress.FoldersVisited);
        Assert.Equal(2, snapshot.Progress.MatchesFound);
        Assert.True(updates > 2);
    }

    [Fact]
    public async Task GlobalSearchAvoidsDirectoryCycles()
    {
        var root = new Uri("http://media.local/root/");
        var child = new Uri(root, "Child/");
        var fixture = new ListingFixture(new()
        {
            [root.AbsoluteUri] = new(root, [new("Child", child, EntryKind.Folder)], null),
            [child.AbsoluteUri] = new(child, [new("Root", root, EntryKind.Folder)], null),
        });
        var snapshot = await new GlobalSearchService(fixture.Load).SearchAsync(
            "none", [new GlobalSearchRoot(Guid.NewGuid(), "Cycle", root)], _ => Task.CompletedTask);

        Assert.Empty(snapshot.Results);
        Assert.Equal(2, snapshot.Progress.FoldersVisited);
        Assert.Equal(1, fixture.CallCount(root));
        Assert.Equal(1, fixture.CallCount(child));
    }

    [Fact]
    public async Task GlobalSearchKeepsPartialResultsAndReportsFailedSource()
    {
        var working = new Uri("http://media.local/working/");
        var failing = new Uri("http://media.local/failing/");
        var fixture = new ListingFixture(
            new() { [working.AbsoluteUri] = new(working, [new("Avatar.mkv", new Uri(working, "Avatar.mkv"), EntryKind.File)], null) },
            [failing.AbsoluteUri]);
        var snapshot = await new GlobalSearchService(fixture.Load).SearchAsync("avatar",
        [
            new GlobalSearchRoot(Guid.NewGuid(), "Working", working),
            new GlobalSearchRoot(Guid.NewGuid(), "Offline", failing),
        ], _ => Task.CompletedTask);

        Assert.Equal(["Avatar.mkv"], snapshot.Results.Select(r => r.Entry.Name));
        Assert.Equal("Offline", Assert.Single(snapshot.Failures).CategoryName);
        Assert.Equal(2, snapshot.Progress.SourcesCompleted);
        Assert.Equal(1, snapshot.Progress.FailedSources);
    }

    [Fact]
    public async Task GlobalSearchValidatesQueryAndSupportsCancellation()
    {
        var root = new Uri("http://media.local/root/");
        var service = new GlobalSearchService(async (url, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new DirectoryListing(url, [], null);
        });
        GlobalSearchRoot[] roots = [new(Guid.NewGuid(), "Root", root)];

        var error = await Assert.ThrowsAsync<GlobalSearchException>(() => service.SearchAsync("ab", roots, _ => Task.CompletedTask));
        Assert.Equal(GlobalSearchErrorKind.QueryTooShort, error.Kind);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync("movie", roots, _ => Task.CompletedTask, cancellationToken: cancellation.Token));
    }
}
