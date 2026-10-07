using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myra.Core;

public static class AtomicFile
{
    /// Writes to a temporary file first, so a crash never leaves a half-written store.
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}

/// Replaces SwiftData: sources, downloads and settings in one JSON document.
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ObservableCollection<Category> Categories { get; set; } = [];
    public ObservableCollection<DownloadBatch> Batches { get; set; } = [];
    public ObservableCollection<DownloadItem> Items { get; set; } = [];
    public AppSettings Settings { get; set; } = new();

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public ObservableCollection<Category> Categories { get; set; } = [];
        public ObservableCollection<DownloadBatch> Batches { get; set; } = [];
        public ObservableCollection<DownloadItem> Items { get; set; } = [];
        public AppSettings Settings { get; set; } = new();
    }

    private AppStore(string path) => _path = path;

    public static AppStore Load(string? path = null)
    {
        var store = new AppStore(path ?? AppPaths.StorePath);
        if (!File.Exists(store._path)) return store;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(store._path), Options) ?? new Document();
            store.Categories = document.Categories;
            store.Batches = document.Batches;
            store.Items = document.Items;
            store.Settings = document.Settings;
        }
        catch (JsonException)
        {
            // Keep the unreadable file for recovery instead of overwriting it.
            File.Copy(store._path, store._path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: false);
        }
        return store;
    }

    public void Save()
    {
        var document = new Document
        {
            Categories = Categories,
            Batches = Batches,
            Items = Items,
            Settings = Settings,
        };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(document, Options));
    }
}
