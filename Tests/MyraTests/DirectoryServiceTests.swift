import XCTest

@testable import Myra

private enum SearchFixtureError: LocalizedError {
  case unavailable

  var errorDescription: String? { "Fixture unavailable" }
}

private actor SearchListingFixture {
  let listings: [URL: DirectoryListing]
  let failingURLs: Set<URL>
  private(set) var calls: [URL: Int] = [:]

  init(listings: [URL: DirectoryListing], failingURLs: Set<URL> = []) {
    self.listings = listings
    self.failingURLs = failingURLs
  }

  func load(url: URL, boundary: URLBoundary) throws -> DirectoryListing {
    calls[url, default: 0] += 1
    if failingURLs.contains(url) { throw SearchFixtureError.unavailable }
    guard boundary.contains(url), let listing = listings[url] else {
      throw SearchFixtureError.unavailable
    }
    return listing
  }

  func callCount(for url: URL) -> Int { calls[url, default: 0] }
}

private actor SearchSnapshotRecorder {
  private(set) var snapshots: [GlobalSearchSnapshot] = []

  func append(_ snapshot: GlobalSearchSnapshot) { snapshots.append(snapshot) }
}

final class DirectoryServiceTests: XCTestCase {
  private let root = URL(
    string: "http://172.16.50.14/DHAKA-FLIX-14/English%20Movies%20%281080p%29/")!

  func testParsesH5AIFallbackTable() throws {
    let html = """
      <html><body><table>
        <tr><td><img alt="folder-parent"></td><td><a href="..">Parent Directory</a></td></tr>
        <tr><td><img alt="folder"></td><td><a href="%282026%29%201080p/">(2026) 1080p</a></td><td>2026-07-01 15:17</td><td></td></tr>
        <tr><td><img alt="file"></td><td><a href="Movie%20One.mkv">Movie One.mkv</a></td><td>2026-07-02 10:20</td><td>1.5 GB</td></tr>
      </table></body></html>
      """
    let boundary = try URLBoundary(root: root)
    let entries = try DirectoryService.parse(html: html, baseURL: root, boundary: boundary)

    XCTAssertEqual(entries.count, 2)
    XCTAssertEqual(entries[0].kind, .folder)
    XCTAssertEqual(entries[0].name, "(2026) 1080p")
    XCTAssertEqual(entries[1].kind, .file)
    XCTAssertEqual(entries[1].size, 1_610_612_736)
    XCTAssertNotNil(entries[1].modifiedAt)
  }

  func testParsesCommonAutoIndexAnchors() throws {
    let html = """
      <html><body><pre>
      <a href="../">../</a>
      <a href="Hindi%20Movies/">Hindi Movies/</a>
      <a href="sample.mp4">sample.mp4</a>
      </pre></body></html>
      """
    let boundary = try URLBoundary(root: root)
    let entries = try DirectoryService.parse(html: html, baseURL: root, boundary: boundary)
    XCTAssertEqual(entries.map(\.name), ["Hindi Movies", "sample.mp4"])
  }

  func testListingMediaTypesMatchSamOnlineRules() {
    let entries = [
      DirectoryEntry(name: "Season 1", url: root.appending(path: "Season 1/"), kind: .folder),
      DirectoryEntry(name: "Movie.mkv", url: root.appending(path: "Movie.mkv"), kind: .file),
      DirectoryEntry(name: "poster.jpg", url: root.appending(path: "poster.jpg"), kind: .file),
      DirectoryEntry(name: "notes.txt", url: root.appending(path: "notes.txt"), kind: .file),
    ]

    XCTAssertTrue(MediaFileType.isVideo(entries[1]))
    XCTAssertTrue(MediaFileType.isArtwork(entries[2]))
    XCTAssertFalse(MediaFileType.isVideo(entries[2]))
    XCTAssertFalse(MediaFileType.isVideo(entries[3]))
  }

  func testBoundaryRejectsParentAndCrossOrigin() throws {
    let boundary = try URLBoundary(root: root)
    XCTAssertTrue(
      boundary.contains(
        URL(string: "http://172.16.50.14/DHAKA-FLIX-14/English%20Movies%20%281080p%29/2026/")!))
    XCTAssertFalse(boundary.contains(URL(string: "http://172.16.50.14/DHAKA-FLIX-14/")!))
    XCTAssertFalse(
      boundary.contains(
        URL(string: "http://example.com/DHAKA-FLIX-14/English%20Movies%20%281080p%29/")!))
  }

