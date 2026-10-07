using System.Text;
using System.Text.Json;

namespace Myra.Core.Tests;

/// Free-first subtitles (nearby files, remembered cache entries, web search) and icon naming.
public sealed class SubtitleFreeFirstTests : IDisposable
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";
    private readonly TemporaryDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string name, string text = Srt)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void NearbyFindsMatchingSubtitleFilesOnly()
    {
        var media = Write("Show.S01E02.mkv", "video");
        Write("Show.S01E02.srt");
        Write("Show.S01E02.en.vtt");
        Write("show.s01e02.ASS");
        Write("Show.S01E03.srt");
        Write("Show.S01E02.txt");
        Write("Show.S01E02.empty.srt", "");
        var found = SubtitleLocal.Nearby(new Uri(media)).Select(Path.GetFileName).ToList();
        Assert.Equal(["show.s01e02.ASS", "Show.S01E02.en.vtt", "Show.S01E02.srt"], found);
    }

    [Fact]
    public void NearbyIgnoresRemoteMediaOversizedAndLinkedFiles()
    {
        Assert.Empty(SubtitleLocal.Nearby(new Uri("http://127.0.0.1:8765/Show.S01E02.mkv")));
        var media = Write("Movie.mkv", "video");
        Write("Movie.big.srt", new string('x', SubtitleCache.MaximumFileBytes + 1));
        var target = Write("target.srt");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_temp.Path, "Movie.link.srt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Assert.Empty(SubtitleLocal.Nearby(new Uri(media)));
    }

    [Fact]
    public void ManualChoiceNeedsSupportedExtensionAndBoundedSize()
    {
        Assert.True(SubtitleLocal.IsUsable(Write("a.srt")));
        Assert.True(SubtitleLocal.IsUsable(Write("b.VTT")));
        Assert.False(SubtitleLocal.IsUsable(Write("c.txt")));
        Assert.False(SubtitleLocal.IsUsable(Write("d.srt", "")));
        Assert.False(SubtitleLocal.IsUsable(Path.Combine(_temp.Path, "missing.srt")));
    }

    [Fact]
    public void WebSearchUrlIsHttpsOpenSubtitlesOrgWithEscapedTerms()
    {
        var url = SubtitleLocal.WebSearchUrl("Some Show & Co", "2020", "1", "2");
        Assert.Equal("https", url.Scheme);
        Assert.Equal("www.opensubtitles.org", url.Host);
        Assert.Equal("/en/search2", url.AbsolutePath);
        Assert.Equal("MovieName=Some%20Show%20%26%20Co%202020%20S1%20E2", url.Query.TrimStart('?'));
        Assert.Equal("MovieName=Movie", SubtitleLocal.WebSearchUrl(" Movie ").Query.TrimStart('?'));
    }

    [Fact]
    public void RememberedSubtitlesAreOfferedAgainForTheSameTitleOnly()
    {
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "cache"));
        var path = cache.Store(Encoding.UTF8.GetBytes(Srt), 24);
        var identity = MediaIdentity.Parse("Series.S01E02.1080p.mkv");
        cache.Remember(24, identity);
        cache.Remember(24, identity);
        Assert.Equal([path], cache.Cached(identity));
        Assert.Empty(cache.Cached(MediaIdentity.Parse("Series.S01E03.1080p.mkv")));
        Assert.Equal([path], new SubtitleCache(cache.DirectoryPath).Cached(identity));
    }

    [Fact]
    public void RememberSkipsUncachedFilesKeepsTwentyAndSurvivesDamage()
    {
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "cache"));
        var identity = new MediaIdentity("Title");
        cache.Remember(99, identity);
        Assert.Empty(cache.Cached(identity));
        for (var id = 1; id <= 22; id++)
        {
            cache.Store(Encoding.UTF8.GetBytes(Srt), id);
            cache.Remember(id, identity);
        }
        Assert.Equal(20, cache.Cached(identity).Count);
        Assert.DoesNotContain(cache.Cached(identity), p => p.EndsWith("MyraSub-1.srt"));
        var manifestPath = Path.Combine(cache.DirectoryPath, "title-cache.json");
        File.WriteAllText(manifestPath, "{not json");
        Assert.Empty(cache.Cached(identity));
        cache.Remember(22, identity);
        Assert.Single(cache.Cached(identity));
        var manifest = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(manifestPath));
        Assert.Equal([22], manifest![identity.CacheKey]);
    }

    /// Review finding: the title manifest was reset silently once it passed 2 MB.
    [Fact]
    public void TitleManifestDropsExpiredFilesAndKeepsTheNewestTitles()
    {
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "cache"));
        var expired = cache.Store(Encoding.UTF8.GetBytes(Srt), 2);
        cache.Remember(2, new MediaIdentity("Old Title"));
        System.IO.File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-40));
        cache.Store(Encoding.UTF8.GetBytes(Srt), 1);
        var first = new MediaIdentity("Title 0");
        for (var i = 0; i < SubtitleCache.MaximumTitles + 5; i++)
        {
            cache.Remember(1, new MediaIdentity($"Title {i}"));
            if (i == 10) cache.Remember(1, first); // Remembered again: it moves to the newest end.
        }

        var manifestPath = Path.Combine(cache.DirectoryPath, "title-cache.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(manifestPath))!;
        Assert.Equal(SubtitleCache.MaximumTitles, manifest.Count);
        Assert.False(manifest.ContainsKey(new MediaIdentity("Old Title").CacheKey));
        Assert.Single(cache.Cached(first));
        Assert.Empty(cache.Cached(new MediaIdentity("Title 1")));
        Assert.Single(cache.Cached(new MediaIdentity($"Title {SubtitleCache.MaximumTitles + 4}")));
        Assert.True(new FileInfo(manifestPath).Length < 512 * 1024);
    }

    [Fact]
    public void ManifestIsNotPrunedAsAnOwnedSubtitle()
    {
        var cache = new SubtitleCache(Path.Combine(_temp.Path, "cache"));
        cache.Store(Encoding.UTF8.GetBytes(Srt), 1);
        cache.Remember(1, new MediaIdentity("T"));
        cache.Store(Encoding.UTF8.GetBytes(Srt), 2);
        Assert.True(File.Exists(Path.Combine(cache.DirectoryPath, "title-cache.json")));
    }

    [Theory]
    [InlineData("Cinema", "Dark", false, "cinema-dark")]
    [InlineData("orbit", "Automatic", false, "orbit-light")]
    [InlineData("orbit", "Automatic", true, "orbit-dark")]
    [InlineData("Minimal", "Glass", true, "minimal-glass")]
    [InlineData("nonsense", "", true, "signature-dark")]
    [InlineData(null, "42", false, "signature-light")]
    public void IconNamesResolveWithSafeFallbacks(string? family, string? variant, bool dark, string expected) =>
        Assert.Equal(expected, MyraIcons.AssetName(MyraIcons.ParseFamily(family), MyraIcons.ParseVariant(variant), dark));

    [Fact]
    public void IconChoiceIsStoredInSettingsJson()
    {
        var path = Path.Combine(_temp.Path, "Myra.json");
        var store = AppStore.Load(path);
        Assert.Equal("Signature", store.Settings.IconFamily);
        store.Settings.IconFamily = "Orbit";
        store.Settings.IconVariant = "Glass";
        store.Save();
        var reloaded = AppStore.Load(path);
        Assert.Equal("Orbit", reloaded.Settings.IconFamily);
        Assert.Equal("Glass", reloaded.Settings.IconVariant);
    }
}
