import SwiftData
import XCTest

@testable import Myra

final class DownloadAndAria2Tests: XCTestCase {
  @MainActor
  func testCategoriesPersistWhenContainerIsReopened() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-persistence-\(UUID().uuidString)", directoryHint: .isDirectory)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let storeURL = directory.appending(path: "Myra.store")
    let categoryID = UUID()

    do {
      let container = try AppPersistence.makeContainer(storeURL: storeURL)
      container.mainContext.insert(
        Category(
          id: categoryID,
          name: "Persistent Movies",
          rootURLString: "http://media.local/movies/"
        ))
      try container.mainContext.save()
    }

    let reopened = try AppPersistence.makeContainer(storeURL: storeURL)
    let categories = try reopened.mainContext.fetch(FetchDescriptor<Myra.Category>())
    XCTAssertEqual(categories.count, 1)
    XCTAssertEqual(categories.first?.id, categoryID)
    XCTAssertEqual(categories.first?.name, "Persistent Movies")
  }

  func testPersistentStoreUsesMyraApplicationSupportFolder() throws {
    let url = try AppPersistence.persistentStoreURL()
    XCTAssertEqual(url.lastPathComponent, "Myra.store")
    XCTAssertEqual(url.deletingLastPathComponent().lastPathComponent, "com.mehedee.Myra")
  }

  func testThemeCycleReturnsToSystem() {
    XCTAssertEqual(AppThemeMode.system.next, .light)
    XCTAssertEqual(AppThemeMode.light.next, .dark)
    XCTAssertEqual(AppThemeMode.dark.next, .system)
  }

  func testDestinationStaysUnderRoot() throws {
    let root = URL(fileURLWithPath: "/tmp/Myra-tests", isDirectory: true)
    let safe = try DestinationSafety.destination(root: root, relativePath: "Movies/Film.mkv")
    XCTAssertEqual(safe.path, "/tmp/Myra-tests/Movies/Film.mkv")
    XCTAssertThrowsError(
      try DestinationSafety.destination(root: root, relativePath: "../escape.mkv"))
  }

  func testFilenameSanitization() {
    XCTAssertEqual(DestinationSafety.sanitize("A/B: C.mkv"), "A_B_ C.mkv")
    XCTAssertEqual(DestinationSafety.sanitize(".."), "Untitled")
  }

  func testGlobalDownloadsAreGroupedByCategoryAndKeepRelativePaths() {
    let firstID = UUID()
    let secondID = UUID()
    let firstRoot = URL(string: "http://media.local/first/")!
    let secondRoot = URL(string: "http://media.local/second/")!
    let results = [
      GlobalSearchResult(
        categoryID: secondID, categoryName: "Shows", categoryRoot: secondRoot,
        entry: DirectoryEntry(
          name: "Pilot.mp4", url: secondRoot.appending(path: "Season 1/Pilot.mp4"), kind: .file),
        relativePath: "Season 1/Pilot.mp4", artworkURL: nil),
      GlobalSearchResult(
        categoryID: firstID, categoryName: "Movies: HD", categoryRoot: firstRoot,
        entry: DirectoryEntry(
          name: "Film.mkv", url: firstRoot.appending(path: "2026/Film.mkv"), kind: .file),
        relativePath: "2026/Film.mkv", artworkURL: nil),
    ]

    let groups = GlobalDownloadManifestBuilder.groups(for: results)

    XCTAssertEqual(groups.map(\.categoryName), ["Movies: HD", "Shows"])
    XCTAssertEqual(groups[0].manifest.map(\.relativePath), ["Movies_ HD/2026/Film.mkv"])
    XCTAssertEqual(groups[1].manifest.map(\.relativePath), ["Shows/Season 1/Pilot.mp4"])
  }

  @MainActor
  func testFindsInstalledAria2() throws {
    let url = try Aria2Controller.discoverExecutable(override: "")
    XCTAssertTrue(FileManager.default.isExecutableFile(atPath: url.path))
  }

  @MainActor
  func testAria2RPCStartsAndStops() async throws {
    let settings = AppSettings()
    let controller = Aria2Controller()
    try await controller.start(settings: AppSettingsSnapshot(settings))
    XCTAssertTrue(controller.isRunning)
    await controller.shutdown()
    XCTAssertFalse(controller.isRunning)
  }
}
