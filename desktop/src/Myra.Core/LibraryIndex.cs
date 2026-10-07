using Microsoft.Data.Sqlite;

namespace Myra.Core;

/// Persistent video index for Global search, plus playback positions and a metadata cache.
/// All access goes through one connection guarded by a lock; callers run it off the UI thread.
public sealed class LibraryIndex : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();
    private readonly bool _hasTrigram;

    public LibraryIndex(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA busy_timeout=3000");

        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > 2) throw new InvalidOperationException("This index was created by a newer Myra version.");
        var ftsExisted = Scalar("SELECT 1 FROM sqlite_master WHERE name='video_fts'") is not null;

        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS videos (
              category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
              url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL,
              relative_path TEXT NOT NULL, artwork TEXT, size INTEGER, modified REAL,
              generation TEXT NOT NULL, first_discovered REAL NOT NULL DEFAULT 0,
              PRIMARY KEY(category, url));
            CREATE TABLE IF NOT EXISTS source_scans (
              category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata (
              cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS playback (
              url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);
            PRAGMA user_version=2;
            """);

        // Fall back to literal substring matching when the SQLite build lacks the trigram tokenizer.
        try
        {
            Execute("""
                CREATE VIRTUAL TABLE IF NOT EXISTS video_fts USING fts5(search_name,
                  content='videos', content_rowid='rowid', tokenize='trigram');
                CREATE TRIGGER IF NOT EXISTS videos_ai AFTER INSERT ON videos BEGIN
                  INSERT INTO video_fts(rowid,search_name) VALUES(new.rowid,new.search_name); END;
                CREATE TRIGGER IF NOT EXISTS videos_ad AFTER DELETE ON videos BEGIN
                  INSERT INTO video_fts(video_fts,rowid,search_name)
                    VALUES('delete',old.rowid,old.search_name); END;
                CREATE TRIGGER IF NOT EXISTS videos_au AFTER UPDATE ON videos BEGIN
                  INSERT INTO video_fts(video_fts,rowid,search_name)
                    VALUES('delete',old.rowid,old.search_name);
                  INSERT INTO video_fts(rowid,search_name) VALUES(new.rowid,new.search_name); END;
                """);
            _hasTrigram = true;
            if (!ftsExisted) Execute("INSERT INTO video_fts(video_fts) VALUES('rebuild')");
        }
        catch (SqliteException)
        {
            _hasTrigram = false;
        }
    }

    public bool UsesTrigramSearch => _hasTrigram;

    public static string Normalized(string value) => value.ToLowerInvariant();

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void SynchronizeSources(IReadOnlyList<GlobalSearchRoot> roots)
    {
        lock (_lock)
        {
            var active = roots.Select(r => r.Id.ToString()).ToHashSet();
            var obsolete = new List<string>();
            using (var command = Command("SELECT DISTINCT category FROM videos UNION SELECT category FROM source_scans"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (!active.Contains(reader.GetString(0))) obsolete.Add(reader.GetString(0));

            Transaction(() =>
            {
                foreach (var id in obsolete)
                {
                    Execute("DELETE FROM videos WHERE category=$a", id);
                    Execute("DELETE FROM source_scans WHERE category=$a", id);
                }
                foreach (var root in roots)
                {
                    Execute("DELETE FROM videos WHERE category=$a AND root<>$b", root.Id.ToString(), root.Url.AbsoluteUri);
                    Execute("DELETE FROM source_scans WHERE category=$a AND root<>$b", root.Id.ToString(), root.Url.AbsoluteUri);
                    Execute("UPDATE videos SET category_name=$a WHERE category=$b AND category_name<>$a", root.Name, root.Id.ToString());
                }
            });
        }
    }

    public bool NeedsRefresh(IReadOnlyList<GlobalSearchRoot> roots, DateTimeOffset? now = null)
    {
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0;
        lock (_lock)
        {
            foreach (var root in roots)
            {
                var completed = Scalar("SELECT completed FROM source_scans WHERE category=$a AND root=$b",
                    root.Id.ToString(), root.Url.AbsoluteUri);
                if (completed is null || current - Convert.ToDouble(completed) >= 86400) return true;
            }
            return false;
        }
    }

    public DateTimeOffset? LastRefresh()
    {
        lock (_lock)
        {
            var value = Scalar("SELECT MIN(completed) FROM source_scans");
            return value is null or DBNull ? null : FromUnix(Convert.ToDouble(value));
        }
    }

    public long VideoCount()
    {
        lock (_lock) return Convert.ToInt64(Scalar("SELECT COUNT(*) FROM videos"));
    }

    public void Upsert(IReadOnlyList<GlobalSearchResult> results, Guid generation)
    {
        if (results.Count == 0) return;
        lock (_lock)
        {
            Transaction(() =>
            {
                using var command = Command("""
                    INSERT INTO videos(category,root,category_name,url,name,search_name,relative_path,
                      artwork,size,modified,generation,first_discovered)
                    VALUES($p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12)
                    ON CONFLICT(category,url) DO UPDATE SET root=excluded.root,
                      category_name=excluded.category_name,name=excluded.name,search_name=excluded.search_name,
                      relative_path=excluded.relative_path,artwork=excluded.artwork,size=excluded.size,
                      modified=excluded.modified,generation=excluded.generation
                    """);
                var parameters = Enumerable.Range(1, 12).Select(i => command.Parameters.Add($"$p{i}", SqliteType.Text)).ToArray();
                command.Prepare();
                foreach (var result in results)
                {
                    var boundary = new UrlBoundary(result.CategoryRoot);
                    if (!MediaFileType.IsVideo(result.Entry) || !boundary.Contains(result.Entry.Url)
                        || result.RelativePath.Split('/').Contains(".."))
                        throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
                    object?[] values =
                    [
                        result.CategoryId.ToString(), result.CategoryRoot.AbsoluteUri, result.CategoryName,
                        result.Entry.Url.AbsoluteUri, result.Entry.Name, Normalized(result.Entry.Name),
                        result.RelativePath, result.ArtworkUrl?.AbsoluteUri, result.Entry.Size,
                        result.Entry.ModifiedAt?.ToUnixTimeMilliseconds() / 1000.0, generation.ToString(), Now(),
                    ];
                    for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
            });
        }
    }

    public void CompleteSource(GlobalSearchRoot root, Guid generation, DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            Transaction(() =>
            {
                Execute("DELETE FROM videos WHERE category=$a AND generation<>$b", root.Id.ToString(), generation.ToString());
                Execute("INSERT OR REPLACE INTO source_scans VALUES($a,$b,$c)",
                    root.Id.ToString(), root.Url.AbsoluteUri, (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() / 1000.0);
            });
        }
    }

    public List<GlobalSearchResult> Search(string rawQuery, int limit = 500, int offset = 0)
    {
        var query = rawQuery.Trim();
        if (query.Length < 3) throw new GlobalSearchException(GlobalSearchErrorKind.QueryTooShort);
        var useFts = _hasTrigram && !query.Contains('\n');
        var source = useFts ? "videos JOIN video_fts ON videos.rowid=video_fts.rowid" : "videos";
        var predicate = useFts ? "video_fts MATCH $a" : "instr(search_name,$a)>0";
        var normalized = Normalized(query);
        // MATCH receives a quoted literal, so query text never acts as FTS syntax.
        var term = useFts ? "\"" + normalized.Replace("\"", "\"\"") + "\"" : normalized;
        lock (_lock)
        {
            using var command = Command($"""
                SELECT category,category_name,root,videos.url,name,relative_path,artwork,size,modified
                FROM {source} WHERE {predicate} ORDER BY category_name,name,videos.url LIMIT $b OFFSET $c
                """, term, Math.Clamp(limit, 1, 500), Math.Max(0, offset));
            return ReadResults(command);
        }
    }

    private static List<GlobalSearchResult> ReadResults(SqliteCommand command)
    {
        var results = new List<GlobalSearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var category)
                || !Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var root)
                || !Uri.TryCreate(reader.GetString(3), UriKind.Absolute, out var url))
                continue;
            try
            {
                if (!new UrlBoundary(root).Contains(url)) continue;
            }
            catch (DirectoryException)
            {
                continue;
            }
            var entry = new DirectoryEntry(
                reader.GetString(4), url, EntryKind.File,
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : FromUnix(reader.GetDouble(8)));
            Uri? artwork = reader.IsDBNull(6) ? null : Uri.TryCreate(reader.GetString(6), UriKind.Absolute, out var a) ? a : null;
            results.Add(new GlobalSearchResult(category, reader.GetString(1), root, entry, reader.GetString(5), artwork));
        }
        return results;
    }

    public string? CachedMetadata(string key, bool allowStale = false, DateTimeOffset? now = null)
    {
        var current = now is { } value ? value.ToUnixTimeMilliseconds() / 1000.0 : Now();
        lock (_lock)
        {
            using var command = Command("SELECT payload,fetched FROM metadata WHERE cache_key=$a", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || (!allowStale && current - reader.GetDouble(1) >= 7 * 86400)) return null;
            return reader.GetString(0);
        }
    }

    public void SaveMetadata(string key, string payload)
    {
        lock (_lock) Execute("INSERT OR REPLACE INTO metadata VALUES($a,$b,$c)", key, payload, Now());
    }

    public void SavePlaybackPosition(Uri url, double seconds, double duration)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(duration)) return;
        lock (_lock)
            Execute("INSERT OR REPLACE INTO playback VALUES($a,$b,$c,$d)", url.AbsoluteUri, Math.Max(0, seconds), Math.Max(0, duration), Now());
    }

    /// Returns a resume point only when it is meaningful: past 5 s and more than 10 s before the end.
    public double? PlaybackPosition(Uri url)
    {
        lock (_lock)
        {
            using var command = Command("SELECT seconds,duration FROM playback WHERE url=$a", url.AbsoluteUri);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var seconds = reader.GetDouble(0);
            var duration = reader.GetDouble(1);
            return seconds >= 5 && duration > seconds + 10 ? seconds : null;
        }
    }

    private static DateTimeOffset FromUnix(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToLocalTime();

    private void Transaction(Action body)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);
        body();
        transaction.Commit();
    }

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        var names = new[] { "$a", "$b", "$c", "$d" };
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(names[i], values[i] ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteScalar();
    }

    public void Dispose() => _connection.Dispose();
}

/// Rebuilds the index by crawling every source. Sources that fail keep their previous records.
public sealed class LibraryIndexer(GlobalSearchService scanner)
{
    public async Task<GlobalSearchSnapshot> RefreshAsync(
        IReadOnlyList<GlobalSearchRoot> roots,
        LibraryIndex index,
        Guid generation,
        Func<GlobalSearchSnapshot, Task> update,
        Func<int>? concurrencyLimit = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() => index.SynchronizeSources(roots), cancellationToken);
        if (roots.Count == 0) return new GlobalSearchSnapshot([], new GlobalSearchProgress(), []);
        var snapshot = await scanner.SearchAsync(
            "", roots, update, matchAllVideos: true,
            batchSink: (batch, token) => Task.Run(() => index.Upsert(batch, generation), token),
            concurrencyLimit: concurrencyLimit,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var failed = snapshot.Failures.Select(f => f.CategoryId).ToHashSet();
        foreach (var root in roots.Where(r => !failed.Contains(r.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => index.CompleteSource(root, generation), cancellationToken);
        }
        return snapshot;
    }
}
