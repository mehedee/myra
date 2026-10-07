using Myra.Core;

namespace Myra.App.Services;

/// Long-lived services shared by every window.
///
/// Construction never fails because of one optional service: a failure is recorded in
/// StartupErrors (shown in the main window) and a safe fallback is used. The library index opens
/// on first use, off the UI thread; an open failure is kept in IndexError and is not retried on
/// every access (call RetryIndex to try again).
public sealed class AppServices
{
    private readonly List<string> _startupErrors = [];
    private readonly Lock _indexLock = new();
    private LibraryIndex? _index;
    private Exception? _indexFailure;
    private bool _indexClosed;
    private int _shutdown;

    public AppServices()
    {
        Store = Create("Settings and sources", () => AppStore.Load(),
            () => AppStore.Load(Path.Combine(Path.GetTempPath(), "Myra-fallback-" + Environment.ProcessId, "Myra.json")));
        if (Store.LoadError is { } storeError) _startupErrors.Add(storeError);
        Directory = new DirectoryService();
        Aria2 = new Aria2Controller();
        Downloads = new DownloadManager(Store, Aria2, new DownloadPlanner(Directory));
        PlayerState = Create("Player preferences", () => PlayerPersonalState.Load(), () => new PlayerPersonalState());
        Secrets = Create("Saved credentials", () => SecretStore.CreateDefault(), () => new InMemorySecretStore());
        IndexRefresh = Create("Index schedules", () => new IndexRefreshController(() => Index, new DirectoryService()),
            () => new IndexRefreshController(() => Index, new DirectoryService(), new IndexPolicies(),
                Path.Combine(Path.GetTempPath(), "Myra-fallback-" + Environment.ProcessId, "IndexPolicies.json")));
        IndexRefresh.IsPlaying = () => IsPlaying;
        Entertainment = Create("Personal library",
            () => new EntertainmentStore(() => Index, secrets: Secrets, isIndexing: () => IndexRefresh.IsRefreshing),
            () => new EntertainmentStore(() => Index,
                Path.Combine(Path.GetTempPath(), "Myra-fallback-" + Environment.ProcessId, "EntertainmentPersonal.json"),
                Secrets, isIndexing: () => IndexRefresh.IsRefreshing));
        Metadata = new MetadataService();
        Subtitles = new OpenSubtitlesService();
        // Leftover staging databases from a crash; locked (running) ones are skipped.
        _ = Task.Run(() => LibraryIndexer.CleanStaleStaging());
    }

    private T Create<T>(string name, Func<T> create, Func<T> fallback)
    {
        try
        {
            return create();
        }
        catch (Exception error)
        {
            _startupErrors.Add($"{name} could not be loaded: {error.Message}");
            return fallback();
        }
    }

    public AppStore Store { get; }
    public DirectoryService Directory { get; }
    public Aria2Controller Aria2 { get; }
    public DownloadManager Downloads { get; }
    public PlayerPersonalState PlayerState { get; }
    public ISecretStore Secrets { get; }
    public IndexRefreshController IndexRefresh { get; }
    public EntertainmentStore Entertainment { get; }
    public MetadataService Metadata { get; }
    public OpenSubtitlesService Subtitles { get; }

    /// Problems found while starting. The main window shows them; the app stays usable.
    public IReadOnlyList<string> StartupErrors => _startupErrors;

    private SubtitleCache? _subtitleCache;

    /// Created on first use, so a broken cache folder affects only the subtitle download.
    public SubtitleCache SubtitleCache
    {
        get
        {
            if (_subtitleCache is { } cache) return cache;
            try
            {
                return _subtitleCache = new SubtitleCache();
            }
            catch (Exception error) when (error is not (SubtitleException or IOException))
            {
                throw new IOException("The subtitle cache folder is unavailable: " + error.Message, error);
            }
        }
    }

    /// Why the library index is unavailable, or null. Home, search and resume points need it.
    public string? IndexError { get; private set; }

    /// Raised (on a worker thread) when the index fails to open.
    public event Action<string>? IndexFailed;

    /// Opens the index on first use. Callers run off the UI thread: the first open may migrate and
    /// back up a large index. Throws LibraryIndexException when the index is unavailable.
    public LibraryIndex Index
    {
        get
        {
            string? failed = null;
            LibraryIndex? opened;
            lock (_indexLock)
            {
                if (_indexClosed) throw new LibraryIndexException("Myra is closing.");
                if (_index is { } index) return index;
                if (_indexFailure is { } previous) throw new LibraryIndexException(Detail(previous), previous);
                try
                {
                    opened = _index = new LibraryIndex(AppPaths.IndexPath);
                }
                catch (Exception error)
                {
                    _indexFailure = error;
                    failed = Detail(error);
                    IndexError = "Library index: " + failed;
                    opened = null;
                }
            }
            if (failed is not null)
            {
                IndexFailed?.Invoke("Library index: " + failed);
                throw new LibraryIndexException(failed, _indexFailure);
            }
            return opened!;
        }
    }

    private static string Detail(Exception error)
    {
        const string prefix = "Library index: ";
        return error.Message.StartsWith(prefix, StringComparison.Ordinal) ? error.Message[prefix.Length..] : error.Message;
    }

    /// Lets the next access try to open the index again (for example after the user closed
    /// another program that held the file).
    public void RetryIndex()
    {
        lock (_indexLock)
        {
            if (_index is not null || _indexClosed) return;
            _indexFailure = null;
            IndexError = null;
        }
    }

    public bool IsPlaying { get; set; }

    /// Stops background work, saves state and closes the index. Safe to call more than once.
    /// The player saves its final position (Close) before this runs.
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        IndexRefresh.Cancel();
        Entertainment.StopBackgroundWork(TimeSpan.FromSeconds(3));
        try
        {
            IndexRefresh.Running.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
        try
        {
            Entertainment.FlushPersonal();
        }
        catch (Exception)
        {
        }
        try
        {
            Store.Save();
        }
        catch (Exception)
        {
        }
        try
        {
            PlayerState.Save();
        }
        catch (Exception)
        {
        }
        // Run off the UI thread: awaiting here would deadlock on the UI context.
        // aria2 also exits on its own through --stop-with-process.
        try
        {
            if (!Task.Run(Aria2.ShutdownAsync).Wait(TimeSpan.FromSeconds(5))) Aria2.TerminateImmediately();
        }
        catch (Exception)
        {
            Aria2.TerminateImmediately();
        }
        LibraryIndex? index;
        lock (_indexLock)
        {
            _indexClosed = true;
            index = _index;
        }
        // Dispose waits for a statement that is still running (it takes the index lock).
        index?.Dispose();
    }
}
