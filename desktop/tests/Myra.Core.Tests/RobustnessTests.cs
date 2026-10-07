using System.Text;
using Microsoft.Data.Sqlite;

namespace Myra.Core.Tests;

/// Failure handling: damaged files, interrupted backups, stuck refreshes and leftover temporary data.
public sealed class RobustnessTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string File(string name) => Path.Combine(_temp.Path, name);

    // ---------- Null entries in personal data ----------

    [Theory]
    [InlineData("""{"collections":[null]}""")]
    [InlineData("""{"collections":null}""")]
    [InlineData("""{"collections":[{"id":"2F1C7A3E-2B44-4C1F-9E55-7E1B2C3D4E5F","name":null,"titleIDs":[]}]}""")]
    [InlineData("""{"collections":[{"id":"2F1C7A3E-2B44-4C1F-9E55-7E1B2C3D4E5F","name":"A","titleIDs":[null]}]}""")]
    [InlineData("""{"watchlist":[null]}""")]
    [InlineData("""{"watched":null}""")]
    [InlineData("""{"history":null}""")]
    [InlineData("""{"history":{"https://a.example/x.mkv":null}}""")]
    [InlineData("""{"matchCorrections":{"https://a.example/x.mkv":{"title":null,"kind":"movie"}}}""")]
    [InlineData("""{"preferences":null}""")]
    public async Task PersonalFileWithNullEntriesIsPreservedAndTheStoreStaysUsable(string json)
    {
        var path = File("EntertainmentPersonal.json");
        System.IO.File.WriteAllText(path, json);
        using var index = new LibraryIndex(File("index.sqlite"));
        var store = new EntertainmentStore(() => index, path, new InMemorySecretStore(), automaticEnrichment: false);

        Assert.False(store.CanWrite);
        Assert.NotNull(store.ErrorMessage);
        Assert.False(store.ToggleWatched("movie|x|"));
        await store.ReloadAsync();
        Assert.Empty(store.Catalogue);
        Assert.Equal(json, System.IO.File.ReadAllText(path));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"personal":{},"sources":[null]}""")]
    [InlineData("""{"schemaVersion":1,"personal":{"collections":[null]},"sources":[]}""")]
    [InlineData("""{"schemaVersion":1,"personal":{"matchCorrections":{"https://a.example/x.mkv":{"title":null,"kind":"movie"}}},"sources":[]}""")]
    [InlineData("""{"schemaVersion":1,"personal":{"watchlist":[null]},"sources":[]}""")]
    [InlineData("""{"schemaVersion":1,"personal":{},"sources":[],"playerState":{"speed":1,"audioLanguage":null,"markers":{}}}""")]
    [InlineData("""{"schemaVersion":1,"personal":{},"sources":[],"playerState":{"speed":1,"markers":null}}""")]
    public void ImportWithNullEntriesIsRejectedCleanly(string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(data));
        Assert.Throws<InvalidImportException>(() => PersonalLibraryTransfer.Preview(data));
    }

    // ---------- Import backup ----------

    [Fact]
    public void ReplaceImportKeepsTheCompleteLocalStateInTheBackupFolder()
    {
        using var fixture = new StoreFixture();
        fixture.Store.ToggleWatched("movie|local|2000");
        var store = AppStore.Load(Path.Combine(fixture.Folder, "Myra.json"));
        store.Categories.Add(new Category { Name = "Private", RootUrlString = "https://user:pw@media.example/Private/?token=abc" });
        store.Save();
        var player = new PlayerPersonalState { Volume = 33 };
        player.Markers["series|show||||"] = new PlayerSkipMarkers(new PlayerSkipRange(0, 30));
        var transfer = new PersonalLibraryTransfer(store, fixture.Store, () => player, state => player = state);
        var archive = new EntertainmentArchive
        {
            Sources = [new EntertainmentSource(Guid.NewGuid(), "Public", "https://public.example/")],
        };

        var result = transfer.Import(archive, replace: true, backupDirectory: fixture.Folder);

        Assert.True(Directory.Exists(result.FullBackupPath));
        var savedStore = System.IO.File.ReadAllText(Path.Combine(result.FullBackupPath, "Myra.json"));
        Assert.Contains("token=abc", savedStore);
        Assert.Contains("user:pw@", savedStore);
        var savedPlayer = PlayerPersonalState.Load(Path.Combine(result.FullBackupPath, "PlayerPreferences.json"));
        Assert.Equal(33, savedPlayer.Volume);
        Assert.Single(savedPlayer.Markers);
        Assert.Contains("movie|local|2000", System.IO.File.ReadAllText(Path.Combine(result.FullBackupPath, "EntertainmentPersonal.json")));
        Assert.Equal("Public", store.Categories.Single().Name);
    }

    // ---------- Index backup before migration ----------

    private static void CreateV2Index(string path, int videos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        var rows = string.Join(",", Enumerable.Range(0, videos).Select(i =>
            $"('{Guid.Empty}','https://m.example/','M','https://m.example/{i}.mkv','{i}.mkv','{i}.mkv','{i}.mkv',NULL,1,1,'g',1)"));
        command.CommandText = $"""
            PRAGMA journal_mode=WAL;
            CREATE TABLE videos (category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
              url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL, relative_path TEXT NOT NULL,
              artwork TEXT, size INTEGER, modified REAL, generation TEXT NOT NULL, first_discovered REAL NOT NULL DEFAULT 0,
              PRIMARY KEY(category, url));
            CREATE TABLE source_scans (category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
            CREATE TABLE metadata (cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
            CREATE TABLE playback (url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);
            INSERT INTO videos VALUES {rows};
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM videos";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void InterruptedBackupIsNotTrustedAndIsRecreated()
    {
        var path = File("LibraryIndex.sqlite");
        CreateV2Index(path, 50);
        var backup = path + ".v2-backup";
        // A backup killed half-way: the final name exists but holds garbage. A stale temporary file is also left.
        System.IO.File.WriteAllBytes(backup, Encoding.ASCII.GetBytes("SQLite format 3\0 truncated"));
        System.IO.File.WriteAllText(backup + ".tmp", "partial");

        using (var index = new LibraryIndex(path)) Assert.Equal(50, index.VideoCount());

        Assert.True(LibraryIndex.IsValidBackup(backup, 2));
        Assert.Equal(50, Count(backup));
        Assert.False(System.IO.File.Exists(backup + ".tmp"));
    }

    [Fact]
    public void ValidExistingBackupIsKept()
    {
        var path = File("LibraryIndex.sqlite");
        CreateV2Index(path, 3);
        var backup = path + ".v2-backup";
        CreateV2Index(backup, 7);
        using (var index = new LibraryIndex(path)) Assert.Equal(3, index.VideoCount());
        Assert.Equal(7, Count(backup));
    }

    [Fact]
    public void ClosedIndexReportsALibraryIndexException()
    {
        var index = new LibraryIndex(File("LibraryIndex.sqlite"));
        index.Dispose();
        index.Dispose();
        Assert.Throws<LibraryIndexException>(() => index.PlaybackPosition(new Uri("https://m.example/a.mkv")));
        Assert.Throws<LibraryIndexException>(() => index.SavePlaybackPosition(new Uri("https://m.example/a.mkv"), 10, 100));
    }

    // ---------- Refresh controller ----------

    [Fact]
    public async Task FailingPreparationStillFinishesTheRefresh()
    {
        using var index = new LibraryIndex(File("LibraryIndex.sqlite"));
        var controller = new IndexRefreshController(() => index, new DirectoryService(), new IndexPolicies(), File("policies.json"));
        var outcome = await controller.RefreshAsync([], IndexRefreshMode.Due, manual: false,
            prepare: () => throw new InvalidOperationException("prepare failed"));
        Assert.False(controller.IsRefreshing);
        Assert.False(outcome.Cancelled);

        outcome = await controller.RefreshAsync([], IndexRefreshMode.Due, manual: false,
            prepare: () => Task.FromException(new IOException("async prepare failed")));
        Assert.False(controller.IsRefreshing);
    }

    [Fact]
    public async Task UnavailableIndexStillFinishesTheRefresh()
    {
        var controller = new IndexRefreshController(() => throw new LibraryIndexException("locked"), new DirectoryService(),
            new IndexPolicies(), File("policies.json"));
        var outcome = await controller.RefreshAsync([], IndexRefreshMode.Due, manual: true);
        Assert.False(controller.IsRefreshing);
        Assert.Contains("locked", outcome.Error);
        await controller.LoadFolderRowsAsync([]);
    }

    [Fact]
    public async Task EnrichmentWaitingForIndexingStopsWhenTheStoreIsStopped()
    {
        using var index = new LibraryIndex(File("LibraryIndex.sqlite"));
        var root = new GlobalSearchRoot(Guid.NewGuid(), "Movies", new Uri("https://m.example/"));
        var entry = new DirectoryEntry("Some.Movie.2020.mkv", new Uri("https://m.example/Some.Movie.2020.mkv"), EntryKind.File);
        index.Upsert([new GlobalSearchResult(root.Id, root.Name, root.Url, entry, entry.Name, null)], Guid.NewGuid());
        var secrets = new InMemorySecretStore();
        secrets.Save(SecretNames.TmdbReadToken, "token");
        var checks = 0;
        // Indexing "starts" right after enrichment begins and never finishes (a stuck refresh).
        var store = new EntertainmentStore(() => index, File("p.json"), secrets, automaticEnrichment: false,
            isIndexing: () => Interlocked.Increment(ref checks) > 1);
        await store.ReloadAsync();
        var enrichment = store.EnrichAsync();
        await Task.Delay(200);
        Assert.True(store.IsEnriching);
        Assert.True(store.StopBackgroundWork(TimeSpan.FromSeconds(5)));
        await enrichment.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(store.IsEnriching);
        await store.ReloadAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PeriodicPlaybackProgressIsKeptInMemoryUntilFlushed()
    {
        using var fixture = new StoreFixture();
        var media = new GlobalSearchResult(Guid.NewGuid(), "M", new Uri("https://m.example/"),
            new DirectoryEntry("A.mkv", new Uri("https://m.example/A.mkv"), EntryKind.File), "A.mkv", null);
        fixture.Store.RecordPlayback(media, 10, 100); // pause, stop or end: written at once
        Assert.Equal("10", ReadSeconds(fixture.PersonalPath));
        fixture.Store.RecordPlayback(media, 20, 100, persist: false);
        Assert.Equal(20, fixture.Store.Personal.History[media.Entry.Url.AbsoluteUri].Seconds);
        Assert.Equal("10", ReadSeconds(fixture.PersonalPath));
        Assert.True(fixture.Store.FlushPersonal());
        Assert.Equal("20", ReadSeconds(fixture.PersonalPath));
    }

    private static string ReadSeconds(string path) =>
        System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!["history"]!["https://m.example/A.mkv"]!["seconds"]!.ToString();

    // ---------- Small files ----------

    [Fact]
    public void CorruptPlayerPreferencesAreRenamedAside()
    {
        var path = File("PlayerPreferences.json");
        System.IO.File.WriteAllText(path, "{ \"Markers\": { broken");
        var state = PlayerPersonalState.Load(path);
        Assert.Empty(state.Markers);
        Assert.False(System.IO.File.Exists(path));
        var aside = Directory.GetFiles(_temp.Path, "PlayerPreferences.json.corrupt-*").Single();
        Assert.Equal("{ \"Markers\": { broken", System.IO.File.ReadAllText(aside));
        // A missing file is not an error and leaves nothing behind.
        Assert.Equal(1, PlayerPersonalState.Load(File("missing.json")).Speed);
    }

    [Fact]
    public void ClearingASecretWithoutASecretsFolderDoesNotFail()
    {
        var store = SecretStore.CreateDefault(File("Secrets"));
        store.Save(SecretNames.OmdbApiKey, "");
        Assert.Equal("", store.Read(SecretNames.OmdbApiKey));
        store.Save(SecretNames.OmdbApiKey, "value");
        Assert.Equal("value", store.Read(SecretNames.OmdbApiKey));
        store.Save(SecretNames.OmdbApiKey, " ");
        Assert.Equal("", store.Read(SecretNames.OmdbApiKey));
    }

    [Fact]
    public void AtomicWriteReplacesTheFileAndLeavesNoTemporaryFile()
    {
        var path = File("doc.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", System.IO.File.ReadAllText(path));
        Assert.False(System.IO.File.Exists(path + ".tmp"));
    }

    [Fact]
    public void CorruptStoreIsCopiedAsideAndStillSaves()
    {
        var path = File("Myra.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var store = AppStore.Load(path);
        Assert.NotNull(store.LoadError);
        Assert.Single(Directory.GetFiles(_temp.Path, "Myra.json.corrupt-*"));
        store.Save();
    }

    [Fact]
    public void StaleStagingFoldersAreRemovedAndRecentOnesKept()
    {
        var stale = Directory.CreateDirectory(File("Myra-staging-old"));
        System.IO.File.WriteAllText(Path.Combine(stale.FullName, "Index.sqlite"), "x");
        var old = DateTime.UtcNow.AddHours(-3);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(stale.FullName, "Index.sqlite"), old);
        Directory.SetLastWriteTimeUtc(stale.FullName, old);
        var fresh = Directory.CreateDirectory(File("Myra-staging-new"));
        System.IO.File.WriteAllText(Path.Combine(fresh.FullName, "Index.sqlite"), "x");
        var other = Directory.CreateDirectory(File("unrelated"));
        Directory.SetLastWriteTimeUtc(other.FullName, old);

        Assert.Equal(1, LibraryIndexer.CleanStaleStaging(_temp.Path));
        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(fresh.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }
}
