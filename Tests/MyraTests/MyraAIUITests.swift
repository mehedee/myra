import AppKit
import SwiftUI
import XCTest

@testable import Myra

private actor MyraUIFixtureTransport: MyraAITransport {
  private(set) var generations = 0

  func generate(
    provider: MyraAIProvider, model: String, key: String, instructions: String,
    prompt: String, maximumTokens: Int, language: String
  ) async throws -> String {
    generations += 1
    return """
      {"summary":"A grounded fixture summary of Arrival.","recommendations":[{"titleID":"t1","reason":"A thoughtful science-fiction story from your indexed library."}],"actions":[],"tags":["thoughtful"]}
      """
  }

  func models(provider: MyraAIProvider, key: String) async throws -> [String] { ["fixture-model"] }
}

final class MyraAIUITests: XCTestCase {
  @MainActor
  func testNativeWorkspaceRunsSelectedTitleWithoutCloudAndRendersResult() async throws {
    _ = NSApplication.shared
    let fixture = try await makeFixture()
    defer { try? FileManager.default.removeItem(at: fixture.directory) }
    let suite = "Myra-AI-UI-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let transport = MyraUIFixtureTransport()
    let ai = MyraAIStore(defaults: defaults, transport: transport)
    let view = MyraAIWorkspaceView(
      store: fixture.store, initialFeature: .summary, initialTitle: fixture.title, ai: ai,
      play: { _ in XCTFail("Rendering and requesting a summary must not start playback") })
    let window = host(view, width: 1040, height: 740)
    defer { window.orderOut(nil) }
    try await Task.sleep(for: .milliseconds(150))
    let event = try XCTUnwrap(
      NSEvent.keyEvent(
        with: .keyDown, location: .zero, modifierFlags: [.command], timestamp: 0,
        windowNumber: window.windowNumber, context: nil, characters: "\r",
        charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36))
    XCTAssertTrue(window.performKeyEquivalent(with: event), "Command-Return invokes Ask Myra")
    for _ in 0..<40 {
      if ai.response != nil || ai.errorMessage != nil { break }
      try await Task.sleep(for: .milliseconds(25))
    }
    XCTAssertNil(ai.errorMessage)
    XCTAssertEqual(ai.response?.recommendations.first?.titleID, fixture.title.id)
    let generations = await transport.generations
    XCTAssertEqual(generations, 1)
    XCTAssertEqual(ai.requestsToday, 1)
    XCTAssertFalse(ai.settings.cloudConsent)
    try await capture(window, name: "myra-ai-workspace-result.png")
  }

  @MainActor
  func testDisabledWorkspaceAndSettingsRenderWithoutModelRequests() async throws {
    _ = NSApplication.shared
    let fixture = try await makeFixture()
    defer { try? FileManager.default.removeItem(at: fixture.directory) }
    let suite = "Myra-AI-disabled-UI-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let transport = MyraUIFixtureTransport()
    let ai = MyraAIStore(defaults: defaults, transport: transport)
    ai.settings.provider = .disabled
    let workspace = host(
      MyraAIWorkspaceView(
        store: fixture.store, ai: ai, play: { _ in XCTFail("No playback expected") }),
      width: 1040, height: 740)
    defer { workspace.orderOut(nil) }
    try await capture(workspace, name: "myra-ai-disabled.png")
    ai.settings.provider = .openAI
    let settings = host(MyraAISettingsWindow(ai: ai), width: 720, height: 900)
    defer { settings.orderOut(nil) }
    try await capture(settings, name: "myra-ai-cloud-settings.png")
    let generations = await transport.generations
    XCTAssertEqual(generations, 0)
    XCTAssertEqual(ai.requestsToday, 0)
    XCTAssertFalse(ai.settings.cloudConsent)
    XCTAssertFalse(ai.settings.shareHistory)
    XCTAssertFalse(ai.settings.shareSubtitles)
  }

  @MainActor
  func testEpisodeRecommendationDoesNotResumeAnEarlierEpisode() {
    let root = URL(string: "https://media.example/Series/")!
    let category = UUID()
    let episodes = [1, 2].map { episode in
      let name = "Example.S01E0\(episode).mkv"
      return EntertainmentVersion(
        media: GlobalSearchResult(
          categoryID: category, categoryName: "Fixture", categoryRoot: root,
          entry: DirectoryEntry(name: name, url: root.appending(path: name), kind: .file),
          relativePath: name, artworkURL: nil),
        firstDiscovered: .now, progressSeconds: episode == 1 ? 300 : 0, duration: 1800,
        lastPlayed: episode == 1 ? .now : nil, season: 1, episode: episode)
    }
    let title = EntertainmentTitle(
      id: "series|example|", name: "Example", year: nil, kind: .series, versions: episodes,
      metadata: nil)
    XCTAssertEqual(title.resumeVersion?.episode, 1)
    let selected = MyraAIWorkspaceView.playbackTitle(title, versionID: episodes[1].id)
    XCTAssertEqual(selected.versions.count, 1)
    XCTAssertEqual(selected.versions.first?.episode, 2)
    XCTAssertNil(
      selected.resumeVersion,
      "A suggested next episode must not jump back to the prior resume record")
  }

  @MainActor
  private func makeFixture() async throws -> (
    directory: URL, store: EntertainmentStore, title: EntertainmentTitle
  ) {
    let directory = FileManager.default.temporaryDirectory.appending(path: "Myra-AI-UI-\(UUID())")
    let index = try LibraryIndex(url: directory.appending(path: "Index.sqlite"))
    let store = EntertainmentStore(
      library: LibraryController(index: index),
      storageURL: directory.appending(path: "Personal.json"),
      automaticEnrichment: false)
    let root = URL(string: "https://media.example/Movies/")!
    let result = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Fixture", categoryRoot: root,
      entry: DirectoryEntry(
        name: "Arrival.2016.1080p.mkv", url: root.appending(path: "Arrival.2016.1080p.mkv"),
        kind: .file),
      relativePath: "Arrival.2016.1080p.mkv", artworkURL: nil)
    try await index.upsert([result], generation: UUID())
    await store.reload()
    let title = try XCTUnwrap(store.catalogue.first)
    return (directory, store, title)
  }

  @MainActor
  private func host<V: View>(_ view: V, width: CGFloat, height: CGFloat) -> NSWindow {
    let window = NSWindow(
      contentRect: NSRect(x: 100, y: 100, width: width, height: height),
      styleMask: [.titled], backing: .buffered, defer: false)
    window.appearance = NSAppearance(named: .darkAqua)
    window.contentView = NSHostingView(
      rootView: view.frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor)).environment(\.colorScheme, .dark))
    window.makeKeyAndOrderFront(nil)
    return window
  }

  @MainActor
  private func capture(_ window: NSWindow, name: String) async throws {
    try await Task.sleep(for: .milliseconds(150))
    let view = try XCTUnwrap(window.contentView)
    view.layoutSubtreeIfNeeded()
    window.displayIfNeeded()
    let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
    view.cacheDisplay(in: view.bounds, to: bitmap)
    let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    XCTAssertGreaterThan(png.count, 5000)
    if let directory = ProcessInfo.processInfo.environment["MYRA_UI_SNAPSHOT_DIR"] {
      try png.write(to: URL(fileURLWithPath: directory).appending(path: name))
    }
  }
}
