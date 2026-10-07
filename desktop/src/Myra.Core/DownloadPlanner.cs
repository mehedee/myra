using System.Text;

namespace Myra.Core;

public static class DestinationSafety
{
    // Windows rejects these in any path component; ':' would also create an NTFS stream.
    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string component)
    {
        var builder = new StringBuilder(component.Length);
        foreach (var character in component)
            builder.Append(char.IsControl(character) || Forbidden.Contains(character) ? '_' : character);
        // Windows silently drops trailing dots and spaces, which would break resume and exact matching.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..") return "Untitled";
        var stem = cleaned.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;
        return cleaned.Length > 240 ? cleaned[..240].TrimEnd('.', ' ') : cleaned;
    }

    public static string Destination(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new DirectoryException(DirectoryErrorKind.UnsafeDestination);
        return destination;
    }
}

public sealed record GlobalDownloadGroup(Guid Id, string CategoryName, IReadOnlyList<DownloadManifestItem> Manifest);

public static class GlobalDownloadManifestBuilder
{
    public static List<GlobalDownloadGroup> Groups(IEnumerable<GlobalSearchResult> results) =>
        results.GroupBy(r => r.CategoryId)
            .Select(group =>
            {
                var first = group.First();
                var categoryFolder = DestinationSafety.Sanitize(first.CategoryName);
                var seen = new HashSet<GlobalSearchResultId>();
                var manifest = group.Where(r => seen.Add(r.Id))
                    .OrderBy(r => r.RelativePath, NaturalStringComparer.Instance)
                    .Select(r => new DownloadManifestItem(
                        r.Entry.Url,
                        categoryFolder + "/" + string.Join('/', r.RelativePath.Split('/').Select(DestinationSafety.Sanitize)),
                        r.Entry.Size))
                    .ToList();
                return new GlobalDownloadGroup(group.Key, first.CategoryName, manifest);
            })
            .OrderBy(g => g.CategoryName, NaturalStringComparer.Instance)
            .ToList();
}

/// Expands selected folders recursively into a flat manifest that keeps their hierarchy.
public sealed class DownloadPlanner(ListingLoader listingLoader, int maximumConcurrentFolders = 4)
{
    private readonly int _maximumConcurrentFolders = Math.Max(1, maximumConcurrentFolders);

    public DownloadPlanner(DirectoryService directoryService, int maximumConcurrentFolders = 4)
        : this(directoryService.ListingAsync, maximumConcurrentFolders)
    {
    }

    public async Task<List<DownloadManifestItem>> PrepareAsync(
        IReadOnlyList<DirectoryEntry> selections,
        Uri categoryRoot,
        Action<ScanProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var boundary = new UrlBoundary(categoryRoot);
        var manifest = new List<DownloadManifestItem>();
        var scan = new ScanProgress();
        var visited = new HashSet<string>();
        var queue = new Queue<(Uri Url, List<string> Components)>();

        foreach (var entry in Deduplicated(selections))
        {
            var safeName = DestinationSafety.Sanitize(entry.Name);
            if (entry.Kind == EntryKind.File)
            {
                manifest.Add(new DownloadManifestItem(entry.Url, safeName, entry.Size));
                scan = scan with { FilesFound = scan.FilesFound + 1, KnownBytes = scan.KnownBytes + (entry.Size ?? 0) };
            }
            else
            {
                queue.Enqueue((entry.Url, [safeName]));
            }
        }
        progress(scan);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<(Uri Url, List<string> Components)>();
            while (batch.Count < _maximumConcurrentFolders && queue.Count > 0)
            {
                var folder = queue.Dequeue();
                if (visited.Add(folder.Url.AbsoluteUri)) batch.Add(folder);
            }
            var listings = await Task.WhenAll(batch.Select(async folder =>
                (folder.Components, Listing: await listingLoader(folder.Url, boundary, cancellationToken))));

            foreach (var (components, listing) in listings)
            {
                scan = scan with { FoldersVisited = scan.FoldersVisited + 1 };
                foreach (var entry in listing.Entries)
                {
                    List<string> next = [.. components, DestinationSafety.Sanitize(entry.Name)];
                    if (entry.Kind == EntryKind.Folder)
                    {
                        if (!visited.Contains(entry.Url.AbsoluteUri)) queue.Enqueue((entry.Url, next));
                    }
                    else
                    {
                        manifest.Add(new DownloadManifestItem(entry.Url, string.Join('/', next), entry.Size));
                        scan = scan with { FilesFound = scan.FilesFound + 1, KnownBytes = scan.KnownBytes + (entry.Size ?? 0) };
                    }
                }
                progress(scan);
            }
        }

        var unique = new HashSet<string>();
        return manifest.Where(item => unique.Add(item.SourceUrl.AbsoluteUri + "|" + item.RelativePath)).ToList();
    }

    private static List<DirectoryEntry> Deduplicated(IEnumerable<DirectoryEntry> selections)
    {
        var accepted = new List<DirectoryEntry>();
        foreach (var entry in selections.OrderBy(e => e.Url.AbsoluteUri.Length))
        {
            var covered = accepted.Any(parent =>
                parent.Kind == EntryKind.Folder && entry.Url.AbsoluteUri.StartsWith(parent.Url.AbsoluteUri, StringComparison.Ordinal));
            if (!covered) accepted.Add(entry);
        }
        return accepted;
    }
}
