using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// Title/year/season/episode parsed from a filename. Metadata and subtitle providers receive only
/// these values (or an IMDb ID), never the source media URL.
public sealed record MediaIdentity(string Title, string? Year = null, string? Season = null, string? Episode = null, string? ImdbId = null)
{
    /// Shared with macOS: metadata cache key, episode identity for followed shows, and marker key.
    public string CacheKey => string.Join('|', Title.ToLowerInvariant(), Year ?? "", Season ?? "", Episode ?? "", ImdbId ?? "");

    private static readonly Regex EpisodePattern = new(@"\bS(\d{1,2})E(\d{1,3})\b", RegexOptions.IgnoreCase);
    private static readonly Regex YearPattern = new(@"\b(?:19|20)\d{2}\b");
    private static readonly Regex QualityPattern = new(
        @"\b(?:2160p|1080p|720p|480p|4k|bluray|blu-ray|webrip|web-dl|hdtv|dvdrip|x264|x265|h264|h265)\b", RegexOptions.IgnoreCase);

    public static MediaIdentity Parse(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var name = stem.Replace('.', ' ').Replace('_', ' ');
        string? season = null, episode = null, year = null;

        var match = EpisodePattern.Match(name);
        if (match.Success)
        {
            season = int.Parse(match.Groups[1].Value).ToString();
            episode = int.Parse(match.Groups[2].Value).ToString();
            name = name[..match.Index];
        }
        match = YearPattern.Match(name);
        if (match.Success && TrimPunctuation(name[..match.Index]).Length > 0)
        {
            year = match.Value;
            name = name[..match.Index];
        }
        match = QualityPattern.Match(name);
        if (match.Success) name = name[..match.Index];

        var title = TrimPunctuation(name);
        return new MediaIdentity(title.Length == 0 ? stem : title, year, season, episode);
    }

    private static string TrimPunctuation(string value) =>
        value.Trim().Trim(" \t\r\n!\"#%&'()*,-./:;?@[\\]_{}".ToCharArray());

