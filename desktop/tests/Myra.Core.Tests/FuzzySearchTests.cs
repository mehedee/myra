using Microsoft.Data.Sqlite;
using Myra.Core;

namespace Myra.Core.Tests;

/// Ported from FuzzySearchTests.swift, plus schema 4 migration checks.
public sealed class FuzzySearchTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string DatabasePath() => Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"), "index.sqlite");

    private static GlobalSearchRoot Root(string name = "Movies") => new(Guid.NewGuid(), name, new Uri($"https://example.com/{name.ToLowerInvariant()}/"));

    private static GlobalSearchResult Video(GlobalSearchRoot root, string name, string? file = null)
    {
        var url = new Uri(root.Url, Uri.EscapeDataString(file ?? name));
        return new GlobalSearchResult(root.Id, root.Name, root.Url, new DirectoryEntry(name, url, EntryKind.File), file ?? name, null);
    }

    private static void Raw(string path, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void TyposReorderedWordsUnicodeAndNumericConstraints()
    {
        Assert.NotNull(FuzzySearch.Score("intersteller", "Interstellar.2014.1080p.mkv"));
        Assert.NotNull(FuzzySearch.Score("rings lord", "The Lord of the Rings.mkv"));
        Assert.NotNull(FuzzySearch.Score("amelie", "Amélie.2001.mkv"));
        Assert.Null(FuzzySearch.Score("movie 2024", "Movie 2023.mkv"));
        Assert.Null(FuzzySearch.Score("jaws", "Lawrence of Arabia.mkv"));
        Assert.True(FuzzySearch.Score("interstellar", "Interstellar.mkv") < FuzzySearch.Score("interstellar", "Intersteller.mkv"));
    }

    [Fact]
    public void NormalizeFoldsCaseDiacriticsAndPunctuation()
    {
        Assert.Equal("amelie 2001 mkv", FuzzySearch.Normalize("  AMÉLIE.2001--mkv "));
        Assert.Equal("", FuzzySearch.Normalize("---"));
        Assert.Equal("naive cafe", FuzzySearch.Normalize("Naïve_Café"));
    }

    [Fact]
    public void BoundedSafeCandidateExpression()
    {
        var expression = FuzzySearch.MatchExpression(string.Concat(Enumerable.Repeat("abcdef ", 1000)))!;
        Assert.True(expression.Split(" OR ").Length <= 64);
        Assert.Null(FuzzySearch.MatchExpression("a b"));
        Assert.All(FuzzySearch.MatchExpression("abc OR DELETE")!.Split(" OR "), part => Assert.Matches("^\"[a-z]{3}\"$", part));
        Assert.All(FuzzySearch.MatchExpression("abc\" OR 1=1 --")!.Split(" OR "), part => Assert.Matches("^\"[^\"]{3}\"$", part));
    }

    [Fact]
    public void IndexedTypoSearchAndStablePagination()
    {
        using var index = new LibraryIndex(DatabasePath());
        var root = Root();
        string[] names = ["Interstellar.2014.mkv", "Intersteller.2015.mkv", "The.Lord.of.the.Rings.mkv", "Unrelated.mkv"];
        index.Upsert([.. names.Select(n => Video(root, n))], Guid.NewGuid());

        var all = index.Search("interstellar");
        Assert.Equal(names[0], all[0].Entry.Name);
        Assert.Equal(2, all.Count);
        var next = index.Search("interstellar", limit: 1, offset: 1);
        Assert.Equal(all[1].Id, next[0].Id);
        Assert.Equal(2, index.Search("intersteller").Count);
        Assert.Equal(names[2], index.Search("rings lord")[0].Entry.Name);
    }

    [Fact]
    public void PaginationNeverRepeatsOrSkipsAcrossPages()
    {
        using var index = new LibraryIndex(DatabasePath());
        var root = Root();
        index.Upsert([.. Enumerable.Range(0, 1200).Select(i => Video(root, $"Catalogue.Movie.{i:D4}.mkv"))], Guid.NewGuid());
        foreach (var fuzzy in new[] { true, false })
        {
            var ids = new List<GlobalSearchResultId>();
            for (var offset = 0; ; offset += 500)
            {
                var page = index.Search("catalogue movie", 500, offset, fuzzy);
                ids.AddRange(page.Select(r => r.Id));
                if (page.Count < 500) break;
            }
            Assert.Equal(1200, ids.Count);
            Assert.Equal(1200, ids.Distinct().Count());
        }
    }

    [Fact]
    public void ExactModeRequiresTheWholeQueryAndFuzzyModeDoesNot()
    {
        using var index = new LibraryIndex(DatabasePath());
        var root = Root();
        index.Upsert([Video(root, "Interstellar.2014.mkv"), Video(root, "Amélie.2001.mkv")], Guid.NewGuid());
        Assert.Single(index.Search("intersteller"));
        Assert.Empty(index.Search("intersteller", fuzzy: false));
        Assert.Single(index.Search("interstellar", fuzzy: false));
        Assert.Single(index.Search("AMELIE", fuzzy: false));
        Assert.Empty(index.Search("---"));
        Assert.Equal(GlobalSearchErrorKind.QueryTooShort, Assert.Throws<GlobalSearchException>(() => index.Search("ab", fuzzy: false)).Kind);
    }

    [Fact]
    public void MetadataAliasesAndPortableSearchWithoutLegacyTrigram()
    {
        var path = DatabasePath();
        var root = Root();
        var media = Video(root, "Le.Fabuleux.Destin.2001.mkv", "film.mkv");
        using (var index = new LibraryIndex(path))
        {
            index.Upsert([media], Guid.NewGuid());
            var title = new EntertainmentTitle("movie|amelie|2001", "Amelie", "2001", EntertainmentKind.Movie,
                [new EntertainmentVersion(media, DateTimeOffset.UtcNow)])
            {
                Metadata = new EntertainmentMetadata { ProviderId = 194, Title = "Amélie", ReleaseDate = "2001-04-25", Language = "fr" },
            };
            Assert.Empty(index.Search("amelie", fuzzy: false));
            index.SaveSearchAliases([title]);
            Assert.Equal(media.Id, index.Search("amelie")[0].Id);
            Assert.Equal(media.Id, index.Search("ameli")[0].Id);
            Assert.Equal(media.Id, index.Search("fabuleu destn")[0].Id);
            Assert.Empty(index.Search("fabuleu destn", fuzzy: false));
            Assert.Empty(index.Search("---"));
        }
        // The legacy trigram table is not needed for search.
        Raw(path, "DROP TRIGGER videos_ai; DROP TRIGGER videos_au; DROP TRIGGER videos_ad; DROP TABLE video_fts;");
        using var reopened = new LibraryIndex(path);
        Assert.Equal(media.Id, reopened.Search("amelie", fuzzy: false)[0].Id);
    }

    [Fact]
    public void TriggersKeepDocumentsCurrentForInsertRenameAndDelete()
    {
        var path = DatabasePath();
        using var index = new LibraryIndex(path);
        var root = Root();
        var generation = Guid.NewGuid();
        index.Upsert([Video(root, "Alpha.Beta.mkv", "one.mkv")], generation);
        Assert.Single(index.Search("alpha beta"));

        // Same file, new name: the update trigger queues it again.
        index.Upsert([Video(root, "Gamma.Delta.mkv", "one.mkv")], generation);
        Assert.Empty(index.Search("alpha beta", fuzzy: false));
        Assert.Single(index.Search("gamma delta", fuzzy: false));

        // Removing the source deletes its documents and queue entries.
        index.SynchronizeSources([]);
        Assert.Empty(index.Search("gamma delta"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_documents"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_document_fts WHERE search_document_fts MATCH 'gam'"));
    }

    private static void CreateV3(string path, int videos)
    {
        var inserts = string.Join("\n", Enumerable.Range(0, videos).Select(i =>
            $"INSERT INTO videos VALUES('11111111-1111-1111-1111-111111111111','https://example.com/movies/','Movies','https://example.com/movies/m{i}.mkv','Movie {i} Interstellar.mkv','movie {i} interstellar.mkv','m{i}.mkv',NULL,1,1,'g',5);"));
        Raw(path, $"""
            CREATE TABLE videos (
              category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
              url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL,
              relative_path TEXT NOT NULL, artwork TEXT, size INTEGER, modified REAL,
              generation TEXT NOT NULL, first_discovered REAL NOT NULL DEFAULT 0,
              PRIMARY KEY(category, url));
            CREATE TABLE source_scans (category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
            CREATE TABLE metadata (cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
            CREATE TABLE playback (url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);
            CREATE TABLE folders(category TEXT NOT NULL, root TEXT NOT NULL, url TEXT NOT NULL, payload TEXT NOT NULL, checked REAL NOT NULL, PRIMARY KEY(category,url));
            CREATE TABLE index_state(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL);
            INSERT INTO index_state VALUES(1,7);
            {inserts}
            PRAGMA user_version=3;
            """);
    }

    [Fact]
    public void V3MigrationBacksUpFirstQueuesEveryVideoAndSearches()
    {
        var path = DatabasePath();
        CreateV3(path, 40);
        using (var index = new LibraryIndex(path))
        {
            Assert.Equal(4L, Scalar(path, "PRAGMA user_version"));
            Assert.Equal(40L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));
            Assert.Equal(7, index.Revision());
            Assert.Equal(40, index.Search("intersteller").Count);
        }
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));
        Assert.Equal(40L, Scalar(path, "SELECT COUNT(*) FROM search_documents"));
        var backup = path + ".v3-backup";
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(backup + ".tmp"));
        Assert.Equal(3L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal("ok", Scalar(backup, "PRAGMA quick_check"));
        Assert.Equal(40L, Scalar(backup, "SELECT COUNT(*) FROM videos"));
        Assert.Null(Scalar(backup, "SELECT 1 FROM sqlite_master WHERE name='search_documents'"));

        // Reopening neither re-queues the catalogue nor replaces the verified backup.
        var stamp = File.GetLastWriteTimeUtc(backup);
        using var reopened = new LibraryIndex(path);
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(backup));
    }

    [Fact]
    public void FailedMigrationRollsBackAndKeepsTheVerifiedBackup()
    {
        var path = DatabasePath();
        CreateV3(path, 5);
        // A foreign one-column table makes the queueing statement fail halfway through the migration.
        Raw(path, "CREATE TABLE search_dirty(only_one TEXT);");
        Assert.Throws<SqliteException>(() => new LibraryIndex(path));
        Assert.Equal(3L, Scalar(path, "PRAGMA user_version"));
        Assert.Null(Scalar(path, "SELECT 1 FROM sqlite_master WHERE name IN ('search_documents','search_document_fts')"));
        Assert.Equal(5L, Scalar(path, "SELECT COUNT(*) FROM videos"));
        Assert.Equal(3L, Scalar(path + ".v3-backup", "PRAGMA user_version"));
    }

    [Fact]
    public void CorruptBackupIsRecreatedAndNewerSchemaIsRejected()
    {
        var path = DatabasePath();
        CreateV3(path, 3);
        File.WriteAllText(path + ".v3-backup", "not a database");
        using (new LibraryIndex(path)) { }
        Assert.Equal(3L, Scalar(path + ".v3-backup", "PRAGMA user_version"));

        var future = DatabasePath();
        Raw(future, "CREATE TABLE future(x); PRAGMA user_version=5;");
        Assert.Throws<LibraryIndexException>(() => new LibraryIndex(future));
        Assert.Equal(5L, Scalar(future, "PRAGMA user_version"));
        Assert.False(File.Exists(future + ".v5-backup"));
    }

    /// Review finding: a weeks-old valid backup was kept instead of a fresh one.
    [Fact]
    public void EveryUpgradeTakesAFreshBackupAndKeepsThePreviousOne()
    {
        var path = DatabasePath();
        var backup = path + ".v3-backup";
        CreateV3(backup, 2);
        CreateV3(path, 9);
        using (new LibraryIndex(path)) { }
        Assert.Equal(9L, Scalar(backup, "SELECT COUNT(*) FROM videos"));
        Assert.Equal(3L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal("delete", Scalar(backup, "PRAGMA journal_mode"));
        Assert.True(LibraryIndex.IsValidBackup(backup, 3));
        Assert.Equal(2L, Scalar(backup + ".previous", "SELECT COUNT(*) FROM videos"));
        Assert.False(File.Exists(backup + ".tmp"));
    }

    [Fact]
    public void BackupVerificationOpensReadOnly()
    {
        var path = DatabasePath();
        CreateV3(path, 1);
        var before = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.True(LibraryIndex.IsValidBackup(path, 3));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.False(LibraryIndex.IsValidBackup(path, 4));
    }

    /// Review finding: no free-space check before the backup and migration.
    [Fact]
    public void UpgradeWithoutEnoughFreeSpaceChangesNothing()
    {
        var path = DatabasePath();
        CreateV3(path, 20);
        var required = LibraryIndex.RequiredUpgradeSpace(path);
        Assert.True(required >= 2 * new FileInfo(path).Length);
        var error = Assert.Throws<LibraryIndexException>(() => new LibraryIndex(path, true, _ => required - 1));
        Assert.Contains("Not enough free disk space", error.Message);
        Assert.Contains("Nothing was changed", error.Message);
        Assert.Equal(3L, Scalar(path, "PRAGMA user_version"));
        Assert.Null(Scalar(path, "SELECT 1 FROM sqlite_master WHERE name='search_documents'"));
        Assert.False(File.Exists(path + ".v3-backup"));
        Assert.False(File.Exists(path + ".v3-backup.tmp"));

        // Enough space, or an unknown amount (network drive): the upgrade runs.
        using (new LibraryIndex(path, true, _ => null)) { }
        Assert.Equal(4L, Scalar(path, "PRAGMA user_version"));
        // A current index needs no space check.
        using (new LibraryIndex(path, true, _ => 0)) { }
    }

    /// Review finding: after an upgrade every document was built twice (without, then with aliases).
    [Fact]
    public void FirstAliasSaveBuildsEachDocumentOnce()
    {
        var path = DatabasePath();
        CreateV3(path, 30);
        using var index = new LibraryIndex(path);
        Raw(path, """
            CREATE TABLE document_writes(kind TEXT);
            CREATE TRIGGER count_document_insert AFTER INSERT ON search_documents BEGIN INSERT INTO document_writes VALUES('insert'); END;
            CREATE TRIGGER count_document_update AFTER UPDATE ON search_documents BEGIN INSERT INTO document_writes VALUES('update'); END;
            """);
        var titles = index.Inventory().Select((version, offset) => new EntertainmentTitle($"movie|{offset}", $"Movie {offset}", null,
            EntertainmentKind.Movie, [version]) { Metadata = new EntertainmentMetadata { Title = $"Kosmonaut {offset}" } }).ToList();

        index.SaveSearchAliases(titles);
        Assert.Equal(30L, Scalar(path, "SELECT COUNT(*) FROM document_writes WHERE kind='insert'"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM document_writes WHERE kind='update'"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));
        Assert.Single(index.Search("kosmonaut 17", fuzzy: false));

        // Unchanged aliases cost no write; one changed title rebuilds only its document.
        index.SaveSearchAliases(titles);
        Assert.Equal(30L, Scalar(path, "SELECT COUNT(*) FROM document_writes"));
        titles[4] = titles[4] with { Metadata = new EntertainmentMetadata { Title = "Astronaut Four" } };
        index.SaveSearchAliases(titles);
        Assert.Equal(2L, Scalar(path, "SELECT COUNT(*) FROM document_writes WHERE kind='update'"));
        Assert.Single(index.Search("astronaut four", fuzzy: false));
    }

    /// Review finding: Search built queued documents without a cancellation token while holding the lock.
    [Fact]
    public void SearchDocumentBuildIsCancellableAndSafeAlongsideOtherCalls()
    {
        var path = DatabasePath();
        CreateV3(path, 1200);
        using var index = new LibraryIndex(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.Search("interstellar", cancellationToken: cancelled.Token));
        Assert.Equal(1200L, Scalar(path, "SELECT COUNT(*) FROM search_dirty"));

        // Other calls run between 500-row batches while a background build continues.
        using var stop = new CancellationTokenSource();
        var build = Task.Run(() =>
        {
            try
            {
                index.BuildSearchDocuments(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
        var url = new Uri("https://example.com/movies/m1.mkv");
        index.SavePlaybackPosition(url, 12, 100);
        Assert.Equal(12, index.PlaybackPosition(url));
        build.Wait(TimeSpan.FromSeconds(30));
        Assert.Equal(500, index.Search("intersteller").Count);
        Assert.Equal(1200L, Scalar(path, "SELECT COUNT(*) FROM search_documents"));
    }

    [Fact]
    public void LargeCatalogueFuzzySearchBenchmark()
    {
        if (Environment.GetEnvironmentVariable("MYRA_FUZZY_BENCHMARK") != "1") return;
        using var index = new LibraryIndex(DatabasePath());
        var root = Root();
        for (var batch = 0; batch < 200; batch++)
            index.Upsert([.. Enumerable.Range(0, 500).Select(offset =>
            {
                var number = batch * 500 + offset;
                return Video(root, number == 99999 ? "Interstellar.2014.mkv" : $"Catalogue.Movie.{number}.mkv");
            })], Guid.NewGuid());
        var build = System.Diagnostics.Stopwatch.StartNew();
        index.BuildSearchDocuments();
        var built = build.Elapsed;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = index.Search("intersteller");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myra-fuzzy-benchmark.txt"),
            $"100000 synthetic files; documents {built.TotalSeconds:F2}s; query {watch.Elapsed.TotalSeconds:F3}s");
        Assert.Equal("Interstellar.2014.mkv", results[0].Entry.Name);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
