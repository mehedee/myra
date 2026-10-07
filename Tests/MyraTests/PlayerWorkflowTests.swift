import AppKit
import Foundation
import SwiftUI
import VLCKit
import XCTest

@testable import Myra

private final class SubtitleFixtures: @unchecked Sendable {
  struct Reply: Sendable {
    let status: Int
    let data: Data
    let delay: Double
  }
  private let lock = NSLock()
  private var replies: [String: Reply] = [:]
  private var requests: [URLRequest] = []
  func reset(_ replies: [String: Reply]) {
    lock.lock()
    defer { lock.unlock() }
    self.replies = replies
    requests = []
  }
  func reply(_ request: URLRequest) -> Reply {
    lock.lock()
    defer { lock.unlock() }
    requests.append(request)
    return replies[request.url!.path] ?? Reply(status: 500, data: Data(), delay: 0)
  }
  func captured() -> [URLRequest] {
    lock.lock()
    defer { lock.unlock() }
    return requests
  }
}

private final class SubtitleFixtureProtocol: URLProtocol, @unchecked Sendable {
  static let fixtures = SubtitleFixtures()
  private let lock = NSLock()
  private var stopped = false
  override class func canInit(with request: URLRequest) -> Bool { true }
  override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
  override func startLoading() {
    let reply = Self.fixtures.reply(request)
    if reply.delay > 0 {
      DispatchQueue.global().asyncAfter(deadline: .now() + reply.delay) { [self] in send(reply) }
    } else {
      send(reply)
    }
  }
  private func send(_ reply: SubtitleFixtures.Reply) {
    lock.lock()
    defer { lock.unlock() }
    guard !stopped else { return }
    let response = HTTPURLResponse(
      url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1",
      headerFields: ["Content-Length": String(reply.data.count)])!
    client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    client?.urlProtocol(self, didLoad: reply.data)
    client?.urlProtocolDidFinishLoading(self)
  }
  override func stopLoading() {
    lock.lock()
    stopped = true
    lock.unlock()
  }
}

@MainActor
private final class FullscreenFixtureWindow: NSWindow {
  var fullscreen = false
  var toggles = 0
  var deferEntering = false
  override var styleMask: NSWindow.StyleMask {
    get {
      fullscreen ? super.styleMask.union(.fullScreen) : super.styleMask.subtracting(.fullScreen)
    }
    set { super.styleMask = newValue.subtracting(.fullScreen) }
  }
  override func toggleFullScreen(_ sender: Any?) {
    toggles += 1
    if fullscreen {
      NotificationCenter.default.post(name: NSWindow.willExitFullScreenNotification, object: self)
    } else {
      NotificationCenter.default.post(name: NSWindow.willEnterFullScreenNotification, object: self)
      if !deferEntering { finishEntering() }
    }
  }
  func finishEntering() {
    fullscreen = true
    NotificationCenter.default.post(name: NSWindow.didEnterFullScreenNotification, object: self)
  }
  func finishExit() {
    fullscreen = false
    NotificationCenter.default.post(name: NSWindow.didExitFullScreenNotification, object: self)
  }
}

final class PlayerWorkflowTests: XCTestCase {
  private var temporaryLibraries: [URL] = []

  override func tearDown() {
    for directory in temporaryLibraries { try? FileManager.default.removeItem(at: directory) }
    temporaryLibraries = []
    super.tearDown()
  }

  @MainActor private func library() throws -> LibraryController {
    let directory = URL(fileURLWithPath: "/private/tmp/Myra-player-library-\(UUID())")
    temporaryLibraries.append(directory)
    return LibraryController(
      index: try LibraryIndex(url: directory.appending(path: "index.sqlite")))
  }
  private func media(_ filename: String = "Episode2.mkv") -> GlobalSearchResult {
    let root = URL(string: "http://127.0.0.1:9/media/")!
    let entry = DirectoryEntry(
      name: filename, url: root.appending(path: "Series/\(filename)"), kind: .file)
    return GlobalSearchResult(
      categoryID: UUID(), categoryName: "TV", categoryRoot: root,
      entry: entry, relativePath: "Series/\(filename)", artworkURL: nil)
  }
  private func service(_ replies: [String: SubtitleFixtures.Reply]) -> OpenSubtitlesService {
    SubtitleFixtureProtocol.fixtures.reset(replies)
    let config = URLSessionConfiguration.ephemeral
    config.protocolClasses = [SubtitleFixtureProtocol.self]
    return OpenSubtitlesService(session: URLSession(configuration: config))
  }
  private func reply(_ text: String, status: Int = 200, delay: Double = 0) -> SubtitleFixtures.Reply
  {
    .init(status: status, data: Data(text.utf8), delay: delay)
  }
  private let searchJSON = """
    {"total_pages":2,"data":[{"attributes":{"language":"en","release":"Series S01E02 WEB-DL","fps":23.976,"download_count":42,"hearing_impaired":true,"from_trusted":true,"files":[{"file_id":24,"file_name":"../../episode.srt"},{"file_id":24,"file_name":"duplicate.srt"}]}}]}
    """
  private let subtitle = "1\n00:00:01,000 --> 00:00:02,000\nHello\n"
  private let credentials = SubtitleCredentials(apiKey: "fixture-key")

  func testClearingGlobalPreservesScopeAndPreviousFilter() {
    var state = LibrarySearchState()
    XCTAssertEqual(state.scope, .global)
    state.select(.current)
    state.updateQuery("Season 1")
    state.select(.global)
    state.updateQuery("Episode")
    XCTAssertTrue(state.showingGlobalResults)
    state.updateQuery("")
    XCTAssertEqual(state.scope, .global)
    XCTAssertEqual(state.currentQuery, "Season 1")
    XCTAssertFalse(state.showingGlobalResults)
    state.updateQuery("Next search")
    XCTAssertEqual(state.scope, .global)
    state.select(.current)
    XCTAssertEqual(state.query, "Season 1")
  }