    public static string Quality(string filename)
    {
        string[] tokens = ["2160p", "1080p", "720p", "480p", "4K", "BluRay", "WEB-DL", "WEBRip", "HDR", "x265", "x264"];
        return string.Join(" · ", tokens.Where(t => filename.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
}

/// Folder-scoped play queue. Browser filters and Global results never define episode order.
public sealed class PlaybackSequence
{
    public IReadOnlyList<GlobalSearchResult> Videos { get; }

    public PlaybackSequence(GlobalSearchResult playing, IEnumerable<DirectoryEntry> entries, Uri? artworkUrl)
    {
        var root = playing.CategoryRoot.StandardizedDirectoryUrl();
        if (!Contains(playing.Entry.Url, root) || !MediaFileType.IsVideo(playing.Entry))
            throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
        var parent = playing.Entry.Url.Parent().AbsoluteUri;
        var seen = new HashSet<string>();
        var candidates = entries.Append(playing.Entry)
            .Where(e => MediaFileType.IsVideo(e) && Contains(e.Url, root)
                        && e.Url.Parent().AbsoluteUri == parent && seen.Add(e.Url.AbsoluteUri))
            .OrderBy(e => e.Name, NaturalStringComparer.Instance)
            .ToList();
        var rootPath = root.AbsolutePath;
        Videos = candidates.Select(entry => playing with
        {
            Entry = entry,
            RelativePath = Uri.UnescapeDataString(entry.Url.AbsolutePath.StartsWith(rootPath)
                ? entry.Url.AbsolutePath[rootPath.Length..].Trim('/')
                : entry.Name),
            ArtworkUrl = artworkUrl ?? playing.ArtworkUrl,
        }).ToList();
    }

    public static bool Contains(Uri url, Uri root)
    {
        if (root.IsFile) return url.IsFile;
        try
        {
            return new UrlBoundary(root).Contains(url);
        }
        catch (DirectoryException)
        {
            return false;
        }
    }

    /// Alternate releases of one movie or episode form one group, so Next skips duplicate encodes.
    public List<List<GlobalSearchResult>> Groups
    {
        get
        {
            var grouped = new List<List<GlobalSearchResult>>();
            var indexes = new Dictionary<string, int>();
            foreach (var video in Videos)
            {
                var identity = MediaIdentity.Parse(video.Entry.Name);
                var normalized = RemoveDiacritics(identity.Title).ToLowerInvariant();
                string key;
                if (identity.Year is not null || identity.Episode is not null)
                    key = string.Join('|', normalized, identity.Year ?? "", identity.Season ?? "", identity.Episode ?? "");
                else if (MediaIdentity.Quality(video.Entry.Name).Length > 0)
                    key = "folder|" + video.Entry.Url.Parent().AbsoluteUri + "|" + normalized;
                else
                    key = video.Entry.Url.AbsoluteUri;

                if (indexes.TryGetValue(key, out var index))
                {
                    grouped[index].Add(video);
                }
                else
                {
                    indexes[key] = grouped.Count;
                    grouped.Add([video]);
                }
            }
            return grouped;
        }
    }

    public IReadOnlyList<GlobalSearchResult> Neighbours(Uri url, int offset)
    {
        var groups = Groups;
        var index = groups.FindIndex(g => g.Any(v => v.Entry.Url.AbsoluteUri == url.AbsoluteUri));
        return index >= 0 && index + offset >= 0 && index + offset < groups.Count ? groups[index + offset] : [];
    }

    public GlobalSearchResult? Previous(Uri url) => Neighbours(url, -1).FirstOrDefault();
    public GlobalSearchResult? Next(Uri url) => Neighbours(url, 1).FirstOrDefault();

    /// Previous/Next are enabled only when a distinct neighbouring title or episode exists.
    public bool HasPrevious(Uri url) => Neighbours(url, -1).Count > 0;
    public bool HasNext(Uri url) => Neighbours(url, 1).Count > 0;

    /// "Try another version": every release of the current movie/episode in this folder, current first excluded
    /// by the caller if desired. Falls back to the current file alone.
    public IReadOnlyList<GlobalSearchResult> CurrentVersions(GlobalSearchResult current) =>
        Groups.FirstOrDefault(g => g.Any(v => v.Entry.Url.AbsoluteUri == current.Entry.Url.AbsoluteUri)) ?? [current];

    private static string RemoveDiacritics(string value) => TextFolding.Fold(value);
}

/// Data for the "Choose a version" dialog: the releases and the one to highlight
/// (the most recently played, else the requested file).
public sealed record PlaybackChoices(IReadOnlyList<EntertainmentVersion> Versions, Uri PreferredUrl)
{
    /// Null when there is nothing to choose (zero or one version): play the requested file directly.
    public static PlaybackChoices? For(IReadOnlyList<EntertainmentVersion> versions, GlobalSearchResult requested)
    {
        if (versions.Count < 2) return null;
        var preferred = versions.Where(v => v.LastPlayed is not null).MaxBy(v => v.LastPlayed);
        return new PlaybackChoices(versions, preferred?.Media.Entry.Url ?? requested.Entry.Url);
    }

    /// Discovers alternate releases from a folder listing when the folder is not indexed yet.
    /// Only versions of the same season/episode as the requested file are returned.
    public static List<EntertainmentVersion> FromListing(
        GlobalSearchResult requested, IEnumerable<DirectoryEntry> entries, Uri? artworkUrl,
        IReadOnlyDictionary<string, EntertainmentMatchCorrection>? corrections = null)
    {
        var sequence = new PlaybackSequence(requested, entries, artworkUrl);
        var candidates = sequence.Videos.Select(v => new EntertainmentVersion(v, DateTimeOffset.Now));
        var title = EntertainmentGrouping.Group(candidates, corrections)
            .FirstOrDefault(t => t.Versions.Any(v => v.Id == requested.Entry.Url.AbsoluteUri));
        if (title is null) return [];
        var identity = MediaIdentity.Parse(requested.Entry.Name);
        int? season = identity.Season is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;
        int? episode = identity.Episode is { } e ? int.Parse(e, CultureInfo.InvariantCulture) : null;
        return title.Versions.Where(v => v.Season == season && v.Episode == episode).ToList();
    }
}

public sealed record PlayerTrack(int Id, string Name, string? Language);

public static class EnglishTrackPreference
{
    private static readonly Regex EnglishToken = new(@"\b(?:en|eng)\b");

    public static int? Preferred(IEnumerable<PlayerTrack> tracks, bool subtitles)
    {
        (int Id, int Score)? best = null;
        foreach (var track in tracks.Where(t => t.Id >= 0))
        {
            var language = track.Language?.Trim().ToLowerInvariant().Replace('_', '-').Split('-')[0];
            var name = track.Name.ToLowerInvariant();
            var english = language is "en" or "eng" or "english" || name.Contains("english") || EnglishToken.IsMatch(name);
            if (!english) continue;
            var score = 0;
            if (name.Contains("commentary") || name.Contains("description")) score -= 10;
            if (subtitles && name.Contains("forced")) score -= 5;
            if (best is null || score > best.Value.Score) best = (track.Id, score);
        }
        return best?.Id;
    }
}

public static class PlayerLanguagePreference
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["bn"] = ["bn", "ben", "bengali", "bangla"],
        ["hi"] = ["hi", "hin", "hindi"],
        ["fr"] = ["fr", "fra", "fre", "french"],
        ["ja"] = ["ja", "jpn", "japanese"],
        ["es"] = ["es", "spa", "spanish"],
        ["ko"] = ["ko", "kor", "korean"],
    };

    /// Returns -1 for "off", null to keep VLC's default, or the matching track id.
    public static int? Preferred(IReadOnlyList<PlayerTrack> tracks, string language, bool subtitles)
    {
        if (language == "off") return -1;
        if (language == "default") return null;
        if (language == "en") return EnglishTrackPreference.Preferred(tracks, subtitles);
        var tokens = Aliases.GetValueOrDefault(language) ?? [language];
        return tracks.FirstOrDefault(track =>
        {
            var code = track.Language?.ToLowerInvariant().Split('-')[0] ?? "";
            return track.Id >= 0 && (tokens.Contains(code)
                || tokens.Any(token => Regex.IsMatch(track.Name.ToLowerInvariant(), @"\b" + Regex.Escape(token) + @"\b")));
        })?.Id;
    }

    public static readonly (string Code, string Label)[] Choices =
    [
        ("en", "English"), ("bn", "Bengali"), ("hi", "Hindi"), ("fr", "French"),
        ("ja", "Japanese"), ("es", "Spanish"), ("ko", "Korean"), ("default", "File default"),
    ];
}

public sealed record PlayerSkipRange(double Start, double End)
{
    public bool Valid(double duration) =>
        double.IsFinite(Start) && double.IsFinite(End) && Start >= 0 && End > Start && End <= duration;

