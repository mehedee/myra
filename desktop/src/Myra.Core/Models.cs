using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Myra.Core;

public enum EntryKind
{
    Folder,
    File,
}

public sealed record DirectoryEntry(
    string Name,
    Uri Url,
    EntryKind Kind,
    long? Size = null,
    DateTimeOffset? ModifiedAt = null)
{
    public string Key => Url.AbsoluteUri;
    public bool IsFolder => Kind == EntryKind.Folder;
    public bool IsVideo => MediaFileType.IsVideo(this);
}

public sealed record DirectoryListing(Uri Url, IReadOnlyList<DirectoryEntry> Entries, Uri? ArtworkUrl);

public enum SearchScope
{
    Current,
    Global,
}

public sealed record GlobalSearchRoot(Guid Id, string Name, Uri Url);

public readonly record struct GlobalSearchResultId(Guid CategoryId, string Url);

public sealed record GlobalSearchResult(
    Guid CategoryId,
    string CategoryName,
    Uri CategoryRoot,
    DirectoryEntry Entry,
    string RelativePath,
    Uri? ArtworkUrl)
{
    public GlobalSearchResultId Id => new(CategoryId, Entry.Url.AbsoluteUri);

    public string ParentPath
    {
        get
        {
            var index = RelativePath.LastIndexOf('/');
            return index <= 0 ? "" : RelativePath[..index];
        }
    }
}

public sealed record GlobalSearchFailure(Guid CategoryId, string CategoryName, int FailedFolders, string Message);

public sealed record GlobalSearchProgress
{
    public int SourcesCompleted { get; init; }
    public int SourcesTotal { get; init; }
    public int FoldersVisited { get; init; }
    public int MatchesFound { get; init; }
    public int FailedSources { get; init; }
}

public sealed record GlobalSearchSnapshot(
    IReadOnlyList<GlobalSearchResult> Results,
    GlobalSearchProgress Progress,
    IReadOnlyList<GlobalSearchFailure> Failures);

public enum GlobalSearchErrorKind
{
    QueryTooShort,
    NoSources,
}

public sealed class GlobalSearchException(GlobalSearchErrorKind kind) : Exception(kind switch
{
    GlobalSearchErrorKind.QueryTooShort => "Enter at least 3 characters for Global search.",
    _ => "Add at least one directory before using Global search.",
})
{
    public GlobalSearchErrorKind Kind { get; } = kind;
}

public static class MediaFileType
{
    private static readonly HashSet<string> VideoExtensions = ["mp4", "mkv", "avi", "mov", "m4v", "ts", "webm"];
    private static readonly HashSet<string> ImageExtensions = ["jpg", "jpeg", "png", "webp"];

    public static string Extension(Uri url)
    {
        var path = url.IsFile ? url.LocalPath : url.AbsolutePath;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    public static bool IsVideo(DirectoryEntry entry) =>
        entry.Kind == EntryKind.File && VideoExtensions.Contains(Extension(entry.Url));

    public static bool IsArtwork(DirectoryEntry entry) =>
        entry.Kind == EntryKind.File && ImageExtensions.Contains(Extension(entry.Url));
}

public enum DirectorySortField
{
    Name,
    Date,
}

public static class DirectoryEntrySorter
{
    public static List<DirectoryEntry> Sorted(IEnumerable<DirectoryEntry> entries, DirectorySortField field, bool ascending)
    {
        var list = entries.ToList();
        list.Sort((left, right) =>
        {
            if (left.Kind != right.Kind) return left.Kind == EntryKind.Folder ? -1 : 1;
            int comparison;
            if (field == DirectorySortField.Name)
            {
                comparison = NaturalStringComparer.Instance.Compare(left.Name, right.Name);
            }
            else
            {
                switch (left.ModifiedAt, right.ModifiedAt)
                {
                    case ({ } l, { } r):
                        comparison = l.CompareTo(r);
                        break;
                    case (null, null):
                        comparison = NaturalStringComparer.Instance.Compare(left.Name, right.Name);
                        break;
                    case (null, _):
                        return 1;
                    default:
                        return -1;
                }
            }
            if (comparison == 0) return NaturalStringComparer.Instance.Compare(left.Name, right.Name);
            return ascending ? comparison : -comparison;
        });
        return list;
    }
}

public sealed record DownloadManifestItem(Uri SourceUrl, string RelativePath, long? Size)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record ScanProgress
{
    public int FoldersVisited { get; init; }
    public int FilesFound { get; init; }
    public long KnownBytes { get; init; }
}

public enum TransferStatus
{
    Preparing,
    Queued,
    Active,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

public static class AppThemeModeExtensions
{
    public static AppThemeMode Next(this AppThemeMode mode) => mode switch
    {
        AppThemeMode.System => AppThemeMode.Light,
        AppThemeMode.Light => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };
}

public enum IndexingMode
{
    Balanced,
    LowImpact,
}

public sealed partial class Category : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _rootUrlString = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public Uri? RootUrl => Uri.TryCreate(RootUrlString, UriKind.Absolute, out var url) ? url : null;
    [JsonIgnore]
    public GlobalSearchRoot? SearchRoot => RootUrl is { } url ? new GlobalSearchRoot(Id, Name, url) : null;
}

public sealed partial class DownloadBatch : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume), nameof(CanCancel))]
    private TransferStatus _status = TransferStatus.Preparing;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Progress), nameof(ProgressPercent))] private long _totalBytes;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Progress), nameof(ProgressPercent))] private long _completedBytes;
    [ObservableProperty] private long _downloadSpeed;
    [ObservableProperty] private string? _errorMessage;

    [JsonIgnore]
    public double Progress => TotalBytes > 0 ? Math.Min(1, (double)CompletedBytes / TotalBytes) : 0;
    [JsonIgnore]
    public double ProgressPercent => Progress * 100;
    [JsonIgnore]
    public bool CanPause => Status is TransferStatus.Active or TransferStatus.Queued;
    [JsonIgnore]
    public bool CanResume => Status is TransferStatus.Paused or TransferStatus.Failed or TransferStatus.Cancelled;
    [JsonIgnore]
    public bool CanCancel => Status is TransferStatus.Active or TransferStatus.Queued or TransferStatus.Paused;

    [JsonIgnore]
    public double? EstimatedSecondsRemaining =>
        TotalBytes > CompletedBytes && DownloadSpeed > 0 ? (double)(TotalBytes - CompletedBytes) / DownloadSpeed : null;
}