  func testSequenceUsesNaturalOrderAndOnlySafeSiblingVideos() throws {
    let playing = media()
    let parent = playing.entry.url.deletingLastPathComponent()
    let entries =
      ["Episode10.mkv", "Episode1.mkv", "Episode3.mkv", "Episode2.mkv", "poster.jpg"].map {
        DirectoryEntry(name: $0, url: parent.appending(path: $0), kind: .file)
      } + [
        playing.entry,
        DirectoryEntry(
          name: "Episode4.mkv", url: parent.appending(path: "Other/Episode4.mkv"), kind: .file),
        DirectoryEntry(
          name: "outside.mkv", url: URL(string: "https://other.example/outside.mkv")!, kind: .file),
        DirectoryEntry(
          name: "folder.mkv", url: parent.appending(path: "folder.mkv"), kind: .folder),
      ]
    let queue = try PlaybackSequence(
      playing: playing, entries: entries, artworkURL: parent.appending(path: "poster.jpg"))
    XCTAssertEqual(
      queue.videos.map(\.entry.name),
      ["Episode1.mkv", "Episode2.mkv", "Episode3.mkv", "Episode10.mkv"])
    let next = try XCTUnwrap(queue.next(after: playing.entry.url))
    XCTAssertEqual(next.entry.name, "Episode3.mkv")
    XCTAssertEqual(next.categoryID, playing.categoryID)
    XCTAssertEqual(next.relativePath, "Series/Episode3.mkv")
    XCTAssertNotNil(next.artworkURL)
    XCTAssertNil(queue.next(after: queue.videos.last!.entry.url))
    let single = try PlaybackSequence(playing: playing, entries: [], artworkURL: nil)
    XCTAssertNil(single.next(after: playing.entry.url))
  }

  func testCompletionIsOncePerLoadAndPauseIsNotCompletion() {
    var completion = PlaybackCompletionState()
    XCTAssertFalse(completion.observe(.stopped))
    XCTAssertFalse(completion.observe(.opening))
    XCTAssertFalse(completion.observe(.playing))
    XCTAssertFalse(completion.observe(.paused))
    XCTAssertTrue(completion.observe(.ended))
    XCTAssertFalse(completion.observe(.ended))
    XCTAssertFalse(completion.observe(.stopped))
    completion.reset()
    XCTAssertFalse(completion.observe(.stopped))
    XCTAssertFalse(completion.observe(.playing))
    XCTAssertTrue(completion.observe(.stopped))
  }

