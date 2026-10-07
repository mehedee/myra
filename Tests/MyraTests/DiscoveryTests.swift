import CSQLite
import XCTest

@testable import Myra

final class DiscoveryTests: XCTestCase {
  private func version(_ filename: String, folder: String = "Movies") -> EntertainmentVersion {
    let root = URL(string: "https://media.example/")!
    let url = root.appending(path: "\(folder)/\(filename)")
    return EntertainmentVersion(
      media: GlobalSearchResult(
        categoryID: UUID(), categoryName: "Movies", categoryRoot: root,
        entry: DirectoryEntry(name: filename, url: url, kind: .file),
        relativePath: "\(folder)/\(filename)", artworkURL: nil),
      firstDiscovered: Date(timeIntervalSince1970: 100))
  }
  func testGroupsYearAndQualityVariantsButNotDifferentMovies() {
    let grouped = EntertainmentGrouping.group([
      version("Movie.2025.1080p.WEB-DL.mkv"), version("Movie.2025.2160p.BluRay.mkv"),
      version("Movie.1995.1080p.mkv"), version("Other.2025.1080p.mkv"),
    ])
    XCTAssertEqual(grouped.count, 3)
    XCTAssertEqual(
      grouped.first(where: { $0.year == "2025" && $0.name == "Movie" })?.versions.count, 2)
  }
  func testYearlessQualityVariantsOnlyGroupInSameFolder() {
    let values = EntertainmentGrouping.group([
      version("Movie.1080p.mkv"), version("Movie.2160p.mkv"),
      version("Movie.720p.mkv", folder: "Elsewhere"), version("Movie.mkv"),
    ])
    XCTAssertEqual(values.count, 3)
    XCTAssertEqual(values.filter { $0.versions.count == 2 }.count, 1)
  }
  func testEpisodesGroupedIntoOneSeriesWithVersionsAndNaturalOrder() {
    let title = EntertainmentGrouping.group([
      version("Show.S01E10.1080p.mkv"), version("Show.S01E02.1080p.mkv"),
      version("Show.S01E02.2160p.mkv"),
    ]).first!
    XCTAssertEqual(title.kind, .series)
    XCTAssertEqual(title.versions.map(\.episode), [2, 2, 10])
  }
  func testInventoryDiscoveryDatesAndPlaybackSurviveRefreshAndReopen() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-discovery-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let url = directory.appending(path: "index.sqlite")
    let index = try LibraryIndex(url: url)
    let media = version("Movie.2025.mkv").media
    try await index.upsert([media], generation: UUID())
    let first = try await index.inventory()
    try await index.savePlaybackPosition(url: media.entry.url, seconds: 15, duration: 100)
    try await index.upsert([media], generation: UUID())
    let reopened = try LibraryIndex(url: url)
    let after = try await reopened.inventory()
    XCTAssertEqual(first.first?.firstDiscovered, after.first?.firstDiscovered)
    XCTAssertEqual(after.first?.progressSeconds, 15)
  }
  func testV1MigrationBacksUpAndPreservesPlayback() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-migration-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let url = directory.appending(path: "index.sqlite")
    var handle: OpaquePointer?
    XCTAssertEqual(sqlite3_open(url.path, &handle), SQLITE_OK)
    let schema = """
      CREATE TABLE videos(category TEXT NOT NULL,root TEXT NOT NULL,category_name TEXT NOT NULL,
        url TEXT NOT NULL,name TEXT NOT NULL,search_name TEXT NOT NULL,relative_path TEXT NOT NULL,
        artwork TEXT,size INTEGER,modified REAL,generation TEXT NOT NULL,PRIMARY KEY(category,url));
      CREATE TABLE playback(url TEXT PRIMARY KEY,seconds REAL NOT NULL,duration REAL NOT NULL,updated REAL NOT NULL);
      INSERT INTO playback VALUES('https://media.example/Movie.mkv',20,100,100);
      PRAGMA user_version=1;
      """
    XCTAssertEqual(sqlite3_exec(handle, schema, nil, nil, nil), SQLITE_OK)
    sqlite3_close(handle)
    let migrated = try LibraryIndex(url: url)
    let position = try await migrated.playbackPosition(
      url: URL(string: "https://media.example/Movie.mkv")!)
    XCTAssertEqual(position, 20)
    XCTAssertTrue(
      FileManager.default.fileExists(atPath: url.appendingPathExtension("v1-backup").path))
    _ = try await migrated.inventory()
  }
  @MainActor
  func testPersonalArchivePersistsAndRejectsMalformedSources() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-personal-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let storage = directory.appending(path: "personal.json")
    let store = EntertainmentStore(library: LibraryController(), storageURL: storage)
    store.toggleWatchlist("movie|matrix|1999")
    store.createCollection(name: "Weekend")
    let reopened = EntertainmentStore(library: LibraryController(), storageURL: storage)
    XCTAssertTrue(reopened.personal.watchlist.contains("movie|matrix|1999"))
    XCTAssertEqual(reopened.personal.collections.first?.name, "Weekend")
    let data = try reopened.exportArchive(sources: [], appPreferences: nil)
    var archive = try reopened.previewImport(data)
    archive.sources = [
      .init(id: UUID(), name: "Unsafe", url: "https://user:password@media.example/")
    ]
    XCTAssertThrowsError(try reopened.previewImport(JSONEncoder().encode(archive)))
    archive.sources = []
    archive.schemaVersion = 999
    XCTAssertThrowsError(try reopened.previewImport(JSONEncoder().encode(archive)))
  }
  @MainActor
  func testExportRemovesURLCredentialsAndQueryTokensFromHistoryAndTitleIDs() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-portable-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = EntertainmentStore(
      library: LibraryController(), storageURL: directory.appending(path: "personal.json"))
    let url = URL(string: "https://username:password@media.example/Movie.mkv?token=secret#part")!
    let media = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Movies",
      categoryRoot: URL(string: "https://media.example/")!,
      entry: DirectoryEntry(name: "Movie.mkv", url: url, kind: .file), relativePath: "Movie.mkv",
      artworkURL: nil)
    store.recordPlayback(media: media, seconds: 10, duration: 100)
    store.toggleWatchlist("file|" + url.absoluteString)
    let output = try store.exportArchive(sources: [], appPreferences: nil)
    let text = String(decoding: output, as: UTF8.self)
    XCTAssertFalse(text.contains("password"))
    XCTAssertFalse(text.contains("secret"))
    let imported = try store.previewImport(output)
    XCTAssertTrue(imported.personal.watchlist.contains("file|https://media.example/Movie.mkv"))
    XCTAssertNotNil(imported.personal.history["https://media.example/Movie.mkv"])
  }
  @MainActor
  func testCorruptPersonalFileIsPreserved() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-corrupt-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let url = directory.appending(path: "personal.json")
    let original = Data("not a valid json document".utf8)
    try original.write(to: url)
    let store = EntertainmentStore(library: LibraryController(), storageURL: url)
    store.toggleWatched("movie")
    XCTAssertEqual(try Data(contentsOf: url), original)
    XCTAssertNotNil(store.errorMessage)
  }
}
