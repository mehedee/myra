using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Myra.App.Services;
using Myra.App.Views;
using Myra.Core;

// Headless render and behaviour checks for the secondary dialogs.
// Call site in Program.cs (inside the Dispatcher work, before window.Close()):
//     await DialogChecks.RunAsync(root, output, Check, Pump);
public static class DialogChecks
{
    private static string _output = "";
    private static readonly Window Owner = new() { Width = 200, Height = 100 };

    /// Runs only the dialog checks (set MYRA_DIALOGS_ONLY=1). Needs the listing server but not aria2 or libVLC.
    public static async Task<int> StandaloneAsync(string listingRoot, string output)
    {
        var failures = 0;
        void Check(bool ok, string message)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + message);
            if (!ok) failures++;
        }
        async Task Pump(Func<bool> until, int timeoutMs)
        {
            var started = DateTime.UtcNow;
            while (!until() && (DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }
            Dispatcher.UIThread.RunJobs();
        }
        var work = Dispatcher.UIThread.InvokeAsync(() => RunAsync(listingRoot, output, Check, Pump));
        while (!work.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        await work;
        Console.WriteLine(failures == 0 ? "ALL DIALOG CHECKS PASSED" : $"{failures} DIALOG CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    public static async Task RunAsync(string listingRoot, string output, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        _output = output;
        Owner.Show();
        var services = new AppServices();
        services.Store.Categories.Add(new Category { Name = "Local Media", RootUrlString = listingRoot });

        await Settings(services, check, pump);
        await IndexManagement(services, check, pump);
        await PersonalLibrary(services, check, pump);
        await About(check);
        await CorrectMatch(services, check, pump);
        await OnlineSubtitles(services, check, pump);
        await Preferences(services, check, pump);
        await Markers(services, check, pump);
        await Versions(services, check, pump);
    }

    private static async Task<T> Open<T>(T window, string screenshot, Func<Func<bool>, int, Task> pump, int settleMs = 400) where T : Window
    {
        if (!window.IsVisible) window.Show();
        await pump(() => false, settleMs);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(_output, screenshot));
        Console.WriteLine("saved " + screenshot);
        return window;
    }

    private static T? Find<T>(Window window, Func<T, bool> match) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(match);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool HasText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(text, StringComparison.Ordinal) == true && t.IsVisible);

    private static GlobalSearchResult Media(string name, string folder = "http://127.0.0.1:8765/") =>
        new(Guid.NewGuid(), "Local Media", new Uri(folder), new DirectoryEntry(name, new Uri(new Uri(folder), name), EntryKind.File, 1_500_000_000), name, null);

    private static async Task Settings(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var window = await Open(new SettingsWindow(services.Store.Settings, services.PlayerState, services) { Height = 1500 }, "10-settings.png", pump);
        check(HasText(window, "This product uses the TMDB API but is not endorsed or certified by TMDB."), "Settings shows the TMDB notice");
        check(HasText(window, "Home Discovery") && HasText(window, "Free-First Subtitles"), "Settings has Home Discovery and Free-First Subtitles");
        var omdb = Find<TextBox>(window, t => t.Watermark == "OMDb API Key (optional)");
        var save = Find<Button>(window, b => b.Content as string == "Save API Key");
        check(omdb is not null && save is not null, "Settings has OMDb key field");
        if (omdb is not null && save is not null)
        {
            omdb.Text = "omdb-secret-123";
            Click(save);
            check(services.Secrets.Read(SecretNames.OmdbApiKey) == "omdb-secret-123", "OMDb key saved through ISecretStore");
        }
        var token = Find<TextBox>(window, t => t.Watermark == "TMDB API Read Access Token");
        var saveToken = Find<Button>(window, b => b.Content as string == "Save Token");
        if (token is not null && saveToken is not null)
        {
            token.Text = "tmdb-secret-456";
            Click(saveToken);
            check(services.Secrets.Read(SecretNames.TmdbReadToken) == "tmdb-secret-456", "TMDB token saved through ISecretStore");
        }
        var notify = Find<CheckBox>(window, c => c.Content as string == "Notify me about new episodes of followed series");
        if (notify is not null)
        {
            notify.IsChecked = true;
            check(services.Entertainment.Personal.Preferences.Notifications, "notification opt-in is stored");
        }
        services.Store.Save();
        var json = File.ReadAllText(AppPaths.StorePath);
        check(!json.Contains("omdb-secret-123") && !json.Contains("tmdb-secret-456"), "secrets are absent from Myra.json");
        window.Close();
    }

    private static async Task IndexManagement(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var window = await Open(new IndexManagementWindow(services), "11-index-management.png", pump, 1200);
        var tree = window.FindControl<TreeView>("Tree")!;
        check(tree.ItemCount >= 1 && tree.ItemCount == services.Store.Categories.Count, $"Index Management lists one root per source ({tree.ItemCount})");
        var selected = window.FindControl<Button>("RefreshSelectedButton")!;
        check(!selected.IsEnabled, "Refresh selected is disabled with no selection");
        if (tree.Items.FirstOrDefault() is Myra.App.ViewModels.IndexNode root)
        {
            root.IsSelected = true;
            root.IsExpanded = true;
            check(selected.IsEnabled, "Refresh selected enables when a folder is selected");
            Click(selected);
            await pump(() => !services.IndexRefresh.IsRefreshing && window.FindControl<TextBlock>("ResultText")!.IsVisible, 20000);
            await pump(() => false, 600);
            check(window.FindControl<TextBlock>("ResultText")!.IsVisible, "refresh result is shown: " + window.FindControl<TextBlock>("ResultText")!.Text);
            check(((Myra.App.ViewModels.IndexNode)tree.Items.First()!).Children.Count > 0, "folder tree has children after refresh");
            root = (Myra.App.ViewModels.IndexNode)tree.Items.First()!;
            root.ScheduleIndex = 2;
            check(services.IndexRefresh.Policies.OverrideFor(root.Scope) == IndexSchedule.Weekly, "schedule override stored in IndexPolicies");
            await pump(() => false, 400);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(_output, "11b-index-refreshed.png"));
        }
        window.Close();
    }

    private static async Task PersonalLibrary(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var window = new PersonalLibraryDialog(services);
        var transfer = new PersonalLibraryTransfer(services.Store, services.Entertainment, () => services.PlayerState, _ => { });
        var preview = PersonalLibraryTransfer.Preview(transfer.Export());
        window.Show();
        window.ShowPreview(preview with { Archive = WithDownloadPath(preview.Archive) });
        await Open(window, "12-personal-library.png", pump);
        check(HasText(window, "Import preview") && HasText(window, "Previous download directory"), "import preview lists download path for reconnection");
        window.Close();
    }

    private static EntertainmentArchive WithDownloadPath(EntertainmentArchive archive)
    {
        archive.AppPreferences = (archive.AppPreferences ?? new EntertainmentAppPreferences("system", 4, 8, 8, 5, "0", null)) with { PreviousDownloadDirectory = "/home/old/Downloads/Myra" };
        return archive;
    }

    private static async Task About(Action<bool, string> check)
    {
        var window = await Open(new AboutDialog(), "13-about.png", (_, ms) => Task.Delay(ms));
        check(HasText(window, "This product uses the TMDB API but is not endorsed or certified by TMDB."), "About shows the TMDB notice");
        check(HasText(window, "CC BY-NC 4.0") && HasText(window, "LGPL") && HasText(window, "GPL 2"), "About shows OMDb, libVLC and aria2 notes");
        check(window.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), "About shows the logo");
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(_output, "13b-about-dark.png"));
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        window.Close();
    }

    private static async Task CorrectMatch(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var versions = new[] { Media("Some.Movie.2020.1080p.WEB-DL.mkv"), Media("Some.Movie.2020.720p.BluRay.mkv") }
            .Select(m => new EntertainmentVersion(m, DateTimeOffset.Now)).ToList();
        var title = new EntertainmentTitle("movie|some movie|2020", "Some Movie", "2020", EntertainmentKind.Movie, versions);
        var window = new CorrectMatchDialog(services.Entertainment, title);
        var result = window.ShowDialog<bool>(Owner);
        await Open(window, "14-correct-match.png", pump);
        var year = Find<TextBox>(window, t => t.Watermark == "Year (optional)")!;
        var save = Find<Button>(window, b => b.Content as string == "Save correction")!;
        year.Text = "20x";
        Click(save);
        await pump(() => HasText(window, "four-digit year"), 3000);
        check(HasText(window, "four-digit year"), "Correct Match shows the store error");
        year.Text = "2021";
        Click(save);
        await pump(() => result.IsCompleted, 5000);
        check(result.IsCompleted && await result, "Correct Match returns true when the store accepts");
    }

    private static async Task OnlineSubtitles(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var window = new OnlineSubtitlesDialog(services, Media("Some.Show.S01E02.720p.mkv"));
        await Open(window, "15-online-subtitles.png", pump, 800);
        check(!HasText(window, "Add your OpenSubtitles.com API key in Settings first"), "opening Find Subtitles does not search or report a missing key");
        check(Find<TextBox>(window, t => t.Text == "Some Show") is not null, "query is prefilled from the file name");
        window.Close();
    }

    private static async Task Preferences(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var speed = 1f;
        var autoplay = true;
        var window = await Open(new PlaybackPreferencesDialog(services.PlayerState, speed, s => speed = s, a => autoplay = a), "16-playback-preferences.png", pump);
        var boxes = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        boxes[2].SelectedIndex = 4;
        check(Math.Abs(speed - 1.5f) < 0.001f, "Playback Preferences sets the speed");
        var autoplayBox = Find<CheckBox>(window, c => c.Content as string == "Automatically play the next video")!;
        autoplayBox.IsChecked = false;
        check(!autoplay && !services.PlayerState.Autoplay, "Playback Preferences sets autoplay");
        window.Close();
    }

    private static async Task Markers(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var media = Media("Some.Show.S01E02.720p.mkv");
        var window = await Open(new SkipMarkersDialog(services.PlayerState, media, 2700, () => 95.5), "17-skip-markers.png", pump);
        var save = Find<Button>(window, b => b.Content as string == "Save")!;
        var intro = Find<CheckBox>(window, c => c.Content as string == "Intro")!;
        intro.IsChecked = true;
        var numbers = window.GetVisualDescendants().OfType<NumericUpDown>().ToList();
        numbers[1].Value = 9999;
        Click(save);
        check(HasText(window, "must end within the video's duration"), "markers are validated against the duration");
        Click(Find<Button>(window, b => b.Content as string == "Use current time")!);
        check(numbers[0].Value == 95.5m, "Use current time fills the start");
        numbers[0].Value = 5;
        numbers[1].Value = 70;
        Click(save);
        await pump(() => !window.IsVisible, 2000);
        check(PlayerMarkers.Current(services.PlayerState, media).Intro == new PlayerSkipRange(5, 70), "markers are saved to PlayerPersonalState");
    }

    private static async Task Versions(AppServices services, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var versions = new[] { Media("Some.Movie.2020.1080p.WEB-DL.x265.mkv"), Media("Some.Movie.2020.720p.BluRay.mkv") };
        var window = new VersionChooserDialog(services, versions);
        var result = window.ShowDialog<GlobalSearchResult?>(Owner);
        await Open(window, "18-version-chooser.png", pump);
        check(HasText(window, "1080p"), "version chooser shows quality labels");
        Dispatcher.UIThread.RunJobs();
        Press(window, Avalonia.Input.Key.Down);
        Press(window, Avalonia.Input.Key.Enter);
        await pump(() => result.IsCompleted, 2000);
        check(result.IsCompleted && (await result)?.Entry.Name == versions[1].Entry.Name, "keyboard Down + Enter chooses the second release");
    }

    private static void Press(Window window, Avalonia.Input.Key key)
    {
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, key, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
