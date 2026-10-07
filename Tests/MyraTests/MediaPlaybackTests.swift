import AppKit
import Foundation
import VLCKit
import XCTest

@testable import Myra

private final class MetadataFixtureState: @unchecked Sendable {
  private let lock = NSLock()
  private var body = ""
  private var captured: URLRequest?
  func set(_ body: String) {
    lock.lock()
    defer { lock.unlock() }
    self.body = body
    captured = nil
  }
  func respond(_ request: URLRequest) -> Data {
    lock.lock()
    defer { lock.unlock() }
    captured = request
    return Data(body.utf8)
  }
  func request() -> URLRequest? {
    lock.lock()
    defer { lock.unlock() }
    return captured
  }
}

private final class MetadataFixtureProtocol: URLProtocol, @unchecked Sendable {
  static let fixture = MetadataFixtureState()
  override class func canInit(with request: URLRequest) -> Bool { true }
  override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
  override func startLoading() {
    let data = Self.fixture.respond(request)
    let response = HTTPURLResponse(
      url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
      headerFields: ["Content-Type": "application/json"])!
    client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    client?.urlProtocol(self, didLoad: data)
    client?.urlProtocolDidFinishLoading(self)
  }
  override func stopLoading() {}
}

final class MediaPlaybackTests: XCTestCase {
  func testEmptyDownloadQueueVisibilityIncludesPreparingAndHistory() {
    XCTAssertFalse(
      DownloadQueueVisibility.shouldShow(batchCount: 0, itemCount: 0, isPreparing: false))
    XCTAssertTrue(
      DownloadQueueVisibility.shouldShow(batchCount: 0, itemCount: 0, isPreparing: true))
    XCTAssertTrue(
      DownloadQueueVisibility.shouldShow(batchCount: 1, itemCount: 0, isPreparing: false))
    XCTAssertTrue(
      DownloadQueueVisibility.shouldShow(batchCount: 0, itemCount: 1, isPreparing: false))
  }

  func testBufferingRequiresAStallNotAStaleVLCState() {
    var detector = PlaybackBufferingState()
    detector.reset(now: 100)
    XCTAssertFalse(detector.update(state: .opening, time: 0, now: 100, scrubbing: false))
    XCTAssertFalse(detector.update(state: .buffering, time: 0, now: 101.9, scrubbing: false))
    XCTAssertTrue(detector.update(state: .buffering, time: 0, now: 102, scrubbing: false))
    XCTAssertFalse(detector.update(state: .buffering, time: 0.5, now: 102.1, scrubbing: false))
    for second in 1...10 {
      XCTAssertFalse(
        detector.update(
          state: .buffering, time: Double(second), now: 103 + Double(second), scrubbing: false))
    }
    XCTAssertTrue(detector.update(state: .playing, time: 10, now: 115, scrubbing: false))
    XCTAssertFalse(detector.update(state: .paused, time: 10, now: 200, scrubbing: false))
    XCTAssertFalse(detector.update(state: .stopped, time: 0, now: 300, scrubbing: false))
    XCTAssertFalse(detector.update(state: .ended, time: 10, now: 400, scrubbing: false))
    XCTAssertFalse(detector.update(state: .error, time: 10, now: 500, scrubbing: false))
    XCTAssertFalse(detector.update(state: .buffering, time: 10, now: 600, scrubbing: true))
    detector.reset(now: 700)
    XCTAssertFalse(detector.update(state: .playing, time: 0, now: 701, scrubbing: false))
  }

  func testFullscreenControlsHideAndReturnOnInteractionWithoutInterruptingScrubbing() {
    var controls = PlayerControlsVisibility()
    XCTAssertTrue(controls.isVisible(now: 100, scrubbing: false))
    controls.fullscreen = true
    controls.interact(now: 100)
    XCTAssertTrue(controls.isVisible(now: 101.9, scrubbing: false))
    XCTAssertFalse(controls.isVisible(now: 102, scrubbing: false))
    controls.interact(now: 103)
    XCTAssertTrue(controls.isVisible(now: 103.5, scrubbing: false))
    XCTAssertTrue(controls.isVisible(now: 200, scrubbing: true))
    controls.hovering = true
    XCTAssertTrue(controls.isVisible(now: 300, scrubbing: false))
    controls.hovering = false
    controls.menuTracking = true
    XCTAssertTrue(controls.isVisible(now: 400, scrubbing: false))
    controls.menuTracking = false
    XCTAssertFalse(controls.isVisible(now: 401, scrubbing: false))
    controls.fullscreen = false
    XCTAssertTrue(controls.isVisible(now: 500, scrubbing: false))
  }

