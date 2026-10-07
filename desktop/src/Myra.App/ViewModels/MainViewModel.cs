using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record SourceInput(string Name, string Url);

/// Sidebar sections, in macOS order. Settings opens a dialog instead of a section.
public enum ShellSection
{
    Home,
    Library,
    OfflineLibrary,
    Personal,
}

public sealed record ShellNavItem(ShellSection Section, string Label, string Icon);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellPlayerHooks _hooks;
    private List<DirectoryEntry> _allEntries = [];
    private Uri? _artwork;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _globalSearch;
    private CancellationTokenSource? _toast;
    private CancellationTokenSource? _playChoice;
    private int _globalOffset;
    private bool _wasShowingGlobal;
    private GlobalSearchResult? _inspectorBeforeSearch;

    public MainViewModel(AppServices services, IAppDialogs? dialogs = null)
    {
        _services = services;
        Dialogs = dialogs ?? new NullAppDialogs();
        _hooks = new ShellPlayerHooks(this);
        Home = new HomeViewModel(this);
        Offline = new OfflineViewModel(this);
        Inspector = new InspectorViewModel(services) { Play = PlayResultAsync };
        Queue = new QueueViewModel(this);
        _selectedNav = NavItems[0];
        Downloads.ErrorRaised += message => ErrorMessage = message;
        Downloads.Notice += ShowToast;
        Downloads.PropertyChanged += OnDownloadsChanged;
        Store.Batches.CollectionChanged += (_, _) => Downloads.UpdateTotals();
        Store.Items.CollectionChanged += (_, _) =>
        {
            if (Offline.IsActive) _ = Offline.Reload();
        };
        services.IndexRefresh.Mode = Store.Settings.IndexingMode;
        services.IndexRefresh.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateIndexState);
        services.IndexRefresh.IndexChanged += (_, _) => Dispatcher.UIThread.Post(OnIndexChanged);
        services.Entertainment.NewEpisodesDetected += (_, e) => Dispatcher.UIThread.Post(() => OnNewEpisodes(e));
        services.IndexFailed += message => Dispatcher.UIThread.Post(() => IndexStatus = message);
        if (services.StartupErrors.Count > 0) ErrorMessage = string.Join(" ", services.StartupErrors);
    }

    // Set by the view: dialogs and clipboard need a window.
    public Func<Category?, Task<SourceInput?>>? ShowSourceDialog { get; set; }
    public Func<string, Task<bool>>? Confirm { get; set; }
    public Func<string, Task>? CopyText { get; set; }

    /// Called whenever the embedded player is shown (checks use it to observe the player).
    public Action<PlayerViewModel>? ShowPlayer { get; set; }

    /// Hosts a stand-alone player window for command-line playback.
    public Action<PlayerViewModel>? ShowPlayerWindow { get; set; }

    public IAppDialogs Dialogs { get; set; }
    public IShellWindows? Windows { get; set; }

    public AppServices Services => _services;
    public AppStore Store => _services.Store;
    public DownloadManager Downloads => _services.Downloads;
    public EntertainmentStore Entertainment => _services.Entertainment;
    public HomeViewModel Home { get; }
    public OfflineViewModel Offline { get; }
    public InspectorViewModel Inspector { get; }
    public QueueViewModel Queue { get; }
    public ObservableCollection<Category> Categories => Store.Categories;
    public ObservableCollection<DownloadBatch> Batches => Store.Batches;
    public ObservableCollection<EntryViewModel> Entries { get; } = [];
    public ObservableCollection<Breadcrumb> Breadcrumbs { get; } = [];
    public ObservableCollection<GlobalResultViewModel> GlobalResults { get; } = [];
    public ObservableCollection<GlobalSearchFailure> IndexFailures { get; } = [];

    public IReadOnlyList<ShellNavItem> NavItems { get; } =
    [
        new(ShellSection.Home, "Home", "IconHome"),
        new(ShellSection.Library, "Library", "IconFolder"),
        new(ShellSection.OfflineLibrary, "Offline Library", "IconDrive"),
        new(ShellSection.Personal, "Watchlist & Collections", "IconBookmark"),
    ];

    /// The Pick Something window state last opened (for checks).
    public PickViewModel? LastPick { get; set; }

    [ObservableProperty] private ShellNavItem _selectedNav;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsLibrary), nameof(IsOffline), nameof(IsPersonal), nameof(SectionTitle), nameof(ShowInspector))]
    private ShellSection _section = ShellSection.Home;
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private Uri? _currentUrl;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowingGlobalResults), nameof(ShowingBrowser), nameof(SearchWatermark))]
    private bool _isGlobalScope = true;
    [ObservableProperty] private DirectorySortField _sortField = DirectorySortField.Name;
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private int _selectedCount;
    [ObservableProperty] private string _globalStatus = "";
    [ObservableProperty] private bool _canLoadMoreGlobal;
    [ObservableProperty] private bool _isGlobalSearching;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _isIndexPaused;
    [ObservableProperty] private string _indexStatus = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIndexCompletion), nameof(ShowIndexCompletion))] private string? _indexCompletionMessage;
    [ObservableProperty] private bool _showDownloadDetails;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowThumbnails), nameof(ShowList))] private bool _hasVideos;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowChrome), nameof(ShowDownloadFooter), nameof(ShowInspector), nameof(ShowBrowserArea), nameof(ShowIndexCompletion))]
    private bool _isPlayerVisible;
    [ObservableProperty] private PlayerViewModel? _player;

    public bool IsHome => Section == ShellSection.Home;
    public bool IsLibrary => Section == ShellSection.Library;
    public bool IsOffline => Section == ShellSection.OfflineLibrary;
    public bool IsPersonal => Section == ShellSection.Personal;
    public string SectionTitle => Section switch
    {
        ShellSection.Library => "Library",
        ShellSection.OfflineLibrary => "Offline Library",
        ShellSection.Personal => "Watchlist & Collections",
        _ => "Home",
    };

    public bool ShowingGlobalResults => IsGlobalScope && SearchText.Trim().Length > 0;
    public bool ShowingBrowser => !ShowingGlobalResults;
    public bool HasSources => Categories.Count > 0;
    public bool HasNoSources => Categories.Count == 0;
    public bool HasSelection => SelectedCount > 0;
    public bool ShowThumbnails => HasVideos;
    public bool ShowList => !HasVideos;
    public bool HasIndexFailures => IndexFailures.Count > 0;
    public string IndexFailureLabel => $"{IndexFailures.Count} unavailable";
    public bool HasIndexCompletion => IndexCompletionMessage is not null;
    public bool ShowIndexCompletion => HasIndexCompletion && ShowChrome;
    public bool IsPlayerFullscreen => IsPlayerVisible && Player?.IsFullscreen == true;

    /// Toolbar, sidebar, index status and download footer are hidden during fullscreen playback.
    public bool ShowChrome => !IsPlayerFullscreen;
    public bool ShowBrowserArea => !IsPlayerVisible;
    public bool ShowDownloadFooter => (Downloads.HasRecords || Downloads.IsScanning) && ShowChrome;
    public bool ShowInspector => IsLibrary && !IsPlayerVisible && Inspector.IsOpen;
    public string SearchWatermark => IsGlobalScope ? "Search all video files (3+ characters)" : "Search files and folders";
    public string SortNameLabel => SortField == DirectorySortField.Name ? (SortAscending ? "File / Folder Name ▲" : "File / Folder Name ▼") : "File / Folder Name";
    public string SortDateLabel => SortField == DirectorySortField.Date ? (SortAscending ? "Date Modified ▲" : "Date Modified ▼") : "Date Modified";
    public string ThemeLabel => $"Theme: {ThemeName(Store.Settings.Theme)}. Click for {ThemeName(Store.Settings.Theme.Next())}.";
    public string ThemeIcon => Store.Settings.Theme switch
    {
        AppThemeMode.Light => "IconSun",
        AppThemeMode.Dark => "IconMoon",
        _ => "IconTheme",
    };
    public string BrowserCountText => ShowingGlobalResults
        ? $"{GlobalResults.Count} matching videos" + (SelectedCount > 0 ? $" • {SelectedCount} selected" : "")
        : $"{Entries.Count} of {_allEntries.Count} items" + (SelectedCount > 0 ? $" • {SelectedCount} selected" : "");

    private static string ThemeName(AppThemeMode mode) => mode == AppThemeMode.System ? "System" : mode.ToString();

    public async Task StartAsync()
    {
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
        Inspector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InspectorViewModel.IsOpen)) OnPropertyChanged(nameof(ShowInspector));
        };
        await Downloads.RecoverAsync();
        if (Categories.FirstOrDefault() is { } first)
        {
            _selectingQuietly = true;
            SelectedCategory = first;
            _selectingQuietly = false;
            _ = NavigateAsync(first.RootUrl!);
        }
        // Home first from the saved snapshot, then only due folders.
        RefreshIndex(manual: false, prepare: () => Entertainment.ReloadAsync());
    }

    // ---------- Navigation ----------

    private bool _selectingQuietly;

    partial void OnSelectedNavChanged(ShellNavItem value)
    {
        if (value is not null) Section = value.Section;
    }

    partial void OnSectionChanged(ShellSection value)
    {
        if (SelectedNav?.Section != value) SelectedNav = NavItems.First(n => n.Section == value);
        Offline.IsActive = value == ShellSection.OfflineLibrary;
        if (Offline.IsActive) _ = Offline.Reload();
    }

    [RelayCommand] private void ShowSection(ShellSection section) => Section = section;

    // ---------- Sources ----------

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (_selectingQuietly || value?.RootUrl is not { } root) return;
        // Choosing a source in the sidebar browses it (macOS switches Global search back to Current).
        Section = ShellSection.Library;
        if (IsGlobalScope && SearchText.Length > 0) IsGlobalScope = false;
        _ = NavigateAsync(root);
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (ShowSourceDialog is null) return;
        var input = await ShowSourceDialog(null);
        if (input is null) return;
        var category = await ValidateSourceAsync(input);
        if (category is null) return;
        Categories.Add(category);
        SaveStore();
        NotifySources();
        SelectedCategory = category;
        if (category.SearchRoot is { } root) RefreshIndex(manual: false, IndexRefreshMode.Selected, [IndexScope.ForRoot(root)]);
    }

    [RelayCommand]
    private async Task EditSourceAsync(Category? category)
    {
        category ??= SelectedCategory;
        if (category is null || ShowSourceDialog is null) return;
        var input = await ShowSourceDialog(category);
        if (input is null) return;
        var validated = await ValidateSourceAsync(input);
        if (validated is null) return;
        category.Name = validated.Name;
        category.RootUrlString = validated.RootUrlString;
        SaveStore();
        if (SelectedCategory == category && category.RootUrl is { } root) await NavigateAsync(root);
        if (category.SearchRoot is { } searchRoot) RefreshIndex(manual: false, IndexRefreshMode.Selected, [IndexScope.ForRoot(searchRoot)]);
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(Category? category)
    {
        category ??= SelectedCategory;
        if (category is null) return;
        if (Confirm is not null && !await Confirm($"Remove the source \"{category.Name}\"? Downloads already added to the queue and downloaded files are not removed.")) return;
        Categories.Remove(category);
        SaveStore();
        NotifySources();
        if (SelectedCategory == category)
        {
            SelectedCategory = Categories.FirstOrDefault();
            if (SelectedCategory is null)
            {
                Entries.Clear();
                Breadcrumbs.Clear();
                _allEntries = [];
                CurrentUrl = null;
                HasVideos = false;
            }
        }
        // Cancels the running scan and removes only the deleted source's records.
        RefreshIndex(manual: false, IndexRefreshMode.Selected, []);
    }

    private void NotifySources()
    {
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
    }

    private async Task<Category?> ValidateSourceAsync(SourceInput input)
    {
        var url = NormalizedHttpUrl(input.Url);
        if (url is null)
        {
            ErrorMessage = "Enter an http:// or https:// directory address.";
            return null;
        }
        IsLoading = true;
        try
        {
            await _services.Directory.ValidateAsync(url);
        }
        catch (Exception error)
        {
            ErrorMessage = $"Could not open {url}: {error.Message}";
            return null;
        }
        finally
        {
            IsLoading = false;
        }
        var name = string.IsNullOrWhiteSpace(input.Name) ? url.LastPathComponent() : input.Name.Trim();
        return new Category { Name = name.Length == 0 ? url.Host : name, RootUrlString = url.AbsoluteUri };
    }

    internal static Uri? NormalizedHttpUrl(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("://")) trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0)
            return null;
        var builder = new UriBuilder(url) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    private List<GlobalSearchRoot> SearchRoots() => Categories.Select(c => c.SearchRoot).OfType<GlobalSearchRoot>().ToList();

    private void SaveStore()
    {
        try
        {
            Store.Save();
        }
        catch (IOException error)
        {
            ErrorMessage = "Could not save settings: " + error.Message;
        }
    }

    // ---------- Browsing ----------

    public async Task NavigateAsync(Uri url)
    {
        if (SelectedCategory?.RootUrl is not { } root) return;
        _navigation?.Cancel();
        var navigation = _navigation = new CancellationTokenSource();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var boundary = new UrlBoundary(root);
            var listing = await _services.Directory.ListingAsync(url, boundary, navigation.Token);
            if (navigation.IsCancellationRequested) return;
            CurrentUrl = listing.Url;
            _allEntries = listing.Entries.ToList();
            _artwork = listing.ArtworkUrl;
            UpdateBreadcrumbs(boundary.Root, listing.Url);
            ApplyFilter();
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
        finally
        {
            if (_navigation == navigation) IsLoading = false;
        }
    }

    private void UpdateBreadcrumbs(Uri root, Uri current)
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Breadcrumb(SelectedCategory?.Name ?? root.Host, root));
        var relative = current.AbsolutePath.Length > root.AbsolutePath.Length ? current.AbsolutePath[root.AbsolutePath.Length..] : "";
        var path = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = new Uri(path, segment + "/");
            Breadcrumbs.Add(new Breadcrumb(Uri.UnescapeDataString(segment), path));
        }
    }

    [RelayCommand]
    private Task OpenBreadcrumbAsync(Breadcrumb crumb) => NavigateAsync(crumb.Url);

    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        if (CurrentUrl is null || SelectedCategory?.RootUrl is not { } root || CurrentUrl.SameAs(root)) return;
        await NavigateAsync(CurrentUrl.Parent());
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (ShowingGlobalResults) StartGlobalSearch();
        else if (CurrentUrl is not null) await NavigateAsync(CurrentUrl);
    }

    /// Folder: open it. Video: show its information (macOS: clicking a thumbnail or title).
    [RelayCommand]
    private async Task OpenEntryAsync(EntryViewModel? item)
    {
        if (item is null) return;
        if (item.IsFolder) await NavigateAsync(item.Entry.Url);
        else if (item.IsVideo) Inspect(item.Entry);
    }

    [RelayCommand]
    private void InspectResult(GlobalResultViewModel? item)
    {
        if (item is not null) Inspector.Select(item.Result);
    }

    public void Inspect(DirectoryEntry entry)
    {
        if (entry.IsVideo && ResultFor(entry) is { } result) Inspector.Select(result);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowingGlobalResults));
        OnPropertyChanged(nameof(ShowingBrowser));
        TrackGlobalTransition();
        if (IsGlobalScope) StartGlobalSearch();
        else ApplyFilter();
    }

    partial void OnIsGlobalScopeChanged(bool value)
    {
        TrackGlobalTransition();
        if (value)
        {
            ApplyFilter();
            StartGlobalSearch();
        }
        else
        {
            _globalSearch?.Cancel();
            IsGlobalSearching = false;
            ApplyFilter();
        }
    }

    /// Clearing Global search restores the previous browser, filter and inspector state.
    private void TrackGlobalTransition()
    {
        var showing = ShowingGlobalResults;
        if (showing == _wasShowingGlobal) return;
        _wasShowingGlobal = showing;
        if (showing)
        {
            _inspectorBeforeSearch = Inspector.Selected;
        }
        else
        {
            if (_inspectorBeforeSearch is { } previous) Inspector.Select(previous);
            else Inspector.Close();
            _inspectorBeforeSearch = null;
        }
        OnPropertyChanged(nameof(BrowserCountText));
    }

    [RelayCommand] private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void SortBy(string field)
    {
        var requested = field == "Date" ? DirectorySortField.Date : DirectorySortField.Name;
        if (SortField == requested) SortAscending = !SortAscending;
        else
        {
            SortField = requested;
            SortAscending = true;
        }
        OnPropertyChanged(nameof(SortNameLabel));
        OnPropertyChanged(nameof(SortDateLabel));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Key).ToHashSet();
        var filter = IsGlobalScope ? "" : SearchText.Trim();
        var visible = _allEntries.Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        Entries.Clear();
        foreach (var entry in DirectoryEntrySorter.Sorted(visible, SortField, SortAscending))
        {
            var item = new EntryViewModel(entry, entry.IsFolder ? null : _artwork) { IsSelected = selected.Contains(entry.Key) };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EntryViewModel.IsSelected)) UpdateSelectedCount();
            };
            Entries.Add(item);
        }
        HasVideos = Entries.Any(e => e.IsVideo);
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = ShowingGlobalResults ? GlobalResults.Count(r => r.IsSelected) : Entries.Count(e => e.IsSelected);
        OnPropertyChanged(nameof(BrowserCountText));
    }

    [RelayCommand]
    private void SelectAll()
    {
        if (ShowingGlobalResults)
            foreach (var result in GlobalResults) result.IsSelected = true;
        else
            foreach (var entry in Entries) entry.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var entry in Entries) entry.IsSelected = false;
        foreach (var result in GlobalResults) result.IsSelected = false;
    }

    // ---------- Global search ----------

    private void StartGlobalSearch(bool keepSelection = false)
    {
        _globalSearch?.Cancel();
        var selected = keepSelection ? GlobalResults.Where(r => r.IsSelected).Select(r => r.Result.Id).ToHashSet() : [];
        GlobalResults.Clear();
        CanLoadMoreGlobal = false;
        IsGlobalSearching = false;
        _globalOffset = 0;
        UpdateSelectedCount();
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            GlobalStatus = "";
            return;
        }
        if (query.Length < 3)
        {
            GlobalStatus = "Enter at least 3 characters for Global search.";
            return;
        }
        if (Categories.Count == 0)
        {
            GlobalStatus = "Add a source to search.";
            return;
        }
        var search = _globalSearch = new CancellationTokenSource();
        _ = RunGlobalSearchAsync(query, search.Token, debounce: !keepSelection, selected);
    }

    private async Task RunGlobalSearchAsync(string query, CancellationToken token, bool debounce, IReadOnlySet<GlobalSearchResultId>? selected = null)
    {
        IsGlobalSearching = true;
        try
        {
            if (debounce) await Task.Delay(150, token);
            var offset = _globalOffset;
            var page = await Task.Run(() => _services.Index.Search(query, 500, offset), token);
            if (token.IsCancellationRequested) return;
            foreach (var result in page)
            {
                var item = new GlobalResultViewModel(result) { IsSelected = selected?.Contains(result.Id) == true };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(GlobalResultViewModel.IsSelected)) UpdateSelectedCount();
                };
                GlobalResults.Add(item);
            }
            _globalOffset += page.Count;
            CanLoadMoreGlobal = page.Count == 500;
            GlobalStatus = GlobalResults.Count == 0
                ? (IsIndexing ? "No matches yet. Indexing is still running…" : $"No video filename matched “{query}”.")
                : $"{GlobalResults.Count}{(CanLoadMoreGlobal ? "+" : "")} matching videos";
            UpdateSelectedCount();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            GlobalStatus = error.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested) IsGlobalSearching = false;
        }
    }

    [RelayCommand]
    private Task LoadMoreGlobalAsync()
    {
        if (!CanLoadMoreGlobal || IsGlobalSearching) return Task.CompletedTask;
        var search = _globalSearch ??= new CancellationTokenSource();
        return RunGlobalSearchAsync(SearchText.Trim(), search.Token, debounce: false);
    }

    [RelayCommand]
    private void CancelGlobalSearch()
    {
        _globalSearch?.Cancel();
        IsGlobalSearching = false;
    }

    // ---------- Index ----------

    /// The latest refresh (startup, footer button, source change). Checks await it.
    public Task IndexTask { get; private set; } = Task.CompletedTask;

    /// Footer refresh button: due folders, with a completion message.
    [RelayCommand]
    private void RefreshIndexNow()
    {
        // A manual refresh also retries an index that failed to open (for example, a locked file).
        _services.RetryIndex();
        RefreshIndex(manual: true);
    }

    [RelayCommand] private Task OpenIndexManagementAsync() => WithOwner(owner => Dialogs.ShowIndexManagementAsync(owner));
    [RelayCommand] private void CancelIndexing() => _services.IndexRefresh.Cancel();

    [RelayCommand]
    private void TogglePauseIndexing()
    {
        if (_services.IndexRefresh.IsPaused) _services.IndexRefresh.Resume();
        else _services.IndexRefresh.Pause();
    }

    [RelayCommand] private void DismissIndexCompletion() => IndexCompletionMessage = null;

    public void RefreshIndex(bool manual, IndexRefreshMode mode = IndexRefreshMode.Due, IReadOnlyList<IndexScope>? scopes = null, Func<Task>? prepare = null)
    {
        var roots = SearchRoots();
        _services.IndexRefresh.Mode = Store.Settings.IndexingMode;
        IsIndexing = true;
        var refresh = _services.IndexRefresh.RefreshAsync(roots, mode, manual, scopes, prepare: prepare);
        IndexTask = FinishRefreshAsync(refresh, manual);
    }

    private async Task FinishRefreshAsync(Task<IndexRefreshOutcome> refresh, bool manual)
    {
        IndexRefreshOutcome outcome;
        try
        {
            outcome = await refresh;
        }
        catch (Exception error)
        {
            IndexStatus = "Index refresh failed: " + error.Message;
            UpdateIndexState(keepStatus: true);
            return;
        }
        UpdateIndexState();
        if (outcome.Error is { } failure && !outcome.Cancelled) IndexStatus = "Index refresh failed: " + failure;
        if (manual && outcome.CompletionMessage is { } message) IndexCompletionMessage = message;
    }

    private void UpdateIndexState() => UpdateIndexState(keepStatus: false);

    private void UpdateIndexState(bool keepStatus)
    {
        var controller = _services.IndexRefresh;
        IsIndexing = controller.IsRefreshing;
        IsIndexPaused = controller.IsPaused;
        var p = controller.Progress;
        if (!keepStatus)
            IndexStatus = controller.IsRefreshing
                ? (controller.IsPaused ? "Indexing paused: " : "Indexing: ") + $"{p.SourcesCompleted}/{p.SourcesTotal} sources • {p.FoldersVisited} folders • {p.MatchesFound} videos"
                : _services.IndexError is { } indexError ? indexError
                : controller.LastRefresh is { } last ? $"Index updated {last.ToLocalTime():g}" : "Library index has not been refreshed";
        if (!IndexFailures.SequenceEqual(controller.Failures))
        {
            IndexFailures.Clear();
            foreach (var failure in controller.Failures) IndexFailures.Add(failure);
            OnPropertyChanged(nameof(HasIndexFailures));
            OnPropertyChanged(nameof(IndexFailureLabel));
        }
    }

    private void OnIndexChanged()
    {
        _ = Entertainment.ReloadAsync();
        if (ShowingGlobalResults && GlobalResults.Count <= 500) StartGlobalSearch(keepSelection: true);
    }

    private void OnNewEpisodes(NewEpisodesEventArgs e)
    {
        if (!e.Notify) return;
        if (!ShellNotifications.Show("New episodes available", e.Message)) ShowToast(e.Message);
    }

    // ---------- Downloads ----------

    private void OnDownloadsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadManager.HasRecords) or nameof(DownloadManager.IsScanning))
            OnPropertyChanged(nameof(ShowDownloadFooter));
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (ShowingGlobalResults)
        {
            var selected = GlobalResults.Where(r => r.IsSelected).Select(r => r.Result).ToList();
            foreach (var group in GlobalDownloadManifestBuilder.Groups(selected))
                await Downloads.DownloadManifestAsync(
                    group.Manifest.Count == 1 ? Path.GetFileName(group.Manifest[0].RelativePath) : $"{group.Manifest.Count} videos from {group.CategoryName}",
                    group.Manifest);
            ClearSelection();
        }
        else if (SelectedCategory?.RootUrl is { } root)
        {
            var selected = Entries.Where(e => e.IsSelected).Select(e => e.Entry).ToList();
            ShowDownloadDetails = true;
            await Downloads.DownloadSelectionAsync(selected, root);
            ClearSelection();
        }
    }

    [RelayCommand]
    private async Task DownloadEntryAsync(EntryViewModel? item)
    {
        if (item is null || SelectedCategory?.RootUrl is not { } root) return;
        await Downloads.DownloadSelectionAsync([item.Entry], root);
    }

    [RelayCommand]
    private async Task DownloadResultAsync(GlobalResultViewModel? item)
    {
        if (item is null) return;
        var group = GlobalDownloadManifestBuilder.Groups([item.Result]).Single();
        await Downloads.DownloadManifestAsync(item.Name, group.Manifest);
    }

    [RelayCommand] private void CancelScan() => Downloads.CancelScan();
    [RelayCommand] private Task PauseBatchAsync(DownloadBatch batch) => Downloads.PauseAsync(batch);
    [RelayCommand] private Task ResumeBatchAsync(DownloadBatch batch) => Downloads.ResumeAsync(batch);
    [RelayCommand] private Task CancelBatchAsync(DownloadBatch batch) => Downloads.CancelAsync(batch);
    [RelayCommand] private void ClearCompleted() => Downloads.ClearCompleted();

    [RelayCommand]
    private Task RemoveBatchAsync(DownloadBatch batch) => Downloads.DeleteRecordAsync(batch);

    [RelayCommand]
    private async Task DeletePartialFilesAsync(DownloadBatch batch)
    {
        if (Confirm is not null && !await Confirm($"Permanently delete the unfinished files of \"{batch.Title}\"? Completed files are kept."))
            return;
        await Downloads.RemovePartialDataAsync(batch);
    }

    [RelayCommand]
    private void RevealBatch(DownloadBatch batch)
    {
        var item = Downloads.ItemsFor(batch).FirstOrDefault();
        Shell.Reveal(item?.DestinationPath ?? Store.Settings.DownloadDirectory);
    }

    [RelayCommand]
    private void PlayBatch(DownloadBatch batch)
    {
        var item = Downloads.ItemsFor(batch).FirstOrDefault(i => i.Status == TransferStatus.Completed && File.Exists(i.DestinationPath));
        if (item is null)
        {
            ShowToast("No completed video in this download yet.");
            return;
        }
        PlayLocal(item.DestinationPath);
    }

    [RelayCommand]
    private void OpenDownloadFolder() => Shell.Reveal(Store.Settings.DownloadDirectory);

    [RelayCommand]
    private void ToggleDownloadDetails() => ShowDownloadDetails = !ShowDownloadDetails;

    // ---------- Home titles ----------

    public async Task ShowDetailsAsync(EntertainmentTitle title)
    {
        if (Windows is null) return;
        using var details = new DetailViewModel(this, title);
        var chosen = await Windows.ShowDetailsAsync(details);
        if (chosen is not null) await StartTitleAsync(chosen);
    }

    /// macOS HomeView.start: resume the latest unfinished file, play a single file, or choose an episode/version.
    /// Callers close Details or Pick before this runs, so the chooser never stacks on them.
    public async Task StartTitleAsync(EntertainmentTitle title, bool resume = true)
    {
        if (resume && title.ResumeVersion is { } latest)
        {
            await ResumeVersionAsync(latest);
            return;
        }
        if (title.Versions.Count == 0) return;
        if (title.Versions.Count == 1)
        {
            await StartChosenAsync(title.Versions[0].Media);
            return;
        }
        if (Windows is null) return;
        var chosen = await Windows.ChooseTitleVersionAsync(new DetailChooserViewModel(title, Entertainment.Personal));
        if (chosen is null) return;
        if (chosen.ProgressSeconds >= 5) await ResumeVersionAsync(chosen);
        else await StartChosenAsync(chosen.Media);
    }

    public async Task ResumeVersionAsync(EntertainmentVersion version)
    {
        var url = version.Media.Entry.Url;
        if (url.IsFile && !File.Exists(url.LocalPath))
        {
            ErrorMessage = "The previously played file is unavailable. Open Details and versions to choose another.";
            return;
        }
        await StartChosenAsync(version.Media, version.ProgressSeconds);
    }

    [RelayCommand]
    private Task OpenPersonalLibraryAsync() => OpenPersonalLibraryAsync(null);

    public async Task OpenPersonalLibraryAsync(object? _ = null)
    {
        await WithOwner(owner => Dialogs.ShowPersonalLibraryAsync(owner));
        NotifySources();
        _ = Entertainment.ReloadAsync();
        if (SelectedCategory is null && Categories.FirstOrDefault() is { } first) SelectedCategory = first;
    }

    [RelayCommand]
    private Task ManageCollectionsAsync() => Home.ManageCollectionsCommand.ExecuteAsync(null);

    // ---------- Playback ----------

    private GlobalSearchResult? ResultFor(DirectoryEntry entry)
    {
        if (SelectedCategory is not { RootUrl: { } root } category) return null;
        var relative = Uri.UnescapeDataString(entry.Url.AbsolutePath.StartsWith(root.AbsolutePath)
            ? entry.Url.AbsolutePath[root.AbsolutePath.Length..]
            : entry.Name);
        return new GlobalSearchResult(category.Id, category.Name, root, entry, relative, _artwork);
    }

    /// Directory browsing: shows the inspector for the video, then plays it (with the version chooser when needed).
    public Task Play(DirectoryEntry entry)
    {
        if (ResultFor(entry) is not { } result) return Task.CompletedTask;
        Inspector.Select(result);
        return PlayResultAsync(result);
    }

    [RelayCommand]
    private Task PlayEntryAsync(EntryViewModel? item) => item?.IsVideo == true ? Play(item.Entry) : Task.CompletedTask;

    [RelayCommand]
    private Task PlayResultItemAsync(GlobalResultViewModel? item) => item is null ? Task.CompletedTask : PlayResultAsync(item.Result);

    /// macOS AppCoordinator.play: alternate releases from the catalogue, else from the folder listing;
    /// several releases open the version chooser.
    public async Task PlayResultAsync(GlobalSearchResult result)
    {
        _playChoice?.Cancel();
        var choice = _playChoice = new CancellationTokenSource();
        var versions = Entertainment.Variants(result);
        if (versions.Count < 2 && !result.Entry.Url.IsFile)
        {
            try
            {
                var listing = await _services.Directory.ListingAsync(result.Entry.Url.Parent(), new UrlBoundary(result.CategoryRoot), choice.Token);
                versions = PlaybackChoices.FromListing(result, listing.Entries, listing.ArtworkUrl, Entertainment.Personal.MatchCorrections);
            }
            catch (Exception)
            {
                // An unreachable folder need not block a known file; playback reports reachability errors.
            }
        }
        if (choice.IsCancellationRequested) return;
        if (PlaybackChoices.For(versions, result) is { } choices)
        {
            var media = choices.Versions.Select(v => v.Media).ToList();
            // Highlighted release first: the most recently played, else the requested file.
            var preferred = media.FindIndex(m => m.Entry.Url == choices.PreferredUrl);
            if (preferred > 0) (media[0], media[preferred]) = (media[preferred], media[0]);
            GlobalSearchResult? chosen = null;
            if (Windows?.OwnerWindow is { } owner) chosen = await Dialogs.ChooseVersionAsync(owner, media);
            if (chosen is null || choice.IsCancellationRequested) return;
            await StartChosenAsync(chosen);
        }
        else
        {
            await StartChosenAsync(result);
        }
    }

    /// Plays one release. Its folder (the open one, or a fresh listing) is the play queue.
    public async Task StartChosenAsync(GlobalSearchResult result, double? resumeSeconds = null)
    {
        if (result.Entry.Url.IsFile)
        {
            PlayLocal(result.Entry.Url.LocalPath, resumeSeconds);
            return;
        }
        var parent = result.Entry.Url.Parent();
        PlaybackSequence sequence;
        if (SelectedCategory?.Id == result.CategoryId && CurrentUrl is { } current && current.SameAs(parent))
        {
            sequence = new PlaybackSequence(result, _allEntries, _artwork);
        }
        else
        {
            try
            {
                var listing = await _services.Directory.ListingAsync(parent, new UrlBoundary(result.CategoryRoot));
                sequence = new PlaybackSequence(result, listing.Entries, listing.ArtworkUrl);
            }
            catch (Exception)
            {
                sequence = new PlaybackSequence(result, [], null);
            }
        }
        OpenPlayer(result, sequence, resumeSeconds);
    }

    public void PlayLocal(string path, double? resumeSeconds = null)
    {
        try
        {
            var (result, sequence) = LocalMedia(path);
            OpenPlayer(result, sequence, resumeSeconds);
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
    }

    /// A downloaded file and its folder as the queue. Uses the Offline Library source so history matches.
    private static (GlobalSearchResult, PlaybackSequence) LocalMedia(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = new Uri(Path.GetDirectoryName(full) + Path.DirectorySeparatorChar);
        var siblings = Directory.EnumerateFiles(folder.LocalPath)
            .Select(f => new DirectoryEntry(Path.GetFileName(f), new Uri(f), EntryKind.File))
            .ToList();
        var entry = new DirectoryEntry(Path.GetFileName(full), new Uri(full), EntryKind.File, new FileInfo(full).Length);
        var result = new GlobalSearchResult(OfflineLibrary.LocalSourceId, "Downloaded", folder, entry, entry.Name, null);
        return (result, new PlaybackSequence(result, siblings, null));
    }

    /// Plays a video given on the command line (an http(s) URL or a local file) in a stand-alone player window.
    /// Fire-and-forget from startup: every failure becomes an error message instead of an app crash.
    public async void PlayTarget(string target)
    {
        try
        {
            await PlayTargetAsync(target);
        }
        catch (Exception error)
        {
            ErrorMessage = $"Cannot play \"{target}\": {error.Message}";
        }
    }

    private async Task PlayTargetAsync(string target)
    {
        GlobalSearchResult result;
        PlaybackSequence sequence;
        if (File.Exists(target))
        {
            (result, sequence) = LocalMedia(target);
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var folder = url.Parent();
            var entry = new DirectoryEntry(url.LastPathComponent(), url, EntryKind.File);
            result = new GlobalSearchResult(Guid.Empty, folder.Host, folder, entry, entry.Name, null);
            try
            {
                var listing = await _services.Directory.ListingAsync(folder, new UrlBoundary(folder));
                sequence = new PlaybackSequence(result, listing.Entries, listing.ArtworkUrl);
            }
            catch (Exception)
            {
                // Not a directory listing; play the single file.
                sequence = new PlaybackSequence(result, [], null);
            }
        }
        else
        {
            ErrorMessage = $"Cannot play \"{target}\": not a video file or http(s) address.";
            return;
        }
        if (CreatePlayer() is not { } player) return;
        ShowPlayerWindow?.Invoke(player);
        player.Load(result, sequence);
    }

    private PlayerViewModel? CreatePlayer()
    {
        PlayerViewModel player;
        try
        {
            player = new PlayerViewModel(_services);
        }
        catch (Exception error)
        {
            ErrorMessage = OperatingSystem.IsWindows()
                ? "The video player could not start (libVLC is missing): " + error.Message
                : "The video player needs libVLC. Arch: 'sudo pacman -S libvlc vlc-plugins-base vlc-plugins-video-output vlc-plugin-ffmpeg vlc-plugin-matroska vlc-plugin-pulse'. "
                  + "Debian/Ubuntu: 'sudo apt install libvlc5 vlc-plugin-base vlc-plugin-video-output'. (" + error.Message + ")";
            return null;
        }
        player.PlaybackPreferences = _hooks;
        player.SkipMarkers = _hooks;
        player.EpisodeQueue = _hooks;
        player.VersionChooser = _hooks;
        player.Notice += ShowToast;
        player.PlaybackEnded += media => Entertainment.MarkPlaybackEnded(media);
        player.PositionSaved += (media, seconds, duration, persist) => Entertainment.RecordPlayback(media, seconds, duration, persist: persist);
        player.OnlineSubtitlesRequested += media => _ = FindSubtitlesAsync(player, media);
        return player;
    }

    private async Task FindSubtitlesAsync(PlayerViewModel player, GlobalSearchResult media)
    {
        if (Windows?.OwnerWindow is not { } owner) return;
        try
        {
            var path = await Dialogs.ShowOnlineSubtitlesAsync(owner, media);
            if (path is not null) player.LoadSubtitleFile(path, media.Id);
        }
        catch (Exception error)
        {
            ShowToast(error.Message);
        }
    }

    private void OpenPlayer(GlobalSearchResult result, PlaybackSequence sequence, double? resumeSeconds = null)
    {
        if (Player is not { IsClosed: false })
        {
            if (CreatePlayer() is not { } created) return;
            created.BackToLibraryRequested += OnBackToLibrary;
            created.PropertyChanged += OnPlayerChanged;
            Player = created;
        }
        var player = Player;
        Queue.IsOpen = false;
        IsPlayerVisible = true;
        ShowPlayer?.Invoke(player);
        player.Load(result, sequence, resume: true, resumeSeconds: resumeSeconds);
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsFullscreen)) return;
        OnPropertyChanged(nameof(IsPlayerFullscreen));
        OnPropertyChanged(nameof(ShowChrome));
        OnPropertyChanged(nameof(ShowIndexCompletion));
        OnPropertyChanged(nameof(ShowDownloadFooter));
    }

    /// Stop, Back to Library or the last video finishing: the preserved browser, filters and inspector return.
    private void OnBackToLibrary()
    {
        Queue.IsOpen = false;
        IsPlayerVisible = false;
        OnPropertyChanged(nameof(IsPlayerFullscreen));
    }

    [RelayCommand]
    private async Task CopyEntryUrlAsync(EntryViewModel? item)
    {
        if (item is null || CopyText is null) return;
        await CopyText(item.Entry.Url.AbsoluteUri);
        ShowToast("Link copied");
    }

    [RelayCommand]
    private async Task CopyResultUrlAsync(GlobalResultViewModel? item)
    {
        if (item is null || CopyText is null) return;
        await CopyText(item.Result.Entry.Url.AbsoluteUri);
        ShowToast("Link copied");
    }

    [RelayCommand]
    private void OpenEntryInVlc(EntryViewModel? item)
    {
        if (item is not null) OpenInVlc(item.Entry.Url.AbsoluteUri);
    }

    [RelayCommand]
    private void OpenResultInVlc(GlobalResultViewModel? item)
    {
        if (item is not null) OpenInVlc(item.Result.Entry.Url.AbsoluteUri);
    }

    private void OpenInVlc(string target)
    {
        try
        {
            if (!Shell.OpenInVlc(target, Store.Settings.VlcPathOverride))
                ErrorMessage = "VLC is not installed. Install it from videolan.org, or use the built-in Play button.";
        }
        catch (Exception error)
        {
            ErrorMessage = "VLC could not start: " + error.Message;
        }
    }

    // ---------- Settings, dialogs and messages ----------

    private async Task WithOwner(Func<Avalonia.Controls.Window, Task> show)
    {
        if (Windows?.OwnerWindow is not { } owner) return;
        try
        {
            await show(owner);
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await WithOwner(owner => Dialogs.ShowSettingsAsync(owner));
        Store.Settings.Clamp();
        SaveStore();
        _services.IndexRefresh.Mode = Store.Settings.IndexingMode;
        App.ApplyTheme(Store.Settings.Theme);
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(ThemeIcon));
        // A new TMDB token starts enrichment after the reload.
        _ = Entertainment.ReloadAsync();
    }

    [RelayCommand] private Task OpenAboutAsync() => WithOwner(owner => Dialogs.ShowAboutAsync(owner));

    [RelayCommand]
    private void CycleTheme()
    {
        Store.Settings.Theme = Store.Settings.Theme.Next();
        App.ApplyTheme(Store.Settings.Theme);
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(ThemeIcon));
        SaveStore();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    public void ShowToast(string message)
    {
        _toast?.Cancel();
        var toast = _toast = new CancellationTokenSource();
        ToastMessage = message;
        _ = Task.Delay(TimeSpan.FromSeconds(3), toast.Token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (!toast.IsCancellationRequested) ToastMessage = null;
            }),
            TaskScheduler.Default);
    }
}
