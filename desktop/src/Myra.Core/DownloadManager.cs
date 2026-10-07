using CommunityToolkit.Mvvm.ComponentModel;

namespace Myra.Core;

/// Download queue logic ported from AppCoordinator. Call it from the UI thread:
/// awaits resume on the captured context, so collection changes stay on that thread.
public sealed partial class DownloadManager(AppStore store, Aria2Controller aria2, DownloadPlanner planner) : ObservableObject
{
    private CancellationTokenSource? _scan;
    private bool _polling;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ScanProgress _scanProgress = new();
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _completedBytes;
    [ObservableProperty] private long _downloadSpeed;
    [ObservableProperty] private bool _hasRecords;

    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    public AppStore Store => store;
    public double OverallPercent => TotalBytes > 0 ? Math.Min(100, 100.0 * CompletedBytes / TotalBytes) : 0;

    public IEnumerable<DownloadItem> ItemsFor(DownloadBatch batch) => store.Items.Where(i => i.BatchId == batch.Id);

    public async Task DownloadSelectionAsync(IReadOnlyList<DirectoryEntry> selected, Uri categoryRoot)
    {
        if (IsScanning || selected.Count == 0) return;
        var title = selected.Count == 1 ? selected[0].Name : $"{selected.Count} selected items";
        var batch = NewBatch(title);
        IsScanning = true;
        ScanProgress = new ScanProgress();
        _scan = new CancellationTokenSource();
        try
        {
            var manifest = await planner.PrepareAsync(selected, categoryRoot, p => ScanProgress = p, _scan.Token);
            _scan.Token.ThrowIfCancellationRequested();
            await EnqueueAsync(manifest, batch);
        }
        catch (OperationCanceledException)
        {
            batch.Status = TransferStatus.Cancelled;
        }
        catch (Exception error)
        {
            Fail(batch, error);
        }
        finally
        {
            IsScanning = false;
            Save();
        }
    }

    public async Task DownloadManifestAsync(string title, IReadOnlyList<DownloadManifestItem> manifest)
    {
        if (manifest.Count == 0) return;
        var batch = NewBatch(title);
        try
        {
            await EnqueueAsync(manifest, batch);
        }
        catch (Exception error)
        {
            Fail(batch, error);
        }
        Save();
    }

    public void CancelScan() => _scan?.Cancel();

    private DownloadBatch NewBatch(string title)
    {
        var batch = new DownloadBatch { Title = title };
        store.Batches.Insert(0, batch);
        Save();
        return batch;
    }

    private void Fail(DownloadBatch batch, Exception error)
    {
        batch.Status = TransferStatus.Failed;
        batch.ErrorMessage = error.Message;
        ErrorRaised?.Invoke(error.Message);
    }

    private async Task EnqueueAsync(IReadOnlyList<DownloadManifestItem> manifest, DownloadBatch batch)
    {
        var settings = store.Settings;
        var root = settings.DownloadDirectory;
        Directory.CreateDirectory(root);
        var snapshot = settings.Snapshot();
        batch.TotalBytes = manifest.Sum(m => m.Size ?? 0);
        await aria2.StartAsync(snapshot);
        batch.Status = TransferStatus.Queued;
        batch.StartedAt = DateTimeOffset.Now;

        foreach (var entry in manifest)
        {
            var destination = DestinationSafety.Destination(root, entry.RelativePath);
            var item = DownloadItem.Create(batch.Id, entry, destination);
            store.Items.Add(item);
            if (IsAlreadyComplete(item))
            {
                item.Status = TransferStatus.Completed;
                item.CompletedBytes = Math.Max(item.TotalBytes, FileSize(destination));
                continue;
            }
            item.AriaGid = await aria2.AddAsync(entry.SourceUrl, destination, snapshot);
            item.Status = TransferStatus.Active;
        }
        batch.Status = ItemsFor(batch).All(i => i.Status == TransferStatus.Completed) ? TransferStatus.Completed : TransferStatus.Active;
        Save();
        StartPolling();
    }

