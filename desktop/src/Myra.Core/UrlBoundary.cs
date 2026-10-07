namespace Myra.Core;

public enum DirectoryErrorKind
{
    InvalidUrl,
    OutsideCategoryRoot,
    UnsupportedListing,
    BadResponse,
    UnsafeDestination,
}

public sealed class DirectoryException : Exception
{
    public DirectoryException(DirectoryErrorKind kind, int statusCode = 0) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public DirectoryErrorKind Kind { get; }
    public int StatusCode { get; }

    private static string Describe(DirectoryErrorKind kind, int statusCode) => kind switch
    {
        DirectoryErrorKind.InvalidUrl => "The address is not a valid HTTP URL.",
        DirectoryErrorKind.OutsideCategoryRoot => "The address is outside this category.",
        DirectoryErrorKind.UnsupportedListing => "This page is not a supported directory listing.",
        DirectoryErrorKind.BadResponse => $"The server returned HTTP status {statusCode}.",
        _ => "A downloaded path would leave the configured folder.",
    };
}

public static class UriExtensions
{
    /// Drops query and fragment; System.Uri already removes dot segments.
    public static Uri StandardizedDirectoryUrl(this Uri url) =>
        url.IsAbsoluteUri && !url.IsFile ? new Uri(url.GetLeftPart(UriPartial.Path)) : url;

    public static string LastPathComponent(this Uri url)
    {
        var path = url.AbsolutePath.TrimEnd('/');
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    public static Uri Parent(this Uri url)
    {
        var path = url.AbsolutePath;
        var trimmed = path.EndsWith('/') ? path[..^1] : path;
        var index = trimmed.LastIndexOf('/');
        return new Uri(url, index < 0 ? "/" : trimmed[..(index + 1)]);
    }

    public static bool SameAs(this Uri left, Uri right) =>
        string.Equals(left.StandardizedDirectoryUrl().AbsoluteUri, right.StandardizedDirectoryUrl().AbsoluteUri, StringComparison.Ordinal);
}

/// Every link must stay inside the source root's scheme, host, port and path.
public sealed class UrlBoundary
{
    public UrlBoundary(Uri root)
    {
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || string.IsNullOrEmpty(root.Host))
            throw new DirectoryException(DirectoryErrorKind.InvalidUrl);
        Root = root.StandardizedDirectoryUrl();
    }

    public Uri Root { get; }

    public bool Contains(Uri candidate)
    {
        if (!candidate.IsAbsoluteUri) return false;
        candidate = candidate.StandardizedDirectoryUrl();
        if (!string.Equals(candidate.Scheme, Root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, Root.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != Root.Port)
            return false;

        var rootPath = SafeEncodedPath(Root);
        var candidatePath = SafeEncodedPath(candidate);
        if (rootPath is null || candidatePath is null) return false;
        if (!rootPath.EndsWith('/')) rootPath += "/";
        return candidatePath == rootPath[..^1] || candidatePath.StartsWith(rootPath, StringComparison.Ordinal);
    }

    private static string? SafeEncodedPath(Uri url)
    {
        var path = url.AbsolutePath;
        foreach (var segment in path.Split('/'))
        {
            var decoded = segment;
            for (var i = 0; i < 2; i++) decoded = Uri.UnescapeDataString(decoded);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\')) return null;
        }
        return path;
    }
}
