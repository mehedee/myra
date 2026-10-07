using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

public sealed record SourceInput(string Name, string Url);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<DirectoryEntry> _allEntries = [];
    private Uri? _artwork;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _globalSearch;
    private CancellationTokenSource? _indexing;
    private CancellationTokenSource? _toast;
    private int _globalOffset;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Downloads.ErrorRaised += message => ErrorMessage = message;
        Downloads.Notice += ShowToast;
        Store.Batches.CollectionChanged += (_, _) => Downloads.UpdateTotals();
    }

    // Set by the view: dialogs and clipboard need a window.
    public Func<Category?, Task<SourceInput?>>? ShowSourceDialog { get; set; }
    public Func<string, Task<bool>>? Confirm { get; set; }
    public Func<string, Task>? CopyText { get; set; }
    public Func<Task>? ShowSettingsDialog { get; set; }
    public Action<PlayerViewModel>? ShowPlayer { get; set; }

    public AppServices Services => _services;
    public AppStore Store => _services.Store;
    public DownloadManager Downloads => _services.Downloads;
    public ObservableCollection<Category> Categories => Store.Categories;
    public ObservableCollection<DownloadBatch> Batches => Store.Batches;
    public ObservableCollection<EntryViewModel> Entries { get; } = [];
    public ObservableCollection<Breadcrumb> Breadcrumbs { get; } = [];
    public ObservableCollection<GlobalResultViewModel> GlobalResults { get; } = [];

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
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private string _indexStatus = "";
    [ObservableProperty] private bool _showDownloadDetails;

    public bool ShowingGlobalResults => IsGlobalScope && SearchText.Trim().Length > 0;
    public bool ShowingBrowser => !ShowingGlobalResults;
    public bool HasSources => Categories.Count > 0;
    public bool HasSelection => SelectedCount > 0;
    public string SearchWatermark => IsGlobalScope ? "Search all sources (3+ characters)" : "Filter this folder";
    public string SortNameLabel => SortField == DirectorySortField.Name ? (SortAscending ? "Name ▲" : "Name ▼") : "Name";
    public string SortDateLabel => SortField == DirectorySortField.Date ? (SortAscending ? "Date ▲" : "Date ▼") : "Date";
    public string ThemeLabel => $"Theme: {Store.Settings.Theme} (click to change)";

    public async Task StartAsync()
    {
        OnPropertyChanged(nameof(HasSources));
        await Downloads.RecoverAsync();
        if (Categories.FirstOrDefault() is { } first) SelectedCategory = first;
        RefreshIndex(manual: false, onlyIfDue: true);
    }

    // ---------- Sources ----------

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (value?.RootUrl is { } root) _ = NavigateAsync(root);
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
        OnPropertyChanged(nameof(HasSources));
        SelectedCategory = category;
        RefreshIndex(manual: false);
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
        RefreshIndex(manual: false);
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(Category? category)
    {
        category ??= SelectedCategory;
        if (category is null) return;
        if (Confirm is not null && !await Confirm($"Remove the source \"{category.Name}\"? Downloaded files are not deleted.")) return;
        Categories.Remove(category);
        SaveStore();
        OnPropertyChanged(nameof(HasSources));
        if (SelectedCategory == category)
        {
            SelectedCategory = Categories.FirstOrDefault();
            if (SelectedCategory is null)
            {
                Entries.Clear();
                Breadcrumbs.Clear();
                CurrentUrl = null;
            }
        }
        var roots = SearchRoots();
        await Task.Run(() => _services.Index.SynchronizeSources(roots));
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
        if (CurrentUrl is not null) await NavigateAsync(CurrentUrl);
    }

    [RelayCommand]
    private async Task OpenEntryAsync(EntryViewModel? item)
    {
        if (item is null) return;
        if (item.IsFolder) await NavigateAsync(item.Entry.Url);
        else if (item.IsVideo) Play(item.Entry);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowingGlobalResults));
        OnPropertyChanged(nameof(ShowingBrowser));
        if (IsGlobalScope) StartGlobalSearch();
        else ApplyFilter();
    }

    partial void OnIsGlobalScopeChanged(bool value)
    {
        if (value)
        {
            ApplyFilter();
            StartGlobalSearch();
        }
        else
        {
            _globalSearch?.Cancel();
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void SortBy(string field)
    {
        var requested = field == "Date" ? DirectorySortField.Date : DirectorySortField.Name;
        if (SortField == requested) SortAscending = !SortAscending;
        else
        {
            SortField = requested;
            SortAscending = requested == DirectorySortField.Name;
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
            var item = new EntryViewModel(entry) { IsSelected = selected.Contains(entry.Key) };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EntryViewModel.IsSelected)) UpdateSelectedCount();
            };
            Entries.Add(item);
        }
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount() =>
        SelectedCount = ShowingGlobalResults ? GlobalResults.Count(r => r.IsSelected) : Entries.Count(e => e.IsSelected);

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

    private void StartGlobalSearch()
    {
        _globalSearch?.Cancel();
        GlobalResults.Clear();
        CanLoadMoreGlobal = false;
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
        var search = _globalSearch = new CancellationTokenSource();
        _ = RunGlobalSearchAsync(query, search.Token, debounce: true);
    }

    private async Task RunGlobalSearchAsync(string query, CancellationToken token, bool debounce)
    {
        try
        {
            if (debounce) await Task.Delay(250, token);
            var offset = _globalOffset;
            var page = await Task.Run(() => _services.Index.Search(query, 500, offset), token);
            if (token.IsCancellationRequested) return;
            foreach (var result in page)
            {
                var item = new GlobalResultViewModel(result);
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(GlobalResultViewModel.IsSelected)) UpdateSelectedCount();
                };
                GlobalResults.Add(item);
            }
            _globalOffset += page.Count;
            CanLoadMoreGlobal = page.Count == 500;
            GlobalStatus = GlobalResults.Count == 0
                ? (IsIndexing ? "No matches yet. Indexing is still running…" : "No matching videos in the index.")
                : $"{GlobalResults.Count}{(CanLoadMoreGlobal ? "+" : "")} matching videos";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            GlobalStatus = error.Message;
        }
    }

    [RelayCommand]
    private Task LoadMoreGlobalAsync()
    {
        var search = _globalSearch ??= new CancellationTokenSource();
        return RunGlobalSearchAsync(SearchText.Trim(), search.Token, debounce: false);
    }

    // ---------- Index ----------

    [RelayCommand]
    private void RefreshIndexNow() => RefreshIndex(manual: true);

    public void RefreshIndex(bool manual, bool onlyIfDue = false)
    {
        _indexing?.Cancel();
        var indexing = _indexing = new CancellationTokenSource();
        _ = RunIndexAsync(manual, onlyIfDue, indexing.Token);
    }

    private async Task RunIndexAsync(bool manual, bool onlyIfDue, CancellationToken token)
    {
        var roots = SearchRoots();
        IsIndexing = true;
        try
        {
            var index = await Task.Run(() => _services.Index, token);
            await Task.Run(() => index.SynchronizeSources(roots), token);
            var due = await Task.Run(() => index.NeedsRefresh(roots), token);
            if (roots.Count == 0 || (onlyIfDue && !due))
            {
                IndexStatus = DescribeIndex(index.LastRefresh());
                return;
            }
            IndexStatus = "Indexing…";
            var mode = Store.Settings.IndexingMode;
            var last = DateTime.MinValue;
            var indexer = new LibraryIndexer(new GlobalSearchService(_services.Directory, 2));
            var snapshot = await indexer.RefreshAsync(roots, index, Guid.NewGuid(),
                snapshot =>
                {
                    if (DateTime.UtcNow - last < TimeSpan.FromMilliseconds(500)) return Task.CompletedTask;
                    last = DateTime.UtcNow;
                    var p = snapshot.Progress;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                            IndexStatus = $"Indexing… {p.SourcesCompleted}/{p.SourcesTotal} sources · {p.FoldersVisited} folders · {p.MatchesFound} videos";
                    });
                    return Task.CompletedTask;
                },
                concurrencyLimit: () => mode == IndexingMode.LowImpact || _services.IsPlaying ? 1 : 2,
                cancellationToken: token);
            var failures = snapshot.Failures.Count;
            var summary = $"Indexed {snapshot.Progress.MatchesFound} videos across {snapshot.Progress.FoldersVisited} folders.";
            if (failures > 0) summary += $" {failures} source(s) could not be fully refreshed; previous records were kept.";
            IndexStatus = failures > 0 ? summary : DescribeIndex(index.LastRefresh());
            if (manual) ShowToast(summary);
            if (ShowingGlobalResults) StartGlobalSearch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            IndexStatus = "Index refresh failed: " + error.Message;
        }
        finally
        {
            if (_indexing?.Token == token) IsIndexing = false;
        }
    }

    private static string DescribeIndex(DateTimeOffset? last) =>
        last is { } value ? $"Index updated {value.ToLocalTime():g}" : "Index not built yet";

    // ---------- Downloads ----------

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

    // ---------- Playback and actions ----------

    private GlobalSearchResult? ResultFor(DirectoryEntry entry)
    {
        if (SelectedCategory is not { RootUrl: { } root } category) return null;
        var relative = Uri.UnescapeDataString(entry.Url.AbsolutePath.StartsWith(root.AbsolutePath)
            ? entry.Url.AbsolutePath[root.AbsolutePath.Length..]
            : entry.Name);
        return new GlobalSearchResult(category.Id, category.Name, root, entry, relative, _artwork);
    }

    public void Play(DirectoryEntry entry)
    {
        if (ResultFor(entry) is not { } result) return;
        try
        {
            OpenPlayer(result, new PlaybackSequence(result, _allEntries, _artwork));
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
    }

    [RelayCommand]
    private void PlayEntry(EntryViewModel? item)
    {
        if (item?.IsVideo == true) Play(item.Entry);
    }

    [RelayCommand]
    private async Task PlayResultAsync(GlobalResultViewModel? item)
    {
        if (item is null) return;
        var result = item.Result;
        PlaybackSequence sequence;
        try
        {
            // The play queue is the video's own folder, not the search results.
            var listing = await _services.Directory.ListingAsync(result.Entry.Url.Parent(), new UrlBoundary(result.CategoryRoot));
            sequence = new PlaybackSequence(result, listing.Entries, listing.ArtworkUrl);
        }
        catch (Exception)
        {
            sequence = new PlaybackSequence(result, [], null);
        }
        OpenPlayer(result, sequence);
    }

    public void PlayLocal(string path)
    {
        var file = new Uri(Path.GetFullPath(path));
        var folder = new Uri(Path.GetDirectoryName(Path.GetFullPath(path)) + Path.DirectorySeparatorChar);
        var siblings = Directory.EnumerateFiles(folder.LocalPath)
            .Select(f => new DirectoryEntry(Path.GetFileName(f), new Uri(f), EntryKind.File))
            .ToList();
        var entry = new DirectoryEntry(Path.GetFileName(path), file, EntryKind.File);
        var result = new GlobalSearchResult(Guid.Empty, "Offline", folder, entry, entry.Name, null);
        try
        {
            OpenPlayer(result, new PlaybackSequence(result, siblings, null));
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
    }

    /// Plays a video given on the command line: an http(s) URL or a local file.
    public async void PlayTarget(string target)
    {
        if (File.Exists(target))
        {
            PlayLocal(target);
            return;
        }
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            ErrorMessage = $"Cannot play \"{target}\": not a video file or http(s) address.";
            return;
        }
        var folder = url.Parent();
        var entry = new DirectoryEntry(url.LastPathComponent(), url, EntryKind.File);
        var result = new GlobalSearchResult(Guid.Empty, folder.Host, folder, entry, entry.Name, null);
        PlaybackSequence sequence;
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
        OpenPlayer(result, sequence);
    }

    private PlayerViewModel? _player;

    private void OpenPlayer(GlobalSearchResult result, PlaybackSequence sequence)
    {
        if (_player is { IsClosed: false })
        {
            _player.Load(result, sequence);
            ShowPlayer?.Invoke(_player);
            return;
        }
        try
        {
            _player = new PlayerViewModel(_services);
        }
        catch (Exception error)
        {
            ErrorMessage = OperatingSystem.IsWindows()
                ? "The video player could not start (libVLC is missing): " + error.Message
                : "The video player needs libVLC. Arch: 'sudo pacman -S libvlc vlc-plugins-base vlc-plugins-video-output vlc-plugin-ffmpeg vlc-plugin-matroska vlc-plugin-pulse'. "
                  + "Debian/Ubuntu: 'sudo apt install libvlc5 vlc-plugin-base vlc-plugin-video-output'. (" + error.Message + ")";
            return;
        }
        ShowPlayer?.Invoke(_player);
        _player.Load(result, sequence);
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

    // ---------- Settings and messages ----------

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (ShowSettingsDialog is not null) await ShowSettingsDialog();
        Store.Settings.Clamp();
        SaveStore();
    }

    [RelayCommand]
    private void CycleTheme()
    {
        Store.Settings.Theme = Store.Settings.Theme.Next();
        App.ApplyTheme(Store.Settings.Theme);
        OnPropertyChanged(nameof(ThemeLabel));
        SaveStore();
        ShowToast("Theme: " + Store.Settings.Theme);
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
