using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myra.Core;

public static class AtomicFile
{
    /// Writes to a temporary file, flushes it to disk, then renames it over the target, so a crash
    /// or power loss leaves either the old or the new document, never a half-written one.
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
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
        catch (Exception error)
        {
            // Keep the unreadable file for recovery instead of overwriting it. When even a copy
            // cannot be made, saving stays blocked so the original is never replaced.
            store.LoadError = "Myra.json could not be loaded: " + error.Message;
            try
            {
                var aside = store._path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                if (!File.Exists(aside)) File.Copy(store._path, aside);
                store.LoadError += " A copy was kept at " + aside + ".";
            }
            catch (Exception)
            {
                store._saveBlocked = true;
            }
        }
        return store;
    }

    private bool _saveBlocked;

    /// Absolute path of Myra.json.
    public string FilePath => Path.GetFullPath(_path);

    /// Set when the existing document could not be read; the app continues with defaults.
    public string? LoadError { get; private set; }

    public void Save()
    {
        if (_saveBlocked) throw new IOException("Myra.json could not be read earlier and was left untouched. Restart Myra to retry.");
        SaveCopy(_path);
    }

    /// Writes the complete current document (including credential-bearing source URLs) to path.
    public void SaveCopy(string path)
    {
        var document = new Document
        {
            Categories = Categories,
            Batches = Batches,
            Items = Items,
            Settings = Settings,
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, Options));
    }
}
