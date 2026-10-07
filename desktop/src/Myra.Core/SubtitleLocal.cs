using System.Text.Json;

namespace Myra.Core;

/// Free-first subtitle sources: files next to local media, titles remembered in the cache, manual
/// choice, and the OpenSubtitles.org web page. None of these need an account or a network call.
public static class SubtitleLocal
{
    public static readonly IReadOnlyList<string> Extensions = [".srt", ".ass", ".ssa", ".vtt"];

    public const int MaximumNearby = 12;

    /// A regular, non-empty subtitle file with a supported extension, at most 5 MB. Symbolic links are refused.
    public static bool IsUsable(string path)
    {
        try
        {
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is null && info.Length > 0 && info.Length <= SubtitleCache.MaximumFileBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// Subtitle files in the media file's folder whose name starts with the media file's name.
    /// Only file:// media is searched; http media has no folder on this computer.
    public static IReadOnlyList<string> Nearby(Uri mediaUrl)
    {
        if (!mediaUrl.IsAbsoluteUri || !mediaUrl.IsFile) return [];
        try
        {
            var media = mediaUrl.LocalPath;
            var directory = Path.GetDirectoryName(media);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [];
            var stem = Path.GetFileNameWithoutExtension(media);
            return Directory.EnumerateFiles(directory)
                .Where(file => Path.GetFileNameWithoutExtension(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase) && IsUsable(file))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .Take(MaximumNearby)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// OpenSubtitles.org search page for a manual browser search (https, no credentials).
    public static Uri WebSearchUrl(string title, string? year = null, string? season = null, string? episode = null)
    {
        var terms = string.Join(' ', new[]
        {
            title.Trim(), year ?? "", string.IsNullOrEmpty(season) ? "" : "S" + season, string.IsNullOrEmpty(episode) ? "" : "E" + episode,
        }.Where(t => t.Length > 0));
        return new Uri("https://www.opensubtitles.org/en/search2?MovieName=" + Uri.EscapeDataString(terms));
    }
}

public sealed partial class SubtitleCache
{
    private const string ManifestName = "title-cache.json";
    private const int MaximumTitleFiles = 20;
    private const long MaximumManifestBytes = 2 * 1024 * 1024;

    /// Remembers that this cached provider file belongs to this title, so it can be offered again offline.
    public void Remember(int fileId, MediaIdentity identity)
    {
        lock (_lock)
        {
            if (Cached(fileId) is null) return;
            var manifest = Path.Combine(_directory, ManifestName);
            if (new FileInfo(manifest).LinkTarget is not null) throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
            var titles = ReadTitles();
            var files = titles.TryGetValue(identity.CacheKey, out var existing) ? existing.ToList() : [];
            if (!files.Contains(fileId)) files.Add(fileId);
            titles[identity.CacheKey] = files.TakeLast(MaximumTitleFiles).ToArray();
            AtomicFile.WriteAllText(manifest, JsonSerializer.Serialize(titles));
        }
    }

    /// Unexpired cached subtitle files remembered for this title.
    public IReadOnlyList<string> Cached(MediaIdentity identity)
    {
        lock (_lock)
        {
            return ReadTitles().TryGetValue(identity.CacheKey, out var files)
                ? files.Select(id => Cached(id)).OfType<string>().ToList()
                : [];
        }
    }

    private Dictionary<string, int[]> ReadTitles()
    {
        var info = new FileInfo(Path.Combine(_directory, ManifestName));
        try
        {
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumManifestBytes) return [];
            return JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(info.FullName)) is { } titles
                ? titles.Where(t => t.Value is not null).ToDictionary(t => t.Key, t => t.Value)
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