    public async Task PauseAsync(DownloadBatch batch)
    {
        foreach (var item in ItemsFor(batch).Where(i => i.Status is TransferStatus.Active or TransferStatus.Queued))
        {
            if (item.AriaGid is { } gid) await Quietly(() => aria2.PauseAsync(gid));
            item.Status = TransferStatus.Paused;
        }
        batch.Status = TransferStatus.Paused;
        Save();
    }

    public async Task ResumeAsync(DownloadBatch batch)
    {
        try
        {
            await aria2.StartAsync(store.Settings.Snapshot());
            foreach (var item in ItemsFor(batch).Where(i => i.Status is TransferStatus.Paused or TransferStatus.Cancelled or TransferStatus.Failed))
            {
                var resumed = false;
                if (item.AriaGid is { } gid)
                {
                    try
                    {
                        await aria2.ResumeAsync(gid);
                        resumed = true;
                    }
                    catch (Exception)
                    {
                        // The gid belongs to an earlier aria2 process; submit again and let aria2 continue the partial file.
                    }
                }
                if (!resumed) await ResubmitAsync(item);
                item.Status = TransferStatus.Active;
                item.ErrorMessage = null;
            }
            batch.Status = TransferStatus.Active;
            batch.ErrorMessage = null;
            Save();
            StartPolling();
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke(error.Message);
        }
    }

    public Task RetryAsync(DownloadBatch batch) => ResumeAsync(batch);

    public async Task CancelAsync(DownloadBatch batch)
    {
        foreach (var item in ItemsFor(batch).Where(i => i.Status is TransferStatus.Active or TransferStatus.Queued or TransferStatus.Paused))
        {
            if (item.AriaGid is { } gid) await Quietly(() => aria2.PauseAsync(gid));
            item.Status = TransferStatus.Cancelled;
        }
        batch.Status = TransferStatus.Cancelled;
        Save();
    }

    /// Removes the record only; partial and completed files stay on disk.
    public async Task DeleteRecordAsync(DownloadBatch batch)
    {
        foreach (var item in ItemsFor(batch).ToList())
        {
            if (item.AriaGid is { } gid && item.Status != TransferStatus.Completed) await Quietly(() => aria2.RemoveAsync(gid));
            store.Items.Remove(item);
        }
        store.Batches.Remove(batch);
        Save();
        Notice?.Invoke("Download removed from the list");
    }

    /// Permanently deletes unfinished files and their .aria2 control files. Completed files stay.
    public async Task RemovePartialDataAsync(DownloadBatch batch)
    {
        foreach (var item in ItemsFor(batch).ToList())
        {
            if (item.Status != TransferStatus.Completed)
            {
                if (item.AriaGid is { } gid) await Quietly(() => aria2.RemoveAsync(gid));
                TryDelete(item.DestinationPath);
                TryDelete(item.DestinationPath + ".aria2");
            }
            store.Items.Remove(item);
        }
        store.Batches.Remove(batch);
        Save();
    }

    public void ClearCompleted()
    {
        var completed = store.Batches.Where(b => b.Status == TransferStatus.Completed).Select(b => b.Id).ToHashSet();
        foreach (var item in store.Items.Where(i => completed.Contains(i.BatchId)).ToList()) store.Items.Remove(item);
        foreach (var batch in store.Batches.Where(b => completed.Contains(b.Id)).ToList()) store.Batches.Remove(batch);
        Save();
    }

    /// Resubmits transfers that were running when the app last closed.
    public async Task RecoverAsync()
    {
        var recoverable = store.Items.Where(i => i.Status is TransferStatus.Active or TransferStatus.Queued).ToList();
        foreach (var batch in store.Batches.Where(b => b.Status == TransferStatus.Preparing)) batch.Status = TransferStatus.Failed;
        UpdateTotals();
        if (recoverable.Count == 0) return;
        try
        {
            await aria2.StartAsync(store.Settings.Snapshot());
            foreach (var item in recoverable)
            {
                if (IsAlreadyComplete(item))
                {
                    item.Status = TransferStatus.Completed;
                    item.CompletedBytes = Math.Max(item.TotalBytes, FileSize(item.DestinationPath));
                }
                else
                {
                    await ResubmitAsync(item);
                }
            }
            Save();
            StartPolling();
        }
        catch (Exception error)
        {
            ErrorRaised?.Invoke(error.Message);
        }
    }

