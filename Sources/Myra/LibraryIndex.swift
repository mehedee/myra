import CSQLite
import Foundation

enum LibraryIndexError: LocalizedError {
  case database(String)
  var errorDescription: String? {
    switch self {
    case .database(let message): "Library index: \(message)"
    }
  }
}

// SQLite is only accessed by LibraryIndex's actor. FULLMUTEX also protects cleanup.
private final class IndexConnection: @unchecked Sendable {
  let handle: OpaquePointer
  init(url: URL) throws {
    var pointer: OpaquePointer?
    let result = sqlite3_open_v2(
      url.path, &pointer,
      SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil)
    guard result == SQLITE_OK, let pointer else {
      let message = pointer.map { String(cString: sqlite3_errmsg($0)) } ?? "Could not open database"
      if let pointer { sqlite3_close(pointer) }
      throw LibraryIndexError.database(message)
    }
    handle = pointer
    sqlite3_busy_timeout(handle, 3000)
  }
  deinit { sqlite3_close(handle) }
}

actor LibraryIndex {
  private let connection: IndexConnection
  nonisolated let url: URL
  private var hasTrigram = false
  private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

  init(url: URL, searchable: Bool = true) throws {
    self.url = url
    try FileManager.default.createDirectory(
      at: url.deletingLastPathComponent(),
      withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    let connection = try IndexConnection(url: url)
    self.connection = connection
    var versionStatement: OpaquePointer?
    sqlite3_prepare_v2(connection.handle, "PRAGMA user_version", -1, &versionStatement, nil)
    let version =
      sqlite3_step(versionStatement) == SQLITE_ROW ? sqlite3_column_int(versionStatement, 0) : 0
    sqlite3_finalize(versionStatement)
    guard version <= 4 else {
      throw LibraryIndexError.database("This index was created by a newer Myra version.")
    }
    if version > 0 && version < 4 {
      // SQLite backup includes committed WAL pages, unlike copying the database file.
      let backupURL = url.appendingPathExtension("v\(version)-backup")
      if !FileManager.default.fileExists(atPath: backupURL.path) {
        var destination: OpaquePointer?
        guard sqlite3_open(backupURL.path, &destination) == SQLITE_OK, let destination else {
          if let destination { sqlite3_close(destination) }
          throw LibraryIndexError.database("Could not back up the index before migration.")
        }
        defer { sqlite3_close(destination) }
        guard let backup = sqlite3_backup_init(destination, "main", connection.handle, "main")
        else {
          throw LibraryIndexError.database("Could not initialize the index backup.")
        }
        let result = sqlite3_backup_step(backup, -1)
        let finished = sqlite3_backup_finish(backup)
        guard result == SQLITE_DONE, finished == SQLITE_OK else {
          try? FileManager.default.removeItem(at: backupURL)
          throw LibraryIndexError.database("Index backup failed; migration was not performed.")
        }
      }
    }
    var ftsStatement: OpaquePointer?
    sqlite3_prepare_v2(
      connection.handle, "SELECT 1 FROM sqlite_master WHERE name='video_fts'", -1, &ftsStatement,
      nil)
    let ftsExisted = sqlite3_step(ftsStatement) == SQLITE_ROW
    sqlite3_finalize(ftsStatement)
    let schema = """
      PRAGMA journal_mode=WAL;
      CREATE TABLE IF NOT EXISTS videos (
        category TEXT NOT NULL, root TEXT NOT NULL, category_name TEXT NOT NULL,
        url TEXT NOT NULL, name TEXT NOT NULL, search_name TEXT NOT NULL,
        relative_path TEXT NOT NULL, artwork TEXT, size INTEGER, modified REAL,
        generation TEXT NOT NULL, PRIMARY KEY(category, url));
      CREATE TABLE IF NOT EXISTS source_scans (
        category TEXT PRIMARY KEY, root TEXT NOT NULL, completed REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS metadata (
        cache_key TEXT PRIMARY KEY, payload TEXT NOT NULL, fetched REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS playback (
        url TEXT PRIMARY KEY, seconds REAL NOT NULL, duration REAL NOT NULL, updated REAL NOT NULL);

      """
    guard sqlite3_exec(connection.handle, schema, nil, nil, nil) == SQLITE_OK else {
      throw LibraryIndexError.database(String(cString: sqlite3_errmsg(connection.handle)))
    }
    if version < 2 {
      let migration = """
        BEGIN IMMEDIATE;
        ALTER TABLE videos ADD COLUMN first_discovered REAL NOT NULL DEFAULT 0;
        UPDATE videos SET first_discovered=COALESCE(
          (SELECT completed FROM source_scans WHERE source_scans.category=videos.category),
          CAST(strftime('%s','now') AS REAL));
        PRAGMA user_version=2;
        COMMIT;
        """
      guard sqlite3_exec(connection.handle, migration, nil, nil, nil) == SQLITE_OK else {
        _ = sqlite3_exec(connection.handle, "ROLLBACK", nil, nil, nil)
        throw LibraryIndexError.database(String(cString: sqlite3_errmsg(connection.handle)))
      }
    }
    let folderSchema = """
      BEGIN IMMEDIATE;
      CREATE TABLE IF NOT EXISTS folders(category TEXT NOT NULL, root TEXT NOT NULL,
        url TEXT NOT NULL, payload TEXT NOT NULL, checked REAL NOT NULL, PRIMARY KEY(category,url));
      CREATE TABLE IF NOT EXISTS index_state(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL);
      INSERT OR IGNORE INTO index_state VALUES(1,0);
      PRAGMA user_version=4;
      COMMIT;
      """
    guard sqlite3_exec(connection.handle, folderSchema, nil, nil, nil) == SQLITE_OK else {
      _ = sqlite3_exec(connection.handle, "ROLLBACK", nil, nil, nil)
      throw LibraryIndexError.database("Could not migrate folder snapshots.")
    }
    guard searchable else { return }
    var searchStatement: OpaquePointer?
    sqlite3_prepare_v2(
      connection.handle, "SELECT 1 FROM sqlite_master WHERE name='search_documents'", -1,
      &searchStatement, nil)
    let searchExisted = sqlite3_step(searchStatement) == SQLITE_ROW
    sqlite3_finalize(searchStatement)
    let searchSchema = """
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
      """
    guard sqlite3_exec(connection.handle, searchSchema, nil, nil, nil) == SQLITE_OK else {
      throw LibraryIndexError.database("Could not create the portable fuzzy-search index.")
    }
    if !searchExisted {
      guard
        sqlite3_exec(
          connection.handle, "INSERT OR IGNORE INTO search_dirty SELECT category,url FROM videos",
          nil, nil, nil) == SQLITE_OK
      else {
        throw LibraryIndexError.database("Could not queue the search index upgrade.")
      }
    }
    // Older deployment runtimes may not support trigram. Preserve literal substring matching.
    let fts = """
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
      """
    hasTrigram = sqlite3_exec(connection.handle, fts, nil, nil, nil) == SQLITE_OK
    if hasTrigram && !ftsExisted {
      _ = sqlite3_exec(
        connection.handle,
        "INSERT INTO video_fts(video_fts) VALUES('rebuild');", nil, nil, nil)
    }
  }

  static func normalized(_ value: String) -> String {
    value.folding(options: [.caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
  }

  func synchronizeSources(_ roots: [GlobalSearchRoot]) throws {
    try Task.checkCancellation()
    let active = Set(roots.map { $0.id.uuidString })
    let statement = try prepare(
      "SELECT DISTINCT category FROM videos UNION SELECT category FROM source_scans")
    var obsolete: [String] = []
    while sqlite3_step(statement) == SQLITE_ROW {
      let id = string(statement, 0)
      if !active.contains(id) { obsolete.append(id) }
    }
    sqlite3_finalize(statement)
    try transaction {
      let changedSources =
        !obsolete.isEmpty
        || roots.contains { root in
          ((try? count(
            "SELECT COUNT(*) FROM videos WHERE category=? AND (root<>? OR category_name<>?)",
            [root.id.uuidString, root.url.absoluteString, root.name])) ?? 0) > 0
        }
      if changedSources { try execute("UPDATE index_state SET revision=revision+1 WHERE id=1") }
      for id in obsolete {
        try execute("DELETE FROM videos WHERE category=?", [id])
        try execute("DELETE FROM source_scans WHERE category=?", [id])
        try execute("DELETE FROM folders WHERE category=?", [id])
      }
      for root in roots {
        try execute(
          "DELETE FROM folders WHERE category=? AND root<>?",
          [root.id.uuidString, root.url.absoluteString])
        try execute(
          "DELETE FROM videos WHERE category=? AND root<>?",
          [root.id.uuidString, root.url.absoluteString])
        try execute(
          "DELETE FROM source_scans WHERE category=? AND root<>?",
          [root.id.uuidString, root.url.absoluteString])
        try execute(
          "UPDATE videos SET category_name=? WHERE category=? AND category_name<>?",
          [root.name, root.id.uuidString, root.name])
      }
    }
  }

  func needsRefresh(_ roots: [GlobalSearchRoot], now: Date = .now) throws -> Bool {
    for root in roots {
      let statement = try prepare("SELECT completed FROM source_scans WHERE category=? AND root=?")
      bind(root.id.uuidString, to: statement, at: 1)
      bind(root.url.absoluteString, to: statement, at: 2)
      let found = sqlite3_step(statement) == SQLITE_ROW
      let completed = found ? sqlite3_column_double(statement, 0) : 0
      sqlite3_finalize(statement)
      if !found || now.timeIntervalSince1970 - completed >= 86400 { return true }
    }
    return false
  }

  func lastRefresh() throws -> Date? {
    let statement = try prepare("SELECT MIN(completed) FROM source_scans")
    defer { sqlite3_finalize(statement) }
    guard sqlite3_step(statement) == SQLITE_ROW,
      sqlite3_column_type(statement, 0) != SQLITE_NULL
    else { return nil }
    return Date(timeIntervalSince1970: sqlite3_column_double(statement, 0))
  }

  func upsert(_ results: [GlobalSearchResult], generation: UUID) throws {
    try Task.checkCancellation()
    guard !results.isEmpty else { return }
    try transaction {
      let statement = try prepare(
        """
        INSERT INTO videos(category,root,category_name,url,name,search_name,relative_path,
          artwork,size,modified,generation,first_discovered) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(category,url) DO UPDATE SET root=excluded.root,
          category_name=excluded.category_name,name=excluded.name,search_name=excluded.search_name,
          relative_path=excluded.relative_path,artwork=excluded.artwork,size=excluded.size,
          modified=excluded.modified,generation=excluded.generation
        """)
      defer { sqlite3_finalize(statement) }
      try execute("UPDATE index_state SET revision=revision+1 WHERE id=1")
      for result in results {
        try Task.checkCancellation()
        let boundary = try URLBoundary(root: result.categoryRoot)
        guard MediaFileType.isVideo(result.entry), boundary.contains(result.entry.url),
          !result.relativePath.split(separator: "/").contains("..")
        else {
          throw DirectoryError.outsideCategoryRoot
        }
        sqlite3_reset(statement)
        sqlite3_clear_bindings(statement)
        let values: [String?] = [
          result.categoryID.uuidString, result.categoryRoot.absoluteString,
          result.categoryName, result.entry.url.absoluteString, result.entry.name,
          Self.normalized(result.entry.name), result.relativePath,
          result.artworkURL?.absoluteString,
        ]
        for (offset, value) in values.enumerated() {
          bind(value, to: statement, at: Int32(offset + 1))
        }
        if let size = result.entry.size { sqlite3_bind_int64(statement, 9, size) }
        if let date = result.entry.modifiedAt {
          sqlite3_bind_double(statement, 10, date.timeIntervalSince1970)
        }
        bind(generation.uuidString, to: statement, at: 11)
        sqlite3_bind_double(statement, 12, Date.now.timeIntervalSince1970)
        try stepDone(statement)
      }
    }
  }

  func completeSource(_ root: GlobalSearchRoot, generation: UUID, now: Date = .now) throws {
    try Task.checkCancellation()
    try transaction {
      try execute(
        "DELETE FROM videos WHERE category=? AND generation<>?",
        [root.id.uuidString, generation.uuidString])
      let statement = try prepare("INSERT OR REPLACE INTO source_scans VALUES(?,?,?)")
      defer { sqlite3_finalize(statement) }
      bind(root.id.uuidString, to: statement, at: 1)
      bind(root.url.absoluteString, to: statement, at: 2)
      sqlite3_bind_double(statement, 3, now.timeIntervalSince1970)
      try stepDone(statement)
    }
  }

  func revision() throws -> Int { try count("SELECT revision FROM index_state WHERE id=1") }

  private func count(_ sql: String, _ values: [String] = []) throws -> Int {
    let statement = try prepare(sql)
    defer { sqlite3_finalize(statement) }
    for (offset, value) in values.enumerated() { bind(value, to: statement, at: Int32(offset + 1)) }
    guard sqlite3_step(statement) == SQLITE_ROW else { throw databaseError() }
    return Int(sqlite3_column_int64(statement, 0))
  }

  func folder(_ scope: IndexScope) throws -> IndexedFolder? {
    let statement = try prepare("SELECT payload FROM folders WHERE category=? AND root=? AND url=?")
    defer { sqlite3_finalize(statement) }
    for (i, value) in [
      scope.root.id.uuidString, scope.root.url.absoluteString, scope.folder.absoluteString,
    ].enumerated() {
      bind(value, to: statement, at: Int32(i + 1))
    }
    guard sqlite3_step(statement) == SQLITE_ROW else { return nil }
    return try JSONDecoder().decode(IndexedFolder.self, from: Data(string(statement, 0).utf8))
  }

  func saveFolder(_ value: IndexedFolder, scope: IndexScope) throws {
    try Task.checkCancellation()
    let payload = String(decoding: try JSONEncoder().encode(value), as: UTF8.self)
    try execute(
      "INSERT OR REPLACE INTO folders VALUES(?,?,?,?,?)",
      [
        scope.root.id.uuidString, scope.root.url.absoluteString, scope.folder.absoluteString,
        payload,
        String(value.checked.timeIntervalSince1970),
      ])
  }

  func folderRows(roots: [GlobalSearchRoot]) throws -> [IndexFolderRow] {
    var rows: [IndexFolderRow] = []
    for root in roots {
      let boundary = try URLBoundary(root: root.url)
      let completed = try prepare("SELECT completed FROM source_scans WHERE category=? AND root=?")
      bind(root.id.uuidString, to: completed, at: 1)
      bind(root.url.absoluteString, to: completed, at: 2)
      let sourceDate =
        sqlite3_step(completed) == SQLITE_ROW
        ? Date(timeIntervalSince1970: sqlite3_column_double(completed, 0)) : nil
      sqlite3_finalize(completed)
      var sourceRows: [String: IndexFolderRow] = [:]
      let statement = try prepare(
        "SELECT url,checked FROM folders WHERE category=? AND root=? ORDER BY url")
      defer { sqlite3_finalize(statement) }
      bind(root.id.uuidString, to: statement, at: 1)
      bind(root.url.absoluteString, to: statement, at: 2)
      while sqlite3_step(statement) == SQLITE_ROW {
        guard let url = URL(string: string(statement, 0)), boundary.contains(url) else { continue }
        let scope = IndexScope(root: root, folder: url)
        sourceRows[scope.prefix] = IndexFolderRow(
          scope: scope, checked: Date(timeIntervalSince1970: sqlite3_column_double(statement, 1)),
          files: 0)
      }
      let rootScope = IndexScope(root: root, folder: root.url)
      if sourceRows[rootScope.prefix] == nil {
        sourceRows[rootScope.prefix] = IndexFolderRow(
          scope: rootScope, checked: sourceDate, files: 0)
      }
      // Existing v2 indexes already contain paths. Infer their tree before any network refresh.
      // Counts include descendants; use one streamed URL query rather than an inventory allocation.
      var counts: [String: Int] = [:]
      let files = try prepare("SELECT url FROM videos WHERE category=? AND root=?")
      defer { sqlite3_finalize(files) }
      bind(root.id.uuidString, to: files, at: 1)
      bind(root.url.absoluteString, to: files, at: 2)
      while sqlite3_step(files) == SQLITE_ROW {
        guard let url = URL(string: string(files, 0)) else { continue }
        var folder = url.deletingLastPathComponent()
        while boundary.contains(folder) {
          let scope = IndexScope(
            root: root, folder: folder.path == root.url.path ? root.url : folder)
          counts[scope.prefix, default: 0] += 1
          if sourceRows[scope.prefix] == nil {
            sourceRows[scope.prefix] = IndexFolderRow(scope: scope, checked: sourceDate, files: 0)
          }
          if folder.path == root.url.path { break }
          let parent = folder.deletingLastPathComponent()
          if parent.path == folder.path { break }
          folder = parent
        }
      }
      for (prefix, row) in sourceRows {
        rows.append(
          IndexFolderRow(scope: row.scope, checked: row.checked, files: counts[prefix] ?? 0))
      }
    }
    return rows.sorted { $0.scope.folder.absoluteString < $1.scope.folder.absoluteString }
  }

  func publish(stagingURL: URL, scopes: [IndexScope], generation: UUID) throws
    -> IndexRefreshSummary
  {
    try Task.checkCancellation()
    try execute("ATTACH DATABASE ? AS incoming", [stagingURL.path])
    defer { try? execute("DETACH DATABASE incoming") }
    var summary = IndexRefreshSummary()
    try transaction {
      for scope in IndexScope.compact(scopes) {
        try Task.checkCancellation()
        let values = [
          scope.root.id.uuidString, scope.root.url.absoluteString, String(scope.prefix.count),
          scope.prefix,
        ]
        let selected = "category=? AND root=? AND substr(url,1,?)=?"
        summary.added += try count(
          "SELECT COUNT(*) FROM incoming.videos s WHERE \(selected) AND NOT EXISTS(SELECT 1 FROM main.videos a WHERE a.category=s.category AND a.url=s.url)",
          values)
        let differs =
          "a.root IS NOT s.root OR a.category_name IS NOT s.category_name OR a.name IS NOT s.name OR a.relative_path IS NOT s.relative_path OR a.artwork IS NOT s.artwork OR a.size IS NOT s.size OR a.modified IS NOT s.modified"
        summary.changed += try count(
          "SELECT COUNT(*) FROM incoming.videos s WHERE \(selected) AND EXISTS(SELECT 1 FROM main.videos a WHERE a.category=s.category AND a.url=s.url AND (\(differs)))",
          values)
        summary.removed += try count(
          "SELECT COUNT(*) FROM main.videos a WHERE \(selected) AND NOT EXISTS(SELECT 1 FROM incoming.videos s WHERE s.category=a.category AND s.url=a.url)",
          values)
        try execute(
          """
          INSERT INTO main.videos SELECT * FROM incoming.videos WHERE \(selected)
          ON CONFLICT(category,url) DO UPDATE SET root=excluded.root,category_name=excluded.category_name,
          name=excluded.name,search_name=excluded.search_name,relative_path=excluded.relative_path,
          artwork=excluded.artwork,size=excluded.size,modified=excluded.modified,generation=excluded.generation
          WHERE videos.root IS NOT excluded.root OR videos.category_name IS NOT excluded.category_name
          OR videos.name IS NOT excluded.name OR videos.relative_path IS NOT excluded.relative_path
          OR videos.artwork IS NOT excluded.artwork OR videos.size IS NOT excluded.size OR videos.modified IS NOT excluded.modified
          """, values)
        try execute(
          "DELETE FROM main.videos WHERE \(selected) AND NOT EXISTS(SELECT 1 FROM incoming.videos s WHERE s.category=main.videos.category AND s.url=main.videos.url)",
          values)
        let folderSelected = "category=? AND root=? AND (url=? OR substr(url,1,?)=?)"
        let folderValues = [
          scope.root.id.uuidString, scope.root.url.absoluteString, scope.folder.absoluteString,
          String(scope.prefix.count), scope.prefix,
        ]
        try execute("DELETE FROM main.folders WHERE \(folderSelected)", folderValues)
        try execute(
          "INSERT INTO main.folders SELECT * FROM incoming.folders WHERE \(folderSelected)",
          folderValues)
        if scope.folder == scope.root.url {
          try execute(
            "INSERT OR REPLACE INTO source_scans VALUES(?,?,?)",
            [
              scope.root.id.uuidString, scope.root.url.absoluteString,
              String(Date.now.timeIntervalSince1970),
            ])
        }
      }
      if summary.added + summary.changed + summary.removed > 0 {
        try execute("UPDATE index_state SET revision=revision+1 WHERE id=1")
      }
    }
    return summary
  }

  func search(_ rawQuery: String, limit: Int = 500, offset: Int = 0, fuzzy: Bool = true) throws
    -> [GlobalSearchResult]
  {
    let query = rawQuery.trimmingCharacters(in: .whitespacesAndNewlines)
    guard query.count >= 3 else { throw GlobalSearchError.queryTooShort }
    try buildPendingSearchDocuments()
    let normalized = FuzzySearch.normalize(query)
    guard !normalized.isEmpty else { return [] }
    let expression = FuzzySearch.matchExpression(query)
    let approximate = fuzzy && expression != nil
    let indexed = expression != nil
    let source =
      indexed
      ? "videos JOIN search_documents d ON videos.category=d.category AND videos.url=d.url JOIN search_document_fts f ON d.rowid=f.rowid"
      : "videos JOIN search_documents d ON videos.category=d.category AND videos.url=d.url"
    let predicate =
      indexed
      ? (approximate
        ? "search_document_fts MATCH ?" : "search_document_fts MATCH ? AND instr(d.normalized,?)>0")
      : "instr(d.normalized,?)>0"
    let statement = try prepare(
      """
      SELECT videos.category,category_name,root,videos.url,videos.name,relative_path,artwork,size,modified,d.aliases
      FROM \(source) WHERE \(predicate)
      ORDER BY \(approximate ? "CASE WHEN instr(d.normalized,?)>0 THEN 0 ELSE 1 END,bm25(search_document_fts)," : "") category_name,videos.name,videos.url LIMIT ? OFFSET ?
      """)
    defer { sqlite3_finalize(statement) }
    bind(
      indexed
        ? (approximate ? expression! : expression!.replacingOccurrences(of: " OR ", with: " AND "))
        : normalized, to: statement, at: 1)
    let limitBinding: Int32 = indexed ? 3 : 2
    if indexed { bind(normalized, to: statement, at: 2) }
    sqlite3_bind_int(statement, limitBinding, Int32(approximate ? 4096 : max(1, min(500, limit))))
    sqlite3_bind_int(statement, limitBinding + 1, Int32(approximate ? 0 : max(0, offset)))
    var aliases: [GlobalSearchResultID: String] = [:]
    var results: [GlobalSearchResult] = []
    var status = sqlite3_step(statement)
    while status == SQLITE_ROW {
      try Task.checkCancellation()
      if let category = UUID(uuidString: string(statement, 0)),
        let root = URL(string: string(statement, 2)), let url = URL(string: string(statement, 3)),
        (try? URLBoundary(root: root).contains(url)) == true
      {
        let size =
          sqlite3_column_type(statement, 7) == SQLITE_NULL
          ? nil : sqlite3_column_int64(statement, 7)
        let modified =
          sqlite3_column_type(statement, 8) == SQLITE_NULL
          ? nil : Date(timeIntervalSince1970: sqlite3_column_double(statement, 8))
        aliases[GlobalSearchResultID(categoryID: category, url: url)] = string(statement, 9)
        results.append(
          GlobalSearchResult(
            categoryID: category, categoryName: string(statement, 1),
            categoryRoot: root,
            entry: DirectoryEntry(
              name: string(statement, 4), url: url,
              kind: .file, size: size, modifiedAt: modified), relativePath: string(statement, 5),
            artworkURL: URL(string: string(statement, 6))))
      }
      status = sqlite3_step(statement)
    }
    guard status == SQLITE_DONE else { throw databaseError() }
    if approximate && results.count == 4096 {
      let count = try prepare("SELECT COUNT(*) FROM search_documents WHERE instr(normalized,?)>0")
      bind(normalized, to: count, at: 1)
      let exactCount = sqlite3_step(count) == SQLITE_ROW ? sqlite3_column_int64(count, 0) : 0
      sqlite3_finalize(count)
      if exactCount >= 4096 {
        return try search(rawQuery, limit: limit, offset: offset, fuzzy: false)
      }
    }
    if approximate {
      let ranked = results.compactMap { result -> (GlobalSearchResult, Int)? in
        let scores =
          ([result.entry.name] + (aliases[result.id] ?? "").components(separatedBy: "\n"))
          .compactMap { FuzzySearch.score(query: query, name: $0) }
        guard let score = scores.min() else {
          return nil
        }
        return (result, score)
      }.sorted { lhs, rhs in
        if lhs.1 != rhs.1 { return lhs.1 < rhs.1 }
        let left =
          lhs.0.categoryName + "\0" + lhs.0.entry.name + "\0" + lhs.0.entry.url.absoluteString
        let right =
          rhs.0.categoryName + "\0" + rhs.0.entry.name + "\0" + rhs.0.entry.url.absoluteString
        return left < right
      }
      return Array(ranked.dropFirst(max(0, offset)).prefix(max(1, min(500, limit)))).map { $0.0 }
    }
    return results
  }

  /// Indexes changed filenames once; query-time work never rereads the full catalogue.
  private func buildPendingSearchDocuments() throws {
    try transaction {
      while true {
        let pending = try prepare(
          "SELECT v.category,v.url,v.name,COALESCE(d.aliases,'') FROM search_dirty q JOIN videos v ON v.category=q.category AND v.url=q.url LEFT JOIN search_documents d ON d.category=v.category AND d.url=v.url LIMIT 500"
        )
        var rows: [(String, String, String, String)] = []
        while sqlite3_step(pending) == SQLITE_ROW {
          rows.append(
            (string(pending, 0), string(pending, 1), string(pending, 2), string(pending, 3)))
        }
        sqlite3_finalize(pending)
        if rows.isEmpty { break }
        let insert = try prepare(
          "INSERT INTO search_documents(category,url,name,aliases,normalized,grams) VALUES(?,?,?,?,?,?) ON CONFLICT(category,url) DO UPDATE SET name=excluded.name,aliases=excluded.aliases,normalized=excluded.normalized,grams=excluded.grams"
        )
        let remove = try prepare("DELETE FROM search_dirty WHERE category=? AND url=?")
        defer {
          sqlite3_finalize(insert)
          sqlite3_finalize(remove)
        }
        for row in rows {
          try Task.checkCancellation()
          let text = row.2 + " " + row.3
          sqlite3_reset(insert)
          sqlite3_clear_bindings(insert)
          let values = [
            row.0, row.1, row.2, row.3, FuzzySearch.normalize(text), FuzzySearch.indexGrams(text),
          ]
          for (offset, value) in values.enumerated() {
            bind(value, to: insert, at: Int32(offset + 1))
          }
          try stepDone(insert)
          sqlite3_reset(remove)
          sqlite3_clear_bindings(remove)
          bind(row.0, to: remove, at: 1)
          bind(row.1, to: remove, at: 2)
          try stepDone(remove)
        }
      }
    }
  }

  /// Associates provider/corrected display titles with verified original file identities.
  func saveSearchAliases(_ titles: [EntertainmentTitle]) throws {
    try buildPendingSearchDocuments()
    try transaction {
      let update = try prepare(
        "UPDATE search_documents SET aliases=? WHERE category=? AND url=? AND aliases!=?")
      let dirty = try prepare("INSERT OR IGNORE INTO search_dirty VALUES(?,?)")
      defer {
        sqlite3_finalize(update)
        sqlite3_finalize(dirty)
      }
      for title in titles {
        let alias = [title.name, title.metadata?.title ?? ""].filter { !$0.isEmpty }.joined(
          separator: "\n")
        for version in title.versions {
          try Task.checkCancellation()
          sqlite3_reset(update)
          sqlite3_clear_bindings(update)
          bind(alias, to: update, at: 1)
          bind(version.media.categoryID.uuidString, to: update, at: 2)
          bind(version.media.entry.url.absoluteString, to: update, at: 3)
          bind(alias, to: update, at: 4)
          try stepDone(update)
          if sqlite3_changes(connection.handle) > 0 {
            sqlite3_reset(dirty)
            sqlite3_clear_bindings(dirty)
            bind(version.media.categoryID.uuidString, to: dirty, at: 1)
            bind(version.media.entry.url.absoluteString, to: dirty, at: 2)
            try stepDone(dirty)
          }
        }
      }
    }
    try buildPendingSearchDocuments()
  }

  /// Supplies all indexed videos and durable discovery dates to the Home catalogue.
  func inventory() throws -> [EntertainmentVersion] {
    let statement = try prepare(
      """
      SELECT category,category_name,root,videos.url,name,relative_path,artwork,size,modified,
        first_discovered,COALESCE(playback.seconds,0),COALESCE(playback.duration,0),playback.updated
      FROM videos LEFT JOIN playback ON playback.url=videos.url ORDER BY category_name,name
      """)
    defer { sqlite3_finalize(statement) }
    var versions: [EntertainmentVersion] = []
    var status = sqlite3_step(statement)
    while status == SQLITE_ROW {
      try Task.checkCancellation()
      if let category = UUID(uuidString: string(statement, 0)),
        let root = URL(string: string(statement, 2)), let url = URL(string: string(statement, 3)),
        (try? URLBoundary(root: root).contains(url)) == true
      {
        let media = GlobalSearchResult(
          categoryID: category, categoryName: string(statement, 1),
          categoryRoot: root,
          entry: DirectoryEntry(
            name: string(statement, 4), url: url, kind: .file,
            size: sqlite3_column_type(statement, 7) == SQLITE_NULL
              ? nil : sqlite3_column_int64(statement, 7),
            modifiedAt: sqlite3_column_type(statement, 8) == SQLITE_NULL
              ? nil : Date(timeIntervalSince1970: sqlite3_column_double(statement, 8))),
          relativePath: string(statement, 5), artworkURL: URL(string: string(statement, 6)))
        versions.append(
          EntertainmentVersion(
            media: media,
            firstDiscovered: Date(timeIntervalSince1970: sqlite3_column_double(statement, 9)),
            progressSeconds: sqlite3_column_double(statement, 10),
            duration: sqlite3_column_double(statement, 11),
            lastPlayed: sqlite3_column_type(statement, 12) == SQLITE_NULL
              ? nil : Date(timeIntervalSince1970: sqlite3_column_double(statement, 12))))
      }
      status = sqlite3_step(statement)
    }
    guard status == SQLITE_DONE else { throw databaseError() }
    return versions
  }

  func cachedMetadata(key: String, now: Date = .now, allowStale: Bool = false) throws -> String? {
    let statement = try prepare("SELECT payload,fetched FROM metadata WHERE cache_key=?")
    defer { sqlite3_finalize(statement) }
    bind(key, to: statement, at: 1)
    guard sqlite3_step(statement) == SQLITE_ROW,
      allowStale || now.timeIntervalSince1970 - sqlite3_column_double(statement, 1) < 7 * 86400
    else { return nil }
    return string(statement, 0)
  }

  struct MetadataRecord: Sendable {
    let payload: String
    let fetched: Double
    func isFresh(now: Date = .now) -> Bool { now.timeIntervalSince1970 - fetched < 7 * 86400 }
  }

  /// SQLite bindings and temporary payloads stay bounded independently of catalogue size.
  func metadataRecords(keys: [String]) throws -> [String: MetadataRecord] {
    guard !keys.isEmpty else { return [:] }
    precondition(keys.count <= 800)
    let placeholders = Array(repeating: "?", count: keys.count).joined(separator: ",")
    let statement = try prepare(
      "SELECT cache_key,payload,fetched FROM metadata WHERE cache_key IN (\(placeholders))")
    defer { sqlite3_finalize(statement) }
    for (offset, key) in keys.enumerated() { bind(key, to: statement, at: Int32(offset + 1)) }
    var records: [String: MetadataRecord] = [:]
    var status = sqlite3_step(statement)
    while status == SQLITE_ROW {
      try Task.checkCancellation()
      records[string(statement, 0)] = MetadataRecord(
        payload: string(statement, 1), fetched: sqlite3_column_double(statement, 2))
      status = sqlite3_step(statement)
    }
    guard status == SQLITE_DONE else { throw databaseError() }
    return records
  }

  func saveMetadata(key: String, payload: String) throws {
    let statement = try prepare("INSERT OR REPLACE INTO metadata VALUES(?,?,?)")
    defer { sqlite3_finalize(statement) }
    bind(key, to: statement, at: 1)
    bind(payload, to: statement, at: 2)
    sqlite3_bind_double(statement, 3, Date.now.timeIntervalSince1970)
    try stepDone(statement)
    try execute("UPDATE index_state SET revision=revision+1 WHERE id=1")
  }

  func savePlaybackPosition(url: URL, seconds: Double, duration: Double) throws {
    guard seconds.isFinite, duration.isFinite else { return }
    let statement = try prepare("INSERT OR REPLACE INTO playback VALUES(?,?,?,?)")
    defer { sqlite3_finalize(statement) }
    bind(url.absoluteString, to: statement, at: 1)
    sqlite3_bind_double(statement, 2, max(0, seconds))
    sqlite3_bind_double(statement, 3, max(0, duration))
    sqlite3_bind_double(statement, 4, Date.now.timeIntervalSince1970)
    try stepDone(statement)
  }

  func playbackPosition(url: URL) throws -> Double? {
    let statement = try prepare("SELECT seconds,duration FROM playback WHERE url=?")
    defer { sqlite3_finalize(statement) }
    bind(url.absoluteString, to: statement, at: 1)
    guard sqlite3_step(statement) == SQLITE_ROW else { return nil }
    let seconds = sqlite3_column_double(statement, 0)
    let duration = sqlite3_column_double(statement, 1)
    guard seconds >= 5, duration > seconds + 10 else { return nil }
    return seconds
  }

  func clearPlaybackPositions(ids: Set<String>) throws {
    guard !ids.isEmpty else { return }
    try transaction {
      let statement = try prepare("DELETE FROM playback WHERE url=?")
      defer { sqlite3_finalize(statement) }
      for id in ids {
        sqlite3_reset(statement)
        sqlite3_clear_bindings(statement)
        bind(id, to: statement, at: 1)
        try stepDone(statement)
      }
      try execute("UPDATE index_state SET revision=revision+1 WHERE id=1")
    }
  }

  private func transaction(_ body: () throws -> Void) throws {
    try execute("BEGIN IMMEDIATE")
    do {
      try body()
      try Task.checkCancellation()
      try execute("COMMIT")
    } catch {
      try? execute("ROLLBACK")
      throw error
    }
  }
  private func prepare(_ sql: String) throws -> OpaquePointer {
    var statement: OpaquePointer?
    guard sqlite3_prepare_v2(connection.handle, sql, -1, &statement, nil) == SQLITE_OK,
      let statement
    else { throw databaseError() }
    return statement
  }
  private func execute(_ sql: String, _ values: [String] = []) throws {
    let statement = try prepare(sql)
    defer { sqlite3_finalize(statement) }
    for (offset, value) in values.enumerated() { bind(value, to: statement, at: Int32(offset + 1)) }
    try stepDone(statement)
  }
  private func bind(_ value: String?, to statement: OpaquePointer, at index: Int32) {
    if let value {
      value.withCString { _ = sqlite3_bind_text(statement, index, $0, -1, Self.transient) }
    } else {
      sqlite3_bind_null(statement, index)
    }
  }
  private func stepDone(_ statement: OpaquePointer) throws {
    guard sqlite3_step(statement) == SQLITE_DONE else { throw databaseError() }
  }
  private func string(_ statement: OpaquePointer, _ column: Int32) -> String {
    guard let value = sqlite3_column_text(statement, column) else { return "" }
    return String(cString: value)
  }
  private func databaseError() -> LibraryIndexError {
    .database(String(cString: sqlite3_errmsg(connection.handle)))
  }
}

actor LibraryIndexer {
  private let scanner: GlobalSearchService?
  private let directoryService: DirectoryService?
  private let maximumConcurrentFolders: Int
  private(set) var summary = IndexRefreshSummary()
  init(directoryService: DirectoryService, maximumConcurrentFolders: Int = 2) {
    self.directoryService = directoryService
    self.scanner = nil
    self.maximumConcurrentFolders = maximumConcurrentFolders
  }
  init(scanner: GlobalSearchService) {
    self.scanner = scanner
    directoryService = nil
    maximumConcurrentFolders = 2
  }

  func refresh(
    roots: [GlobalSearchRoot], index: LibraryIndex, generation: UUID,
    scopes: [IndexScope]? = nil, policies: IndexPolicies = .init(), automatic: Bool = false,
    full: Bool = false,
    concurrencyLimit: (@Sendable () async -> Int)? = nil,
    update: @escaping @Sendable (GlobalSearchSnapshot) async -> Void
  ) async throws -> GlobalSearchSnapshot {
    let selected = IndexScope.compact(scopes ?? roots.map { IndexScope(root: $0, folder: $0.url) })
    if selected.isEmpty {
      return GlobalSearchSnapshot(results: [], progress: .init(), failures: [])
    }
    let temp = FileManager.default.temporaryDirectory.appending(
      path: "Myra-staging-\(generation)", directoryHint: .isDirectory)
    defer { try? FileManager.default.removeItem(at: temp) }
    let staged = try LibraryIndex(url: temp.appending(path: "Index.sqlite"), searchable: false)
    let loader = directoryService.map {
      IndexListingLoader(
        service: $0, active: index, staging: staged, roots: roots, policies: policies,
        selected: selected, automatic: automatic, full: full)
    }
    let service: GlobalSearchService
    if let scanner {
      service = scanner
    } else if let loader {
      service = GlobalSearchService(maximumConcurrentFolders: maximumConcurrentFolders) {
        url, boundary in
        try await loader.listing(at: url, boundary: boundary)
      }
    } else {
      throw LibraryIndexError.database("No directory loader available")
    }
    let selectedIDs = Set(selected.map { $0.root.id })
    let snapshot = try await service.search(
      query: "", roots: roots.filter { selectedIDs.contains($0.id) }, matchAllVideos: true,
      scopes: selected,
      batchSink: { batch in try await staged.upsert(batch, generation: generation) },
      concurrencyLimit: concurrencyLimit, update: update)
    try Task.checkCancellation()
    let failed = Set(snapshot.failures.map(\.categoryID))
    summary = try await index.publish(
      stagingURL: staged.url, scopes: selected.filter { !failed.contains($0.root.id) },
      generation: generation)
    if let loader {
      let scanned = await loader.summary
      summary.checked = scanned.checked
      summary.unchanged = scanned.unchanged
    }
    return snapshot
  }
}
