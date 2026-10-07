using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// OpenSubtitles.com credentials. Saved only in the secret store (ISecretStore).
public sealed record SubtitleCredentials(string ApiKey = "", string Username = "", string Password = "");

public enum SubtitleErrorKind
{
    MissingKey,
    Keychain,
    Authentication,
    Quota,
    RateLimited,
    InvalidResponse,
    UnsafeUrl,
    Oversized,
    InvalidFile,
    InvalidSearch,
    Network,
    Server,
}

public sealed class SubtitleException(SubtitleErrorKind kind, int status = 0) : Exception(kind switch
{
    SubtitleErrorKind.MissingKey => "Add your OpenSubtitles.com API key in Settings first. This is separate from OMDb.",
    SubtitleErrorKind.Keychain => "Myra could not access saved subtitle credentials.",
    SubtitleErrorKind.Authentication => "OpenSubtitles rejected the API key or account. Check Subtitle settings and sign in again.",
    SubtitleErrorKind.Quota => "The OpenSubtitles download allowance is exhausted or unavailable. Check your account allowance before trying again.",
    SubtitleErrorKind.RateLimited => "OpenSubtitles is rate limiting requests. Please wait before trying again.",
    SubtitleErrorKind.InvalidResponse => "OpenSubtitles returned an invalid response.",
    SubtitleErrorKind.UnsafeUrl => "The subtitle service returned an untrusted address.",
    SubtitleErrorKind.Oversized => "The subtitle response exceeds the safe size limit.",
    SubtitleErrorKind.InvalidFile => "The download is not a supported text subtitle.",
    SubtitleErrorKind.InvalidSearch => "Enter a title and valid year/season/episode numbers.",
    SubtitleErrorKind.Network => "Subtitles are unavailable. Check your connection and try again.",
    _ => $"OpenSubtitles returned HTTP {status}. Try again later.",
})
{
    public SubtitleErrorKind Kind { get; } = kind;
    public int Status { get; } = status;
}

public sealed record OnlineSubtitleResult(int Id, string Filename, string Release, string Language, double? Fps, int Downloads, bool HearingImpaired, bool Trusted);

public sealed record SubtitleSearchPage(IReadOnlyList<OnlineSubtitleResult> Results, bool HasMore);

public static class SubtitleUrlPolicy
{
    public static readonly IReadOnlySet<string> ApiHosts = new HashSet<string> { "api.opensubtitles.com", "vip-api.opensubtitles.com" };

    /// HTTPS only, default port, no user info. API calls use the two API hosts; file downloads any
    /// opensubtitles.com host.
    public static bool Accepts(Uri url, bool download)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || url.UserInfo.Length > 0 || url.Port != 443)
            return false;
        var host = url.Host.ToLowerInvariant();
        return download ? host == "opensubtitles.com" || host.EndsWith(".opensubtitles.com", StringComparison.Ordinal) : ApiHosts.Contains(host);
    }
}

/// App-owned subtitle cache. Files are named "MyraSub-{fileId}.srt"; provider names never become
/// paths. Files expire after 30 days and the owned total stays under 100 MB. Symbolic links are refused.
public sealed partial class SubtitleCache
{
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public const long MaximumTotalBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly Lock _lock = new();

