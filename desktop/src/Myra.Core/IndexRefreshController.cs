namespace Myra.Core;

/// Kind of refresh request.
public enum IndexRefreshMode
{
    /// Every source root (all folders), honouring snapshots and validators.
    All,
    /// Only folders whose inherited schedule is due (startup and footer refresh).
    Due,
    /// Explicitly selected scopes; manual-only folders inside them are refreshed too.
    Selected,
}

public sealed record IndexRefreshOutcome(
    bool Changed, bool Cancelled, IndexRefreshSummary Summary, IReadOnlyList<GlobalSearchFailure> Failures,
    GlobalSearchProgress Progress, string? Error, string? CompletionMessage);

/// Non-UI port of macOS LibraryController: serialized refreshes, due-folder selection, schedules,
/// pause (between folder batches), cancellation and Index Management rows.
/// Events fire on worker threads; the UI must marshal.
public sealed class IndexRefreshController
{
    private readonly Func<LibraryIndex> _index;
    private readonly DirectoryService _directory;
    private readonly string? _policiesPath;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _current;
    private Task _previous = Task.CompletedTask;
    private TaskCompletionSource _resume = NewResume(true);
    private IndexPolicies _policies;

    public IndexRefreshController(Func<LibraryIndex> index, DirectoryService directory, IndexPolicies? policies = null, string? policiesPath = null)
    {
        _index = index;
        _directory = directory;
        _policiesPath = policiesPath;
        _policies = policies ?? IndexPolicies.Load(policiesPath);
    }

    public IndexingMode Mode { get; set; } = IndexingMode.Balanced;

    /// Playback (or a low-power state) reduces folder concurrency to one.
    public Func<bool> IsPlaying { get; set; } = () => false;
    public Func<bool> IsLowPower { get; set; } = () => false;

    public bool IsRefreshing { get; private set; }
    public bool IsPaused { get; private set; }
    public GlobalSearchProgress Progress { get; private set; } = new();
    public IReadOnlyList<GlobalSearchFailure> Failures { get; private set; } = [];
    public IReadOnlyList<IndexFolderRow> FolderRows { get; private set; } = [];
    public DateTimeOffset? LastRefresh { get; private set; }

    public IndexPolicies Policies
    {
        get { lock (_lock) return _policies.Clone(); }
    }

    /// Progress, pause and row changes (throttled to twice per second during scans).
    public event EventHandler? StateChanged;

    /// The published index changed; Home and Global search should reload.
    public event EventHandler? IndexChanged;

    public static int FolderConcurrency(IndexingMode mode, bool playing, bool lowPower) =>
        mode == IndexingMode.LowImpact || playing || lowPower ? 1 : 2;

