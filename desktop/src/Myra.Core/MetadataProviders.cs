using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Myra.Core;

public enum DiscoveryErrorKind
{
    MissingToken,
    InvalidResponse,
    Ambiguous,
}

public sealed class DiscoveryException(DiscoveryErrorKind kind) : Exception(kind switch
{
    DiscoveryErrorKind.MissingToken => "Add a TMDB API Read Access Token in Settings to enrich Home.",
    DiscoveryErrorKind.InvalidResponse => "TMDB is unavailable or returned an invalid response. Try again later.",
    _ => "No confident metadata match. Use Correct Match to choose the title and year.",
})
{
    public DiscoveryErrorKind Kind { get; } = kind;
}

/// TMDB discovery. Requests send only title/year or a TMDB ID, over HTTPS to api.themoviedb.org,
/// at most one request per 0.3 s, with 3 MB response bodies. Results are cached in the index.
public sealed class DiscoveryService
{
    public const string Host = "api.themoviedb.org";
    public const int ResponseLimit = 3_000_000;
    public const string AttributionText = "This product uses the TMDB API but is not endorsed or certified by TMDB.";

    private readonly HttpClient _client;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public DiscoveryService(HttpClient? client = null, TimeSpan? minimumInterval = null)
    {
        _client = client ?? SafeHttp.CreateClient(TimeSpan.FromSeconds(20));
        _interval = minimumInterval ?? TimeSpan.FromSeconds(0.3);
    }

    private sealed class SearchReply
    {
        [JsonPropertyName("results")] public List<Item> Results { get; set; } = [];
    }

    private sealed class SeasonReply
    {
        [JsonPropertyName("episodes")] public List<EpisodeReply> Episodes { get; set; } = [];
    }

