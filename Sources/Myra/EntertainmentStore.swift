import Combine
import Foundation
import OSLog
import UserNotifications

struct EntertainmentSource: Codable, Identifiable, Sendable {
  var id: UUID
  var name: String
  var url: String
}

struct EntertainmentArchive: Codable, Sendable {
  var schemaVersion: Int = 1
  var personal: EntertainmentPersonalData
  var sources: [EntertainmentSource]
  var appPreferences: EntertainmentAppPreferences?
  var playerState: PlayerPersonalState?
}

@MainActor
final class EntertainmentStore: ObservableObject {
  private(set) var personalChangeRevision = 0
  @Published private(set) var projectionRevision = 0
  @Published private(set) var catalogue: [EntertainmentTitle] = [] {
    didSet { projectionRevision &+= 1 }
  }
  @Published private(set) var personal = EntertainmentPersonalData() {
    didSet {
      projectionRevision &+= 1
      personalChangeRevision &+= 1
    }
  }
  @Published private(set) var newEpisodeIDs: Set<String> = [] {
    didSet { projectionRevision &+= 1 }
  }
  @Published private(set) var isEnriching = false
  @Published var errorMessage: String?
  private let library: LibraryController
  private let url: URL?
  private let service = DiscoveryService()
  private let automaticEnrichment: Bool
  private var canWrite = true
  private var enrichmentIDs: Set<String> = []
  private let catalogueWorker = EntertainmentCatalogueWorker()
  private var loading = false
  private var reloadAgain = false
  private var reloadWaiters: [CheckedContinuation<Void, Never>] = []

  init(library: LibraryController, storageURL: URL? = nil, automaticEnrichment: Bool = true) {
    self.automaticEnrichment = automaticEnrichment
    self.library = library
    self.url =
      storageURL
      ?? (try? AppPersistence.persistentStoreURL().deletingLastPathComponent().appending(
        path: "EntertainmentPersonal.json"))
    if let url, FileManager.default.fileExists(atPath: url.path) {
      do {
        let loaded = try JSONDecoder().decode(
          EntertainmentPersonalData.self, from: Data(contentsOf: url))
        try Self.validate(loaded)
        personal = loaded
      } catch {
        canWrite = false
        errorMessage =
          "Personal library could not be loaded. The existing file has been preserved: \(error.localizedDescription)"
      }
    }
  }

  func reload() async {
    if loading {
      reloadAgain = true
      await withCheckedContinuation { reloadWaiters.append($0) }
      return
    }
    loading = true
    repeat {
      reloadAgain = false
      do {
        let index = try await library.database()
        let prepared = try await catalogueWorker.prepare(index: index, personal: personal)
        if reloadAgain { continue }
        var values = prepared.titles
        // Playback may advance while the worker is preparing a catalogue. Merge the latest records.
        for title in values.indices {
          for version in values[title].versions.indices {
            if let record = personal.history[values[title].versions[version].id] {
              values[title].versions[version].progressSeconds = record.seconds
              values[title].versions[version].duration = record.duration
              values[title].versions[version].lastPlayed = record.updated
            }
          }
        }
        enrichmentIDs = prepared.enrichmentIDs
        catalogue = values
        Task(priority: .utility) { try? await index.saveSearchAliases(values) }
        discoverEpisodes()
      } catch { errorMessage = error.localizedDescription }
    } while reloadAgain
    loading = false
    let waiters = reloadWaiters
    reloadWaiters.removeAll()
    for waiter in waiters { waiter.resume() }
    if automaticEnrichment, !enrichmentIDs.isEmpty, (try? TMDBKeyStore.read().isEmpty) == false {
      await enrich()
    }
  }