  @MainActor
  func testStopReturnsToLibraryAndDetachedVideoKeepsWindow() throws {
    _ = NSApplication.shared
    let window = NSWindow(
      contentRect: NSRect(x: 0, y: 0, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.start(media(), library: try library())
    player.attach(view: VLCVideoView(frame: .zero))
    XCTAssertTrue(player.playbackWindow === window)
    player.stop()
    XCTAssertNil(player.media)
    XCTAssertNil(player.engine)
    player.setWindow(nil)
  }

  @MainActor
  func testBackWaitsForFullscreenExitAndExitButtonUsesActualWindow() throws {
    _ = NSApplication.shared
    let window = FullscreenFixtureWindow(
      contentRect: NSRect(x: 0, y: 0, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.start(media(), library: try library())
    player.toggleFullscreen()
    XCTAssertTrue(player.isFullscreen)
    player.toggleFullscreen()
    XCTAssertEqual(window.toggles, 2)
    window.finishExit()
    XCTAssertFalse(player.isFullscreen)
    XCTAssertNotNil(player.media)
    player.toggleFullscreen()
    player.close()
    XCTAssertNotNil(
      player.media, "The player must remain mounted during the fullscreen exit animation")
    window.finishExit()
    XCTAssertNil(player.media)
    XCTAssertFalse(player.isFullscreen)
    player.setWindow(nil)
  }

  @MainActor
  func testExitRequestedDuringFullscreenEntryIsNotLost() throws {
    _ = NSApplication.shared
    let window = FullscreenFixtureWindow(
      contentRect: NSRect(x: 0, y: 0, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    window.deferEntering = true
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.start(media(), library: try library())
    defer {
      player.close()
      player.setWindow(nil)
    }
    player.toggleFullscreen()
    player.exitFullscreen()
    XCTAssertEqual(window.toggles, 1)
    window.finishEntering()
    XCTAssertEqual(window.toggles, 2)
    window.finishExit()
    XCTAssertFalse(player.isFullscreen)
    XCTAssertNotNil(player.media)
  }

  @MainActor
  func testFullscreenChromeIsHiddenAndRestoredOnExit() throws {
    _ = NSApplication.shared
    let window = FullscreenFixtureWindow(
      contentRect: NSRect(x: 0, y: 0, width: 640, height: 360),
      styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
    window.toolbar = NSToolbar(identifier: "fixture.toolbar")
    window.toolbar?.isVisible = true
    window.titleVisibility = .visible
    window.titlebarAppearsTransparent = false
    window.titlebarSeparatorStyle = .line
    let originalBackground = window.backgroundColor
    let close = try XCTUnwrap(window.standardWindowButton(.closeButton))
    close.isHidden = false
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.start(media(), library: try library())
    defer {
      player.stop()
      player.setWindow(nil)
    }
    player.toggleFullscreen()
    XCTAssertTrue(window.styleMask.contains(.fullSizeContentView))
    XCTAssertEqual(window.titleVisibility, .hidden)
    XCTAssertTrue(window.titlebarAppearsTransparent)
    XCTAssertEqual(window.titlebarSeparatorStyle, .none)
    XCTAssertFalse(window.toolbar?.isVisible ?? true)
    XCTAssertTrue(close.isHidden)
    player.exitFullscreen()
    window.finishExit()
    XCTAssertFalse(window.styleMask.contains(.fullSizeContentView))
    XCTAssertEqual(window.titleVisibility, .visible)
    XCTAssertFalse(window.titlebarAppearsTransparent)
    XCTAssertEqual(window.titlebarSeparatorStyle, .line)
    XCTAssertTrue(window.toolbar?.isVisible ?? false)
    XCTAssertFalse(close.isHidden)
    XCTAssertEqual(window.backgroundColor, originalBackground)
  }

  @MainActor
  func testSeekbarTooltipsAreDisabledAndRestored() throws {
    _ = NSApplication.shared
    let slider = SeekSlider(frame: NSRect(x: 0, y: 0, width: 300, height: 22))
    slider.duration = 120
    let event = try XCTUnwrap(
      NSEvent.mouseEvent(
        with: .mouseMoved, location: NSPoint(x: 150, y: 11), modifierFlags: [], timestamp: 0,
        windowNumber: 0, context: nil, eventNumber: 0, clickCount: 0, pressure: 0))
    slider.mouseMoved(with: event)
    XCTAssertNotNil(slider.toolTip)
    slider.allowsTooltips = false
    XCTAssertNil(slider.toolTip, "Entering fullscreen must clear an existing hover hint")
    slider.mouseMoved(with: event)
    XCTAssertNil(slider.toolTip)
    slider.allowsTooltips = true
    slider.mouseMoved(with: event)
    XCTAssertNotNil(slider.toolTip)
  }

  @MainActor
  func testInformationOverlayDoesNotResizeBrowser() async throws {
    _ = NSApplication.shared
    let coordinator = AppCoordinator()
    let selected = media()
    coordinator.currentCategory = Category(
      name: "Synthetic TV", rootURLString: selected.categoryRoot.absoluteString)
    coordinator.currentURL = selected.categoryRoot
    coordinator.entries = [
      DirectoryEntry(
        name: "Series", url: selected.categoryRoot.appending(path: "Series/"), kind: .folder)
    ]
    let window = NSWindow(
      contentRect: NSRect(x: 100, y: 100, width: 1100, height: 700),
      styleMask: [.titled, .resizable], backing: .buffered, defer: false)
    let host = NSHostingView(rootView: LibraryPlayerContainer(coordinator: coordinator))
    window.contentView = host
    window.makeKeyAndOrderFront(nil)
    defer { window.orderOut(nil) }
    try await Task.sleep(for: .milliseconds(250))
    host.layoutSubtreeIfNeeded()
    var scrollViews: [NSScrollView] = []
    func collect(_ view: NSView) {
      if let scroll = view as? NSScrollView { scrollViews.append(scroll) }
      for child in view.subviews { collect(child) }
    }
    collect(host)
    let browser = try XCTUnwrap(scrollViews.max { $0.frame.width < $1.frame.width })
    let originalFrame = browser.convert(browser.bounds, to: host)
    XCTAssertGreaterThan(originalFrame.width, 500)
    // Assign directly so this layout test does not fetch metadata or touch the personal index.
    coordinator.inspector.identity = MediaIdentity(title: "Episode 2")
    coordinator.inspector.selected = selected
    try await Task.sleep(for: .milliseconds(350))
    host.layoutSubtreeIfNeeded()
    XCTAssertNotNil(browser.superview)
    XCTAssertEqual(browser.convert(browser.bounds, to: host), originalFrame)
    scrollViews = []
    collect(host)
    let information = try XCTUnwrap(
      scrollViews.first {
        $0 !== browser && abs($0.frame.width - 460) < 1
      })
    // Spring transitions can still be settling after the initial layout delay.
    let alignmentDeadline = Date.now.addingTimeInterval(2)
    while abs(information.convert(information.bounds, to: host).maxX - host.bounds.maxX) > 1,
      Date.now < alignmentDeadline
    {
      try await Task.sleep(for: .milliseconds(50))
      host.layoutSubtreeIfNeeded()
    }
    XCTAssertEqual(browser.convert(browser.bounds, to: host), originalFrame)
    XCTAssertEqual(
      information.convert(information.bounds, to: host).maxX, host.bounds.maxX, accuracy: 1)
    if let directory = ProcessInfo.processInfo.environment["MYRA_UI_SNAPSHOT_DIR"],
      let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds)
    {
      host.cacheDisplay(in: host.bounds, to: bitmap)
      let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
      try data.write(to: URL(fileURLWithPath: directory).appending(path: "information-overlay.png"))
    }
    coordinator.inspector.close()
  }

  @MainActor
  func testVolumeIsCappedForBothModelAndVLCEngine() throws {
    let player = EmbeddedPlayerModel()
    player.start(media(), library: try library())
    defer { player.close() }
    for (requested, expected) in [(200.0, 100.0), (-5.0, 0.0), (75.0, 75.0)] {
      player.setVolume(requested)
      XCTAssertEqual(player.volume, expected)
      XCTAssertEqual(player.engine?.audio?.volume, Int32(expected))
    }
    player.setVolume(.infinity)
    player.setVolume(.nan)
    XCTAssertEqual(player.volume, 75)
    XCTAssertEqual(player.engine?.audio?.volume, 75)
  }

  @MainActor
  func testKeyboardRoutingRespectsMenusAndTextEditing() throws {
    _ = NSApplication.shared
    let window = NSWindow(
      contentRect: NSRect(x: 0, y: 0, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.start(media(), library: try library())
    defer {
      player.close()
      player.setWindow(nil)
    }
    let event = try XCTUnwrap(
      NSEvent.keyEvent(
        with: .keyDown, location: .zero, modifierFlags: [],
        timestamp: 0, windowNumber: window.windowNumber, context: nil, characters: "",
        charactersIgnoringModifiers: "",
        isARepeat: false, keyCode: 126))
    player.setVolume(95)
    XCTAssertTrue(player.handleKeyEvent(event))
    XCTAssertEqual(player.volume, 100)
    player.setMenuTracking(true)
    XCTAssertFalse(player.handleKeyEvent(event))
    player.setMenuTracking(false)
    let text = NSTextView(frame: window.contentView!.bounds)
    window.contentView?.addSubview(text)
    window.makeFirstResponder(text)
    XCTAssertFalse(player.handleKeyEvent(event))
    XCTAssertEqual(player.volume, 100)
  }

  @MainActor
  func testNativeSubmenusAreStableSnapshotsAndIncludeOnlineSearch() {
    let player = EmbeddedPlayerModel()
    let builder = PlayerOptionsMenus.Coordinator(player: player)
    let video = builder.makeMenu(kind: 3)
    let aspect = video.items[0].submenu!
    XCTAssertEqual(aspect.items.map(\.title), ["Default", "16:9", "4:3", "16:10", "1:1", "2.35:1"])
    player.setVolume(60)
    player.noteInteraction()
    XCTAssertTrue(video.items[0].submenu === aspect)
    let subtitles = builder.makeMenu(kind: 1)
    XCTAssertTrue(subtitles.items.contains(where: { $0.title == "Find Online Subtitles…" }))
    XCTAssertNotNil(subtitles.items.last?.submenu)
  }

  func testSubtitleSearchUsesIdentityAndParsesSelectableFiles() async throws {
    let service = service(["/api/v1/subtitles": reply(searchJSON)])
    let page = try await service.search(
      MediaIdentity.parse(filename: "Series.S01E02.1080p.mkv"), language: "en",
      credentials: credentials)
    XCTAssertEqual(page.results.count, 1)
    XCTAssertEqual(page.results.first?.id, 24)
    XCTAssertEqual(page.results.first?.fps, 23.976)
    XCTAssertEqual(page.results.first?.hearingImpaired, true)
    XCTAssertTrue(page.hasMore)
    let request = try XCTUnwrap(SubtitleFixtureProtocol.fixtures.captured().first)
    XCTAssertEqual(request.value(forHTTPHeaderField: "Api-Key"), "fixture-key")
    let query = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)!.queryItems!
    XCTAssertTrue(query.contains(URLQueryItem(name: "season_number", value: "1")))
    XCTAssertTrue(query.contains(URLQueryItem(name: "episode_number", value: "2")))
    XCTAssertFalse(request.url!.absoluteString.contains("127.0.0.1"))
  }

  func testSubtitleDownloadsOnlySelectedFileIntoSafeCacheAndReusesIt() async throws {
    let service = service([
      "/api/v1/subtitles": reply(searchJSON),
      "/api/v1/download": reply(
        "{\"link\":\"https://www.opensubtitles.com/download/selected\",\"remaining\":4}"),
      "/download/selected": reply(subtitle),
    ])
    let cacheURL = URL(fileURLWithPath: "/private/tmp/Myra-subtitle-test-\(UUID())")
    defer { try? FileManager.default.removeItem(at: cacheURL) }
    let cache = try SubtitleCache(directory: cacheURL)
    let page = try await service.search(
      MediaIdentity(title: "Series"), language: "en", credentials: credentials)
    let selected = try XCTUnwrap(page.results.first)
    let result = try await service.download(selected, credentials: credentials, cache: cache)
    XCTAssertEqual(result.remaining, 4)
    XCTAssertEqual(result.url.deletingLastPathComponent().path, cacheURL.path)
    XCTAssertEqual(result.url.lastPathComponent, "MyraSub-24.srt")
    XCTAssertEqual(try Data(contentsOf: result.url), Data(subtitle.utf8))
    let requests = SubtitleFixtureProtocol.fixtures.captured()
    XCTAssertEqual(requests.count, 3)
    XCTAssertEqual(requests[1].httpMethod, "POST")
    XCTAssertNil(requests[2].value(forHTTPHeaderField: "Api-Key"))
    XCTAssertNil(requests[2].value(forHTTPHeaderField: "Authorization"))
    _ = try await service.download(selected, credentials: credentials, cache: cache)
    XCTAssertEqual(SubtitleFixtureProtocol.fixtures.captured().count, 3)
  }

  func testSubtitleFailuresAndMissingCredentialsAreVisibleErrors() async throws {
    for (status, body) in [(429, "{}"), (401, "{}"), (406, "{}"), (200, "not JSON")] {
      let service = service(["/api/v1/subtitles": reply(body, status: status)])
      do {
        _ = try await service.search(
          MediaIdentity(title: "Series"), language: "en", credentials: credentials)
        XCTFail("Failure response accepted: \(status)")
      } catch { XCTAssertTrue(error is SubtitleError) }
    }
    let service = service([:])
    do {
      _ = try await service.search(
        MediaIdentity(title: "Series"), language: "en", credentials: SubtitleCredentials())
      XCTFail("Missing API key accepted")
    } catch { XCTAssertTrue(error is SubtitleError) }
    XCTAssertTrue(SubtitleFixtureProtocol.fixtures.captured().isEmpty)
  }

  func testUntrustedAddressesAndNonSubtitleDataAreRejected() async throws {
    for url in [
      "http://www.opensubtitles.com/file", "https://evil.example/file",
      "https://api.opensubtitles.com.evil.example/file",
      "https://user:secret@www.opensubtitles.com/file", "https://www.opensubtitles.com:8443/file",
    ] {
      XCTAssertFalse(SubtitleURLPolicy.accepts(URL(string: url)!, download: true))
    }
    XCTAssertTrue(
      SubtitleURLPolicy.accepts(URL(string: "https://dl.opensubtitles.com/file")!, download: true))
    let folder = URL(fileURLWithPath: "/private/tmp/Myra-invalid-subtitle-\(UUID())")
    defer { try? FileManager.default.removeItem(at: folder) }
    let cache = try SubtitleCache(directory: folder)
    for data in [
      Data("<html>not subtitles</html>".utf8),
      Data(repeating: 0, count: SubtitleCache.maximumFileBytes + 1),
    ] {
      do {
        _ = try await cache.store(data, fileID: 1)
        XCTFail("Invalid file accepted")
      } catch { XCTAssertTrue(error is SubtitleError) }
    }
    XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: folder.path).isEmpty)
  }

  func testCancellationStopsPendingSubtitleSearch() async throws {
    let service = service(["/api/v1/subtitles": reply(searchJSON, delay: 1)])
    let credentials = self.credentials
    let task = Task {
      try await service.search(
        MediaIdentity(title: "Series"), language: "en", credentials: credentials)
    }
    try await Task.sleep(for: .milliseconds(50))
    task.cancel()
    do {
      _ = try await task.value
      XCTFail("Cancelled search returned results")
    } catch { XCTAssertTrue(error is CancellationError) }
  }

  func testAccountLoginUsesVerifiedHostAndKeepsCredentialsOffFileRequests() async throws {
    let service = service([
      "/api/v1/login": reply(
        "{\"token\":\"fixture-token\",\"base_url\":\"vip-api.opensubtitles.com\",\"user\":{\"allowed_downloads\":20}}"
      ),
      "/api/v1/subtitles": reply(searchJSON),
    ])
    let account = SubtitleCredentials(
      apiKey: "fixture-key", username: "fixture-user", password: "fixture-password")
    let allowance = try await service.signIn(account)
    XCTAssertEqual(allowance, 20)
    _ = try await service.search(
      MediaIdentity(title: "Series"), language: "en", credentials: account)
    let requests = SubtitleFixtureProtocol.fixtures.captured()
    XCTAssertEqual(requests.count, 2)
    XCTAssertEqual(requests[0].httpMethod, "POST")
    XCTAssertEqual(requests[1].url?.host, "vip-api.opensubtitles.com")
    XCTAssertEqual(requests[1].value(forHTTPHeaderField: "Authorization"), "Bearer fixture-token")
    XCTAssertFalse(requests[1].url!.absoluteString.contains("fixture-password"))
  }

  func testUnsafeProviderDownloadLinkNeverGetsFetched() async throws {
    let service = service([
      "/api/v1/download": reply("{\"link\":\"https://evil.example/sub.srt\",\"remaining\":4}")
    ])
    let folder = URL(fileURLWithPath: "/private/tmp/Myra-unsafe-subtitle-\(UUID())")
    defer { try? FileManager.default.removeItem(at: folder) }
    let cache = try SubtitleCache(directory: folder)
    let selected = OnlineSubtitleResult(
      id: 24, filename: "ignored.srt", release: "Series", language: "en", fps: nil, downloads: 1,
      hearingImpaired: false, trusted: false)
    do {
      _ = try await service.download(selected, credentials: credentials, cache: cache)
      XCTFail("Unsafe link accepted")
    } catch { XCTAssertTrue(error is SubtitleError) }
    XCTAssertEqual(SubtitleFixtureProtocol.fixtures.captured().count, 1)
  }

  @MainActor
  func testRealNativeFullscreenExitAndBackToLibrary() async throws {
    guard ProcessInfo.processInfo.environment["MYRA_NATIVE_WINDOW_TESTS"] == "1" else {
      throw XCTSkip(
        "Set MYRA_NATIVE_WINDOW_TESTS=1 to exercise real macOS fullscreen transitions.")
    }
    _ = NSApplication.shared
    NSApp.setActivationPolicy(.regular)
    let window = NSWindow(
      contentRect: NSRect(x: 100, y: 100, width: 640, height: 360),
      styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false
    )
    window.title = "Myra — fullscreen verification"
    window.collectionBehavior = [.fullScreenPrimary]
    let video = VLCVideoView(frame: window.contentView!.bounds)
    video.autoresizingMask = [.width, .height]
    window.contentView?.addSubview(video)
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    player.attach(view: video)
    player.start(media(), library: try library())
    window.makeKeyAndOrderFront(nil)
    NSApp.activate(ignoringOtherApps: true)
    defer {
      player.stop()
      window.orderOut(nil)
      player.setWindow(nil)
    }
    player.toggleFullscreen()
    try await waitFor(
      diagnostics: {
        "enter: model=\(player.isFullscreen), native=\(window.styleMask.contains(.fullScreen)), running=\(NSApp.isRunning), media=\(player.media != nil)"
      }, { player.isFullscreen && window.styleMask.contains(.fullScreen) })
    // Wait until macOS has completed the enter animation before issuing another transition.
    try await Task.sleep(for: .seconds(1))
    let control = NSButton(title: "Transport control", target: nil, action: nil)
    window.contentView?.addSubview(control)
    window.makeFirstResponder(control)
    let arrow = try XCTUnwrap(
      NSEvent.keyEvent(
        with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
        windowNumber: window.windowNumber, context: nil, characters: "",
        charactersIgnoringModifiers: "", isARepeat: false, keyCode: 126))
    // Send through AppKit, not directly to the model, to verify window-level routing.
    player.setVolume(95)
    NSApp.postEvent(arrow, atStart: false)
    try await waitFor { player.volume == 100 }
    XCTAssertTrue(player.controlsVisible)
    player.toggleFullscreen()
    try await waitFor(
      diagnostics: {
        "exit: model=\(player.isFullscreen), native=\(window.styleMask.contains(.fullScreen))"
      }, { !player.isFullscreen && !window.styleMask.contains(.fullScreen) })
    XCTAssertNotNil(player.media)
    player.toggleFullscreen()
    try await waitFor { player.isFullscreen && window.styleMask.contains(.fullScreen) }
    try await Task.sleep(for: .seconds(1))
    player.close()
    try await waitFor(
      diagnostics: {
        "back: model=\(player.isFullscreen), native=\(window.styleMask.contains(.fullScreen)), media=\(player.media != nil)"
      }, { player.media == nil && !window.styleMask.contains(.fullScreen) })
    XCTAssertFalse(player.isFullscreen)
  }

  @MainActor
  func testActualVLCAdvancesEpisodesAndReturnsAfterLast() async throws {
    guard let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else {
      throw XCTSkip("Set MYRA_WORKFLOW_MEDIA_ROOT to the local synthetic-video HTTP fixture.")
    }
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Synthetic TV", categoryRoot: root,
      entry: DirectoryEntry(
        name: "Episode1.mkv", url: root.appending(path: "Series/Episode1.mkv"), kind: .file),
      relativePath: "Series/Episode1.mkv", artworkURL: nil)
    let service = DirectoryService()
    let player = EmbeddedPlayerModel()
    let isolatedLibrary = try library()
    _ = NSApplication.shared
    NSApp.setActivationPolicy(.regular)
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 320, height: 200),
      styleMask: [.titled, .resizable], backing: .buffered, defer: false)
    window.collectionBehavior = [.fullScreenPrimary]
    let video = VLCVideoView(frame: window.contentView!.bounds)
    window.contentView?.addSubview(video)
    player.setWindow(window)
    player.attach(view: video)
    window.makeKeyAndOrderFront(nil)
    player.start(playing, library: isolatedLibrary) {
      let parent = playing.entry.url.deletingLastPathComponent()
      let listing = try await service.listing(at: parent, boundary: URLBoundary(root: root))
      let sequence = try PlaybackSequence(
        playing: playing, entries: listing.entries, artworkURL: listing.artworkURL)
      XCTAssertEqual(
        sequence.videos.count, 2,
        "The actual directory listing must contain both synthetic episodes")
      return sequence
    }
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor(
      diagnostics: {
        "playing: state=\(String(describing: player.engine?.state)), status=\(player.status), time=\(player.elapsed)"
      }, { player.isPlaying })
    player.togglePlayback()
    try await waitFor(
      diagnostics: {
        "pause: state=\(String(describing: player.engine?.state)), media=\(player.media?.entry.name ?? "nil")"
      }, { player.engine?.state == .paused })
    try await Task.sleep(for: .milliseconds(500))
    XCTAssertEqual(player.media?.entry.name, "Episode1.mkv")
    player.toggleFullscreen()
    try await waitFor { player.isFullscreen && window.styleMask.contains(.fullScreen) }
    try await Task.sleep(for: .seconds(1))
    player.togglePlayback()
    try await waitFor(
      diagnostics: {
        "resume: state=\(player.engine?.state.rawValue ?? -1), playing=\(player.engine?.isPlaying ?? false), status=\(player.status)"
      }, { player.engine?.state == .playing })
    try await waitFor(
      seconds: 10,
      diagnostics: {
        "next: state=\(String(describing: player.engine?.state)), media=\(player.media?.entry.name ?? "nil"), time=\(player.elapsed), status=\(player.status)"
      }, { player.media?.entry.name == "Episode2.mkv" }
    )
    XCTAssertTrue(player.isFullscreen)
    XCTAssertTrue(window.styleMask.contains(.fullScreen))
    try await waitFor(seconds: 10) { player.media == nil }
    XCTAssertNil(player.engine)
    XCTAssertFalse(window.styleMask.contains(.fullScreen))
    let index = try await isolatedLibrary.database()
    let firstResume = try await index.playbackPosition(url: playing.entry.url)
    let lastResume = try await index.playbackPosition(
      url: root.appending(path: "Series/Episode2.mkv"))
    XCTAssertNil(firstResume)
    XCTAssertNil(lastResume)
  }

  @MainActor private func waitFor(
    seconds: Double = 7, diagnostics: () -> String = { "" }, _ predicate: () -> Bool
  ) async throws {
    let deadline = Date.now.addingTimeInterval(seconds)
    while !predicate(), Date.now < deadline {
      pumpWindowEvents()
      try await Task.sleep(for: .milliseconds(50))
    }
    guard predicate() else {
      XCTFail("Timed out waiting for the native playback/window transition: \(diagnostics())")
      throw NSError(domain: "Myra-Test", code: 1)
    }
  }

  @MainActor
  func testActualVLCRepeatKeepsCurrentEpisode() async throws {
    guard let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else { throw XCTSkip("Local synthetic-video HTTP fixture is required") }
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Synthetic TV", categoryRoot: root,
      entry: DirectoryEntry(
        name: "Episode1.mkv", url: root.appending(path: "Series/Episode1.mkv"), kind: .file),
      relativePath: "Series/Episode1.mkv", artworkURL: nil)
    let player = EmbeddedPlayerModel()
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let video = VLCVideoView(frame: window.contentView!.bounds)
    window.contentView?.addSubview(video)
    player.setWindow(window)
    player.attach(view: video)
    window.makeKeyAndOrderFront(nil)
    player.repeatEnabled = true
    player.start(playing, library: try library())
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor { player.isPlaying && player.elapsed > 1 }
    try await waitFor(
      seconds: 10,
      diagnostics: {
        "repeat: media=\(player.media?.entry.name ?? "nil"), state=\(player.engine?.state.rawValue ?? -1), time=\(player.elapsed)"
      }, { player.isPlaying && player.elapsed < 0.5 }
    )
    XCTAssertEqual(player.media?.entry.name, "Episode1.mkv")
    try await waitFor { player.isPlaying && player.elapsed > 1 }
    XCTAssertEqual(player.media?.entry.name, "Episode1.mkv")
  }

  @MainActor private func pumpWindowEvents() {
    while let event = NSApp.nextEvent(matching: .any, until: .now, inMode: .default, dequeue: true)
    {
      NSApp.sendEvent(event)
    }
    NSApp.updateWindows()
  }

  @MainActor
  func testEnglishTracksBecomeDefaultInActualEmbeddedPlayer() async throws {
    guard let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else { throw XCTSkip("Local synthetic-video HTTP fixture is required") }
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Track verification", categoryRoot: root,
      entry: DirectoryEntry(
        name: "TrackDefaults.mkv", url: root.appending(path: "TrackDefaults.mkv"), kind: .file),
      relativePath: "TrackDefaults.mkv", artworkURL: nil)
    let player = EmbeddedPlayerModel()
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 800, height: 500),
      styleMask: [.titled, .resizable], backing: .buffered, defer: false)
    window.contentView = NSHostingView(rootView: EmbeddedPlayerView(player: player))
    player.setWindow(window)
    window.makeKeyAndOrderFront(nil)
    player.start(playing, library: try library())
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor(
      diagnostics: {
        "English defaults: audio=\(player.audioTracks), selected=\(player.selectedAudio); subtitles=\(player.subtitleTracks), selected=\(player.selectedSubtitle); state=\(player.engine?.state.rawValue ?? -1), playing=\(player.engine?.isPlaying ?? false)"
      },
      {
        let audio = player.audioTracks.first(where: { $0.language == "eng" })
        let subtitle = player.subtitleTracks.first(where: { $0.language == "eng" })
        return audio != nil && subtitle != nil && player.selectedAudio == audio?.id
          && player.selectedSubtitle == subtitle?.id
      })
    let frenchAudio = try XCTUnwrap(player.audioTracks.first(where: { $0.language == "fra" }))
    let frenchSubtitle = try XCTUnwrap(player.subtitleTracks.first(where: { $0.language == "fra" }))
    player.chooseAudio(frenchAudio.id)
    player.chooseSubtitle(frenchSubtitle.id)
    try await Task.sleep(for: .milliseconds(700))
    XCTAssertEqual(
      player.selectedAudio, frenchAudio.id, "A manual choice must override the default")
    XCTAssertEqual(player.selectedSubtitle, frenchSubtitle.id)
  }

  @MainActor
  func testFullscreenIconExitsFromSwiftUIPlayer() async throws {
    guard ProcessInfo.processInfo.environment["MYRA_NATIVE_WINDOW_TESTS"] == "1",
      let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else { throw XCTSkip("Native window and local video fixtures are required") }
    _ = NSApplication.shared
    NSApp.setActivationPolicy(.regular)
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Track verification", categoryRoot: root,
      entry: DirectoryEntry(
        name: "TrackDefaults.mkv", url: root.appending(path: "TrackDefaults.mkv"), kind: .file),
      relativePath: "TrackDefaults.mkv", artworkURL: nil)
    let player = EmbeddedPlayerModel()
    let window = NSWindow(
      contentRect: NSRect(x: 150, y: 150, width: 1000, height: 650),
      styleMask: [.titled, .resizable], backing: .buffered, defer: false)
    window.collectionBehavior = [.fullScreenPrimary]
    let host = NSHostingView(rootView: EmbeddedPlayerView(player: player))
    window.contentView = host
    player.setWindow(window)
    window.makeKeyAndOrderFront(nil)
    NSApp.activate(ignoringOtherApps: true)
    player.start(playing, library: try library())
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor { player.isPlaying }
    // The window toolbar now owns normal-mode entry; the overlay retains the exit button.
    player.toggleFullscreen()
    try await waitFor { player.isFullscreen && window.styleMask.contains(.fullScreen) }
    try await Task.sleep(for: .seconds(1))
    XCTAssertTrue(window.styleMask.contains(.fullSizeContentView))
    XCTAssertEqual(window.titleVisibility, .hidden)
    XCTAssertTrue(window.titlebarAppearsTransparent)
    host.layoutSubtreeIfNeeded()
    XCTAssertEqual(
      host.frame.height, window.frame.height, accuracy: 1,
      "Fullscreen content must extend through the native title-bar region")
    var seekbar: SeekSlider?
    func findSeekbar(_ view: NSView) {
      if let slider = view as? SeekSlider { seekbar = slider }
      for child in view.subviews { findSeekbar(child) }
    }
    findSeekbar(host)
    XCTAssertFalse(try XCTUnwrap(seekbar).allowsTooltips)
    XCTAssertNil(seekbar?.toolTip)
    player.hoverControls(false)
    try await waitFor { !player.controlsVisible }
    player.noteInteraction()
    host.layoutSubtreeIfNeeded()
    pumpWindowEvents()
    try clickFullscreen(in: host, window: window)
    try await waitFor(
      diagnostics: {
        "Fullscreen icon: model=\(player.isFullscreen), native=\(window.styleMask.contains(.fullScreen))"
      }, { !player.isFullscreen && !window.styleMask.contains(.fullScreen) })
    XCTAssertNotNil(player.media)
    XCTAssertFalse(window.styleMask.contains(.fullSizeContentView))
    XCTAssertEqual(window.titleVisibility, .visible)
    try await Task.sleep(for: .milliseconds(150))
    findSeekbar(host)
    XCTAssertTrue(try XCTUnwrap(seekbar).allowsTooltips)
  }

  @MainActor private func clickFullscreen(in host: NSView, window: NSWindow) throws {
    host.layoutSubtreeIfNeeded()
    let point = NSPoint(
      x: host.bounds.maxX - 22,
      y: host.isFlipped ? 28 : host.bounds.maxY - 28)
    let location = host.convert(point, to: nil)
    for kind in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
      let event = try XCTUnwrap(
        NSEvent.mouseEvent(
          with: kind, location: location,
          modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
          windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1,
          pressure: 1))
      NSApp.postEvent(event, atStart: false)
    }
    pumpWindowEvents()
  }

  @MainActor
  func testReplacementPlaybackCancelsPendingAutoNext() async throws {
    guard let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else { throw XCTSkip("Local synthetic-video HTTP fixture is required") }
    let first = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Synthetic TV", categoryRoot: root,
      entry: DirectoryEntry(
        name: "Episode1.mkv", url: root.appending(path: "Series/Episode1.mkv"), kind: .file),
      relativePath: "Series/Episode1.mkv", artworkURL: nil)
    let last = GlobalSearchResult(
      categoryID: first.categoryID, categoryName: first.categoryName, categoryRoot: root,
      entry: DirectoryEntry(
        name: "Episode2.mkv", url: root.appending(path: "Series/Episode2.mkv"), kind: .file),
      relativePath: "Series/Episode2.mkv", artworkURL: nil)
    let player = EmbeddedPlayerModel()
    let isolatedLibrary = try library()
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let video = VLCVideoView(frame: window.contentView!.bounds)
    window.contentView?.addSubview(video)
    player.setWindow(window)
    player.attach(view: video)
    window.makeKeyAndOrderFront(nil)
    player.start(first, library: isolatedLibrary) {
      try await Task.sleep(for: .seconds(30))
      return try PlaybackSequence(
        playing: first, entries: [first.entry, last.entry], artworkURL: nil)
    }
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor { player.isPlaying }
    try await waitFor { player.engine?.state == .ended || player.engine?.state == .stopped }
    XCTAssertEqual(player.media?.entry.url, first.entry.url)
    player.start(last, library: isolatedLibrary)
    try await waitFor { player.isPlaying && player.media?.entry.url == last.entry.url }
    try await waitFor { player.media == nil }
    XCTAssertNil(player.engine)
  }

  func testSubtitleCacheRejectsSymlinksAndPrunesOnlyOwnedExpiredFiles() async throws {
    let folder = URL(
      fileURLWithPath: "/private/tmp/Myra-cache-safety-\(UUID())", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: folder) }
    let directory = folder.appending(path: "cache", directoryHint: .isDirectory)
    let cache = try SubtitleCache(directory: directory)
    let external = folder.appending(path: "outside.srt")
    let data = Data(subtitle.utf8)
    try data.write(to: external)
    let link = directory.appending(path: "MyraSub-24.srt")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: external)
    let cachedLink = await cache.cached(fileID: 24)
    XCTAssertNil(cachedLink)
    do {
      _ = try await cache.store(data, fileID: 24)
      XCTFail("A symbolic link must not redirect cache writes")
    } catch { XCTAssertTrue(error is SubtitleError) }
    XCTAssertEqual(try Data(contentsOf: external), data)
    let old = try await cache.store(data, fileID: 25)
    let unrelated = directory.appending(path: "personal.srt")
    try data.write(to: unrelated)
    let expired = Date.now.addingTimeInterval(-31 * 86400)
    for file in [old, unrelated] {
      try FileManager.default.setAttributes([.modificationDate: expired], ofItemAtPath: file.path)
    }
    let expiredHit = await cache.cached(fileID: 25)
    XCTAssertNil(expiredHit)
    _ = try await cache.store(data, fileID: 26)
    XCTAssertFalse(FileManager.default.fileExists(atPath: old.path))
    XCTAssertTrue(FileManager.default.fileExists(atPath: unrelated.path))
    XCTAssertEqual(try Data(contentsOf: external), data)
  }

  @MainActor
  func testSelectedCachedSubtitleLoadsIntoActualVLC() async throws {
    guard let text = ProcessInfo.processInfo.environment["MYRA_WORKFLOW_MEDIA_ROOT"],
      let root = URL(string: text)
    else { throw XCTSkip("Local synthetic-video HTTP fixture is required") }
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Synthetic TV", categoryRoot: root,
      entry: DirectoryEntry(
        name: "Episode1.mkv", url: root.appending(path: "Series/Episode1.mkv"), kind: .file),
      relativePath: "Series/Episode1.mkv", artworkURL: nil)
    let player = EmbeddedPlayerModel()
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 320, height: 200),
      styleMask: [.titled], backing: .buffered, defer: false)
    let video = VLCVideoView(frame: window.contentView!.bounds)
    window.contentView?.addSubview(video)
    player.setWindow(window)
    player.attach(view: video)
    window.makeKeyAndOrderFront(nil)
    player.start(playing, library: try library())
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    try await waitFor { player.isPlaying }
    player.togglePlayback()
    try await waitFor { player.engine?.state == .paused }
    let folder = URL(
      fileURLWithPath: "/private/tmp/Myra-VLC-subtitle-\(UUID())", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: folder) }
    let cache = try SubtitleCache(directory: folder)
    let selected = OnlineSubtitleResult(
      id: 24, filename: "../../English.srt", release: "English",
      language: "en", fps: 10, downloads: 1, hearingImpaired: false, trusted: true)
    let provider = service([
      "/api/v1/download": reply(
        "{\"link\":\"https://www.opensubtitles.com/fixture.srt\",\"remaining\":4}"),
      "/fixture.srt": reply(subtitle),
    ])
    let download = try await provider.download(selected, credentials: credentials, cache: cache)
    XCTAssertEqual(download.url.lastPathComponent, "MyraSub-24.srt")
    let wrongMedia = GlobalSearchResultID(categoryID: UUID(), url: playing.entry.url)
    XCTAssertFalse(player.loadSubtitle(from: download.url, for: wrongMedia))
    XCTAssertTrue(player.loadSubtitle(from: download.url, for: playing.id))
    try await waitFor(
      diagnostics: {
        "subtitle: tracks=\(player.subtitleTracks), selected=\(player.selectedSubtitle)"
      }, { player.subtitleTracks.contains(where: { $0.id >= 0 }) && player.selectedSubtitle >= 0 })
    XCTAssertEqual(player.media?.entry.url, playing.entry.url)
  }
}
