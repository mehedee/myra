using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record FilterChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

public sealed record RatingChoice(string Label, double Value)
{
    public override string ToString() => Label;
}

/// Home (macOS HomeView): filters, shelves and Pick Something. The projection runs off the UI thread,
/// debounced by 100 ms, and re-runs when filters or the store change.
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _projection;
    private HomeCatalogueProjection _prepared = new();
    private long _appliedRevision = -1;
    private HomeCatalogueFilter? _appliedFilter;
    private bool _applying;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        _genre = _genres[0];
        _language = _languages[0];
        _year = _years[0];
        _source = _sources[0];
        _rating = RatingChoices[0];
        Store.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(IsEnriching));
            OnPropertyChanged(nameof(UpdateMetadataLabel));
            Schedule();
        });
    }

    private EntertainmentStore Store => _main.Entertainment;

    public ObservableCollection<HomeShelfViewModel> Shelves { get; } = [];
    public ObservableCollection<HomeShelfViewModel> PersonalShelves { get; } = [];
    // Choice lists are replaced, never changed in place: a combo box loses its selection when its items reset.
    [ObservableProperty] private IReadOnlyList<FilterChoice> _genres = [AllChoice("genres")];
    [ObservableProperty] private IReadOnlyList<FilterChoice> _languages = [AllChoice("languages")];
    [ObservableProperty] private IReadOnlyList<FilterChoice> _years = [AllChoice("years")];
    [ObservableProperty] private IReadOnlyList<FilterChoice> _sources = [AllChoice("sources")];
    public IReadOnlyList<RatingChoice> RatingChoices { get; } = HomeCatalogueFilter.RatingChoices
        .Select(r => new RatingChoice(r == 0 ? "Any rating" : $"{(int)r}+ / 10", r)).ToList();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private FilterChoice _genre;
    [ObservableProperty] private FilterChoice _language;
    [ObservableProperty] private FilterChoice _year;
    [ObservableProperty] private FilterChoice _source;
    [ObservableProperty] private RatingChoice _rating;
    [ObservableProperty] private bool _hideWatched;
    [ObservableProperty] private bool _isPreparing;
    [ObservableProperty] private int _titleCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCatalogueEmpty), nameof(HasCatalogue))] private bool _hasTitles;
    [ObservableProperty] private bool _canPick;

    /// True once the first projection has run (Home shows its empty state only after that).
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCatalogueEmpty))] private bool _isReady;

    // Combo boxes bind to indexes: a selected record is lost when a combo box resets its items.
    public int GenreIndex { get => Index(Genres, Genre); set => Pick(Genres, value, c => Genre = c); }
    public int LanguageIndex { get => Index(Languages, Language); set => Pick(Languages, value, c => Language = c); }
    public int YearIndex { get => Index(Years, Year); set => Pick(Years, value, c => Year = c); }
    public int SourceIndex { get => Index(Sources, Source); set => Pick(Sources, value, c => Source = c); }
    public int RatingIndex
    {
        get => Rating is null ? 0 : Math.Max(0, RatingChoices.ToList().IndexOf(Rating));
        set
        {
            if (value >= 0 && value < RatingChoices.Count) Rating = RatingChoices[value];
        }
    }

    private static int Index(IReadOnlyList<FilterChoice> choices, FilterChoice? selected) =>
        selected is null ? 0 : Math.Max(0, choices.ToList().IndexOf(selected));

    private void Pick(IReadOnlyList<FilterChoice> choices, int index, Action<FilterChoice> set)
    {
        if (!_applying && index >= 0 && index < choices.Count) set(choices[index]);
    }

    public bool IsCatalogueEmpty => IsReady && !HasTitles;
    public bool HasCatalogue => HasTitles;
    public string? ErrorMessage => Store.ErrorMessage;
    public bool IsEnriching => Store.IsEnriching;
    public string UpdateMetadataLabel => Store.IsEnriching ? "Updating…" : "Update metadata";
    public string TitleCountText => $"{TitleCount.ToString("N0", CultureInfo.CurrentCulture)} titles";
    public string Attribution => HomeProjection.Attribution;
    public bool HasQuery => Query.Length > 0;

    // Combo boxes can briefly clear their selection (null) while their items are replaced; null means "All".
    public HomeCatalogueFilter Filter => new(Query.Trim(), Genre?.Value ?? "", Language?.Value ?? "", Year?.Value ?? "", Source?.Value ?? "",
        Rating?.Value ?? 0, HideWatched);

    /// Titles matching the current filters (the Pick Something pool).
    public IReadOnlyList<EntertainmentTitle> Filtered => _prepared.Filtered;

    private static FilterChoice AllChoice(string plural) => new($"All {plural}", "");

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Schedule();
    }

    partial void OnGenreChanged(FilterChoice value) => OnFilterChanged(value, nameof(GenreIndex));
    partial void OnLanguageChanged(FilterChoice value) => OnFilterChanged(value, nameof(LanguageIndex));
    partial void OnYearChanged(FilterChoice value) => OnFilterChanged(value, nameof(YearIndex));
    partial void OnSourceChanged(FilterChoice value) => OnFilterChanged(value, nameof(SourceIndex));
    partial void OnRatingChanged(RatingChoice value) => OnFilterChanged(value, nameof(RatingIndex));

    /// A combo box clears its selection (null) while its items are replaced; that is not a user choice.
    private void OnFilterChanged(object? value, string index)
    {
        OnPropertyChanged(index);
        if (!_applying && value is not null) Schedule();
    }
    partial void OnHideWatchedChanged(bool value) => Schedule();
    partial void OnTitleCountChanged(int value) => OnPropertyChanged(nameof(TitleCountText));

    /// Re-projects Home after 100 ms; a newer request cancels an older one.
    public void Schedule()
    {
        _projection?.Cancel();
        var cancellation = _projection = new CancellationTokenSource();
        var filter = Filter;
        var catalogue = Store.Catalogue;
        var personal = Store.Personal;
        var newEpisodes = Store.NewEpisodeIds;
        var revision = Store.ProjectionRevision;
        IsPreparing = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100, cancellation.Token);
            var projection = HomeProjection.Prepare(catalogue, personal, filter, cancellation.Token);
            var shelves = HomeProjection.Shelves(projection, personal, newEpisodes);
            return (projection, shelves);
        }, cancellation.Token).ContinueWith(task =>
        {
            if (cancellation.IsCancellationRequested || !task.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                Apply(task.Result.projection, task.Result.shelves, catalogue.Count, personal, filter, revision);
                IsPreparing = false;
            });
        }, TaskScheduler.Default);
    }

    /// Waits until the latest scheduled projection is on screen (for checks).
    public async Task WaitForProjectionAsync(int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (IsPreparing && Environment.TickCount64 - started < timeoutMs) await Task.Delay(20);
    }

    private void Apply(HomeCatalogueProjection projection, List<HomeShelf> shelves, int count, EntertainmentPersonalData personal,
        HomeCatalogueFilter filter, long revision)
    {
        _prepared = projection;
        TitleCount = count;
        HasTitles = count > 0;
        // A combo box bound before its items exist shows nothing until its items reset once.
        var first = !IsReady;
        IsReady = true;
        CanPick = EntertainmentPick.CanPick(projection.Filtered, personal.Watched);
        var (genre, language, year, source) = (filter.Genre, filter.Language, filter.Year, filter.Source);
        _applying = true;
        Genres = Choices(Genres, projection.Genres, "genres", genre, first);
        Languages = Choices(Languages, projection.Languages, "languages", language, first);
        Years = Choices(Years, projection.Years, "years", year, first);
        Sources = Choices(Sources, projection.Sources, "sources", source, first);
        // Re-assert the selections so every combo box shows its choice after its items changed.
        SetSelections(genre, language, year, source);
        _applying = false;
        if (revision == _appliedRevision && filter == _appliedFilter) return;
        _appliedRevision = revision;
        _appliedFilter = filter;
        Sync(Shelves, shelves, personal);
        Sync(PersonalShelves, shelves.Where(s => s.Kind is HomeShelfKind.Watchlist or HomeShelfKind.Collection).ToList(), personal, expanded: true);
    }

    private static IReadOnlyList<FilterChoice> Choices(IReadOnlyList<FilterChoice> current, IReadOnlyList<string> values, string plural, string selected, bool force)
    {
        var wanted = values.Where(v => v.Length > 0).Select(v => new FilterChoice(v, v)).Prepend(AllChoice(plural)).ToList();
        // Keep the chosen filter even when no title currently offers it.
        if (selected.Length > 0 && !values.Contains(selected)) wanted.Add(new FilterChoice(selected, selected));
        return !force && current.SequenceEqual(wanted) ? current : wanted;
    }

    private void SetSelections(string genre, string language, string year, string source)
    {
        Genre = Genres.First(c => c.Value == genre);
        Language = Languages.First(c => c.Value == language);
        Year = Years.First(c => c.Value == year);
        Source = Sources.First(c => c.Value == source);
        Rating ??= RatingChoices[0];
        foreach (var name in new[] { nameof(GenreIndex), nameof(LanguageIndex), nameof(YearIndex), nameof(SourceIndex), nameof(RatingIndex) })
            OnPropertyChanged(name);
    }

    private void Sync(ObservableCollection<HomeShelfViewModel> target, List<HomeShelf> shelves, EntertainmentPersonalData personal, bool expanded = false)
    {
        var existing = target.ToDictionary(s => s.Key);
        var wanted = new List<HomeShelfViewModel>();
        foreach (var shelf in shelves)
        {
            var key = shelf.Kind + "|" + (shelf.CollectionId?.ToString() ?? shelf.Name);
            if (!existing.TryGetValue(key, out var model)) model = new HomeShelfViewModel(this, key) { IsExpanded = expanded };
            model.Update(shelf, personal, Store.NewEpisodeIds);
            wanted.Add(model);
        }
        if (!target.SequenceEqual(wanted))
        {
            target.Clear();
            foreach (var model in wanted) target.Add(model);
        }
    }

    // ---------- Commands ----------

    [RelayCommand]
    private void Reset()
    {
        Query = "";
        Genre = Genres[0];
        Language = Languages[0];
        Year = Years[0];
        Source = Sources[0];
        Rating = RatingChoices[0];
        HideWatched = false;
    }

    [RelayCommand] private void ClearQuery() => Query = "";

    [RelayCommand]
    private async Task PickSomethingAsync()
    {
        if (_main.Windows is not { } windows) return;
        using var pick = new PickViewModel(_main, _prepared.Filtered, Filter.Description);
        _main.LastPick = pick;
        var chosen = await windows.ShowPickAsync(pick);
        if (chosen is not null) await _main.StartTitleAsync(chosen);
    }

    [RelayCommand]
    private async Task UpdateMetadataAsync()
    {
        var task = Store.EnrichAsync();
        OnPropertyChanged(nameof(IsEnriching));
        OnPropertyChanged(nameof(UpdateMetadataLabel));
        try
        {
            await task;
        }
        catch (Exception error)
        {
            _main.ShowToast("Metadata update failed: " + error.Message);
        }
        OnPropertyChanged(nameof(IsEnriching));
        OnPropertyChanged(nameof(UpdateMetadataLabel));
    }

    [RelayCommand]
    private async Task ManageCollectionsAsync()
    {
        if (_main.Windows is { } windows) await windows.ShowCollectionsAsync(new HomeCollectionsViewModel(Store));
    }

    [RelayCommand] private Task ExportImportAsync() => _main.OpenPersonalLibraryAsync();

    [RelayCommand] private void ClearError() => Store.ClearError();

    // Card actions.

    [RelayCommand] private Task PlayAsync(HomeCardViewModel card) => _main.StartTitleAsync(card.Title);
    [RelayCommand] private void ToggleWatchlist(HomeCardViewModel card) => Store.ToggleWatchlist(card.Title.Id);
    [RelayCommand] private void ToggleWatched(HomeCardViewModel card) => Store.ToggleWatched(card.Title.Id);
    [RelayCommand] private void ToggleFollow(HomeCardViewModel card) => Store.ToggleFollow(card.Title.Id);
    [RelayCommand] private Task DetailsAsync(HomeCardViewModel card) => _main.ShowDetailsAsync(card.Title);

    public void ToggleCollection(HomeCardViewModel card, Guid collection) => Store.ToggleCollection(card.Title.Id, collection);

    public IReadOnlyList<EntertainmentCollection> Collections => Store.Personal.Collections;
    public bool IsWatched(EntertainmentTitle title) => Store.Personal.Watched.Contains(title.Id);
    public bool IsFollowed(EntertainmentTitle title) => Store.Personal.Followed.Contains(title.Id);
}

