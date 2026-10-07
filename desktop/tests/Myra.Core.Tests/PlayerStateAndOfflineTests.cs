namespace Myra.Core.Tests;

/// Playback preferences, intro/outro markers, version choices, Offline Library and secret storage.
public sealed class PlayerStateAndOfflineTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static GlobalSearchResult Media(string name, string folder = "Show/")
    {
        var root = new Uri("https://example.test/media/");
        return new GlobalSearchResult(Guid.NewGuid(), "TV", root, new DirectoryEntry(name, new Uri(root, folder + name), EntryKind.File), folder + name, null);
    }

    [Fact]
    public void PlayerStateRoundTripsMarkersAndKeepsOldFilesReadable()
    {
        var path = Path.Combine(_temp.Path, "PlayerPreferences.json");
        File.WriteAllText(path, """{"Speed":1.5,"AudioLanguage":"fr","SubtitleLanguage":"off","Autoplay":false,"Volume":70}""");
        var legacy = PlayerPersonalState.Load(path);
        Assert.Equal((1.5f, "fr", 70), (legacy.Speed, legacy.AudioLanguage, legacy.Volume));
        Assert.Empty(legacy.Markers);
        Assert.False(legacy.AutomaticSkipping);

        legacy.AutomaticSkipping = true;
        legacy.Markers["series|show||||"] = new PlayerSkipMarkers(new PlayerSkipRange(0, 45));
        legacy.Save(path);
        var reloaded = PlayerPersonalState.Load(path);
        Assert.True(reloaded.AutomaticSkipping);
        Assert.Equal(new PlayerSkipRange(0, 45), reloaded.Markers["series|show||||"].Intro);
        Assert.Null(reloaded.Markers["series|show||||"].Outro);
    }

    [Fact]
    public void EpisodeMarkersOverrideSeriesMarkersAndAreValidatedAgainstDuration()
    {
        var state = new PlayerPersonalState();
        var episode1 = Media("Show.S01E01.1080p.mkv");
        var episode2 = Media("Show.S01E02.mkv");
        Assert.Equal("series|show||||", PlayerMarkers.SeriesKey(episode1));
        Assert.Equal("show||1|1|", PlayerMarkers.EpisodeKey(episode1));

        Assert.False(PlayerMarkers.Set(state, episode1, new PlayerSkipMarkers(new PlayerSkipRange(0, 60)), series: true, duration: 0));
        Assert.False(PlayerMarkers.Set(state, episode1, new PlayerSkipMarkers(Outro: new PlayerSkipRange(1700, 1900)), series: true, duration: 1800));
        Assert.Empty(state.Markers);
        Assert.True(PlayerMarkers.Set(state, episode1, new PlayerSkipMarkers(new PlayerSkipRange(0, 60), new PlayerSkipRange(1700, 1800)), series: true, duration: 1800));
        Assert.Equal(new PlayerSkipRange(0, 60), PlayerMarkers.Current(state, episode2).Intro);

        Assert.True(PlayerMarkers.Set(state, episode1, new PlayerSkipMarkers(new PlayerSkipRange(10, 90)), series: false, duration: 1800));
        Assert.Equal(new PlayerSkipRange(10, 90), PlayerMarkers.Current(state, episode1).Intro);
        Assert.Null(PlayerMarkers.Current(state, episode1).Outro);
        Assert.Equal(new PlayerSkipRange(0, 60), PlayerMarkers.Current(state, episode2).Intro);

        PlayerMarkers.Clear(state, episode1, series: false);
        Assert.Equal(new PlayerSkipRange(0, 60), PlayerMarkers.Current(state, episode1).Intro);
        Assert.Equal(new PlayerSkipMarkers(new PlayerSkipRange(0, 60), new PlayerSkipRange(1740, 1800)),
            PlayerMarkers.EditorDefaults(new PlayerSkipMarkers(), 1800));
    }

    [Fact]
    public void AutomaticSkipOnlyAppliesWhenEnabledSeekableAndInsideAValidRange()
    {
        var state = new PlayerPersonalState();
        var media = Media("Show.S01E01.mkv");
        PlayerMarkers.Set(state, media, new PlayerSkipMarkers(new PlayerSkipRange(5, 60), new PlayerSkipRange(1700, 1800)), series: true, duration: 1800);
        Assert.Equal("Skip Intro", PlayerMarkers.ActiveSkip(PlayerMarkers.Current(state, media), 30, 1800)?.Label);
        Assert.Equal("Skip Outro", PlayerMarkers.ActiveSkip(PlayerMarkers.Current(state, media), 1750, 1800)?.Label);
        Assert.Null(PlayerMarkers.ActiveSkip(PlayerMarkers.Current(state, media), 60, 1800));
        Assert.Null(PlayerMarkers.AutomaticSkipTarget(state, media, 30, 1800, seekable: true));
        state.AutomaticSkipping = true;
        Assert.Equal(60, PlayerMarkers.AutomaticSkipTarget(state, media, 30, 1800, seekable: true));
        Assert.Null(PlayerMarkers.AutomaticSkipTarget(state, media, 30, 1800, seekable: false));
        Assert.Null(PlayerMarkers.AutomaticSkipTarget(state, media, 30, 50, seekable: true));
        Assert.Null(PlayerMarkers.AutomaticSkipTarget(state, media, 100, 1800, seekable: true));
    }

    [Fact]
    public void VersionChoicesPreferLastPlayedAndNavigationSkipsEncodes()
    {
        var root = new Uri("https://example.test/media/");
        string[] names = ["Show.S01E01.1080p.mkv", "Show.S01E01.720p.mkv", "Show.S01E02.mkv"];
        var entries = names.Select(n => new DirectoryEntry(n, new Uri(root, n), EntryKind.File)).ToList();
        var playing = new GlobalSearchResult(Guid.NewGuid(), "TV", root, entries[0], names[0], null);
        var versions = PlaybackChoices.FromListing(playing, entries, null);
        Assert.Equal(2, versions.Count);
        Assert.Null(PlaybackChoices.For(versions.Take(1).ToList(), playing));
        var choices = PlaybackChoices.For(versions, playing)!;
        Assert.Equal(entries[0].Url, choices.PreferredUrl);
        var played = versions.Select(v => v.Media.Entry.Url == entries[1].Url ? v with { LastPlayed = DateTimeOffset.Now } : v).ToList();
        Assert.Equal(entries[1].Url, PlaybackChoices.For(played, playing)!.PreferredUrl);

        var sequence = new PlaybackSequence(playing, entries, null);
        Assert.Equal(2, sequence.CurrentVersions(playing).Count);
        Assert.False(sequence.HasPrevious(entries[0].Url));
        Assert.True(sequence.HasNext(entries[1].Url));
        Assert.Equal(names[2], sequence.Next(entries[1].Url)?.Entry.Name);
    }

    private DownloadItem Completed(string relative, TransferStatus status = TransferStatus.Completed, bool create = true)
    {
        var path = Path.Combine(_temp.Path, "Downloads", relative.Replace('/', Path.DirectorySeparatorChar));
        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "video");
        }
        return new DownloadItem { RelativePath = relative, DestinationPath = path, Status = status, CompletedBytes = 5 };
    }

    [Fact]
    public void OfflineLibraryGroupsCompletedDownloadsAndKeepsMissingFilesVisible()
    {
        var items = new List<DownloadItem>
        {
            Completed("Movies/Movie.2020.1080p.mkv"),
            Completed("Movies/Movie.2020.720p.mkv", create: false),
            Completed("Gone.2019.mkv", create: false),
            Completed("Show/Show.S01E01.mkv"),
            Completed("Partial.2021.mkv", TransferStatus.Active),
            Completed("Notes.txt"),
        };
        var personal = new EntertainmentPersonalData();
        var titles = OfflineLibrary.Titles(items, personal);
        Assert.Equal(3, titles.Count);
        var movie = titles.Single(t => t.Title.Name == "Movie");
        Assert.Single(movie.Available);
        Assert.Equal(1, movie.Missing);
        Assert.Equal("1 available version(s) • 1 missing", movie.Summary);
        Assert.Equal(OfflineStartKind.Choose, OfflineLibrary.Start(movie).Kind);
        var gone = titles.Single(t => t.Title.Name == "Gone");
        Assert.True(gone.IsUnavailable);
        Assert.Equal(OfflineStartKind.Unavailable, OfflineLibrary.Start(gone).Kind);
        var show = titles.Single(t => t.Title.Kind == EntertainmentKind.Series);
        var start = OfflineLibrary.Start(show);
        Assert.Equal(OfflineStartKind.Play, start.Kind);
        Assert.True(File.Exists(start.Path));
        Assert.Equal(OfflineLibrary.LocalSourceId, show.Title.Versions[0].Media.CategoryId);

        Assert.Equal(2, OfflineLibrary.Filter(titles, "", hideMissing: true).Count);
        Assert.Single(OfflineLibrary.Filter(titles, "show", hideMissing: false));
        Assert.Single(OfflineLibrary.Filter(titles, "Movies/", hideMissing: false));
        // Listing never touches the files.
        Assert.Equal("video", File.ReadAllText(start.Path!));
    }

    [Fact]
    public void OfflineProgressComesFromPersonalHistory()
    {
        var item = Completed("Film.2020.mkv");
        var personal = new EntertainmentPersonalData();
        personal.History[new Uri(item.DestinationPath).AbsoluteUri] = new EntertainmentPlaybackRecord(120, 600, DateTimeOffset.Now);
        var title = OfflineLibrary.Titles([item], personal).Single();
        Assert.Equal(120, title.Title.Versions[0].ProgressSeconds);
        Assert.False(OfflineLibrary.IsPlayableLocalFile(_temp.Path));
    }

    [Fact]
    public void FileSecretStoreRoundTripsWithPrivatePermissionsAndDeletesEmptyValues()
    {
        var directory = Path.Combine(_temp.Path, "Secrets");
        ISecretStore store = OperatingSystem.IsWindows() ? SecretStore.CreateDefault(directory) : new FileSecretStore(directory);
        Assert.Equal("", store.Read(SecretNames.TmdbReadToken));
        store.Save(SecretNames.TmdbReadToken, "token-value");
        Assert.Equal("token-value", store.Read(SecretNames.TmdbReadToken));
        store.SaveSubtitleCredentials(new SubtitleCredentials(" key ", " user ", " pass "));
        Assert.Equal(new SubtitleCredentials("key", "user", " pass "), store.ReadSubtitleCredentials());
        if (!OperatingSystem.IsWindows())
        {
            var file = Directory.GetFiles(directory).First();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
        else
        {
            // DPAPI output is not plaintext.
            Assert.DoesNotContain("token-value", File.ReadAllText(Directory.GetFiles(directory, "tmdb*").Single()));
        }
        store.Save(SecretNames.TmdbReadToken, "  ");
        Assert.Equal("", store.Read(SecretNames.TmdbReadToken));
        Assert.Throws<ArgumentException>(() => store.Read("../escape"));
    }

    [Fact]
    public void SecretsNeverEnterTheAppStore()
    {
        var path = Path.Combine(_temp.Path, "Myra.json");
        var store = AppStore.Load(path);
        store.Save();
        var secrets = new FileSecretStore(Path.Combine(_temp.Path, "Secrets"));
        secrets.Save(SecretNames.OmdbApiKey, "omdb-secret");
        store.Save();
        Assert.DoesNotContain("omdb-secret", File.ReadAllText(path));
    }
}
