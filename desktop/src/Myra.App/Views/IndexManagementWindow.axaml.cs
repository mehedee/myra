using Avalonia.Controls;
using Avalonia.Threading;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

public partial class IndexManagementWindow : Window
{
    private readonly AppServices? _services;
    private readonly IndexRefreshController? _controller;
    private IReadOnlyList<IndexFolderRow>? _shownRows;
    private bool _scheduleDirty;
    private bool _closed;
    private readonly HashSet<string> _selected = [];
    private readonly HashSet<string> _expanded = [];

    public IndexManagementWindow() : this(null)
    {
    }

    public IndexManagementWindow(AppServices? services)
    {
        InitializeComponent();
        DoneButton.Click += (_, _) => Close();
        if (services is null) return;
        _services = services;
        _controller = services.IndexRefresh;

        RefreshDueButton.Click += async (_, _) => await RunAsync(IndexRefreshMode.Due, null, false);
        RefreshSelectedButton.Click += async (_, _) => await RunAsync(IndexRefreshMode.Selected, SelectedScopes(), false);
        FullRescanButton.Click += async (_, _) =>
        {
            var confirmed = await new ConfirmDialog("Fully rescan the selected folders? Your published library remains available until the scan completes.").ShowDialog<bool>(this);
            if (confirmed) await RunAsync(IndexRefreshMode.Selected, SelectedScopes(), true);
        };
        PauseButton.Click += (_, _) =>
        {
            if (_controller.IsPaused) _controller.Resume();
            else _controller.Pause();
        };
        CancelButton.Click += (_, _) => _controller.Cancel();

        // StateChanged can fire on a worker thread.
        void OnState(object? s, EventArgs e) => Dispatcher.UIThread.Post(UpdateState);
        _controller.StateChanged += OnState;
        Closed += (_, _) =>
        {
            _closed = true;
            _controller.StateChanged -= OnState;
        };
        Opened += async (_, _) =>
        {
            UpdateState();
            await LoadAsync();
        };
    }

    private IReadOnlyList<GlobalSearchRoot> Roots =>
        _services!.Store.Categories.Select(c => c.SearchRoot).OfType<GlobalSearchRoot>().ToList();

    private List<IndexScope> SelectedScopes() =>
        IndexScope.Compact(AllNodes().Where(n => n.IsSelected).Select(n => n.Scope));

    private IEnumerable<IndexNode> AllNodes() =>
        (Tree.ItemsSource as IEnumerable<IndexNode> ?? []).SelectMany(n => n.Descendants());

    private async Task LoadAsync()
    {
        await _controller!.LoadFolderRowsAsync(Roots);
        if (!_closed) UpdateState();
    }

    private async Task RunAsync(IndexRefreshMode mode, IReadOnlyList<IndexScope>? scopes, bool full)
    {
        ResultText.IsVisible = false;
        try
        {
            var outcome = await _controller!.RefreshAsync(Roots, mode, manual: true, scopes: scopes, full: full);
            if (_closed) return;
            ResultText.Text = outcome.Error ?? outcome.CompletionMessage;
            ResultText.IsVisible = !string.IsNullOrEmpty(ResultText.Text);
        }
        catch (Exception ex) when (ex is LibraryIndexException or IOException or InvalidOperationException)
        {
            if (_closed) return;
            ResultText.Text = ex.Message;
            ResultText.IsVisible = true;
        }
        if (!_closed) await LoadAsync();
    }

    private void UpdateState()
    {
        if (_closed || _controller is null) return;
        var busy = _controller.IsRefreshing;
        ProgressPanel.IsVisible = busy;
        if (busy)
        {
            var progress = _controller.Progress;
            ProgressText.Text = (_controller.IsPaused
                ? "Paused — your saved library is available"
                : "Refreshing — your saved library is available")
                + $" · {progress.FoldersVisited} folders checked";
        }
        PauseButton.Content = _controller.IsPaused ? "Continue" : "Pause";
        RefreshDueButton.IsEnabled = !busy;
        UpdateSelectionButtons();
        if (!ReferenceEquals(_shownRows, _controller.FolderRows) || _scheduleDirty) Rebuild();
    }

    private void UpdateSelectionButtons()
    {
        var any = AllNodes().Any(n => n.IsSelected);
        var busy = _controller!.IsRefreshing;
        RefreshSelectedButton.IsEnabled = any && !busy;
        FullRescanButton.IsEnabled = any && !busy;
    }

    private void Rebuild()
    {
        foreach (var node in AllNodes())
        {
            if (node.IsSelected) _selected.Add(node.Id);
            else _selected.Remove(node.Id);
            if (node.IsExpanded) _expanded.Add(node.Id);
            else _expanded.Remove(node.Id);
        }
        _scheduleDirty = false;
        _shownRows = _controller!.FolderRows;
        var policies = _controller.Policies;
        var rows = _shownRows;
        var failures = _controller.Failures;

        IndexNode Make(IndexFolderRow row, string name, string? failure)
        {
            var node = new IndexNode(row, name, policies.ScheduleFor(row.Scope), policies.OverrideFor(row.Scope), failure, OnSchedule)
            {
                IsSelected = _selected.Contains(row.Id),
                IsExpanded = _expanded.Contains(row.Id),
            };
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IndexNode.IsSelected)) UpdateSelectionButtons();
            };
            return node;
        }

        var roots = new List<IndexNode>();
        foreach (var root in Roots)
        {
            var rootScope = IndexScope.ForRoot(root);
            var rootRow = rows.FirstOrDefault(r => r.Scope == rootScope) ?? new IndexFolderRow(rootScope, null, 0);
            var failure = failures.FirstOrDefault(f => f.CategoryId == root.Id)?.Message;
            var rootNode = Make(rootRow, root.Name, failure);
            var byParent = rows.Where(r => r.Scope.Root.Id == root.Id && r.Scope != rootScope)
                .ToLookup(r => Trim(ParentPath(r.Scope.Folder)));
            void AddChildren(IndexNode parent)
            {
                foreach (var row in byParent[Trim(parent.Scope.Folder.AbsolutePath)].OrderBy(r => r.Scope.Folder.AbsoluteUri, NaturalStringComparer.Instance))
                {
                    var child = Make(row, Uri.UnescapeDataString(LastSegment(row.Scope.Folder)), null);
                    parent.Children.Add(child);
                    AddChildren(child);
                }
            }
            AddChildren(rootNode);
            roots.Add(rootNode);
        }
        Tree.ItemsSource = roots;
        UpdateSelectionButtons();
    }

    private void OnSchedule(IndexNode node, IndexSchedule? schedule)
    {
        _scheduleDirty = true;
        _controller!.SetSchedule(node.Scope, schedule);
    }

    private static string Trim(string path) => path.Trim('/');

    private static string ParentPath(Uri folder)
    {
        var path = Trim(folder.AbsolutePath);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string LastSegment(Uri folder)
    {
        var path = Trim(folder.AbsolutePath);
        return path[(path.LastIndexOf('/') + 1)..];
    }
}
