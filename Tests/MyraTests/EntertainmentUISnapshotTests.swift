import AppKit
import SwiftData
import SwiftUI
import XCTest

@testable import Myra

final class EntertainmentUISnapshotTests: XCTestCase {
  @MainActor
  func testHomeAndOfflineRenderWithGroupedTitlesAndMissingDownload() async throws {
    _ = NSApplication.shared
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-home-render-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let index = try LibraryIndex(url: directory.appending(path: "LibraryIndex.sqlite"))
    let library = LibraryController(index: index)
    let coordinator = AppCoordinator(
      library: library, personalStorageURL: directory.appending(path: "Personal.json"),
      automaticEnrichment: false)
    let container = try AppPersistence.makeContainer(inMemory: true)
    let batch = DownloadBatch(title: "Snapshot downloads", status: .completed)
    container.mainContext.insert(batch)
    let localFiles = [
      "Dune.2021.1080p.WEB-DL.mkv", "Dune.2021.2160p.BluRay.mkv", "Missing.Movie.2024.1080p.mkv",
    ]
    for (offset, filename) in localFiles.enumerated() {
      let local = directory.appending(path: filename)
      if offset < 2 { try Data("snapshot file".utf8).write(to: local) }
      let manifest = DownloadManifestItem(
        sourceURL: URL(string: "https://media.example/Movies/\(filename)")!, relativePath: filename,
        size: 13)
      let item = DownloadItem(batchID: batch.id, manifest: manifest, destinationPath: local.path)
      item.status = .completed
      item.completedBytes = 13
      container.mainContext.insert(item)
    }
    try container.mainContext.save()
    coordinator.configure(modelContext: container.mainContext)
    // Cancel startup refresh before seeding the fixture: no provider or directory requests are made.
    library.cancel()
    let source = GlobalSearchRoot(
      id: UUID(), name: "Personal Movies & Series",
      url: URL(string: "https://media.example/Movies/")!)
    let filenames = [
      "Dune.2021.1080p.WEB-DL.mkv", "Dune.2021.2160p.BluRay.mkv", "Arrival.2016.1080p.mkv",
      "Severance.S01E01.1080p.mkv", "Severance.S01E02.1080p.mkv",
      "Severance.S02E01.1080p.mkv", "Severance.S01E01.720p.mkv",
    ]
    let results = filenames.map { filename in
      GlobalSearchResult(
        categoryID: source.id, categoryName: source.name, categoryRoot: source.url,
        entry: DirectoryEntry(
          name: filename, url: source.url.appending(path: filename), kind: .file,
          size: 4_000_000_000),
        relativePath: filename, artworkURL: nil)
    }
    try await index.upsert(results, generation: UUID())
    await coordinator.entertainment.reload()
    XCTAssertEqual(coordinator.entertainment.catalogue.count, 3)
    let dune = try XCTUnwrap(coordinator.entertainment.catalogue.first { $0.name == "Dune" })
    XCTAssertEqual(dune.versions.count, 2)
    let catalogue = coordinator.entertainment.catalogue
    XCTAssertNil(EntertainmentPick.select(from: [], watched: [], excluding: nil))
    XCTAssertNil(
      EntertainmentPick.select(from: catalogue, watched: Set(catalogue.map(\.id)), excluding: nil))
    XCTAssertEqual(
      EntertainmentPick.select(from: [dune], watched: [], excluding: dune.id)?.id, dune.id)
    let alternate = try XCTUnwrap(catalogue.first { $0.id != dune.id })
    for _ in 0..<100 {
      XCTAssertEqual(
        EntertainmentPick.select(from: [dune, alternate], watched: [], excluding: dune.id)?.id,
        alternate.id)
      XCTAssertEqual(
        EntertainmentPick.select(from: [dune, alternate], watched: [alternate.id], excluding: nil)?
          .id,
        dune.id)
    }
    for title in coordinator.entertainment.catalogue {
      let metadata = EntertainmentMetadata(
        providerID: title.kind == .movie ? 438631 : 95396,
        title: title.name,
        overview: "A fixture synopsis used to verify readable discovery cards and details.",
        posterURL: nil, rating: 8.2, voteCount: 1000,
        releaseDate: title.kind == .movie ? "2021-09-15" : "2022-02-18",
        genres: ["Science Fiction", "Drama"], language: "en",
        episodeReleaseDates: ["1:1": "2022-02-18", "1:2": "2022-02-25"])
      let payload = try XCTUnwrap(String(data: JSONEncoder().encode(metadata), encoding: .utf8))
      try await index.saveMetadata(key: title.metadataCacheKey(), payload: payload)
    }
    coordinator.entertainment.recordPlayback(media: results[0], seconds: 1200, duration: 9000)
    coordinator.entertainment.toggleWatchlist(dune.id)
    coordinator.entertainment.createCollection(name: "Weekend Movies")
    await coordinator.entertainment.reload()
    XCTAssertNotNil(coordinator.entertainment.catalogue.first { $0.id == dune.id }?.resumeVersion)
    let enrichedPick = try XCTUnwrap(coordinator.entertainment.catalogue.first { $0.id == dune.id })
    try await renderHosted(
      HomeView(coordinator: coordinator, store: coordinator.entertainment, initialQuery: "Dune"),
      width: 1200, height: 1100, name: "home-search-matches.png")
    try await renderHosted(
      HomeView(
        coordinator: coordinator, store: coordinator.entertainment, initialQuery: "NoMatchingMovie"),
      width: 1200, height: 850, name: "home-search-empty.png")
    try await renderHosted(
      UpdateSettingsSection(updater: coordinator.updater), width: 620, height: 400,
      name: "update-settings.png")
    try await renderHosted(
      EntertainmentPickView(
        title: enrichedPick, store: coordinator.entertainment, filters: "all sources",
        anotherPick: {}, play: { _ in }),
      width: 900, height: 740, name: "pick-suggestion.png")
    try await renderHosted(
      EntertainmentPickView(
        title: nil, store: coordinator.entertainment, filters: "all sources",
        anotherPick: {}, play: { _ in }),
      width: 720, height: 560, name: "pick-empty.png")
    try await renderHosted(
      HomeView(coordinator: coordinator, store: coordinator.entertainment), width: 780,
      height: 1400, name: "home-compact.png")
    try await renderHosted(
      HomeView(coordinator: coordinator, store: coordinator.entertainment), width: 1200,
      height: 1100, name: "home-wide.png")
    try await renderHosted(
      EntertainmentVersionChooser(title: dune, store: coordinator.entertainment, choose: { _ in }),
      width: 750, height: 520, name: "movie-version-chooser.png")
    let series = try XCTUnwrap(coordinator.entertainment.catalogue.first { $0.kind == .series })
    try await renderHosted(
      EntertainmentVersionChooser(
        title: series, store: coordinator.entertainment, choose: { _ in }),
      width: 750, height: 520, name: "season-episode-chooser.png")
    try await renderHosted(
      RootView(coordinator: coordinator).modelContainer(container),
      width: 1100, height: 850, name: "home-native-toolbar.png")
    try await renderHosted(
      RootView(coordinator: coordinator).modelContainer(container), width: 1100,
      height: 850, name: "home-header-scrolled.png", scroll: true)
    try await renderHosted(
      OfflineLibraryView(coordinator: coordinator), width: 900, height: 640,
      name: "offline-library.png")
    try await renderHosted(
      EmbeddedPlayerView(player: coordinator.player), width: 1000, height: 650,
      name: "player-controls.png")
    try await renderHosted(
      HomeView(coordinator: coordinator, store: coordinator.entertainment), width: 780,
      height: 900, name: "home-light.png", light: true)
    coordinator.player.start(results[0], library: library, startPaused: true)
    try await renderHosted(
      RootView(coordinator: coordinator).modelContainer(container), width: 1100,
      height: 850, name: "player-native-toolbar.png")
    coordinator.player.stop()
    await coordinator.shutdown()
  }

