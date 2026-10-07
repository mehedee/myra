using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.App.Views;
using Myra.Core;

/// Main-window checks: Home, navigation, Pick Something, Details, the inspector, Offline Library,
/// the version chooser and index refresh integration. They need the listing server, not libVLC or aria2.
internal static class ShellChecks
{
    /// Records dialog calls; the version chooser cancels.
    private sealed class RecordingDialogs : IAppDialogs
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<GlobalSearchResult>? Versions { get; private set; }

        public Task ShowSettingsAsync(Window owner) => Record("Settings");
        public Task ShowIndexManagementAsync(Window owner) => Record("IndexManagement");
        public Task ShowPersonalLibraryAsync(Window owner) => Record("PersonalLibrary");
        public Task ShowAboutAsync(Window owner) => Record("About");
        public Task ShowPlaybackPreferencesAsync(Window owner, PlayerViewModel player) => Record("PlaybackPreferences");
        public Task ShowSkipMarkersAsync(Window owner, PlayerViewModel player) => Record("SkipMarkers");

        public Task<bool> ShowCorrectMatchAsync(Window owner, EntertainmentTitle title)
        {
            Calls.Add("CorrectMatch");
            return Task.FromResult(false);
        }

        public Task<string?> ShowOnlineSubtitlesAsync(Window owner, GlobalSearchResult media)
        {
            Calls.Add("OnlineSubtitles");
            return Task.FromResult<string?>(null);
        }

        public Task<GlobalSearchResult?> ChooseVersionAsync(Window owner, IReadOnlyList<GlobalSearchResult> versions)
        {
            Calls.Add("ChooseVersion");
            Versions = versions;
            return Task.FromResult<GlobalSearchResult?>(null);
        }

