import CSQLite
import XCTest

@testable import Myra

final class FuzzySearchTests: XCTestCase {
  func testTyposReorderedWordsUnicodeAndNumericConstraints() {
    XCTAssertNotNil(FuzzySearch.score(query: "intersteller", name: "Interstellar.2014.1080p.mkv"))
    XCTAssertNotNil(FuzzySearch.score(query: "rings lord", name: "The Lord of the Rings.mkv"))
    XCTAssertNotNil(FuzzySearch.score(query: "amelie", name: "Amélie.2001.mkv"))
    XCTAssertNil(FuzzySearch.score(query: "movie 2024", name: "Movie 2023.mkv"))
    XCTAssertNil(FuzzySearch.score(query: "jaws", name: "Lawrence of Arabia.mkv"))
    XCTAssertLessThan(
      FuzzySearch.score(query: "interstellar", name: "Interstellar.mkv")!,
      FuzzySearch.score(query: "interstellar", name: "Intersteller.mkv")!)
  }

  func testBoundedSafeCandidateExpression() {
    let expression = FuzzySearch.matchExpression(String(repeating: "abcdef ", count: 1000))!
    XCTAssertLessThanOrEqual(expression.components(separatedBy: " OR ").count, 64)
    XCTAssertNil(FuzzySearch.matchExpression("a b"))
    XCTAssertFalse(FuzzySearch.matchExpression("abc OR DELETE")!.contains("DELETE"))
  }

  func testIndexedTypoSearchAndStablePagination() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let index = try LibraryIndex(url: directory.appending(path: "index.sqlite"))
    let root = GlobalSearchRoot(
      id: UUID(), name: "Movies", url: URL(string: "https://example.com/movies/")!)
    let names = [
      "Interstellar.2014.mkv", "Intersteller.2015.mkv", "The.Lord.of.the.Rings.mkv",
      "Unrelated.mkv",
    ]
    let results = names.map { name in
      GlobalSearchResult(
        categoryID: root.id, categoryName: root.name, categoryRoot: root.url,
        entry: DirectoryEntry(name: name, url: root.url.appending(path: name), kind: .file),
        relativePath: name, artworkURL: nil)
    }
    try await index.upsert(results, generation: UUID())
    let all = try await index.search("interstellar")
    XCTAssertEqual(all.first?.entry.name, names[0])
    XCTAssertEqual(all.count, 2)
    let next = try await index.search("interstellar", limit: 1, offset: 1)
    XCTAssertEqual(next.first?.id, all[1].id)
    let typo = try await index.search("intersteller")
    XCTAssertEqual(typo.count, 2)
    let reordered = try await index.search("rings lord")
    XCTAssertEqual(reordered.first?.entry.name, names[2])
  }
  func testMetadataAliasesAndPortableSearchWithoutLegacyTrigram() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let database = directory.appending(path: "index.sqlite")
    let index = try LibraryIndex(url: database)
    let root = GlobalSearchRoot(
      id: UUID(), name: "Movies", url: URL(string: "https://example.com/movies/")!)
    let media = GlobalSearchResult(
      categoryID: root.id, categoryName: root.name, categoryRoot: root.url,
      entry: DirectoryEntry(
        name: "Le.Fabuleux.Destin.2001.mkv", url: root.url.appending(path: "film.mkv"), kind: .file),
      relativePath: "film.mkv", artworkURL: nil)
    try await index.upsert([media], generation: UUID())
    var titles = EntertainmentGrouping.group([
      EntertainmentVersion(media: media, firstDiscovered: .now)
    ])
    titles[0].metadata = EntertainmentMetadata(
      providerID: 194, title: "Amélie", overview: "", posterURL: nil,
      rating: 8.0, voteCount: 100, releaseDate: "2001-04-25", genres: [], language: "fr")
    try await index.saveSearchAliases(titles)
    var connection: OpaquePointer?
    XCTAssertEqual(sqlite3_open(database.path, &connection), SQLITE_OK)
    defer { sqlite3_close(connection) }
    XCTAssertEqual(
      sqlite3_exec(
        connection,
        "DROP TRIGGER videos_ai; DROP TRIGGER videos_au; DROP TRIGGER videos_ad; DROP TABLE video_fts;",
        nil, nil, nil), SQLITE_OK)
    let accent = try await index.search("amelie")
    XCTAssertEqual(accent.first?.id, media.id)
    let typo = try await index.search("ameli")
    XCTAssertEqual(typo.first?.id, media.id)
    let filename = try await index.search("fabuleu destn")
    XCTAssertEqual(filename.first?.id, media.id)
    let exactMiss = try await index.search("fabuleu destn", fuzzy: false)
    XCTAssertTrue(exactMiss.isEmpty)
    let punctuation = try await index.search("---")
    XCTAssertTrue(punctuation.isEmpty)
    let reopened = try LibraryIndex(url: database)
    let persisted = try await reopened.search("amelie", fuzzy: false)
    XCTAssertEqual(persisted.first?.id, media.id)
  }

  func testSubtitleCacheTitleAssociationWithoutProvider() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = try SubtitleCache(directory: directory)
    let identity = MediaIdentity.parse(filename: "Interstellar.2014.mkv")
    let file = try await cache.store(
      Data("1\n00:00:01,000 --> 00:00:02,000\nHello\n".utf8), fileID: 1)
    try await cache.remember(fileID: 1, identity: identity)
    let recovered = await cache.cached(identity: identity)
    XCTAssertEqual(recovered, [file])
    let unrelated = await cache.cached(identity: MediaIdentity.parse(filename: "Other.2024.mkv"))
    XCTAssertTrue(unrelated.isEmpty)
  }

  func testLargeCatalogueFuzzySearchBenchmark() async throws {
    guard ProcessInfo.processInfo.environment["MYRA_FUZZY_BENCHMARK"] == "1" else {
      throw XCTSkip("Opt-in 100k catalogue performance measurement")
    }
    let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let index = try LibraryIndex(url: directory.appending(path: "index.sqlite"))
    let root = GlobalSearchRoot(
      id: UUID(), name: "Movies", url: URL(string: "https://example.com/movies/")!)
    for batch in 0..<200 {
      let results = (0..<500).map { offset in
        let number = batch * 500 + offset
        let name = number == 99999 ? "Interstellar.2014.mkv" : "Catalogue.Movie.\(number).mkv"
        return GlobalSearchResult(
          categoryID: root.id, categoryName: root.name, categoryRoot: root.url,
          entry: DirectoryEntry(name: name, url: root.url.appending(path: name), kind: .file),
          relativePath: name, artworkURL: nil)
      }
      try await index.upsert(results, generation: UUID())
    }
    let indexingStart = Date.now
    try await index.saveSearchAliases([])
    print(
      "Myra one-time search-document index build: \(Date.now.timeIntervalSince(indexingStart)) seconds"
    )
    let start = Date.now
    let results = try await index.search("intersteller")
    let seconds = Date.now.timeIntervalSince(start)
    print("Myra fuzzy search: 100000 synthetic indexed files; query duration \(seconds) seconds")
    XCTAssertEqual(results.first?.entry.name, "Interstellar.2014.mkv")
    XCTAssertLessThan(seconds, 5, "A bounded indexed query should finish well within five seconds")
  }

}
