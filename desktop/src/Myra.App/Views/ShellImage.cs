using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Myra.App.Views;

/// Loads posters and folder artwork from http(s) or local files into an Image, off the UI thread.
/// Decoded bitmaps are cached in memory (bounded). A failed load leaves the placeholder visible.
public static class ShellImage
{
    private const int CacheLimit = 400;
    private const long MaximumBytes = 8_000_000;
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly Dictionary<string, Task<Bitmap?>> Cache = [];
    private static readonly Queue<string> Order = new();
    private static readonly Lock CacheLock = new();

    public static readonly AttachedProperty<Uri?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Image, Uri?>("Url", typeof(ShellImage));

    /// Decode width in pixels; 0 keeps the full size.
    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeWidth", typeof(ShellImage), 360);

    static ShellImage()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, e) => _ = LoadAsync(image, e.NewValue as Uri));
    }

    public static Uri? GetUrl(Image image) => image.GetValue(UrlProperty);
    public static void SetUrl(Image image, Uri? value) => image.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Myra/2.0");
        return client;
    }

    private static async Task LoadAsync(Image image, Uri? url)
    {
        image.Source = null;
        image.IsVisible = false;
        if (url is null || !(url.IsFile || url.Scheme is "http" or "https")) return;
        var width = GetDecodeWidth(image);
        var bitmap = await Get(url, width);
        if (GetUrl(image) != url || bitmap is null) return;
        image.Source = bitmap;
        image.IsVisible = true;
    }

    private static Task<Bitmap?> Get(Uri url, int width)
    {
        var key = width + "|" + url.AbsoluteUri;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var task = Task.Run(() => Fetch(url, width));
            Cache[key] = task;
            Order.Enqueue(key);
            while (Order.Count > CacheLimit && Order.TryDequeue(out var old)) Cache.Remove(old);
            return task;
        }
    }

    private static async Task<Bitmap?> Fetch(Uri url, int width)
    {
        await Downloads.WaitAsync();
        try
        {
            using var memory = new MemoryStream();
            if (url.IsFile)
            {
                var info = new FileInfo(url.LocalPath);
                if (!info.Exists || info.Length > MaximumBytes) return null;
                await using var file = info.OpenRead();
                await file.CopyToAsync(memory);
            }
            else
            {
                using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
                if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return null;
                await using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > MaximumBytes) return null;
                }
            }
            memory.Position = 0;
            // Skia decodes off the UI thread.
            return width > 0 ? Bitmap.DecodeToWidth(memory, width) : new Bitmap(memory);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }
}
