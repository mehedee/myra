using Microsoft.Data.Sqlite;

namespace Myra.Core;

public sealed class LibraryIndexException(string message, Exception? inner = null) : Exception("Library index: " + message, inner);

/// Persistent video index for Global search and Home, plus playback positions, a metadata cache,
/// per-folder snapshots and a catalogue revision. All access goes through one connection guarded
/// by a lock; callers run it off the UI thread.
///
/// Schema history (shared with macOS): v1 videos/playback; v2 adds videos.first_discovered;
/// v3 adds folders (directory snapshots) and index_state (catalogue revision); v4 adds the fuzzy-search
/// documents (search_documents, an FTS5 unicode61 trigram index kept current by SQL triggers, and a
/// search_dirty queue). An older index is backed up with the SQLite backup API to "{path}.v{N}-backup"
/// (written to ".tmp", verified, then renamed) before it is migrated in place in one transaction.
public sealed class LibraryIndex : IDisposable
{
    public const int SchemaVersion = 4;

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();
    private readonly bool _hasTrigram;
    private readonly bool _hasSearchSchema;

    public LibraryIndex(string path, bool searchable = true)
    {
        Path = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _connection = Open(Path);
        try
        {
            Execute("PRAGMA busy_timeout=3000");
            var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            if (version > SchemaVersion) throw new LibraryIndexException("This index was created by a newer Myra version.");
            if (version is > 0 and < SchemaVersion) Backup(version);
            _hasTrigram = Migrate(searchable);
            _hasSearchSchema = searchable;
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    /// Absolute path of the SQLite file. The saved Home snapshot lives beside it ("{Path}.home-cache").
    public string Path { get; }

    public bool UsesTrigramSearch => _hasTrigram;

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// Backup includes committed WAL pages, unlike copying the database file. The copy is written to
    /// "{backup}.tmp", verified, and only then renamed, so an interrupted backup is never trusted.
    /// An existing backup is kept only when it verifies; otherwise it is recreated.
    private void Backup(int version)
    {
        var backupPath = Path + $".v{version}-backup";
        if (File.Exists(backupPath) && IsValidBackup(backupPath, version)) return;
        var temporary = backupPath + ".tmp";
        try
        {
            DeleteDatabaseFiles(temporary);
            using (var destination = Open(temporary))
                _connection.BackupDatabase(destination);
            if (!IsValidBackup(temporary, version)) throw new LibraryIndexException("The backup copy did not pass verification.");
            File.Move(temporary, backupPath, overwrite: true);
        }
        catch (Exception error)
        {
            try
            {
                DeleteDatabaseFiles(temporary);
            }
            catch (Exception)
            {
            }
            throw new LibraryIndexException("Index backup failed; migration was not performed.", error);
        }
    }

    /// True when the file opens as SQLite, passes quick_check and has the expected schema version.
    internal static bool IsValidBackup(string path, int version)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (check.ExecuteScalar() as string != "ok") return false;
            check.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(check.ExecuteScalar()) == version;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(file)) File.Delete(file);
    }

