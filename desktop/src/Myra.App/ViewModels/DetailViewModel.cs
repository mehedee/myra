using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.Core;

namespace Myra.App.ViewModels;

/// Title Details (macOS EntertainmentDetailView). Pick Something reuses it with a pick header.
public sealed partial class DetailViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly string _titleId;

    public DetailViewModel(MainViewModel main, EntertainmentTitle title, string? pickFilters = null)
    {
        _main = main;
        _titleId = title.Id;
        _title = main.Entertainment.Catalogue.FirstOrDefault(t => t.Id == title.Id) ?? title;
        PickFilters = pickFilters;
        Episodes = BuildEpisodes();
        main.Entertainment.Changed += OnStoreChanged;
    }

    /// Play (or Resume) was chosen. The host closes the window, then starts playback.
    public event Action<EntertainmentTitle>? PlayRequested;

    /// Done / Close.
    public event Action? CloseRequested;

    /// An AI tool was chosen in "Explore with Myra". The host closes Details and opens the AI workspace.
    public event Action<AiFeature>? AiRequested;

    /// Shows "Explore with Myra". The host sets it when it can open the AI workspace (not in Pick Something).
    public bool CanAskAi { get; init; }

    private EntertainmentTitle _title;

    public EntertainmentTitle Title
    {
        get => _title;
        private set
        {
            _title = value;
            OnPropertyChanged(string.Empty);
        }
    }

    [ObservableProperty] private DetailEpisodeListViewModel? _episodes;
    [ObservableProperty] private string? _message;

    public string? PickFilters { get; }
    public bool IsPick => PickFilters is not null;
    public string PickText => $"An unwatched title matching {PickFilters}.";
    public string CloseLabel => IsPick ? "Close" : "Done";

    private EntertainmentPersonalData Personal => _main.Entertainment.Personal;
    private EntertainmentMetadata? Metadata => Title.Metadata;

    public string DisplayName => Title.DisplayName;
    public string InfoLine => string.Join(" · ", new[] { Title.Year, Title.Kind == EntertainmentKind.Series ? "Series" : "Movie" }.OfType<string>());
    public Uri? PosterUrl => Title.PosterUrl;
    public bool IsSeries => Title.Kind == EntertainmentKind.Series;
    public bool HasMetadata => Metadata is not null;
    public bool LacksMetadata => Metadata is null;
    public string RatingText => Metadata is { } m ? $"TMDB {m.Rating.ToString("0.0", CultureInfo.CurrentCulture)} / 10 • {m.VoteCount} votes" : "";
    public string Overview => Metadata is { Overview.Length: > 0 } m ? m.Overview : "No synopsis available.";
    public string GenresText => Metadata is { } m ? string.Join(" · ", m.Genres) : "";
    public bool HasRelease => Metadata?.ReleaseDate is { Length: > 0 };
    public string ReleaseText => $"Released: {Metadata?.ReleaseDate}";
    public string LanguageText => $"Original language: {Metadata?.Language}";
    public string PlayLabel => Title.ResumeVersion is null ? "Play" : "Resume";
    public string WatchlistLabel => Personal.Watchlist.Contains(Title.Id) ? "Remove from Watchlist" : "Add to Watchlist";
    public string WatchedLabel => Personal.Watched.Contains(Title.Id) ? "Mark unwatched" : "Mark watched";
    public string FollowLabel => Personal.Followed.Contains(Title.Id) ? "Unfollow series" : "Follow series";
    public bool HasCollections => Personal.Collections.Count > 0;
    public string VersionsHeading => IsSeries ? "Available Episodes" : "Available Versions";
    public string SourcesText => string.Join(", ", Title.Versions.Select(v => v.Media.CategoryName).Distinct().Order(StringComparer.CurrentCulture));

    /// Collection names with a check mark for membership, for the collections menu.
    public IReadOnlyList<(Guid Id, string Label)> CollectionChoices => Personal.Collections
        .Select(c => (c.Id, (c.TitleIds.Contains(Title.Id) ? "✓ " : "") + c.Name)).ToList();

    private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (_main.Entertainment.Catalogue.FirstOrDefault(t => t.Id == _titleId) is { } current) Title = current;
        Message = _main.Entertainment.ErrorMessage;
        Episodes = BuildEpisodes();
        OnPropertyChanged(string.Empty);
    }

    private DetailEpisodeListViewModel BuildEpisodes()
    {
        var expanded = Episodes?.ExpandedSeasons;
        return new DetailEpisodeListViewModel(Title.Versions, IsSeries, null, LastPlayedId(Title, Personal), version =>
        {
            PlayRequested?.Invoke(Title with { Versions = [version] });
        }, expanded: expanded);
    }

    public static string? LastPlayedId(EntertainmentTitle title, EntertainmentPersonalData personal) =>
        title.Versions.Where(v => personal.History.ContainsKey(v.Id))
            .MaxBy(v => personal.History[v.Id].Updated)?.Id;

    [RelayCommand] private void Play() => PlayRequested?.Invoke(Title);
    [RelayCommand] private void Close() => CloseRequested?.Invoke();
    [RelayCommand] private void AskAi(AiFeature feature) => AiRequested?.Invoke(feature);
    [RelayCommand] private void ToggleWatchlist() => _main.Entertainment.ToggleWatchlist(Title.Id);
    [RelayCommand] private void ToggleWatched() => _main.Entertainment.ToggleWatched(Title.Id);
    [RelayCommand] private void ToggleFollow() => _main.Entertainment.ToggleFollow(Title.Id);
    [RelayCommand] private void ToggleCollection(Guid id) => _main.Entertainment.ToggleCollection(Title.Id, id);

    [RelayCommand]
    private void OpenTmdb()
    {
        if (Metadata is { } m) ShellLinks.Open($"https://www.themoviedb.org/{(IsSeries ? "tv" : "movie")}/{m.ProviderId}");
    }

    [RelayCommand]
    private async Task CorrectMatchAsync()
    {
        if (_main.Windows?.OwnerWindow is not { } owner) return;
        if (await _main.Dialogs.ShowCorrectMatchAsync(owner, Title)) Message = null;
    }

    public void Dispose() => _main.Entertainment.Changed -= OnStoreChanged;
}

