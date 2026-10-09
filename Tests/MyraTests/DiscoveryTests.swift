import CSQLite
import Sparkle
import XCTest

@testable import Myra

final class DiscoveryTests: XCTestCase {
  @MainActor
  func testNativeUpdaterStartsForIsolatedAppWithTrustedConfiguration() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-updater-start-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let contents = directory.appending(path: "Fixture.app/Contents")
    let executableDirectory = contents.appending(path: "MacOS")
    try FileManager.default.createDirectory(
      at: executableDirectory, withIntermediateDirectories: true)
    let configURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
      .deletingLastPathComponent().deletingLastPathComponent().appending(
        path: "Config/Myra-Info.plist")
    var info = try XCTUnwrap(
      try PropertyListSerialization.propertyList(from: Data(contentsOf: configURL), format: nil)
        as? [String: Any])
    let domain = "com.mehedee.Myra.StartTest.\(UUID().uuidString)"
    defer { UserDefaults(suiteName: domain)?.removePersistentDomain(forName: domain) }
    info["CFBundleIdentifier"] = domain
    info["CFBundleName"] = "Myra updater fixture"
    info["CFBundlePackageType"] = "APPL"
    info["CFBundleExecutable"] = "Fixture"
    info["CFBundleVersion"] = "10"
    info["CFBundleShortVersionString"] = "3.0.2"
    info["SUEnableAutomaticChecks"] = false
    try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0).write(
      to: contents.appending(path: "Info.plist"))
    try FileManager.default.copyItem(
      at: URL(fileURLWithPath: "/usr/bin/true"), to: executableDirectory.appending(path: "Fixture"))
    let bundle = try XCTUnwrap(Bundle(url: contents.deletingLastPathComponent()))
    let driver = SPUStandardUserDriver(hostBundle: bundle, delegate: nil)
    let updater = SPUUpdater(
      hostBundle: bundle, applicationBundle: bundle, userDriver: driver, delegate: nil)
    try updater.start()
    XCTAssertTrue(updater.canCheckForUpdates)
    XCTAssertFalse(updater.automaticallyChecksForUpdates, "Fixture must not contact the network")
    XCTAssertFalse(updater.automaticallyDownloadsUpdates)
    XCTAssertEqual(updater.updateCheckInterval, 86400)
  }
  @MainActor
  func testUpdaterPreferencesPersistAndDisableUnattendedInstallation() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-update-prefs-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let contents = directory.appending(path: "Fixture.app/Contents")
    try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
    let domain = "com.mehedee.Myra.UpdateTest.\(UUID().uuidString)"
    defer { UserDefaults(suiteName: domain)?.removePersistentDomain(forName: domain) }
    let info: [String: Any] = [
      "CFBundleIdentifier": domain, "CFBundlePackageType": "APPL", "CFBundleVersion": "10",
      "SUEnableAutomaticChecks": true, "SUScheduledCheckInterval": 86400,
      "SUAutomaticallyUpdate": false, "SUAllowsAutomaticUpdates": false, "SUSendProfileInfo": false,
    ]
    try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0).write(
      to: contents.appending(path: "Info.plist"))
    let bundle = try XCTUnwrap(Bundle(url: contents.deletingLastPathComponent()))
    let settings = SPUUpdaterSettings(hostBundle: bundle)
    XCTAssertTrue(settings.automaticallyChecksForUpdates)
    XCTAssertEqual(settings.updateCheckInterval, 86400)
    XCTAssertFalse(settings.automaticallyDownloadsUpdates)
    XCTAssertFalse(settings.allowsAutomaticUpdates)
    XCTAssertFalse(settings.sendsSystemProfile)
    settings.automaticallyChecksForUpdates = false
    XCTAssertFalse(SPUUpdaterSettings(hostBundle: bundle).automaticallyChecksForUpdates)
    settings.automaticallyChecksForUpdates = true
    XCTAssertTrue(SPUUpdaterSettings(hostBundle: bundle).automaticallyChecksForUpdates)
    XCTAssertFalse(SPUUpdaterSettings(hostBundle: bundle).allowsAutomaticUpdates)
  }

  @MainActor
  func testClearPreservesWatchedUnfinishedHistory() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-clear-watched-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let index = try LibraryIndex(url: directory.appending(path: "index.sqlite"))
    let media = version("Watched.2024.mkv").media
    try await index.upsert([media], generation: UUID())
    try await index.savePlaybackPosition(url: media.entry.url, seconds: 30, duration: 100)
    let store = EntertainmentStore(
      library: LibraryController(index: index),
      storageURL: directory.appending(path: "personal.json"), automaticEnrichment: false)
    store.recordPlayback(media: media, seconds: 30, duration: 100)
    await store.reload()
    store.toggleWatched(try XCTUnwrap(store.catalogue.first?.id))
    await store.clearContinueWatching()
    XCTAssertNotNil(store.personal.history[media.entry.url.absoluteString])
    let position = try await index.playbackPosition(url: media.entry.url)
    XCTAssertEqual(position, 30)
  }
  func testUpdateConfigurationFailsClosedAndUsesOneSecureFeed() throws {
    let configURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
      .deletingLastPathComponent().deletingLastPathComponent().appending(
        path: "Config/Myra-Info.plist")
    let data = try Data(contentsOf: configURL)
    let info = try XCTUnwrap(
      try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
    XCTAssertNil(UpdateConfiguration.problem(in: info))
    XCTAssertEqual(info["SUFeedURL"] as? String, UpdateConfiguration.feedURL.absoluteString)
    XCTAssertEqual(info["SUScheduledCheckInterval"] as? Int, 86400)
    XCTAssertEqual(info["SUSignedFeedFailureExpirationInterval"] as? Int, 0)
    XCTAssertEqual(info["SUEnableAutomaticChecks"] as? Bool, true)
    XCTAssertEqual(info["SUAutomaticallyUpdate"] as? Bool, false)
    XCTAssertEqual(info["SUSendProfileInfo"] as? Bool, false)
    for (key, value) in [
      ("SUFeedURL", "http://example.com/appcast.xml"),
      ("SUFeedURL", "https://user:secret@example.com/appcast.xml"), ("SUPublicEDKey", "invalid"),
    ] {
      var altered = info
      altered[key] = value
      XCTAssertNotNil(UpdateConfiguration.problem(in: altered))
    }
    for key in [
      "SUVerifyUpdateBeforeExtraction", "SURequireSignedFeed", "SUAllowsAutomaticUpdates",
      "SUSignedFeedFailureExpirationInterval",
    ] {
      var altered = info
      altered.removeValue(forKey: key)
      XCTAssertNotNil(UpdateConfiguration.problem(in: altered))
    }
    XCTAssertNotNil(UpdateConfiguration.problem(in: [:]))
    let comparator = SUStandardVersionComparator()
    XCTAssertEqual(comparator.compareVersion("9", toVersion: "10"), .orderedAscending)
    XCTAssertEqual(comparator.compareVersion("10", toVersion: "10"), .orderedSame)
    XCTAssertEqual(comparator.compareVersion("11", toVersion: "10"), .orderedDescending)
  }

  @MainActor
  func testCorruptPersonalStoreCannotBeCleared() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-clear-corrupt-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let url = directory.appending(path: "personal.json")
    let bytes = Data("not valid personal JSON".utf8)
    try bytes.write(to: url)
    let store = EntertainmentStore(
      library: LibraryController(), storageURL: url, automaticEnrichment: false)
    await store.clearContinueWatching()
    XCTAssertNotNil(store.errorMessage)
    XCTAssertEqual(try Data(contentsOf: url), bytes)
    XCTAssertFalse(store.isClearingContinueWatching)
  }
  @MainActor
  func testClearContinueWatchingPersistsAndPreservesCompletedAndPersonalData() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(path: "Myra-clear-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let indexURL = directory.appending(path: "index.sqlite")
    let personalURL = directory.appending(path: "personal.json")
    let index = try LibraryIndex(url: indexURL)
    let unfinished = version("Movie.2025.mkv").media
    let completed = version("Finished.2024.mkv").media
    let unknownDuration = version("Unknown.2024.mkv").media
    try await index.upsert([unfinished, completed, unknownDuration], generation: UUID())
    let library = LibraryController(index: index)
    let store = EntertainmentStore(
      library: library, storageURL: personalURL, automaticEnrichment: false)
    for (media, seconds, duration) in [
      (unfinished, 30.0, 100.0), (completed, 100.0, 100.0), (unknownDuration, 20.0, 0.0),
    ] {
      store.recordPlayback(media: media, seconds: seconds, duration: duration)
      try await index.savePlaybackPosition(
        url: media.entry.url, seconds: seconds, duration: duration)
    }
    store.toggleWatchlist("keep-me")
    store.toggleWatched("keep-watched")
    store.createCollection(name: "Keep collection")
    await store.reload()
    XCTAssertEqual(store.catalogue.filter { $0.resumeVersion != nil }.count, 2)
    await store.clearContinueWatching()
    XCTAssertFalse(store.catalogue.contains { $0.resumeVersion != nil })
    XCTAssertNotNil(store.personal.history[completed.entry.url.absoluteString])
    XCTAssertTrue(store.personal.watchlist.contains("keep-me"))
    XCTAssertTrue(store.personal.watched.contains("keep-watched"))
    XCTAssertEqual(store.personal.collections.first?.name, "Keep collection")
    store.recordPlayback(media: unfinished, seconds: 40, duration: 100)
    XCTAssertNil(
      store.personal.history[unfinished.entry.url.absoluteString],
      "Active playback must not immediately recreate a cleared entry")
    let reopened = EntertainmentStore(
      library: LibraryController(index: try LibraryIndex(url: indexURL)), storageURL: personalURL,
      automaticEnrichment: false)
    await reopened.reload()
    XCTAssertFalse(reopened.catalogue.contains { $0.resumeVersion != nil })
    store.beginPlayback(unfinished)
    store.recordPlayback(media: unfinished, seconds: 50, duration: 100)
    XCTAssertNotNil(store.personal.history[unfinished.entry.url.absoluteString])
  }

  func testSearchShelfVisibility() {
    XCTAssertTrue(HomeShelfPresentation.isVisible(count: 0, query: ""))
    XCTAssertTrue(HomeShelfPresentation.isVisible(count: 0, query: "  "))
    XCTAssertFalse(HomeShelfPresentation.isVisible(count: 0, query: "matrix"))
    XCTAssertTrue(HomeShelfPresentation.isVisible(count: 1, query: "matrix"))
  }
  private func version(_ filename: String, folder: String = "Movies") -> EntertainmentVersion {
    let root = URL(string: "https://media.example/")!
    let url = root.appending(path: "\(folder)/\(filename)")
    return EntertainmentVersion(
      media: GlobalSearchResult(
        categoryID: UUID(), categoryName: "Movies", categoryRoot: root,
        entry: DirectoryEntry(name: filename, url: url, kind: .file),
        relativePath: "\(folder)/\(filename)", artworkURL: nil),
      firstDiscovered: Date(timeIntervalSince1970: 100))
  }
  func testGroupsYearAndQualityVariantsButNotDifferentMovies() {
    let grouped = EntertainmentGrouping.group([
      version("Movie.2025.1080p.WEB-DL.mkv"), version("Movie.2025.2160p.BluRay.mkv"),
      version("Movie.1995.1080p.mkv"), version("Other.2025.1080p.mkv"),
    ])
    XCTAssertEqual(grouped.count, 3)
    XCTAssertEqual(
      grouped.first(where: { $0.year == "2025" && $0.name == "Movie" })?.versions.count, 2)
  }
  func testYearlessQualityVariantsOnlyGroupInSameFolder() {
    let values = EntertainmentGrouping.group([
      version("Movie.1080p.mkv"), version("Movie.2160p.mkv"),
      version("Movie.720p.mkv", folder: "Elsewhere"), version("Movie.mkv"),
    ])
    XCTAssertEqual(values.count, 3)
    XCTAssertEqual(values.filter { $0.versions.count == 2 }.count, 1)
  }
  func testEpisodesGroupedIntoOneSeriesWithVersionsAndNaturalOrder() {
    let title = EntertainmentGrouping.group([
      version("Show.S01E10.1080p.mkv"), version("Show.S01E02.1080p.mkv"),
      version("Show.S01E02.2160p.mkv"),
    ]).first!
    XCTAssertEqual(title.kind, .series)
    XCTAssertEqual(title.versions.map(\.episode), [2, 2, 10])
  }
  func testInventoryDiscoveryDatesAndPlaybackSurviveRefreshAndReopen() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-discovery-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let url = directory.appending(path: "index.sqlite")
    let index = try LibraryIndex(url: url)
    let media = version("Movie.2025.mkv").media
    try await index.upsert([media], generation: UUID())
    let first = try await index.inventory()
    try await index.savePlaybackPosition(url: media.entry.url, seconds: 15, duration: 100)
    try await index.upsert([media], generation: UUID())
    let reopened = try LibraryIndex(url: url)
    let after = try await reopened.inventory()
    XCTAssertEqual(first.first?.firstDiscovered, after.first?.firstDiscovered)
    XCTAssertEqual(after.first?.progressSeconds, 15)
  }
  func testV1MigrationBacksUpAndPreservesPlayback() async throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-migration-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let url = directory.appending(path: "index.sqlite")
    var handle: OpaquePointer?
    XCTAssertEqual(sqlite3_open(url.path, &handle), SQLITE_OK)
    let schema = """
      CREATE TABLE videos(category TEXT NOT NULL,root TEXT NOT NULL,category_name TEXT NOT NULL,
        url TEXT NOT NULL,name TEXT NOT NULL,search_name TEXT NOT NULL,relative_path TEXT NOT NULL,
        artwork TEXT,size INTEGER,modified REAL,generation TEXT NOT NULL,PRIMARY KEY(category,url));
      CREATE TABLE playback(url TEXT PRIMARY KEY,seconds REAL NOT NULL,duration REAL NOT NULL,updated REAL NOT NULL);
      INSERT INTO playback VALUES('https://media.example/Movie.mkv',20,100,100);
      PRAGMA user_version=1;
      """
    XCTAssertEqual(sqlite3_exec(handle, schema, nil, nil, nil), SQLITE_OK)
    sqlite3_close(handle)
    let migrated = try LibraryIndex(url: url)
    let position = try await migrated.playbackPosition(
      url: URL(string: "https://media.example/Movie.mkv")!)
    XCTAssertEqual(position, 20)
    XCTAssertTrue(
      FileManager.default.fileExists(atPath: url.appendingPathExtension("v1-backup").path))
    _ = try await migrated.inventory()
  }
  @MainActor
  func testPersonalArchivePersistsAndRejectsMalformedSources() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-personal-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let storage = directory.appending(path: "personal.json")
    let store = EntertainmentStore(library: LibraryController(), storageURL: storage)
    store.toggleWatchlist("movie|matrix|1999")
    store.createCollection(name: "Weekend")
    let reopened = EntertainmentStore(library: LibraryController(), storageURL: storage)
    XCTAssertTrue(reopened.personal.watchlist.contains("movie|matrix|1999"))
    XCTAssertEqual(reopened.personal.collections.first?.name, "Weekend")
    let data = try reopened.exportArchive(sources: [], appPreferences: nil)
    var archive = try reopened.previewImport(data)
    archive.sources = [
      .init(id: UUID(), name: "Unsafe", url: "https://user:password@media.example/")
    ]
    XCTAssertThrowsError(try reopened.previewImport(JSONEncoder().encode(archive)))
    archive.sources = []
    archive.schemaVersion = 999
    XCTAssertThrowsError(try reopened.previewImport(JSONEncoder().encode(archive)))
  }
  @MainActor
  func testExportRemovesURLCredentialsAndQueryTokensFromHistoryAndTitleIDs() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-portable-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = EntertainmentStore(
      library: LibraryController(), storageURL: directory.appending(path: "personal.json"))
    let url = URL(string: "https://username:password@media.example/Movie.mkv?token=secret#part")!
    let media = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Movies",
      categoryRoot: URL(string: "https://media.example/")!,
      entry: DirectoryEntry(name: "Movie.mkv", url: url, kind: .file), relativePath: "Movie.mkv",
      artworkURL: nil)
    store.recordPlayback(media: media, seconds: 10, duration: 100)
    store.toggleWatchlist("file|" + url.absoluteString)
    let output = try store.exportArchive(sources: [], appPreferences: nil)
    let text = String(decoding: output, as: UTF8.self)
    XCTAssertFalse(text.contains("password"))
    XCTAssertFalse(text.contains("secret"))
    let imported = try store.previewImport(output)
    XCTAssertTrue(imported.personal.watchlist.contains("file|https://media.example/Movie.mkv"))
    XCTAssertNotNil(imported.personal.history["https://media.example/Movie.mkv"])
  }
  @MainActor
  func testCorruptPersonalFileIsPreserved() throws {
    let directory = FileManager.default.temporaryDirectory.appending(
      path: "Myra-corrupt-\(UUID())")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let url = directory.appending(path: "personal.json")
    let original = Data("not a valid json document".utf8)
    try original.write(to: url)
    let store = EntertainmentStore(library: LibraryController(), storageURL: url)
    store.toggleWatched("movie")
    XCTAssertEqual(try Data(contentsOf: url), original)
    XCTAssertNotNil(store.errorMessage)
  }
}