/// One Home row. Rows with more than 60 titles show Show all / Show fewer.
public sealed partial class HomeShelfViewModel(HomeViewModel home, string key) : ObservableObject
{
    private IReadOnlyList<HomeCardViewModel> _all = [];

    public HomeViewModel Home => home;
    public string Key => key;
    public ObservableCollection<HomeCardViewModel> Cards { get; } = [];
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowAllLabel))] private bool _isExpanded;

    public bool IsEmpty => Count == 0;
    public bool HasCards => Count > 0;
    public bool CanExpand => Count > HomeShelf.VisibleLimit;
    public string ShowAllLabel => IsExpanded ? "Show fewer" : "Show all";

    public void Update(HomeShelf shelf, EntertainmentPersonalData personal, IReadOnlySet<string> newEpisodes)
    {
        Name = shelf.Name;
        Subtitle = shelf.Subtitle;
        EmptyMessage = shelf.EmptyMessage;
        Count = shelf.Titles.Count;
        _all = shelf.Titles.Select(t => new HomeCardViewModel(home, t, personal, newEpisodes)).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CanExpand));
        Fill();
    }

    partial void OnIsExpandedChanged(bool value) => Fill();

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void Fill()
    {
        var visible = IsExpanded ? _all : _all.Take(HomeShelf.VisibleLimit).ToList();
        Cards.Clear();
        foreach (var card in visible) Cards.Add(card);
    }
}

