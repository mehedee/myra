using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

/// Find Online Subtitles (OpenSubtitles.com). Closes with the cached subtitle path, or null.
public sealed class OnlineSubtitlesDialog : AppDialog
{
    public static readonly (string Code, string Label)[] Languages =
    [
        ("en", "English"), ("bn", "Bengali"), ("hi", "Hindi"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("ar", "Arabic"), ("ja", "Japanese"), ("ko", "Korean"), ("pt-br", "Portuguese (Brazil)"), ("", "All languages"),
    ];

    /// Last allowance reported by OpenSubtitles in this session.
    private static int? _remaining;

    private readonly AppServices _services;
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

    public OnlineSubtitlesDialog(AppServices services, GlobalSearchResult media) : base("Find Online Subtitles", 740, 680, resizable: true)
    {
        _services = services;
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
        Opened += async (_, _) => await SearchAsync(false);

        _busyPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 90 });
        _busyPanel.Children.Add(_busyText);
        UpdateQuota();

        var panel = new DockPanel { Margin = new Thickness(22) };
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(_more);
        bottom.Children.Add(_busyPanel);
        bottom.Children.Add(_message);
        bottom.Children.Add(Muted("OpenSubtitles.com • Only title/episode information is sent, never your media URL. Downloads use your provider allowance and are cached locally."));
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
        top.Children.Add(Title2("Find Online Subtitles"));
        top.Children.Add(new TextBlock { Text = media.Entry.Name, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
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
            var response = await _services.Subtitles.SearchAsync(identity, language, credentials, page, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
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
            var download = await _services.Subtitles.DownloadAsync(result, credentials, _services.SubtitleCache, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
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