    public SubtitleCache(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? AppPaths.SubtitleCacheDirectory);
        Directory.CreateDirectory(_directory);
        if (new DirectoryInfo(_directory).LinkTarget is not null) throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
    }

    public string DirectoryPath => _directory;

    private string FileFor(int fileId) => Path.Combine(_directory, $"MyraSub-{fileId.ToString(CultureInfo.InvariantCulture)}.srt");

    /// A cached, unexpired regular file for this provider file ID, or null.
    public string? Cached(int fileId, DateTimeOffset? now = null)
    {
        if (fileId <= 0) return null;
        var info = new FileInfo(FileFor(fileId));
        lock (_lock)
        {
            if (!info.Exists || info.LinkTarget is not null || info.Length <= 0 || info.Length > MaximumFileBytes) return null;
            if (info.LastWriteTimeUtc <= ((now ?? DateTimeOffset.UtcNow) - Lifetime).UtcDateTime) return null;
            return info.FullName;
        }
    }

    [GeneratedRegex(@"\d{1,2}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{1,2}:\d{2}:\d{2}[,.]\d{3}")]
    private static partial Regex CuePattern();

    /// Accepts only bounded SRT text with at least one cue. Returns the stored path.
    public string Store(byte[] data, int fileId)
    {
        if (fileId <= 0) throw new SubtitleException(SubtitleErrorKind.InvalidFile);
        if (data.Length > MaximumFileBytes) throw new SubtitleException(SubtitleErrorKind.Oversized);
        if (!LooksLikeSrt(data)) throw new SubtitleException(SubtitleErrorKind.InvalidFile);
        var path = FileFor(fileId);
        lock (_lock)
        {
            if (new FileInfo(path).LinkTarget is not null) throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
            var temporary = Path.Combine(_directory, $".MyraSub-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
            Prune(path);
        }
        return path;
    }

    internal static bool LooksLikeSrt(byte[] data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data) : Encoding.Latin1.GetString(data);
        }
        return !text.Contains("<html", StringComparison.OrdinalIgnoreCase) && CuePattern().IsMatch(text);
    }

    /// Removes owned files that expired or exceed the total budget, oldest first. Other files are untouched.
    private void Prune(string protectedPath)
    {
        var owned = new DirectoryInfo(_directory).EnumerateFiles("MyraSub-*.srt")
            .Where(f => f.LinkTarget is null && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = owned.Sum(f => f.Length);
        var cutoff = DateTime.UtcNow - Lifetime;
        foreach (var file in owned)
        {
            if (file.FullName == protectedPath) continue;
            if (file.LastWriteTimeUtc < cutoff || total > MaximumTotalBytes)
            {
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

/// OpenSubtitles.com REST client. Searches send title/year/season/episode (or IMDb ID), never media URLs.
/// Downloads are user-selected, never retried automatically (they consume allowance), and the file
/// request carries no API key or token.
public sealed partial class OpenSubtitlesService
{
    private const int ApiLimit = 256 * 1024;
    private const int SearchLimit = 2 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _session = new(1, 1);
    private SubtitleCredentials? _authenticated;
    private string _token = "";
    private string _apiHost = "api.opensubtitles.com";
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;

    public OpenSubtitlesService(HttpClient? client = null) => _client = client ?? SafeHttp.CreateClient(TimeSpan.FromSeconds(45));

    public void ResetSession()
    {
        _authenticated = null;
        _token = "";
        _apiHost = "api.opensubtitles.com";
    }

    /// Signs in with the account and returns the allowed downloads, when reported.
    public async Task<int?> SignInAsync(SubtitleCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (credentials.Username.Length == 0 || credentials.Password.Length == 0) throw new SubtitleException(SubtitleErrorKind.Authentication);
        ResetSession();
        var body = JsonSerializer.Serialize(new { username = credentials.Username, password = credentials.Password });
        var data = await ApiAsync("login", HttpMethod.Post, body, credentials, ApiLimit, cancellationToken);
        LoginReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<LoginReply>(data);
        }
        catch (JsonException)
        {
            reply = null;
        }
        if (reply is null || string.IsNullOrEmpty(reply.Token) || !SubtitleUrlPolicy.ApiHosts.Contains(reply.BaseUrl ?? ""))
            throw new SubtitleException(SubtitleErrorKind.InvalidResponse);
        _authenticated = credentials;
        _token = reply.Token;
        _apiHost = reply.BaseUrl!;
        _tokenExpires = DateTimeOffset.UtcNow.AddMinutes(20);
        return reply.User?.AllowedDownloads;
    }

    private async Task AuthenticateIfNeededAsync(SubtitleCredentials credentials, CancellationToken cancellationToken)
    {
        if (_authenticated != credentials || DateTimeOffset.UtcNow >= _tokenExpires) ResetSession();
        if ((credentials.Username.Length > 0 || credentials.Password.Length > 0) && _token.Length == 0)
            await SignInAsync(credentials, cancellationToken);
    }

    [GeneratedRegex("^[a-z]{2,3}(?:-[a-z]{2})?$")]
    private static partial Regex LanguagePattern();

    public async Task<SubtitleSearchPage> SearchAsync(
        MediaIdentity identity, string language, SubtitleCredentials credentials, int page = 1, CancellationToken cancellationToken = default)
    {
        if (identity.Title.Trim().Length == 0 || identity.Title.Length > 200 || page <= 0
            || (language.Length > 0 && !LanguagePattern().IsMatch(language)))
            throw new SubtitleException(SubtitleErrorKind.InvalidSearch);
        var items = new List<(string Name, string Value)>
        {
            ("query", identity.Title),
            ("page", page.ToString(CultureInfo.InvariantCulture)),
            ("order_by", "download_count"),
            ("order_direction", "desc"),
        };
        if (language.Length > 0) items.Add(("languages", language));
        foreach (var (name, value) in new[] { ("year", identity.Year), ("season_number", identity.Season), ("episode_number", identity.Episode) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 9999)
                throw new SubtitleException(SubtitleErrorKind.InvalidSearch);
            items.Add((name, number.ToString(CultureInfo.InvariantCulture)));
        }
        if (identity.ImdbId?.Replace("tt", "") is { Length: > 0 } imdb && long.TryParse(imdb, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            items.Add(("imdb_id", imdb));
        ValidateKey(credentials);
        await _session.WaitAsync(cancellationToken);
        try
        {
            await AuthenticateIfNeededAsync(credentials, cancellationToken);
            var query = string.Join('&', items.OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => Uri.EscapeDataString(i.Name) + "=" + Uri.EscapeDataString(i.Value)));
            var data = await ApiAsync("subtitles?" + query, HttpMethod.Get, null, credentials, SearchLimit, cancellationToken);
            SearchReply? reply;
            try
            {
                reply = JsonSerializer.Deserialize<SearchReply>(data);
            }
            catch (JsonException)
            {
                reply = null;
            }
            if (reply?.Data is null) throw new SubtitleException(SubtitleErrorKind.InvalidResponse);
            var seen = new HashSet<int>();
            var results = new List<OnlineSubtitleResult>();
            foreach (var attributes in reply.Data.Select(d => d.Attributes))
            {
                if (attributes?.Files is null) continue;
                foreach (var file in attributes.Files)
                {
                    if (file.FileId <= 0 || !seen.Add(file.FileId)) continue;
                    results.Add(new OnlineSubtitleResult(
                        file.FileId, file.FileName ?? "", attributes.Release is { Length: > 0 } release ? release : file.FileName ?? "",
                        attributes.Language ?? "", attributes.Fps, attributes.DownloadCount ?? 0,
                        attributes.HearingImpaired ?? false, attributes.FromTrusted ?? false));
                }
            }
            return new SubtitleSearchPage(results, page < (reply.TotalPages ?? page));
        }
        finally
        {
            _session.Release();
        }
    }

    /// Downloads one selected subtitle into the cache. Returns the local path and remaining allowance.
    public async Task<(string Path, int? Remaining)> DownloadAsync(
        OnlineSubtitleResult result, SubtitleCredentials credentials, SubtitleCache cache, CancellationToken cancellationToken = default)
    {
        if (cache.Cached(result.Id) is { } existing) return (existing, null);
        if (result.Id <= 0) throw new SubtitleException(SubtitleErrorKind.InvalidFile);
        ValidateKey(credentials);
        DownloadReply? reply;
        await _session.WaitAsync(cancellationToken);
        try
        {
            await AuthenticateIfNeededAsync(credentials, cancellationToken);
            var body = JsonSerializer.Serialize(new { file_id = result.Id, sub_format = "srt" });
            // No automatic retry: this POST may consume the user's provider allowance.
            var data = await ApiAsync("download", HttpMethod.Post, body, credentials, ApiLimit, cancellationToken);
            try
            {
                reply = JsonSerializer.Deserialize<DownloadReply>(data);
            }
            catch (JsonException)
            {
                reply = null;
            }
        }
        finally
        {
            _session.Release();
        }
        if (reply?.Link is null || !Uri.TryCreate(reply.Link, UriKind.Absolute, out var link) || !SubtitleUrlPolicy.Accepts(link, download: true))
            throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
        var subtitle = await SendAsync(link, () =>
        {
            // Credential-free file request.
            var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.UserAgent.ParseAdd("Myra v1.0");
            return request;
        }, download: true, SubtitleCache.MaximumFileBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (cache.Store(subtitle, result.Id), reply.Remaining);
    }

    private static string ValidateKey(SubtitleCredentials credentials)
    {
        var key = credentials.ApiKey.Trim();
        if (key.Length == 0 || key.Contains('\n') || key.Contains('\r')) throw new SubtitleException(SubtitleErrorKind.MissingKey);
        return key;
    }

    private Task<byte[]> ApiAsync(string path, HttpMethod method, string? body, SubtitleCredentials credentials, int limit, CancellationToken cancellationToken)
    {
        var key = ValidateKey(credentials);
        var url = new Uri($"https://{_apiHost}/api/v1/{path}");
        var token = _token;
        return SendAsync(url, () =>
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Api-Key", key);
            request.Headers.UserAgent.ParseAdd("Myra v1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, download: false, limit, cancellationToken);
    }

    private async Task<byte[]> SendAsync(Uri url, Func<HttpRequestMessage> build, bool download, int limit, CancellationToken cancellationToken)
    {
        if (!SubtitleUrlPolicy.Accepts(url, download)) throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
        try
        {
            // API redirects must stay on the same host; file redirects stay on opensubtitles.com and
            // are rebuilt without credentials.
            var originalHost = url.Host;
            var (response, final) = await SafeHttp.SendAsync(_client, url, target =>
            {
                if (target == url) return build();
                var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.UserAgent.ParseAdd("Myra v1.0");
                return request;
            }, target => SubtitleUrlPolicy.Accepts(target, download) && (download || target.Host == originalHost), cancellationToken);
            using (response)
            {
                if (!SubtitleUrlPolicy.Accepts(final, download)) throw new SubtitleException(SubtitleErrorKind.UnsafeUrl);
                var status = (int)response.StatusCode;
                switch (status)
                {
                    case >= 200 and < 300:
                        break;
                    case 401 or 403:
                        ResetSession();
                        throw new SubtitleException(SubtitleErrorKind.Authentication);
                    case 406:
                        throw new SubtitleException(SubtitleErrorKind.Quota);
                    case 429:
                        throw new SubtitleException(SubtitleErrorKind.RateLimited);
                    default:
                        throw new SubtitleException(SubtitleErrorKind.Server, status);
                }
                var data = await SafeHttp.ReadBoundedAsync(response, limit, () => new SubtitleException(SubtitleErrorKind.Oversized), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return data;
            }
        }
        catch (HttpRequestException)
        {
            throw new SubtitleException(SubtitleErrorKind.Network);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SubtitleException(SubtitleErrorKind.Network);
        }
    }

    private sealed class LoginReply
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
        [JsonPropertyName("user")] public LoginUser? User { get; set; }
    }

    private sealed class LoginUser
    {
        [JsonPropertyName("allowed_downloads")] public int? AllowedDownloads { get; set; }
    }

    private sealed class DownloadReply
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("remaining")] public int? Remaining { get; set; }
    }

    private sealed class SearchReply
    {
        [JsonPropertyName("data")] public List<SearchItem>? Data { get; set; }
        [JsonPropertyName("total_pages")] public int? TotalPages { get; set; }
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("attributes")] public SearchAttributes? Attributes { get; set; }
    }

    private sealed class SearchAttributes
    {
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("release")] public string? Release { get; set; }
        [JsonPropertyName("fps")] public double? Fps { get; set; }
        [JsonPropertyName("download_count")] public int? DownloadCount { get; set; }
        [JsonPropertyName("hearing_impaired")] public bool? HearingImpaired { get; set; }
        [JsonPropertyName("from_trusted")] public bool? FromTrusted { get; set; }
        [JsonPropertyName("files")] public List<SearchFile>? Files { get; set; }
    }

    private sealed class SearchFile
    {
        [JsonPropertyName("file_id")] public int FileId { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
    }
}
