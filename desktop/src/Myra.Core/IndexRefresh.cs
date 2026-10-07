using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myra.Core;

/// One folder of one source. Scopes select what a refresh rescans and publishes.
public sealed record IndexScope(GlobalSearchRoot Root, Uri Folder)
{
    /// Stable key used for schedule overrides: "{source id}|{folder url}".
    public string Id => Root.Id.ToString().ToUpperInvariant() + "|" + Folder.AbsoluteUri;

    /// Folder URL with a trailing slash, so "Archive/" never matches "Archive2/".
    public string Prefix => Folder.AbsoluteUri.EndsWith('/') ? Folder.AbsoluteUri : Folder.AbsoluteUri + "/";

    public static IndexScope ForRoot(GlobalSearchRoot root) => new(root, root.Url);

    /// Drops scopes already covered by a selected ancestor in the same source.
    public static List<IndexScope> Compact(IEnumerable<IndexScope> scopes)
    {
        var result = new List<IndexScope>();
        foreach (var scope in scopes.OrderBy(s => s.Prefix.Length))
            if (!result.Any(r => r.Root.Id == scope.Root.Id && scope.Prefix.StartsWith(r.Prefix, StringComparison.Ordinal)))
                result.Add(scope);
        return result;
    }

    public bool Equals(IndexScope? other) => other is not null && Id == other.Id;
    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IndexSchedule
{
    Daily,
    Weekly,
    Manual,
}

public static class IndexScheduleExtensions
{
    public static string Label(this IndexSchedule schedule) => schedule switch
    {
        IndexSchedule.Daily => "Daily",
        IndexSchedule.Weekly => "Weekly",
        _ => "Manual only",
    };

    public static TimeSpan Interval(this IndexSchedule schedule) => schedule switch
    {
        IndexSchedule.Daily => TimeSpan.FromDays(1),
        IndexSchedule.Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.MaxValue,
    };

    /// Manual-only folders are never due automatically. Never-checked folders are always due.
    public static bool IsDue(this IndexSchedule schedule, DateTimeOffset? checkedAt, DateTimeOffset? now = null)
    {
        if (schedule == IndexSchedule.Manual) return false;
        if (checkedAt is not { } value) return true;
        return (now ?? DateTimeOffset.Now) - value >= schedule.Interval();
    }
}

/// Per-folder schedule overrides. A folder inherits the nearest ancestor's override; the default is Daily.
public sealed class IndexPolicies
{
    public Dictionary<string, IndexSchedule> Overrides { get; set; } = [];

    public IndexSchedule ScheduleFor(IndexScope scope) => Nearest(scope, _ => true)?.Schedule ?? IndexSchedule.Daily;

    /// Explicit override for exactly this folder, or null when it inherits.
    public IndexSchedule? OverrideFor(IndexScope scope) => Overrides.TryGetValue(scope.Id, out var value) ? value : null;

    /// Null removes the override so the folder inherits again.
    public void Set(IndexScope scope, IndexSchedule? schedule)
    {
        if (schedule is { } value) Overrides[scope.Id] = value;
        else Overrides.Remove(scope.Id);
    }

    internal (int Length, IndexSchedule Schedule)? Nearest(IndexScope scope, Func<IndexSchedule, bool> include)
    {
        (int Length, IndexSchedule Schedule)? best = null;
        var prefixBase = scope.Root.Id.ToString().ToUpperInvariant() + "|";
        foreach (var (key, value) in Overrides)
        {
            if (!key.StartsWith(prefixBase, StringComparison.OrdinalIgnoreCase) || !include(value)) continue;
            var path = key[prefixBase.Length..];
            var prefix = path.EndsWith('/') ? path : path + "/";
            if (scope.Prefix.StartsWith(prefix, StringComparison.Ordinal) && (best is null || prefix.Length > best.Value.Length))
                best = (prefix.Length, value);
        }
        return best;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// Missing or unreadable files yield default (Daily) policies; schedules are not critical data.
    public static IndexPolicies Load(string? path = null)
    {
        try
        {
            var file = path ?? AppPaths.IndexPoliciesPath;
            return File.Exists(file) ? JsonSerializer.Deserialize<IndexPolicies>(File.ReadAllText(file), Options) ?? new() : new();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null) =>
        AtomicFile.WriteAllText(path ?? AppPaths.IndexPoliciesPath, JsonSerializer.Serialize(this, Options));

    public IndexPolicies Clone() => new() { Overrides = new Dictionary<string, IndexSchedule>(Overrides) };
}

/// Saved directory snapshot with the HTTP validators used for conditional GET.
public sealed record IndexedFolder(DirectoryListing Listing, DateTimeOffset Checked, string? ETag = null, string? LastModified = null)
{
    internal string Serialize() => JsonSerializer.Serialize(new FolderPayload
    {
        Url = Listing.Url.AbsoluteUri,
        Artwork = Listing.ArtworkUrl?.AbsoluteUri,
        Checked = Checked.ToUnixTimeMilliseconds() / 1000.0,
        ETag = ETag,
        LastModified = LastModified,
        Entries = Listing.Entries.Select(e => new EntryPayload
        {
            Name = e.Name,
            Url = e.Url.AbsoluteUri,
            Folder = e.Kind == EntryKind.Folder,
            Size = e.Size,
            Modified = e.ModifiedAt?.ToUnixTimeMilliseconds() / 1000.0,
        }).ToList(),
    });

    internal static IndexedFolder? Deserialize(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<FolderPayload>(json);
            if (payload is null || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var url)) return null;
            var entries = new List<DirectoryEntry>();
            foreach (var entry in payload.Entries)
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var entryUrl)) continue;
                entries.Add(new DirectoryEntry(entry.Name, entryUrl, entry.Folder ? EntryKind.Folder : EntryKind.File, entry.Size,
                    entry.Modified is { } m ? LibraryIndex.FromUnix(m) : null));
            }
            Uri? artwork = payload.Artwork is not null && Uri.TryCreate(payload.Artwork, UriKind.Absolute, out var a) ? a : null;
            return new IndexedFolder(new DirectoryListing(url, entries, artwork), LibraryIndex.FromUnix(payload.Checked), payload.ETag, payload.LastModified);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool SameContent(DirectoryListing left, DirectoryListing right) =>
        left.Entries.SequenceEqual(right.Entries) && Equals(left.ArtworkUrl, right.ArtworkUrl);