public sealed partial class DownloadItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public string SourceUrlString { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    [ObservableProperty] private TransferStatus _status = TransferStatus.Queued;
    public string? AriaGid { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProgressPercent))] private long _totalBytes;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProgressPercent))] private long _completedBytes;
    [ObservableProperty] private long _downloadSpeed;
    [ObservableProperty] private string? _errorMessage;

    [JsonIgnore]
    public double ProgressPercent => TotalBytes > 0 ? Math.Min(100, 100.0 * CompletedBytes / TotalBytes) : 0;

    public static DownloadItem Create(Guid batchId, DownloadManifestItem manifest, string destinationPath) => new()
    {
        Id = manifest.Id,
        BatchId = batchId,
        SourceUrlString = manifest.SourceUrl.AbsoluteUri,
        RelativePath = manifest.RelativePath,
        DestinationPath = destinationPath,
        TotalBytes = manifest.Size ?? 0,
    };
}

public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty] private string _downloadDirectory = AppPaths.DefaultDownloadDirectory();
    [ObservableProperty] private string _aria2PathOverride = "";
    [ObservableProperty] private string _vlcPathOverride = "";
    [ObservableProperty] private int _concurrentDownloads = 4;
    [ObservableProperty] private int _connectionsPerFile = 8;
    [ObservableProperty] private int _splitCount = 8;
    [ObservableProperty] private int _retryCount = 5;
    [ObservableProperty] private string _speedLimit = "0";
    [ObservableProperty] private AppThemeMode _theme = AppThemeMode.System;
    [ObservableProperty] private IndexingMode _indexingMode = IndexingMode.Balanced;

    public void Clamp()
    {
        ConcurrentDownloads = Math.Clamp(ConcurrentDownloads, 1, 20);
        ConnectionsPerFile = Math.Clamp(ConnectionsPerFile, 1, 16);
        SplitCount = Math.Clamp(SplitCount, 1, 16);
        RetryCount = Math.Clamp(RetryCount, 1, 20);
        if (string.IsNullOrWhiteSpace(SpeedLimit)) SpeedLimit = "0";
    }

    public AppSettingsSnapshot Snapshot() => new(
        Aria2PathOverride, ConcurrentDownloads, ConnectionsPerFile, SplitCount, RetryCount, SpeedLimit);
}

public sealed record AppSettingsSnapshot(
    string Aria2PathOverride,
    int ConcurrentDownloads,
    int ConnectionsPerFile,
    int SplitCount,
    int RetryCount,
    string SpeedLimit);

public sealed record Aria2Status(
    string Gid,
    string Status,
    long TotalLength,
    long CompletedLength,
    long DownloadSpeed,
    string? ErrorMessage);
