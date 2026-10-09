using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Myra.Core;

public delegate Task<DirectoryListing> ListingLoader(Uri url, UrlBoundary boundary, CancellationToken cancellationToken);

/// Reads h5ai, Apache and nginx directory listings.
public sealed partial class DirectoryService
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public DirectoryService(HttpClient? client = null) => _client = client ?? SharedClient;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Myra", "2.0"));
        return client;
    }

    public Task<DirectoryListing> ValidateAsync(Uri rootUrl, CancellationToken cancellationToken = default)
    {
        var boundary = new UrlBoundary(rootUrl);
        return ListingAsync(boundary.Root, boundary, cancellationToken);
    }

    public async Task<DirectoryListing> ListingAsync(Uri url, UrlBoundary boundary, CancellationToken cancellationToken = default) =>
        (await ConditionalListingAsync(url, boundary, null, cancellationToken).ConfigureAwait(false)).Listing;

    /// Conditional GET: sends If-None-Match (preferred) or If-Modified-Since from the cached snapshot.
    /// A 304 reply returns the cached snapshot with a new check time and skips HTML parsing.
    public async Task<IndexedFolder> ConditionalListingAsync(
        Uri url, UrlBoundary boundary, IndexedFolder? cached, CancellationToken cancellationToken = default)
    {
        if (!boundary.Contains(url)) throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cached?.ETag is { } tag) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
        else if (cached?.LastModified is { } modified) request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Redirects must stay inside the source boundary.
        if (response.RequestMessage?.RequestUri is { } final && !boundary.Contains(final))
            throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
            return cached with { Checked = DateTimeOffset.Now };
        if (!response.IsSuccessStatusCode)
            throw new DirectoryException(DirectoryErrorKind.BadResponse, (int)response.StatusCode);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var html = Decode(data);
        var parsed = Parse(html, url, boundary);
        if (parsed.Count == 0 && !html.Contains("Parent Directory", StringComparison.OrdinalIgnoreCase))
            throw new DirectoryException(DirectoryErrorKind.UnsupportedListing);
        var artwork = parsed.FirstOrDefault(MediaFileType.IsArtwork)?.Url;
        var visible = parsed.Where(e => e.Kind == EntryKind.Folder || MediaFileType.IsVideo(e)).ToList();
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() : null);
        var lastModified = response.Content.Headers.TryGetValues("Last-Modified", out var dates) ? dates.FirstOrDefault() : null;
        return new IndexedFolder(new DirectoryListing(url, visible, artwork), DateTimeOffset.Now, etag, lastModified);
    }

    private static string Decode(byte[] data)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    public static List<DirectoryEntry> Parse(string html, Uri baseUrl, UrlBoundary boundary)
    {
        var document = new HtmlParser().ParseDocument(html);
        var entries = new List<DirectoryEntry>();
        var seen = new HashSet<string>();

        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var anchor = row.QuerySelector("a[href]");
            if (anchor is null) continue;
            var href = anchor.GetAttribute("href") ?? "";
            if (!TryResolve(href, baseUrl, out var entryUrl) || !IsUsableLink(href, entryUrl, baseUrl, boundary)) continue;

            var imageAlt = row.QuerySelector("img")?.GetAttribute("alt") ?? "";
            var folder = imageAlt.Contains("folder", StringComparison.OrdinalIgnoreCase) || href.EndsWith('/');
            var name = CleanName(anchor.TextContent, entryUrl);
            if (name.Length == 0 || !seen.Add(entryUrl.AbsoluteUri)) continue;

            var texts = row.QuerySelectorAll("td").Select(cell => cell.TextContent).ToList();
            long? size = folder ? null : texts.AsEnumerable().Reverse().Select(ParseByteSize).FirstOrDefault(v => v is not null);
            var date = texts.Select(ParseDate).FirstOrDefault(v => v is not null);
            entries.Add(new DirectoryEntry(name, entryUrl, folder ? EntryKind.Folder : EntryKind.File, size, date));
        }

        if (entries.Count == 0)
        {
            foreach (var anchor in document.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href") ?? "";
                if (!TryResolve(href, baseUrl, out var entryUrl) || !IsUsableLink(href, entryUrl, baseUrl, boundary)) continue;
                var name = CleanName(anchor.TextContent, entryUrl);
                if (name.Length == 0 || !seen.Add(entryUrl.AbsoluteUri)) continue;
                entries.Add(new DirectoryEntry(name, entryUrl, href.EndsWith('/') ? EntryKind.Folder : EntryKind.File));
            }
        }

        entries.Sort((a, b) => a.Kind != b.Kind
            ? (a.Kind == EntryKind.Folder ? -1 : 1)
            : NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return entries;
    }

    private static bool TryResolve(string href, Uri baseUrl, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(baseUrl, href, out var resolved)) return false;
        url = resolved.StandardizedDirectoryUrl();
        return true;
    }

    private static bool IsUsableLink(string href, Uri url, Uri baseUrl, UrlBoundary boundary)
    {
        var lower = href.ToLowerInvariant();
        return href is not (".." or "../" or "." or "./")
            && !href.StartsWith('#')
            && !lower.StartsWith("javascript:")
            && !lower.StartsWith("mailto:")
            && boundary.Contains(url)
            && !url.SameAs(baseUrl);
    }

    private static string CleanName(string text, Uri url)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0 && !trimmed.Equals("Parent Directory", StringComparison.OrdinalIgnoreCase))
            return trimmed.Trim('/');
        return url.LastPathComponent();
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([KMGTPE]?B)$")]
    private static partial Regex ByteSizeRegex();

    internal static long? ParseByteSize(string text)
    {
        var compact = text.Trim().ToUpperInvariant().Replace("IB", "B");
        var match = ByteSizeRegex().Match(compact);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        var power = "BKMGTPE".IndexOf(match.Groups[2].Value[0]);
        if (match.Groups[2].Value == "B") power = 0;
        return (long)(number * Math.Pow(1024, power));
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm", "dd-MMM-yyyy HH:mm", "yyyy-MM-dd HH:mm:ss"];

    internal static DateTimeOffset? ParseDate(string text)
    {
        var value = text.Trim();
        if (value.Length < 16 || !value.Contains(':')) return null;
        return DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
    }
}