  func testStreamDetailsCombineResolutionAndFrameRate() {
    XCTAssertEqual(
      StreamDetailsPresentation.videoFormat(["Resolution": "1920 × 1080", "Frame rate": "24 fps"]),
      "1920x1080 @ 24fps")
    XCTAssertEqual(
      StreamDetailsPresentation.videoFormat([
        "Resolution": "1920 × 1080", "Frame rate": "23.98 fps",
      ]), "1920x1080 @ 23.98fps")
    XCTAssertEqual(StreamDetailsPresentation.videoFormat(["Resolution": "64 × 36"]), "64x36")
    XCTAssertEqual(StreamDetailsPresentation.videoFormat([:]), "Unavailable")
  }

  func testSeekHitTestingIncludesBothEndsAndClampsOutOfBounds() {
    let bounds = NSRect(x: 0, y: 0, width: 300, height: 22)
    XCTAssertEqual(SeekBarGeometry.fraction(x: -100, bounds: bounds), 0)
    XCTAssertEqual(SeekBarGeometry.fraction(x: 9, bounds: bounds), 0)
    XCTAssertEqual(SeekBarGeometry.fraction(x: 150, bounds: bounds), 0.5)
    XCTAssertEqual(SeekBarGeometry.fraction(x: 291, bounds: bounds), 1)
    XCTAssertEqual(SeekBarGeometry.fraction(x: 400, bounds: bounds), 1)
    XCTAssertTrue(SeekBarGeometry.fraction(x: 1, bounds: .zero).isFinite)
  }

  @MainActor
  func testWindowTrackingRestoresMouseEventSettingWhenDetached() {
    _ = NSApplication.shared
    let window = NSWindow(
      contentRect: NSRect(x: 0, y: 0, width: 320, height: 200), styleMask: [.titled],
      backing: .buffered, defer: true)
    window.acceptsMouseMovedEvents = false
    let player = EmbeddedPlayerModel()
    player.setWindow(window)
    XCTAssertTrue(window.acceptsMouseMovedEvents)
    player.setWindow(window)
    player.setWindow(nil)
    XCTAssertFalse(window.acceptsMouseMovedEvents)
    XCTAssertFalse(player.isFullscreen)
    player.close()
  }

  @MainActor
  func testActualVLCIconAndSeekBarContrastInBothAppearances() throws {
    XCTAssertGreaterThan(VLCIconAsset.image.size.width, 20)
    for (name, dark) in [(NSAppearance.Name.aqua, false), (.darkAqua, true)] {
      let slider = NSSlider(frame: NSRect(x: 0, y: 0, width: 300, height: 22))
      slider.appearance = NSAppearance(named: name)
      let cell = PlayerSeekSliderCell()
      slider.cell = cell
      cell.minValue = 0
      cell.maxValue = 1
      cell.doubleValue = 0.4
      let bitmap = try XCTUnwrap(
        NSBitmapImageRep(
          bitmapDataPlanes: nil, pixelsWide: 300, pixelsHigh: 22, bitsPerSample: 8,
          samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
          bytesPerRow: 0, bitsPerPixel: 0))
      let context = try XCTUnwrap(NSGraphicsContext(bitmapImageRep: bitmap))
      NSGraphicsContext.saveGraphicsState()
      NSGraphicsContext.current = context
      slider.effectiveAppearance.performAsCurrentDrawingAppearance {
        (dark ? NSColor.black : NSColor.white).setFill()
        slider.bounds.fill()
        cell.drawBar(inside: slider.bounds, flipped: false)
        cell.drawKnob(cell.knobRect(flipped: false))
      }
      NSGraphicsContext.restoreGraphicsState()
      let unplayed = try XCTUnwrap(bitmap.colorAt(x: 230, y: 11)?.usingColorSpace(.deviceRGB))
      let background = try XCTUnwrap(bitmap.colorAt(x: 230, y: 0)?.usingColorSpace(.deviceRGB))
      XCTAssertGreaterThan(abs(unplayed.redComponent - background.redComponent), 0.15)
      XCTAssertEqual(cell.knobRect(flipped: false).width, 16)
      if let directory = ProcessInfo.processInfo.environment["MYRA_UI_SNAPSHOT_DIR"] {
        let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        try data.write(
          to: URL(fileURLWithPath: directory).appending(
            path: dark ? "seekbar-dark.png" : "seekbar-light.png"))
      }
    }
  }