    private sealed class EpisodeReply
    {
        [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
        [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
        [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    }

    private sealed class GenreReply
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class Item
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; set; }
        [JsonPropertyName("vote_count")] public int? VoteCount { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
        [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
        [JsonPropertyName("genres")] public List<GenreReply>? Genres { get; set; }
    }

    private async Task<T> RequestAsync<T>(string path, IEnumerable<(string Name, string Value)> query, string token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _interval - (DateTimeOffset.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            var text = string.Join('&', query.Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value)));
            var url = new Uri($"https://{Host}/3/{path}" + (text.Length > 0 ? "?" + text : ""));
            _lastRequest = DateTimeOffset.UtcNow;
            HttpResponseMessage response;
            try
            {
                (response, _) = await SafeHttp.SendAsync(_client, url, target =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, target);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return request;
                }, target => target.Scheme == Uri.UriSchemeHttps && target.Host == Host && target.IsDefaultPort, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new DiscoveryException(DiscoveryErrorKind.InvalidResponse);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DiscoveryException(DiscoveryErrorKind.InvalidResponse);
            }
            using (response)
            {
                if (!SafeHttp.IsSuccess(response.StatusCode)) throw new DiscoveryException(DiscoveryErrorKind.InvalidResponse);
                var data = await SafeHttp.ReadBoundedAsync(response, ResponseLimit, () => new DiscoveryException(DiscoveryErrorKind.InvalidResponse), cancellationToken);
                try
                {
                    return JsonSerializer.Deserialize<T>(data) ?? throw new DiscoveryException(DiscoveryErrorKind.InvalidResponse);
                }
                catch (JsonException)
                {
                    throw new DiscoveryException(DiscoveryErrorKind.InvalidResponse);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Looks up a title. A correction's TMDB ID skips search. Without it, exactly one exact title
    /// (and year) match is required; otherwise DiscoveryErrorKind.Ambiguous asks for Correct Match.
    public async Task<EntertainmentMetadata> LookupAsync(
        EntertainmentTitle title, EntertainmentMatchCorrection? correction, string token, LibraryIndex index,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = title.MetadataCacheKey(correction?.ProviderId);
        if (index.CachedMetadata(cacheKey) is { } cached && EntertainmentMetadata.Decode(cached) is { } hit) return hit;
        if (string.IsNullOrWhiteSpace(token)) throw new DiscoveryException(DiscoveryErrorKind.MissingToken);
        token = token.Trim();
        var category = title.Kind == EntertainmentKind.Movie ? "movie" : "tv";
        int id;
        if (correction?.ProviderId is { } corrected)
        {
            id = corrected;
        }
        else
        {
            var query = new List<(string, string)> { ("query", title.Name), ("include_adult", "false") };
            if (title.Year is { } year) query.Add((title.Kind == EntertainmentKind.Movie ? "primary_release_year" : "first_air_date_year", year));
            var search = await RequestAsync<SearchReply>("search/" + category, query, token, cancellationToken);
            var wanted = TextFolding.Fold(title.Name);
            var exact = search.Results.Where(r =>
                TextFolding.Fold(r.Title ?? r.Name ?? "") == wanted
                && (title.Year is null || (r.ReleaseDate ?? r.FirstAirDate ?? "").StartsWith(title.Year, StringComparison.Ordinal))).ToList();
            if (exact.Count != 1) throw new DiscoveryException(DiscoveryErrorKind.Ambiguous);
            id = exact[0].Id;
        }

        var item = await RequestAsync<Item>($"{category}/{id}", [], token, cancellationToken);
        Dictionary<string, string>? dates = null;
        if (title.Kind == EntertainmentKind.Series)
        {
            dates = [];
            foreach (var season in title.Versions.Where(v => v.Season is not null).Select(v => v.Season!.Value).Distinct().Order().Take(50))
            {
                var seasonKey = $"tmdb-season|{id}|{season}";
                SeasonReply? payload = null;
                if (index.CachedMetadata(seasonKey) is { } seasonCache)
                {
                    try
                    {
                        payload = JsonSerializer.Deserialize<SeasonReply>(seasonCache);
                    }
                    catch (JsonException)
                    {
                    }
                }
                if (payload is null)
                {
                    payload = await RequestAsync<SeasonReply>($"tv/{id}/season/{season}", [], token, cancellationToken);
                    index.SaveMetadata(seasonKey, JsonSerializer.Serialize(payload));
                }
                var available = title.Versions.Where(v => v.Season == season && v.Episode is not null).Select(v => v.Episode!.Value).ToHashSet();
                foreach (var episode in payload.Episodes)
                    if (available.Contains(episode.EpisodeNumber) && !string.IsNullOrEmpty(episode.AirDate))
                        dates[$"{season}|{episode.EpisodeNumber}"] = episode.AirDate;
            }
        }
        var result = new EntertainmentMetadata
        {
            ProviderId = item.Id,
            Title = item.Title ?? item.Name ?? title.Name,
            Overview = item.Overview ?? "",
            PosterUrl = item.PosterPath is { Length: > 0 } poster && Uri.TryCreate("https://image.tmdb.org/t/p/w500" + poster, UriKind.Absolute, out var p) ? p : null,
            Rating = item.VoteAverage ?? 0,
            VoteCount = item.VoteCount ?? 0,
            ReleaseDate = item.ReleaseDate ?? item.FirstAirDate,
            Genres = item.Genres?.Select(g => g.Name).ToList() ?? [],
            Language = item.OriginalLanguage ?? "",
            EpisodeReleaseDates = dates,
        };
        var json = JsonSerializer.Serialize(result, SwiftJson.Options);
        index.SaveMetadata(cacheKey, json);
        index.SaveMetadata($"tmdb-base|{title.Id}|{correction?.ProviderId ?? 0}", json);
        return result;
    }
}

public enum MetadataErrorKind
{
    MissingKey,
    Network,
    BadResponse,
    NotFound,
}

public sealed class MetadataException(MetadataErrorKind kind, string? reason = null) : Exception(kind switch
{
    MetadataErrorKind.MissingKey => "Add an OMDb API key in Settings to load online information.",
    MetadataErrorKind.Network => "Online metadata is unavailable. Check your connection and try again.",
    MetadataErrorKind.BadResponse => "OMDb returned an invalid response.",
    _ => reason ?? "No matching movie or episode was found.",
})
{
    public MetadataErrorKind Kind { get; } = kind;
}

/// OMDb reply. "N/A" values are hidden by Available().
public sealed record MovieMetadata
{
    [JsonPropertyName("Title")] public string? Title { get; init; }
    [JsonPropertyName("Year")] public string? Year { get; init; }
    [JsonPropertyName("imdbRating")] public string? ImdbRating { get; init; }
    [JsonPropertyName("imdbID")] public string? ImdbId { get; init; }
    [JsonPropertyName("Plot")] public string? Plot { get; init; }
    [JsonPropertyName("Genre")] public string? Genre { get; init; }
    [JsonPropertyName("Director")] public string? Director { get; init; }
    [JsonPropertyName("Actors")] public string? Actors { get; init; }
    [JsonPropertyName("Runtime")] public string? Runtime { get; init; }
    [JsonPropertyName("Rated")] public string? Rated { get; init; }
    [JsonPropertyName("Released")] public string? Released { get; init; }
    [JsonPropertyName("Language")] public string? Language { get; init; }
    [JsonPropertyName("Country")] public string? Country { get; init; }
    [JsonPropertyName("Awards")] public string? Awards { get; init; }
    [JsonPropertyName("Poster")] public string? Poster { get; init; }
    [JsonPropertyName("Response")] public string? Response { get; init; }
    [JsonPropertyName("Error")] public string? Error { get; init; }

    public static string? Available(string? value) => value is null or "" or "N/A" ? null : value;

    public const string License = "Online metadata is provided by OMDb under CC BY-NC 4.0 for personal noncommercial use.";
}

/// OMDb lookups for the inspector. Requests carry the API key and title/year/season/episode or an
/// IMDb ID only, over HTTPS to www.omdbapi.com. Successful replies are cached in the index for 7 days.
public sealed partial class MetadataService
{
    public const string Host = "www.omdbapi.com";
    public const int ResponseLimit = 3_000_000;
    private readonly HttpClient _client;

    public MetadataService(HttpClient? client = null) => _client = client ?? SafeHttp.CreateClient(TimeSpan.FromSeconds(15));

    [GeneratedRegex("^tt[0-9]{7,10}$")]
    private static partial Regex ImdbPattern();

    public async Task<MovieMetadata> LookupAsync(MediaIdentity identity, string key, LibraryIndex? index, CancellationToken cancellationToken = default)
    {
        if (index?.CachedMetadata(identity.CacheKey) is { } cached)
        {
            try
            {
                if (JsonSerializer.Deserialize<MovieMetadata>(cached) is { } hit) return hit;
            }
            catch (JsonException)
            {
            }
        }
        key = key.Trim();
        if (key.Length == 0) throw new MetadataException(MetadataErrorKind.MissingKey);
        var items = new List<(string, string)> { ("apikey", key), ("plot", "full") };
        if (identity.ImdbId is { } imdb && ImdbPattern().IsMatch(imdb)) items.Add(("i", imdb));
        else
        {
            items.Add(("t", identity.Title));
            if (identity.Year is { } year) items.Add(("y", year));
        }
        if (identity.Season is { } season) items.Add(("Season", season));
        if (identity.Episode is { } episode) items.Add(("Episode", episode));
        var url = new Uri($"https://{Host}/?" + string.Join('&', items.Select(i => i.Item1 + "=" + Uri.EscapeDataString(i.Item2))));

        byte[] data;
        try
        {
            var (response, _) = await SafeHttp.SendAsync(_client, url, target => new HttpRequestMessage(HttpMethod.Get, target),
                target => target.Scheme == Uri.UriSchemeHttps && target.Host == Host, cancellationToken);
            using (response)
            {
                if (!SafeHttp.IsSuccess(response.StatusCode)) throw new MetadataException(MetadataErrorKind.BadResponse);
                data = await SafeHttp.ReadBoundedAsync(response, ResponseLimit, () => new MetadataException(MetadataErrorKind.BadResponse), cancellationToken);
            }
        }
        catch (HttpRequestException)
        {
            throw new MetadataException(MetadataErrorKind.Network);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetadataException(MetadataErrorKind.Network);
        }
        cancellationToken.ThrowIfCancellationRequested();
        MovieMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<MovieMetadata>(data) ?? throw new MetadataException(MetadataErrorKind.BadResponse);
        }
        catch (JsonException)
        {
            throw new MetadataException(MetadataErrorKind.BadResponse);
        }
        if (metadata.Response != "True") throw new MetadataException(MetadataErrorKind.NotFound, metadata.Error);
        index?.SaveMetadata(identity.CacheKey, Encoding.UTF8.GetString(data));
        return metadata;
    }
}
