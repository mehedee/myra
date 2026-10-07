using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Myra.Core;

public static class AiProviderIds
{
    /// Default on Windows/Linux. Search, random picks and playback stay available.
    public const string Disabled = "disabled";
    public const string OpenAI = "openAI";
    public const string Claude = "claude";
}

/// One generation call. Key is "" for local providers.
public sealed record AiGenerationRequest(string Model, string Key, string Instructions, string Prompt, int MaximumTokens, string Language);

/// A text-generation backend. OpenAI and Claude are built in. A local provider (for example a model
/// served on this computer) can be added by implementing this interface with IsCloud = false and
/// passing it to AiService; local providers need no consent, key or history permission.
public interface IAiProvider
{
    /// Stable ID saved in AiSettings.Provider.
    string Id { get; }
    string Title { get; }
    /// Cloud providers require AiSettings.CloudConsent and an API key.
    bool IsCloud { get; }
    /// ISecretStore name of the provider key; null for providers without a key.
    string? SecretName { get; }
    /// UTF-8 budget for one prompt (cloud: 18000).
    int ContextByteLimit { get; }
    /// Upper bound for AiSettings.MaximumOutputTokens (cloud: 2000).
    int MaximumOutputTokens { get; }
    /// Null when ready; otherwise a user-facing reason.
    string? Availability();
    Task<string> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken);
    /// Lists model identifiers. Lists only; it does not test generation.
    Task<IReadOnlyList<string>> ModelsAsync(string key, CancellationToken cancellationToken);
}

/// Fixed HTTPS endpoints. No redirects (an API key is never forwarded), no cookies, no response storage
/// (OpenAI "store": false), 2 MB response limit. Port of MyraAIProviderClient.
public abstract class CloudAiProvider : IAiProvider
{
    public const int ResponseLimit = 2_000_000;
    private readonly HttpClient _client;

    protected CloudAiProvider(HttpClient? client) =>
        _client = client ?? SafeHttp.CreateClient(TimeSpan.FromSeconds(90));

    public abstract string Id { get; }
    public abstract string Title { get; }
    public bool IsCloud => true;
    public abstract string SecretName { get; }
    public int ContextByteLimit => AiContext.CloudByteLimit;
    public int MaximumOutputTokens => 2000;
    /// "https://api.openai.com/v1/" or "https://api.anthropic.com/v1/".
    public abstract Uri BaseUrl { get; }

    public string? Availability() => null;

    protected abstract void Authorize(HttpRequestMessage request, string key);
    protected abstract (string Path, JsonObject Body) BuildGeneration(AiGenerationRequest request);
    protected abstract IEnumerable<string> OutputText(JsonObject response);

    public async Task<string> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken)
    {
        RequireKey(request.Key);
        if (request.Model.Length == 0 || request.Model.Length > 200 || request.Model.Contains('\n') || request.Model.Contains('\r'))
            throw new AiException(AiErrorKind.Unavailable, "Select a text model in AI Settings.");
        var (path, body) = BuildGeneration(request);
        var response = await SendAsync(request.Key, path, body, cancellationToken).ConfigureAwait(false);
        var pieces = OutputText(response).ToList();
        if (pieces.Count == 0) throw new AiException(AiErrorKind.InvalidResponse);
        return string.Join("\n", pieces);
    }

    public async Task<IReadOnlyList<string>> ModelsAsync(string key, CancellationToken cancellationToken)
    {
        RequireKey(key);
        var response = await SendAsync(key, "models", null, cancellationToken).ConfigureAwait(false);
        if (response["data"] is not JsonArray items) throw new AiException(AiErrorKind.InvalidResponse);
        return items.OfType<JsonObject>()
            .Select(item => item["id"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null)
            .OfType<string>().Where(id => id.Length is > 0 and < 200)
            .Order(StringComparer.Ordinal).ToList();
    }

    private static void RequireKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Contains('\n') || key.Contains('\r'))
            throw new AiException(AiErrorKind.MissingKey);
    }

    private async Task<JsonObject> SendAsync(string key, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var url = new Uri(BaseUrl, path);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Authorize(request, key);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(AiPrivacy.ModelJson), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // Redirects are never followed. A 3xx is an error, and the answer must come from the fixed URL.
        if (response.RequestMessage?.RequestUri is { } answered && answered != url) throw new AiException(AiErrorKind.InvalidResponse);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300) throw AiException.Http(status);
        var data = await SafeHttp.ReadBoundedAsync(response, ResponseLimit, () => new AiException(AiErrorKind.InvalidResponse), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(data) as JsonObject ?? throw new AiException(AiErrorKind.InvalidResponse);
        }
        catch (JsonException error)
        {
            throw new AiException(AiErrorKind.InvalidResponse, inner: error);
        }
    }

    protected static IEnumerable<string> Texts(JsonNode? array, string type)
    {
        if (array is not JsonArray items) yield break;
        foreach (var item in items.OfType<JsonObject>())
            if (item["type"] is JsonValue kind && kind.TryGetValue<string>(out var value) && value == type
                && item["text"] is JsonValue text && text.TryGetValue<string>(out var piece))
                yield return piece;
    }
}

/// OpenAI Responses API: POST https://api.openai.com/v1/responses, Bearer key, "store": false.
public sealed class OpenAiProvider(HttpClient? client = null) : CloudAiProvider(client)
{
    public override string Id => AiProviderIds.OpenAI;
    public override string Title => "OpenAI";
    public override string SecretName => SecretNames.OpenAiApiKey;
    public override Uri BaseUrl { get; } = new("https://api.openai.com/v1/");

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    protected override (string Path, JsonObject Body) BuildGeneration(AiGenerationRequest request) => ("responses", new JsonObject
    {
        ["model"] = request.Model,
        ["instructions"] = request.Instructions,
        ["input"] = request.Prompt,
        ["max_output_tokens"] = request.MaximumTokens,
        ["store"] = false,
    });

    protected override IEnumerable<string> OutputText(JsonObject response) =>
        response["output"] is JsonArray outputs
            ? outputs.OfType<JsonObject>().SelectMany(output => Texts(output["content"], "output_text"))
            : [];
}

/// Anthropic Messages API: POST https://api.anthropic.com/v1/messages, x-api-key, anthropic-version 2023-06-01.
public sealed class ClaudeProvider(HttpClient? client = null) : CloudAiProvider(client)
{
    public const string ApiVersion = "2023-06-01";
    public override string Id => AiProviderIds.Claude;
    public override string Title => "Claude";
    public override string SecretName => SecretNames.ClaudeApiKey;
    public override Uri BaseUrl { get; } = new("https://api.anthropic.com/v1/");

    protected override void Authorize(HttpRequestMessage request, string key)
    {
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", ApiVersion);
    }

    protected override (string Path, JsonObject Body) BuildGeneration(AiGenerationRequest request) => ("messages", new JsonObject
    {
        ["model"] = request.Model,
        ["system"] = request.Instructions,
        ["max_tokens"] = request.MaximumTokens,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
    });

    protected override IEnumerable<string> OutputText(JsonObject response) => Texts(response["content"], "text");
}