  @MainActor
  func testIndexManagementRendersSchedulesAndSelectionControls() async throws {
    _ = NSApplication.shared
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-index-ui-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let index = try LibraryIndex(url: directory.appending(path: "Index.sqlite"))
    let library = LibraryController(index: index)
    let root = GlobalSearchRoot(
      id: UUID(), name: "Movies", url: URL(string: "https://media.example/Movies/")!)
    library.setRoots([root])
    let scope = IndexScope(root: root, folder: root.url)
    try await index.saveFolder(
      IndexedFolder(
        listing: DirectoryListing(url: root.url, entries: [], artworkURL: nil), checked: .now),
      scope: scope)
    library.setSchedule(.weekly, scope: scope)
    await library.loadFolderRows()
    XCTAssertEqual(library.folderRows.count, 1)
    try await renderHosted(
      IndexManagementView(library: library), width: 900, height: 620, name: "index-management.png")
  }

  @MainActor
  private func renderHosted<V: View>(
    _ view: V, width: CGFloat, height: CGFloat, name: String, light: Bool = false,
    scroll: Bool = false
  )
    async throws
  {
    let window = NSWindow(
      contentRect: NSRect(x: 100, y: 100, width: width, height: height),
      styleMask: [.titled], backing: .buffered, defer: false)
    window.appearance = NSAppearance(named: light ? .aqua : .darkAqua)
    let host = NSHostingView(
      rootView:
        view
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .environment(\.colorScheme, light ? .light : .dark))
    window.contentView = host
    window.makeKeyAndOrderFront(nil)
    defer { window.orderOut(nil) }
    try await Task.sleep(for: .milliseconds(200))
    host.layoutSubtreeIfNeeded()
    window.displayIfNeeded()
    if scroll {
      func homeScrollView(_ view: NSView) -> NSScrollView? {
        if let scroll = view as? NSScrollView, scroll.bounds.width > 500,
          (scroll.documentView?.bounds.height ?? 0) > scroll.bounds.height + 200
        {
          return scroll
        }
        for child in view.subviews {
          if let found = homeScrollView(child) { return found }
        }
        return nil
      }
      let scrolling = try XCTUnwrap(homeScrollView(host))
      let originalHeight = host.bounds.height
      scrolling.contentView.scroll(to: NSPoint(x: 0, y: 300))
      scrolling.reflectScrolledClipView(scrolling.contentView)
      try await Task.sleep(for: .milliseconds(200))
      host.layoutSubtreeIfNeeded()
      window.displayIfNeeded()
      XCTAssertGreaterThan(scrolling.contentView.bounds.origin.y, 200)
      XCTAssertEqual(host.bounds.height, originalHeight, accuracy: 1)
      XCTAssertTrue(window.toolbar?.isVisible == true, "The header stays fixed during scrolling")
    }
    XCTAssertEqual(host.bounds.width, width, accuracy: 1)
    // SwiftUI installs a native toolbar on RootView; its titlebar inset is part of the hosting bounds.
    if window.toolbar == nil {
      XCTAssertEqual(host.bounds.height, height, accuracy: 1)
    } else {
      XCTAssertGreaterThanOrEqual(host.bounds.height, height)
      XCTAssertLessThanOrEqual(host.bounds.height, height + 52)
      XCTAssertTrue(window.toolbar?.isVisible == true)
    }
    let capture: NSView = window.toolbar != nil ? (host.superview ?? host) : host
    let bitmap = try XCTUnwrap(capture.bitmapImageRepForCachingDisplay(in: capture.bounds))
    capture.cacheDisplay(in: capture.bounds, to: bitmap)
    XCTAssertGreaterThanOrEqual(bitmap.colorAt(x: 0, y: 0)?.alphaComponent ?? 0, 0.99)
    let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    XCTAssertGreaterThan(png.count, 5000)
    if let directory = ProcessInfo.processInfo.environment["MYRA_UI_SNAPSHOT_DIR"] {
      try png.write(to: URL(fileURLWithPath: directory).appending(path: name))
    }
  }

}
