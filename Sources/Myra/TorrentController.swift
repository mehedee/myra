#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import Foundation
import UniformTypeIdentifiers

struct TorrentTransfer: Sendable {
  var state = "Paused — ready to resume"
  var downloaded: Int64 = 0
  var total: Int64 = 0
  var downloadSpeed: Int64 = 0
  var uploadSpeed: Int64 = 0
  var peers = 0
  var paused = true
  var completed = false
  var exported = false
  /// Contiguous verified seconds ahead of the playhead, estimated from the read-ahead bitrate.
  var bufferedSeconds: Double = 0
  /// Percent of the streamed video that is verified on disk.
  var streamPercent: Double = 0
}

@MainActor
final class TorrentController: ObservableObject {
  @Published private(set) var jobs: [TorrentJob] = []
  @Published private(set) var transfers: [String: TorrentTransfer] = [:]
  @Published var preferences = TorrentPreferences()
  @Published var error: String?
  @Published var notice: String?
  @Published private(set) var ready = false
  @Published var selectingJob: TorrentIdentity?
  /// Set when a link waits for the one-time networking consent.
  @Published var consentRequest: PendingTorrentLink?
  @Published private(set) var consentGrantedAt: Date?
  @Published private(set) var importing = false
  /// Search indexers (built-in and user-added) and the Search tab's state.
  let indexers: TorrentIndexerStore
  let search: TorrentSearchModel
  /// Selected segment of the Transfers page; kept here so a command can switch it.
  @Published var selectedTab: TorrentsTab = .transfers
  /// Bumped to move keyboard focus into the Search field.
  @Published private(set) var searchFocusToken = 0
  /// Action chosen on a search result. The Add torrent sheet puts it first, then resets it.
  var startIntent: TorrentStartMode = .play
  let consent: TorrentConsent
  private var importTask: Task<Void, Never>?
  private var database: TorrentDatabase?
  private var cache: TorrentCache?
  private var root: URL?
  private var clients: [String: TorrentEngineClient] = [:]
  private var pendingPlay = Set<String>()
  private var streamFiles: [URL: (TorrentIdentity, Int)] = [:]
  private var activePlayback: (TorrentIdentity, Int)?
  private var completion = Set<String>()
  private var retiring = Set<String>()
  /// Download intent for the selected file. In memory only; a paused download that
  /// survives an app restart completes into the cache without an automatic export.
  private var downloadIntent: [String: Int] = [:]
  var playStream: ((GlobalSearchResult, Double?) -> Void)?
  var closePlayer: (() -> Void)?
  var playerAutoplay: (() -> Bool)?
  /// Shared download folder, supplied by the coordinator from the app's download setting.
  var downloadDirectory: (() -> URL?)?
  var storageURL: URL?
  var engineURL: URL?
  // Isolated integration fixtures connect only to this explicitly injected loopback peer.
  var testPeerPort: Int?

  var hasConsent: Bool { consentGrantedAt != nil }

  init(
    consent: TorrentConsent = TorrentConsent(),
    indexers: TorrentIndexerStore = TorrentIndexerStore(),
    searchEngine: TorrentSearchEngine? = nil
  ) {
    self.consent = consent
    self.indexers = indexers
    search = TorrentSearchModel(store: indexers, engine: searchEngine)
    consentGrantedAt = consent.grantedAt
  }

  /// Rows for the Transfers and Downloads screens. Derived only from controller state.
  var transferRows: [TorrentTransferRow] {
    jobs.map { job in
      TorrentTransferRow(
        job: job, transfer: transfers[job.id] ?? TorrentTransfer(),
        isStreaming: activePlayback?.0 == job.identity)
    }
  }

  var streamingRow: TorrentTransferRow? { transferRows.first(where: \.isStreaming) }