  func enrich() async {
    guard !isEnriching, !library.isRefreshing else { return }
    isEnriching = true
    defer { isEnriching = false }
    do {
      let token = try TMDBKeyStore.read()
      guard !token.isEmpty else { throw DiscoveryError.missingToken }
      let index = try await library.database()
      // A bounded batch avoids hammering providers when a large source is first indexed.
      let targets = Array(catalogue.filter { enrichmentIDs.contains($0.id) }.prefix(50))
      var uncertain = 0
      for target in targets {
        while library.isRefreshing {
          try Task.checkCancellation()
          try await Task.sleep(for: .milliseconds(500))
        }
        try Task.checkCancellation()
        do {
          let correction = target.versions.first.flatMap { personal.matchCorrections[$0.id] }
          let metadata = try await service.lookup(
            title: target, correction: correction, token: token, index: index)
          if let offset = catalogue.firstIndex(where: { $0.id == target.id }) {
            catalogue[offset].metadata = metadata
            try await index.saveSearchAliases([catalogue[offset]])
          }
          enrichmentIDs.remove(target.id)
        } catch DiscoveryError.ambiguous {
          uncertain += 1
          let correction = target.versions.first.flatMap { personal.matchCorrections[$0.id] }
          try await index.saveMetadata(
            key: "tmdb-attempt|" + target.metadataCacheKey(providerID: correction?.providerID),
            payload: "ambiguous")
          enrichmentIDs.remove(target.id)
        }
      }
      if uncertain > 0 {
        errorMessage =
          "\(uncertain) titles need a confident match. Use Correct Match. Enrichment processes up to 50 titles per run."
      }
    } catch is CancellationError {} catch { errorMessage = error.localizedDescription }
  }

  func hasNewEpisode(_ version: EntertainmentVersion) -> Bool {
    newEpisodeIDs.contains(Self.episodeID(version))
  }

  func variants(for media: GlobalSearchResult) -> [EntertainmentVersion] {
    guard
      let title = catalogue.first(where: {
        $0.versions.contains(where: { $0.id == media.entry.url.absoluteString })
      }), let selected = title.versions.first(where: { $0.id == media.entry.url.absoluteString })
    else { return [] }
    return title.versions.filter { $0.season == selected.season && $0.episode == selected.episode }
  }

