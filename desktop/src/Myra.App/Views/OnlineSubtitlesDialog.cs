using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

/// Find Subtitles. Free sources come first: nearby files, cached downloads, a manual file, the
/// OpenSubtitles.org web page. The OpenSubtitles.com search runs only when the user asks for it.
/// Closes with the subtitle path, or null.
public sealed class OnlineSubtitlesDialog : AppDialog
{
    public static readonly (string Code, string Label)[] Languages =
    [
        ("en", "English"), ("bn", "Bengali"), ("hi", "Hindi"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("ar", "Arabic"), ("ja", "Japanese"), ("ko", "Korean"), ("pt-br", "Portuguese (Brazil)"), ("", "All languages"),
    ];

    /// Last allowance reported by OpenSubtitles in this session.
    private static int? _remaining;

    /// Opens an https page in the default browser. Checks replace it.
    public static Action<string> OpenUrl { get; set; } = OpenLink;

    /// Lets the user pick a subtitle file. Checks replace it.
    public Func<Task<string?>>? PickFile { get; set; }

    private readonly AppServices _services;
    private readonly OpenSubtitlesService _provider;
    private readonly GlobalSearchResult _media;
    private readonly StackPanel _freeSources = new() { Spacing = 6 };
    private readonly ObservableCollection<OnlineSubtitleResult> _results = [];
    private readonly TextBox _name = new() { Watermark = "Movie or series title" };
    private readonly TextBox _year = new() { Watermark = "Year", Width = 80 };
    private readonly TextBox _season = new() { Watermark = "Season", Width = 80 };
    private readonly TextBox _episode = new() { Watermark = "Episode", Width = 80 };
    private readonly ComboBox _language = new() { ItemsSource = Languages.Select(l => l.Label).ToList(), SelectedIndex = 0, Width = 220 };
    private readonly Button _search = new() { Content = "Search" };
    private readonly Button _more = new() { Content = "More Results", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _download = Action("Download & Use", accent: true, isDefault: true);
    private readonly ListBox _list = new() { MinHeight = 220 };
    private readonly StackPanel _busyPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock _busyText = new();
    private readonly TextBlock _message = Message();
    private readonly TextBlock _quota = Muted("");
    private CancellationTokenSource? _cancellation;
    private MediaIdentity? _submitted;
    private string _submittedLanguage = "en";
    private int _page;
    private bool _busy;

    public OnlineSubtitlesDialog(AppServices services, GlobalSearchResult media, OpenSubtitlesService? provider = null)
        : base("Find Subtitles", 740, 780, resizable: true)
    {
        _services = services;
        _provider = provider ?? services.Subtitles;
        _media = media;
        _language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == services.Store.Settings.SubtitlePreferredLanguage));
        var identity = MediaIdentity.Parse(media.Entry.Name);
        _name.Text = identity.Title;
        _year.Text = identity.Year ?? "";
        _season.Text = identity.Season ?? "";
        _episode.Text = identity.Episode ?? "";

        _list.ItemsSource = _results;
        _list.ItemTemplate = new FuncDataTemplate<OnlineSubtitleResult>((result, _) => ResultRow(result));
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += async (_, _) => await DownloadAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null)
            {
                e.Handled = true;
                await DownloadAsync();
            }
        };
        foreach (var box in new[] { _name, _year, _season, _episode })
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await SearchAsync(false);
            };
        _search.Click += async (_, _) => await SearchAsync(false);
        _more.Click += async (_, _) => await SearchAsync(true);
        _download.Click += async (_, _) => await DownloadAsync();
        var cancel = Action("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close();
        Closing += (_, _) => _cancellation?.Cancel();
        Opened += async (_, _) => await DiscoverFreeSourcesAsync();

        _busyPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 90 });
        _busyPanel.Children.Add(_busyText);
        UpdateQuota();

        var panel = new DockPanel { Margin = new Thickness(22) };
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(_more);
        bottom.Children.Add(_busyPanel);
        bottom.Children.Add(_message);
        bottom.Children.Add(Muted("Search uses OpenSubtitles.com and starts only when you press Search. Only title/episode information is sent, never your media URL. Downloads use your provider allowance and are cached locally."));
        bottom.Children.Add(_quota);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(LinkButton("Subtitle Account / API Key", "https://www.opensubtitles.com/en/consumers"));
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        Grid.SetColumn(_download, 2);
        footer.Children.Add(_download);
        bottom.Children.Add(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(Title2("Find Subtitles"));
        top.Children.Add(new TextBlock { Text = media.Entry.Name, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        top.Children.Add(Muted("First check the player's embedded subtitle tracks. Local files and previously downloaded subtitles need no account or paid subscription."));
        var import = new Button { Content = "Import Subtitle…" };
        import.Click += async (_, _) => await ImportAsync();
        var web = new Button { Content = "Find on the Web" };
        web.Click += (_, _) => OpenUrl(WebSearchUrl().AbsoluteUri);
        top.Children.Add(Buttons(import, web));
        top.Children.Add(_freeSources);
        top.Children.Add(new Border { Height = 1, Opacity = 0.25, Background = Brushes.Gray, Margin = new Thickness(0, 4) });
        top.Children.Add(Heading("Optional Free-Account Search"));
        top.Children.Add(_name);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([_year, _season, _episode, _language, _search]);
        top.Children.Add(row);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_list);
        Content = panel;
        UpdateButtons();
    }

    private static StackPanel Buttons(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(controls);
        return row;
    }

    private Uri WebSearchUrl() =>
        SubtitleLocal.WebSearchUrl((_name.Text ?? "").Trim(), Blank(_year.Text), Blank(_season.Text), Blank(_episode.Text));

    /// Nearby files and cached downloads for this title. No network access.
    private async Task DiscoverFreeSourcesAsync()
    {
        var name = _media.Entry.Name;
        var url = _media.Entry.Url;
        var (nearby, cached) = await Task.Run(() =>
        {
            IReadOnlyList<string> cachedFiles;
            try
            {
                cachedFiles = _services.SubtitleCache.Cached(MediaIdentity.Parse(name));
            }
            catch (Exception ex) when (ex is SubtitleException or IOException or UnauthorizedAccessException)
            {
                cachedFiles = [];
            }
            return (SubtitleLocal.Nearby(url), cachedFiles);
        });
        _freeSources.Children.Clear();
        AddSource("Nearby Subtitle Files", nearby);
        AddSource("Cached Subtitles for This Title", cached);
    }

    private void AddSource(string heading, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        _freeSources.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold });
        foreach (var file in files)
        {
            var button = new Button { Content = Path.GetFileName(file), Tag = file, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(button, file);
            button.Click += (_, _) => UseFile(file);
            _freeSources.Children.Add(button);
        }
    }

    private async Task ImportAsync()
    {
        string? path;
        if (PickFile is { } pick)
        {
            path = await pick();
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose a subtitle file",
                AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Subtitles") { Patterns = [.. SubtitleLocal.Extensions.Select(e => "*" + e)] }],
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is not null) UseFile(path);
    }

    private void UseFile(string path)
    {
        if (!SubtitleLocal.IsUsable(path))
        {
            Show(_message, "Choose a supported subtitle file smaller than 5 MB.");
            return;
        }
        Close(path);
    }

    private static Control ResultRow(OnlineSubtitleResult? result)
    {
        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4) };
        if (result is null) return stack;
        stack.Children.Add(new TextBlock { Text = result.Release, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        stack.Children.Add(new TextBlock { Text = result.Filename, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = new List<string> { LanguageName(result.Language) };
        if (result.Fps is > 0) details.Add(result.Fps.Value.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
        details.Add($"{result.Downloads} downloads");
        if (result.Trusted) details.Add("Trusted");
        if (result.HearingImpaired) details.Add("Hearing impaired");
        stack.Children.Add(new TextBlock { Text = string.Join(" · ", details), FontSize = 12, Opacity = 0.65 });
        return stack;
    }

    private static string LanguageName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            return name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private void UpdateQuota() => Show(_quota, _remaining is { } n ? $"Downloads remaining on your allowance: {n}" : null);

    private void UpdateButtons()
    {
        _search.IsEnabled = !_busy;
        _more.IsEnabled = !_busy;
        _download.IsEnabled = !_busy && _list.SelectedItem is OnlineSubtitleResult;
    }

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        _busyPanel.IsVisible = busy;
        _busyText.Text = text;
        UpdateButtons();
    }

    private SubtitleCredentials? ReadCredentials()
    {
        try
        {
            return _services.Secrets.ReadSubtitleCredentials();
        }
        catch (SecretStoreException ex)
        {
            Show(_message, ex.Message);
            return null;
        }
    }

    private async Task SearchAsync(bool more)
    {
        if (_busy && !more) _cancellation?.Cancel();
        MediaIdentity? identity = more
            ? _submitted
            : new MediaIdentity((_name.Text ?? "").Trim(), Blank(_year.Text), Blank(_season.Text), Blank(_episode.Text));
        if (identity is null) return;
        var language = more ? _submittedLanguage : Languages[Math.Max(0, _language.SelectedIndex)].Code;
        var page = more ? _page + 1 : 1;
        if (ReadCredentials() is not { } credentials) return;
        if (!more)
        {
            _results.Clear();
            _more.IsVisible = false;
        }
        _submitted = identity;
        _submittedLanguage = language;
        Show(_message, null);
        _cancellation?.Cancel();
        var cancellation = _cancellation = new CancellationTokenSource();
        SetBusy(true, "Searching OpenSubtitles…");
        try
        {
            var response = await _provider.SearchAsync(identity, language, credentials, page, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var fallback = _services.Store.Settings.SubtitleFallbackLanguage;
            if (response.Results.Count == 0 && !more && fallback.Length > 0 && fallback != language)
            {
                response = await _provider.SearchAsync(identity, fallback, credentials, 1, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _submittedLanguage = fallback;
                var index = Array.FindIndex(Languages, l => l.Code == fallback);
                if (index >= 0) _language.SelectedIndex = index;
                if (response.Results.Count > 0) Show(_message, "No results in your first language; showing fallback-language results.");
            }
            var seen = _results.Select(r => r.Id).ToHashSet();
            foreach (var result in response.Results)
                if (seen.Add(result.Id)) _results.Add(result);
            _page = page;
            _more.IsVisible = response.HasMore;
            if (_results.Count == 0) Show(_message, "No subtitles found. Adjust the title, episode or language and search again.");
            else if (!more) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is SubtitleException or HttpRequestException or IOException)
        {
            if (!cancellation.IsCancellationRequested) Show(_message, Explain(ex));
        }
        if (!cancellation.IsCancellationRequested) SetBusy(false);
    }

    private async Task DownloadAsync()
    {
        if (_busy || _list.SelectedItem is not OnlineSubtitleResult result) return;
        if (ReadCredentials() is not { } credentials) return;
        Show(_message, null);
        _cancellation?.Cancel();
        var cancellation = _cancellation = new CancellationTokenSource();
        SetBusy(true, "Downloading selected subtitle…");
        try
        {
            var download = await _provider.DownloadAsync(result, credentials, _services.SubtitleCache, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            try
            {
                _services.SubtitleCache.Remember(result.Id, MediaIdentity.Parse(_media.Entry.Name));
            }
            catch (Exception ex) when (ex is SubtitleException or IOException or UnauthorizedAccessException)
            {
                // The subtitle is cached either way; only the per-title shortcut is lost.
            }
            if (download.Remaining is { } remaining)
            {
                _remaining = remaining;
                UpdateQuota();
            }
            Close(download.Path);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is SubtitleException or HttpRequestException or IOException)
        {
            if (!cancellation.IsCancellationRequested) Show(_message, Explain(ex));
        }
        if (!cancellation.IsCancellationRequested) SetBusy(false);
    }

    private static string Explain(Exception ex) => ex switch
    {
        SubtitleException { Kind: SubtitleErrorKind.Quota } e => e.Message,
        SubtitleException e => e.Message,
        HttpRequestException => "Subtitles are unavailable. Check your connection and try again.",
        _ => ex.Message,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