    private async Task ResubmitAsync(DownloadItem item)
    {
        if (!Uri.TryCreate(item.SourceUrlString, UriKind.Absolute, out var source))
            throw new DirectoryException(DirectoryErrorKind.InvalidUrl);
        item.AriaGid = await aria2.AddAsync(source, item.DestinationPath, store.Settings.Snapshot());
        item.Status = TransferStatus.Active;
    }

    private async void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            while (await PollAsync()) await Task.Delay(1000);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task<bool> PollAsync()
    {
        var active = store.Items.Where(i => i.Status is TransferStatus.Active or TransferStatus.Queued && i.AriaGid is not null).ToList();
        foreach (var item in active)
        {
            try
            {
                var state = await aria2.StatusAsync(item.AriaGid!);
                if (state.TotalLength > 0) item.TotalBytes = state.TotalLength;
                item.CompletedBytes = state.CompletedLength;
                item.DownloadSpeed = state.DownloadSpeed;
                item.ErrorMessage = string.IsNullOrEmpty(state.ErrorMessage) ? null : state.ErrorMessage;
                item.Status = state.Status switch
                {
                    "complete" => TransferStatus.Completed,
                    "paused" => TransferStatus.Paused,
                    "error" or "removed" => TransferStatus.Failed,
                    "waiting" => TransferStatus.Queued,
                    _ => TransferStatus.Active,
                };
            }
            catch (Exception error)
            {
                item.ErrorMessage = error.Message;
            }
            if (item.Status != TransferStatus.Active) item.DownloadSpeed = 0;
        }

        foreach (var batch in store.Batches)
        {
            var items = ItemsFor(batch).ToList();
            if (items.Count == 0) continue;
            batch.TotalBytes = items.Sum(i => i.TotalBytes);
            batch.CompletedBytes = items.Sum(i => i.CompletedBytes);
            batch.DownloadSpeed = items.Sum(i => i.DownloadSpeed);
            if (items.All(i => i.Status == TransferStatus.Completed))
            {
                if (batch.Status != TransferStatus.Completed) Notice?.Invoke($"Finished: {batch.Title}");
                batch.Status = TransferStatus.Completed;
                batch.CompletedAt ??= DateTimeOffset.Now;
            }
            else if (items.Any(i => i.Status == TransferStatus.Failed))
            {
                batch.Status = TransferStatus.Failed;
                batch.ErrorMessage = items.FirstOrDefault(i => i.ErrorMessage is not null)?.ErrorMessage;
            }
            else if (items.Any(i => i.Status is TransferStatus.Active or TransferStatus.Queued))
            {
                batch.Status = TransferStatus.Active;
            }
        }
        UpdateTotals();
        Save();
        return active.Count > 0;
    }

    public void UpdateTotals()
    {
        var running = store.Batches.Where(b => b.Status is not (TransferStatus.Cancelled)).ToList();
        TotalBytes = running.Sum(b => b.TotalBytes);
        CompletedBytes = running.Sum(b => b.CompletedBytes);
        DownloadSpeed = running.Sum(b => b.DownloadSpeed);
        HasRecords = store.Batches.Count > 0 || IsScanning;
        OnPropertyChanged(nameof(OverallPercent));
    }

    partial void OnIsScanningChanged(bool value) => UpdateTotals();

    public void Save()
    {
        try
        {
            store.Save();
        }
        catch (IOException error)
        {
            ErrorRaised?.Invoke("Could not save downloads: " + error.Message);
        }
    }

    internal static bool IsAlreadyComplete(DownloadItem item)
    {
        if (!File.Exists(item.DestinationPath) || File.Exists(item.DestinationPath + ".aria2")) return false;
        var size = FileSize(item.DestinationPath);
        return item.TotalBytes == 0 ? size > 0 : size == item.TotalBytes;
    }

    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task Quietly(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
        }
    }
}