/// Pick Something window state: the current pick (or none) and Another Pick.
public sealed partial class PickViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<EntertainmentTitle> _candidates;

    private readonly Action? _resetFilters;

    public PickViewModel(MainViewModel main, IReadOnlyList<EntertainmentTitle> candidates, string filters, Action? resetFilters = null)
    {
        _main = main;
        _candidates = candidates;
        _resetFilters = resetFilters;
        Filters = filters;
        AnotherPick();
    }

    public event Action<EntertainmentTitle>? PlayRequested;
    public event Action? CloseRequested;

    public string Filters { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasPick))] private DetailViewModel? _details;
    public bool IsEmpty => Details is null;
    public bool HasPick => Details is not null;

    /// The empty state offers Reset Home Filters when the caller can reset them.
    public bool CanResetFilters => _resetFilters is not null;

    /// IDs shown so far, newest last (for checks).
    public List<string> History { get; } = [];

    [RelayCommand]
    private void AnotherPick()
    {
        var previous = Details?.Title.Id;
        var watched = _main.Entertainment.Personal.Watched;
        var next = EntertainmentPick.Select(_candidates, watched, previous);
        if (Details is { } old)
        {
            old.PlayRequested -= OnPlay;
            old.CloseRequested -= OnClose;
            old.Dispose();
        }
        if (next is null)
        {
            Details = null;
            return;
        }
        History.Add(next.Id);
        var details = new DetailViewModel(_main, next, Filters);
        details.PlayRequested += OnPlay;
        details.CloseRequested += OnClose;
        Details = details;
    }

    [RelayCommand] private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void ResetFilters()
    {
        CloseRequested?.Invoke();
        _resetFilters?.Invoke();
    }

    private void OnPlay(EntertainmentTitle title) => PlayRequested?.Invoke(title);
    private void OnClose() => CloseRequested?.Invoke();

    public void Dispose() => Details?.Dispose();
}

/// "Choose an episode" / "Choose a version" for a Home title (macOS EntertainmentVersionChooser).
public sealed class DetailChooserViewModel
{
    public DetailChooserViewModel(EntertainmentTitle title, EntertainmentPersonalData personal)
    {
        Title = title;
        Heading = title.Kind == EntertainmentKind.Series ? "Choose an episode" : "Choose a version";
        Episodes = new DetailEpisodeListViewModel(title.Versions, title.Kind == EntertainmentKind.Series, null,
            DetailViewModel.LastPlayedId(title, personal), version => Chosen?.Invoke(version));
    }

    public EntertainmentTitle Title { get; }
    public string Heading { get; }
    public string DisplayName => Title.DisplayName;
    public DetailEpisodeListViewModel Episodes { get; }
    public event Action<EntertainmentVersion>? Chosen;
}