/// Poster card (macOS EntertainmentCard).
public sealed class HomeCardViewModel(HomeViewModel home, EntertainmentTitle title, EntertainmentPersonalData personal, IReadOnlySet<string> newEpisodes)
{
    public HomeViewModel Home => home;
    public EntertainmentTitle Title => title;
    public string DisplayName => title.DisplayName;
    public Uri? PosterUrl => title.PosterUrl;
    public bool IsSeries => title.Kind == EntertainmentKind.Series;
    public bool IsMovie => !IsSeries;
    public string YearOrKind => title.Year ?? (IsSeries ? "Series" : "Movie");
    public bool HasRating => title.Metadata is not null;
    public string RatingText => title.Metadata?.Rating.ToString("0.0", CultureInfo.CurrentCulture) ?? "";
    public string RatingTip => $"TMDB rating from {title.Metadata?.VoteCount ?? 0} votes";
    public string SourcesText => string.Join(", ", title.Versions.Select(v => v.Media.CategoryName).Distinct().Order(StringComparer.CurrentCulture));
    public bool IsWatched { get; } = personal.Watched.Contains(title.Id);
    public bool InWatchlist { get; } = personal.Watchlist.Contains(title.Id);
    public string WatchlistTip => InWatchlist ? "Remove from Watchlist" : "Add to Watchlist";
    public bool HasNewEpisodes { get; } = newEpisodes.Count > 0 && title.Versions.Any(v => newEpisodes.Contains(FollowedEpisodes.EpisodeId(v)));
    public bool HasResume => title.ResumeVersion is { Duration: > 0 };
    public double ResumePercent => title.ResumeVersion is { Duration: > 0 } r ? Math.Min(100, 100 * r.ProgressSeconds / r.Duration) : 0;
    public string ResumeText => title.ResumeVersion is { } r ? $"Resume at {(int)(r.ProgressSeconds / 60)} min" : "";
    public string PlayLabel => title.ResumeVersion is null ? "Play" : "Resume";
}

