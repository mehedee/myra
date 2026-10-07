using System.Net;
using System.Text;

namespace Myra.Core.Tests;

/// Recorded request: URL, method, headers and body, captured before the request is disposed.
public sealed record CapturedRequest(Uri Url, HttpMethod Method, Dictionary<string, string> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// In-process HTTP fixture. No live network is used by any test.
public sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public static FakeHttpHandler ByPath(Dictionary<string, (int Status, string Body)> replies, TimeSpan? delay = null) =>
        new(async request =>
        {
            if (delay is { } wait) await Task.Delay(wait);
            var (status, body) = replies.TryGetValue(request.RequestUri!.AbsolutePath, out var value) ? value : (500, "");
            return Text(status, body);
        });

    public static HttpResponseMessage Text(int status, string body, string contentType = "application/json") => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests) _requests.Add(new CapturedRequest(request.RequestUri!, request.Method, headers, body));
        var response = await reply(request).WaitAsync(cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(10) };
}