        private Task Record(string name)
        {
            Calls.Add(name);
            return Task.CompletedTask;
        }
    }

    public static async Task RunAsync(MainWindow window, MainViewModel vm, AppServices services, string root, string output, string data,
        Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        void Save(Window target, string name)
        {
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            target.CaptureRenderedFrame()?.Save(Path.Combine(output, name));
            Console.WriteLine("saved " + name);
        }

        T? Owned<T>() where T : Window => window.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible);

        var dialogs = new RecordingDialogs();
        vm.Dialogs = dialogs;
        vm.SearchText = "";
        vm.IsGlobalScope = true;

        // ---------- Index refresh integration ----------
        await vm.IndexTask;
        vm.RefreshIndexNowCommand.Execute(null);
        await vm.IndexTask;
        await pump(() => vm.IndexCompletionMessage is not null, 5000);
        check(vm.IndexCompletionMessage is not null, "manual refresh shows a completion message: " + vm.IndexCompletionMessage);
        check(vm.IndexStatus.StartsWith("Index updated") && !vm.IsIndexing, "index status after refresh: " + vm.IndexStatus);
        vm.DismissIndexCompletionCommand.Execute(null);
        check(vm.ShowDownloadFooter == services.Downloads.HasRecords, "download footer follows the queue records");

        // ---------- Home ----------
        await services.Entertainment.ReloadAsync();
        vm.Section = ShellSection.Home;
        vm.Home.Schedule();
        await pump(() => !vm.Home.IsPreparing && vm.Home.TitleCount > 0, 8000);
        var names = vm.Home.Shelves.Select(s => s.Name).ToList();
        check(vm.IsHome && vm.SelectedNav.Section == ShellSection.Home, "Home section selected");
        check(names.Take(6).SequenceEqual(["Continue Watching", "Top Rated Movies", "Series to Watch", "Latest Releases", "Recently Added", "Watchlist"]),
            "Home shelves in macOS order: " + string.Join(", ", names));
        var recent = vm.Home.Shelves.First(s => s.Name == "Recently Added");
        var arrival = recent.Cards.FirstOrDefault(c => c.DisplayName.Contains("Arrival"));
        check(arrival is not null && arrival.Title.Versions.Count == 2, "two Arrival releases are one title with two versions");
        var show = recent.Cards.FirstOrDefault(c => c.IsSeries);
        check(show is not null && show.Title.Versions.Count == 4, "the series groups its four episodes");
        check(vm.Home.TitleCountText.EndsWith("titles") && vm.Home.Sources.Any(s => s.Value == "Local Media"), "title count and source filter: " + vm.Home.TitleCountText);
        var combos = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ComboBox>().ToList();
        check(combos.Count == 5 && combos.All(c => c.SelectedIndex == 0), "Home filters show their choices: " + string.Join(", ", combos.Select(c => c.SelectedItem)));
        Save(window, "10-home.png");

        // Filters (debounced projection off the UI thread).
        vm.Home.Query = "arriv";
        await pump(() => !vm.Home.IsPreparing && vm.Home.Filtered.Count == 1, 5000);
        check(vm.Home.Filtered.Count == 1 && vm.Home.Filtered[0].DisplayName.Contains("Arrival"), "Home search filters titles");

        // ---------- Pick Something: dismissed before the version chooser opens ----------
        var pickTask = vm.Home.PickSomethingCommand.ExecuteAsync(null);
        await pump(() => Owned<PickWindow>() is not null, 5000);
        var pickWindow = Owned<PickWindow>();
        check(pickWindow is not null && vm.LastPick?.Details?.DisplayName.Contains("Arrival") == true, "Pick window shows a title matching the filters");
        if (pickWindow is not null) Save(pickWindow, "12-pick.png");
        vm.LastPick?.Details?.PlayCommand.Execute(null);
        await pump(() => Owned<DetailChooserWindow>() is not null, 5000);
        var chooser = Owned<DetailChooserWindow>();
        check(chooser is not null && Owned<PickWindow>() is null, "Play closes the Pick window before the version chooser opens");
        if (chooser is not null)
        {
            Save(chooser, "14-version-chooser.png");
            chooser.Close(null);
        }
        await pump(() => pickTask.IsCompleted, 3000);

        // Another Pick never repeats while another unwatched title exists; watched titles are excluded.
        vm.Home.Query = "";
        await pump(() => !vm.Home.IsPreparing && vm.Home.Filtered.Count > 2, 5000);
        using (var pick = new PickViewModel(vm, vm.Home.Filtered, vm.Home.Filter.Description))
        {
            for (var i = 0; i < 6; i++) pick.AnotherPickCommand.Execute(null);
            var repeats = pick.History.Zip(pick.History.Skip(1)).Count(p => p.First == p.Second);
            check(repeats == 0 && pick.History.Count == 7, "Another Pick never repeats the current title");
        }
        var watched = vm.Home.Filtered.Select(t => t.Id).ToList();
        foreach (var id in watched) services.Entertainment.ToggleWatched(id);
        vm.Home.Query = "arriv";
        await pump(() => !vm.Home.IsPreparing && vm.Home.Filtered.Count == 1, 5000);
        check(vm.Home.CanPick, "Pick stays available when every matching title is watched (the window explains why)");
        vm.Home.Query = "zzzz";
        check(!vm.Home.CanPick, "Pick is disabled while the projection does not match the current filters");
        await pump(() => !vm.Home.IsPreparing, 5000);
        vm.Home.Query = "";
        await pump(() => !vm.Home.IsPreparing && vm.Home.Filtered.Count > 2, 5000);
        using (var empty = new PickViewModel(vm, vm.Home.Filtered, vm.Home.Filter.Description, () => vm.Home.ResetCommand.Execute(null)))
        {
            check(empty.IsEmpty && empty.CanResetFilters, "Pick shows the empty state with Reset Home Filters when every title is watched");
            var emptyWindow = new PickWindow(empty);
            _ = emptyWindow.ShowDialog<EntertainmentTitle?>(window);
            await pump(() => emptyWindow.IsVisible, 2000);
            Save(emptyWindow, "13-pick-empty.png");
            var resetButton = emptyWindow.FindControl<Button>("ResetFiltersButton");
            check(resetButton is { IsVisible: true }, "the empty Pick window shows Reset Home Filters");
            vm.Home.Query = "arriv";
            await pump(() => !vm.Home.IsPreparing && vm.Home.Filtered.Count == 1, 5000);
            empty.ResetFiltersCommand.Execute(null);
            await pump(() => !emptyWindow.IsVisible, 2000);
            await pump(() => !vm.Home.IsPreparing && vm.Home.Query.Length == 0, 5000);
            check(!emptyWindow.IsVisible && vm.Home.Query.Length == 0, "Reset Home Filters closes the window and clears the filters");
        }
        foreach (var id in watched) services.Entertainment.ToggleWatched(id);
        await pump(() => !vm.Home.IsPreparing, 3000);

        // ---------- Details ----------
        if (show is not null)
        {
            var detailsTask = vm.ShowDetailsAsync(show.Title);
            await pump(() => Owned<DetailWindow>() is not null, 5000);
            var details = Owned<DetailWindow>();
            check(details?.DataContext is DetailViewModel { IsSeries: true, Episodes.Seasons.Count: 1 }, "Details lists the series by season");
            if (details is not null)
            {
                var model = (DetailViewModel)details.DataContext!;
                model.CorrectMatchCommand.Execute(null);
                await pump(() => dialogs.Calls.Contains("CorrectMatch"), 2000);
                check(dialogs.Calls.Contains("CorrectMatch"), "Correct Match opens through IAppDialogs");
                Save(details, "15-details.png");
                model.CloseCommand.Execute(null);
            }
            await pump(() => detailsTask.IsCompleted, 3000);
        }

        // ---------- Watchlist and the Personal section ----------
        if (show is not null) vm.Home.ToggleWatchlistCommand.Execute(show);
        await pump(() => !vm.Home.IsPreparing && vm.Home.Shelves.First(s => s.Name == "Watchlist").Count == 1, 5000);
        check(vm.Home.Shelves.First(s => s.Name == "Watchlist").Count == 1, "Watchlist toggle adds the title to the Watchlist shelf");
        services.Entertainment.CreateCollection("Weekend");
        await pump(() => !vm.Home.IsPreparing && vm.Home.PersonalShelves.Any(s => s.Name == "Weekend"), 5000);
        vm.SelectedNav = vm.NavItems.First(n => n.Section == ShellSection.Personal);
        check(vm.IsPersonal && vm.Home.PersonalShelves.Select(s => s.Name).SequenceEqual(["Watchlist", "Weekend"]), "Personal section shows Watchlist and collections");
        Save(window, "11-personal.png");
        var collections = new HomeCollectionsWindow(new HomeCollectionsViewModel(services.Entertainment));
        _ = collections.ShowDialog(window);
        await pump(() => collections.IsVisible, 2000);
        Save(collections, "18-collections.png");
        collections.Close();

        // ---------- Offline Library ----------
        var folder = Path.Combine(data, "Downloads", "Movies");
        Directory.CreateDirectory(folder);
        var mediaRoot = Environment.GetEnvironmentVariable("MYRA_TEST_MEDIA");
        var present = Path.Combine(folder, "Dune Messiah (2026) 1080p.mp4");
        if (!string.IsNullOrEmpty(mediaRoot)) File.Copy(Path.Combine(mediaRoot, "Movies", "2026", "Dune Messiah (2026) 1080p.mp4"), present, true);
        else File.WriteAllBytes(present, new byte[1024]);
        services.Store.Items.Add(new DownloadItem { DestinationPath = present, RelativePath = "Movies/Dune Messiah (2026) 1080p.mp4", Status = TransferStatus.Completed, CompletedBytes = 1024 });
        services.Store.Items.Add(new DownloadItem { DestinationPath = Path.Combine(folder, "Gone Film (2019) 720p.mkv"), RelativePath = "Movies/Gone Film (2019) 720p.mkv", Status = TransferStatus.Completed, CompletedBytes = 1 });
        vm.SelectedNav = vm.NavItems.First(n => n.Section == ShellSection.OfflineLibrary);
        await pump(() => vm.Offline.HasLoaded && !vm.Offline.IsLoading && vm.Offline.Rows.Count == 2, 5000);
        var rows = vm.Offline.Rows.ToList();
        check(vm.IsOffline && rows.Count == 2, "Offline Library lists downloaded titles: " + rows.Count);
        check(rows.Any(r => r.IsUnavailable && r.Summary.Contains("0 available")) && rows.Any(r => r.IsAvailable),
            "missing files stay visible with guidance: " + string.Join(" | ", rows.Select(r => r.Summary)));
        Save(window, "16-offline.png");
        vm.Offline.HideMissing = true;
        check(vm.Offline.Rows.Count == 1, "Hide missing hides unavailable titles");
        vm.Offline.HideMissing = false;

        // ---------- Library browser, inspector and the version chooser ----------
        vm.SelectedNav = vm.NavItems.First(n => n.Section == ShellSection.Library);
        vm.IsGlobalScope = false;
        await vm.NavigateAsync(new Uri(new Uri(root), "Movies/2025/"));
        await pump(() => vm.Entries.Count == 2, 5000);
        check(vm.IsLibrary && vm.ShowThumbnails, "a folder with videos shows thumbnails");
        var libraryWidth = window.FindControl<Panel>("LibraryArea")!.Bounds.Width;
        await vm.OpenEntryCommand.ExecuteAsync(vm.Entries[0]);
        await pump(() => vm.Inspector.MetadataError is not null && !vm.Inspector.IsProbing, 15000);
        var panel = window.FindControl<Border>("InspectorPanel")!;
        check(vm.ShowInspector && vm.Inspector.Selected?.Entry.Name == vm.Entries[0].Name, "clicking a video opens the inspector");
        check(vm.Inspector.MetadataError?.Contains("OMDb API key") == true, "without an OMDb key the inspector explains it: " + vm.Inspector.MetadataError);
        check(vm.Inspector.LocalFields.Any(f => f.Name == "File name") && vm.Inspector.LocalFields.Any(f => f.Name == "Category"), "local information is listed");
        Save(window, "17-inspector.png");
        check(Math.Abs(panel.Bounds.Width - Math.Min(460, libraryWidth)) < 1 && Math.Abs(window.FindControl<Panel>("LibraryArea")!.Bounds.Width - libraryWidth) < 1,
            $"inspector is {panel.Bounds.Width} px over the browser, which keeps {libraryWidth} px");

        // Clearing Global search restores the previous inspector.
        var before = vm.Inspector.Selected;
        vm.IsGlobalScope = true;
        vm.SearchText = "dune";
        await pump(() => vm.GlobalResults.Count == 1, 5000);
        vm.InspectResultCommand.Execute(vm.GlobalResults[0]);
        check(vm.Inspector.Selected?.Entry.Name.Contains("Dune") == true, "a global result opens the inspector");
        vm.SearchText = "";
        check(vm.Inspector.Selected?.Id == before?.Id && vm.Entries.Count == 2, "clearing Global search restores the browser and the inspector");
        vm.SearchText = "ar";
        check(vm.GlobalStatus.Contains("3 characters"), "Global search needs three characters");
        vm.SearchText = "";

        // Fuzzy Global search tolerates typos; the toggle switches to exact matching and back.
        var fuzzyToggle = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FuzzyToggle");
        check(fuzzyToggle is { IsVisible: true, IsChecked: true } && vm.FuzzyGlobalSearch, "Fuzzy toggle sits beside Current/Global and is on by default");
        vm.SearchText = "arrivel";
        await pump(() => vm.GlobalResults.Count == 2, 5000);
        check(vm.GlobalResults.Count == 2 && vm.GlobalResults.All(r => r.Result.Entry.Name.Contains("Arrival")), $"fuzzy search finds Arrival for a typo ({vm.GlobalResults.Count}): " + vm.GlobalStatus);
        Save(window, "19-fuzzy-search.png");
        fuzzyToggle!.IsChecked = false;
        await pump(() => !vm.FuzzyGlobalSearch && !vm.IsGlobalSearching && vm.GlobalResults.Count == 0, 5000);
        check(!vm.FuzzyGlobalSearch && vm.GlobalResults.Count == 0 && vm.GlobalStatus.Contains("No indexed title matched"), "exact mode rejects the typo: " + vm.GlobalStatus);
        vm.SearchText = "arrival";
        await pump(() => vm.GlobalResults.Count == 2, 5000);
        check(vm.GlobalResults.Count == 2, "exact mode still finds the correct spelling");
        check(services.Store.Settings.FuzzyGlobalSearch == false, "the search mode is saved with the settings");
        Save(window, "20-exact-search.png");
        fuzzyToggle.IsChecked = true;
        vm.IsGlobalScope = false;
        check(!fuzzyToggle.IsVisible, "the Fuzzy toggle is hidden for Current-folder search");
        vm.IsGlobalScope = true;
        vm.SearchText = "";

        // Play from directory browsing offers the version chooser for alternate releases.
        await vm.PlayEntryCommand.ExecuteAsync(vm.Entries[0]);
        check(dialogs.Calls.Contains("ChooseVersion") && dialogs.Versions?.Count == 2 && !vm.IsPlayerVisible,
            $"directory Play asks which release to play ({dialogs.Versions?.Count} versions)");
        vm.Inspector.Close();

        // Menu entries go through IAppDialogs.
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await vm.OpenIndexManagementCommand.ExecuteAsync(null);
        await vm.OpenPersonalLibraryCommand.ExecuteAsync(null);
        await vm.OpenAboutCommand.ExecuteAsync(null);
        check(new[] { "Settings", "IndexManagement", "PersonalLibrary", "About" }.All(dialogs.Calls.Contains), "menu entries open the dialogs: " + string.Join(", ", dialogs.Calls));

        vm.Section = ShellSection.Home;
        vm.Dialogs = new NullAppDialogs();
    }
}
