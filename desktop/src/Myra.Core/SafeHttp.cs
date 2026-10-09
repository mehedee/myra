using System.Net;
using System.Net.Http.Headers;

namespace Myra.Core;

/// Provider HTTP rules: no automatic redirects (each hop is checked by a policy), no cookies,
/// and bounded response bodies.
public static class SafeHttp
{
    /// Production client for metadata and subtitle providers. Redirects are followed by SendAsync only.
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Myra", "2.0"));
        return client;
    }

    /// Sends a request built for each hop. A redirect is followed only when allowRedirect accepts the
    /// target; otherwise the 3xx response is returned to the caller unchanged.
    internal static async Task<(HttpResponseMessage Response, Uri Url)> SendAsync(
        HttpClient client, Uri url, Func<Uri, HttpRequestMessage> build, Func<Uri, bool> allowRedirect,
        CancellationToken cancellationToken, int maximumRedirects = 5)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = build(url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || hop >= maximumRedirects || response.Headers.Location is not { } location)
                return (response, response.RequestMessage?.RequestUri ?? url);
            var next = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (!allowRedirect(next)) return (response, url);
            response.Dispose();
            url = next;
        }
    }

    /// Reads at most limit bytes. Larger declared or actual bodies throw the caller's exception.
    internal static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response, int limit, Func<Exception> oversized, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit) throw oversized();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > limit) throw oversized();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;
}
