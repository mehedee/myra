using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Myra.Core.Tests;

/// Personal records, corrections, and portable export/import (ported from DiscoveryTests.swift).
public class PersonalLibraryTests
{
    /// Shape written by the macOS app (Swift JSONEncoder, .sortedKeys, dates since 2001-01-01).
    private const string MacArchive = """
        {
          "appPreferences" : { "concurrentDownloads" : 6, "connectionsPerFile" : 8, "previousDownloadDirectory" : "/Users/me/Downloads/Myra",
            "retryCount" : 5, "speedLimit" : "2M", "splitCount" : 8, "theme" : "dark" },
          "personal" : {
            "collections" : [ { "id" : "2F1C7A3E-2B44-4C1F-9E55-7E1B2C3D4E5F", "name" : "Weekend", "titleIDs" : [ "movie|the matrix|1999" ] } ],
            "followed" : [ "series|show|" ],
            "history" : { "https://media.example/Movies/The.Matrix.1999.mkv" : { "duration" : 8160, "seconds" : 1200, "updated" : 781000000 } },
            "knownEpisodeIDs" : [ "show||1|1|" ],
            "matchCorrections" : { "https://media.example/Movies/x.mkv" : { "kind" : "movie", "providerID" : 603, "title" : "The Matrix", "year" : "1999" } },
            "preferences" : { "notifications" : true },
            "schemaVersion" : 1,
            "watched" : [ ],
            "watchlist" : [ "movie|the matrix|1999" ]
          },
          "playerState" : { "audioLanguage" : "en", "automaticSkipping" : true, "autoplay" : false,
            "markers" : { "series|show||||" : { "intro" : { "end" : 45, "start" : 0 } } }, "speed" : 1.25, "subtitleLanguage" : "off" },
          "schemaVersion" : 1,
          "sources" : [ { "id" : "6B3A1D2C-0F9E-4A8B-B7C6-D5E4F3A2B1C0", "name" : "Movies", "url" : "https://media.example/Movies/" } ]
        }
        """;

