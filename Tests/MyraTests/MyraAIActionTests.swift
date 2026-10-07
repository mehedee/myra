import XCTest

@testable import Myra

/// These tests exercise reviewed actions only. Any accidental inference request fails locally.
private actor MyraActionFixtureTransport: MyraAITransport {
  func generate(
    provider: MyraAIProvider, model: String, key: String, instructions: String,
    prompt: String, maximumTokens: Int, language: String
  ) async throws -> String {
    throw MyraAIError.unavailable("Action tests must not request a model")
  }

  func models(provider: MyraAIProvider, key: String) async throws -> [String] {
    throw MyraAIError.unavailable("Action tests must not contact providers")
  }
}

final class MyraAIActionTests: XCTestCase {
  @MainActor
  func testReviewedWatchlistAndCollectionActionsPersistAndUndoIndependently() async throws {
    let fixture = try await makeFixture()
    defer { fixture.clean() }
    let first = fixture.titles[0]
    let second = fixture.titles[1]
    fixture.store.toggleWatchlist(second.id)

    try fixture.ai.apply(
      .init(kind: .addWatchlist, titleIDs: [first.id], value: ""), to: fixture.store)
    XCTAssertEqual(fixture.store.personal.watchlist, [first.id, second.id])
    XCTAssertEqual(try fixture.savedPersonal().watchlist, [first.id, second.id])
    XCTAssertTrue(fixture.ai.canUndo)
    try fixture.ai.undo(in: fixture.store)
    XCTAssertEqual(fixture.store.personal.watchlist, [second.id])
    XCTAssertEqual(try fixture.savedPersonal().watchlist, [second.id])
    XCTAssertFalse(fixture.ai.canUndo)

    try fixture.ai.apply(
      .init(kind: .collection, titleIDs: [first.id, second.id], value: "Thoughtful Weekend"),
      to: fixture.store)
    let collection = try XCTUnwrap(fixture.store.personal.collections.first)
    XCTAssertEqual(collection.name, "Thoughtful Weekend")
    XCTAssertEqual(collection.titleIDs, [first.id, second.id])
    XCTAssertEqual(try fixture.savedPersonal().collections.first?.titleIDs, [first.id, second.id])
    try fixture.ai.undo(in: fixture.store)
    XCTAssertTrue(fixture.store.personal.collections.isEmpty)
    XCTAssertTrue(try fixture.savedPersonal().collections.isEmpty)
    XCTAssertEqual(fixture.store.personal.watchlist, [second.id])
  }

  @MainActor
  func testCorrectionUndoRestoresGroupingAfterReloadAndPreservesHistoryAndCollections() async throws
  {
    let fixture = try await makeFixture()
    defer { fixture.clean() }
    let original = try XCTUnwrap(fixture.titles.first(where: { $0.name == "Arrival" }))
    XCTAssertEqual(original.versions.count, 2)
    fixture.store.toggleWatchlist(original.id)
    fixture.store.toggleWatched(original.id)
    fixture.store.createCollection(name: "Weekend")
    let collectionID = try XCTUnwrap(fixture.store.personal.collections.first?.id)
    fixture.store.toggleCollection(titleID: original.id, collectionID: collectionID)
    let media = try XCTUnwrap(original.versions.first?.media)
    fixture.store.recordPlayback(media: media, seconds: 300, duration: 7200)
    let initialHistory = fixture.store.personal.history[media.entry.url.absoluteString]

    try fixture.ai.apply(
      .init(
        kind: .correction, titleIDs: [original.id], value: " The Arrival ", year: nil,
        mediaKind: .movie),
      to: fixture.store)
    await fixture.store.reload()
    let corrected = try XCTUnwrap(
      fixture.store.catalogue.first(where: { $0.name == "The Arrival" }))
    XCTAssertNotEqual(corrected.id, original.id)
    XCTAssertEqual(corrected.versions.count, 2)
    XCTAssertTrue(fixture.store.personal.watchlist.contains(corrected.id))
    XCTAssertFalse(fixture.store.personal.watchlist.contains(original.id))
    XCTAssertTrue(fixture.store.personal.watched.contains(corrected.id))
    XCTAssertEqual(fixture.store.personal.collections.first?.titleIDs, [corrected.id])
    XCTAssertEqual(fixture.store.personal.matchCorrections.count, 2)
    XCTAssertEqual(try fixture.savedPersonal().matchCorrections.count, 2)

    try fixture.ai.undo(in: fixture.store)
    await fixture.store.reload()
    XCTAssertNotNil(fixture.store.catalogue.first(where: { $0.id == original.id }))
    XCTAssertNil(fixture.store.catalogue.first(where: { $0.id == corrected.id }))
    XCTAssertTrue(fixture.store.personal.matchCorrections.isEmpty)
    XCTAssertEqual(fixture.store.personal.watchlist, [original.id])
    XCTAssertEqual(fixture.store.personal.watched, [original.id])
    XCTAssertEqual(fixture.store.personal.collections.first?.titleIDs, [original.id])
    let history = try XCTUnwrap(fixture.store.personal.history[media.entry.url.absoluteString])
    XCTAssertEqual(history.seconds, initialHistory?.seconds)
    XCTAssertEqual(history.duration, initialHistory?.duration)
    XCTAssertEqual(history.updated, initialHistory?.updated)
    XCTAssertTrue(try fixture.savedPersonal().matchCorrections.isEmpty)
    XCTAssertEqual(try fixture.savedPersonal().watchlist, [original.id])
    XCTAssertEqual(
      Set(
        fixture.store.catalogue.first(where: { $0.id == original.id })!.versions.map {
          $0.media.entry.name
        }),
      Set(original.versions.map { $0.media.entry.name }),
      "Corrections never rename the source media files")
  }

