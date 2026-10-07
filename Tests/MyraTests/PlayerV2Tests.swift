import AppKit
import Foundation
import SwiftUI
import XCTest

@testable import Myra

final class PlayerV2Tests: XCTestCase {
  func testVariantGroupingDoesNotNavigateToSameMovie() throws {
    let root = URL(string: "https://example.test/media/")!
    let names = [
      "Movie.2025.1080p.WEB-DL.mkv", "Movie.2025.2160p.BluRay.mkv", "Next.2026.1080p.mkv",
    ]
    let entries = names.map { DirectoryEntry(name: $0, url: root.appending(path: $0), kind: .file) }
    let playing = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Movies", categoryRoot: root, entry: entries[1],
      relativePath: names[1], artworkURL: nil)
    let sequence = try PlaybackSequence(playing: playing, entries: entries, artworkURL: nil)
    XCTAssertEqual(sequence.groups.count, 2)
    XCTAssertNil(sequence.previous(before: entries[1].url))
    XCTAssertEqual(sequence.next(after: entries[1].url)?.entry.name, names[2])
    XCTAssertEqual(sequence.neighbours(of: entries[2].url, offset: -1).count, 2)
  }

  func testYearlessQualityVariantsMergeInFolderAndEpisodesStaySeparate() throws {
    let root = URL(string: "https://example.test/media/")!
    let names = [
      "Movie.1080p.mkv", "Movie.720p.mkv", "Series.S01E01.1080p.mkv", "Series.S01E01.720p.mkv",
      "Series.S01E02.mkv",
    ]
    let entries = names.map { DirectoryEntry(name: $0, url: root.appending(path: $0), kind: .file) }
    let result = GlobalSearchResult(
      categoryID: UUID(), categoryName: "TV", categoryRoot: root, entry: entries[0],
      relativePath: names[0], artworkURL: nil)
    let sequence = try PlaybackSequence(playing: result, entries: entries, artworkURL: nil)
    XCTAssertEqual(sequence.groups.count, 3)
    XCTAssertEqual(sequence.neighbours(of: entries[3].url, offset: 1).first?.entry.name, names[4])
  }

  func testSkipRangeRejectsInvalidNumbersAndDurations() {
    XCTAssertTrue(PlayerSkipRange(start: 0, end: 60).valid(duration: 90))
    for range in [
      PlayerSkipRange(start: -1, end: 10), .init(start: 10, end: 10), .init(start: 0, end: 100),
      .init(start: .nan, end: 10),
    ] {
      XCTAssertFalse(range.valid(duration: 90))
    }
    XCTAssertFalse(PlayerSkipRange(start: 0, end: 60).contains(60))
  }

  func testPlayerPersonalStateRoundTripsWithoutIndex() throws {
    let defaults = try XCTUnwrap(UserDefaults(suiteName: "Myra.tests.\(UUID())"))
    defer { defaults.removeObject(forKey: PlayerPersonalState.defaultsKey) }
    var state = PlayerPersonalState()
    state.speed = 1.5
    state.autoplay = false
    state.subtitleLanguage = "off"
    state.markers["series|show"] = .init(intro: .init(start: 0, end: 45), outro: nil)
    try state.save(defaults: defaults)
    XCTAssertEqual(PlayerPersonalState.load(defaults: defaults), state)
    XCTAssertEqual(
      try JSONDecoder().decode(PlayerPersonalState.self, from: JSONEncoder().encode(state)), state)
  }

  func testLocalBoundaryRejectsOtherDirectories() {
    let root = URL(fileURLWithPath: "/private/tmp/Myra-local")
    XCTAssertTrue(PlaybackSequence.contains(root.appending(path: "movie.mkv"), root: root))
    XCTAssertFalse(PlaybackSequence.contains(root.appending(path: "sub/movie.mkv"), root: root))
    XCTAssertFalse(
      PlaybackSequence.contains(URL(string: "https://example.test/movie.mkv")!, root: root))
  }
  func testResumeChoosesMostRecentlyPlayedVersionRatherThanFurthestEpisode() {
    let root = URL(string: "https://example.test/media/")!
    func version(_ name: String, seconds: Double, updated: Double, season: Int, episode: Int)
      -> EntertainmentVersion
    {
      let media = GlobalSearchResult(
        categoryID: UUID(), categoryName: "TV", categoryRoot: root,
        entry: DirectoryEntry(name: name, url: root.appending(path: name), kind: .file),
        relativePath: name, artworkURL: nil)
      return EntertainmentVersion(
        media: media, firstDiscovered: .distantPast,
        progressSeconds: seconds, duration: 1800, lastPlayed: Date(timeIntervalSince1970: updated),
        season: season, episode: episode)
    }
    let older = version("Show.S02E05.1080p.mkv", seconds: 900, updated: 1, season: 2, episode: 5)
    let recent = version("Show.S01E01.720p.mkv", seconds: 60, updated: 2, season: 1, episode: 1)
    var title = EntertainmentTitle(
      id: "show", name: "Show", kind: .series, versions: [older, recent])
    XCTAssertEqual(title.resumeVersion?.id, recent.id)
    title.kind = .movie
    XCTAssertEqual(title.resumeVersion?.id, recent.id)
    title.versions[1].progressSeconds = 1795
    XCTAssertNil(title.resumeVersion, "Finishing the last played file must not resume an older one")
    title.versions[1].duration = 0
    title.versions[1].progressSeconds = 60
    XCTAssertEqual(
      title.resumeVersion?.id, recent.id, "Unknown duration must not discard saved progress")
    title.versions[1].lastPlayed = nil
    XCTAssertEqual(title.resumeVersion?.id, older.id)
  }

  func testSeasonGroupingKeepsEpisodeVersionsTogetherAndSpecialsSeparate() {
    let root = URL(string: "https://example.test/media/")!
    let names = [
      "Show.S02E01.mkv", "Show.S01E02.mkv", "Show.S01E01.1080p.mkv", "Show.S01E01.720p.mkv",
      "Show.Special.mkv",
    ]
    let versions = names.map { name in
      let identity = MediaIdentity.parse(filename: name)
      let media = GlobalSearchResult(
        categoryID: UUID(), categoryName: "TV", categoryRoot: root,
        entry: DirectoryEntry(name: name, url: root.appending(path: name), kind: .file),
        relativePath: name, artworkURL: nil)
      return EntertainmentVersion(
        media: media, firstDiscovered: .distantPast,
        season: identity.season.flatMap(Int.init), episode: identity.episode.flatMap(Int.init))
    }
    let seasons = EntertainmentSeason.seasons(versions)
    XCTAssertEqual(seasons.map(\.number), [1, 2, nil])
    XCTAssertEqual(seasons[0].episodes.map(\.episode), [1, 2])
    XCTAssertEqual(seasons[0].episodes[0].versions.count, 2)
    XCTAssertEqual(seasons[2].label, "Other files")
  }

  @MainActor
  func testExplicitResumeSeeksInActualVLCWithoutPrompt() async throws {
    guard let path = ProcessInfo.processInfo.environment["MYRA_VLC_TEST_MEDIA"] else {
      throw XCTSkip("Synthetic local media is required")
    }
    let url = URL(fileURLWithPath: path)
    let directory = FileManager.default.temporaryDirectory.appending(path: "Myra-resume-\(UUID())")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let library = LibraryController(
      index: try LibraryIndex(url: directory.appending(path: "index.sqlite")))
    let media = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Resume test",
      categoryRoot: url.deletingLastPathComponent(),
      entry: DirectoryEntry(name: url.lastPathComponent, url: url, kind: .file),
      relativePath: url.lastPathComponent, artworkURL: nil)
    _ = NSApplication.shared
    let player = EmbeddedPlayerModel()
    let window = NSWindow(
      contentRect: NSRect(x: 120, y: 120, width: 1000, height: 650),
      styleMask: [.titled, .resizable], backing: .buffered, defer: false)
    window.contentView = NSHostingView(rootView: EmbeddedPlayerView(player: player))
    window.makeKeyAndOrderFront(nil)
    player.setWindow(window)
    defer {
      player.stop()
      player.setWindow(nil)
      window.orderOut(nil)
    }
    player.start(media, library: library, resumeSeconds: 8)
    for _ in 0..<100 {
      if player.elapsed >= 7.5 { break }
      try await Task.sleep(for: .milliseconds(100))
    }
    XCTAssertGreaterThanOrEqual(player.elapsed, 7.5)
    XCTAssertLessThan(player.elapsed, 12)
    XCTAssertNil(player.resumePosition, "Explicit Resume must not ask for a second confirmation")
    player.start(media, library: library, resumeSeconds: 12)
    for _ in 0..<100 {
      if player.elapsed >= 11.5 { break }
      try await Task.sleep(for: .milliseconds(100))
    }
    XCTAssertGreaterThanOrEqual(player.elapsed, 11.5)
    XCTAssertLessThan(player.elapsed, 16)
    XCTAssertNil(player.resumePosition)
  }

}