  func testEnglishDefaultsAndFallbacks() {
    let tracks = [
      PlayerTrack(id: 1, name: "French", language: "fra"),
      PlayerTrack(id: 2, name: "English commentary", language: "eng"),
      PlayerTrack(id: 3, name: "Main", language: "en-GB"),
    ]
    XCTAssertEqual(EnglishTrackPreference.preferred(in: tracks, subtitles: false), 3)
    XCTAssertNil(EnglishTrackPreference.preferred(in: [tracks[0]], subtitles: false))
    let subtitles = [
      PlayerTrack(id: -1, name: "Disabled", language: nil),
      PlayerTrack(id: 4, name: "English forced", language: "eng"),
      PlayerTrack(id: 5, name: "English", language: nil),
    ]
    XCTAssertEqual(EnglishTrackPreference.preferred(in: subtitles, subtitles: true), 5)
    XCTAssertNil(EnglishTrackPreference.preferred(in: [], subtitles: true))
  }

  func testTimestamps() {
    XCTAssertEqual(PlayerTime.string(0), "0:00")
    XCTAssertEqual(PlayerTime.string(3661), "1:01:01")
    XCTAssertEqual(PlayerTime.string(-2), "0:00")
    XCTAssertEqual(PlayerTime.string(.nan), "0:00")
  }

  func testOMDbUsesOnlyIdentityAndHandlesFailureAndMissingKey() async throws {
    let config = URLSessionConfiguration.ephemeral
    config.protocolClasses = [MetadataFixtureProtocol.self]
    let service = MetadataService(session: URLSession(configuration: config))
    MetadataFixtureProtocol.fixture.set(
      """
      {"Title":"The Matrix","Year":"1999","imdbRating":"8.7","Response":"True","Plot":"Test plot","imdbID":"tt0133093"}
      """)
    let identity = MediaIdentity.parse(filename: "The.Matrix.1999.1080p.mkv")
    let metadata = try await service.lookup(identity, key: "fixture-not-a-real-key", index: nil)
    XCTAssertEqual(metadata.imdbRating, "8.7")
    let captured = try XCTUnwrap(MetadataFixtureProtocol.fixture.request())
    XCTAssertEqual(captured.url?.scheme, "https")
    XCTAssertEqual(captured.url?.host, "www.omdbapi.com")
    let names =
      URLComponents(url: captured.url!, resolvingAgainstBaseURL: false)?.queryItems?.map(\.name)
      ?? []
    XCTAssertEqual(Set(names), ["apikey", "plot", "t", "y"])
    MetadataFixtureProtocol.fixture.set("{\"Response\":\"False\",\"Error\":\"Movie not found!\"}")
    do {
      _ = try await service.lookup(identity, key: "fixture", index: nil)
      XCTFail("Provider failure accepted")
    } catch { XCTAssertTrue(error is MetadataError) }
    MetadataFixtureProtocol.fixture.set("not JSON")
    do {
      _ = try await service.lookup(identity, key: "fixture", index: nil)
      XCTFail("Malformed response accepted")
    } catch { XCTAssertTrue(error is MetadataError) }
    MetadataFixtureProtocol.fixture.set("")
    do {
      _ = try await service.lookup(identity, key: "", index: nil)
      XCTFail("Empty key accepted")
    } catch { XCTAssertTrue(error is MetadataError) }
    XCTAssertNil(MetadataFixtureProtocol.fixture.request())
  }