  func testEncodedTraversalIsRejectedByParser() throws {
    let html = #"<a href="%2E%2E/secret/">secret</a>"#
    let boundary = try URLBoundary(root: root)
    let entries = try DirectoryService.parse(html: html, baseURL: root, boundary: boundary)
    XCTAssertTrue(entries.isEmpty)
  }

  func testDirectorySortingKeepsFoldersGroupedAndHandlesDates() {
    let older = Date(timeIntervalSince1970: 100)
    let newer = Date(timeIntervalSince1970: 200)
    let entries = [
      DirectoryEntry(
        name: "Zulu.mkv", url: root.appending(path: "Zulu.mkv"), kind: .file,
        modifiedAt: newer),
      DirectoryEntry(
        name: "Archive", url: root.appending(path: "Archive/"), kind: .folder,
        modifiedAt: older),
      DirectoryEntry(
        name: "Alpha.mkv", url: root.appending(path: "Alpha.mkv"), kind: .file,
        modifiedAt: nil),
    ]

    XCTAssertEqual(
      DirectoryEntrySorter.sorted(entries, by: .name, ascending: true).map(\.name),
      ["Archive", "Alpha.mkv", "Zulu.mkv"])
    XCTAssertEqual(
      DirectoryEntrySorter.sorted(entries, by: .date, ascending: false).map(\.name),
      ["Archive", "Zulu.mkv", "Alpha.mkv"])
  }

  func testLiveProviderListingWhenRequested() async throws {
    guard ProcessInfo.processInfo.environment["MYRA_LIVE_TEST"] == "1" else {
      throw XCTSkip("Set MYRA_LIVE_TEST=1 while connected to the provider network.")
    }
    let listing = try await DirectoryService().validate(rootURL: root)
    XCTAssertGreaterThan(listing.entries.count, 20)
    XCTAssertTrue(listing.entries.contains { $0.kind == .folder && $0.name.contains("2026") })
  }

  func testGlobalSearchRecursesAcrossSourcesAndCarriesArtwork() async throws {
    let firstRoot = URL(string: "http://media.local/first/")!
    let movies = firstRoot.appending(path: "Movies/", directoryHint: .isDirectory)
    let secondRoot = URL(string: "http://media.local/second/")!
    let firstPoster = movies.appending(path: "poster.jpg")
    let secondPoster = secondRoot.appending(path: "cover.webp")
    let fixture = SearchListingFixture(listings: [
      firstRoot: DirectoryListing(
        url: firstRoot,
        entries: [DirectoryEntry(name: "Movies", url: movies, kind: .folder)],
        artworkURL: nil),
      movies: DirectoryListing(
        url: movies,
        entries: [
          DirectoryEntry(
            name: "Dune Part Two.mkv", url: movies.appending(path: "Dune.mkv"), kind: .file),
          DirectoryEntry(
            name: "Dune notes.txt", url: movies.appending(path: "notes.txt"), kind: .file),
        ],
        artworkURL: firstPoster),
      secondRoot: DirectoryListing(
        url: secondRoot,
        entries: [
          DirectoryEntry(name: "DUNE.mp4", url: secondRoot.appending(path: "DUNE.mp4"), kind: .file)
        ],
        artworkURL: secondPoster),
    ])
    let recorder = SearchSnapshotRecorder()
    let service = GlobalSearchService { url, boundary in
      try await fixture.load(url: url, boundary: boundary)
    }

    let snapshot = try await service.search(
      query: "dune",
      roots: [
        GlobalSearchRoot(id: UUID(), name: "First", url: firstRoot),
        GlobalSearchRoot(id: UUID(), name: "Second", url: secondRoot),
      ]
    ) { snapshot in
      await recorder.append(snapshot)
    }

    XCTAssertEqual(snapshot.results.count, 2)
    XCTAssertEqual(snapshot.results.map(\.relativePath), ["Movies/Dune Part Two.mkv", "DUNE.mp4"])
    XCTAssertEqual(
      Set(snapshot.results.compactMap(\.artworkURL)), Set([firstPoster, secondPoster]))
    XCTAssertEqual(snapshot.progress.sourcesCompleted, 2)
    XCTAssertEqual(snapshot.progress.foldersVisited, 3)
    XCTAssertEqual(snapshot.progress.matchesFound, 2)
    let updateCount = await recorder.snapshots.count
    XCTAssertGreaterThan(updateCount, 2)
  }

