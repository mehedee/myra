using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly (IndexingMode Mode, string Label)[] IndexingChoices =
        [(IndexingMode.Balanced, "Balanced"), (IndexingMode.LowImpact, "Low impact")];

    public SettingsWindow() : this(new AppSettings(), new PlayerPersonalState())
    {
    }

    public SettingsWindow(AppSettings settings, PlayerPersonalState player, AppServices? services = null)
    {
        InitializeComponent();
        DataContext = settings;
        BuildAppearance(settings);
        if (services is not null) BuildServices(services);

        IndexingBox.ItemsSource = IndexingChoices.Select(c => c.Label).ToList();
        IndexingBox.SelectedIndex = Array.FindIndex(IndexingChoices, c => c.Mode == settings.IndexingMode);
        IndexingBox.SelectionChanged += (_, _) => settings.IndexingMode = IndexingChoices[Math.Max(0, IndexingBox.SelectedIndex)].Mode;

        var audio = PlayerLanguagePreference.Choices;
        AudioBox.ItemsSource = audio.Select(c => c.Label).ToList();
        AudioBox.SelectedIndex = Math.Max(0, Array.FindIndex(audio, c => c.Code == player.AudioLanguage));
        AudioBox.SelectionChanged += (_, _) => player.AudioLanguage = audio[Math.Max(0, AudioBox.SelectedIndex)].Code;

        (string Code, string Label)[] subtitles = [.. PlayerLanguagePreference.Choices, ("off", "Off")];
        SubtitleBox.ItemsSource = subtitles.Select(c => c.Label).ToList();
        SubtitleBox.SelectedIndex = Math.Max(0, Array.FindIndex(subtitles, c => c.Code == player.SubtitleLanguage));
        SubtitleBox.SelectionChanged += (_, _) => player.SubtitleLanguage = subtitles[Math.Max(0, SubtitleBox.SelectedIndex)].Code;

        BrowseDownloads.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Download folder" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) settings.DownloadDirectory = path;
        };
        BrowseAria2.Click += async (_, _) =>
        {
            if (await PickExecutable("aria2c") is { } path) settings.Aria2PathOverride = path;
        };
        BrowseVlc.Click += async (_, _) =>
        {
            if (await PickExecutable("vlc") is { } path) settings.VlcPathOverride = path;
        };
        CloseButton.Click += (_, _) =>
        {
            try
            {
                player.Save();
            }
            catch (IOException)
            {
            }
            Close();
        };
    }

    private async Task<string?> PickExecutable(string name)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Locate {name}",
            FileTypeFilter = OperatingSystem.IsWindows()
                ? [new FilePickerFileType("Programs") { Patterns = [$"{name}.exe"] }]
                : null,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
