namespace Myra.Core;

/// A downloaded title with the versions whose files still exist.
public sealed record OfflineTitle(EntertainmentTitle Title, IReadOnlyList<EntertainmentVersion> Available)
{
    public int Missing => Title.Versions.Count - Available.Count;
    public bool IsUnavailable => Available.Count == 0;

    public string Summary => $"{Available.Count} available version(s)" + (Missing > 0 ? $" • {Missing} missing" : "");

    /// Shown when no file exists any more.
    public const string ReconnectHint = "Reconnect its drive or download it again";
}

public enum OfflineStartKind
{
    /// More than one known version (including missing ones): show the version chooser.
    Choose,
    /// Exactly one version and its file exists: play it.
    Play,
    /// Nothing playable.
    Unavailable,
}

public sealed record OfflineStart(OfflineStartKind Kind, string? Path = null);

/// Offline Library: completed video downloads grouped by title. Missing files stay visible.
/// Nothing here deletes or modifies media files.
public static class OfflineLibrary
{
    /// Fixed source ID for local downloads (shared with macOS).
    public static readonly Guid LocalSourceId = new("A90C94FE-EA65-4EE6-AEBE-38F6AB267F73");

    public static List<OfflineTitle> Titles(
        IEnumerable<DownloadItem> items, EntertainmentPersonalData personal, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? IsPlayableLocalFile;
        var versions = new List<EntertainmentVersion>();
        foreach (var item in items.Where(i => i.Status == TransferStatus.Completed && i.DestinationPath.Length > 0))
        {
            Uri url;
            try
            {
                url = new Uri(Path.GetFullPath(item.DestinationPath));
            }
            catch (Exception error) when (error is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            var entry = new DirectoryEntry(Path.GetFileName(item.DestinationPath), url, EntryKind.File, item.CompletedBytes);
            if (!MediaFileType.IsVideo(entry)) continue;
            var root = new Uri(Path.GetDirectoryName(url.LocalPath) + Path.DirectorySeparatorChar);
            var media = new GlobalSearchResult(LocalSourceId, "Downloaded", root, entry, item.RelativePath, null);
            var record = personal.History.GetValueOrDefault(url.AbsoluteUri);
            versions.Add(new EntertainmentVersion(media, DateTimeOffset.MinValue)
            {
                ProgressSeconds = record?.Seconds ?? 0,
                Duration = record?.Duration ?? 0,
            });
        }
        return EntertainmentGrouping.Group(versions, personal.MatchCorrections)
            .Select(t => new OfflineTitle(t, t.Versions.Where(v => exists(v.Media.Entry.Url.LocalPath)).ToList()))
            .ToList();
    }

    public static List<OfflineTitle> Filter(IEnumerable<OfflineTitle> titles, string query, bool hideMissing) => titles
        .Where(t => (query.Length == 0
                     || t.Title.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || t.Title.Versions.Any(v => v.Media.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                    && (!hideMissing || !t.IsUnavailable))
        .ToList();

    /// Play decision for the row's Play button.
    public static OfflineStart Start(OfflineTitle title)
    {
        if (title.Title.Versions.Count > 1) return new OfflineStart(OfflineStartKind.Choose);
        return title.Available.FirstOrDefault() is { } version
            ? new OfflineStart(OfflineStartKind.Play, version.Media.Entry.Url.LocalPath)
            : new OfflineStart(OfflineStartKind.Unavailable);
    }

    /// A regular, readable file (not a directory, link target is followed by the OS).
    public static bool IsPlayableLocalFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