    private sealed class FolderPayload
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("artwork")] public string? Artwork { get; set; }
        [JsonPropertyName("checked")] public double Checked { get; set; }
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
        [JsonPropertyName("entries")] public List<EntryPayload> Entries { get; set; } = [];
    }

    private sealed class EntryPayload
    {
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("u")] public string Url { get; set; } = "";
        [JsonPropertyName("f")] public bool Folder { get; set; }
        [JsonPropertyName("s")] public long? Size { get; set; }
        [JsonPropertyName("m")] public double? Modified { get; set; }
    }
}

/// One row of the Index Management tree. Files counts include descendants.
public sealed record IndexFolderRow(IndexScope Scope, DateTimeOffset? Checked, int Files)
{
    public string Id => Scope.Id;
}

public sealed record IndexRefreshSummary
{
    public int Added { get; set; }
    public int Changed { get; set; }
    public int Removed { get; set; }
    public int Checked { get; set; }
    public int Unchanged { get; set; }

    public string Text =>
        $"Checked {Checked} folders · {Unchanged} unchanged · {Added} files added · {Changed} updated · {Removed} removed";
}

/// Snapshot-aware folder loader for one refresh. It writes snapshots to the private staging index only;
/// the published index changes when the completed scopes are promoted.
internal sealed class IndexListingLoader(
    DirectoryService service,
    LibraryIndex active,
    LibraryIndex staging,
    IReadOnlyList<GlobalSearchRoot> roots,
    IndexPolicies policies,
    IReadOnlyList<IndexScope> selected,
    bool automatic,
    bool full)
{
    private int _checked;
    private int _unchanged;

    public int CheckedFolders => _checked;
    public int UnchangedFolders => _unchanged;

    public async Task<DirectoryListing> ListingAsync(Uri url, UrlBoundary boundary, CancellationToken cancellationToken)
    {
        var root = roots.FirstOrDefault(r => boundary.SourceId is { } id ? r.Id == id : r.Url.SameAs(boundary.Root))
                   ?? throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
        var scope = new IndexScope(root, url.SameAs(root.Url) ? root.Url : url);
        var old = await Task.Run(() => active.Folder(scope), cancellationToken);
        var schedule = policies.ScheduleFor(scope);
        var manualBlocked = IsManualBlocked(scope);
        var useCache = !full && (manualBlocked || (automatic && !schedule.IsDue(old?.Checked)));
        IndexedFolder value;
        if (useCache)
        {
            // A manual-only branch cannot be safely reconciled without an existing complete snapshot.
            value = old ?? throw new LibraryIndexException("Refresh this manual-only folder once to establish its snapshot.");
        }
        else
        {
            value = await service.ConditionalListingAsync(url, boundary, full ? null : old, cancellationToken);
            Interlocked.Increment(ref _checked);
            if (old is not null && IndexedFolder.SameContent(old.Listing, value.Listing)) Interlocked.Increment(ref _unchanged);
        }

        if (!full && old is not null)
        {
            // Excluded manual-only children stay in the tree even when the parent no longer links them.
            var entries = value.Listing.Entries.ToList();
            var observed = entries.Select(e => e.Url.AbsoluteUri).ToHashSet();
            foreach (var entry in old.Listing.Entries)
                if (entry.Kind == EntryKind.Folder && !observed.Contains(entry.Url.AbsoluteUri) && IsManualBlocked(new IndexScope(root, entry.Url)))
                    entries.Add(entry);
            value = value with { Listing = value.Listing with { Entries = entries } };
        }
        await Task.Run(() => staging.SaveFolder(value, scope), cancellationToken);
        return value.Listing;
    }

    private bool IsManualBlocked(IndexScope scope)
    {
        var requestedPrefix = selected
            .Where(s => s.Root.Id == scope.Root.Id && scope.Prefix.StartsWith(s.Prefix, StringComparison.Ordinal))
            .Select(s => s.Prefix.Length).DefaultIfEmpty(0).Max();
        var manualPrefix = policies.Nearest(scope, s => s == IndexSchedule.Manual)?.Length ?? 0;
        return policies.ScheduleFor(scope) == IndexSchedule.Manual && (automatic || manualPrefix > requestedPrefix);
    }
}
