namespace Myra.Core;

/// Breadth-first crawl of every source. Partial results survive failed folders.
public sealed class GlobalSearchService
{
    private sealed record Folder(GlobalSearchRoot Root, UrlBoundary Boundary, Uri Url, IReadOnlyList<string> RelativeComponents);

    private readonly ListingLoader _listingLoader;
    private readonly int _maximumConcurrentFolders;

    public GlobalSearchService(DirectoryService directoryService, int maximumConcurrentFolders = 4)
        : this(directoryService.ListingAsync, maximumConcurrentFolders)
    {
    }

    public GlobalSearchService(ListingLoader listingLoader, int maximumConcurrentFolders = 4)
    {
        _listingLoader = listingLoader;
        _maximumConcurrentFolders = Math.Max(1, maximumConcurrentFolders);
    }

    public async Task<GlobalSearchSnapshot> SearchAsync(
        string rawQuery,
        IReadOnlyList<GlobalSearchRoot> roots,
        Func<GlobalSearchSnapshot, Task> update,
        bool matchAllVideos = false,
        Func<List<GlobalSearchResult>, CancellationToken, Task>? batchSink = null,
        Func<int>? concurrencyLimit = null,
        CancellationToken cancellationToken = default)
    {
        var query = rawQuery.Trim();
        if (!matchAllVideos && query.Length < 3) throw new GlobalSearchException(GlobalSearchErrorKind.QueryTooShort);
        if (roots.Count == 0) throw new GlobalSearchException(GlobalSearchErrorKind.NoSources);

        var queue = new Queue<Folder>();
        var visited = new HashSet<(Guid, string)>();
        var outstanding = new Dictionary<Guid, int>();
        foreach (var root in roots)
        {
            var boundary = new UrlBoundary(root.Url);
            queue.Enqueue(new Folder(root, boundary, boundary.Root, []));
            visited.Add((root.Id, boundary.Root.AbsoluteUri));
            outstanding[root.Id] = 1;
        }

        var results = new List<GlobalSearchResult>();
        var resultIds = new HashSet<GlobalSearchResultId>();
        var failures = new Dictionary<Guid, GlobalSearchFailure>();
        var completedSources = new HashSet<Guid>();
        var progress = new GlobalSearchProgress { SourcesTotal = roots.Count };
        var sorted = !matchAllVideos;
        await update(Snapshot(batchSink is null ? results : [], progress, failures, sorted));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = concurrencyLimit?.Invoke() ?? _maximumConcurrentFolders;
            var count = Math.Min(Math.Max(1, Math.Min(requested, _maximumConcurrentFolders)), queue.Count);
            var batch = Enumerable.Range(0, count).Select(_ => queue.Dequeue()).ToList();

            var outcomes = await Task.WhenAll(batch.Select(async folder =>
            {
                try
                {
                    var listing = await _listingLoader(folder.Url, folder.Boundary, cancellationToken);
                    return (folder, listing, error: (string?)null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    return (folder, listing: (DirectoryListing?)null, error: error.Message);
                }
            }));

            foreach (var (folder, listing, error) in outcomes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (listing is not null)
                {
                    progress = progress with { FoldersVisited = progress.FoldersVisited + 1 };
                    foreach (var entry in listing.Entries)
                    {
                        if (entry.Kind == EntryKind.Folder)
                        {
                            if (visited.Add((folder.Root.Id, entry.Url.AbsoluteUri)))
                            {
                                queue.Enqueue(new Folder(folder.Root, folder.Boundary, entry.Url, [.. folder.RelativeComponents, entry.Name]));
                                outstanding[folder.Root.Id] = outstanding.GetValueOrDefault(folder.Root.Id) + 1;
                            }
                        }
                        else if (MediaFileType.IsVideo(entry)
                                 && (matchAllVideos || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                        {
                            var result = new GlobalSearchResult(
                                folder.Root.Id, folder.Root.Name, folder.Root.Url, entry,
                                string.Join('/', [.. folder.RelativeComponents, entry.Name]), listing.ArtworkUrl);
                            if (resultIds.Add(result.Id))
                            {
                                results.Add(result);
                                if (batchSink is not null && results.Count >= 250)
                                {
                                    await batchSink(results, cancellationToken);
                                    results = [];
                                }
                            }
                        }
                    }
                }
                else
                {
                    failures[folder.Root.Id] = failures.TryGetValue(folder.Root.Id, out var failure)
                        ? failure with { FailedFolders = failure.FailedFolders + 1 }
                        : new GlobalSearchFailure(folder.Root.Id, folder.Root.Name, 1, $"{folder.Url.AbsolutePath}: {error}");
                }

                outstanding[folder.Root.Id] = outstanding.GetValueOrDefault(folder.Root.Id, 1) - 1;
                if (outstanding[folder.Root.Id] == 0 && completedSources.Add(folder.Root.Id))
                    progress = progress with { SourcesCompleted = progress.SourcesCompleted + 1 };
                progress = progress with { MatchesFound = resultIds.Count, FailedSources = failures.Count };
                await update(Snapshot(batchSink is null ? results : [], progress, failures, sorted));
            }
        }

        if (batchSink is not null && results.Count > 0)
        {
            await batchSink(results, cancellationToken);
            results = [];
        }
        return Snapshot(batchSink is null ? results : [], progress, failures, sorted);
    }

    private static GlobalSearchSnapshot Snapshot(
        List<GlobalSearchResult> results, GlobalSearchProgress progress,
        Dictionary<Guid, GlobalSearchFailure> failures, bool sorted)
    {
        var ordered = sorted
            ? results.OrderBy(r => r.CategoryName, NaturalStringComparer.Instance)
                .ThenBy(r => r.RelativePath, NaturalStringComparer.Instance).ToList()
            : results.ToList();
        return new GlobalSearchSnapshot(
            ordered, progress,
            failures.Values.OrderBy(f => f.CategoryName, NaturalStringComparer.Instance).ToList());
    }
}
