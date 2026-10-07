using System.Net;
using System.Text;

namespace Myra.Core.Tests;

/// TMDB, OMDb and OpenSubtitles clients against in-process fixtures (ported from PlayerWorkflowTests.swift
/// and MediaPlaybackTests.swift). No test touches the network.
public sealed class ProviderTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private LibraryIndex NewIndex() => new(Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"), "index.sqlite"));

    private const string SearchJson = """
        {"total_pages":2,"data":[{"attributes":{"language":"en","release":"Series S01E02 WEB-DL","fps":23.976,"download_count":42,"hearing_impaired":true,"from_trusted":true,"files":[{"file_id":24,"file_name":"../../episode.srt"},{"file_id":24,"file_name":"duplicate.srt"}]}}]}
        """;

    private const string Subtitle = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";
    private static readonly SubtitleCredentials Credentials = new("fixture-key");

    private static Dictionary<string, string> Query(Uri url) => url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    // ---------- TMDB ----------

    private static EntertainmentTitle Movie(string name, string? year) =>
        EntertainmentGrouping.Group([EntertainmentTests.Version($"{name}.{year}.mkv".Replace("..", "."))]).Single();

    [Fact]
    public async Task TmdbRequiresOneExactMatchAndCachesDetails()
    {
        using var index = NewIndex();
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/3/search/movie"] = (200, """{"results":[{"id":603,"title":"The Matrix","release_date":"1999-03-31"},{"id":604,"title":"The Matrix Reloaded","release_date":"2003-05-15"}]}"""),
            ["/3/movie/603"] = (200, """{"id":603,"title":"The Matrix","overview":"Neo.","poster_path":"/p.jpg","vote_average":8.2,"vote_count":25000,"release_date":"1999-03-31","original_language":"en","genres":[{"name":"Action"}]}"""),
        });
        var service = new DiscoveryService(handler.Client(), TimeSpan.Zero);
        var title = Movie("The.Matrix", "1999");
        var metadata = await service.LookupAsync(title, null, "token", index);
        Assert.Equal(603, metadata.ProviderId);
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w500/p.jpg"), metadata.PosterUrl);
        Assert.Equal(["Action"], metadata.Genres);
        var search = handler.Requests[0];
        Assert.Equal("https", search.Url.Scheme);
        Assert.Equal(DiscoveryService.Host, search.Url.Host);
        Assert.Equal("Bearer token", search.Header("Authorization"));
        Assert.Equal(new Dictionary<string, string> { ["query"] = "The Matrix", ["include_adult"] = "false", ["primary_release_year"] = "1999" }, Query(search.Url));
        Assert.DoesNotContain("media.example", search.Url.AbsoluteUri);

        // Cached: no further requests.
        await service.LookupAsync(title, null, "token", index);
        Assert.Equal(2, handler.Requests.Count);
        var cached = EntertainmentMetadata.Decode(index.CachedMetadata($"tmdb-base|{title.Id}|0")!);
        Assert.Equal((603, "The Matrix", 25000), (cached!.ProviderId, cached.Title, cached.VoteCount));
    }

    [Fact]
    public async Task TmdbAmbiguityAndMissingTokenAreReported()
    {
        using var index = NewIndex();
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/3/search/movie"] = (200, """{"results":[{"id":1,"title":"Movie","release_date":"2020-01-01"},{"id":2,"title":"Movie","release_date":"2020-06-01"}]}"""),
        });
        var service = new DiscoveryService(handler.Client(), TimeSpan.Zero);
        var title = Movie("Movie", "2020");
        Assert.Equal(DiscoveryErrorKind.Ambiguous, (await Assert.ThrowsAsync<DiscoveryException>(() => service.LookupAsync(title, null, "t", index))).Kind);
        Assert.Equal(DiscoveryErrorKind.MissingToken, (await Assert.ThrowsAsync<DiscoveryException>(() => service.LookupAsync(title, null, " ", index))).Kind);
        var failing = new DiscoveryService(FakeHttpHandler.ByPath(new() { ["/3/search/movie"] = (500, "") }).Client(), TimeSpan.Zero);
        Assert.Equal(DiscoveryErrorKind.InvalidResponse, (await Assert.ThrowsAsync<DiscoveryException>(() => failing.LookupAsync(title, null, "t", index))).Kind);
    }

    [Fact]
    public async Task TmdbCorrectionIdSkipsSearchAndSeriesGetEpisodeAirDates()
    {
        using var index = NewIndex();
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/3/tv/77"] = (200, """{"id":77,"name":"Show","vote_average":7.5,"vote_count":300,"first_air_date":"2020-01-01","episode_run_time":[]}"""),
            ["/3/tv/77/season/1"] = (200, """{"episodes":[{"episode_number":1,"season_number":1,"air_date":"2020-01-01"},{"episode_number":2,"season_number":1,"air_date":"2020-01-08"},{"episode_number":3,"season_number":1,"air_date":"2020-01-15"}]}"""),
        });
        var service = new DiscoveryService(handler.Client(), TimeSpan.Zero);
        var title = EntertainmentGrouping.Group([EntertainmentTests.Version("Show.S01E01.mkv", "TV"), EntertainmentTests.Version("Show.S01E02.mkv", "TV")]).Single();
        var correction = new EntertainmentMatchCorrection("Show", null, EntertainmentKind.Series, 77);
        var metadata = await service.LookupAsync(title, correction, "t", index);
        Assert.Equal(new Dictionary<string, string> { ["1|1"] = "2020-01-01", ["1|2"] = "2020-01-08" }, metadata.EpisodeReleaseDates);
        Assert.DoesNotContain(handler.Requests, r => r.Url.AbsolutePath.Contains("search"));
        Assert.Equal("2020-01-08", (title with { Metadata = metadata }).LatestReleaseDate);
        // An empty episode_run_time list means an unknown runtime, not 0 minutes.
        Assert.Null(metadata.RuntimeMinutes);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, new int[0], null)]
    [InlineData(0, new[] { 0 }, null)]
    [InlineData(0, new[] { 0, 45 }, 45)]
    [InlineData(null, new[] { 42, 50 }, 42)]
    [InlineData(118, new[] { 42 }, 118)]
    public void RuntimeIsNullWithoutAPositiveValue(int? runtime, int[]? episodes, int? expected) =>
        Assert.Equal(expected, DiscoveryService.RuntimeMinutes(runtime, episodes));

    [Fact]
    public async Task StoreEnrichmentProcessesBoundedBatchAndRemembersAmbiguousTitles()
    {
        var secrets = new InMemorySecretStore();
        secrets.Save(SecretNames.TmdbReadToken, "token");
        var handler = new FakeHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/3/search/movie")
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                return Task.FromResult(query.Contains("query=Ambiguous")
                    ? FakeHttpHandler.Text(200, """{"results":[]}""")
                    : FakeHttpHandler.Text(200, """{"results":[{"id":5,"title":"Known","release_date":"2010-01-01"}]}"""));
            }
            return Task.FromResult(FakeHttpHandler.Text(200, """{"id":5,"title":"Known","vote_average":7,"vote_count":100}"""));
        });
        using var fixture = new StoreFixture(secrets, new DiscoveryService(handler.Client(), TimeSpan.Zero));
        fixture.SetCatalogue([
            EntertainmentGrouping.Group([EntertainmentTests.Version("Known.2010.mkv")]).Single(),
            EntertainmentGrouping.Group([EntertainmentTests.Version("Ambiguous.2011.mkv")]).Single(),
        ]);
        await fixture.Store.EnrichAsync();
        Assert.Equal(5, fixture.Store.Catalogue.Single(t => t.Name == "Known").Metadata?.ProviderId);
        Assert.Contains("Correct Match", fixture.Store.ErrorMessage);
        var requests = handler.Requests.Count;
        // Both titles are now fresh or remembered as ambiguous: a reload needs no new work.
        await fixture.Store.ReloadAsync();
        await fixture.Store.EnrichAsync();
        Assert.Equal(requests, handler.Requests.Count);
    }

    // ---------- OMDb ----------

    [Fact]
    public async Task OmdbUsesOnlyIdentityAndHandlesFailureAndMissingKey()
    {
        var body = """{"Title":"The Matrix","Year":"1999","imdbRating":"8.7","Response":"True","Plot":"Test plot","imdbID":"tt0133093"}""";
        var handler = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, body)));
        var service = new MetadataService(handler.Client());
        var identity = MediaIdentity.Parse("The.Matrix.1999.1080p.mkv");
        var metadata = await service.LookupAsync(identity, "fixture-not-a-real-key", null);
        Assert.Equal("8.7", metadata.ImdbRating);
        var captured = handler.Requests.Single();
        Assert.Equal("https", captured.Url.Scheme);
        Assert.Equal("www.omdbapi.com", captured.Url.Host);
        Assert.Equal(new HashSet<string> { "apikey", "plot", "t", "y" }, Query(captured.Url).Keys.ToHashSet());

        body = """{"Response":"False","Error":"Movie not found!"}""";
        Assert.Equal(MetadataErrorKind.NotFound, (await Assert.ThrowsAsync<MetadataException>(() => service.LookupAsync(identity, "k", null))).Kind);
        body = "not JSON";
        Assert.Equal(MetadataErrorKind.BadResponse, (await Assert.ThrowsAsync<MetadataException>(() => service.LookupAsync(identity, "k", null))).Kind);
        var before = handler.Requests.Count;
        Assert.Equal(MetadataErrorKind.MissingKey, (await Assert.ThrowsAsync<MetadataException>(() => service.LookupAsync(identity, "", null))).Kind);
        Assert.Equal(before, handler.Requests.Count);
        Assert.Null(MovieMetadata.Available("N/A"));
    }

    [Fact]
    public async Task OmdbCachesInTheIndexAndRefusesForeignRedirects()
    {
        using var index = NewIndex();
        var handler = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, """{"Title":"X","Response":"True"}""")));
        var service = new MetadataService(handler.Client());
        var identity = new MediaIdentity("X", "2000");
        await service.LookupAsync(identity, "k", index);
        await service.LookupAsync(identity, "k", index);
        Assert.Single(handler.Requests);

        var redirect = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/");
            return Task.FromResult(response);
        });
        var redirected = new MetadataService(redirect.Client());
        await Assert.ThrowsAsync<MetadataException>(() => redirected.LookupAsync(new MediaIdentity("Y"), "k", null));
        Assert.Single(redirect.Requests);
    }

    // ---------- OpenSubtitles ----------

    [Fact]
    public async Task SubtitleSearchUsesIdentityAndParsesSelectableFiles()
    {
        var handler = FakeHttpHandler.ByPath(new() { ["/api/v1/subtitles"] = (200, SearchJson) });
        var service = new OpenSubtitlesService(handler.Client());
        var page = await service.SearchAsync(MediaIdentity.Parse("Series.S01E02.1080p.mkv"), "en", Credentials);
        var result = Assert.Single(page.Results);
        Assert.Equal(24, result.Id);
        Assert.Equal(23.976, result.Fps);
        Assert.True(result.HearingImpaired);
        Assert.True(page.HasMore);
        var request = handler.Requests.Single();
        Assert.Equal("fixture-key", request.Header("Api-Key"));
        var query = Query(request.Url);
        Assert.Equal("1", query["season_number"]);
        Assert.Equal("2", query["episode_number"]);
        Assert.Equal("Series", query["query"]);
        Assert.Equal("api.opensubtitles.com", request.Url.Host);
    }

    [Fact]
    public async Task SubtitleDownloadsOnlySelectedFileIntoSafeCacheAndReusesIt()
    {
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/api/v1/subtitles"] = (200, SearchJson),
            ["/api/v1/download"] = (200, """{"link":"https://www.opensubtitles.com/download/selected","remaining":4}"""),
            ["/download/selected"] = (200, Subtitle),
        });
        var service = new OpenSubtitlesService(handler.Client());
        var folder = Path.Combine(_temp.Path, "subs");
        var cache = new SubtitleCache(folder);
        var page = await service.SearchAsync(new MediaIdentity("Series"), "en", Credentials);
        var (path, remaining) = await service.DownloadAsync(page.Results[0], Credentials, cache);
        Assert.Equal(4, remaining);
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(path));
        Assert.Equal("MyraSub-24.srt", Path.GetFileName(path));
        Assert.Equal(Subtitle, File.ReadAllText(path));
        var requests = handler.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Contains("\"file_id\":24", requests[1].Body);
        Assert.Null(requests[2].Header("Api-Key"));
        Assert.Null(requests[2].Header("Authorization"));
        await service.DownloadAsync(page.Results[0], Credentials, cache);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(429, "{}", SubtitleErrorKind.RateLimited)]
    [InlineData(401, "{}", SubtitleErrorKind.Authentication)]
    [InlineData(406, "{}", SubtitleErrorKind.Quota)]
    [InlineData(503, "{}", SubtitleErrorKind.Server)]
    [InlineData(200, "not JSON", SubtitleErrorKind.InvalidResponse)]
    public async Task SubtitleFailuresAreVisibleErrors(int status, string body, SubtitleErrorKind expected)
    {
        var service = new OpenSubtitlesService(FakeHttpHandler.ByPath(new() { ["/api/v1/subtitles"] = (status, body) }).Client());
        var error = await Assert.ThrowsAsync<SubtitleException>(() => service.SearchAsync(new MediaIdentity("Series"), "en", Credentials));
        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task MissingKeyAndInvalidSearchSendNothing()
    {
        var handler = FakeHttpHandler.ByPath(new());
        var service = new OpenSubtitlesService(handler.Client());
        Assert.Equal(SubtitleErrorKind.MissingKey,
            (await Assert.ThrowsAsync<SubtitleException>(() => service.SearchAsync(new MediaIdentity("Series"), "en", new SubtitleCredentials()))).Kind);
        Assert.Equal(SubtitleErrorKind.InvalidSearch,
            (await Assert.ThrowsAsync<SubtitleException>(() => service.SearchAsync(new MediaIdentity("Series"), "EN; drop", Credentials))).Kind);
        Assert.Equal(SubtitleErrorKind.InvalidSearch,
            (await Assert.ThrowsAsync<SubtitleException>(() => service.SearchAsync(new MediaIdentity(" "), "en", Credentials))).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void UntrustedAddressesAndNonSubtitleDataAreRejected()
    {
        foreach (var url in new[]
                 {
                     "http://www.opensubtitles.com/file", "https://evil.example/file", "https://api.opensubtitles.com.evil.example/file",
                     "https://user:secret@www.opensubtitles.com/file", "https://www.opensubtitles.com:8443/file",
                 })
            Assert.False(SubtitleUrlPolicy.Accepts(new Uri(url), download: true));
        Assert.True(SubtitleUrlPolicy.Accepts(new Uri("https://dl.opensubtitles.com/file"), download: true));
        Assert.False(SubtitleUrlPolicy.Accepts(new Uri("https://dl.opensubtitles.com/file"), download: false));
        var folder = Path.Combine(_temp.Path, "invalid");
        var cache = new SubtitleCache(folder);
        Assert.Equal(SubtitleErrorKind.InvalidFile, Assert.Throws<SubtitleException>(() => cache.Store(Encoding.UTF8.GetBytes("<html>not subtitles</html>"), 1)).Kind);
        Assert.Equal(SubtitleErrorKind.Oversized, Assert.Throws<SubtitleException>(() => cache.Store(new byte[SubtitleCache.MaximumFileBytes + 1], 1)).Kind);
        Assert.Equal(SubtitleErrorKind.InvalidFile, Assert.Throws<SubtitleException>(() => cache.Store(Encoding.UTF8.GetBytes(Subtitle), 0)).Kind);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public async Task OversizedDownloadIsRejectedWhileStreaming()
    {
        var huge = new string('x', SubtitleCache.MaximumFileBytes + 10);
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/api/v1/download"] = (200, """{"link":"https://dl.opensubtitles.com/big","remaining":1}"""),
            ["/big"] = (200, huge),
        });
        var service = new OpenSubtitlesService(handler.Client());
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "big"));
        var selected = new OnlineSubtitleResult(7, "x.srt", "x", "en", null, 1, false, false);
        Assert.Equal(SubtitleErrorKind.Oversized, (await Assert.ThrowsAsync<SubtitleException>(() => service.DownloadAsync(selected, Credentials, cache))).Kind);
        Assert.Null(cache.Cached(7));
    }

    [Fact]
    public async Task CancellationStopsPendingSubtitleSearch()
    {
        var service = new OpenSubtitlesService(FakeHttpHandler.ByPath(new() { ["/api/v1/subtitles"] = (200, SearchJson) }, TimeSpan.FromSeconds(5)).Client());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync(new MediaIdentity("Series"), "en", Credentials, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task AccountLoginUsesVerifiedHostAndKeepsCredentialsOffFileRequests()
    {
        var handler = FakeHttpHandler.ByPath(new()
        {
            ["/api/v1/login"] = (200, """{"token":"fixture-token","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":20}}"""),
            ["/api/v1/subtitles"] = (200, SearchJson),
        });
        var service = new OpenSubtitlesService(handler.Client());
        var account = new SubtitleCredentials("fixture-key", "fixture-user", "fixture-password");
        Assert.Equal(20, await service.SignInAsync(account));
        await service.SearchAsync(new MediaIdentity("Series"), "en", account);
        var requests = handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal("vip-api.opensubtitles.com", requests[1].Url.Host);
        Assert.Equal("Bearer fixture-token", requests[1].Header("Authorization"));
        Assert.DoesNotContain("fixture-password", requests[1].Url.AbsoluteUri);

        var untrusted = new OpenSubtitlesService(FakeHttpHandler.ByPath(new()
        {
            ["/api/v1/login"] = (200, """{"token":"t","base_url":"evil.example"}"""),
        }).Client());
        Assert.Equal(SubtitleErrorKind.InvalidResponse, (await Assert.ThrowsAsync<SubtitleException>(() => untrusted.SignInAsync(account))).Kind);
    }

    [Fact]
    public async Task UnsafeProviderDownloadLinkNeverGetsFetched()
    {
        var handler = FakeHttpHandler.ByPath(new() { ["/api/v1/download"] = (200, """{"link":"https://evil.example/sub.srt","remaining":4}""") });
        var service = new OpenSubtitlesService(handler.Client());
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "unsafe"));
        var selected = new OnlineSubtitleResult(24, "ignored.srt", "Series", "en", null, 1, false, false);
        Assert.Equal(SubtitleErrorKind.UnsafeUrl, (await Assert.ThrowsAsync<SubtitleException>(() => service.DownloadAsync(selected, Credentials, cache))).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SubtitleCacheRejectsSymlinksAndPrunesOnlyOwnedExpiredFiles()
    {
        var folder = Path.Combine(_temp.Path, "cache-safety");
        var directory = Path.Combine(folder, "cache");
        var cache = new SubtitleCache(directory);
        var data = Encoding.UTF8.GetBytes(Subtitle);
        var external = Path.Combine(folder, "outside.srt");
        File.WriteAllBytes(external, data);
        var link = Path.Combine(directory, "MyraSub-24.srt");
        try
        {
            File.CreateSymbolicLink(link, external);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return; // Symbolic links need extra rights on some Windows machines.
        }
        Assert.Null(cache.Cached(24));
        Assert.Equal(SubtitleErrorKind.UnsafeUrl, Assert.Throws<SubtitleException>(() => cache.Store(data, 24)).Kind);
        Assert.Equal(data, File.ReadAllBytes(external));

        var old = cache.Store(data, 25);
        var unrelated = Path.Combine(directory, "personal.srt");
        File.WriteAllBytes(unrelated, data);
        var expired = DateTime.UtcNow.AddDays(-31);
        File.SetLastWriteTimeUtc(old, expired);
        File.SetLastWriteTimeUtc(unrelated, expired);
        Assert.Null(cache.Cached(25));
        cache.Store(data, 26);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(data, File.ReadAllBytes(external));
        Assert.NotNull(cache.Cached(26));
    }
}