  func testPlaybackPositionPersistsAndCompletedFilesDoNotResume() async throws {
    let folder = FileManager.default.temporaryDirectory.appending(
      path: "Myra-playback-test-\(UUID())")
    defer { try? FileManager.default.removeItem(at: folder) }
    let url = folder.appending(path: "index.sqlite")
    let index = try LibraryIndex(url: url)
    let media = URL(string: "https://media.example/Movie.mkv")!
    try await index.savePlaybackPosition(url: media, seconds: 30, duration: 200)
    let reopened = try LibraryIndex(url: url)
    let saved = try await reopened.playbackPosition(url: media)
    XCTAssertEqual(saved, 30)
    try await index.savePlaybackPosition(url: media, seconds: 0, duration: 200)
    let completed = try await reopened.playbackPosition(url: media)
    XCTAssertNil(completed)
  }

  @MainActor
  func testRealVLCEngineParsesAndPlaysSyntheticMedia() async throws {
    guard let path = ProcessInfo.processInfo.environment["MYRA_VLC_TEST_MEDIA"],
      FileManager.default.fileExists(atPath: path)
    else {
      throw XCTSkip(
        "Set MYRA_VLC_TEST_MEDIA to a synthetic video fixture to exercise the bundled VLC engine.")
    }
    let url = URL(fileURLWithPath: path)
    let details = try await VLCMediaProbe.inspect(url: url)
    XCTAssertEqual(details["Resolution"], "64 × 36")
    XCTAssertNotNil(details["Video codec"])
    let engine = VLCMediaPlayer(options: ["--quiet", "--vout=dummy", "--aout=dummy"])
    engine.media = VLCMedia(url: url)
    engine.play()
    defer { engine.stop() }
    let deadline = Date.now.addingTimeInterval(10)
    while (!engine.isPlaying || !engine.isSeekable || engine.time.intValue < 200)
      && Date.now < deadline
    {
      try await Task.sleep(for: .milliseconds(100))
    }
    XCTAssertTrue(engine.isPlaying)
    engine.pause()
    let pauseDeadline = Date.now.addingTimeInterval(5)
    while engine.isPlaying && Date.now < pauseDeadline {
      try await Task.sleep(for: .milliseconds(50))
    }
    // VLCKit can leave its reported state at buffering while playback actually pauses.
    XCTAssertFalse(engine.isPlaying)
    let pausedTime = engine.time.intValue
    try await Task.sleep(for: .milliseconds(300))
    XCTAssertEqual(engine.time.intValue, pausedTime, accuracy: 150)
    XCTAssertLessThan(pausedTime, 10_000)
    XCTAssertTrue(engine.isSeekable)
    let targetSeconds = Double(engine.media?.length.intValue ?? 0) / 1000 * 0.4
    XCTAssertGreaterThan(targetSeconds, 7)
    let seekStarted = ProcessInfo.processInfo.systemUptime
    engine.position = 0.4
    engine.rate = 1.5
    engine.play()
    // VLC's time/position getters are updated asynchronously by engine events.
    // Wait for the requested seek, not a fixed delay or merely any nonzero time.
    let seekDeadline = Date.now.addingTimeInterval(5)
    while Double(engine.time.intValue) / 1000 < targetSeconds - 1 && Date.now < seekDeadline {
      try await Task.sleep(for: .milliseconds(50))
    }
    let seekElapsed = ProcessInfo.processInfo.systemUptime - seekStarted
    let actualSeconds = Double(engine.time.intValue) / 1000
    XCTAssertGreaterThanOrEqual(actualSeconds, targetSeconds - 1)
    XCTAssertEqual(actualSeconds, targetSeconds + seekElapsed * 1.5, accuracy: 2)
    XCTAssertGreaterThanOrEqual(engine.position, 0.35)
    XCTAssertEqual(engine.rate, 1.5, accuracy: 0.01)
    XCTAssertGreaterThanOrEqual(engine.audioTrackIndexes.count, 2)
    let playbackDeadline = Date.now.addingTimeInterval(3)
    while Double(engine.time.intValue) / 1000 <= actualSeconds + 0.25
      && Date.now < playbackDeadline
    {
      try await Task.sleep(for: .milliseconds(50))
    }
    XCTAssertGreaterThan(Double(engine.time.intValue) / 1000, actualSeconds + 0.25)
  }
}
