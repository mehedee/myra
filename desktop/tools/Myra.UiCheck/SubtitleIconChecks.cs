using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Myra.App.Services;
using Myra.App.Views;
using Myra.Core;

// Headless checks for free-first subtitles, the icon families and the AI hook section in Settings.
// Call site in Program.cs: await SubtitleIconChecks.RunAsync(root, output, Check, Pump);
public static class SubtitleIconChecks
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";

    private const string SearchJson =
        """{"total_pages":1,"data":[{"attributes":{"language":"en","release":"Movie Release","fps":24,"download_count":5,"files":[{"file_id":4242,"file_name":"movie.srt"}]}}]}""";

    private static readonly Window Owner = new() { Width = 200, Height = 100 };

    public static async Task RunAsync(string listingRoot, string output, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        Owner.Show();
        var services = new AppServices();
        services.Store.Categories.Add(new Category { Name = "Local Media", RootUrlString = listingRoot });
        await SubtitleChecks(services, output, check, pump);
        await IconChecks(services, output, check, pump);
    }

    private static T? Find<T>(Window window, Func<T, bool> match) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(match);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool HasText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(text, StringComparison.Ordinal) == true && t.IsVisible);

    private static void Capture(Window window, string output, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, name));
        Console.WriteLine("saved " + name);
    }

    private static async Task SubtitleChecks(AppServices services, string output, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var folder = Path.Combine(Path.GetTempPath(), "myra-subcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Movie.2020.1080p.mkv");
        File.WriteAllText(video, "video");
        File.WriteAllText(Path.Combine(folder, "Movie.2020.1080p.en.srt"), Srt);
        File.WriteAllText(Path.Combine(folder, "Other.srt"), Srt);
        var media = new GlobalSearchResult(Guid.NewGuid(), "Local", new Uri(folder + "/"),
            new DirectoryEntry(Path.GetFileName(video), new Uri(video), EntryKind.File, 5), Path.GetFileName(video), null);

        var handler = new CountingHandler(new()
        {
            ["/api/v1/subtitles"] = (200, SearchJson),
            ["/api/v1/download"] = (200, """{"link":"https://dl.opensubtitles.com/file/4242","remaining":7}"""),
            ["/file/4242"] = (200, Srt),
        });
        var provider = new OpenSubtitlesService(handler.Client());
        var opened = new List<string>();
        var previousOpen = OnlineSubtitlesDialog.OpenUrl;
        OnlineSubtitlesDialog.OpenUrl = opened.Add;
        try
        {
            // 1. Opening the dialog finds the nearby file and calls no provider.
            var dialog = new OnlineSubtitlesDialog(services, media, provider);
            var result = dialog.ShowDialog<string?>(Owner);
            await pump(() => Find<Button>(dialog, b => b.Content as string == "Movie.2020.1080p.en.srt") is not null, 5000);
            await pump(() => false, 300);
            Capture(dialog, output, "18-subtitles-free-first.png");
            var nearby = Find<Button>(dialog, b => b.Content as string == "Movie.2020.1080p.en.srt");
            check(nearby is not null, "nearby subtitle file is offered for a local video");
            check(Find<Button>(dialog, b => b.Content as string == "Other.srt") is null, "unrelated subtitle files are not offered");
            check(handler.Requests.Count == 0, "opening the dialog makes no provider request");
            check(!HasText(dialog, "Add your OpenSubtitles.com API key"), "no API key error before an explicit search");
            check(Find<Button>(dialog, b => b.Content as string == "Import Subtitle…") is not null, "manual import is offered");
            Click(Find<Button>(dialog, b => b.Content as string == "Find on the Web")!);
            check(opened.Count == 1 && opened[0].StartsWith("https://www.opensubtitles.org/en/search2?MovieName=Movie", StringComparison.Ordinal),
                "web fallback opens the OpenSubtitles.org search page: " + opened.FirstOrDefault());
            check(handler.Requests.Count == 0, "the web fallback makes no provider request");

            // 2. Choosing the nearby file returns its path.
            Click(nearby!);
            await pump(() => result.IsCompleted, 3000);
            check(result.IsCompleted && await result == Path.Combine(folder, "Movie.2020.1080p.en.srt"), "choosing a nearby file returns it");

            // 3. Manual file choice.
            var manual = new OnlineSubtitlesDialog(services, media, provider) { PickFile = () => Task.FromResult<string?>(Path.Combine(folder, "Other.srt")) };
            var manualResult = manual.ShowDialog<string?>(Owner);
            await pump(() => false, 300);
            Click(Find<Button>(manual, b => b.Content as string == "Import Subtitle…")!);
            await pump(() => manualResult.IsCompleted, 3000);
            check(manualResult.IsCompleted && await manualResult == Path.Combine(folder, "Other.srt"), "manual import returns the chosen file");
            check(handler.Requests.Count == 0, "manual choice makes no provider request");

            // 4. Provider search only on an explicit click. A missing key is explained then.
            services.Secrets.SaveSubtitleCredentials(new SubtitleCredentials());
            var search = new OnlineSubtitlesDialog(services, media, provider);
            var searchResult = search.ShowDialog<string?>(Owner);
            await pump(() => false, 300);
            Click(Find<Button>(search, b => b.Content as string == "Search")!);
            await pump(() => HasText(search, "Add your OpenSubtitles.com API key"), 3000);
            check(HasText(search, "Add your OpenSubtitles.com API key"), "explicit search without a key explains the missing key");
            check(handler.Requests.Count == 0, "a missing key stops the search before any request");

            services.Secrets.SaveSubtitleCredentials(new SubtitleCredentials("fixture-key"));
            Click(Find<Button>(search, b => b.Content as string == "Search")!);
            await pump(() => handler.Requests.Any(r => r.AbsolutePath == "/api/v1/subtitles") && Find<ListBox>(search, _ => true)?.ItemCount > 0, 5000);
            check(handler.Requests.Count(r => r.AbsolutePath == "/api/v1/subtitles") == 1, "explicit search calls the provider once");
            check(HasText(search, "Movie Release"), "provider results are listed");
            check(!handler.Requests.Any(r => r.AbsolutePath is "/api/v1/download"), "search alone downloads nothing");

            // 5. Download is explicit, caches the file and remembers it for the title.
            Click(Find<Button>(search, b => b.Content as string == "Download & Use")!);
            await pump(() => searchResult.IsCompleted, 5000);
            var downloaded = searchResult.IsCompleted ? await searchResult : null;
            check(downloaded is not null && File.Exists(downloaded) && Path.GetFileName(downloaded) == "MyraSub-4242.srt", "download returns the cached file");

            var again = new OnlineSubtitlesDialog(services, media, provider);
            _ = again.ShowDialog<string?>(Owner);
            await pump(() => Find<Button>(again, b => b.Content as string == "MyraSub-4242.srt") is not null, 5000);
            check(Find<Button>(again, b => b.Content as string == "MyraSub-4242.srt") is not null, "the downloaded subtitle is offered again for the same title without a search");
            check(handler.Requests.Count(r => r.AbsolutePath == "/api/v1/subtitles") == 1, "reopening does not search again");
            again.Close();
        }
        finally
        {
            OnlineSubtitlesDialog.OpenUrl = previousOpen;
            services.Secrets.SaveSubtitleCredentials(new SubtitleCredentials());
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task IconChecks(AppServices services, string output, Action<bool, string> check, Func<Func<bool>, int, Task> pump)
    {
        var settings = services.Store.Settings;
        SettingsHooks.CreateAiSettings = null;
        var window = new SettingsWindow(settings, services.PlayerState, services) { Height = 1700 };
        window.Show();
        await pump(() => false, 400);
        check(HasText(window, "Myra Icon") && HasText(window, "AI settings are not available"), "Settings shows the icon picker and the AI section fallback");
        var tiles = window.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name?.StartsWith("IconFamily", StringComparison.Ordinal) == true).ToList();
        check(tiles.Count == 4, "Settings offers four icon families");

        var orbit = tiles.First(t => t.Name == "IconFamilyOrbit");
        Click(orbit);
        await pump(() => false, 200);
        check(settings.IconFamily == "Orbit" && MyraAppearance.CurrentName == "orbit-light", "choosing Orbit applies orbit-light: " + MyraAppearance.CurrentName);
        check(window.Icon is not null && ReferenceEquals(window.Icon, MyraAppearance.CurrentIcon), "the Settings window icon is the chosen icon");
        check(AppStore.Load().Settings.IconFamily == "Orbit", "the family is saved in Myra.json");

        var other = new Window { Width = 200, Height = 100 };
        other.Show();
        await pump(() => other.Icon is not null, 2000);
        check(other.Icon is not null && ReferenceEquals(other.Icon, MyraAppearance.CurrentIcon), "a window opened later gets the chosen icon");

        var variant = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Glass"));
        variant.SelectedIndex = 3;
        await pump(() => false, 200);
        check(settings.IconVariant == "Glass" && MyraAppearance.CurrentName == "orbit-glass", "choosing Glass applies orbit-glass: " + MyraAppearance.CurrentName);
        check(ReferenceEquals(other.Icon, MyraAppearance.CurrentIcon), "an open window follows the icon change");
        check(AppStore.Load().Settings.IconVariant == "Glass", "the variant is saved in Myra.json");
        foreach (var family in new[] { "Cinema", "Minimal", "Signature" })
        {
            Click(tiles.First(t => t.Name == "IconFamily" + family));
            check(MyraAppearance.CurrentName == family.ToLowerInvariant() + "-glass", family + " glass asset loads");
        }
        variant.SelectedIndex = 2;
        check(MyraAppearance.CurrentName == "signature-dark", "Dark variant applies signature-dark");
        Capture(window, output, "17-settings-appearance.png");
        // Back to the defaults.
        variant.SelectedIndex = 0;
        check(settings.IconFamily == "Signature" && settings.IconVariant == "Automatic", "defaults restored");
        window.Close();
        other.Close();

        // The AI hook shows the control another part of the app supplies.
        SettingsHooks.CreateAiSettings = _ => new TextBlock { Text = "AI hook control" };
        var hooked = new SettingsWindow(settings, services.PlayerState, services) { Height = 1700 };
        hooked.Show();
        await pump(() => false, 300);
        check(HasText(hooked, "AI hook control") && !HasText(hooked, "AI settings are not available"), "the AI section shows the hook control");
        hooked.Close();
        SettingsHooks.CreateAiSettings = null;
    }

    /// In-process stand-in for the provider. Records every request path; no live network.
    private sealed class CountingHandler(Dictionary<string, (int Status, string Body)> replies) : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(10) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request.RequestUri!);
            var (status, body) = replies.TryGetValue(request.RequestUri!.AbsolutePath, out var value) ? value : (500, "");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
