using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed class QueueFileRow(GlobalSearchResult media, bool isCurrent, Action play)
{
    public GlobalSearchResult Media => media;
    public string Name => media.Entry.Name;
    public string Quality => MediaIdentity.Quality(media.Entry.Name);
    public bool IsCurrent => isCurrent;
    public IRelayCommand PlayCommand { get; } = new RelayCommand(play);
}

/// Episodes / Files side panel beside the player. An indexed title shows all of its seasons;
/// otherwise the folder queue of the playing video is listed.
public sealed partial class QueueViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _heading = "Episodes";
    [ObservableProperty] private string _subheading = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEpisodes))] private DetailEpisodeListViewModel? _episodes;
    public bool HasEpisodes => Episodes is not null;
    public ObservableCollection<QueueFileRow> Files { get; } = [];
    public bool HasFiles => Files.Count > 0;

    public void Open(PlayerViewModel player)
    {
        if (player.Current is not { } current) return;
        Files.Clear();
        Episodes = null;
        var title = main.Entertainment.TitleContaining(current);
        if (title is not null && (title.Kind == EntertainmentKind.Series || title.Versions.Count > 1))
        {
            Heading = title.Kind == EntertainmentKind.Series ? "Episodes" : "Versions";
            Subheading = title.DisplayName;
            Episodes = new DetailEpisodeListViewModel(title.Versions, title.Kind == EntertainmentKind.Series, current.Entry.Url.AbsoluteUri,
                DetailViewModel.LastPlayedId(title, main.Entertainment.Personal), version =>
                {
                    IsOpen = false;
                    _ = main.StartChosenAsync(version.Media, version.ProgressSeconds >= 5 && version.Id != current.Entry.Url.AbsoluteUri ? version.ProgressSeconds : null);
                });
        }
        else
        {
            Heading = "Files";
            Subheading = current.CategoryName + (current.ParentPath.Length > 0 ? " › " + current.ParentPath.Replace("/", " › ") : "");
            var sequence = player.Sequence;
            foreach (var video in sequence?.Videos ?? [current])
                Files.Add(new QueueFileRow(video, video.Entry.Url == current.Entry.Url, () =>
                {
                    IsOpen = false;
                    if (sequence is not null) player.Load(video, sequence);
                }));
        }
        OnPropertyChanged(nameof(HasFiles));
        IsOpen = true;
    }

    [RelayCommand] private void Close() => IsOpen = false;
}

/// Player features whose logic lives in the shell: preferences, skip markers, the queue panel and the version chooser.
public sealed class ShellPlayerHooks(MainViewModel main) : IPlayerPreferencesHook, IPlayerSkipMarkersHook, IPlayerQueueHook, IPlayerVersionChooser
{
    public void OpenPreferences(PlayerViewModel player)
    {
        if (main.Windows?.OwnerWindow is { } owner) _ = main.Dialogs.ShowPlaybackPreferencesAsync(owner, player);
    }

    public bool AutomaticSkipping => main.Services.PlayerState.AutomaticSkipping;

    public PlayerSkipPrompt? ActiveSkip(GlobalSearchResult media, double elapsed, double duration)
    {
        var markers = PlayerMarkers.Current(main.Services.PlayerState, media);
        return PlayerMarkers.ActiveSkip(markers, elapsed, duration) is { } skip
            ? new PlayerSkipPrompt(skip.Label, skip.Range.Start, skip.Range.End)
            : null;
    }

    public void EditMarkers(PlayerViewModel player)
    {
        if (main.Windows?.OwnerWindow is { } owner) _ = main.Dialogs.ShowSkipMarkersAsync(owner, player);
    }

    public void OpenQueue(PlayerViewModel player) => main.Queue.Open(player);

    public async void Choose(PlayerViewModel player, IReadOnlyList<GlobalSearchResult> versions)
    {
        GlobalSearchResult? chosen = null;
        try
        {
            if (main.Windows?.OwnerWindow is { } owner) chosen = await main.Dialogs.ChooseVersionAsync(owner, versions);
        }
        catch (Exception error)
        {
            main.ShowToast(error.Message);
        }
        try
        {
            if (chosen is not null) player.ChooseVersion(chosen);
            else player.CancelVersionChoice();
        }
        catch (Exception error)
        {
            main.ShowToast(error.Message);
        }
    }
}
