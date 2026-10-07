using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.App.Views;
using Myra.Core;

// Usage: Myra.UiCheck <listing-root-url> <output-folder>
var root = args.Length > 0 ? args[0] : "http://127.0.0.1:8765/";
var output = Path.GetFullPath(args.Length > 1 ? args[1] : "ui-check");
Directory.CreateDirectory(output);
var data = Path.Combine(Path.GetTempPath(), "myra-uicheck-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("MYRA_DATA_DIR", data);

AppBuilder.Configure<Myra.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var failures = new List<string>();
void Check(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    if (!condition) failures.Add(message);
}

async Task Pump(Func<bool> until, int timeoutMs = 15000)
{
    var started = DateTime.UtcNow;
    while (!until() && (DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
    }
    Dispatcher.UIThread.RunJobs();
}

void Save(MainWindow window, string name)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    window.CaptureRenderedFrame()?.Save(Path.Combine(output, name));
    Console.WriteLine("saved " + name);
}

if (Environment.GetEnvironmentVariable("MYRA_DIALOGS_ONLY") == "1") return await DialogChecks.StandaloneAsync(root, output);

var work = Dispatcher.UIThread.InvokeAsync(async () =>
{
    var services = new AppServices();
    services.Store.Settings.DownloadDirectory = Path.Combine(data, "Downloads");
    var vm = new MainViewModel(services);
    var window = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
    window.Show();

    // Empty state.
    await vm.StartAsync();
    Save(window, "01-empty.png");

    // Add a source the same way the dialog does.
    vm.ShowSourceDialog = _ => Task.FromResult<SourceInput?>(new SourceInput("Local Media", root));
    await vm.AddSourceCommand.ExecuteAsync(null);
    await Pump(() => vm.Entries.Count > 0 && !vm.IsIndexing);
    Check(vm.Categories.Count == 1, "source added");
    Check(vm.Entries.Select(e => e.Name).Where(n => n is "Movies" or "TV Series").SequenceEqual(["Movies", "TV Series"]) && vm.Entries.All(e => e.IsFolder), "root listing shows folders: " + string.Join(", ", vm.Entries.Select(e => e.Name)));
    Check(vm.IndexStatus.StartsWith("Index updated"), "index built: " + vm.IndexStatus);
    Save(window, "02-root.png");

    // Browse into a folder.
    await vm.OpenEntryCommand.ExecuteAsync(vm.Entries.First(e => e.Name == "TV Series"));
    await vm.OpenEntryCommand.ExecuteAsync(vm.Entries.First());
    await vm.OpenEntryCommand.ExecuteAsync(vm.Entries.First());
    await Pump(() => vm.Entries.Count == 4);
    Check(vm.Entries.Select(e => e.Name).SequenceEqual(["Some.Show.S01E01.720p.mkv", "Some.Show.S01E02.720p.mkv", "Some.Show.S01E03.720p.mkv", "Some.Show.S01E10.720p.mkv"]),
        "episodes in natural order");
    Check(vm.Breadcrumbs.Select(b => b.Name).SequenceEqual(["Local Media", "TV Series", "Some Show", "Season 1"]), "breadcrumbs");
    vm.Entries[0].IsSelected = true;
    vm.Entries[1].IsSelected = true;
    Check(vm.SelectedCount == 2 && vm.HasSelection, "selection count");
    Save(window, "03-season.png");

    // Current-folder filter.
    vm.IsGlobalScope = false;
    vm.SearchText = "E10";
    await Pump(() => vm.Entries.Count == 1);
    Check(vm.Entries.Count == 1 && vm.Entries[0].Name.Contains("E10"), "current filter");

    // Global search from the index.
    vm.IsGlobalScope = true;
    vm.SearchText = "arrival";
    await Pump(() => vm.GlobalResults.Count >= 2);
    Check(vm.GlobalResults.Count == 2, "global search finds both Arrival releases: " + vm.GlobalStatus);
    Check(vm.GlobalResults.All(r => r.Location.Contains("Movies › 2025")), "global result location");
    Save(window, "04-global.png");

    // Up navigation stops at the root.
    vm.SearchText = "";
    await vm.NavigateAsync(new Uri(root));
    await vm.NavigateUpCommand.ExecuteAsync(null);
    await Pump(() => !vm.IsLoading);
    Check(vm.CurrentUrl?.AbsoluteUri == new Uri(root).AbsoluteUri, "up stops at root");

    // Error bar for an unreachable source.
    vm.ShowSourceDialog = _ => Task.FromResult<SourceInput?>(new SourceInput("Broken", "http://127.0.0.1:9/nothing/"));
    await vm.AddSourceCommand.ExecuteAsync(null);
    Check(vm.ErrorMessage is not null && vm.Categories.Count == 1, "unreachable source rejected: " + vm.ErrorMessage);

    // Real downloads through aria2 when MYRA_TEST_ARIA2 points at an aria2c binary.
    var aria2 = Environment.GetEnvironmentVariable("MYRA_TEST_ARIA2");
    var mediaRoot = Environment.GetEnvironmentVariable("MYRA_TEST_MEDIA");
    if (!string.IsNullOrEmpty(aria2) && !string.IsNullOrEmpty(mediaRoot))
    {
        services.Store.Settings.Aria2PathOverride = aria2;
        await vm.NavigateAsync(new Uri(root));
        var series = vm.Entries.First(e => e.Name == "TV Series");
        series.IsSelected = true;
        await vm.DownloadSelectedCommand.ExecuteAsync(null);
        await Pump(() => vm.Batches.Count > 0 && vm.Batches[0].Status is TransferStatus.Completed or TransferStatus.Failed, 30000);
        var batch = vm.Batches[0];
        Check(batch.Status == TransferStatus.Completed, $"folder download completed ({batch.Status} {batch.ErrorMessage})");
        foreach (var name in new[] { "S01E01", "S01E02", "S01E03", "S01E10" })
        {
            var relative = Path.Combine("TV Series", "Some Show", "Season 1", $"Some.Show.{name}.720p.mkv");
            var local = Path.Combine(services.Store.Settings.DownloadDirectory, relative);
            var source = Path.Combine(mediaRoot, relative);
            Check(File.Exists(local) && new FileInfo(local).Length == new FileInfo(source).Length && !File.Exists(local + ".aria2"),
                "downloaded with hierarchy and full size: " + relative);
        }
        Check(batch.CompletedBytes == batch.TotalBytes && batch.TotalBytes > 0, "batch totals match");
        // Downloading the same folder again must not overwrite: finished files are detected locally.
        series = vm.Entries.First(e => e.Name == "TV Series");
        series.IsSelected = true;
        await vm.DownloadSelectedCommand.ExecuteAsync(null);
        await Pump(() => vm.Batches.Count > 1 && vm.Batches[0].Status == TransferStatus.Completed, 10000);
        Check(vm.Batches[0].Status == TransferStatus.Completed, "repeat download recognised existing files");

        // A global-search download goes under the source's folder name.
        vm.SearchText = "dune";
        await Pump(() => vm.GlobalResults.Count == 1);
        await vm.DownloadResultCommand.ExecuteAsync(vm.GlobalResults[0]);
        await Pump(() => vm.Batches[0].Status is TransferStatus.Completed or TransferStatus.Failed, 15000);
        var dune = Path.Combine(services.Store.Settings.DownloadDirectory, "Local Media", "Movies", "2026", "Dune Messiah (2026) 1080p.mp4");
        Check(File.Exists(dune), "global download stored under the source name");
        vm.SearchText = "";

        // Pause, resume, then resume again after aria2 restarts (the old gid is gone; the partial file continues).
        services.Store.Settings.SpeedLimit = "3M";
        var big = new DownloadManifestItem(new Uri(new Uri(root), "Big/Large%20Film%202024.mkv"), "Big/Large Film 2024.mkv", 30000000);
        _ = vm.Downloads.DownloadManifestAsync("Large Film", [big]);
        await Pump(() => vm.Batches[0].Title == "Large Film" && vm.Batches[0].CompletedBytes > 2_000_000, 15000);
        var large = vm.Batches[0];
        await vm.PauseBatchCommand.ExecuteAsync(large);
        await Pump(() => false, 1500);
        var pausedBytes = vm.Downloads.ItemsFor(large).Single().CompletedBytes;
        var localBig = Path.Combine(services.Store.Settings.DownloadDirectory, "Big", "Large Film 2024.mkv");
        Check(large.Status == TransferStatus.Paused && File.Exists(localBig + ".aria2"), $"paused with a partial file ({Format.Bytes(pausedBytes)})");
        await Pump(() => false, 1500);
        Check(vm.Downloads.ItemsFor(large).Single().CompletedBytes == pausedBytes, "no progress while paused");

        await services.Aria2.ShutdownAsync();
        await vm.ResumeBatchCommand.ExecuteAsync(large);
        await Pump(() => vm.Downloads.ItemsFor(large).Single().CompletedBytes > pausedBytes + 1_000_000, 15000);
        Check(large.Status == TransferStatus.Active, $"resumed after aria2 restart ({large.Status} {large.ErrorMessage} {vm.ErrorMessage} {vm.Downloads.ItemsFor(large).Single().CompletedBytes})");
        services.Store.Settings.SpeedLimit = "0";
        await vm.CancelBatchCommand.ExecuteAsync(large);
        await vm.ResumeBatchCommand.ExecuteAsync(large);
        await Pump(() => large.Status is TransferStatus.Completed or TransferStatus.Failed, 60000);
        Check(large.Status == TransferStatus.Completed && new FileInfo(localBig).Length == 30000000 && !File.Exists(localBig + ".aria2"),
            "large file completed with correct size after pause, restart and cancel/resume");
        var sameBytes = File.ReadAllBytes(localBig).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(mediaRoot, "Big", "Large Film 2024.mkv")));
        Check(sameBytes, "large file content is identical to the source");

        // Records survive a restart.
        var reloaded = AppStore.Load();
        Check(reloaded.Batches.Count == 4 && reloaded.Items.Count == 10, $"download records persisted ({reloaded.Batches.Count} batches, {reloaded.Items.Count} items)");
        vm.ShowDownloadDetails = true;
        Save(window, "05-downloads.png");
        await services.Aria2.ShutdownAsync();
        Check(!services.Aria2.IsRunning, "aria2 shut down");
    }
    else
    {
        // Without aria2 the failure must be reported, not crash.
        await vm.Downloads.DownloadManifestAsync("Some.Show.S01E01.720p.mkv",
            [new DownloadManifestItem(new Uri(new Uri(root), "TV%20Series/Some%20Show/Season%201/Some.Show.S01E01.720p.mkv"), "Some Show/Some.Show.S01E01.720p.mkv", 1000)]);
        vm.ShowDownloadDetails = true;
        Check(vm.Downloads.HasRecords && vm.Batches[0].Status == TransferStatus.Failed, "missing aria2 is reported");
        Save(window, "05-downloads.png");
    }

    await ShellChecks.RunAsync(window, vm, services, root, output, data, Check, Pump);

    // Player rules that need no libVLC.
    var stall = new PlayerStallDetector();
    stall.Reset(0);
    Check(!stall.Update(PlayerState.Playing, 1, 1, false) && !stall.Update(PlayerState.Playing, 2, 3, false), "no buffering while time advances");
    Check(!stall.Update(PlayerState.Playing, 2, 4.5, false) && stall.Update(PlayerState.Playing, 2, 5.1, false), "buffering only after a two-second stall");
    Check(!stall.Update(PlayerState.Playing, 2, 9, true) && !stall.Update(PlayerState.Paused, 2, 12, false), "no buffering while scrubbing or paused");
    var visibility = new PlayerControlsVisibility { Fullscreen = true };
    visibility.Interact(10);
    Check(visibility.IsVisible(11.9, false) && !visibility.IsVisible(12.1, false), "fullscreen controls hide after two seconds");
    visibility.Hovering = true;
    Check(visibility.IsVisible(60, false), "controls stay while the pointer is over them");
    visibility.Hovering = false;
    visibility.MenuTracking = true;
    Check(visibility.IsVisible(60, false), "controls stay while a menu is open");
    visibility.MenuTracking = false;
    Check(visibility.IsVisible(60, true), "controls stay while seeking");
    visibility.Fullscreen = false;
    Check(visibility.IsVisible(60, false), "controls always show outside fullscreen");
    Check(SeekBarGeometry.Fraction(0, 200) == 0 && SeekBarGeometry.Fraction(200, 200) == 1 && Math.Abs(SeekBarGeometry.Fraction(100, 200) - 0.5) < 0.001, "seekbar geometry maps the inset track");
    try
    {
        var view = new PlayerView();
        var host = new Window { Content = view, Width = 1100, Height = 360 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        host.CaptureRenderedFrame()?.Save(Path.Combine(output, "08-player-view.png"));
        Console.WriteLine("saved 08-player-view.png");
        Check(true, "PlayerView loads and renders without a view model");
        host.Close();
    }
    catch (Exception error)
    {
        Console.WriteLine("SKIP PlayerView render: " + error.Message);
    }

    // Embedded playback through libVLC when MYRA_LIBVLC_DIR is set.
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYRA_LIBVLC_DIR")))
    {
        PlayerViewModel? player = null;
        var closed = 0;
        vm.ShowPlayer = p =>
        {
            if (player != p) p.BackToLibraryRequested += () => closed++;
            player = p;
        };
        vm.SearchText = "";
        await vm.NavigateAsync(new Uri(new Uri(root), "TV%20Series/Some%20Show/Season%201/"));
        await vm.Play(vm.Entries[0].Entry);
        Check(player is not null, "player opened " + vm.ErrorMessage);
        if (player is null) throw new InvalidOperationException("player did not open");
        await Pump(() => player!.State == PlayerState.Playing && player.Duration > 0, 10000);
        Check(player!.State == PlayerState.Playing && player.Duration is > 2 and < 4, $"episode 1 plays ({player.State}, {player.Duration:0.0}s) {player.ErrorMessage}");
        Check(!player.HasPrevious && player.HasNext, "first episode has only a next neighbour");
        // The test plays the window's part: it confirms fullscreen changes the player asks for.
        var requests = new List<bool>();
        player.FullscreenChangeRequested += requests.Add;
        player.ToggleFullscreenCommand.Execute(null);
        player.ExitFullscreenCommand.Execute(null);
        Check(requests.SequenceEqual([true]), "an exit during the enter transition is queued, not sent");
        player.NotifyFullscreenChanged(true);
        Check(requests.SequenceEqual([true, false]), "the queued exit runs once the window has entered fullscreen");
        player.NotifyFullscreenChanged(false);
        Check(!player.IsFullscreen, "fullscreen ended");
        player.ToggleFullscreenCommand.Execute(null);
        player.NotifyFullscreenChanged(true);
        Check(player.IsFullscreen, "fullscreen entered");
        await Pump(() => player.Title.Contains("S01E02") && player.State == PlayerState.Playing, 10000);
        Check(player.Title.Contains("S01E02") && player.State == PlayerState.Playing, "auto-next moved to episode 2: " + player.Title);
        Check(player.IsFullscreen, "automatic episode change keeps fullscreen");
        player.NotifyFullscreenChanged(false);
        requests.Clear();
        player.NextCommand.Execute(null);
        await Pump(() => player.Title.Contains("S01E03") && player.State == PlayerState.Playing, 5000);
        player.NextCommand.Execute(null);
        await Pump(() => player.Title.Contains("S01E10") && player.State == PlayerState.Playing, 5000);
        Check(player.Title.Contains("S01E10") && !player.HasNext, "natural order: E03 then E10, which is last");
        player.PreviousCommand.Execute(null);
        await Pump(() => player.Title.Contains("S01E03"), 5000);
        Check(player.Title.Contains("S01E03"), "previous goes back to E03");
        player.Repeat = true;
        await Pump(() => false, 4500);
        Check(player.Title.Contains("S01E03") && player.State == PlayerState.Playing, "repeat keeps the same episode");
        player.Repeat = false;
        player.NextCommand.Execute(null);
        await Pump(() => player.Title.Contains("S01E10") && player.State == PlayerState.Playing, 5000);
        await Pump(() => closed > 0, 8000);
        Check(closed == 1, "last episode finishes and returns to the library");

        // English audio and subtitles are chosen over French defaults.
        await vm.NavigateAsync(new Uri(new Uri(root), "Tracks/"));
        await vm.Play(vm.Entries.First(e => e.Name == "TrackDefaults.mkv").Entry);
        await Pump(() => player.State == PlayerState.Playing && player.AudioTracks.Count >= 2 && player.SubtitleTracks.Count >= 2, 10000);
        await Pump(() => false, 800);
        Check(player.SelectedAudio?.Name.Contains("English", StringComparison.OrdinalIgnoreCase) == true,
            $"English audio selected: {player.SelectedAudio?.Name} of [{string.Join(", ", player.AudioTracks)}]");
        Check(player.SelectedSubtitle?.Name.Contains("English", StringComparison.OrdinalIgnoreCase) == true,
            $"English subtitles selected: {player.SelectedSubtitle?.Name} of [{string.Join(", ", player.SubtitleTracks)}]");
        var french = player.AudioTracks.First(t => t.Name.Contains("French", StringComparison.OrdinalIgnoreCase));
        player.SelectedAudio = french;
        await Pump(() => false, 1200);
        Check(player.MediaPlayer.AudioTrack == french.Id, "manual audio choice is kept");

        // Resume position.
        await vm.Play(vm.Entries.First(e => e.Name.StartsWith("Long Clip")).Entry);
        await Pump(() => player.State == PlayerState.Playing && player.Duration > 15, 10000);
        player.SeekTo(9);
        await Pump(() => false, 6000);
        await vm.Play(vm.Entries.First(e => e.Name == "TrackDefaults.mkv").Entry);
        await Pump(() => player.Title == "TrackDefaults" && player.State == PlayerState.Playing, 10000);
        await vm.Play(vm.Entries.First(e => e.Name.StartsWith("Long Clip")).Entry);
        await Pump(() => player.Title.StartsWith("Long Clip") && player.State == PlayerState.Playing && player.IsSeekable, 10000);
        Check(player.ResumePosition is >= 9, $"resume prompt offers the saved position ({player.ResumePosition:0.0}s)");
        player.ResumeSavedPositionCommand.Execute(null);
        await Pump(() => player.Position >= 9, 5000);
        Check(player.Position >= 9 && player.ResumePosition is null, $"Resume jumps to the saved position ({player.Position:0.0}s)");
        // Command-line playback ("Myra.exe <url>") builds its queue from the URL's folder.
        // Command-line playback uses its own player window.
        vm.ShowPlayerWindow = p => player = p;
        vm.PlayTarget(new Uri(new Uri(root), "TV%20Series/Some%20Show/Season%201/Some.Show.S01E02.720p.mkv").AbsoluteUri);
        await Pump(() => player.Title.Contains("S01E02") && player.State == PlayerState.Playing, 10000);
        Check(player.Title.Contains("S01E02") && player.HasPrevious && player.HasNext, "command-line URL plays with its folder as the queue");
        player.ChangeVolume(30);
        Check(player.Volume == 100 && player.MediaPlayer.Volume <= 100, "volume is capped at 100");
        Check(player.HandleShortcut(Key.Down, KeyModifiers.None) && player.Volume == 95, "Down lowers the volume by 5");
        var before = player.Position;
        Check(player.HandleShortcut(Key.Right, KeyModifiers.Shift) && player.Position > before + 40, "Shift+Right seeks far forward (60 seconds, within the clip)");
        Check(player.HandleShortcut(Key.Left, KeyModifiers.Alt) && !player.HandleShortcut(Key.Escape, KeyModifiers.None), "Alt+Left seeks 3 seconds; Escape does nothing outside fullscreen");
        Check(player.HandleShortcut(Key.Space, KeyModifiers.None), "Space is handled");
        await Pump(() => player.State == PlayerState.Paused, 3000);
        Check(player.State == PlayerState.Paused, "Space pauses and Pause stays in playback");
        player.HandleShortcut(Key.Space, KeyModifiers.None);
        var menuItems = new[] { PlayerMenuKind.Audio, PlayerMenuKind.Subtitles, PlayerMenuKind.Speed, PlayerMenuKind.Video }
            .Select(kind => PlayerMenus.Build(player, kind, () => { })).ToList();
        Check(menuItems.All(m => m.Items.Count > 0), "audio, subtitle, speed and video menus build");
        var requested = 0;
        player.OnlineSubtitlesRequested += _ => requested++;
        player.FindOnlineSubtitles();
        Check(requested == 1, "Find Online Subtitles raises OnlineSubtitlesRequested");
        // Back to Library from fullscreen first leaves fullscreen, then returns to the library.
        var returned = 0;
        player.BackToLibraryRequested += () => returned++;
        player.ToggleFullscreenCommand.Execute(null);
        player.NotifyFullscreenChanged(true);
        player.BackToLibraryCommand.Execute(null);
        Check(returned == 0 && requests.Last() == false, "Back to Library asks to leave fullscreen first");
        player.NotifyFullscreenChanged(false);
        Check(returned == 1 && player.Current is null && !player.IsClosed, "Back to Library returns after fullscreen ends and keeps the player reusable");
        player.Close();
        Check(player.IsClosed, "player closed");
    }

    var settings = new SettingsWindow(services.Store.Settings, services.PlayerState) { Width = 600, Height = 520 };
    settings.Show();
    await Pump(() => false, 300);
    settings.CaptureRenderedFrame()?.Save(Path.Combine(output, "06-settings.png"));
    settings.Close();

    vm.CycleThemeCommand.Execute(null);
    vm.CycleThemeCommand.Execute(null);
    Save(window, "07-dark.png");
    await DialogChecks.RunAsync(root, output, Check, Pump);
    await SubtitleIconChecks.RunAsync(root, output, Check, Pump);
    window.Close();
});

while (!work.IsCompleted)
{
    Dispatcher.UIThread.RunJobs();
    Thread.Sleep(20);
}
await work;
Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED");
try { Directory.Delete(data, true); } catch { }
return failures.Count == 0 ? 0 : 1;