  func testGlobalSearchAvoidsDirectoryCycles() async throws {
    let root = URL(string: "http://media.local/root/")!
    let child = root.appending(path: "Child/", directoryHint: .isDirectory)
    let fixture = SearchListingFixture(listings: [
      root: DirectoryListing(
        url: root,
        entries: [DirectoryEntry(name: "Child", url: child, kind: .folder)],
        artworkURL: nil),
      child: DirectoryListing(
        url: child,
        entries: [DirectoryEntry(name: "Root", url: root, kind: .folder)],
        artworkURL: nil),
    ])
    let service = GlobalSearchService { url, boundary in
      try await fixture.load(url: url, boundary: boundary)
    }

    let snapshot = try await service.search(
      query: "none", roots: [GlobalSearchRoot(id: UUID(), name: "Cycle", url: root)]
    ) { _ in }

    XCTAssertTrue(snapshot.results.isEmpty)
    XCTAssertEqual(snapshot.progress.foldersVisited, 2)
    let rootCalls = await fixture.callCount(for: root)
    let childCalls = await fixture.callCount(for: child)
    XCTAssertEqual(rootCalls, 1)
    XCTAssertEqual(childCalls, 1)
  }

  func testGlobalSearchKeepsPartialResultsAndReportsFailedSource() async throws {
    let workingRoot = URL(string: "http://media.local/working/")!
    let failingRoot = URL(string: "http://media.local/failing/")!
    let fixture = SearchListingFixture(
      listings: [
        workingRoot: DirectoryListing(
          url: workingRoot,
          entries: [
            DirectoryEntry(
              name: "Avatar.mkv", url: workingRoot.appending(path: "Avatar.mkv"), kind: .file)
          ],
          artworkURL: nil)
      ],
      failingURLs: [failingRoot])
    let service = GlobalSearchService { url, boundary in
      try await fixture.load(url: url, boundary: boundary)
    }

    let snapshot = try await service.search(
      query: "avatar",
      roots: [
        GlobalSearchRoot(id: UUID(), name: "Working", url: workingRoot),
        GlobalSearchRoot(id: UUID(), name: "Offline", url: failingRoot),
      ]
    ) { _ in }

    XCTAssertEqual(snapshot.results.map(\.entry.name), ["Avatar.mkv"])
    XCTAssertEqual(snapshot.failures.count, 1)
    XCTAssertEqual(snapshot.failures.first?.categoryName, "Offline")
    XCTAssertEqual(snapshot.progress.sourcesCompleted, 2)
    XCTAssertEqual(snapshot.progress.failedSources, 1)
  }

  func testGlobalSearchValidatesQueryAndSupportsCancellation() async throws {
    let root = URL(string: "http://media.local/root/")!
    let service = GlobalSearchService { _, _ in
      try await Task.sleep(for: .seconds(10))
      return DirectoryListing(url: root, entries: [], artworkURL: nil)
    }

    await assertThrowsErrorAsync(
      try await service.search(
        query: "ab", roots: [GlobalSearchRoot(id: UUID(), name: "Root", url: root)]
      ) { _ in }
    ) { error in
      XCTAssertEqual(error as? GlobalSearchError, .queryTooShort)
    }

    let task = Task {
      try await service.search(
        query: "movie", roots: [GlobalSearchRoot(id: UUID(), name: "Root", url: root)]
      ) { _ in }
    }
    task.cancel()
    do {
      _ = try await task.value
      XCTFail("Expected cancellation")
    } catch is CancellationError {
      // Expected.
    }
  }
}

private func assertThrowsErrorAsync<T>(
  _ expression: @autoclosure () async throws -> T,
  _ errorHandler: (Error) -> Void = { _ in }
) async {
  do {
    _ = try await expression()
    XCTFail("Expected expression to throw")
  } catch {
    errorHandler(error)
  }
}
