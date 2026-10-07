using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Myra.Core.Tests;

/// Schema migration, staged refresh and folder schedules (ported from LibraryIndexTests/DiscoveryTests.swift).
public sealed class IndexRefreshTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    private string DatabasePath() => Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"), "LibraryIndex.sqlite");

    private static GlobalSearchRoot Root(string name = "Movies") => new(Guid.NewGuid(), name, new Uri($"https://media.example/{name}/"));

    private static GlobalSearchResult Video(GlobalSearchRoot root, string name, string folder = "")
    {
        var path = folder.Length == 0 ? name : folder + "/" + name;
        var url = new Uri(root.Url, path);
        return new GlobalSearchResult(root.Id, root.Name, root.Url,
            new DirectoryEntry(name, url, EntryKind.File, 12345, DateTimeOffset.FromUnixTimeSeconds(100).ToLocalTime()),
            path, new Uri(url, "poster.jpg"));
    }

    private static GlobalSearchService Scanner(Func<Uri, UrlBoundary, DirectoryListing> listing) =>
        new((url, boundary, _) => Task.FromResult(listing(url, boundary)));

    private static Task Ignore(GlobalSearchSnapshot _) => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static void ExecuteRaw(string path, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long UserVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void WindowsV2IndexMigratesInPlaceWithBackupAndKeepsData()
    {
        // The schema written by the first Windows release: first_discovered inline, user_version=2, FTS triggers.
        var path = DatabasePath();
        var source = Root();
        var movie = Video(source, "Legacy.Movie.2020.mkv", "Films");
        ExecuteRaw(path, $"""
            PRAGMA journal_mode=WAL;
            CREATE TABLE videos (
              category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
              url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL,
              relative_path TEXT NOT NULL, artwork TEXT, size INTEGER, modified REAL,
              generation TEXT NOT NULL, first_discovered REAL NOT NULL DEFAULT 0,
              PRIMARY KEY(category, url));
            CREATE TABLE source_scans (category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
            CREATE TABLE metadata (cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
            CREATE TABLE playback (url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);
            CREATE VIRTUAL TABLE video_fts USING fts5(search_name, content='videos', content_rowid='rowid', tokenize='trigram');
            CREATE TRIGGER videos_ai AFTER INSERT ON videos BEGIN
              INSERT INTO video_fts(rowid,search_name) VALUES(new.rowid,new.search_name); END;
            INSERT INTO videos VALUES('{source.Id}','{source.Url.AbsoluteUri}','Movies','{movie.Entry.Url.AbsoluteUri}',
              'Legacy.Movie.2020.mkv','legacy.movie.2020.mkv','Films/Legacy.Movie.2020.mkv',NULL,12345,100,'old',42);
            INSERT INTO source_scans VALUES('{source.Id}','{source.Url.AbsoluteUri}',1000);
            INSERT INTO metadata VALUES('k','payload',{DateTimeOffset.UtcNow.ToUnixTimeSeconds()});
            INSERT INTO playback VALUES('{movie.Entry.Url.AbsoluteUri}',600,3600,100);
            PRAGMA user_version=2;
            """);

        using (var index = new LibraryIndex(path))
        {
            Assert.Equal(1, index.VideoCount());
            var inventory = index.Inventory();
            Assert.Single(inventory);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(42), inventory[0].FirstDiscovered);
            Assert.Equal(600, inventory[0].ProgressSeconds);
            Assert.Equal(600, index.PlaybackPosition(movie.Entry.Url));
            Assert.Equal("payload", index.CachedMetadata("k"));
            Assert.Single(index.Search("legacy"));
            Assert.Equal(0, index.Revision());
            // The folder tree is inferred from file paths before any snapshot exists.
            var rows = index.FolderRows([source]);
            Assert.Equal(1, rows.Single(r => r.Scope.Folder == source.Url).Files);
            Assert.Equal(1, rows.Single(r => r.Scope.Folder.AbsolutePath.EndsWith("/Films/")).Files);
        }
        Assert.Equal(4, UserVersion(path));
        var backup = path + ".v2-backup";
        Assert.True(File.Exists(backup));
        Assert.Equal(2, UserVersion(backup));
        using (var check = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            check.Open();
            using var command = check.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM videos";
            Assert.Equal(1L, command.ExecuteScalar());
        }

        // Reopening a v3 index neither re-migrates nor replaces the backup.
        var stamp = File.GetLastWriteTimeUtc(backup);
        using (var reopened = new LibraryIndex(path)) Assert.Equal(1, reopened.VideoCount());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(backup));
    }

    /// Opt-in (MYRA_LARGE_MIGRATION=1): a Windows v2 index of 120,000 videos, similar in size to a live index.
    [Fact]
    public void LargeWindowsV2IndexMigratesWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("MYRA_LARGE_MIGRATION") != "1") return;
        var path = DatabasePath();
        var source = Root();
        ExecuteRaw(path, """
            CREATE TABLE videos (category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
              url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL, relative_path TEXT NOT NULL, artwork TEXT,
              size INTEGER, modified REAL, generation TEXT NOT NULL, first_discovered REAL NOT NULL DEFAULT 0, PRIMARY KEY(category, url));
            CREATE TABLE source_scans (category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
            CREATE TABLE metadata (cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
            CREATE TABLE playback (url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);
            CREATE VIRTUAL TABLE video_fts USING fts5(search_name, content='videos', content_rowid='rowid', tokenize='trigram');
            CREATE TRIGGER videos_ai AFTER INSERT ON videos BEGIN INSERT INTO video_fts(rowid,search_name) VALUES(new.rowid,new.search_name); END;
            PRAGMA user_version=2;
            """);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO videos VALUES($c,$r,'Movies',$u,$n,$s,$p,NULL,1000000,100,'g',42)";
            var u = command.Parameters.Add("$u", SqliteType.Text);
            var n = command.Parameters.Add("$n", SqliteType.Text);
            var s = command.Parameters.Add("$s", SqliteType.Text);
            var p = command.Parameters.Add("$p", SqliteType.Text);
            command.Parameters.AddWithValue("$c", source.Id.ToString());
            command.Parameters.AddWithValue("$r", source.Url.AbsoluteUri);
            for (var i = 0; i < 120_000; i++)
            {
                var name = $"Some.Long.Movie.Title.{i}.2020.1080p.WEB-DL.x264.mkv";
                var relative = $"Folder {i % 500}/Sub {i % 7}/{name}";
                u.Value = new Uri(source.Url, relative).AbsoluteUri;
                n.Value = name;
                s.Value = name.ToLowerInvariant();
                p.Value = relative;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var size = new FileInfo(path).Length;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var index = new LibraryIndex(path);
        var migrated = watch.Elapsed;
        Assert.Equal(120_000, index.VideoCount());
        Assert.True(new FileInfo(path + ".v2-backup").Length > 0);
        watch.Restart();
        var rows = index.FolderRows([source]);
        var tree = watch.Elapsed;
        watch.Restart();
        var catalogue = EntertainmentCatalogue.Prepare(index, new EntertainmentPersonalData());
        var cold = watch.Elapsed;
        watch.Restart();
        var cached = EntertainmentCatalogue.Prepare(index, new EntertainmentPersonalData());
        Assert.True(cached.RestoredFromSnapshot);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myra-large-migration.txt"), $"Large migration: {size / 1048576.0:F0} MiB index, migrate+backup {migrated.TotalSeconds:F2}s, " +
                          $"{rows.Count} folder rows {tree.TotalSeconds:F2}s, {catalogue.Titles.Count} titles cold {cold.TotalSeconds:F2}s, " +
                          $"snapshot restore {watch.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public void V1MigrationAddsDiscoveryDatesBacksUpAndPreservesPlayback()
    {
        var path = DatabasePath();
        var source = Root();
        var movie = Video(source, "Movie.mkv");
        ExecuteRaw(path, $"""
            CREATE TABLE videos(category TEXT NOT NULL,root TEXT NOT NULL,category_name TEXT NOT NULL,
              url TEXT NOT NULL,name TEXT NOT NULL,search_name TEXT NOT NULL,relative_path TEXT NOT NULL,
              artwork TEXT,size INTEGER,modified REAL,generation TEXT NOT NULL,PRIMARY KEY(category,url));
            CREATE TABLE source_scans(category TEXT PRIMARY KEY,root TEXT NOT NULL,completed REAL NOT NULL);
            CREATE TABLE playback(url TEXT PRIMARY KEY,seconds REAL NOT NULL,duration REAL NOT NULL,updated REAL NOT NULL);
            INSERT INTO videos VALUES('{source.Id}','{source.Url.AbsoluteUri}','Movies','{movie.Entry.Url.AbsoluteUri}','Movie.mkv','movie.mkv','Movie.mkv',NULL,1,100,'g');
            INSERT INTO source_scans VALUES('{source.Id}','{source.Url.AbsoluteUri}',777);
            INSERT INTO playback VALUES('{movie.Entry.Url.AbsoluteUri}',20,100,100);
            PRAGMA user_version=1;
            """);
        using var index = new LibraryIndex(path);
        Assert.Equal(20, index.PlaybackPosition(movie.Entry.Url));
        Assert.True(File.Exists(path + ".v1-backup"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(777), index.Inventory().Single().FirstDiscovered);
        Assert.Equal(4, UserVersion(path));
    }

    [Fact]
    public void NewerSchemaIsRejectedWithoutChanges()
    {
        var path = DatabasePath();
        ExecuteRaw(path, "CREATE TABLE future(x); PRAGMA user_version=5;");
        Assert.Throws<LibraryIndexException>(() => new LibraryIndex(path));
        Assert.Equal(5, UserVersion(path));
        Assert.False(File.Exists(path + ".v5-backup"));
    }

    [Fact]
    public void InventoryDiscoveryDatesAndPlaybackSurviveRefreshAndReopen()
    {
        var path = DatabasePath();
        var media = Video(Root(), "Movie.2025.mkv");
        List<EntertainmentVersion> first;
        using (var index = new LibraryIndex(path))
        {
            index.Upsert([media], Guid.NewGuid());
            first = index.Inventory();
            index.SavePlaybackPosition(media.Entry.Url, 15, 100);
            index.Upsert([media], Guid.NewGuid());
        }
        using var reopened = new LibraryIndex(path);
        var after = reopened.Inventory();
        Assert.Equal(first[0].FirstDiscovered, after[0].FirstDiscovered);
        Assert.Equal(15, after[0].ProgressSeconds);
        Assert.NotNull(after[0].LastPlayed);
    }

    [Fact]
    public async Task SelectedFolderPublicationKeepsOtherSourcesAndSiblings()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var other = Root("Other");
        var keep = Video(source, "Keep.Movie.mkv", "Archive");
        var old = Video(source, "Old.Movie.mkv", "New");
        var unrelated = Video(other, "Other.Movie.mkv");
        index.Upsert([keep, old, unrelated], Guid.NewGuid());
        var fresh = Video(source, "Fresh.Movie.mkv", "New");
        var scope = new IndexScope(source, new Uri(source.Url, "New/"));
        var scanner = Scanner((requested, boundary) =>
        {
            Assert.Equal(scope.Folder, requested);
            Assert.Equal(source.Url, boundary.Root);
            return new DirectoryListing(requested, [fresh.Entry], fresh.ArtworkUrl);
        });
        await new LibraryIndexer(scanner).RefreshAsync([source, other], index, Guid.NewGuid(), Ignore, scopes: [scope]);
        var inventory = index.Inventory().Select(v => v.Media).ToHashSet();
        Assert.Equal(new HashSet<GlobalSearchResult> { keep, fresh, unrelated }, inventory);
    }

    [Fact]
    public async Task StagingIsInvisibleDuringScanAndCancellationDiscardsIt()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var old = Video(source, "Old.Movie.mkv");
        index.Upsert([old], Guid.NewGuid());
        var files = Enumerable.Range(0, 300).Select(i => Video(source, $"New.Movie.{i}.mkv").Entry).ToList();
        var scanner = Scanner((requested, _) => new DirectoryListing(requested, files, null));
        using var cancellation = new CancellationTokenSource();
        var sawStaged = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LibraryIndexer(scanner).RefreshAsync(
            [source], index, Guid.NewGuid(), snapshot =>
            {
                if (snapshot.Progress.MatchesFound == 300)
                {
                    Assert.Equal([old], index.Inventory().Select(v => v.Media));
                    sawStaged = true;
                    cancellation.Cancel();
                }
                return Task.CompletedTask;
            }, cancellationToken: cancellation.Token));
        Assert.True(sawStaged);
        Assert.Equal([old], index.Inventory().Select(v => v.Media));
    }

    [Fact]
    public async Task StagingDatabaseIsPrivateAndRemovedAfterRefresh()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var staging = Path.Combine(_temp.Path, "staging");
        Directory.CreateDirectory(staging);
        string? seen = null;
        var scanner = Scanner((requested, _) =>
        {
            seen = Directory.GetDirectories(staging).Single();
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(seen));
            return new DirectoryListing(requested, [Video(source, "A.mkv").Entry], null);
        });
        await new LibraryIndexer(scanner) { StagingDirectory = staging }.RefreshAsync([source], index, Guid.NewGuid(), Ignore);
        Assert.NotNull(seen);
        Assert.Empty(Directory.GetDirectories(staging));
        Assert.Equal(1, index.VideoCount());
    }

    [Fact]
    public async Task UnchangedPromotionKeepsRevisionAndDiscoveryDate()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var original = Video(source, "Same.Movie.mkv");
        index.Upsert([original], Guid.NewGuid());
        var before = index.Inventory();
        var revision = index.Revision();
        var indexer = new LibraryIndexer(Scanner((requested, _) => new DirectoryListing(requested, [original.Entry], original.ArtworkUrl)));
        await indexer.RefreshAsync([source], index, Guid.NewGuid(), Ignore);
        Assert.Equal(revision, index.Revision());
        Assert.Equal(before[0].FirstDiscovered, index.Inventory()[0].FirstDiscovered);
        Assert.Equal(0, indexer.Summary.Added + indexer.Summary.Changed + indexer.Summary.Removed);
        Assert.False(index.NeedsRefresh([source]));
    }

    [Fact]
    public async Task FailedSourcePublishesNoPartialChangesButSuccessfulSourceCompletes()
    {
        using var index = new LibraryIndex(DatabasePath());
        var bad = Root("Bad");
        var good = Root("Good");
        var old = Video(bad, "Old.Movie.mkv");
        index.Upsert([old], Guid.NewGuid());
        var scanner = Scanner((requested, boundary) =>
        {
            if (requested == bad.Url)
                return new DirectoryListing(requested,
                [
                    new DirectoryEntry("New.Movie.mkv", new Uri(requested, "New.Movie.mkv"), EntryKind.File),
                    new DirectoryEntry("Broken", new Uri(requested, "Broken/"), EntryKind.Folder),
                ], null);
            if (boundary.Root == bad.Url) throw new DirectoryException(DirectoryErrorKind.BadResponse, 503);
            return new DirectoryListing(requested, [new DirectoryEntry("Good.Movie.mkv", new Uri(requested, "Good.Movie.mkv"), EntryKind.File)], null);
        });
        var snapshot = await new LibraryIndexer(scanner).RefreshAsync([bad, good], index, Guid.NewGuid(), Ignore);
        var after = index.Inventory();
        Assert.Single(snapshot.Failures);
        Assert.Equal([old], after.Where(v => v.Media.CategoryId == bad.Id).Select(v => v.Media));
        Assert.Single(after, v => v.Media.CategoryId == good.Id);
        Assert.True(index.NeedsRefresh([bad]));
        Assert.False(index.NeedsRefresh([good]));
    }

    [Fact]
    public void FolderPoliciesInheritanceAndCompactionRespectPathBoundaries()
    {
        var source = Root();
        var rootScope = IndexScope.ForRoot(source);
        var archive = new IndexScope(source, new Uri(source.Url, "Archive/"));
        var child = new IndexScope(source, new Uri(source.Url, "Archive/Season/"));
        var sibling = new IndexScope(source, new Uri(source.Url, "Archive2/"));
        var policies = new IndexPolicies();
        policies.Set(rootScope, IndexSchedule.Weekly);
        policies.Set(archive, IndexSchedule.Manual);
        Assert.Equal(IndexSchedule.Manual, policies.ScheduleFor(child));
        Assert.Equal(IndexSchedule.Weekly, policies.ScheduleFor(sibling));
        Assert.Equal(IndexSchedule.Daily, policies.ScheduleFor(IndexScope.ForRoot(Root("Other"))));
        Assert.Equal(new HashSet<IndexScope> { archive, sibling }, IndexScope.Compact([archive, child, sibling]).ToHashSet());
        Assert.False(IndexSchedule.Manual.IsDue(null));
        Assert.False(IndexSchedule.Weekly.IsDue(DateTimeOffset.Now.AddDays(-1)));
        Assert.True(IndexSchedule.Daily.IsDue(DateTimeOffset.Now.AddSeconds(-86401)));
        Assert.True(IndexSchedule.Weekly.IsDue(null));

        policies.Set(archive, null);
        Assert.Equal(IndexSchedule.Weekly, policies.ScheduleFor(child));
        var file = Path.Combine(Path.GetTempPath(), "Myra-policies-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            policies.Save(file);
            Assert.Equal(IndexSchedule.Weekly, IndexPolicies.Load(file).ScheduleFor(child));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExistingIndexProvidesFolderSelectionBeforeFirstSnapshot()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        index.Upsert([Video(source, "Movie.mkv", "Archive/2025"), Video(source, "Other.mkv", "New")], Guid.NewGuid());
        var rows = index.FolderRows([source]);
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Single(r => r.Scope.Folder == source.Url).Files);
        Assert.Equal(1, rows.Single(r => r.Scope.Folder.LastPathComponent() == "Archive").Files);
        Assert.Equal(1, rows.Single(r => r.Scope.Folder.LastPathComponent() == "2025").Files);
        Assert.Contains(rows, r => r.Scope.Folder.LastPathComponent() == "New");
    }

    [Fact]
    public void SavedHomeSnapshotAvoidsCatalogueRebuildAndInvalidatesOnNewContent()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        index.Upsert([Video(source, "Movie.2025.mkv")], Guid.NewGuid());
        var personal = new EntertainmentPersonalData();
        var cold = EntertainmentCatalogue.Prepare(index, personal);
        var cached = EntertainmentCatalogue.Prepare(index, personal);
        Assert.True(cold.MetadataQueryCount > 0);
        Assert.False(cold.RestoredFromSnapshot);
        Assert.Equal(0, cached.MetadataQueryCount);
        Assert.True(cached.RestoredFromSnapshot);
        Assert.Equal(cold.Titles.Select(t => t.Id), cached.Titles.Select(t => t.Id));
        Assert.Equal(cold.Titles[0].Versions[0].Media, cached.Titles[0].Versions[0].Media);

        index.Upsert([Video(source, "Another.2025.mkv")], Guid.NewGuid());
        var updated = EntertainmentCatalogue.Prepare(index, personal);
        Assert.Equal(2, updated.Titles.Count);
        Assert.True(updated.MetadataQueryCount > 0);

        // A match correction also invalidates the saved snapshot.
        personal.MatchCorrections[updated.Titles[0].Versions[0].Id] = new EntertainmentMatchCorrection("Renamed", "2024", EntertainmentKind.Movie);
        var corrected = EntertainmentCatalogue.Prepare(index, personal);
        Assert.False(corrected.RestoredFromSnapshot);
        Assert.Contains(corrected.Titles, t => t.Name == "Renamed");
    }

    /// Serves listings with ETag versions and counts requests and 304 replies.
    private sealed class ListingServer
    {
        private readonly Dictionary<string, (string Html, int Version)> _pages = [];
        private readonly Dictionary<string, int> _calls = [];
        public int NotModified { get; private set; }

        public void Set(Uri url, string html)
        {
            lock (_pages) _pages[url.AbsoluteUri] = (html, (_pages.TryGetValue(url.AbsoluteUri, out var old) ? old.Version : 0) + 1);
        }

        public int Calls(Uri url)
        {
            lock (_pages) return _calls.GetValueOrDefault(url.AbsoluteUri);
        }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            lock (_pages)
            {
                var key = request.RequestUri!.AbsoluteUri;
                if (!_pages.TryGetValue(key, out var page)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                _calls[key] = _calls.GetValueOrDefault(key) + 1;
                var tag = $"\"version-{page.Version}\"";
                if (request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains(tag))
                {
                    NotModified++;
                    var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
                    unchanged.Headers.TryAddWithoutValidation("ETag", tag);
                    return unchanged;
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Html, Encoding.UTF8, "text/html") };
                response.Headers.TryAddWithoutValidation("ETag", tag);
                return response;
            }
        }
    }

    [Fact]
    public async Task ConditionalRefreshFindsChildChangesUnderUnchangedParentAndSkipsManualBranch()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var child = new Uri(source.Url, "Series/");
        var archive = new Uri(source.Url, "Archive/");
        var server = new ListingServer();
        server.Set(source.Url, "<a href='../'>Parent Directory</a><a href='Series/'>Series</a><a href='Archive/'>Archive</a>");
        server.Set(child, "<a href='../'>Parent Directory</a><a href='Show.S01E01.mkv'>Show.S01E01.mkv</a>");
        server.Set(archive, "<a href='../'>Parent Directory</a><a href='Old.Movie.mkv'>Old.Movie.mkv</a>");
        var handler = new FakeHttpHandler(request => Task.FromResult(server.Reply(request)));
        var service = new DirectoryService(handler.Client());

        await new LibraryIndexer(service).RefreshAsync([source], index, Guid.NewGuid(), Ignore, full: true);
        Assert.Equal(2, index.VideoCount());
        foreach (var folder in new[] { source.Url, child, archive })
        {
            var scope = new IndexScope(source, folder);
            var cached = index.Folder(scope);
            Assert.NotNull(cached);
            Assert.NotNull(cached.ETag);
            index.SaveFolder(cached with { Checked = DateTimeOffset.Now.AddSeconds(-86401) }, scope);
        }

        var policies = new IndexPolicies();
        policies.Set(new IndexScope(source, archive), IndexSchedule.Manual);
        server.Set(child, "<a href='../'>Parent Directory</a><a href='Show.S01E01.mkv'>Show.S01E01.mkv</a><a href='Show.S01E02.mkv'>Show.S01E02.mkv</a>");
        var indexer = new LibraryIndexer(service);
        var result = await indexer.RefreshAsync([source], index, Guid.NewGuid(), Ignore, policies: policies, automatic: true);
        Assert.Empty(result.Failures);
        var all = index.Inventory();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, v => v.Media.Entry.Name == "Show.S01E02.mkv");
        Assert.Contains(all, v => v.Media.Entry.Name == "Old.Movie.mkv");
        Assert.Equal(1, server.Calls(archive));
        Assert.Equal(1, server.NotModified);
        Assert.Equal(1, indexer.Summary.Added);
        Assert.Equal(1, indexer.Summary.Unchanged);

        // A parent refresh keeps an excluded manual branch even if its link disappears.
        server.Set(source.Url, "<a href='../'>Parent Directory</a><a href='Series/'>Series</a>");
        await new LibraryIndexer(service).RefreshAsync([source], index, Guid.NewGuid(), Ignore, policies: policies);
        Assert.Contains(index.Inventory(), v => v.Media.Entry.Name == "Old.Movie.mkv");

        // Explicit selection still refreshes a manual-only folder.
        server.Set(archive, "<a href='../'>Parent Directory</a>");
        await new LibraryIndexer(service).RefreshAsync([source], index, Guid.NewGuid(), Ignore,
            scopes: [new IndexScope(source, archive)], policies: policies);
        Assert.Equal(2, index.Inventory().Count);
        Assert.Equal(2, server.Calls(archive));
    }

    [Fact]
    public async Task ManualBranchWithoutSnapshotFailsSafelyWithoutDeletingRecords()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var archive = new Uri(source.Url, "Archive/");
        var old = Video(source, "Old.Movie.mkv", "Archive");
        index.Upsert([old], Guid.NewGuid());
        var server = new ListingServer();
        server.Set(source.Url, "<a href='../'>Parent Directory</a><a href='Archive/'>Archive</a>");
        server.Set(archive, "<a href='../'>Parent Directory</a>");
        var service = new DirectoryService(new FakeHttpHandler(r => Task.FromResult(server.Reply(r))).Client());
        var policies = new IndexPolicies();
        policies.Set(new IndexScope(source, archive), IndexSchedule.Manual);
        var snapshot = await new LibraryIndexer(service).RefreshAsync([source], index, Guid.NewGuid(), Ignore, policies: policies, automatic: true);
        Assert.Single(snapshot.Failures);
        Assert.Contains("manual-only", snapshot.Failures[0].Message);
        Assert.Equal([old], index.Inventory().Select(v => v.Media));
        Assert.Equal(0, server.Calls(archive));
    }

    [Fact]
    public async Task ConditionalListingRejectsRedirectOutsideSource()
    {
        var source = Root();
        var handler = new FakeHttpHandler(request =>
        {
            var response = FakeHttpHandler.Text(200, "<a href='x.mkv'>x.mkv</a>", "text/html");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://elsewhere.example/");
            return Task.FromResult(response);
        });
        var service = new DirectoryService(new HttpClient(new RedirectingHandler(handler)));
        var error = await Assert.ThrowsAsync<DirectoryException>(() => service.ConditionalListingAsync(source.Url, new UrlBoundary(source.Url), null));
        Assert.Equal(DirectoryErrorKind.OutsideCategoryRoot, error.Kind);
    }

    /// Simulates a handler that followed a redirect: RequestMessage points elsewhere.
    private sealed class RedirectingHandler(FakeHttpHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://elsewhere.example/");
            return response;
        }
    }

    [Fact]
    public async Task ControllerSelectsDueFoldersPausesAndReportsChanges()
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var server = new ListingServer();
        server.Set(source.Url, "<a href='../'>Parent Directory</a><a href='Movie.2024.mkv'>Movie.2024.mkv</a>");
        var service = new DirectoryService(new FakeHttpHandler(r => Task.FromResult(server.Reply(r))).Client());
        var policiesPath = Path.Combine(_temp.Path, "policies.json");
        var controller = new IndexRefreshController(() => index, service, new IndexPolicies(), policiesPath);
        var changed = 0;
        controller.IndexChanged += (_, _) => changed++;

        var first = await controller.RefreshAsync([source], IndexRefreshMode.Due, manual: true);
        Assert.True(first.Changed);
        Assert.Equal(1, first.Summary.Added);
        Assert.Equal(1, changed);
        Assert.False(controller.IsRefreshing);

        // Everything was just checked, so nothing is due.
        var second = await controller.RefreshAsync([source], IndexRefreshMode.Due, manual: true);
        Assert.False(second.Changed);
        Assert.Equal("All scheduled folders are up to date.", second.CompletionMessage);

        controller.SetSchedule(IndexScope.ForRoot(source), IndexSchedule.Weekly);
        Assert.Equal(IndexSchedule.Weekly, IndexPolicies.Load(policiesPath).ScheduleFor(IndexScope.ForRoot(source)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseTakesEffectBetweenFolderBatchesAndCancelDiscardsWork(bool cancel)
    {
        using var index = new LibraryIndex(DatabasePath());
        var source = Root();
        var child = new Uri(source.Url, "Child/");
        var server = new ListingServer();
        server.Set(source.Url, "<a href='../'>Parent Directory</a><a href='Child/'>Child</a><a href='A.2024.mkv'>A.2024.mkv</a>");
        server.Set(child, "<a href='../'>Parent Directory</a><a href='B.2024.mkv'>B.2024.mkv</a>");
        IndexRefreshController? controller = null;
        var handler = new FakeHttpHandler(request =>
        {
            // The in-flight root request finishes; the next batch waits.
            if (request.RequestUri == source.Url) controller!.Pause();
            return Task.FromResult(server.Reply(request));
        });
        controller = new IndexRefreshController(() => index, new DirectoryService(handler.Client()), new IndexPolicies(), Path.Combine(_temp.Path, "p.json"));
        var running = controller.RefreshAsync([source], IndexRefreshMode.All, manual: true);
        for (var i = 0; i < 100 && !controller.IsPaused; i++) await Task.Delay(20);
        Assert.True(controller.IsPaused);
        await Task.Delay(150);
        Assert.Equal(0, server.Calls(child));
        Assert.False(running.IsCompleted);
        if (cancel) controller.Cancel();
        else controller.Resume();
        var outcome = await running;
        Assert.False(controller.IsRefreshing);
        Assert.False(controller.IsPaused);
        Assert.Equal(cancel, outcome.Cancelled);
        Assert.Equal(cancel ? 0 : 2, index.VideoCount());
    }

    [Fact]
    public void FolderConcurrencyYieldsToPlaybackLowImpactAndLowPower()
    {
        Assert.Equal(2, IndexRefreshController.FolderConcurrency(IndexingMode.Balanced, false, false));
        Assert.Equal(1, IndexRefreshController.FolderConcurrency(IndexingMode.LowImpact, false, false));
        Assert.Equal(1, IndexRefreshController.FolderConcurrency(IndexingMode.Balanced, true, false));
        Assert.Equal(1, IndexRefreshController.FolderConcurrency(IndexingMode.Balanced, false, true));
    }
}