    [Fact]
    public void PersonalArchivePersistsAndRejectsMalformedSources()
    {
        using var fixture = new StoreFixture();
        Assert.True(fixture.Store.ToggleWatchlist("movie|matrix|1999"));
        Assert.True(fixture.Store.CreateCollection("Weekend"));
        var reopened = new EntertainmentStore(() => fixture.Index, fixture.PersonalPath, new InMemorySecretStore(), automaticEnrichment: false);
        Assert.Contains("movie|matrix|1999", reopened.Personal.Watchlist);
        Assert.Equal("Weekend", reopened.Personal.Collections.Single().Name);

        var data = reopened.ExportArchive([], null, null);
        var archive = EntertainmentStore.PreviewImport(data);
        archive.Sources = [new EntertainmentSource(Guid.NewGuid(), "Unsafe", "https://user:password@media.example/")];
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(JsonSerializer.SerializeToUtf8Bytes(archive, SwiftJson.Options)));
        archive.Sources = [new EntertainmentSource(Guid.NewGuid(), "Local", "file:///etc/")];
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(JsonSerializer.SerializeToUtf8Bytes(archive, SwiftJson.Options)));
        archive.Sources = [];
        archive.SchemaVersion = 999;
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(JsonSerializer.SerializeToUtf8Bytes(archive, SwiftJson.Options)));
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<InvalidImportException>(() => EntertainmentStore.PreviewImport(new byte[20_000_001]));
    }

    [Fact]
    public void ExportRemovesUrlCredentialsAndQueryTokensFromHistoryTitleIdsAndMarkers()
    {
        using var fixture = new StoreFixture();
        var url = new Uri("https://username:password@media.example/Movie.mkv?token=secret#part");
        var media = new GlobalSearchResult(Guid.NewGuid(), "Movies", new Uri("https://media.example/"),
            new DirectoryEntry("Movie.mkv", url, EntryKind.File), "Movie.mkv", null);
        fixture.Store.RecordPlayback(media, 10, 100);
        fixture.Store.ToggleWatchlist("file|" + url.AbsoluteUri);
        var player = new PlayerPersonalState();
        player.Markers["file|" + url.AbsoluteUri] = new PlayerSkipMarkers(new PlayerSkipRange(0, 30));
        var output = fixture.Store.ExportArchive(
            [new EntertainmentSource(Guid.NewGuid(), "Movies", "https://user:pw@media.example/?key=abc")], null, player);
        var text = Encoding.UTF8.GetString(output);
        Assert.DoesNotContain("password", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("key=abc", text);
        var imported = EntertainmentStore.PreviewImport(output);
        Assert.Contains("file|https://media.example/Movie.mkv", imported.Personal.Watchlist);
        Assert.True(imported.Personal.History.ContainsKey("https://media.example/Movie.mkv"));
        Assert.True(imported.PlayerState!.Markers.ContainsKey("file|https://media.example/Movie.mkv"));
        Assert.Equal("https://media.example/", imported.Sources.Single().Url);
    }

    [Fact]
    public void DuplicatePortableReferencesAreRejected()
    {
        using var fixture = new StoreFixture();
        fixture.Store.ToggleWatchlist("file|https://media.example/A.mkv?token=1");
        fixture.Store.ToggleWatchlist("file|https://media.example/A.mkv?token=2");
        Assert.Throws<InvalidImportException>(() => fixture.Store.ExportArchive([], null, null));
    }

    [Fact]
    public void CorruptPersonalFileIsPreserved()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "personal.json");
        File.WriteAllText(path, "not a valid json document");
        using var index = new LibraryIndex(Path.Combine(temp.Path, "index.sqlite"));
        var store = new EntertainmentStore(() => index, path, new InMemorySecretStore(), automaticEnrichment: false);
        Assert.False(store.CanWrite);
        Assert.False(store.ToggleWatched("movie"));
        Assert.Equal("not a valid json document", File.ReadAllText(path));
        Assert.NotNull(store.ErrorMessage);
        Assert.Throws<InvalidImportException>(() => store.ApplyArchive(new EntertainmentArchive(), replace: true));
        Assert.Equal("not a valid json document", File.ReadAllText(path));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void PersonalFileUsesTheMacOsFormat()
    {
        using var fixture = new StoreFixture();
        fixture.Store.CreateCollection("Weekend");
        var media = new GlobalSearchResult(Guid.NewGuid(), "M", new Uri("https://media.example/"),
            new DirectoryEntry("A.mkv", new Uri("https://media.example/A.mkv"), EntryKind.File), "A.mkv", null);
        fixture.Store.RecordPlayback(media, 5, 50, new DateTimeOffset(2001, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var json = JsonNode.Parse(File.ReadAllText(fixture.PersonalPath))!;
        Assert.Equal(1, (int)json["schemaVersion"]!);
        Assert.Equal(86400.0, (double)json["history"]!["https://media.example/A.mkv"]!["updated"]!);
        var id = (string)json["collections"]![0]!["id"]!;
        Assert.Equal(id.ToUpperInvariant(), id);
        foreach (var key in new[] { "watchlist", "watched", "collections", "followed", "knownEpisodeIDs", "matchCorrections", "preferences", "history" })
            Assert.NotNull(json[key]);
    }

    [Fact]
    public void MacOsArchiveImportsWithMergeAndKeepsLocalVolume()
    {
        using var fixture = new StoreFixture();
        fixture.Store.ToggleWatched("movie|local|2000");
        var preview = PersonalLibraryTransfer.Preview(Encoding.UTF8.GetBytes(MacArchive));
        Assert.Equal(1, preview.Sources);
        Assert.Equal(1, preview.MarkerConfigurations);
        Assert.Equal("/Users/me/Downloads/Myra", preview.PreviousDownloadDirectory);
        var archive = preview.Archive;
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(781000000),
            archive.Personal.History.Values.Single().Updated);
        Assert.Equal(603, archive.Personal.MatchCorrections.Values.Single().ProviderId);

        var store = AppStore.Load(Path.Combine(fixture.Folder, "Myra.json"));
        store.Settings.DownloadDirectory = "/home/me/Videos";
        var player = new PlayerPersonalState { Volume = 40 };
        var transfer = new PersonalLibraryTransfer(store, fixture.Store, () => player, state => player = state);
        var result = transfer.Import(archive, replace: false, backupDirectory: fixture.Folder);

        Assert.True(File.Exists(result.FullBackupPath));
        Assert.True(File.Exists(result.PersonalBackupPath));
        Assert.Contains("Previous download folder", result.Message);
        Assert.Contains("movie|local|2000", fixture.Store.Personal.Watched);
        Assert.Contains("movie|the matrix|1999", fixture.Store.Personal.Watchlist);
        Assert.False(fixture.Store.Personal.Preferences.Notifications); // merge keeps local preferences, as on macOS
        var category = store.Categories.Single();
        Assert.Equal(Guid.Parse("6B3A1D2C-0F9E-4A8B-B7C6-D5E4F3A2B1C0"), category.Id);
        Assert.Equal(AppThemeMode.Dark, store.Settings.Theme);
        Assert.Equal(6, store.Settings.ConcurrentDownloads);
        Assert.Equal("2M", store.Settings.SpeedLimit);
        Assert.Equal("/home/me/Videos", store.Settings.DownloadDirectory);
        Assert.Equal(1.25f, player.Speed);
        Assert.False(player.Autoplay);
        Assert.True(player.AutomaticSkipping);
        Assert.Equal(40, player.Volume);
        Assert.Equal(new PlayerSkipRange(0, 45), player.Markers["series|show||||"].Intro);
        Assert.True(File.Exists(Path.Combine(fixture.Folder, "Myra.json")));

        // Re-exporting produces a document the macOS decoder accepts: all required keys, enum and date formats.
        var exported = JsonNode.Parse(transfer.Export())!;
        Assert.Equal("dark", (string)exported["appPreferences"]!["theme"]!);
        Assert.Equal("movie", (string)exported["personal"]!["matchCorrections"]!["https://media.example/Movies/x.mkv"]!["kind"]!);
        Assert.Equal(781000000.0, (double)exported["personal"]!["history"]!["https://media.example/Movies/The.Matrix.1999.mkv"]!["updated"]!);
        Assert.Null(exported["playerState"]!["volume"]);
        foreach (var key in new[] { "speed", "audioLanguage", "subtitleLanguage", "autoplay", "automaticSkipping", "markers" })
            Assert.NotNull(exported["playerState"]![key]);
    }

    [Fact]
    public void ReplaceImportRemovesMissingSourcesAndRejectsInvalidPreferencesWithoutChanges()
    {
        using var fixture = new StoreFixture();
        fixture.Store.ToggleWatched("movie|local|2000");
        var store = AppStore.Load(Path.Combine(fixture.Folder, "Myra.json"));
        store.Categories.Add(new Category { Name = "Old", RootUrlString = "https://old.example/" });
        var player = new PlayerPersonalState();
        var transfer = new PersonalLibraryTransfer(store, fixture.Store, () => player, state => player = state);
        var archive = EntertainmentStore.PreviewImport(Encoding.UTF8.GetBytes(MacArchive));

        archive.AppPreferences = archive.AppPreferences! with { ConcurrentDownloads = 99 };
        Assert.Throws<InvalidImportException>(() => transfer.Import(archive, replace: true, backupDirectory: fixture.Folder));
        Assert.Contains("movie|local|2000", fixture.Store.Personal.Watched);
        Assert.Equal("Old", store.Categories.Single().Name);

        archive.AppPreferences = archive.AppPreferences with { ConcurrentDownloads = 4 };
        transfer.Import(archive, replace: true, backupDirectory: fixture.Folder);
        Assert.DoesNotContain("movie|local|2000", fixture.Store.Personal.Watched);
        Assert.Equal("Movies", store.Categories.Single().Name);
        Assert.Single(player.Markers);
    }

    [Fact]
    public void MergeUnionsSetsCollectionsAndKeepsNewerHistory()
    {
        var current = new EntertainmentPersonalData();
        var collection = new EntertainmentCollection { Name = "Mine", TitleIds = ["a"] };
        current.Collections.Add(collection);
        current.History["u"] = new EntertainmentPlaybackRecord(50, 100, DateTimeOffset.FromUnixTimeSeconds(200));
        current.History["v"] = new EntertainmentPlaybackRecord(10, 100, DateTimeOffset.FromUnixTimeSeconds(100));
        var incoming = new EntertainmentPersonalData();
        incoming.Collections.Add(new EntertainmentCollection { Id = collection.Id, Name = "Theirs", TitleIds = ["b"] });
        incoming.Collections.Add(new EntertainmentCollection { Name = "New" });
        incoming.History["u"] = new EntertainmentPlaybackRecord(5, 100, DateTimeOffset.FromUnixTimeSeconds(100));
        incoming.History["v"] = new EntertainmentPlaybackRecord(90, 100, DateTimeOffset.FromUnixTimeSeconds(300));
        incoming.Watchlist.Add("x");
        var merged = EntertainmentStore.Merge(current, incoming);
        Assert.Equal(2, merged.Collections.Count);
        Assert.Equal(new HashSet<string> { "a", "b" }, merged.Collections[0].TitleIds);
        Assert.Equal("Mine", merged.Collections[0].Name);
        Assert.Equal(50, merged.History["u"].Seconds);
        Assert.Equal(90, merged.History["v"].Seconds);
        Assert.Contains("x", merged.Watchlist);
        Assert.Empty(current.Watchlist);
    }

    [Fact]
    public async Task CorrectMatchMovesPersonalFlagsToTheNewTitle()
    {
        using var fixture = new StoreFixture();
        var version = EntertainmentTests.Version("Wrong.Name.2001.mkv");
        fixture.SetCatalogue([EntertainmentGrouping.Group([version]).Single()]);
        var oldId = fixture.Store.Catalogue.Single().Id;
        fixture.Store.ToggleWatchlist(oldId);
        fixture.Store.CreateCollection("Weekend");
        fixture.Store.ToggleCollection(oldId, fixture.Store.Personal.Collections[0].Id);

        Assert.False(await fixture.Store.CorrectMatchAsync([version.Id], "Right", "20x1", EntertainmentKind.Movie));
        Assert.False(await fixture.Store.CorrectMatchAsync([version.Id], "Right", null, EntertainmentKind.Movie, providerId: 0));
        Assert.True(await fixture.Store.CorrectMatchAsync([version.Id], "Right Name", "2002", EntertainmentKind.Movie, 42));
        var newId = "movie|right name|2002";
        Assert.Equal(newId, fixture.Store.Catalogue.Single().Id);
        Assert.Contains(newId, fixture.Store.Personal.Watchlist);
        Assert.DoesNotContain(oldId, fixture.Store.Personal.Watchlist);
        Assert.Contains(newId, fixture.Store.Personal.Collections[0].TitleIds);
        Assert.Equal(42, fixture.Store.Personal.MatchCorrections[version.Id].ProviderId);
    }

    [Fact]
    public void PlaybackUpdatesHistoryCatalogueAndWatchedState()
    {
        using var fixture = new StoreFixture();
        var version = EntertainmentTests.Version("Movie.2020.mkv");
        fixture.SetCatalogue([EntertainmentGrouping.Group([version]).Single()]);
        var media = fixture.Store.Catalogue.Single().Versions.Single().Media;
        fixture.Store.RecordPlayback(media, 300, 6000);
        fixture.Store.RecordPlayback(media, double.NaN, 6000);
        var title = fixture.Store.Catalogue.Single();
        Assert.Equal(300, title.ResumeVersion!.ProgressSeconds);
        Assert.Equal(300, fixture.Store.Personal.History[media.Entry.Url.AbsoluteUri].Seconds);
        var projection = HomeProjection.Prepare(fixture.Store.Catalogue, fixture.Store.Personal, new HomeCatalogueFilter());
        Assert.Single(HomeProjection.Shelves(projection, fixture.Store.Personal, new HashSet<string>())[0].Titles);

        fixture.Store.MarkPlaybackEnded(media);
        Assert.Contains(title.Id, fixture.Store.Personal.Watched);
        Assert.Null(fixture.Store.Catalogue.Single().ResumeVersion);
        Assert.Single(fixture.Store.Variants(media));
    }

    [Fact]
    public async Task ReloadRestoresHomeAndReportsNewFollowedEpisodes()
    {
        using var fixture = new StoreFixture();
        fixture.SetCatalogue([EntertainmentGrouping.Group([EntertainmentTests.Version("Show.S01E01.mkv", "TV")]).Single()]);
        var show = fixture.Store.Catalogue.Single();
        fixture.Store.ToggleFollow(show.Id);
        NewEpisodesEventArgs? reported = null;
        fixture.Store.NewEpisodesDetected += (_, args) => reported = args;
        fixture.Store.SetNotifications(true);

        await fixture.Store.ReloadAsync();
        Assert.Null(reported);
        fixture.Index.Upsert([EntertainmentTests.Version("Show.S01E02.mkv", "TV").Media], Guid.NewGuid());
        await fixture.Store.ReloadAsync();
        Assert.NotNull(reported);
        Assert.True(reported.Notify);
        Assert.Equal(["show||1|2|"], reported.EpisodeIds);
        Assert.Contains("show||1|2|", fixture.Store.NewEpisodeIds);
        Assert.True(fixture.Store.HasNewEpisode(fixture.Store.Catalogue.Single().Versions.Single(v => v.Episode == 2)));
        // Each episode is reported once.
        reported = null;
        await fixture.Store.ReloadAsync();
        Assert.Null(reported);
    }
}