/// Shared season / episode / version list (macOS SeasonEpisodeList). Only the selected season starts expanded.
public sealed class DetailEpisodeListViewModel
{
    public DetailEpisodeListViewModel(
        IReadOnlyList<EntertainmentVersion> versions, bool isSeries, string? currentId, string? lastPlayedId,
        Action<EntertainmentVersion> choose, Func<EntertainmentVersion, bool>? isAvailable = null, ISet<string>? expanded = null)
    {
        IsSeries = isSeries;
        DetailVersionViewModel Row(EntertainmentVersion v) =>
            new(v, v.Id == lastPlayedId, isAvailable?.Invoke(v) ?? true, () => choose(v));
        if (isSeries)
        {
            var selected = versions.FirstOrDefault(v => v.Id == currentId) ?? versions.FirstOrDefault(v => v.Id == lastPlayedId) ?? versions.FirstOrDefault();
            var open = expanded ?? new HashSet<string> { selected?.Season?.ToString(CultureInfo.InvariantCulture) ?? "other" };
            ExpandedSeasons = open;
            Seasons = EntertainmentSeason.Seasons(versions).Select(season => new DetailSeasonViewModel(
                season, open,
                season.Episodes.Select(episode => new DetailEpisodeViewModel(
                    episode, episode.Versions.Any(v => v.Id == currentId), episode.Versions.Any(v => v.Id == lastPlayedId),
                    episode.Versions.Select(Row).ToList())).ToList())).ToList();
            Versions = [];
        }
        else
        {
            ExpandedSeasons = expanded ?? new HashSet<string>();
            Seasons = [];
            Versions = versions.Select(Row).ToList();
        }
    }

    public bool IsSeries { get; }
    public bool IsMovie => !IsSeries;
    public ISet<string> ExpandedSeasons { get; }
    public IReadOnlyList<DetailSeasonViewModel> Seasons { get; }
    public IReadOnlyList<DetailVersionViewModel> Versions { get; }
}

public sealed partial class DetailSeasonViewModel(EntertainmentSeason season, ISet<string> expanded, IReadOnlyList<DetailEpisodeViewModel> episodes) : ObservableObject
{
    public string Label => season.Label;
    public string CountText => $"{episodes.Count} {(episodes.Count == 1 ? "episode" : "episodes")}";
    public IReadOnlyList<DetailEpisodeViewModel> Episodes => episodes;

    public bool IsExpanded
    {
        get => expanded.Contains(season.Id);
        set
        {
            if (value) expanded.Add(season.Id);
            else expanded.Remove(season.Id);
            OnPropertyChanged();
        }
    }
}

public sealed class DetailEpisodeViewModel(EntertainmentEpisodeGroup episode, bool isCurrent, bool isLastPlayed, IReadOnlyList<DetailVersionViewModel> versions)
{
    private readonly EntertainmentVersion? _progress = episode.Versions.FirstOrDefault(v => v.ProgressSeconds >= 5);

    public string EpisodeLabel => episode.Episode is { } number ? $"Episode {number:00}" : episode.Label;
    public bool IsCurrent => isCurrent;
    public bool IsLastPlayed => isLastPlayed;
    public bool HasResume => _progress is not null;
    public string ResumeText => _progress is { } p ? "Resume at " + PlayerTime.Format(p.ProgressSeconds) : "";
    public bool HasProgressBar => _progress is { Duration: > 0 };
    public double ProgressPercent => _progress is { Duration: > 0 } p ? Math.Min(100, 100 * p.ProgressSeconds / p.Duration) : 0;
    public IReadOnlyList<DetailVersionViewModel> Versions => versions;
    public bool HasManyVersions => versions.Count > 1;
    public bool HasOneVersion => versions.Count == 1;
    public string VersionCountText => $"{versions.Count} versions";
    public DetailVersionViewModel? Single => versions.Count == 1 ? versions[0] : null;
    public string Tooltip => versions.FirstOrDefault()?.Name ?? "";
}

public sealed class DetailVersionViewModel
{
    public DetailVersionViewModel(EntertainmentVersion version, bool isLastPlayed, bool isAvailable, Action choose)
    {
        Version = version;
        IsLastPlayed = isLastPlayed;
        IsAvailable = isAvailable;
        ChooseCommand = new RelayCommand(choose, () => IsAvailable);
    }

    public EntertainmentVersion Version { get; }
    public bool IsLastPlayed { get; }
    public bool IsAvailable { get; }
    public bool IsMissing => !IsAvailable;
    public IRelayCommand ChooseCommand { get; }
    public string Name => Version.Media.Entry.Name;
    public string QualityText => Version.Quality.Length == 0 ? "Quality / encoding unknown" : Version.Quality;
    public string ShortQuality => Version.Quality.Length == 0 ? "Quality unknown" : Version.Quality;
    public string CategoryName => Version.Media.CategoryName;
    public string SourceText => $"Source: {Version.Media.CategoryName} • {(Version.Media.Entry.Size is { } size ? Format.Bytes(size) : "Size unknown")}";
    public string RelativePath => Version.Media.RelativePath;
    public string LocalPath => Version.Media.Entry.Url.IsFile ? Version.Media.Entry.Url.LocalPath : Version.Media.Entry.Url.AbsoluteUri;
    public bool HasSavedProgress => Version.ProgressSeconds >= 5;
    public string SavedProgressText => $"Saved progress: {(int)(Version.ProgressSeconds / 60)} min {(int)(Version.ProgressSeconds % 60)} sec";
    public string PlayLabel => HasSavedProgress ? "Resume" : "Play";
}