  @MainActor
  func testUndoRejectsNewerPersonalEditsAndDifferentStore() async throws {
    let fixture = try await makeFixture()
    defer { fixture.clean() }
    let other = try await makeFixture()
    defer { other.clean() }
    let first = fixture.titles[0]
    let second = fixture.titles[1]
    try fixture.ai.apply(
      .init(kind: .addWatchlist, titleIDs: [first.id], value: ""), to: fixture.store)
    XCTAssertThrowsError(try fixture.ai.undo(in: other.store))
    XCTAssertTrue(fixture.store.personal.watchlist.contains(first.id))
    fixture.store.toggleWatched(second.id)
    XCTAssertThrowsError(try fixture.ai.undo(in: fixture.store))
    XCTAssertEqual(fixture.store.personal.watchlist, [first.id])
    XCTAssertEqual(fixture.store.personal.watched, [second.id])
    XCTAssertEqual(try fixture.savedPersonal().watched, [second.id])
  }

  @MainActor
  func testRejectedProposalDoesNotOverwritePreviousUndoSnapshot() async throws {
    let fixture = try await makeFixture()
    defer { fixture.clean() }
    let first = fixture.titles[0]
    try fixture.ai.apply(
      .init(kind: .addWatchlist, titleIDs: [first.id], value: ""), to: fixture.store)
    XCTAssertThrowsError(
      try fixture.ai.apply(.init(kind: .play, titleIDs: [first.id], value: ""), to: fixture.store))
    XCTAssertThrowsError(
      try fixture.ai.apply(
        .init(kind: .collection, titleIDs: ["missing-title"], value: "Invalid"), to: fixture.store))
    XCTAssertThrowsError(
      try fixture.ai.apply(
        .init(
          kind: .correction, titleIDs: [first.id], value: "Arrival", year: "invalid",
          mediaKind: .movie),
        to: fixture.store))
    XCTAssertTrue(fixture.ai.canUndo)
    XCTAssertEqual(fixture.store.personal.watchlist, [first.id])
    XCTAssertTrue(fixture.store.personal.collections.isEmpty)
    XCTAssertTrue(fixture.store.personal.matchCorrections.isEmpty)
    try fixture.ai.undo(in: fixture.store)
    XCTAssertTrue(fixture.store.personal.watchlist.isEmpty)
    XCTAssertTrue(try fixture.savedPersonal().watchlist.isEmpty)
  }

  @MainActor
  private struct Fixture {
    let directory: URL
    let suite: String
    let defaults: UserDefaults
    let store: EntertainmentStore
    let ai: MyraAIStore
    let titles: [EntertainmentTitle]

    func savedPersonal() throws -> EntertainmentPersonalData {
      try JSONDecoder().decode(
        EntertainmentPersonalData.self,
        from: Data(contentsOf: directory.appending(path: "Personal.json")))
    }

    func clean() {
      ai.cancel()
      defaults.removePersistentDomain(forName: suite)
      try? FileManager.default.removeItem(at: directory)
    }
  }

  @MainActor
  private func makeFixture() async throws -> Fixture {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-action-tests-\(UUID())")
    let suite = "Myra-action-tests-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    let index = try LibraryIndex(url: directory.appending(path: "Index.sqlite"))
    let library = LibraryController(index: index)
    let store = EntertainmentStore(
      library: library, storageURL: directory.appending(path: "Personal.json"),
      automaticEnrichment: false)
    let ai = MyraAIStore(defaults: defaults, transport: MyraActionFixtureTransport())
    ai.settings.provider = .disabled
    let root = URL(string: "https://media.example/Movies/")!
    let category = UUID()
    let results = ["Arrival.2016.1080p.mkv", "Arrival.2016.2160p.mkv", "Dune.2021.1080p.mkv"].map {
      name in
      GlobalSearchResult(
        categoryID: category, categoryName: "Fixture", categoryRoot: root,
        entry: DirectoryEntry(name: name, url: root.appending(path: name), kind: .file),
        relativePath: name, artworkURL: nil)
    }
    try await index.upsert(results, generation: UUID())
    await store.reload()
    XCTAssertEqual(store.catalogue.count, 2)
    return Fixture(
      directory: directory, suite: suite, defaults: defaults, store: store, ai: ai,
      titles: store.catalogue)
  }
}
