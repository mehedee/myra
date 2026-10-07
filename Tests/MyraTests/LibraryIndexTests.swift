import CSQLite
import Darwin
import XCTest

@testable import Myra

final class LibraryIndexTests: XCTestCase {
  private func databaseURL() -> URL {
    FileManager.default.temporaryDirectory.appending(path: "Myra-index-test-\(UUID().uuidString)")
      .appending(path: "LibraryIndex.sqlite")
  }
  private func root(_ name: String = "Movies") -> GlobalSearchRoot {
    GlobalSearchRoot(id: UUID(), name: name, url: URL(string: "https://media.example/\(name)/")!)
  }
  private func video(_ root: GlobalSearchRoot, name: String, folder: String = "")
    -> GlobalSearchResult
  {
    let path = folder.isEmpty ? name : folder + "/" + name
    let url = root.url.appending(path: path)
    return GlobalSearchResult(
      categoryID: root.id, categoryName: root.name, categoryRoot: root.url,
      entry: DirectoryEntry(
        name: name, url: url, kind: .file, size: 12345,
        modifiedAt: Date(timeIntervalSince1970: 100)), relativePath: path,
      artworkURL: url.deletingLastPathComponent().appending(path: "poster.jpg"))
  }

  func testPersistentCaseInsensitiveSubstringSearchPreservesPathsAndArtwork() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let first = root()
    let second = root("Series")
    let expected = [
      video(first, name: "The.Matrix.1999.mkv", folder: "1999"),
      video(second, name: "Matrix.S01E01.mp4", folder: "Season 1"),
    ]
    try await index.upsert(expected, generation: UUID())
    let reopened = try LibraryIndex(url: url)
    let results = try await reopened.search("aTRi")
    XCTAssertEqual(Set(results), Set(expected))
    let groups = GlobalDownloadManifestBuilder.groups(for: results)
    XCTAssertEqual(groups.count, 2)
    XCTAssertEqual(
      Set(groups.flatMap { $0.manifest.map(\.relativePath) }),
      ["Movies/1999/The.Matrix.1999.mkv", "Series/Season 1/Matrix.S01E01.mp4"])
  }

  func testThreeCharacterMinimumAndLiteralQuery() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    try await index.upsert([video(root(), name: "100%_Movie.mkv")], generation: UUID())
    do {
      _ = try await index.search("ab")
      XCTFail("Short query accepted")
    } catch { XCTAssertEqual(error as? GlobalSearchError, .queryTooShort) }
    let literal = try await index.search("0%_")
    XCTAssertEqual(literal.count, 1)
    let injection = try await index.search("\" OR 1=1 --")
    XCTAssertTrue(injection.isEmpty)
  }

  func testInvalidMediaAndBoundaryAreRejectedAtomically() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let invalid = GlobalSearchResult(
      categoryID: source.id, categoryName: source.name,
      categoryRoot: source.url,
      entry: DirectoryEntry(
        name: "Movie.mkv",
        url: URL(string: "https://other.example/Movie.mkv")!, kind: .file),
      relativePath: "Movie.mkv", artworkURL: nil)
    do {
      try await index.upsert([video(source, name: "Movie.mkv"), invalid], generation: UUID())
      XCTFail("Cross-origin record accepted")
    } catch {}
    let results = try await index.search("Movie")
    XCTAssertTrue(results.isEmpty)
    do {
      try await index.upsert([video(source, name: "poster.jpg")], generation: UUID())
      XCTFail("Image indexed")
    } catch {}
  }

  func testDailyRefreshThresholdAndSourceChanges() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let now = Date(timeIntervalSince1970: 1_000_000)
    let initiallyDue = try await index.needsRefresh([source], now: now)
    XCTAssertTrue(initiallyDue)
    try await index.completeSource(source, generation: UUID(), now: now)
    let fresh = try await index.needsRefresh([source], now: now.addingTimeInterval(86399))
    let due = try await index.needsRefresh([source], now: now.addingTimeInterval(86400))
    XCTAssertFalse(fresh)
    XCTAssertTrue(due)
    let changed = GlobalSearchRoot(
      id: source.id, name: source.name, url: URL(string: "https://media.example/New/")!)
    let changedDue = try await index.needsRefresh([changed], now: now)
    XCTAssertTrue(changedDue)
  }

  func testFailedScanKeepsRecordsAndSuccessfulScanPrunesMissingFiles() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    try await index.upsert([video(source, name: "Old.Movie.mkv")], generation: UUID())
    let failing = GlobalSearchService { _, _ in throw DirectoryError.badResponse(503) }
    let failure = try await LibraryIndexer(scanner: failing).refresh(
      roots: [source], index: index,
      generation: UUID(), update: { _ in })
    XCTAssertEqual(failure.failures.count, 1)
    let retained = try await index.search("Old")
    XCTAssertEqual(retained.count, 1)
    let successful = GlobalSearchService { url, _ in
      DirectoryListing(url: url, entries: [], artworkURL: nil)
    }
    _ = try await LibraryIndexer(scanner: successful).refresh(
      roots: [source], index: index,
      generation: UUID(), update: { _ in })
    let removed = try await index.search("Old")
    XCTAssertTrue(removed.isEmpty)
  }

  func testRecursiveIndexerStoresAllVideosNotJustQueryMatches() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let child = source.url.appending(path: "Folder/")
    let expected = video(source, name: "Nested.Movie.mkv", folder: "Folder")
    let scanner = GlobalSearchService { requested, _ in
      if requested == source.url {
        return DirectoryListing(
          url: requested,
          entries: [
            DirectoryEntry(name: "Folder", url: child, kind: .folder),
            DirectoryEntry(
              name: "Another.mp4", url: source.url.appending(path: "Another.mp4"), kind: .file),
          ], artworkURL: nil)
      }
      return DirectoryListing(
        url: requested,
        entries: [
          expected.entry,
          DirectoryEntry(name: "poster.jpg", url: expected.artworkURL!, kind: .file),
        ], artworkURL: expected.artworkURL)
    }
    _ = try await LibraryIndexer(scanner: scanner).refresh(
      roots: [source], index: index,
      generation: UUID(), update: { _ in })
    let nested = try await index.search("Nested")
    XCTAssertEqual(nested, [expected])
    let another = try await index.search("Another")
    XCTAssertEqual(another.count, 1)
  }

  func testCancelledWriteDoesNotDeleteExistingRows() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    try await index.upsert([video(source, name: "Old.Movie.mkv")], generation: UUID())
    let task = Task {
      withUnsafeCurrentTask { $0?.cancel() }
      try await index.completeSource(source, generation: UUID())
    }
    do {
      try await task.value
      XCTFail("Cancellation was ignored")
    } catch is CancellationError {} catch { throw error }
    let retained = try await index.search("Old")
    XCTAssertEqual(retained.count, 1)
  }

  func testCategoryDeletionOnlyRemovesItsCache() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let first = root()
    let second = root("Series")
    try await index.upsert(
      [video(first, name: "Movie1.mkv"), video(second, name: "Movie2.mkv")], generation: UUID())
    try await index.synchronizeSources([second])
    let results = try await index.search("Movie")
    XCTAssertEqual(results.map(\.categoryID), [second.id])
  }

  func testMetadataCacheExpiryAndFilenameParsing() async throws {
    let identity = MediaIdentity.parse(filename: "The.Matrix.1999.1080p.BluRay.x264.mkv")
    XCTAssertEqual(identity.title, "The Matrix")
    XCTAssertEqual(identity.year, "1999")
    let episode = MediaIdentity.parse(filename: "Some.Show.S02E03.1080p.mkv")
    XCTAssertEqual(episode.title, "Some Show")
    XCTAssertEqual(episode.season, "2")
    XCTAssertEqual(episode.episode, "3")
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    try await index.saveMetadata(key: identity.cacheKey, payload: "{}")
    let cached = try await index.cachedMetadata(key: identity.cacheKey)
    let expired = try await index.cachedMetadata(
      key: identity.cacheKey, now: .now.addingTimeInterval(8 * 86400))
    XCTAssertEqual(cached, "{}")
    XCTAssertNil(expired)
  }

  func testPaginationDoesNotLoseMatches() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    var videos: [GlobalSearchResult] = []
    for number in 0..<501 {
      videos.append(video(source, name: String(format: "Movie%04d.mkv", number)))
    }
    try await index.upsert(videos, generation: UUID())
    let first = try await index.search("Movie")
    let second = try await index.search("Movie", offset: first.count)
    XCTAssertEqual(first.count, 500)
    XCTAssertEqual(second.count, 1)
    XCTAssertEqual(Set(first + second), Set(videos))
  }
  func testStreamingThirtyThousandVideosKeepsBatchesBounded() async throws {
    try await streamingFixture(count: 30_000)
  }

  func testStreamingHundredThousandVideosKeepsBatchesBounded() async throws {
    try await streamingFixture(count: 100_000)
  }

  private actor StreamingRecorder {
    var count = 0
    var batches = 0
    var maximum = 0
    func record(_ batch: [GlobalSearchResult]) {
      count += batch.count
      batches += 1
      maximum = max(maximum, batch.count)
    }
    func values() -> [Int] { [count, batches, maximum] }
  }

  private func streamingFixture(count: Int) async throws {
    let source = root()
    let scanner = GlobalSearchService(maximumConcurrentFolders: 2) { url, _ in
      let entries: [DirectoryEntry]
      if url == source.url {
        entries = (0..<(count / 100)).map { number in
          DirectoryEntry(
            name: "Folder\(number)", url: url.appending(path: "Folder\(number)/"), kind: .folder)
        }
      } else {
        entries = (0..<100).map { number in
          DirectoryEntry(
            name: "Movie.\(number).mkv", url: url.appending(path: "Movie.\(number).mkv"),
            kind: .file)
        }
      }
      return DirectoryListing(url: url, entries: entries, artworkURL: nil)
    }
    let recorder = StreamingRecorder()
    let started = Date.now
    let snapshot = try await scanner.search(
      query: "", roots: [source], matchAllVideos: true,
      batchSink: { batch in await recorder.record(batch) },
      update: { snapshot in
        XCTAssertTrue(snapshot.results.isEmpty, "Progress must not carry the accumulated inventory")
      })
    XCTAssertEqual(snapshot.progress.matchesFound, count)
    XCTAssertTrue(snapshot.results.isEmpty)
    let metrics = await recorder.values()
    XCTAssertEqual(metrics, [count, count / 250, 250])
    print(
      "Streaming fixture: \(count) videos, \(metrics[1]) batches, maximum \(metrics[2]), \(Date.now.timeIntervalSince(started)) seconds"
    )
  }

  func testStreamingWriteFailureRetainsPreviouslyIndexedFiles() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    try await index.upsert([video(source, name: "Old.Movie.mkv")], generation: UUID())
    let scanner = GlobalSearchService { requested, _ in
      DirectoryListing(
        url: requested,
        entries: [
          DirectoryEntry(
            name: "Outside.mkv", url: URL(string: "https://other.example/Outside.mkv")!, kind: .file
          )
        ], artworkURL: nil)
    }
    do {
      _ = try await LibraryIndexer(scanner: scanner).refresh(
        roots: [source], index: index, generation: UUID(), update: { _ in })
      XCTFail("Unsafe write must fail the scan before pruning")
    } catch {}
    let retained = try await index.search("Old")
    XCTAssertEqual(retained.count, 1)
  }

  func testBulkCatalogueMetadataQueriesScaleByBatchAndPreserveMetadata() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let videos = (0..<1000).map { video(source, name: "UniqueMovie\($0).2025.mkv") }
    try await index.upsert(videos, generation: UUID())
    let worker = EntertainmentCatalogueWorker()
    let prepared = try await worker.prepare(index: index, personal: .init())
    XCTAssertEqual(prepared.titles.count, 1000)
    XCTAssertEqual(prepared.metadataQueryCount, 5)
    let first = try XCTUnwrap(prepared.titles.first)
    let metadata = EntertainmentMetadata(
      providerID: 1, title: "Matched", overview: "Fixture", posterURL: nil,
      rating: 8, voteCount: 100, releaseDate: "2025-01-01", genres: ["Drama"], language: "en")
    try await index.saveMetadata(
      key: first.metadataCacheKey(),
      payload: String(decoding: JSONEncoder().encode(metadata), as: UTF8.self))
    let updated = try await worker.prepare(index: index, personal: .init())
    XCTAssertEqual(updated.titles.first { $0.id == first.id }?.metadata, metadata)
    XCTAssertFalse(updated.enrichmentIDs.contains(first.id))
  }

  func testIndexingConcurrencyYieldsToPlaybackAndLowPower() {
    XCTAssertEqual(
      LibraryController.folderConcurrency(mode: .balanced, playing: false, lowPower: false), 2)
    XCTAssertEqual(
      LibraryController.folderConcurrency(mode: .lowImpact, playing: false, lowPower: false), 1)
    XCTAssertEqual(
      LibraryController.folderConcurrency(mode: .balanced, playing: true, lowPower: false), 1)
    XCTAssertEqual(
      LibraryController.folderConcurrency(mode: .balanced, playing: false, lowPower: true), 1)
  }

  func testIndexingMemoryBenchmark() async throws {
    guard let mode = ProcessInfo.processInfo.environment["MYRA_INDEX_BENCHMARK"] else {
      throw XCTSkip("Opt-in scanner memory benchmark")
    }
    let source = root()
    let scanner = GlobalSearchService(maximumConcurrentFolders: 2) { url, _ in
      let entries: [DirectoryEntry]
      if url == source.url {
        entries = (0..<1000).map {
          DirectoryEntry(
            name: "Folder\($0)", url: url.appending(path: "Folder\($0)/"), kind: .folder)
        }
      } else {
        entries = (0..<100).map {
          DirectoryEntry(
            name: "Movie\($0).mkv", url: url.appending(path: "Movie\($0).mkv"), kind: .file)
        }
      }
      return DirectoryListing(url: url, entries: entries, artworkURL: nil)
    }
    let started = Date.now
    let sink: (@Sendable ([GlobalSearchResult]) async throws -> Void)?
    if mode == "streamed" { sink = { _ in } } else { sink = nil }
    let snapshot = try await scanner.search(
      query: "", roots: [source], matchAllVideos: true, batchSink: sink, update: { _ in })
    XCTAssertEqual(snapshot.progress.matchesFound, 100_000)
    XCTAssertEqual(snapshot.results.count, mode == "streamed" ? 0 : 100_000)
    print(
      "Memory benchmark \(mode): 100000 videos in \(Date.now.timeIntervalSince(started)) seconds")
  }

  func testSelectedFolderPublicationKeepsOtherSourcesAndSiblings() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let other = root("Other")
    let keep = video(source, name: "Keep.Movie.mkv", folder: "Archive")
    let old = video(source, name: "Old.Movie.mkv", folder: "New")
    let unrelated = video(other, name: "Other.Movie.mkv")
    try await index.upsert([keep, old, unrelated], generation: UUID())
    let fresh = video(source, name: "Fresh.Movie.mkv", folder: "New")
    let scope = IndexScope(root: source, folder: source.url.appending(path: "New/"))
    let scanner = GlobalSearchService { requested, boundary in
      XCTAssertEqual(requested, scope.folder)
      XCTAssertEqual(boundary.root, source.url)
      return DirectoryListing(url: requested, entries: [fresh.entry], artworkURL: fresh.artworkURL)
    }
    _ = try await LibraryIndexer(scanner: scanner).refresh(
      roots: [source, other], index: index, generation: UUID(), scopes: [scope], update: { _ in })
    let inventory = try await index.inventory()
    XCTAssertEqual(Set(inventory.map { $0.media }), Set([keep, fresh, unrelated]))
  }

  func testStagingIsInvisibleDuringScanAndCancellationDiscardsIt() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let old = video(source, name: "Old.Movie.mkv")
    try await index.upsert([old], generation: UUID())
    let files = (0..<300).map { video(source, name: "New.Movie.\($0).mkv").entry }
    let scanner = GlobalSearchService { requested, _ in
      DirectoryListing(url: requested, entries: files, artworkURL: nil)
    }
    let task = Task {
      try await LibraryIndexer(scanner: scanner).refresh(
        roots: [source], index: index, generation: UUID()
      ) { snapshot in
        if snapshot.progress.matchesFound == 300 {
          let during = try! await index.inventory()
          XCTAssertEqual(during.map(\.media), [old], "Staged batches must be invisible")
          withUnsafeCurrentTask { $0?.cancel() }
        }
      }
    }
    do {
      _ = try await task.value
      XCTFail("Cancelled refresh published")
    } catch is CancellationError {} catch { XCTFail("Unexpected error: \(error)") }
    let after = try await index.inventory()
    XCTAssertEqual(after.map(\.media), [old])
  }

  func testUnchangedPromotionKeepsRevisionAndDiscoveryDate() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    let original = video(source, name: "Same.Movie.mkv")
    try await index.upsert([original], generation: UUID())
    let before = try await index.inventory()
    let revision = try await index.revision()
    let scanner = GlobalSearchService { requested, _ in
      DirectoryListing(url: requested, entries: [original.entry], artworkURL: original.artworkURL)
    }
    let indexer = LibraryIndexer(scanner: scanner)
    _ = try await indexer.refresh(
      roots: [source], index: index, generation: UUID(), update: { _ in })
    let after = try await index.inventory()
    let afterRevision = try await index.revision()
    let summary = await indexer.summary
    XCTAssertEqual(afterRevision, revision)
    XCTAssertEqual(after.first?.firstDiscovered, before.first?.firstDiscovered)
    XCTAssertEqual(summary.added + summary.changed + summary.removed, 0)
  }

  func testFailedSourcePublishesNoPartialChangesButSuccessfulSourceCompletes() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let bad = root("Bad")
    let good = root("Good")
    let old = video(bad, name: "Old.Movie.mkv")
    try await index.upsert([old], generation: UUID())
    let scanner = GlobalSearchService { requested, boundary in
      if requested == bad.url {
        return DirectoryListing(
          url: requested,
          entries: [
            DirectoryEntry(
              name: "New.Movie.mkv", url: requested.appending(path: "New.Movie.mkv"), kind: .file),
            DirectoryEntry(
              name: "Broken", url: requested.appending(path: "Broken/"), kind: .folder),
          ], artworkURL: nil)
      }
      if boundary.root == bad.url { throw DirectoryError.badResponse(503) }
      return DirectoryListing(
        url: requested,
        entries: [
          DirectoryEntry(
            name: "Good.Movie.mkv", url: requested.appending(path: "Good.Movie.mkv"), kind: .file)
        ], artworkURL: nil)
    }
    let snapshot = try await LibraryIndexer(scanner: scanner).refresh(
      roots: [bad, good], index: index, generation: UUID(), update: { _ in })
    let after = try await index.inventory()
    XCTAssertEqual(snapshot.failures.count, 1)
    XCTAssertEqual(after.filter { $0.media.categoryID == bad.id }.map(\.media), [old])
    XCTAssertEqual(after.filter { $0.media.categoryID == good.id }.count, 1)
  }

  func testFolderPoliciesInheritanceAndCompactionRespectPathBoundaries() {
    let source = root()
    let rootScope = IndexScope(root: source, folder: source.url)
    let archive = IndexScope(root: source, folder: source.url.appending(path: "Archive/"))
    let child = IndexScope(root: source, folder: source.url.appending(path: "Archive/Season/"))
    let sibling = IndexScope(root: source, folder: source.url.appending(path: "Archive2/"))
    var policies = IndexPolicies()
    policies.overrides[rootScope.id] = .weekly
    policies.overrides[archive.id] = .manual
    XCTAssertEqual(policies.schedule(for: child), .manual)
    XCTAssertEqual(policies.schedule(for: sibling), .weekly)
    XCTAssertEqual(Set(IndexScope.compact([archive, child, sibling])), Set([archive, sibling]))
    XCTAssertFalse(IndexSchedule.manual.isDue(nil))
    XCTAssertFalse(IndexSchedule.weekly.isDue(.now.addingTimeInterval(-86400)))
    XCTAssertTrue(IndexSchedule.daily.isDue(.now.addingTimeInterval(-86401)))
  }

  func testSavedHomeSnapshotAvoidsCatalogueRebuildAndInvalidatesOnNewContent() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    try await index.upsert([video(source, name: "Movie.2025.mkv")], generation: UUID())
    let cold = try await EntertainmentCatalogueWorker().prepare(index: index, personal: .init())
    let cached = try await EntertainmentCatalogueWorker().prepare(index: index, personal: .init())
    XCTAssertGreaterThan(cold.metadataQueryCount, 0)
    XCTAssertEqual(cached.metadataQueryCount, 0)
    XCTAssertEqual(cached.titles.map(\.id), cold.titles.map(\.id))
    try await index.upsert([video(source, name: "Another.2025.mkv")], generation: UUID())
    let updated = try await EntertainmentCatalogueWorker().prepare(index: index, personal: .init())
    XCTAssertEqual(updated.titles.count, 2)
    XCTAssertGreaterThan(updated.metadataQueryCount, 0)
  }

  func testConditionalRefreshFindsChildChangesUnderUnchangedParentAndSkipsManualBranch()
    async throws
  {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let source = root()
    let index = try LibraryIndex(url: url)
    let child = source.url.appending(path: "Series/")
    let archive = source.url.appending(path: "Archive/")
    IndexHTTPFixture.state.configure([
      source.url:
        "<a href='../'>Parent Directory</a><a href='Series/'>Series</a><a href='Archive/'>Archive</a>",
      child: "<a href='../'>Parent Directory</a><a href='Show.S01E01.mkv'>Show.S01E01.mkv</a>",
      archive: "<a href='../'>Parent Directory</a><a href='Old.Movie.mkv'>Old.Movie.mkv</a>",
    ])
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [IndexHTTPFixture.self]
    let session = URLSession(configuration: configuration)
    defer { session.invalidateAndCancel() }
    let service = DirectoryService(session: session)
    _ = try await LibraryIndexer(directoryService: service).refresh(
      roots: [source], index: index, generation: UUID(), full: true, update: { _ in })
    for folder in [source.url, child, archive] {
      let scope = IndexScope(root: source, folder: folder)
      let existing = try await index.folder(scope)
      var cached = try XCTUnwrap(existing)
      cached.checked = .now.addingTimeInterval(-86401)
      try await index.saveFolder(cached, scope: scope)
    }
    var policies = IndexPolicies()
    policies.overrides[IndexScope(root: source, folder: archive).id] = .manual
    IndexHTTPFixture.state.update(
      child,
      html:
        "<a href='../'>Parent Directory</a><a href='Show.S01E01.mkv'>Show.S01E01.mkv</a><a href='Show.S01E02.mkv'>Show.S01E02.mkv</a>"
    )
    let indexer = LibraryIndexer(directoryService: service)
    let result = try await indexer.refresh(
      roots: [source], index: index, generation: UUID(), policies: policies, automatic: true,
      update: { _ in })
    XCTAssertTrue(result.failures.isEmpty)
    let all = try await index.inventory()
    XCTAssertEqual(all.count, 3)
    XCTAssertTrue(all.contains { $0.media.entry.name == "Show.S01E02.mkv" })
    XCTAssertTrue(all.contains { $0.media.entry.name == "Old.Movie.mkv" })
    XCTAssertEqual(
      IndexHTTPFixture.state.calls(archive), 1, "Manual archive must not be fetched again")
    XCTAssertEqual(IndexHTTPFixture.state.notModified, 1, "The unchanged parent returns 304")
    let summary = await indexer.summary
    XCTAssertEqual(summary.added, 1)
    XCTAssertEqual(summary.unchanged, 1)
    // A parent refresh must also retain an excluded manual branch if its link disappears.
    IndexHTTPFixture.state.update(
      source.url, html: "<a href='../'>Parent Directory</a><a href='Series/'>Series</a>")
    _ = try await LibraryIndexer(directoryService: service).refresh(
      roots: [source], index: index, generation: UUID(), policies: policies, update: { _ in })
    let retainedManual = try await index.inventory()
    XCTAssertTrue(retainedManual.contains { $0.media.entry.name == "Old.Movie.mkv" })
    // Explicit selection must still refresh a manual-only folder.
    IndexHTTPFixture.state.update(archive, html: "<a href='../'>Parent Directory</a>")
    _ = try await LibraryIndexer(directoryService: service).refresh(
      roots: [source], index: index, generation: UUID(),
      scopes: [IndexScope(root: source, folder: archive)], policies: policies, update: { _ in })
    let afterManual = try await index.inventory()
    XCTAssertEqual(afterManual.count, 2)
    XCTAssertEqual(IndexHTTPFixture.state.calls(archive), 2)
  }

  func testIsolatedHomeCatalogueProfileWhenRequested() async throws {
    guard let path = ProcessInfo.processInfo.environment["MYRA_PROFILE_INDEX"],
      path.hasPrefix("/tmp/myra-profile-index/")
    else {
      throw XCTSkip("Opt-in Home profiling uses an isolated index copy")
    }
    let index = try LibraryIndex(url: URL(fileURLWithPath: path))
    let cachedOnly = ProcessInfo.processInfo.environment["MYRA_PROFILE_MODE"] == "cached"
    if !cachedOnly {
      try? FileManager.default.removeItem(at: index.url.appendingPathExtension("home-cache"))
    }
    let start = Date.now
    let result = try await EntertainmentCatalogueWorker().prepare(index: index, personal: .init())
    let seconds = Date.now.timeIntervalSince(start)
    if cachedOnly { XCTAssertEqual(result.metadataQueryCount, 0) }
    var usage = rusage()
    getrusage(0, &usage)
    print(
      "Home profile: \(result.titles.count) titles; \(cachedOnly ? "saved snapshot" : "cold preparation") \(seconds)s; peak test-process RSS \(usage.ru_maxrss) bytes"
    )
  }

  func testExistingIndexProvidesFolderSelectionBeforeFirstSnapshot() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    let index = try LibraryIndex(url: url)
    let source = root()
    try await index.upsert(
      [
        video(source, name: "Movie.mkv", folder: "Archive/2025"),
        video(source, name: "Other.mkv", folder: "New"),
      ], generation: UUID())
    let rows = try await index.folderRows(roots: [source])
    XCTAssertEqual(rows.count, 4)
    XCTAssertEqual(rows.first { $0.scope.folder == source.url }?.files, 2)
    XCTAssertEqual(rows.first { $0.scope.folder.lastPathComponent == "Archive" }?.files, 1)
    XCTAssertEqual(rows.first { $0.scope.folder.lastPathComponent == "2025" }?.files, 1)
    XCTAssertNotNil(rows.first { $0.scope.folder.lastPathComponent == "New" })
  }

  func testV2MigrationBacksUpAndPreservesExistingContent() async throws {
    let url = databaseURL()
    defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
    try FileManager.default.createDirectory(
      at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    var db: OpaquePointer?
    XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
    let source = root()
    let movie = video(source, name: "Legacy.Movie.mkv")
    let sql = """
      CREATE TABLE videos(category TEXT NOT NULL,root TEXT NOT NULL,category_name TEXT NOT NULL,url TEXT NOT NULL,name TEXT NOT NULL,search_name TEXT NOT NULL,relative_path TEXT NOT NULL,artwork TEXT,size INTEGER,modified REAL,generation TEXT NOT NULL,first_discovered REAL NOT NULL,PRIMARY KEY(category,url));
      CREATE TABLE source_scans(category TEXT PRIMARY KEY,root TEXT NOT NULL,completed REAL NOT NULL);
      CREATE TABLE metadata(cache_key TEXT PRIMARY KEY,payload TEXT NOT NULL,fetched REAL NOT NULL);
      CREATE TABLE playback(url TEXT PRIMARY KEY,seconds REAL NOT NULL,duration REAL NOT NULL,updated REAL NOT NULL);
      INSERT INTO videos VALUES('\(source.id)','\(source.url)','Movies','\(movie.entry.url)','Legacy.Movie.mkv','legacy.movie.mkv','Legacy.Movie.mkv',NULL,12345,100,'old',42);
      INSERT INTO playback VALUES('\(movie.entry.url)',600,3600,100);
      PRAGMA user_version=2;
      """
    XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
    sqlite3_close(db)
    let index = try LibraryIndex(url: url)
    XCTAssertTrue(
      FileManager.default.fileExists(atPath: url.appendingPathExtension("v2-backup").path))
    let inventory = try await index.inventory()
    XCTAssertEqual(inventory.count, 1)
    XCTAssertEqual(inventory.first?.firstDiscovered, Date(timeIntervalSince1970: 42))
    let seconds = try await index.playbackPosition(url: movie.entry.url)
    XCTAssertEqual(seconds, 600)
  }

}

