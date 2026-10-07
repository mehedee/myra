using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

/// Personal Library export and import with a preview, then merge or replace.
public sealed class PersonalLibraryDialog : AppDialog
{
    private const long MaximumBackupBytes = 20_000_000;
    private static readonly FilePickerFileType JsonType = new("JSON backup") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    private readonly AppServices _services;
    private readonly PersonalLibraryTransfer _transfer;
    private readonly Button _export = new() { Content = "Export backup…" };
    private readonly Button _choose = new() { Content = "Choose backup…" };
    private readonly Button _done = Action("Done", isCancel: true);
    private readonly StackPanel _preview = new() { Spacing = 8, IsVisible = false };
    private readonly StackPanel _details = new() { Spacing = 6 };
    private readonly RadioButton _merge = new() { Content = "Merge with current library", IsChecked = true, GroupName = "mode" };
    private readonly RadioButton _replace = new() { Content = "Replace sources and personal library", GroupName = "mode" };
    private readonly TextBlock _modeHelp = Muted("");
    private readonly Button _apply = Action("Merge backup", accent: true, isDefault: true);
    private readonly Button _cancelPreview = new() { Content = "Cancel preview" };
    private readonly TextBlock _message = Message();
    private readonly TextBlock _error = Message();
    private PersonalImportPreview? _current;
    private bool _busy;

    public PersonalLibraryDialog(AppServices services) : base("Personal Library — Export / Import", 720, 640, resizable: true)
    {
        _services = services;
        _transfer = new PersonalLibraryTransfer(services.Store, services.Entertainment, () => services.PlayerState, ReplacePlayerState);
        _message.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3FB950"));
        _error.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0883E"));

        var root = new DockPanel { Margin = new Thickness(24) };
        var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(_message);
        bottom.Children.Add(_error);
        bottom.Children.Add(ButtonRow(_done));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var top = new StackPanel { Spacing = 12 };
        top.Children.Add(Title2("Export / Import"));
        top.Children.Add(new TextBlock { Text = PersonalLibraryTransfer.Explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75 });
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _export, _choose } });

        _preview.Children.Add(new Border { Height = 1, Background = Avalonia.Media.Brushes.Gray, Opacity = 0.4 });
        _preview.Children.Add(new TextBlock { Text = "Import preview", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        _preview.Children.Add(new ScrollViewer { Content = _details, MaxHeight = 260, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        _preview.Children.Add(_merge);
        _preview.Children.Add(_replace);
        _preview.Children.Add(_modeHelp);
        _preview.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children = { _cancelPreview, Place(_apply, 2) },
        });
        top.Children.Add(_preview);
        root.Children.Add(top);
        Content = root;

        _export.Click += async (_, _) => await ExportAsync();
        _choose.Click += async (_, _) => await ChooseAsync();
        _cancelPreview.Click += (_, _) => ClearPreview();
        _apply.Click += (_, _) => Apply();
        _done.Click += (_, _) => Close();
        _merge.IsCheckedChanged += (_, _) => UpdateMode();
        _replace.IsCheckedChanged += (_, _) => UpdateMode();
        UpdateMode();
    }

    private static Control Place(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    /// The player owns one PlayerPersonalState object. Copy the imported values into it.
    private void ReplacePlayerState(PlayerPersonalState imported)
    {
        var state = _services.PlayerState;
        state.Speed = imported.Speed;
        state.AudioLanguage = imported.AudioLanguage;
        state.SubtitleLanguage = imported.SubtitleLanguage;
        state.Autoplay = imported.Autoplay;
        state.AutomaticSkipping = imported.AutomaticSkipping;
        state.Markers = new Dictionary<string, PlayerSkipMarkers>(imported.Markers);
        state.Save();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _export.IsEnabled = _choose.IsEnabled = _apply.IsEnabled = _cancelPreview.IsEnabled = !busy;
        _done.IsEnabled = !busy;
    }

    private void Report(string? message, string? error)
    {
        Show(_message, message);
        Show(_error, error);
    }

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export personal library",
            SuggestedFileName = "Myra-Entertainment-Backup.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonType],
        });
        if (file is null) return;
        try
        {
            var data = _transfer.Export();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(data);
            Report($"Backup saved to {file.TryGetLocalPath() ?? file.Name}.", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidImportException)
        {
            Report(null, ex.Message);
        }
    }

    private async Task ChooseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a personal library backup",
            AllowMultiple = false,
            FileTypeFilter = [JsonType],
        });
        if (files.FirstOrDefault() is not { } file) return;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumBackupBytes) throw new InvalidImportException("Backup exceeds 20 MB.");
            }
            ShowPreview(PersonalLibraryTransfer.Preview(buffer.ToArray()));
            Report(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidImportException)
        {
            ClearPreview();
            Report(null, ex.Message);
        }
    }

    public void ShowPreview(PersonalImportPreview preview)
    {
        _current = preview;
        _details.Children.Clear();
        _details.Children.Add(new TextBlock
        {
            Text = $"{preview.Sources} sources • {preview.Watchlist} watchlist titles • {preview.Collections} collections\n"
                + $"{preview.Watched} watched titles • {preview.Followed} followed shows • {preview.History} playback records\n"
                + $"{preview.MatchCorrections} match corrections • {preview.MarkerConfigurations} skip marker configurations",
        });
        foreach (var source in preview.Archive.Sources)
        {
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(new TextBlock { Text = source.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            item.Children.Add(Muted(source.Url));
            _details.Children.Add(item);
        }
        var orange = Avalonia.Media.Color.Parse("#F0883E");
        if (preview.PreviousDownloadDirectory is { Length: > 0 } path)
        {
            _details.Children.Add(new TextBlock { Text = "Previous download directory: " + path, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            _details.Children.Add(new TextBlock
            {
                Text = "This path is informational. Reconnect your download directory in Settings on this computer. Downloaded files are not included in the backup.",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new Avalonia.Media.SolidColorBrush(orange),
            });
        }
        if (preview.LocalPathMarkers > 0)
            _details.Children.Add(new TextBlock
            {
                Text = $"{preview.LocalPathMarkers} local playback marker keys may need matching paths on this computer.",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new Avalonia.Media.SolidColorBrush(orange),
            });
        _merge.IsChecked = true;
        _preview.IsVisible = true;
        UpdateMode();
    }

    private void ClearPreview()
    {
        _current = null;
        _preview.IsVisible = false;
    }

    private void UpdateMode()
    {
        var replace = _replace.IsChecked == true;
        _apply.Content = replace ? "Replace from backup" : "Merge backup";
        _modeHelp.Text = replace
            ? "Current sources and personal records will be replaced. A backup is saved before replacement. Downloaded files and credentials are preserved."
            : "Combine lists and collections. Newer playback records win; imported match corrections replace conflicting corrections. Imported playback and app preferences are applied.";
    }

    private void Apply()
    {
        if (_current is not { } preview || _busy) return;
        SetBusy(true);
        Report(null, null);
        try
        {
            // Import changes AppStore.Categories, so it runs on the UI thread.
            var result = _transfer.Import(preview.Archive, _replace.IsChecked == true);
            SettingsWindow.ApplyTheme(_services.Store.Settings.Theme);
            ClearPreview();
            Report(result.Message + $" Backup: {result.FullBackupPath}", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidImportException)
        {
            Report(null, "Import failed and your data was restored. " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
