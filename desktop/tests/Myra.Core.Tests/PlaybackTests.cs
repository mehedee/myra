using Myra.Core;

namespace Myra.Core.Tests;

public class PlaybackTests
{
    private static readonly Uri Root = new("https://example.test/media/");

    private static List<DirectoryEntry> Entries(Uri parent, params string[] names) =>
        names.Select(n => new DirectoryEntry(n, new Uri(parent, n), EntryKind.File)).ToList();

    private static GlobalSearchResult Result(DirectoryEntry entry, string? path = null) =>
        new(Guid.NewGuid(), "Movies", Root, entry, path ?? entry.Name, null);

    [Fact]
    public void VariantGroupingDoesNotNavigateToSameMovie()
    {
        string[] names = ["Movie.2025.1080p.WEB-DL.mkv", "Movie.2025.2160p.BluRay.mkv", "Next.2026.1080p.mkv"];
        var entries = Entries(Root, names);
        var sequence = new PlaybackSequence(Result(entries[1]), entries, null);
        Assert.Equal(2, sequence.Groups.Count);
        Assert.Null(sequence.Previous(entries[1].Url));
        Assert.Equal(names[2], sequence.Next(entries[1].Url)?.Entry.Name);
        Assert.Equal(2, sequence.Neighbours(entries[2].Url, -1).Count);
    }

    [Fact]
    public void YearlessQualityVariantsMergeInFolderAndEpisodesStaySeparate()
    {
        string[] names = ["Movie.1080p.mkv", "Movie.720p.mkv", "Series.S01E01.1080p.mkv", "Series.S01E01.720p.mkv", "Series.S01E02.mkv"];
        var entries = Entries(Root, names);
        var sequence = new PlaybackSequence(Result(entries[0]), entries, null);
        Assert.Equal(3, sequence.Groups.Count);
        Assert.Equal(names[4], sequence.Neighbours(entries[3].Url, 1).FirstOrDefault()?.Entry.Name);
    }

    [Fact]
    public void SequenceUsesNaturalOrderAndOnlySafeSiblingVideos()
    {
        var parent = new Uri(Root, "Series/");
        var playing = Result(new DirectoryEntry("Episode2.mkv", new Uri(parent, "Episode2.mkv"), EntryKind.File), "Series/Episode2.mkv");
        var entries = Entries(parent, "Episode10.mkv", "Episode1.mkv", "Episode3.mkv", "Episode2.mkv", "poster.jpg");
        entries.Add(new DirectoryEntry("Episode4.mkv", new Uri(parent, "Other/Episode4.mkv"), EntryKind.File));
        entries.Add(new DirectoryEntry("outside.mkv", new Uri("https://other.example/outside.mkv"), EntryKind.File));
        entries.Add(new DirectoryEntry("folder.mkv", new Uri(parent, "folder.mkv"), EntryKind.Folder));

        var queue = new PlaybackSequence(playing, entries, new Uri(parent, "poster.jpg"));
        Assert.Equal(["Episode1.mkv", "Episode2.mkv", "Episode3.mkv", "Episode10.mkv"], queue.Videos.Select(v => v.Entry.Name));
        var next = queue.Next(playing.Entry.Url);
        Assert.NotNull(next);
        Assert.Equal("Episode3.mkv", next.Entry.Name);
        Assert.Equal(playing.CategoryId, next.CategoryId);
        Assert.Equal("Series/Episode3.mkv", next.RelativePath);
        Assert.NotNull(next.ArtworkUrl);
        Assert.Null(queue.Next(queue.Videos[^1].Entry.Url));
        Assert.Null(new PlaybackSequence(playing, [], null).Next(playing.Entry.Url));
    }

    [Fact]
    public void LocalFilesFormASequence()
    {
        var folder = new Uri("file:///C:/Downloads/Myra/Show/");
        var entries = Entries(folder, "E02.mkv", "E01.mkv");
        var playing = new GlobalSearchResult(Guid.Empty, "Offline", folder, entries[1], "E01.mkv", null);
        Assert.Equal("E02.mkv", new PlaybackSequence(playing, entries, null).Next(entries[1].Url)?.Entry.Name);
    }

    [Fact]
    public void CompletionIsOncePerLoadAndPauseIsNotCompletion()
    {
        var completion = new PlaybackCompletionState();
        Assert.False(completion.Observe(PlayerState.Stopped));
        Assert.False(completion.Observe(PlayerState.Opening));
        Assert.False(completion.Observe(PlayerState.Playing));
        Assert.False(completion.Observe(PlayerState.Paused));
        Assert.True(completion.Observe(PlayerState.Ended));
        Assert.False(completion.Observe(PlayerState.Ended));
        Assert.False(completion.Observe(PlayerState.Stopped));
        completion.Reset();
        Assert.False(completion.Observe(PlayerState.Stopped));
        Assert.False(completion.Observe(PlayerState.Playing));
        Assert.True(completion.Observe(PlayerState.Stopped));
    }

    [Fact]
    public void EnglishDefaultsAndFallbacks()
    {
        PlayerTrack[] tracks = [new(1, "French", "fra"), new(2, "English commentary", "eng"), new(3, "Main", "en-GB")];
        Assert.Equal(3, EnglishTrackPreference.Preferred(tracks, subtitles: false));
        Assert.Null(EnglishTrackPreference.Preferred([tracks[0]], subtitles: false));
        PlayerTrack[] subtitles = [new(-1, "Disabled", null), new(4, "English forced", "eng"), new(5, "English", null)];
        Assert.Equal(5, EnglishTrackPreference.Preferred(subtitles, subtitles: true));
        Assert.Null(EnglishTrackPreference.Preferred([], subtitles: true));
    }

    [Fact]
    public void OtherLanguagePreferences()
    {
        PlayerTrack[] tracks = [new(1, "Track 1 - [Bangla]", null), new(2, "Hindi", "hin")];
        Assert.Equal(1, PlayerLanguagePreference.Preferred(tracks, "bn", false));
        Assert.Equal(2, PlayerLanguagePreference.Preferred(tracks, "hi", false));
        Assert.Equal(-1, PlayerLanguagePreference.Preferred(tracks, "off", true));
        Assert.Null(PlayerLanguagePreference.Preferred(tracks, "default", true));
    }

    [Fact]
    public void Timestamps()
    {
        Assert.Equal("0:00", PlayerTime.Format(0));
        Assert.Equal("1:01:01", PlayerTime.Format(3661));
        Assert.Equal("0:00", PlayerTime.Format(-2));
        Assert.Equal("0:00", PlayerTime.Format(double.NaN));
    }

    [Fact]
    public void SkipRangeRejectsInvalidNumbersAndDurations()
    {
        Assert.True(new PlayerSkipRange(0, 60).Valid(90));
        foreach (var range in new PlayerSkipRange[] { new(-1, 10), new(10, 10), new(0, 100), new(double.NaN, 10) })
            Assert.False(range.Valid(90));
        Assert.False(new PlayerSkipRange(0, 60).Contains(60));
    }

    [Fact]
    public void PlayerPersonalStateRoundTrips()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "player.json");
        new PlayerPersonalState { Speed = 1.5f, AudioLanguage = "hi", Autoplay = false }.Save(path);
        var loaded = PlayerPersonalState.Load(path);
        Assert.Equal(1.5f, loaded.Speed);
        Assert.Equal("hi", loaded.AudioLanguage);
        Assert.False(loaded.Autoplay);
        Assert.Equal("en", PlayerPersonalState.Load(Path.Combine(temp.Path, "missing.json")).AudioLanguage);
    }
}