  func toggleWatchlist(_ titleID: String) { mutate { Self.toggle(titleID, in: &$0.watchlist) } }
  func toggleWatched(_ titleID: String) { mutate { Self.toggle(titleID, in: &$0.watched) } }
  func toggleFollow(_ titleID: String) {
    mutate { data in
      Self.toggle(titleID, in: &data.followed)
      if data.followed.contains(titleID), let title = catalogue.first(where: { $0.id == titleID }) {
        data.knownEpisodeIDs.formUnion(title.versions.map(Self.episodeID))
      }
    }
  }
  func createCollection(name: String) {
    let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !name.isEmpty else { return }
    mutate { $0.collections.append(EntertainmentCollection(name: String(name.prefix(200)))) }
  }
  func renameCollection(id: UUID, name: String) {
    let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !name.isEmpty else { return }
    mutate { data in
      if let offset = data.collections.firstIndex(where: { $0.id == id }) {
        data.collections[offset].name = String(name.prefix(200))
      }
    }
  }
  func deleteCollection(id: UUID) { mutate { $0.collections.removeAll { $0.id == id } } }
  func toggleCollection(titleID: String, collectionID: UUID) {
    mutate { data in
      if let offset = data.collections.firstIndex(where: { $0.id == collectionID }) {
        Self.toggle(titleID, in: &data.collections[offset].titleIDs)
      }
    }
  }
  func correctMatch(
    versionID: String, title: String, year: String?, kind: EntertainmentKind, providerID: Int? = nil
  ) {
    let name = title.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !name.isEmpty, name.count <= 300, providerID == nil || providerID! > 0,
      year == nil || year!.range(of: "^(19|20)[0-9]{2}$", options: .regularExpression) != nil
    else {
      errorMessage = "Enter a title, an optional four-digit year, and a positive TMDB ID."
      return
    }
    let previousID = catalogue.first(where: { $0.versions.contains { $0.id == versionID } })?.id
    let normalized = name.folding(
      options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    let nextID = "\(kind.rawValue)|\(normalized)|\(year ?? "")"
    mutate { data in
      data.matchCorrections[versionID] = EntertainmentMatchCorrection(
        title: name, year: year, kind: kind, providerID: providerID)
      if let previousID, previousID != nextID {
        if data.watchlist.remove(previousID) != nil { data.watchlist.insert(nextID) }
        if data.watched.remove(previousID) != nil { data.watched.insert(nextID) }
        if data.followed.remove(previousID) != nil { data.followed.insert(nextID) }
        for offset in data.collections.indices {
          if data.collections[offset].titleIDs.remove(previousID) != nil {
            data.collections[offset].titleIDs.insert(nextID)
          }
        }
      }
    }
    Task { await reload() }
  }
  func recordPlayback(media: GlobalSearchResult, seconds: Double, duration: Double) {
    guard seconds.isFinite, duration.isFinite, seconds >= 0, duration >= 0 else { return }
    let record = EntertainmentPlaybackRecord(seconds: seconds, duration: duration, updated: .now)
    mutate { $0.history[media.entry.url.absoluteString] = record }
    if personal.history[media.entry.url.absoluteString]?.updated == record.updated {
      for titleOffset in catalogue.indices {
        for versionOffset in catalogue[titleOffset].versions.indices
        where catalogue[titleOffset].versions[versionOffset].id == media.entry.url.absoluteString {
          catalogue[titleOffset].versions[versionOffset].progressSeconds = seconds
          catalogue[titleOffset].versions[versionOffset].duration = duration
          catalogue[titleOffset].versions[versionOffset].lastPlayed = record.updated
        }
      }
    }
  }
  func markPlaybackEnded(media: GlobalSearchResult) {
    guard let title = catalogue.first(where: { $0.versions.contains { $0.media.id == media.id } })
    else { return }
    if title.kind == .movie { mutate { $0.watched.insert(title.id) } }
    if var record = personal.history[media.entry.url.absoluteString] {
      record.seconds = record.duration
      record.updated = .now
      recordPlayback(media: media, seconds: record.seconds, duration: record.duration)
    }
  }
  func setNotifications(_ enabled: Bool) {
    if enabled {
      Task {
        do {
          let allowed = try await UNUserNotificationCenter.current().requestAuthorization(options: [
            .alert, .sound,
          ])
          mutate { $0.preferences.notifications = allowed }
          if !allowed { errorMessage = "Notifications are disabled in macOS settings." }
        } catch { errorMessage = error.localizedDescription }
      }
    } else {
      mutate { $0.preferences.notifications = false }
    }
  }

  func exportArchive(sources: [EntertainmentSource], appPreferences: EntertainmentAppPreferences?)
    throws -> Data
  {
    let player = UserDefaults.standard.data(forKey: "Myra.playerPersonalState.v2").flatMap {
      try? JSONDecoder().decode(PlayerPersonalState.self, from: $0)
    }
    var cleaned = personal
    cleaned.watchlist = try Self.portableIDs(cleaned.watchlist)
    cleaned.watched = try Self.portableIDs(cleaned.watched)
    cleaned.followed = try Self.portableIDs(cleaned.followed)
    cleaned.knownEpisodeIDs = try Self.portableIDs(cleaned.knownEpisodeIDs)
    for offset in cleaned.collections.indices {
      cleaned.collections[offset].titleIDs = try Self.portableIDs(
        cleaned.collections[offset].titleIDs)
    }
    cleaned.history = try Self.portableDictionary(cleaned.history)
    cleaned.matchCorrections = try Self.portableDictionary(cleaned.matchCorrections)
    var cleanedPlayer = player
    if let player { cleanedPlayer?.markers = try Self.portableDictionary(player.markers) }
    var cleanedSources = sources
    for offset in cleanedSources.indices {
      cleanedSources[offset].url = try Self.portableID(cleanedSources[offset].url)
    }
    let archive = EntertainmentArchive(
      personal: cleaned, sources: cleanedSources, appPreferences: appPreferences,
      playerState: cleanedPlayer)
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let data = try encoder.encode(archive)
    _ = try previewImport(data)
    return data
  }
  func previewImport(_ data: Data) throws -> EntertainmentArchive {
    guard data.count <= 20_000_000 else {
      throw DiscoveryError.invalidImport("Backup exceeds 20 MB.")
    }
    let archive = try JSONDecoder().decode(EntertainmentArchive.self, from: data)
    guard archive.schemaVersion == 1 else {
      throw DiscoveryError.invalidImport("Unsupported backup version.")
    }
    try Self.validate(archive.personal)
    guard archive.sources.count <= 1000,
      Set(archive.sources.map(\.id)).count == archive.sources.count
    else { throw DiscoveryError.invalidImport("Invalid or duplicate sources.") }
    for source in archive.sources {
      guard !source.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
        source.name.count <= 300,
        let url = URL(string: source.url), ["http", "https"].contains(url.scheme ?? ""),
        url.host != nil,
        url.user == nil, url.password == nil
      else {
        throw DiscoveryError.invalidImport(
          "Sources must use HTTP(S) URLs without embedded credentials.")
      }
      _ = try URLBoundary(root: url)
    }
    if let state = archive.playerState {
      guard state.speed.isFinite, state.speed >= 0.25, state.speed <= 4,
        state.markers.count <= 100_000
      else { throw DiscoveryError.invalidImport("Invalid player preferences.") }
      for markers in state.markers.values {
        for range in [markers.intro, markers.outro].compactMap({ $0 }) {
          guard range.start.isFinite, range.end.isFinite, range.start >= 0, range.end > range.start
          else { throw DiscoveryError.invalidImport("Invalid skip ranges.") }
        }
      }
    }
    return archive
  }
  func applyArchive(_ archive: EntertainmentArchive, replace: Bool) throws {
    _ = try previewImport(JSONEncoder().encode(archive))
    guard canWrite, let url else {
      throw DiscoveryError.invalidImport(
        "The personal library is unavailable; its existing file has been preserved.")
    }
    if FileManager.default.fileExists(atPath: url.path) {
      let backup = url.deletingPathExtension().appendingPathExtension(
        "backup-\(UUID().uuidString).json")
      try FileManager.default.copyItem(at: url, to: backup)
    }
    var result = archive.personal
    if !replace {
      result = personal
      result.watchlist.formUnion(archive.personal.watchlist)
      result.watched.formUnion(archive.personal.watched)
      result.followed.formUnion(archive.personal.followed)
      result.knownEpisodeIDs.formUnion(archive.personal.knownEpisodeIDs)
      for collection in archive.personal.collections {
        if let offset = result.collections.firstIndex(where: { $0.id == collection.id }) {
          result.collections[offset].titleIDs.formUnion(collection.titleIDs)
        } else {
          result.collections.append(collection)
        }
      }
      result.matchCorrections.merge(archive.personal.matchCorrections) { _, new in new }
      result.history.merge(archive.personal.history) { old, new in
        old.updated > new.updated ? old : new
      }
    }
    try Self.validate(result)
    let data = try JSONEncoder().encode(result)
    try FileManager.default.createDirectory(
      at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try data.write(to: url, options: .atomic)
    personal = result
    if var state = archive.playerState {
      if !replace, let oldData = UserDefaults.standard.data(forKey: "Myra.playerPersonalState.v2"),
        let old = try? JSONDecoder().decode(PlayerPersonalState.self, from: oldData)
      {
        state.markers = old.markers.merging(state.markers) { _, new in new }
      }
      let data = try JSONEncoder().encode(state)
      UserDefaults.standard.set(data, forKey: "Myra.playerPersonalState.v2")
    }
  }

  /// Removes URL credentials and transient query tokens from portable identity references.
  private static func portableID(_ value: String) throws -> String {
    let regex = try NSRegularExpression(pattern: "https?://[^|\\s]+", options: [.caseInsensitive])
    var result = value
    let range = NSRange(value.startIndex..<value.endIndex, in: value)
    for match in regex.matches(in: value, range: range).reversed() {
      guard let original = Range(match.range, in: result),
        var components = URLComponents(string: String(result[original]))
      else { throw DiscoveryError.invalidImport("A library URL could not be prepared for export.") }
      components.user = nil
      components.password = nil
      components.query = nil
      components.fragment = nil
      guard let cleaned = components.string else {
        throw DiscoveryError.invalidImport("A library URL could not be prepared for export.")
      }
      result.replaceSubrange(original, with: cleaned)
    }
    return result
  }
  private static func portableIDs(_ values: Set<String>) throws -> Set<String> {
    let result = Set(try values.map(portableID))
    guard result.count == values.count else {
      throw DiscoveryError.invalidImport(
        "Two library references differ only by credentials or query tokens. Resolve duplicate sources before exporting."
      )
    }
    return result
  }
  private static func portableDictionary<Value>(_ values: [String: Value]) throws -> [String: Value]
  {
    var result: [String: Value] = [:]
    for (key, value) in values {
      let portable = try portableID(key)
      guard result[portable] == nil else {
        throw DiscoveryError.invalidImport(
          "Two library references differ only by credentials or query tokens. Resolve duplicate sources before exporting."
        )
      }
      result[portable] = value
    }
    return result
  }

  private static func validate(_ data: EntertainmentPersonalData) throws {
    guard data.schemaVersion == 1, data.collections.count <= 5000, data.history.count <= 100_000,
      data.matchCorrections.count <= 100_000,
      Set(data.collections.map(\.id)).count == data.collections.count,
      data.collections.allSatisfy({ !$0.name.isEmpty && $0.name.count <= 200 }),
      data.history.values.allSatisfy({
        $0.seconds.isFinite && $0.duration.isFinite && $0.seconds >= 0 && $0.duration >= 0
      }),
      data.matchCorrections.values.allSatisfy({
        !$0.title.isEmpty && $0.title.count <= 300 && ($0.providerID == nil || $0.providerID! > 0)
      })
    else { throw DiscoveryError.invalidImport("Invalid personal library data.") }
  }
  /// Applies an explicit reviewed proposal atomically, without altering media files or history.
  func applyReviewedAIAction(_ action: MyraAIAction) throws {
    guard canWrite, let url else { throw MyraAIError.unsafeAction }
    let selected = catalogue.filter { action.titleIDs.contains($0.id) }
    guard selected.count == Set(action.titleIDs).count, !selected.isEmpty else {
      throw MyraAIError.unsafeAction
    }
    var updated = personal
    switch action.kind {
    case .addWatchlist: updated.watchlist.formUnion(action.titleIDs)
    case .markWatched: updated.watched.formUnion(action.titleIDs)
    case .collection:
      guard !action.value.isEmpty, action.value.count <= 200 else { throw MyraAIError.unsafeAction }
      updated.collections.append(.init(name: action.value, titleIDs: Set(action.titleIDs)))
    case .correction:
      let cleanTitle = action.value.trimmingCharacters(in: .whitespacesAndNewlines)
      guard selected.count == 1, !cleanTitle.isEmpty, cleanTitle.count <= 300,
        let kind = action.mediaKind,
        action.year == nil
          || action.year!.range(of: "^(19|20)[0-9]{2}$", options: .regularExpression) != nil
      else { throw MyraAIError.unsafeAction }
      for version in selected[0].versions {
        updated.matchCorrections[version.id] = .init(
          title: cleanTitle, year: action.year ?? selected[0].year, kind: kind, providerID: nil)
      }
      let regrouped = EntertainmentGrouping.group(
        selected[0].versions, corrections: updated.matchCorrections)
      guard regrouped.count == 1, let nextID = regrouped.first?.id else {
        throw MyraAIError.unsafeAction
      }
      let previousID = selected[0].id
      if updated.watchlist.remove(previousID) != nil { updated.watchlist.insert(nextID) }
      if updated.watched.remove(previousID) != nil { updated.watched.insert(nextID) }
      if updated.followed.remove(previousID) != nil { updated.followed.insert(nextID) }
      for index in updated.collections.indices {
        if updated.collections[index].titleIDs.remove(previousID) != nil {
          updated.collections[index].titleIDs.insert(nextID)
        }
      }
    default: throw MyraAIError.unsafeAction
    }
    try Self.validate(updated)
    try JSONEncoder().encode(updated).write(to: url, options: .atomic)
    personal = updated
    if action.kind == .correction { Task { await reload() } }
  }

  func restoreReviewedAISnapshot(_ snapshot: EntertainmentPersonalData) throws {
    guard canWrite, let url else { throw MyraAIError.unsafeAction }
    var updated = snapshot
    // Playback and newly discovered episodes are never rolled back by a personal action.
    updated.history = personal.history
    updated.knownEpisodeIDs = personal.knownEpisodeIDs
    try Self.validate(updated)
    try JSONEncoder().encode(updated).write(to: url, options: .atomic)
    let changedGrouping = !updated.matchCorrections.isEmpty || !personal.matchCorrections.isEmpty
    personal = updated
    if changedGrouping { Task { await reload() } }
  }

  private static func toggle(_ value: String, in set: inout Set<String>) {
    if !set.insert(value).inserted { set.remove(value) }
  }
  private func mutate(_ change: (inout EntertainmentPersonalData) -> Void) {
    guard canWrite, let url else {
      errorMessage = "Personal data is read-only because the existing file could not be loaded."
      return
    }
    var updated = personal
    change(&updated)
    do {
      try Self.validate(updated)
      try FileManager.default.createDirectory(
        at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
      try JSONEncoder().encode(updated).write(to: url, options: .atomic)
      personal = updated
    } catch { errorMessage = error.localizedDescription }
  }
  private static func episodeID(_ version: EntertainmentVersion) -> String {
    let identity = MediaIdentity.parse(filename: version.media.entry.name)
    return identity.cacheKey
  }
  private func discoverEpisodes() {
    var current: Set<String> = []
    for title in catalogue where title.kind == .series && personal.followed.contains(title.id) {
      current.formUnion(title.versions.map(Self.episodeID))
    }
    let new = current.subtracting(personal.knownEpisodeIDs)
    newEpisodeIDs.formUnion(new)
    if !new.isEmpty {
      mutate { $0.knownEpisodeIDs.formUnion(current) }
      if personal.preferences.notifications {
        let content = UNMutableNotificationContent()
        content.title = "New episodes available"
        content.body = "\(new.count) new episode(s) were indexed for series you follow."
        let request = UNNotificationRequest(
          identifier: "Myra.episodes.\(UUID().uuidString)", content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
      }
    }
  }
}

actor EntertainmentCatalogueWorker {
  // Store each source once rather than repeating its name/root in every media version.
  private struct CachedVersion: Codable {
    let source: String
    let url: URL
    let name: String
    let relativePath: String
    let artwork: URL?
    let size: Int64?
    let modified: Date?
    let discovered: Date
    let season: Int?
    let episode: Int?
    let progress: Double
    let duration: Double
    let lastPlayed: Date?
    init(_ version: EntertainmentVersion) {
      let media = version.media
      source = media.categoryID.uuidString
      url = media.entry.url
      name = media.entry.name
      relativePath = media.relativePath
      artwork = media.artworkURL
      size = media.entry.size
      modified = media.entry.modifiedAt
      discovered = version.firstDiscovered
      season = version.season
      episode = version.episode
      progress = version.progressSeconds
      duration = version.duration
      lastPlayed = version.lastPlayed
    }
    func restore(sources: [String: GlobalSearchRoot]) throws -> EntertainmentVersion {
      guard let root = sources[source] else {
        throw LibraryIndexError.database("Incomplete Home snapshot")
      }
      let media = GlobalSearchResult(
        categoryID: root.id, categoryName: root.name, categoryRoot: root.url,
        entry: DirectoryEntry(name: name, url: url, kind: .file, size: size, modifiedAt: modified),
        relativePath: relativePath, artworkURL: artwork)
      return EntertainmentVersion(
        media: media, firstDiscovered: discovered, progressSeconds: progress,
        duration: duration, lastPlayed: lastPlayed, season: season, episode: episode)
    }
  }
  private struct CachedTitle: Codable {
    let id: String
    let name: String
    let year: String?
    let kind: EntertainmentKind
    let metadata: EntertainmentMetadata?
    let versions: [CachedVersion]
    init(_ title: EntertainmentTitle) {
      id = title.id
      name = title.name
      year = title.year
      kind = title.kind
      metadata = title.metadata
      versions = title.versions.map(CachedVersion.init)
    }
    func restore(sources: [String: GlobalSearchRoot]) throws -> EntertainmentTitle {
      EntertainmentTitle(
        id: id, name: name, year: year, kind: kind,
        versions: try versions.map { try $0.restore(sources: sources) }, metadata: metadata)
    }
  }
  private struct Snapshot: Codable {
    var format = 2
    let revision: Int
    let corrections: Data
    let sources: [String: GlobalSearchRoot]
    let titles: [CachedTitle]
    let enrichmentIDs: Set<String>
    let saved: Date
  }
  struct Prepared: Sendable {
    var titles: [EntertainmentTitle]
    let enrichmentIDs: Set<String>
    let metadataQueryCount: Int
  }
  func prepare(index: LibraryIndex, personal: EntertainmentPersonalData) async throws -> Prepared {
    let started = Date.now
    let revision = try await index.revision()
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    let corrections = try encoder.encode(personal.matchCorrections)
    let cacheURL = index.url.appendingPathExtension("home-cache")
    var savedTitles: [EntertainmentTitle]?
    if let data = try? Data(contentsOf: cacheURL),
      let snapshot = try? JSONDecoder().decode(Snapshot.self, from: data), snapshot.format == 2,
      snapshot.revision == revision, snapshot.corrections == corrections,
      let restored = try? snapshot.titles.map({ try $0.restore(sources: snapshot.sources) })
    {
      if Date.now.timeIntervalSince(snapshot.saved) < 86400 {
        return Prepared(
          titles: restored,
          enrichmentIDs: snapshot.enrichmentIDs, metadataQueryCount: 0)
      }
      savedTitles = restored
    }
    var titles: [EntertainmentTitle]
    if let savedTitles {
      titles = savedTitles
    } else {
      let versions = try await index.inventory()
      titles = EntertainmentGrouping.group(versions, corrections: personal.matchCorrections)
    }
    var enrichmentIDs: Set<String> = []
    var queryCount = 0
    for start in stride(from: 0, to: titles.count, by: 200) {
      try Task.checkCancellation()
      let end = min(start + 200, titles.count)
      var keys: [String] = []
      for offset in start..<end {
        let title = titles[offset]
        let correction = title.versions.first.flatMap { personal.matchCorrections[$0.id] }
        let key = title.metadataCacheKey(providerID: correction?.providerID)
        keys += [
          key, "tmdb-base|\(title.id)|\(correction?.providerID ?? 0)", "tmdb-attempt|\(key)",
        ]
      }
      let records = try await index.metadataRecords(keys: keys)
      queryCount += 1
      for offset in start..<end {
        let title = titles[offset]
        let correction = title.versions.first.flatMap { personal.matchCorrections[$0.id] }
        let key = title.metadataCacheKey(providerID: correction?.providerID)
        if let cached = records[key]
          ?? records["tmdb-base|\(title.id)|\(correction?.providerID ?? 0)"]
        {
          titles[offset].metadata = try? JSONDecoder().decode(
            EntertainmentMetadata.self, from: Data(cached.payload.utf8))
        }
        if records[key]?.isFresh() != true && records["tmdb-attempt|\(key)"]?.isFresh() != true {
          enrichmentIDs.insert(title.id)
        }
      }
      await Task.yield()
    }
    Logger(subsystem: "Myra", category: "Catalogue").info(
      "Prepared \(titles.count) titles with \(queryCount) metadata queries in \(Date.now.timeIntervalSince(started)) seconds"
    )
    if try await index.revision() == revision {
      var sources: [String: GlobalSearchRoot] = [:]
      for title in titles {
        for version in title.versions {
          let media = version.media
          sources[media.categoryID.uuidString] = GlobalSearchRoot(
            id: media.categoryID, name: media.categoryName, url: media.categoryRoot)
        }
      }
      let snapshot = Snapshot(
        revision: revision, corrections: corrections, sources: sources,
        titles: titles.map(CachedTitle.init), enrichmentIDs: enrichmentIDs, saved: .now)
      if let data = try? JSONEncoder().encode(snapshot) {
        try? data.write(to: cacheURL, options: .atomic)
      }
    }
    return Prepared(titles: titles, enrichmentIDs: enrichmentIDs, metadataQueryCount: queryCount)
  }
}