    /// Sets (or clears, with null) a folder's schedule override and saves the policies.
    public void SetSchedule(IndexScope scope, IndexSchedule? schedule)
    {
        lock (_lock)
        {
            _policies.Set(scope, schedule);
            _policies.Save(_policiesPath);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// Reads the folder rows off the calling thread. An unavailable index leaves the rows unchanged.
    public async Task LoadFolderRowsAsync(IReadOnlyList<GlobalSearchRoot> roots)
    {
        try
        {
            FolderRows = await Task.Run(() => _index().FolderRows(roots)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// Scopes that are due now under their inherited schedules.
    public IReadOnlyList<IndexScope> DueScopes(IReadOnlyList<IndexFolderRow> rows, DateTimeOffset? now = null)
    {
        var policies = Policies;
        return IndexScope.Compact(rows.Where(r => policies.ScheduleFor(r.Scope).IsDue(r.Checked, now)).Select(r => r.Scope));
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsRefreshing || IsPaused) return;
            IsPaused = true;
            _resume = NewResume(false);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        lock (_lock)
        {
            IsPaused = false;
            _resume.TrySetResult();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// The latest refresh run (completed when idle). Shutdown waits on it after Cancel.
    public Task Running
    {
        get { lock (_lock) return _previous; }
    }

    /// Cancels the running refresh. Staged changes are discarded; the published index is unchanged.
    public void Cancel()
    {
        lock (_lock)
        {
            _current?.Cancel();
            IsPaused = false;
            _resume.TrySetResult();
        }
    }

    /// Starts a refresh after any previous one stops. prepare (for example the Home restore) runs
    /// first and is not cancelled by Cancel. full bypasses schedules and validators in the selection.
    public Task<IndexRefreshOutcome> RefreshAsync(
        IReadOnlyList<GlobalSearchRoot> roots, IndexRefreshMode mode, bool manual,
        IReadOnlyList<IndexScope>? scopes = null, bool full = false, Func<Task>? prepare = null)
    {
        CancellationTokenSource cancellation;
        Task previous;
        lock (_lock)
        {
            _current?.Cancel();
            cancellation = _current = new CancellationTokenSource();
            previous = _previous;
            IsRefreshing = true;
            IsPaused = false;
            _resume.TrySetResult();
            Progress = new GlobalSearchProgress { SourcesTotal = roots.Count };
        }
        // The whole run, including opening the index, stays off the caller's (UI) thread.
        var task = Task.Run(() => RunAsync(roots, mode, manual, scopes, full, prepare, previous, cancellation));
        lock (_lock) _previous = task;
        return task;
    }

    private async Task<IndexRefreshOutcome> RunAsync(
        IReadOnlyList<GlobalSearchRoot> roots, IndexRefreshMode mode, bool manual, IReadOnlyList<IndexScope>? scopes,
        bool full, Func<Task>? prepare, Task previous, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await previous.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        var changed = false;
        var summary = new IndexRefreshSummary();
        string? error = null;
        string? completion = null;
        var cancelled = false;
        try
        {
            // A failed preparation (for example the Home restore) must not leave IsRefreshing set.
            if (prepare is not null)
            {
                try
                {
                    await prepare().ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                }
            }
            token.ThrowIfCancellationRequested();
            var index = _index();
            var before = await Task.Run(index.Revision, token).ConfigureAwait(false);
            await Task.Run(() => index.SynchronizeSources(roots), token).ConfigureAwait(false);
            LastRefresh = await Task.Run(index.LastRefresh, token).ConfigureAwait(false);
            FolderRows = await Task.Run(() => index.FolderRows(roots), token).ConfigureAwait(false);
            var selected = mode switch
            {
                IndexRefreshMode.Selected => IndexScope.Compact(scopes ?? []),
                IndexRefreshMode.Due => [.. DueScopes(FolderRows)],
                _ => roots.Select(IndexScope.ForRoot).ToList(),
            };
            if (selected.Count == 0)
            {
                if (manual) completion = "All scheduled folders are up to date.";
                changed = await Task.Run(index.Revision, token).ConfigureAwait(false) != before;
                return Finish(new IndexRefreshOutcome(changed, false, summary, [], Progress, null, completion), cancellation);
            }
            Failures = [];
            var indexer = new LibraryIndexer(_directory);
            var last = DateTimeOffset.MinValue;
            var snapshot = await indexer.RefreshAsync(
                roots, index, Guid.NewGuid(),
                update: s =>
                {
                    if (DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(500)) return Task.CompletedTask;
                    last = DateTimeOffset.UtcNow;
                    Progress = s.Progress;
                    Failures = s.Failures;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                concurrencyLimit: () => FolderConcurrency(Mode, IsPlaying(), IsLowPower()),
                cancellationToken: token,
                scopes: selected,
                policies: Policies,
                automatic: mode == IndexRefreshMode.Due,
                full: full,
                beforeBatch: async t =>
                {
                    Task wait;
                    lock (_lock) wait = _resume.Task;
                    await wait.WaitAsync(t).ConfigureAwait(false);
                }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            summary = indexer.Summary;
            Progress = snapshot.Progress;
            Failures = snapshot.Failures;
            changed = await Task.Run(index.Revision, token).ConfigureAwait(false) != before;
            LastRefresh = await Task.Run(index.LastRefresh, token).ConfigureAwait(false);
            if (manual)
                completion = summary.Text + (Failures.Count == 0 ? "" : $" {Failures.Count} source(s) could not be fully refreshed; previous records were retained.");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception failure)
        {
            error = failure.Message;
            if (manual) completion = "Refresh failed: " + failure.Message;
        }
        if (!cancelled)
        {
            try
            {
                var index = _index();
                FolderRows = await Task.Run(() => index.FolderRows(roots)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        return Finish(new IndexRefreshOutcome(changed, cancelled, summary, Failures, Progress, error, completion), cancellation);
    }

    private IndexRefreshOutcome Finish(IndexRefreshOutcome outcome, CancellationTokenSource cancellation)
    {
        lock (_lock)
        {
            if (_current == cancellation)
            {
                IsRefreshing = false;
                IsPaused = false;
                _current = null;
            }
        }
        cancellation.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (outcome.Changed) IndexChanged?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    private static TaskCompletionSource NewResume(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }
}
