using Myra.Core;

namespace Myra.Core.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Myra-tests-" + Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class DownloadTests
{
    [Fact]
    public void ThemeCycleReturnsToSystem()
    {
        Assert.Equal(AppThemeMode.Light, AppThemeMode.System.Next());
        Assert.Equal(AppThemeMode.Dark, AppThemeMode.Light.Next());
        Assert.Equal(AppThemeMode.System, AppThemeMode.Dark.Next());
    }

    [Fact]
    public void DestinationStaysUnderRoot()
    {
        using var temp = new TemporaryDirectory();
        var safe = DestinationSafety.Destination(temp.Path, "Movies/Film.mkv");
        Assert.Equal(Path.Combine(temp.Path, "Movies", "Film.mkv"), safe);
        Assert.Throws<DirectoryException>(() => DestinationSafety.Destination(temp.Path, "../escape.mkv"));
        Assert.Throws<DirectoryException>(() => DestinationSafety.Destination(temp.Path, ""));
        // A sibling folder that shares the root's prefix is still outside it.
        Assert.Throws<DirectoryException>(() => DestinationSafety.Destination(temp.Path, "../" + Path.GetFileName(temp.Path) + "-other/x.mkv"));
    }

    [Theory]
    [InlineData("A/B: C.mkv", "A_B_ C.mkv")]
    [InlineData("..", "Untitled")]
    [InlineData("   ", "Untitled")]
    [InlineData("What? <Really> \"yes\"|*.mkv", "What_ _Really_ _yes___.mkv")]
    [InlineData("Back\\slash.mkv", "Back_slash.mkv")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1 .mkv", "_Com1 .mkv")]
    [InlineData("Console.mkv", "Console.mkv")]
    public void FilenameSanitizationIsSafeOnWindows(string input, string expected)
    {
        Assert.Equal(expected, DestinationSafety.Sanitize(input));
    }

    [Fact]
    public void SanitizationCapsLength()
    {
        var name = new string('a', 300) + ".mkv";
        Assert.Equal(240, DestinationSafety.Sanitize(name).Length);
    }

    [Fact]
    public void GlobalDownloadsAreGroupedByCategoryAndKeepRelativePaths()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstRoot = new Uri("http://media.local/first/");
        var secondRoot = new Uri("http://media.local/second/");
        GlobalSearchResult[] results =
        [
            new(secondId, "Shows", secondRoot, new DirectoryEntry("Pilot.mp4", new Uri(secondRoot, "Season%201/Pilot.mp4"), EntryKind.File), "Season 1/Pilot.mp4", null),
            new(firstId, "Movies: HD", firstRoot, new DirectoryEntry("Film.mkv", new Uri(firstRoot, "2026/Film.mkv"), EntryKind.File), "2026/Film.mkv", null),
        ];

        var groups = GlobalDownloadManifestBuilder.Groups(results);

        Assert.Equal(["Movies: HD", "Shows"], groups.Select(g => g.CategoryName));
        Assert.Equal(["Movies_ HD/2026/Film.mkv"], groups[0].Manifest.Select(m => m.RelativePath));
        Assert.Equal(["Shows/Season 1/Pilot.mp4"], groups[1].Manifest.Select(m => m.RelativePath));
    }

    [Fact]
    public async Task PlannerExpandsFoldersRecursivelyAndDeduplicatesNestedSelections()
    {
        var root = new Uri("http://media.local/shows/");
        var show = new Uri(root, "Show/");
        var season = new Uri(show, "Season%201/");
        var fixture = new ListingFixture(new()
        {
            [show.AbsoluteUri] = new(show, [new("Season 1", season, EntryKind.Folder), new("Trailer.mp4", new Uri(show, "Trailer.mp4"), EntryKind.File, 10)], null),
            [season.AbsoluteUri] = new(season, [new("E01.mkv", new Uri(season, "E01.mkv"), EntryKind.File, 100), new("E02.mkv", new Uri(season, "E02.mkv"), EntryKind.File, 200)], null),
        });
        var progress = new List<ScanProgress>();
        var manifest = await new DownloadPlanner(fixture.Load).PrepareAsync(
        [
            new DirectoryEntry("Show", show, EntryKind.Folder),
            new DirectoryEntry("Season 1", season, EntryKind.Folder),
            new DirectoryEntry("Single.mkv", new Uri(root, "Single.mkv"), EntryKind.File, 5),
        ], root, progress.Add);

        Assert.Equal(
            new[] { "Single.mkv", "Show/Trailer.mp4", "Show/Season 1/E01.mkv", "Show/Season 1/E02.mkv" }.Order(),
            manifest.Select(m => m.RelativePath).Order());
        Assert.Equal(315, progress[^1].KnownBytes);
        Assert.Equal(1, fixture.CallCount(season));
    }

    [Fact]
    public void StorePersistsCategoriesDownloadsAndSettings()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "Myra.json");
        var store = AppStore.Load(path);
        var category = new Category { Name = "Persistent Movies", RootUrlString = "http://media.local/movies/" };
        store.Categories.Add(category);
        var batch = new DownloadBatch { Title = "Film", Status = TransferStatus.Paused, TotalBytes = 10 };
        store.Batches.Add(batch);
        store.Items.Add(new DownloadItem { BatchId = batch.Id, DestinationPath = "x", Status = TransferStatus.Paused });
        store.Settings.SplitCount = 4;
        store.Save();

        var reopened = AppStore.Load(path);
        Assert.Equal(category.Id, Assert.Single(reopened.Categories).Id);
        Assert.Equal("Persistent Movies", reopened.Categories[0].Name);
        Assert.Equal(TransferStatus.Paused, Assert.Single(reopened.Batches).Status);
        Assert.Equal(batch.Id, Assert.Single(reopened.Items).BatchId);
        Assert.Equal(4, reopened.Settings.SplitCount);
    }

    [Fact]
    public void CorruptStoreIsPreservedInsteadOfOverwritten()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "Myra.json");
        File.WriteAllText(path, "{ not json");
        var store = AppStore.Load(path);
        Assert.Empty(store.Categories);
        Assert.Single(Directory.GetFiles(temp.Path, "Myra.json.corrupt-*"));
    }

    [Fact]
    public void CompletedFileDetectionRequiresMatchingSizeAndNoControlFile()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "Film.mkv");
        File.WriteAllBytes(file, new byte[10]);
        var item = new DownloadItem { DestinationPath = file, TotalBytes = 10 };
        Assert.True(DownloadManager.IsAlreadyComplete(item));
        File.WriteAllText(file + ".aria2", "");
        Assert.False(DownloadManager.IsAlreadyComplete(item));
        File.Delete(file + ".aria2");
        item.TotalBytes = 20;
        Assert.False(DownloadManager.IsAlreadyComplete(item));
    }

    [Fact]
    public void Aria2OverrideIsPreferred()
    {
        using var temp = new TemporaryDirectory();
        var fake = Path.Combine(temp.Path, "aria2c-custom");
        File.WriteAllText(fake, "");
        Assert.Equal(fake, Aria2Controller.DiscoverExecutable(fake));
    }

    [Fact]
    public async Task Aria2RpcStartsAndStopsWhenInstalled()
    {
        string executable;
        try
        {
            executable = Aria2Controller.DiscoverExecutable("");
        }
        catch (Aria2Exception)
        {
            return; // aria2 is not installed on this machine.
        }
        var controller = new Aria2Controller();
        await controller.StartAsync(new AppSettings().Snapshot() with { Aria2PathOverride = executable });
        Assert.True(controller.IsRunning);
        await controller.ShutdownAsync();
        Assert.False(controller.IsRunning);
    }
}