/// Personal Collections window and the followed-series notification opt-in.
public sealed partial class HomeCollectionsViewModel : ObservableObject, IDisposable
{
    private readonly EntertainmentStore _store;

    public HomeCollectionsViewModel(EntertainmentStore store)
    {
        _store = store;
        _store.Changed += OnChanged;
        Load();
    }

    public ObservableCollection<HomeCollectionRow> Collections { get; } = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CreateCommand))] private string _newName = "";
    [ObservableProperty] private string? _errorMessage;

    public bool Notifications
    {
        get => _store.Personal.Preferences.Notifications;
        set
        {
            if (value != Notifications) _store.SetNotifications(value);
            OnPropertyChanged();
        }
    }

    public string NotificationHint => OperatingSystem.IsLinux()
        ? "Following a show establishes its current episodes as the baseline. Desktop notifications use notify-send when it is installed."
        : "Following a show establishes its current episodes as the baseline. New episodes appear as a Myra notice.";

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Load);

    private void Load()
    {
        ErrorMessage = _store.ErrorMessage;
        Collections.Clear();
        foreach (var collection in _store.Personal.Collections) Collections.Add(new HomeCollectionRow(this, collection));
        OnPropertyChanged(nameof(Notifications));
    }

    private bool CanCreate() => NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        _store.CreateCollection(NewName);
        NewName = "";
    }

    internal void Rename(Guid id, string name) => _store.RenameCollection(id, name);
    internal void Delete(Guid id) => _store.DeleteCollection(id);

    /// Set by the window: asks before deleting.
    public Func<string, Task<bool>>? Confirm { get; set; }

    internal async Task DeleteAsync(HomeCollectionRow row)
    {
        if (Confirm is not null && !await Confirm($"Delete the collection \"{row.Name}\"? Only the collection is removed. Its movies and source files remain available."))
            return;
        Delete(row.Id);
    }

    public void Dispose() => _store.Changed -= OnChanged;
}

public sealed partial class HomeCollectionRow(HomeCollectionsViewModel owner, EntertainmentCollection collection) : ObservableObject
{
    public Guid Id => collection.Id;
    public string Name => collection.Name;
    public string CountText => $"{collection.TitleIds.Count} titles";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNotRenaming))] private bool _isRenaming;
    [ObservableProperty] private string _renameText = collection.Name;
    public bool IsNotRenaming => !IsRenaming;

    [RelayCommand] private void StartRename() => IsRenaming = true;
    [RelayCommand] private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void SaveRename()
    {
        if (RenameText.Trim().Length == 0) return;
        owner.Rename(Id, RenameText);
        IsRenaming = false;
    }

    [RelayCommand] private Task DeleteAsync() => owner.DeleteAsync(this);
}