private final class IndexHTTPState: @unchecked Sendable {
  private let lock = NSLock()
  private var pages: [URL: (String, Int)] = [:]
  private var requests: [URL: Int] = [:]
  private var unchangedCount = 0
  func configure(_ values: [URL: String]) {
    lock.lock()
    defer { lock.unlock() }
    pages = values.mapValues { ($0, 1) }
    requests = [:]
    unchangedCount = 0
  }
  func update(_ url: URL, html: String) {
    lock.lock()
    defer { lock.unlock() }
    pages[url] = (html, (pages[url]?.1 ?? 0) + 1)
  }
  func response(_ request: URLRequest) -> (Int, [String: String], Data) {
    lock.lock()
    defer { lock.unlock() }
    guard let url = request.url, let page = pages[url] else { return (503, [:], Data()) }
    requests[url, default: 0] += 1
    let tag = "\"version-\(page.1)\""
    if request.value(forHTTPHeaderField: "If-None-Match") == tag {
      unchangedCount += 1
      return (304, ["ETag": tag], Data())
    }
    return (200, ["ETag": tag, "Content-Type": "text/html"], Data(page.0.utf8))
  }
  func calls(_ url: URL) -> Int {
    lock.lock()
    defer { lock.unlock() }
    return requests[url] ?? 0
  }
  var notModified: Int {
    lock.lock()
    defer { lock.unlock() }
    return unchangedCount
  }
}
private final class IndexHTTPFixture: URLProtocol, @unchecked Sendable {
  static let state = IndexHTTPState()
  override class func canInit(with request: URLRequest) -> Bool { true }
  override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
  override func startLoading() {
    let (status, headers, data) = Self.state.response(request)
    let response = HTTPURLResponse(
      url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
    client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    client?.urlProtocol(self, didLoad: data)
    client?.urlProtocolDidFinishLoading(self)
  }
  override func stopLoading() {}
}