    public bool Contains(double seconds) => seconds >= Start && seconds < End;
}

/// Durable viewing choices, separate from the rebuildable index.
public sealed class PlayerPersonalState
{
    public float Speed { get; set; } = 1;
    public string AudioLanguage { get; set; } = "en";
    public string SubtitleLanguage { get; set; } = "en";
    public bool Autoplay { get; set; } = true;
    public int Volume { get; set; } = 100;

    /// Skip intro/outro automatically when the playhead enters a valid marker range.
    public bool AutomaticSkipping { get; set; }

    /// Marker ranges keyed by PlayerMarkers.EpisodeKey (one file identity) or SeriesKey ("series|…").
    public Dictionary<string, PlayerSkipMarkers> Markers { get; set; } = [];

    /// A missing file gives defaults. An unreadable or corrupt file is renamed to
    /// "{name}.corrupt-{timestamp}" first, so the next save cannot overwrite its skip markers.
    public static PlayerPersonalState Load(string? path = null)
    {
        var file = path ?? AppPaths.PlayerStatePath;
        try
        {
            if (!File.Exists(file)) return new();
            var state = JsonSerializer.Deserialize<PlayerPersonalState>(File.ReadAllText(file)) ?? throw new JsonException("Empty player preferences.");
            state.Markers ??= [];
            state.AudioLanguage ??= "en";
            state.SubtitleLanguage ??= "en";
            if (state.Markers.Keys.Any(k => k is null)) throw new JsonException("Invalid marker key.");
            return state;
        }
        catch (Exception)
        {
            PreserveCorrupt(file);
            return new();
        }
    }

    internal static void PreserveCorrupt(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            var aside = file + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (var i = 1; File.Exists(aside); i++) aside = file + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{i}";
            File.Move(file, aside);
        }
        catch (Exception)
        {
            // If it cannot be moved, the original stays; saving may then replace it.
        }
    }

