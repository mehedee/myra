using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

/// Offline Library (macOS OfflineLibraryView): completed video downloads grouped by title.
/// Missing files stay visible with reconnection guidance. Nothing here deletes or changes files.
public sealed partial class OfflineViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private List<OfflineTitle> _titles = [];
    private CancellationTokenSource? _load;

    public OfflineViewModel(MainViewModel main)
    {
        _main = main;
        main.Entertainment.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (IsActive) Reload();
        });
    }

    public ObservableCollection<OfflineRowViewModel> Rows { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _hideMissing;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private int _visibleCount;
    [ObservableProperty] private bool _hasLoaded;

    /// The Offline section is on screen; store changes then refresh the list.
    public bool IsActive { get; set; }

    public bool IsEmpty => HasLoaded && VisibleCount == 0;

    partial void OnQueryChanged(string value) => ApplyFilter();
    partial void OnHideMissingChanged(bool value) => ApplyFilter();

    /// Re-reads completed downloads and checks each file off the UI thread.
    public Task Reload()
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        var items = _main.Store.Items.ToList();
        var personal = _main.Entertainment.Personal;
        IsLoading = true;
        return Task.Run(() => OfflineLibrary.Titles(items, personal), load.Token).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (load.IsCancellationRequested) return;
            IsLoading = false;
            _titles = task.IsCompletedSuccessfully ? task.Result : [];
            HasLoaded = true;
            ApplyFilter();
        }), TaskScheduler.Default);
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var title in OfflineLibrary.Filter(_titles, Query.Trim(), HideMissing).OrderBy(t => t.Title.DisplayName, NaturalStringComparer.Instance))
            Rows.Add(new OfflineRowViewModel(this, title));
        VisibleCount = Rows.Count;
    }

    internal async Task PlayAsync(OfflineTitle title)
    {
        var start = OfflineLibrary.Start(title);
        switch (start.Kind)
        {
            case OfflineStartKind.Choose:
                await ChooseAsync(title);
                break;
            case OfflineStartKind.Play when start.Path is { } path:
                _main.PlayLocal(path);
                break;
            default:
                _main.ShowToast(OfflineTitle.ReconnectHint);
                break;
        }
    }

    internal async Task ChooseAsync(OfflineTitle title)
    {
        if (_main.Windows is not { } windows) return;
        var chosen = await windows.ChooseOfflineVersionAsync(new OfflineChooserViewModel(title));
        if (chosen is null) return;
        if (!OfflineLibrary.IsPlayableLocalFile(chosen.Media.Entry.Url.LocalPath))
        {
            _main.ShowToast("Missing file. " + OfflineTitle.ReconnectHint + ".");
            return;
        }
        _main.PlayLocal(chosen.Media.Entry.Url.LocalPath);
    }
}

public sealed partial class OfflineRowViewModel(OfflineViewModel owner, OfflineTitle title)
{
    public OfflineTitle Title => title;
    public string DisplayName => title.Title.DisplayName;
    public string Summary => title.Summary;
    public bool IsUnavailable => title.IsUnavailable;
    public bool IsAvailable => !title.IsUnavailable;
    public bool IsSeries => title.Title.Kind == EntertainmentKind.Series && !title.IsUnavailable;
    public bool IsMovie => title.Title.Kind == EntertainmentKind.Movie && !title.IsUnavailable;
    public string ReconnectHint => OfflineTitle.ReconnectHint;

    [RelayCommand] private Task PlayAsync() => owner.PlayAsync(title);
    [RelayCommand] private Task FilesAsync() => owner.ChooseAsync(title);
}

/// Files and versions of a downloaded title, with file paths, Reveal and missing-file markers.
public sealed class OfflineChooserViewModel
{
    public OfflineChooserViewModel(OfflineTitle title)
    {
        Title = title;
        var available = title.Available.Select(v => v.Id).ToHashSet();
        Groups = EntertainmentEpisodeGroup.Groups(title.Title.Versions).Select(group => new OfflineChooserGroup(
            title.Title.Kind == EntertainmentKind.Series ? group.Label : null,
            group.Versions.Select(v => new OfflineChooserRow(
                new DetailVersionViewModel(v, false, available.Contains(v.Id), () => Chosen?.Invoke(v)))).ToList())).ToList();
    }

    public OfflineTitle Title { get; }
    public string DisplayName => Title.Title.DisplayName;
    public IReadOnlyList<OfflineChooserGroup> Groups { get; }
    public event Action<EntertainmentVersion>? Chosen;
}

public sealed record OfflineChooserGroup(string? Label, IReadOnlyList<OfflineChooserRow> Rows)
{
    public bool HasLabel => Label is not null;
}

public sealed class OfflineChooserRow(DetailVersionViewModel version)
{
    public DetailVersionViewModel Version => version;
    public string Path => version.LocalPath;
    public bool Exists => version.IsAvailable;
    public bool Missing => !version.IsAvailable;
    public IRelayCommand RevealCommand { get; } = new RelayCommand(() => Shell.Reveal(version.LocalPath));
}
