using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// Imported subtitles, metadata and model output stay data. They are never interpreted as tools.
public static partial class AiPrivacy
{
    /// Replaces links, local paths (macOS, Linux, Windows, UNC) and IPv4 addresses, then truncates.
    public static string Text(string value, int limit = 800)
    {
        var clean = Links().Replace(value, "[private link]");
        clean = UnixPaths().Replace(clean, "[private path]");
        clean = WindowsPaths().Replace(clean, "[private path]");
        clean = Addresses().Replace(clean, "[private address]");
        return Truncate(clean, limit);
    }

    /// Cuts at limit UTF-16 units without splitting a surrogate pair.
    internal static string Truncate(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var end = limit;
        if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end];
    }

    /// Decodes a model's JSON answer. Accepts one surrounding Markdown code fence. Any failure,
    /// missing required key or oversized answer becomes AiErrorKind.InvalidResponse.
    public static T Decode<T>(string response)
    {
        var value = response.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal) && value.EndsWith("```", StringComparison.Ordinal)
            && value.IndexOf('\n') is var firstBreak and >= 0 && value.Length >= firstBreak + 4)
            value = value[(firstBreak + 1)..^3].Trim();
        if (Encoding.UTF8.GetByteCount(value) >= 150_000) throw new AiException(AiErrorKind.InvalidResponse);
        try
        {
            return JsonSerializer.Deserialize<T>(value, ModelJson) ?? throw new AiException(AiErrorKind.InvalidResponse);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            throw new AiException(AiErrorKind.InvalidResponse, inner: error);
        }
    }

    /// Model-facing JSON: camelCase, nulls omitted, readable non-ASCII (smaller prompts than \u escapes).
    internal static readonly JsonSerializerOptions ModelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        MaxDepth = 16,
        RespectNullableAnnotations = true,
    };

    [GeneratedRegex(@"(?:https?|file|ftps?|sftp|smb|nfs|davs?|webdavs?)://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Links();

    [GeneratedRegex(@"(?:/Users/|/Volumes/|/private/|/home/|/mnt/|/media/|/run/media/|/tmp/|/var/|/srv/)[^\s<>]+")]
    private static partial Regex UnixPaths();

    [GeneratedRegex(@"(?:\b[A-Za-z]:[\\/]|\\\\[^\s<>\\]+\\)[^\s<>]*")]
    private static partial Regex WindowsPaths();

    [GeneratedRegex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b")]
    private static partial Regex Addresses();
}
