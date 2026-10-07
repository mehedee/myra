using Myra.Core;

namespace Myra.App.Services;

/// Long-lived services shared by every window.
public sealed class AppServices
{
    public AppServices()
    {
        Store = AppStore.Load();
        Directory = new DirectoryService();
        Aria2 = new Aria2Controller();
        Downloads = new DownloadManager(Store, Aria2, new DownloadPlanner(Directory));
        PlayerState = PlayerPersonalState.Load();
        Secrets = SecretStore.CreateDefault();
        IndexRefresh = new IndexRefreshController(() => Index, new DirectoryService()) { IsPlaying = () => IsPlaying };
        Entertainment = new EntertainmentStore(() => Index, secrets: Secrets, isIndexing: () => IndexRefresh.IsRefreshing);
        Metadata = new MetadataService();
        Subtitles = new OpenSubtitlesService();
        SubtitleCache = new SubtitleCache();
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
    public SubtitleCache SubtitleCache { get; }

    private LibraryIndex? _index;
    private readonly Lock _indexLock = new();

    public LibraryIndex Index
    {
        get
        {
            lock (_indexLock) return _index ??= new LibraryIndex(AppPaths.IndexPath);
        }
    }

    public bool IsPlaying { get; set; }

    public void Shutdown()
    {
        try
        {
            Store.Save();
            PlayerState.Save();
        }
        catch (IOException)
        {
        }
        // Run off the UI thread: awaiting here would deadlock on the UI context.
        // aria2 also exits on its own through --stop-with-process.
        if (!Task.Run(Aria2.ShutdownAsync).Wait(TimeSpan.FromSeconds(5))) Aria2.TerminateImmediately();
        _index?.Dispose();
    }
}