    private bool Migrate(bool searchable)
    {
        var ftsExisted = Scalar("SELECT 1 FROM sqlite_master WHERE name='video_fts'") is not null;
        Execute("PRAGMA journal_mode=WAL");
        Execute("""
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
            """);

        var hasFirstDiscovered = Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('videos') WHERE name='first_discovered'")) > 0;
        Transaction(() =>
        {
            // v2: durable first-discovery dates. Older rows take their source's last scan time.
            if (!hasFirstDiscovered)
            {
                Execute("ALTER TABLE videos ADD COLUMN first_discovered REAL NOT NULL DEFAULT 0");
                Execute("""
                    UPDATE videos SET first_discovered=COALESCE(
                      (SELECT completed FROM source_scans WHERE source_scans.category=videos.category),
                      CAST(strftime('%s','now') AS REAL))
                    """);
            }
            // v3: folder snapshots and a revision that invalidates the saved Home snapshot.
            Execute("""
                CREATE TABLE IF NOT EXISTS folders(category TEXT NOT NULL, root TEXT NOT NULL,
                  url TEXT NOT NULL, payload TEXT NOT NULL, checked REAL NOT NULL, PRIMARY KEY(category,url));
                CREATE TABLE IF NOT EXISTS index_state(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL);
                INSERT OR IGNORE INTO index_state VALUES(1,0);
                """);
            // v4: portable fuzzy search. Created in the same transaction as the version bump, so a
            // failure leaves the v3 index untouched. Every existing video is queued for indexing.
            if (searchable) CreateSearchSchema();
            Execute($"PRAGMA user_version={SchemaVersion}");
        });

        if (!searchable) return false;
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
            if (!ftsExisted) Execute("INSERT INTO video_fts(video_fts) VALUES('rebuild')");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void CreateSearchSchema()
    {
        var existed = Scalar("SELECT 1 FROM sqlite_master WHERE name='search_documents'") is not null;
        Execute("""
            CREATE TABLE IF NOT EXISTS search_documents(category TEXT NOT NULL,url TEXT NOT NULL,
              name TEXT NOT NULL,aliases TEXT NOT NULL DEFAULT '',normalized TEXT NOT NULL,
              grams TEXT NOT NULL, UNIQUE(category,url));
            CREATE VIRTUAL TABLE IF NOT EXISTS search_document_fts USING fts5(grams,
              content='search_documents',content_rowid='rowid',tokenize='unicode61');
            CREATE TABLE IF NOT EXISTS search_dirty(category TEXT NOT NULL,url TEXT NOT NULL,PRIMARY KEY(category,url));
            CREATE TRIGGER IF NOT EXISTS search_video_insert AFTER INSERT ON videos BEGIN
              INSERT OR IGNORE INTO search_dirty VALUES(new.category,new.url); END;
            CREATE TRIGGER IF NOT EXISTS search_video_update AFTER UPDATE OF name ON videos WHEN new.name != old.name BEGIN
              INSERT OR IGNORE INTO search_dirty VALUES(new.category,new.url); END;
            CREATE TRIGGER IF NOT EXISTS search_video_delete AFTER DELETE ON videos BEGIN
              DELETE FROM search_documents WHERE category=old.category AND url=old.url;
              DELETE FROM search_dirty WHERE category=old.category AND url=old.url; END;
            CREATE TRIGGER IF NOT EXISTS search_doc_insert AFTER INSERT ON search_documents BEGIN
              INSERT INTO search_document_fts(rowid,grams) VALUES(new.rowid,new.grams); END;
            CREATE TRIGGER IF NOT EXISTS search_doc_delete AFTER DELETE ON search_documents BEGIN
              INSERT INTO search_document_fts(search_document_fts,rowid,grams) VALUES('delete',old.rowid,old.grams); END;
            CREATE TRIGGER IF NOT EXISTS search_doc_update AFTER UPDATE ON search_documents BEGIN
              INSERT INTO search_document_fts(search_document_fts,rowid,grams) VALUES('delete',old.rowid,old.grams);
              INSERT INTO search_document_fts(rowid,grams) VALUES(new.rowid,new.grams); END;
            """);
        if (!existed) Execute("INSERT OR IGNORE INTO search_dirty SELECT category,url FROM videos");
    }

    public static string Normalized(string value) => value.ToLowerInvariant();

    private static double Now() => ToUnix(DateTimeOffset.UtcNow);

    internal static double ToUnix(DateTimeOffset value) => value.ToUnixTimeMilliseconds() / 1000.0;

    internal static DateTimeOffset FromUnix(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToLocalTime();

    private static string SourceKey(Guid id) => id.ToString();

    /// Removes deleted sources and records whose root changed, and renames sources. Bumps the revision on change.
    public void SynchronizeSources(IReadOnlyList<GlobalSearchRoot> roots)
    {
        lock (_lock)
        {
            var active = roots.Select(r => SourceKey(r.Id)).ToHashSet();
            var obsolete = new List<string>();
            using (var command = Command("SELECT DISTINCT category FROM videos UNION SELECT category FROM source_scans"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (!active.Contains(reader.GetString(0))) obsolete.Add(reader.GetString(0));

            Transaction(() =>
            {
                var changed = obsolete.Count > 0 || roots.Any(root => Convert.ToInt64(Scalar(
                    "SELECT COUNT(*) FROM videos WHERE category=$a AND (root<>$b OR category_name<>$c)",
                    SourceKey(root.Id), root.Url.AbsoluteUri, root.Name)) > 0);
                if (changed) BumpRevision();
                foreach (var id in obsolete)
                {
                    Execute("DELETE FROM videos WHERE category=$a", id);
                    Execute("DELETE FROM source_scans WHERE category=$a", id);
                    Execute("DELETE FROM folders WHERE category=$a", id);
                }
                foreach (var root in roots)
                {
                    var id = SourceKey(root.Id);
                    Execute("DELETE FROM folders WHERE category=$a AND root<>$b", id, root.Url.AbsoluteUri);
                    Execute("DELETE FROM videos WHERE category=$a AND root<>$b", id, root.Url.AbsoluteUri);
                    Execute("DELETE FROM source_scans WHERE category=$a AND root<>$b", id, root.Url.AbsoluteUri);
                    Execute("UPDATE videos SET category_name=$a WHERE category=$b AND category_name<>$a", root.Name, id);
                }
            });
        }
    }

    /// True when any source has no completed scan, or its last scan is at least 24 hours old.
    public bool NeedsRefresh(IReadOnlyList<GlobalSearchRoot> roots, DateTimeOffset? now = null)
    {
        var current = ToUnix(now ?? DateTimeOffset.UtcNow);
        lock (_lock)
        {
            foreach (var root in roots)
            {
                var completed = Scalar("SELECT completed FROM source_scans WHERE category=$a AND root=$b",
                    SourceKey(root.Id), root.Url.AbsoluteUri);
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

    /// Monotonic catalogue revision. Any published content, source or metadata change increments it.
    public long Revision()
    {
        lock (_lock) return Convert.ToInt64(Scalar("SELECT revision FROM index_state WHERE id=1"));
    }

    private void BumpRevision() => Execute("UPDATE index_state SET revision=revision+1 WHERE id=1");

    public void Upsert(IReadOnlyList<GlobalSearchResult> results, Guid generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                BumpRevision();
                foreach (var result in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var boundary = new UrlBoundary(result.CategoryRoot);
                    if (!MediaFileType.IsVideo(result.Entry) || !boundary.Contains(result.Entry.Url)
                        || result.RelativePath.Split('/').Contains(".."))
                        throw new DirectoryException(DirectoryErrorKind.OutsideCategoryRoot);
                    object?[] values =
                    [
                        SourceKey(result.CategoryId), result.CategoryRoot.AbsoluteUri, result.CategoryName,
                        result.Entry.Url.AbsoluteUri, result.Entry.Name, Normalized(result.Entry.Name),
                        result.RelativePath, result.ArtworkUrl?.AbsoluteUri, result.Entry.Size,
                        result.Entry.ModifiedAt is { } modified ? ToUnix(modified) : null, generation.ToString(), Now(),
                    ];
                    for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
            }, cancellationToken);
        }
    }

    /// Legacy whole-source completion: prunes rows from older generations and records the scan time.
    public void CompleteSource(GlobalSearchRoot root, Guid generation, DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            Transaction(() =>
            {
                Execute("DELETE FROM videos WHERE category=$a AND generation<>$b", SourceKey(root.Id), generation.ToString());
                Execute("INSERT OR REPLACE INTO source_scans VALUES($a,$b,$c)",
                    SourceKey(root.Id), root.Url.AbsoluteUri, ToUnix(now ?? DateTimeOffset.UtcNow));
                BumpRevision();
            });
        }
    }

    public IndexedFolder? Folder(IndexScope scope)
    {
        lock (_lock)
        {
            var payload = Scalar("SELECT payload FROM folders WHERE category=$a AND root=$b AND url=$c",
                SourceKey(scope.Root.Id), scope.Root.Url.AbsoluteUri, scope.Folder.AbsoluteUri);
            return payload is string json ? IndexedFolder.Deserialize(json) : null;
        }
    }

    public void SaveFolder(IndexedFolder value, IndexScope scope)
    {
        lock (_lock)
            Execute("INSERT OR REPLACE INTO folders VALUES($a,$b,$c,$d,$e)",
                SourceKey(scope.Root.Id), scope.Root.Url.AbsoluteUri, scope.Folder.AbsoluteUri, value.Serialize(), ToUnix(value.Checked));
    }

    /// Folder tree for Index Management. Existing indexes without snapshots infer folders from file paths,
    /// so the tree is available immediately after migration. File counts include descendants.
    public List<IndexFolderRow> FolderRows(IReadOnlyList<GlobalSearchRoot> roots)
    {
        var rows = new List<IndexFolderRow>();
        lock (_lock)
        {
            foreach (var root in roots)
            {
                var boundary = new UrlBoundary(root.Url);
                var id = SourceKey(root.Id);
                var completed = Scalar("SELECT completed FROM source_scans WHERE category=$a AND root=$b", id, root.Url.AbsoluteUri);
                DateTimeOffset? sourceDate = completed is null or DBNull ? null : FromUnix(Convert.ToDouble(completed));
                var sourceRows = new Dictionary<string, (IndexScope Scope, DateTimeOffset? Checked)>();
                using (var command = Command("SELECT url,checked FROM folders WHERE category=$a AND root=$b ORDER BY url", id, root.Url.AbsoluteUri))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!Uri.TryCreate(reader.GetString(0), UriKind.Absolute, out var url) || !boundary.Contains(url)) continue;
                        var scope = new IndexScope(root, url);
                        sourceRows[scope.Prefix] = (scope, FromUnix(reader.GetDouble(1)));
                    }
                }
                var rootScope = IndexScope.ForRoot(root);
                sourceRows.TryAdd(rootScope.Prefix, (rootScope, sourceDate));

                var counts = new Dictionary<string, int>();
                var rootPath = boundary.Root.AbsolutePath.TrimEnd('/');
                using (var command = Command("SELECT url FROM videos WHERE category=$a AND root=$b", id, root.Url.AbsoluteUri))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!Uri.TryCreate(reader.GetString(0), UriKind.Absolute, out var url)) continue;
                        var folder = url.Parent();
                        while (boundary.Contains(folder))
                        {
                            var atRoot = folder.AbsolutePath.TrimEnd('/') == rootPath;
                            var scope = atRoot ? rootScope : new IndexScope(root, folder);
                            counts[scope.Prefix] = counts.GetValueOrDefault(scope.Prefix) + 1;
                            sourceRows.TryAdd(scope.Prefix, (scope, sourceDate));
                            if (atRoot) break;
                            var parent = folder.Parent();
                            if (parent.AbsolutePath == folder.AbsolutePath) break;
                            folder = parent;
                        }
                    }
                }
                foreach (var (prefix, row) in sourceRows)
                    rows.Add(new IndexFolderRow(row.Scope, row.Checked, counts.GetValueOrDefault(prefix)));
            }
        }
        return rows.OrderBy(r => r.Scope.Folder.AbsoluteUri, StringComparer.Ordinal).ToList();
    }

    /// Promotes completed scopes from a staging index in one transaction. Identical rows are not
    /// rewritten, first-discovery dates are preserved, and the revision changes only when content changed.
    public IndexRefreshSummary Publish(string stagingPath, IReadOnlyList<IndexScope> scopes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = new IndexRefreshSummary();
        lock (_lock)
        {
            Execute("ATTACH DATABASE $a AS incoming", stagingPath);
            try
            {
                Transaction(() =>
                {
                    foreach (var scope in IndexScope.Compact(scopes))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        object?[] values = [SourceKey(scope.Root.Id), scope.Root.Url.AbsoluteUri, scope.Prefix.Length, scope.Prefix];
                        const string selected = "category=$a AND root=$b AND substr(url,1,$c)=$d";
                        const string differs = "a.root IS NOT s.root OR a.category_name IS NOT s.category_name OR a.name IS NOT s.name "
                            + "OR a.relative_path IS NOT s.relative_path OR a.artwork IS NOT s.artwork OR a.size IS NOT s.size "
                            + "OR a.modified IS NOT s.modified";
                        summary.Added += Count($"SELECT COUNT(*) FROM incoming.videos s WHERE {selected} AND NOT EXISTS(SELECT 1 FROM main.videos a WHERE a.category=s.category AND a.url=s.url)", values);
                        summary.Changed += Count($"SELECT COUNT(*) FROM incoming.videos s WHERE {selected} AND EXISTS(SELECT 1 FROM main.videos a WHERE a.category=s.category AND a.url=s.url AND ({differs}))", values);
                        summary.Removed += Count($"SELECT COUNT(*) FROM main.videos a WHERE {selected} AND NOT EXISTS(SELECT 1 FROM incoming.videos s WHERE s.category=a.category AND s.url=a.url)", values);
                        Execute($"""
                            INSERT INTO main.videos SELECT * FROM incoming.videos WHERE {selected}
                            ON CONFLICT(category,url) DO UPDATE SET root=excluded.root,category_name=excluded.category_name,
                            name=excluded.name,search_name=excluded.search_name,relative_path=excluded.relative_path,
                            artwork=excluded.artwork,size=excluded.size,modified=excluded.modified,generation=excluded.generation
                            WHERE videos.root IS NOT excluded.root OR videos.category_name IS NOT excluded.category_name
                            OR videos.name IS NOT excluded.name OR videos.relative_path IS NOT excluded.relative_path
                            OR videos.artwork IS NOT excluded.artwork OR videos.size IS NOT excluded.size OR videos.modified IS NOT excluded.modified
                            """, values);
                        Execute($"DELETE FROM main.videos WHERE {selected} AND NOT EXISTS(SELECT 1 FROM incoming.videos s WHERE s.category=main.videos.category AND s.url=main.videos.url)", values);

                        const string folderSelected = "category=$a AND root=$b AND (url=$c OR substr(url,1,$d)=$e)";
                        object?[] folderValues = [SourceKey(scope.Root.Id), scope.Root.Url.AbsoluteUri, scope.Folder.AbsoluteUri, scope.Prefix.Length, scope.Prefix];
                        Execute($"DELETE FROM main.folders WHERE {folderSelected}", folderValues);
                        Execute($"INSERT INTO main.folders SELECT * FROM incoming.folders WHERE {folderSelected}", folderValues);
                        if (scope.Folder.SameAs(scope.Root.Url))
                            Execute("INSERT OR REPLACE INTO source_scans VALUES($a,$b,$c)", SourceKey(scope.Root.Id), scope.Root.Url.AbsoluteUri, Now());
                    }
                    if (summary.Added + summary.Changed + summary.Removed > 0) BumpRevision();
                }, cancellationToken);
            }
            finally
            {
                Execute("DETACH DATABASE incoming");
            }
        }
        return summary;
    }

    /// Rows ranked per fuzzy query. More exact-substring matches than this fall back to exact mode.
    private const int FuzzyCandidateLimit = 4096;

    /// Global search. Fuzzy (default) tolerates typos and reordered words and ranks exact phrases first;
    /// exact requires the whole normalized query as a substring. Both are deterministic, so
    /// limit/offset pages never repeat or skip a result.
    public List<GlobalSearchResult> Search(string rawQuery, int limit = 500, int offset = 0, bool fuzzy = true)
    {
        var query = rawQuery.Trim();
        if (query.Length < 3) throw new GlobalSearchException(GlobalSearchErrorKind.QueryTooShort);
        var normalized = FuzzySearch.Normalize(query);
        if (normalized.Length == 0) return [];
        var expression = FuzzySearch.MatchExpression(query);
        var indexed = expression is not null;
        var approximate = fuzzy && indexed;
        var pageSize = Math.Clamp(limit, 1, 500);
        var start = Math.Max(0, offset);
        lock (_lock)
        {
            BuildPendingSearchDocuments();
            var source = indexed
                ? "videos JOIN search_documents d ON videos.category=d.category AND videos.url=d.url JOIN search_document_fts f ON d.rowid=f.rowid"
                : "videos JOIN search_documents d ON videos.category=d.category AND videos.url=d.url";
            var predicate = indexed
                ? (approximate ? "search_document_fts MATCH $a" : "search_document_fts MATCH $a AND instr(d.normalized,$b)>0")
                : "instr(d.normalized,$b)>0";
            var order = approximate ? "CASE WHEN instr(d.normalized,$b)>0 THEN 0 ELSE 1 END,bm25(search_document_fts)," : "";
            using var command = Command($"""
                SELECT videos.category,category_name,root,videos.url,videos.name,relative_path,artwork,size,modified,d.aliases
                FROM {source} WHERE {predicate}
                ORDER BY {order}category_name,videos.name,videos.url LIMIT $c OFFSET $d
                """);
            if (indexed) command.Parameters.AddWithValue("$a", approximate ? expression! : expression!.Replace(" OR ", " AND "));
            command.Parameters.AddWithValue("$b", normalized);
            command.Parameters.AddWithValue("$c", approximate ? FuzzyCandidateLimit : pageSize);
            command.Parameters.AddWithValue("$d", approximate ? 0 : start);
            var results = new List<GlobalSearchResult>();
            var aliases = new Dictionary<GlobalSearchResultId, string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (ReadResult(reader) is { } result)
                    {
                        aliases[result.Id] = reader.GetString(9);
                        results.Add(result);
                    }
            if (!approximate) return results;
            if (results.Count == FuzzyCandidateLimit
                && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM search_documents WHERE instr(normalized,$a)>0", normalized)) >= FuzzyCandidateLimit)
                return Search(rawQuery, limit, offset, fuzzy: false);

            return results
                .Select(result => (Result: result, Score: new[] { result.Entry.Name }
                    .Concat(aliases[result.Id].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    .Select(name => FuzzySearch.Score(query, name)).Where(score => score is not null).Min()))
                .Where(item => item.Score is not null)
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Result.CategoryName + "\0" + item.Result.Entry.Name + "\0" + item.Result.Entry.Url.AbsoluteUri, StringComparer.Ordinal)
                .Skip(start).Take(pageSize).Select(item => item.Result).ToList();
        }
    }

    /// Indexes new and renamed filenames in batches of 500. Query time never rereads the whole catalogue.
    /// Safe to call from a background task: it is a no-op when nothing is queued.
    public void BuildSearchDocuments(CancellationToken cancellationToken = default)
    {
        lock (_lock) BuildPendingSearchDocuments(cancellationToken);
    }

    private void BuildPendingSearchDocuments(CancellationToken cancellationToken = default)
    {
        if (!_hasSearchSchema) return;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<(string Category, string Url, string Name, string Aliases)>();
            using (var pending = Command("""
                SELECT v.category,v.url,v.name,COALESCE(d.aliases,'') FROM search_dirty q
                JOIN videos v ON v.category=q.category AND v.url=q.url
                LEFT JOIN search_documents d ON d.category=v.category AND d.url=v.url LIMIT 500
                """))
            using (var reader = pending.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            if (rows.Count == 0) return;
            Transaction(() =>
            {
                using var insert = Command("""
                    INSERT INTO search_documents(category,url,name,aliases,normalized,grams) VALUES($a,$b,$c,$d,$e,$f)
                    ON CONFLICT(category,url) DO UPDATE SET name=excluded.name,aliases=excluded.aliases,
                      normalized=excluded.normalized,grams=excluded.grams
                    """);
                var values = "abcdef".Select(c => insert.Parameters.Add("$" + c, SqliteType.Text)).ToArray();
                using var remove = Command("DELETE FROM search_dirty WHERE category=$a AND url=$b");
                var keys = "ab".Select(c => remove.Parameters.Add("$" + c, SqliteType.Text)).ToArray();
                foreach (var row in rows)
                {
                    var text = row.Name + " " + row.Aliases;
                    string[] fields = [row.Category, row.Url, row.Name, row.Aliases, FuzzySearch.Normalize(text), FuzzySearch.IndexGrams(text)];
                    for (var i = 0; i < fields.Length; i++) values[i].Value = fields[i];
                    insert.ExecuteNonQuery();
                    keys[0].Value = row.Category;
                    keys[1].Value = row.Url;
                    remove.ExecuteNonQuery();
                }
            });
        }
    }

    /// Associates provider and corrected display titles with the original file identities so Global
    /// search finds "Amelie" for "Le.Fabuleux.Destin.2001.mkv". Idempotent; unchanged aliases cost no write.
    public void SaveSearchAliases(IEnumerable<EntertainmentTitle> titles, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in titles.Chunk(2000))
        {
            lock (_lock)
            {
                BuildPendingSearchDocuments(cancellationToken);
                if (!_hasSearchSchema) return;
                Transaction(() =>
                {
                    using var update = Command("UPDATE search_documents SET aliases=$a WHERE category=$b AND url=$c AND aliases<>$a");
                    var parameters = "abc".Select(c => update.Parameters.Add("$" + c, SqliteType.Text)).ToArray();
                    using var dirty = Command("INSERT OR IGNORE INTO search_dirty VALUES($a,$b)");
                    var keys = "ab".Select(c => dirty.Parameters.Add("$" + c, SqliteType.Text)).ToArray();
                    foreach (var title in chunk)
                    {
                        var alias = string.Join('\n', new[] { title.Name, title.Metadata?.Title ?? "" }.Where(name => name.Length > 0));
                        foreach (var version in title.Versions)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            parameters[0].Value = alias;
                            parameters[1].Value = SourceKey(version.Media.CategoryId);
                            parameters[2].Value = version.Media.Entry.Url.AbsoluteUri;
                            if (update.ExecuteNonQuery() == 0) continue;
                            keys[0].Value = parameters[1].Value;
                            keys[1].Value = parameters[2].Value;
                            dirty.ExecuteNonQuery();
                        }
                    }
                }, cancellationToken);
                BuildPendingSearchDocuments(cancellationToken);
            }
        }
    }

    /// Every indexed video with its first-discovery date and playback progress, for the Home catalogue.
    public List<EntertainmentVersion> Inventory(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            using var command = Command("""
                SELECT category,category_name,root,videos.url,name,relative_path,artwork,size,modified,
                  first_discovered,COALESCE(playback.seconds,0),COALESCE(playback.duration,0),playback.updated
                FROM videos LEFT JOIN playback ON playback.url=videos.url ORDER BY category_name,name
                """);
            var versions = new List<EntertainmentVersion>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadResult(reader) is not { } media) continue;
                versions.Add(new EntertainmentVersion(media, FromUnix(reader.GetDouble(9)))
                {
                    ProgressSeconds = reader.GetDouble(10),
                    Duration = reader.GetDouble(11),
                    LastPlayed = reader.IsDBNull(12) ? null : FromUnix(reader.GetDouble(12)),
                });
            }
            return versions;
        }
    }

    private static GlobalSearchResult? ReadResult(SqliteDataReader reader)
    {
        if (!Guid.TryParse(reader.GetString(0), out var category)
            || !Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var root)
            || !Uri.TryCreate(reader.GetString(3), UriKind.Absolute, out var url))
            return null;
        try
        {
            if (!new UrlBoundary(root).Contains(url)) return null;
        }
        catch (DirectoryException)
        {
            return null;
        }
        var entry = new DirectoryEntry(
            reader.GetString(4), url, EntryKind.File,
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : FromUnix(reader.GetDouble(8)));
        Uri? artwork = reader.IsDBNull(6) ? null : Uri.TryCreate(reader.GetString(6), UriKind.Absolute, out var a) ? a : null;
        return new GlobalSearchResult(category, reader.GetString(1), root, entry, reader.GetString(5), artwork);
    }

    /// Cached metadata payloads are fresh for 7 days; allowStale returns older payloads for offline display.
    public string? CachedMetadata(string key, bool allowStale = false, DateTimeOffset? now = null)
    {
        var current = now is { } value ? ToUnix(value) : Now();
        lock (_lock)
        {
            using var command = Command("SELECT payload,fetched FROM metadata WHERE cache_key=$a", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || (!allowStale && current - reader.GetDouble(1) >= 7 * 86400)) return null;
            return reader.GetString(0);
        }
    }

    public sealed record MetadataRecord(string Payload, double Fetched)
    {
        public bool IsFresh(DateTimeOffset? now = null) => ToUnix(now ?? DateTimeOffset.UtcNow) - Fetched < 7 * 86400;
    }

    /// Bulk read for catalogue preparation. Keys are bound in chunks so SQLite limits are never reached.
    public Dictionary<string, MetadataRecord> MetadataRecords(IReadOnlyCollection<string> keys)
    {
        var records = new Dictionary<string, MetadataRecord>();
        if (keys.Count == 0) return records;
        lock (_lock)
        {
            foreach (var chunk in keys.Distinct().Chunk(800))
            {
                ThrowIfDisposed();
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT cache_key,payload,fetched FROM metadata WHERE cache_key IN ("
                    + string.Join(',', chunk.Select((_, i) => "$k" + i)) + ")";
                for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue("$k" + i, chunk[i]);
                using var reader = command.ExecuteReader();
                while (reader.Read()) records[reader.GetString(0)] = new MetadataRecord(reader.GetString(1), reader.GetDouble(2));
            }
        }
        return records;
    }

    public void SaveMetadata(string key, string payload)
    {
        lock (_lock)
        {
            Execute("INSERT OR REPLACE INTO metadata VALUES($a,$b,$c)", key, payload, Now());
            BumpRevision();
        }
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

    private void Transaction(Action body, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var transaction = _connection.BeginTransaction(deferred: false);
        body();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static readonly string[] Names = ["$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h"];

    private SqliteCommand Command(string sql, params object?[] values)
    {
        ThrowIfDisposed();
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(Names[i], values[i] ?? DBNull.Value);
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

    private int Count(string sql, object?[] values) => Convert.ToInt32(Scalar(sql, values));

    private bool _disposed;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new LibraryIndexException("The index is closed.");
    }

    /// Waits for the running statement or transaction, then closes the connection.
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}

/// Refreshes the index through a private staging database. Completed scopes are published atomically;
/// sources with any failed folder keep all their previous records; cancellation discards staged work.
public sealed class LibraryIndexer
{
    private readonly GlobalSearchService? _scanner;
    private readonly DirectoryService? _directoryService;
    private readonly int _maximumConcurrentFolders;

    /// Test seam and legacy path: a plain scanner, without folder snapshots or schedules.
    public LibraryIndexer(GlobalSearchService scanner)
    {
        _scanner = scanner;
        _maximumConcurrentFolders = 2;
    }

    /// Production path: snapshots, HTTP validators, schedules and manual-only branches.
    public LibraryIndexer(DirectoryService directoryService, int maximumConcurrentFolders = 2)
    {
        _directoryService = directoryService;
        _maximumConcurrentFolders = Math.Max(1, maximumConcurrentFolders);
    }

    /// Added/changed/removed counts from publication, plus checked/unchanged folder counts.
    public IndexRefreshSummary Summary { get; private set; } = new();

    /// Directory for private staging databases. Defaults to the system temporary folder.
    public string? StagingDirectory { get; init; }

    /// Deletes "Myra-staging-*" folders left by a crash. A folder is removed only when none of its
    /// files changed for minimumAge (default 1 hour); locked files (Windows) make the delete fail
    /// harmlessly. Returns the number of folders removed.
    public static int CleanStaleStaging(string? directory = null, TimeSpan? minimumAge = null, DateTime? nowUtc = null)
    {
        var limit = (nowUtc ?? DateTime.UtcNow) - (minimumAge ?? TimeSpan.FromHours(1));
        var removed = 0;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(directory ?? Path.GetTempPath(), "Myra-staging-*"))
            {
                try
                {
                    var info = new DirectoryInfo(folder);
                    if (info.LinkTarget is not null) continue;
                    var newest = info.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Select(f => f.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
                    if (newest > limit) continue;
                    info.Delete(recursive: true);
                    removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return removed;
    }

    /// Refreshes the selected scopes (default: every source root). Roots must contain every configured
    /// source because they also drive source synchronization.
    public async Task<GlobalSearchSnapshot> RefreshAsync(
        IReadOnlyList<GlobalSearchRoot> roots,
        LibraryIndex index,
        Guid generation,
        Func<GlobalSearchSnapshot, Task> update,
        Func<int>? concurrencyLimit = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IndexScope>? scopes = null,
        IndexPolicies? policies = null,
        bool automatic = false,
        bool full = false,
        Func<CancellationToken, Task>? beforeBatch = null)
    {
        await Task.Run(() => index.SynchronizeSources(roots), cancellationToken).ConfigureAwait(false);
        var selected = IndexScope.Compact(scopes ?? roots.Select(IndexScope.ForRoot).ToList())
            .Where(s => roots.Any(r => r.Id == s.Root.Id)).ToList();
        Summary = new IndexRefreshSummary();
        if (selected.Count == 0) return new GlobalSearchSnapshot([], new GlobalSearchProgress(), []);

        var folder = Path.Combine(StagingDirectory ?? Path.GetTempPath(), "Myra-staging-" + generation.ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var stagingPath = Path.Combine(folder, "Index.sqlite");
        try
        {
            GlobalSearchSnapshot snapshot;
            IndexListingLoader? loader = null;
            using (var staged = new LibraryIndex(stagingPath, searchable: false))
            {
                GlobalSearchService service;
                if (_scanner is not null)
                {
                    service = _scanner;
                }
                else
                {
                    loader = new IndexListingLoader(_directoryService!, index, staged, roots, policies ?? new IndexPolicies(), selected, automatic, full);
                    service = new GlobalSearchService(loader.ListingAsync, _maximumConcurrentFolders);
                }
                var selectedIds = selected.Select(s => s.Root.Id).ToHashSet();
                snapshot = await service.SearchAsync(
                    "", roots.Where(r => selectedIds.Contains(r.Id)).ToList(), update, matchAllVideos: true,
                    batchSink: (batch, token) => Task.Run(() => staged.Upsert(batch, generation, token), token),
                    concurrencyLimit: concurrencyLimit,
                    cancellationToken: cancellationToken,
                    scopes: selected,
                    beforeBatch: beforeBatch).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var failed = snapshot.Failures.Select(f => f.CategoryId).ToHashSet();
            var publishable = selected.Where(s => !failed.Contains(s.Root.Id)).ToList();
            var summary = await Task.Run(() => index.Publish(stagingPath, publishable, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (loader is not null)
            {
                summary.Checked = loader.CheckedFolders;
                summary.Unchanged = loader.UnchangedFolders;
            }
            Summary = summary;
            return snapshot;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // CleanStaleStaging removes it at a later start.
            }
        }
    }
}