  func load() async {
    guard !ready else { return }
    do {
      let directory =
        try storageURL
        ?? AppPersistence.persistentStoreURL().deletingLastPathComponent().appending(
          path: "Torrents")
      try FileManager.default.createDirectory(
        at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
      root = directory.resolvingSymlinksInPath()
      indexers.storageURL = directory.appending(path: "indexers.json")
      indexers.legacyURL = directory.appending(path: "sources.json")
      try indexers.load()
      let database = try TorrentDatabase(url: directory.appending(path: "torrents.sqlite"))
      self.database = database
      preferences = try await database.preferences()
      jobs = try await database.jobs()
      cache = try TorrentCache(
        root: directory.appending(path: "Cache"), limitBytes: preferences.cacheLimitBytes)
      // Restart paused: recover disk usage and Keep Download protections before
      // allowing transfers or cleanup. No public swarm is contacted on launch.
      for job in jobs {
        await cache?.protect(job.identity, active: false, retained: job.keepDownload)
        transfers[job.id] = TorrentTransfer()
      }
      ready = true
    } catch { self.error = error.localizedDescription }
  }

  /// Magnet and .torrent links from paste, Finder, a browser, or drag and drop share this route.
  func handleOpenURL(_ url: URL) {
    switch TorrentLinkRouter.route(url) {
    case .magnet(let value):
      Task { await load(); await addMagnet(value) }
    case .torrentFile(let file):
      Task { await load(); await importTorrentFile(file) }
    case .ignored:
      break
    }
  }

  func addMagnet(_ value: String) async {
    do {
      guard ready else { throw TorrentError.database("Torrent storage is not ready.") }
      let text = value.trimmingCharacters(in: .whitespacesAndNewlines)
      let identity = try TorrentIdentity(magnet: text)
      guard hasConsent else {
        consentRequest = .magnet(text)
        return
      }
      if let job = jobs.first(where: { $0.identity == identity }) {
        selectingJob = identity
        if job.files.isEmpty { try await start(job) }
        return
      }
      let name = TorrentLinkRouter.displayName(magnet: text) ?? "Magnet link"
      let job = TorrentJob(identity: identity, name: name, source: .magnet(text), files: [])
      try await database?.save(job)
      jobs.insert(job, at: 0)
      selectingJob = identity
      try await start(job)
    } catch { self.error = error.localizedDescription }
  }

  func chooseTorrentFile() {
    #if os(iOS)
    error = TorrentEngineClient.unavailableMessage
    #else
    let panel = NSOpenPanel()
    panel.allowedContentTypes = [UTType(filenameExtension: "torrent") ?? .data]
    panel.allowsMultipleSelection = false
    panel.canChooseDirectories = false
    panel.begin { [weak self] response in
      guard response == .OK, let url = panel.url else { return }
      Task { @MainActor in await self?.importTorrentFile(url) }
    }
    #endif
  }

  func importTorrentFile(_ url: URL) async {
    do {
      let data = try await Task.detached { () -> Data in
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        guard url.pathExtension.lowercased() == "torrent",
          try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max <= 16 * 1024 * 1024
        else { throw TorrentError.unsafePath }
        return try Data(contentsOf: url)
      }.value
      await importTorrent(data)
    } catch { self.error = error.localizedDescription }
  }

  func importTorrent(_ data: Data) async {
    do {
      guard data.count <= 16 * 1024 * 1024, let root else { throw TorrentError.unsafePath }
      guard hasConsent else {
        consentRequest = .torrentData(data)
        return
      }
      let imports = root.appending(path: "Imports")
      try FileManager.default.createDirectory(
        at: imports, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
      let temporary = imports.appending(path: UUID().uuidString + ".torrent")
      try data.write(to: temporary, options: .atomic)
      defer { try? FileManager.default.removeItem(at: temporary) }
      let event = try await TorrentEngineClient.inspect(
        file: temporary, executable: engineURL ?? TorrentEngineClient.executableURL())
      let identity = try TorrentIdentity(rawValue: event.identity ?? "")
      let filename = identity.rawValue + ".torrent"
      try data.write(to: imports.appending(path: filename), options: .atomic)
      if jobs.contains(where: { $0.identity == identity }) {
        selectingJob = identity
        return
      }
      let job = TorrentJob(
        identity: identity, name: event.name ?? "Torrent",
        source: .importedTorrent(filename: filename), files: event.files ?? [])
      try await database?.save(job)
      jobs.insert(job, at: 0)
      transfers[job.id] = TorrentTransfer()
      selectingJob = identity
    } catch { self.error = error.localizedDescription }
  }

  private func start(_ job: TorrentJob) async throws {
    guard hasConsent else { throw TorrentError.consentRequired }
    guard !retiring.contains(job.id) else {
      throw TorrentError.database("Wait for this torrent to finish stopping.")
    }
    if clients[job.id]?.isRunning == true { return }
    guard clients.count < 3 else {
      throw TorrentError.database(
        "Pause or cancel another torrent before starting more than three transfers.")
    }
    guard let cache, let root else { throw TorrentError.database("Torrent storage is not ready.") }
    // Metadata resolution reserves a small control-file budget; payload stays disabled.
    let folder = try await cache.reserve(job.identity, bytes: 16 * 1024 * 1024)
    await cache.protect(job.identity, active: true, retained: job.keepDownload)
    let client = TorrentEngineClient()
    client.receive = { [weak self, weak client] event in
      guard let self, let client, self.clients[job.id] === client else { return }
      Task { @MainActor [weak self, weak client] in
        guard let self, let client, self.clients[job.id] === client else { return }
        await self.receive(event, identity: job.identity)
      }
    }
    client.failed = { [weak self, weak client] message in
      guard let self, let client, self.clients[job.id] === client else { return }
      self.error = message
      self.cancel(job.identity)
    }
    let input: String
    switch job.source {
    case .magnet(let value): input = value
    case .importedTorrent(let filename):
      guard filename == job.id + ".torrent" else { throw TorrentError.unsafePath }
      input = root.appending(path: "Imports/" + filename).path
    }
    clients[job.id] = client
    do {
      try client.start(
        source: input, cache: folder, executable: engineURL, testPeerPort: testPeerPort)
      try client.command(
        "preferences",
        values: ["seed": preferences.seedAfterCompletion, "uploadKB": preferences.uploadLimitKB])
      transfers[job.id] = TorrentTransfer(state: "Resolving metadata…", paused: false)
    } catch {
      clients.removeValue(forKey: job.id)
      client.stop()
      await cache.protect(job.identity, active: false, retained: job.keepDownload)
      throw error
    }
  }

  /// Play starts the stream as soon as the engine answers. Download keeps the file in the
  /// cache and copies it to the shared download folder when it completes.
  @discardableResult
  func select(
    _ identity: TorrentIdentity, file: TorrentFile, play: Bool, download: Bool = false
  ) async -> Bool {
    do {
      guard file.isPlayable, let index = jobs.firstIndex(where: { $0.identity == identity }),
        jobs[index].files.contains(file), let cache
      else { throw TorrentError.unsafePath }
      try await start(jobs[index])
      // Existing bytes plus the full selected file and two boundary pieces are
      // conservatively reserved. A quota failure never silently evicts a kept job.
      _ = try await cache.reserveVideo(identity, file: file)
      guard let index = jobs.firstIndex(where: { $0.identity == identity }),
        let client = clients[identity.rawValue]
      else { return false }
      jobs[index].selectedFileIndex = file.index
      if download {
        jobs[index].keepDownload = true
        downloadIntent[identity.rawValue] = file.index
      } else if play {
        downloadIntent.removeValue(forKey: identity.rawValue)
      }
      try await database?.save(jobs[index])
      guard clients[identity.rawValue] === client else { return false }
      if download { await cache.protect(identity, active: true, retained: true) }
      if play { pendingPlay.insert(identity.rawValue) }
      try clients[identity.rawValue]?.command("select", values: ["index": file.index])
      selectingJob = nil
      completion.remove(identity.fileKey(index: file.index))
      return true
    } catch {
      self.error = error.localizedDescription
      return false
    }
  }

  private func stateText(_ transfer: TorrentTransfer, identity: TorrentIdentity) -> String {
    if transfer.exported { return "Saved to Downloads" }
    if transfer.completed {
      return preferences.seedAfterCompletion && !transfer.paused ? "Seeding" : "Completed"
    }
    if transfer.paused { return "Paused" }
    if transfer.total <= 0 { return "Resolving metadata…" }
    if activePlayback?.0 == identity && transfer.bufferedSeconds < 5 {
      return "Buffering · \(Int(transfer.bufferedSeconds.rounded())) s ahead"
    }
    return "Downloading"
  }

  private func receive(_ event: TorrentEngineEvent, identity: TorrentIdentity) async {
    guard let index = jobs.firstIndex(where: { $0.identity == identity }) else { return }
    switch event.event {
    case "metadata":
      do {
        guard event.identity == identity.rawValue, let files = event.files,
          files.allSatisfy(\.isPlayable)
        else { throw TorrentError.unsafePath }
        jobs[index].files = files
        jobs[index].name = event.name ?? jobs[index].name
        try await database?.save(jobs[index])
        transfers[identity.rawValue]?.state = "Choose a video"
      } catch {
        self.error = error.localizedDescription
        cancel(identity)
      }
    case "status":
      var transfer = transfers[identity.rawValue] ?? TorrentTransfer()
      transfer.downloaded = event.downloaded ?? 0
      transfer.total = event.total ?? 0
      transfer.downloadSpeed = event.downloadSpeed ?? 0
      transfer.uploadSpeed = event.uploadSpeed ?? 0
      transfer.peers = event.peers ?? 0
      transfer.paused = event.paused ?? false
      transfer.state = stateText(transfer, identity: identity)
      transfers[identity.rawValue] = transfer
    case "buffer":
      var transfer = transfers[identity.rawValue] ?? TorrentTransfer()
      transfer.bufferedSeconds = event.bufferedSeconds ?? 0
      transfer.streamPercent = event.percent ?? 0
      transfer.peers = event.peers ?? transfer.peers
      transfer.downloadSpeed = event.downloadSpeed ?? transfer.downloadSpeed
      transfer.state = stateText(transfer, identity: identity)
      transfers[identity.rawValue] = transfer
    case "stream":
      guard let url = event.url, url.scheme == "http", url.host == "127.0.0.1",
        let fileIndex = event.index,
        let file = jobs[index].files.first(where: { $0.index == fileIndex })
      else { return }
      streamFiles[url] = (identity, fileIndex)
      if pendingPlay.remove(identity.rawValue) != nil {
        activePlayback = (identity, fileIndex)
        let entry = DirectoryEntry(name: file.name, url: url, kind: .file, size: file.size)
        let media = GlobalSearchResult(
          categoryID: UUID(), categoryName: "Torrents",
          categoryRoot: url.deletingLastPathComponent(), entry: entry, relativePath: file.path,
          artworkURL: nil)
        let progress = try? await database?.progress(identity: identity, fileIndex: fileIndex)
        guard clients[identity.rawValue] != nil, activePlayback?.0 == identity,
          activePlayback?.1 == fileIndex
        else { return }
        playStream?(media, progress?.seconds ?? 0)
      }
    case "complete":
      transfers[identity.rawValue]?.completed = true
      notice = "Download completed: \(jobs[index].name)"
      if let fileIndex = jobs[index].selectedFileIndex,
        downloadIntent[identity.rawValue] == fileIndex
      {
        await exportDownload(identity, fileIndex: fileIndex)
      }
    case "error":
      error = event.message ?? "Torrent operation failed."
      cancel(identity)
    default: break
    }
  }

  private func exportDownload(_ identity: TorrentIdentity, fileIndex: Int) async {
    guard let job = jobs.first(where: { $0.identity == identity }),
      let file = job.files.first(where: { $0.index == fileIndex }), let cache,
      let destinationRoot = downloadDirectory?()
    else {
      error = "Choose a download folder in Settings before downloading a torrent."
      return
    }
    do {
      let source = cache.root.appending(path: identity.rawValue).appending(path: file.path)
      let relative = TorrentExportPath.relativePath(jobName: job.name, file: file)
      let destination = try DestinationSafety.destination(
        root: destinationRoot, relativePath: relative)
      let saved = try await Task.detached {
        try Self.copyUnique(source, to: destination)
      }.value
      transfers[identity.rawValue]?.exported = true
      transfers[identity.rawValue]?.state = "Saved to Downloads"
      notice = "Saved \(saved.lastPathComponent) to Downloads."
    } catch { self.error = error.localizedDescription }
  }

  /// Never overwrites an existing download: a numbered name is chosen instead.
  nonisolated static func copyUnique(_ source: URL, to destination: URL) throws -> URL {
    let manager = FileManager.default
    try manager.createDirectory(
      at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
    let base = destination.deletingPathExtension().lastPathComponent
    let extensionSuffix = destination.pathExtension.isEmpty ? "" : "." + destination.pathExtension
    var target = destination
    var counter = 2
    while manager.fileExists(atPath: target.path) {
      target = destination.deletingLastPathComponent().appending(
        path: "\(base) \(counter)\(extensionSuffix)")
      counter += 1
    }
    try manager.copyItem(at: source, to: target)
    return target
  }

  func pause(_ identity: TorrentIdentity) {
    do {
      try clients[identity.rawValue]?.command("pause")
      transfers[identity.rawValue]?.paused = true
    } catch { self.error = error.localizedDescription }
  }

  func resume(_ identity: TorrentIdentity) async {
    guard let job = jobs.first(where: { $0.identity == identity }) else { return }
    do {
      if clients[job.id]?.isRunning == true {
        try clients[job.id]?.command("resume")
      } else if let file = job.files.first(where: { $0.index == job.selectedFileIndex }) {
        await select(identity, file: file, play: false)
      } else {
        try await start(job)
        selectingJob = identity
      }
    } catch { self.error = error.localizedDescription }
  }

  func cancel(_ identity: TorrentIdentity) {
    if activePlayback?.0 == identity {
      closePlayer?()
      playerClosed()
    }
    let client = clients.removeValue(forKey: identity.rawValue)
    client?.stop()
    retiring.insert(identity.rawValue)
    pendingPlay.remove(identity.rawValue)
    transfers[identity.rawValue]?.state = "Cancelled — cached files retained"
    transfers[identity.rawValue]?.paused = true
    Task {
      await client?.waitForExit()
      retiring.remove(identity.rawValue)
      await cache?.protect(
        identity, active: false,
        retained: jobs.first(where: { $0.identity == identity })?.keepDownload ?? false)
    }
  }

  func setKeep(_ identity: TorrentIdentity, keep: Bool) async {
    guard let index = jobs.firstIndex(where: { $0.identity == identity }) else { return }
    do {
      jobs[index].keepDownload = keep
      try await database?.save(jobs[index])
      await cache?.protect(
        identity, active: clients[identity.rawValue] != nil || retiring.contains(identity.rawValue),
        retained: keep)
    } catch { self.error = error.localizedDescription }
  }

  func remove(_ identity: TorrentIdentity) async {
    do {
      guard clients[identity.rawValue] == nil, !retiring.contains(identity.rawValue),
        activePlayback?.0 != identity,
        jobs.first(where: { $0.identity == identity })?.keepDownload != true
      else { throw TorrentError.protectedDownload }
      try await cache?.remove(identity)
      try await database?.remove(identity)
      jobs.removeAll { $0.identity == identity }
      transfers.removeValue(forKey: identity.rawValue)
    } catch { self.error = error.localizedDescription }
  }

  func savePreferences() async {
    do {
      let validated = try preferences.validated()
      try await cache?.setLimit(bytes: validated.cacheLimitBytes)
      try await database?.savePreferences(validated)
      for client in clients.values {
        try client.command(
          "preferences",
          values: ["seed": validated.seedAfterCompletion, "uploadKB": validated.uploadLimitKB])
      }
      notice = "Torrent settings saved."
    } catch { self.error = error.localizedDescription }
  }

  func isTorrent(_ media: GlobalSearchResult) -> Bool { streamFiles[media.entry.url] != nil }

  func record(_ media: GlobalSearchResult, seconds: Double, duration: Double) {
    guard let (identity, index) = streamFiles[media.entry.url] else { return }
    guard !completion.contains(identity.fileKey(index: index)) else { return }
    Task {
      try? await database?.saveProgress(
        identity: identity, fileIndex: index, seconds: seconds, duration: duration)
    }
  }

  func ended(_ media: GlobalSearchResult) -> Bool {
    guard let (identity, index) = streamFiles[media.entry.url] else { return false }
    completion.insert(identity.fileKey(index: index))
    Task {
      try? await database?.saveProgress(
        identity: identity, fileIndex: index, seconds: 0, duration: 0)
    }
    guard playerAutoplay?() != false, let job = jobs.first(where: { $0.identity == identity })
    else { return false }
    let files = TorrentFile.playbackOrder(job.files)
    guard let current = files.firstIndex(where: { $0.index == index }),
      files.indices.contains(current + 1)
    else { return false }
    // The next episode takes over this playback, so closing this one must not forget the torrent.
    activePlayback = nil
    Task { if !(await select(identity, file: files[current + 1], play: true)) { closePlayer?() } }
    return true
  }

  func playerClosed() {
    guard let playback = activePlayback else { return }
    let identity = playback.0
    activePlayback = nil
    try? clients[identity.rawValue]?.command("deselect")
    let keep = jobs.first(where: { $0.identity == identity })?.keepDownload == true
    if !keep, downloadIntent[identity.rawValue] == nil { forgetAfterWatching(identity) }
  }

  /// "Keep after watching" off: stop the engine and delete this torrent's cache once it has exited.
  private func forgetAfterWatching(_ identity: TorrentIdentity) {
    cancel(identity)
    Task { [weak self] in
      guard let self else { return }
      while self.retiring.contains(identity.rawValue) {
        try? await Task.sleep(for: .milliseconds(100))
      }
      guard self.activePlayback?.0 != identity, !self.pendingPlay.contains(identity.rawValue),
        self.clients[identity.rawValue] == nil,
        self.jobs.first(where: { $0.identity == identity })?.keepDownload != true,
        self.downloadIntent[identity.rawValue] == nil
      else { return }
      do {
        await self.cache?.protect(identity, active: false, retained: false)
        try await self.cache?.remove(identity)
        try await self.database?.remove(identity)
        self.jobs.removeAll { $0.identity == identity }
        self.transfers.removeValue(forKey: identity.rawValue)
      } catch { self.error = error.localizedDescription }
    }
  }

  // MARK: Consent

  /// Records the one-time consent, then continues the link that asked for it.
  func grantConsent() {
    consent.grant()
    consentGrantedAt = consent.grantedAt
    guard let pending = consentRequest else { return }
    consentRequest = nil
    switch pending {
    case .magnet(let value): Task { await addMagnet(value) }
    case .torrentData(let data): Task { await importTorrent(data) }
    }
  }

  func declineConsent() {
    consentRequest = nil
  }

  /// Withdrawing consent stops every running transfer. Cached files are kept until removed.
  func withdrawConsent() {
    consent.withdraw()
    consentGrantedAt = nil
    for job in jobs where clients[job.id] != nil { cancel(job.identity) }
  }

  /// Switches Transfers to the Search tab and focuses the query field ("Search Torrents", shift-cmd-T).
  func showSearch() {
    selectedTab = .search
    searchFocusToken += 1
  }

  /// Play Now or Download on a search result. Magnets (or an info hash) go straight to the
  /// Add torrent sheet; otherwise the torrent file is fetched first. The sheet opens with
  /// the chosen action first.
  func start(_ result: TorrentSearchResult, mode: TorrentStartMode) {
    cancelImport()
    startIntent = mode
    importing = true
    importTask = Task {
      defer { importing = false }
      do {
        if let magnet = result.shareableMagnet {
          await addMagnet(magnet)
          return
        }
        let url: URL
        if let id = result.archiveID {
          url = try await TorrentDiscoveryService.archiveTorrent(id)
        } else if let torrentURL = result.torrentURL {
          url = torrentURL
        } else {
          throw TorrentError.invalidIdentity
        }
        let data = try await TorrentDiscoveryService.fetch(url, limit: 16 * 1024 * 1024)
        try Task.checkCancellation()
        await importTorrent(data)
      } catch {
        if !Task.isCancelled {
          self.error = "Could not import this torrent. Check the source and try again."
        }
      }
    }
  }

  func cancelImport() {
    importTask?.cancel()
    importTask = nil
    importing = false
  }

  func shutdown() {
    cancelImport()
    search.cancel()
    for client in clients.values { client.stop() }
    clients.removeAll()
  }
}