    public void Save(string? path = null) =>
        AtomicFile.WriteAllText(path ?? AppPaths.PlayerStatePath, JsonSerializer.Serialize(this));

    public PlayerPersonalState Clone() => new()
    {
        Speed = Speed,
        AudioLanguage = AudioLanguage,
        SubtitleLanguage = SubtitleLanguage,
        Autoplay = Autoplay,
        Volume = Volume,
        AutomaticSkipping = AutomaticSkipping,
        Markers = new Dictionary<string, PlayerSkipMarkers>(Markers),
    };
}

public sealed record PlayerSkipMarkers(PlayerSkipRange? Intro = null, PlayerSkipRange? Outro = null);

/// Intro/outro marker rules. Episode markers override series markers; ranges must fit the duration.
public static class PlayerMarkers
{
    public static string EpisodeKey(GlobalSearchResult media) => MediaIdentity.Parse(media.Entry.Name).CacheKey;

    public static string SeriesKey(GlobalSearchResult media) =>
        "series|" + (MediaIdentity.Parse(media.Entry.Name) with { Season = null, Episode = null }).CacheKey;

    public static PlayerSkipMarkers Current(PlayerPersonalState state, GlobalSearchResult media) =>
        state.Markers.GetValueOrDefault(EpisodeKey(media)) ?? state.Markers.GetValueOrDefault(SeriesKey(media)) ?? new PlayerSkipMarkers();

    /// Saves markers for the episode or the whole series. Returns false (and saves nothing) when the
    /// duration is unknown or a range is invalid for it.
    public static bool Set(PlayerPersonalState state, GlobalSearchResult media, PlayerSkipMarkers markers, bool series, double duration)
    {
        if (!(duration > 0) || new[] { markers.Intro, markers.Outro }.Any(r => r is not null && !r.Valid(duration))) return false;
        state.Markers[series ? SeriesKey(media) : EpisodeKey(media)] = markers;
        return true;
    }

    public static void Clear(PlayerPersonalState state, GlobalSearchResult media, bool series) =>
        state.Markers.Remove(series ? SeriesKey(media) : EpisodeKey(media));

    /// The range the playhead is in, with its button label ("Skip Intro" / "Skip Outro"), or null.
    public static (string Label, PlayerSkipRange Range)? ActiveSkip(PlayerSkipMarkers markers, double elapsed, double duration)
    {
        if (markers.Intro is { } intro && intro.Valid(duration) && intro.Contains(elapsed)) return ("Skip Intro", intro);
        if (markers.Outro is { } outro && outro.Valid(duration) && outro.Contains(elapsed)) return ("Skip Outro", outro);
        return null;
    }

    /// Seek target in seconds when automatic skipping applies now; null to keep playing.
    public static double? AutomaticSkipTarget(PlayerPersonalState state, GlobalSearchResult media, double elapsed, double duration, bool seekable)
    {
        if (!state.AutomaticSkipping || !seekable || !(duration > 0)) return null;
        return ActiveSkip(Current(state, media), elapsed, duration)?.Range.End;
    }

    /// Default editor values when no markers exist: intro 0–60 s, outro the last 60 s.
    public static PlayerSkipMarkers EditorDefaults(PlayerSkipMarkers current, double duration) => new(
        current.Intro ?? new PlayerSkipRange(0, Math.Min(60, duration)),
        current.Outro ?? new PlayerSkipRange(Math.Max(0, duration - 60), duration));
}

public enum PlayerState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error,
}

/// VLC may report the same terminal state repeatedly; each media load finishes only once.
public sealed class PlaybackCompletionState
{
    private bool _hasPlayed;
    private bool _handled;

    public void Reset()
    {
        _hasPlayed = false;
        _handled = false;
    }

    public bool Observe(PlayerState state)
    {
        if (state is PlayerState.Playing or PlayerState.Paused) _hasPlayed = true;
        if (_handled || !(state == PlayerState.Ended || (state == PlayerState.Stopped && _hasPlayed))) return false;
        _handled = true;
        return true;
    }
}

public static class PlayerTime
{
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var total = (long)seconds;
        var (hours, minutes, secs) = (total / 3600, total % 3600 / 60, total % 60);
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
