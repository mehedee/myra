import AppKit
import Foundation
import SwiftData

enum DownloadQueueVisibility {
  static func shouldShow(batchCount: Int, itemCount: Int, isPreparing: Bool) -> Bool {
    batchCount > 0 || itemCount > 0 || isPreparing
  }
}

@MainActor
final class AppCoordinator: ObservableObject {
  @Published private(set) var categories: [Category] = []
  @Published private(set) var batches: [DownloadBatch] = []
  @Published private(set) var items: [DownloadItem] = []
  @Published var currentCategory: Category?
  @Published var currentURL: URL?
  @Published var entries: [DirectoryEntry] = []
  @Published var artworkURL: URL?
  @Published var selection = Set<URL>()
  @Published var fuzzyGlobalSearch =
    UserDefaults.standard.object(forKey: "fuzzyGlobalSearch") as? Bool ?? true
  {
    didSet {
      UserDefaults.standard.set(fuzzyGlobalSearch, forKey: "fuzzyGlobalSearch")
      if !activeGlobalQuery.isEmpty { startGlobalSearch(query: activeGlobalQuery) }
    }
  }
  @Published var globalSearchResults: [GlobalSearchResult] = []
  @Published var globalSearchSelection = Set<GlobalSearchResultID>()
  @Published var globalSearchProgress = GlobalSearchProgress()
  @Published var globalSearchFailures: [GlobalSearchFailure] = []
  @Published var globalSearchErrorMessage: String?
  @Published var isGlobalSearching = false
  @Published var isEnqueueingGlobalDownloads = false
  @Published var isLoading = false
  @Published var isScanning = false
  @Published var scanProgress = ScanProgress()
  @Published var errorMessage: String?
  @Published var toastMessage: String?
  @Published var settings: AppSettings?
  @Published var hasMoreGlobalResults = false
  let library: LibraryController
  let inspector = MediaInspectorModel()
  let player = EmbeddedPlayerModel()
  let updater = AppUpdater()
  private let personalStorageURL: URL?
  private let automaticEnrichment: Bool
  lazy var entertainment = EntertainmentStore(
    library: library, storageURL: personalStorageURL, automaticEnrichment: automaticEnrichment)
  @Published var playbackChoices: PlaybackChoices?
  private var playbackChoiceTask: Task<Void, Never>?

  var hasDownloadQueue: Bool {
    DownloadQueueVisibility.shouldShow(
      batchCount: batches.count, itemCount: items.count,
      isPreparing: isScanning || isEnqueueingGlobalDownloads)
  }

  private var modelContext: ModelContext?
  private let directoryService = DirectoryService()
  private lazy var planner = DownloadPlanner(directoryService: directoryService)
  private let aria2 = Aria2Controller()
  private var pollTask: Task<Void, Never>?
  private var scanTask: Task<Void, Never>?
  private var globalSearchTask: Task<Void, Never>?
  private var globalSearchID = UUID()
  private var toastTask: Task<Void, Never>?
  private var configured = false
  private var activeGlobalQuery = ""

  init(
    library: LibraryController = LibraryController(), personalStorageURL: URL? = nil,
    automaticEnrichment: Bool = true
  ) {
    self.automaticEnrichment = automaticEnrichment
    self.library = library
    self.personalStorageURL = personalStorageURL
  }

  func configure(modelContext: ModelContext) {
    guard !configured else { return }
    configured = true
    self.modelContext = modelContext
    do {
      categories = try modelContext.fetch(
        FetchDescriptor<Category>(sortBy: [SortDescriptor(\.name)]))
      batches = try modelContext.fetch(
        FetchDescriptor<DownloadBatch>(sortBy: [SortDescriptor(\.createdAt, order: .reverse)]))
      items = try modelContext.fetch(FetchDescriptor<DownloadItem>())
      let stored = try modelContext.fetch(FetchDescriptor<AppSettings>()).first
      if let stored {
        settings = stored
      } else {
        let defaults = AppSettings()
        modelContext.insert(defaults)
        settings = defaults
        try modelContext.save()
      }
      if currentCategory == nil, let first = categories.first {
        Task { await selectCategory(first) }
      }
      Task { await recoverDownloads() }
      library.isPlaying = { [weak self] in self?.player.isPlaying == true }
      library.didUpdate = { [weak self] in
        guard let self else { return }
        Task { await self.entertainment.reload() }
        if !self.activeGlobalQuery.isEmpty, self.globalSearchResults.count <= 500 {
          self.startGlobalSearch(
            query: self.activeGlobalQuery, debounce: false, keepSelection: true)
        }
      }
      player.onPositionChanged = { [weak self] media, seconds, duration in
        self?.entertainment.recordPlayback(media: media, seconds: seconds, duration: duration)
      }
      player.onPlaybackEnded = { [weak self] media in
        self?.entertainment.markPlaybackEnded(media: media)
      }
      player.onPlaybackStarted = { [weak self] media in self?.entertainment.beginPlayback(media) }
      player.shouldRecordPosition = { [weak self] media in
        self?.entertainment.shouldRecordPlayback(media) ?? true
      }
      library.refresh(
        roots: searchRoots, onlyIfDue: true,
        prepare: { [weak self] in
          await self?.entertainment.reload()
        })
    } catch {
      errorMessage = error.localizedDescription
    }
  }

  func addCategory(name: String, urlText: String) async -> Bool {
    guard let url = normalizedHTTPURL(urlText) else {
      errorMessage = DirectoryError.invalidURL.localizedDescription
      return false
    }
    isLoading = true
    defer { isLoading = false }
    do {
      _ = try await directoryService.validate(rootURL: url)
      clearGlobalSearch()
      let category = Category(
        name: name.trimmingCharacters(in: .whitespacesAndNewlines),
        rootURLString: url.absoluteString)
      modelContext?.insert(category)
      try modelContext?.save()
      categories.append(category)
      categories.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
      await selectCategory(category)
      library.refresh(
        roots: searchRoots,
        scopes: searchRoots.filter { $0.id == category.id }.map {
          IndexScope(root: $0, folder: $0.url)
        })
      return true
    } catch {
      errorMessage = error.localizedDescription
      return false
    }
  }

  func updateCategory(_ category: Category, name: String, urlText: String) async -> Bool {
    guard let url = normalizedHTTPURL(urlText) else {
      errorMessage = DirectoryError.invalidURL.localizedDescription
      return false
    }
    do {
      _ = try await directoryService.validate(rootURL: url)
      clearGlobalSearch()
      category.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
      category.rootURLString = url.absoluteString
      try modelContext?.save()
      categories.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
      if currentCategory?.id == category.id { await selectCategory(category) }
      library.refresh(
        roots: searchRoots,
        scopes: searchRoots.filter { $0.id == category.id }.map {
          IndexScope(root: $0, folder: $0.url)
        })
      return true
    } catch {
      errorMessage = error.localizedDescription
      return false
    }
  }

  func deleteCategory(_ category: Category) {
    library.cancel()
    clearGlobalSearch()
    if currentCategory?.id == category.id {
      currentCategory = nil
      currentURL = nil
      entries = []
      artworkURL = nil
    }
    modelContext?.delete(category)
    categories.removeAll { $0.id == category.id }
    try? modelContext?.save()
    library.setRoots(searchRoots)
    Task {
      do {
        try await library.database().synchronizeSources(searchRoots)
        await entertainment.reload()
      } catch { errorMessage = error.localizedDescription }
    }
  }

  func selectCategory(_ category: Category) async {
    guard let root = category.rootURL else { return }
    currentCategory = category
    await navigate(to: root)
  }

  func navigate(to url: URL) async {
    guard let root = currentCategory?.rootURL else { return }
    isLoading = true
    selection.removeAll()
    artworkURL = nil
    defer { isLoading = false }
    do {
      let boundary = try URLBoundary(root: root)
      let listing = try await directoryService.listing(at: url, boundary: boundary)
      currentURL = listing.url
      entries = listing.entries
      artworkURL = listing.artworkURL
    } catch {
      errorMessage = error.localizedDescription
    }
  }

  func navigateUp() async {
    guard let category = currentCategory, let root = category.rootURL, let currentURL,
      currentURL != root.standardizedDirectoryURL
    else { return }
    let parent = currentURL.deletingLastPathComponent()
    await navigate(to: parent)
  }

  func toggleSelection(_ entry: DirectoryEntry) {
    if selection.contains(entry.url) {
      selection.remove(entry.url)
    } else {
      selection.insert(entry.url)
    }
  }

  var searchRoots: [GlobalSearchRoot] {
    categories.compactMap { category in
      guard let url = category.rootURL else { return nil }
      return GlobalSearchRoot(id: category.id, name: category.name, url: url)
    }
  }

  func refreshLibraryIndex() { library.refresh(roots: searchRoots, manual: true, onlyIfDue: true) }

  func exportEntertainmentData() throws -> Data {
    try entertainment.exportArchive(
      sources: exportEntertainmentSources(), appPreferences: exportEntertainmentPreferences())
  }

  /// Validate and back up the portable configuration before changing either persistence boundary.
  func importEntertainmentArchive(_ raw: EntertainmentArchive, replace: Bool) async throws {
    let archive = try entertainment.previewImport(JSONEncoder().encode(raw))
    playbackChoiceTask?.cancel()
    playbackChoices = nil
    guard let context = modelContext, let settings else {
      throw DiscoveryError.invalidImport("The app is still loading. Try again shortly.")
    }
    if let prefs = archive.appPreferences {
      guard (1...20).contains(prefs.concurrentDownloads),
        (1...16).contains(prefs.connectionsPerFile),
        (1...16).contains(prefs.splitCount), (1...20).contains(prefs.retryCount),
        AppThemeMode(rawValue: prefs.theme) != nil,
        prefs.speedLimit.range(of: "^[0-9]+([KM])?$", options: .regularExpression) != nil
      else {
        throw DiscoveryError.invalidImport("Invalid download or appearance preferences.")
      }
    }
    let original = try exportEntertainmentData()
    let previous = EntertainmentArchive(
      personal: entertainment.personal, sources: [], appPreferences: nil,
      playerState: player.personalState)
    let existingPortableSources = exportEntertainmentSources()
    let folder = try AppPersistence.persistentStoreURL().deletingLastPathComponent()
    let backup = folder.appending(path: "Myra-before-import-\(UUID().uuidString).json")
    try original.write(to: backup, options: .atomic)
    do {
      try entertainment.applyArchive(archive, replace: replace)
      let importedIDs = Set(archive.sources.map(\.id))
      if replace {
        for category in categories where !importedIDs.contains(category.id) {
          context.delete(category)
        }
      }
      for source in archive.sources {
        if let category = categories.first(where: { $0.id == source.id }) {
          category.name = source.name
          let portableURL = existingPortableSources.first(where: { $0.id == category.id })?.url
          if replace || portableURL != source.url { category.rootURLString = source.url }
        } else {
          context.insert(Category(id: source.id, name: source.name, rootURLString: source.url))
        }
      }
      if let prefs = archive.appPreferences {
        settings.themeRaw = prefs.theme
        settings.concurrentDownloads = prefs.concurrentDownloads
        settings.connectionsPerFile = prefs.connectionsPerFile
        settings.splitCount = prefs.splitCount
        settings.retryCount = prefs.retryCount
        settings.speedLimit = prefs.speedLimit
      }
      try context.save()
    } catch {
      context.rollback()
      do { try entertainment.applyArchive(previous, replace: true) } catch {
        errorMessage =
          "Import recovery requires the preserved backup at \(backup.path): \(error.localizedDescription)"
      }
      throw error
    }
    if archive.playerState != nil {
      let state = PlayerPersonalState.load()
      player.personalState = state
      player.setSpeed(state.speed)
    }
    clearGlobalSearch()
    categories = try context.fetch(FetchDescriptor<Category>(sortBy: [SortDescriptor(\.name)]))
    currentCategory = nil
    currentURL = nil
    entries = []
    inspector.close()
    if let category = categories.first { await selectCategory(category) }
    await entertainment.reload()
    refreshLibraryIndex()
    if let previousPath = archive.appPreferences?.previousDownloadDirectory {
      showToast(
        "Imported. Previous download folder: \(previousPath). Choose a folder in Settings if needed."
      )
    } else {
      showToast("Personal library imported. Backup saved before changes.")
    }
  }

  func inspect(_ entry: DirectoryEntry) {
    guard MediaFileType.isVideo(entry), let category = currentCategory,
      let root = category.rootURL,
      (try? URLBoundary(root: root).contains(entry.url)) == true
    else { return }
    let rootPath = root.standardizedDirectoryURL.path
    let path = String(entry.url.path.dropFirst(rootPath.count)).trimmingCharacters(
      in: CharacterSet(charactersIn: "/"))
    inspector.select(
      GlobalSearchResult(
        categoryID: category.id, categoryName: category.name,
        categoryRoot: root, entry: entry, relativePath: path, artworkURL: artworkURL),
      library: library)
  }

  func inspect(_ result: GlobalSearchResult) { inspector.select(result, library: library) }

  func play(_ entry: DirectoryEntry) {
    inspect(entry)
    guard let result = inspector.selected, result.entry.url == entry.url else { return }
    play(result)
  }

  /// Discover alternate releases even when the directory has not yet been indexed.
  func play(_ result: GlobalSearchResult) {
    playbackChoiceTask?.cancel()
    playbackChoiceTask = Task { [weak self] in
      guard let self else { return }
      var versions = entertainment.variants(for: result)
      if versions.count < 2 {
        do {
          let parent = result.entry.url.deletingLastPathComponent()
          let boundary = try URLBoundary(root: result.categoryRoot)
          let listing = try await directoryService.listing(at: parent, boundary: boundary)
          let sequence = try PlaybackSequence(
            playing: result, entries: listing.entries, artworkURL: listing.artworkURL)
          let candidates = sequence.videos.map {
            EntertainmentVersion(media: $0, firstDiscovered: .now)
          }
          let titles = EntertainmentGrouping.group(
            candidates, corrections: entertainment.personal.matchCorrections)
          if let title = titles.first(where: {
            $0.versions.contains(where: { $0.id == result.entry.url.absoluteString })
          }) {
            let identity = MediaIdentity.parse(filename: result.entry.name)
            versions = title.versions.filter {
              $0.season == identity.season.flatMap(Int.init)
                && $0.episode == identity.episode.flatMap(Int.init)
            }
          }
        } catch {
          // Let playback report reachability errors; an unavailable directory need not block a known file.
        }
      }
      guard !Task.isCancelled else { return }
      if versions.count > 1 {
        let preferred = versions.filter { $0.lastPlayed != nil }.max {
          ($0.lastPlayed ?? .distantPast) < ($1.lastPlayed ?? .distantPast)
        }
        playbackChoices = PlaybackChoices(
          versions: versions, preferredURL: preferred?.media.entry.url ?? result.entry.url)
      } else {
        playChosenVersion(result)
      }
    }
  }

  func playLocal(url: URL) {
    playbackChoiceTask?.cancel()
    playbackChoices = nil
    player.onNotice = { [weak self] message in self?.errorMessage = message }
    player.startLocal(url, library: library)
  }

  func resumeVersion(_ version: EntertainmentVersion) {
    if version.media.entry.url.isFileURL,
      !FileManager.default.fileExists(atPath: version.media.entry.url.path)
    {
      errorMessage =
        "The previously played file is unavailable. Open Details and versions to choose another."
      return
    }
    startChosenVersion(version.media, resumeSeconds: version.progressSeconds)
  }

  func playChosenVersion(_ result: GlobalSearchResult) {
    startChosenVersion(result)
  }

  private func startChosenVersion(_ result: GlobalSearchResult, resumeSeconds: Double? = nil) {
    playbackChoiceTask?.cancel()
    playbackChoices = nil
    player.onNotice = { [weak self] message in self?.errorMessage = message }
    let parent = result.entry.url.deletingLastPathComponent().standardizedDirectoryURL
    let sequence: PlaybackSequence?
    if currentCategory?.id == result.categoryID, currentURL?.standardizedDirectoryURL == parent {
      sequence = try? PlaybackSequence(playing: result, entries: entries, artworkURL: artworkURL)
    } else {
      sequence = nil
    }
    let service = directoryService
    player.start(result, library: library, sequence: sequence, resumeSeconds: resumeSeconds) {
      let boundary = try URLBoundary(root: result.categoryRoot)
      let listing = try await service.listing(at: parent, boundary: boundary)
      return try PlaybackSequence(
        playing: result, entries: listing.entries, artworkURL: listing.artworkURL)
    }
  }

  func startGlobalSearch(query rawQuery: String, debounce: Bool = true, keepSelection: Bool = false)
  {
    let query = rawQuery.trimmingCharacters(in: .whitespacesAndNewlines)
    guard query.count >= 3 else {
      clearGlobalSearch()
      if !query.isEmpty {
        globalSearchErrorMessage = GlobalSearchError.queryTooShort.localizedDescription
      }
      return
    }
    let roots = searchRoots
    guard !roots.isEmpty else {
      cancelGlobalSearch()
      globalSearchErrorMessage = GlobalSearchError.noSources.localizedDescription
      return
    }

    globalSearchTask?.cancel()
    globalSearchID = UUID()
    let searchID = globalSearchID
    activeGlobalQuery = query
    if !keepSelection {
      globalSearchResults = []
      globalSearchSelection.removeAll()
    }
    globalSearchFailures = []
    globalSearchErrorMessage = nil
    globalSearchProgress = GlobalSearchProgress(sourcesTotal: roots.count)
    isGlobalSearching = true

    globalSearchTask = Task { [weak self] in
      guard let self else { return }
      do {
        if debounce { try await Task.sleep(for: .milliseconds(150)) }
        let index = try await library.database()
        let results = try await index.search(query, fuzzy: fuzzyGlobalSearch)
        try Task.checkCancellation()
        guard globalSearchID == searchID else { return }
        globalSearchResults = results
        hasMoreGlobalResults = results.count == 500
        globalSearchProgress = GlobalSearchProgress(
          sourcesTotal: roots.count, matchesFound: results.count)
        globalSearchFailures = library.failures
      } catch is CancellationError {
        // Keep results already received so an explicit cancellation remains useful.
      } catch {
        guard globalSearchID == searchID else { return }
        globalSearchErrorMessage = error.localizedDescription
      }
      guard globalSearchID == searchID else { return }
      isGlobalSearching = false
      globalSearchTask = nil
    }
  }

  func cancelGlobalSearch() {
    globalSearchTask?.cancel()
    globalSearchTask = nil
    globalSearchID = UUID()
    isGlobalSearching = false
    activeGlobalQuery = ""
  }

  func clearGlobalSearch() {
    cancelGlobalSearch()
    globalSearchResults = []
    globalSearchSelection.removeAll()
    globalSearchProgress = GlobalSearchProgress()
    globalSearchFailures = []
    globalSearchErrorMessage = nil
    hasMoreGlobalResults = false
  }

  func loadMoreGlobalResults() {
    guard hasMoreGlobalResults, !isGlobalSearching else { return }
    let query = activeGlobalQuery
    let searchID = globalSearchID
    let offset = globalSearchResults.count
    isGlobalSearching = true
    globalSearchTask = Task { [weak self] in
      guard let self else { return }
      do {
        let index = try await library.database()
        let results = try await index.search(query, offset: offset, fuzzy: fuzzyGlobalSearch)
        try Task.checkCancellation()
        guard globalSearchID == searchID else { return }
        globalSearchResults.append(contentsOf: results)
        hasMoreGlobalResults = results.count == 500
      } catch {
        guard globalSearchID == searchID else { return }
        if !(error is CancellationError) { globalSearchErrorMessage = error.localizedDescription }
      }
      guard globalSearchID == searchID else { return }
      isGlobalSearching = false
    }
  }

  func toggleGlobalSearchSelection(_ result: GlobalSearchResult) {
    if globalSearchSelection.contains(result.id) {
      globalSearchSelection.remove(result.id)
    } else {
      globalSearchSelection.insert(result.id)
    }
  }

  func downloadGlobalSearchSelection() {
    guard !isEnqueueingGlobalDownloads, let settings else { return }
    let selected = globalSearchResults.filter { globalSearchSelection.contains($0.id) }
    let groups = GlobalDownloadManifestBuilder.groups(for: selected)
    guard !groups.isEmpty else { return }
    isEnqueueingGlobalDownloads = true

    Task { [weak self] in
      guard let self else { return }
      for group in groups {
        let title =
          group.manifest.count == 1
          ? group.manifest[0].relativePath
          : "\(group.categoryName) — \(group.manifest.count) search results"
        let batch = DownloadBatch(title: title)
        modelContext?.insert(batch)
        batches.insert(batch, at: 0)
        do {
          try await enqueue(manifest: group.manifest, batch: batch, settings: settings)
        } catch {
          batch.status = .failed
          batch.errorMessage = error.localizedDescription
          errorMessage = error.localizedDescription
        }
      }
      globalSearchSelection.removeAll()
      isEnqueueingGlobalDownloads = false
      try? modelContext?.save()
    }
  }

  private func applyGlobalSearchSnapshot(_ snapshot: GlobalSearchSnapshot) {
    globalSearchResults = snapshot.results
    globalSearchProgress = snapshot.progress
    globalSearchFailures = snapshot.failures
  }

  var isVLCInstalled: Bool {
    NSWorkspace.shared.urlForApplication(withBundleIdentifier: "org.videolan.vlc") != nil
  }

  func copyURL(_ entry: DirectoryEntry) {
    guard entry.kind == .file else { return }
    let pasteboard = NSPasteboard.general
    pasteboard.clearContents()
    pasteboard.setString(entry.url.absoluteString, forType: .string)
    showToast("File URL copied")
  }

  func openInVLC(_ entry: DirectoryEntry) {
    guard entry.kind == .file else { return }
    guard
      let vlcURL = NSWorkspace.shared.urlForApplication(
        withBundleIdentifier: "org.videolan.vlc")
    else {
      errorMessage = "VLC is not installed. Install VLC in Applications and try again."
      return
    }

    let configuration = NSWorkspace.OpenConfiguration()
    configuration.activates = true
    showToast("Opening in VLC…")
    NSWorkspace.shared.open(
      [entry.url], withApplicationAt: vlcURL, configuration: configuration
    ) { [weak self] _, error in
      guard let error else { return }
      Task { @MainActor [weak self] in
        self?.errorMessage = "VLC could not open this URL: \(error.localizedDescription)"
      }
    }
  }

  func pushToTV(_ entry: DirectoryEntry) {
    guard entry.kind == .file else { return }
    var components = URLComponents()
    components.scheme = "shortcuts"
    components.host = "run-shortcut"
    components.queryItems = [
      URLQueryItem(name: "name", value: "Play on Sam Online"),
      URLQueryItem(name: "input", value: "text"),
      URLQueryItem(name: "text", value: entry.url.absoluteString),
    ]
    guard let shortcutURL = components.url else {
      errorMessage = "The Play on Sam Online Shortcut URL could not be created."
      return
    }
    showToast("Sending to Sam Online…")
    NSWorkspace.shared.open(shortcutURL)
  }

  func cycleTheme() {
    guard let settings else { return }
    objectWillChange.send()
    settings.theme = settings.theme.next
    try? modelContext?.save()
    showToast("Theme: \(settings.theme.displayName)")
  }

  func downloadSelection() {
    guard !isScanning, let categoryRoot = currentCategory?.rootURL, let settings else { return }
    let selected = entries.filter { selection.contains($0.url) }
    guard !selected.isEmpty else { return }
    let title = selected.count == 1 ? selected[0].name : "\(selected.count) selected items"
    let batch = DownloadBatch(title: title)
    modelContext?.insert(batch)
    batches.insert(batch, at: 0)
    try? modelContext?.save()
    isScanning = true
    scanProgress = ScanProgress()

    scanTask = Task {
      do {
        let manifest = try await planner.prepare(selections: selected, categoryRoot: categoryRoot) {
          [weak self] progress in
          await MainActor.run { self?.scanProgress = progress }
        }
        try Task.checkCancellation()
        try await enqueue(manifest: manifest, batch: batch, settings: settings)
        selection.removeAll()
      } catch is CancellationError {
        batch.status = .cancelled
      } catch {
        batch.status = .failed
        batch.errorMessage = error.localizedDescription
        errorMessage = error.localizedDescription
      }
      isScanning = false
      try? modelContext?.save()
    }
  }

  func cancelScan() {
    scanTask?.cancel()
    scanTask = nil
  }

  private func enqueue(
    manifest: [DownloadManifestItem], batch: DownloadBatch, settings: AppSettings
  ) async throws {
    let root = URL(fileURLWithPath: settings.downloadDirectory, isDirectory: true)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    let snapshot = AppSettingsSnapshot(settings)
    try await aria2.start(settings: snapshot)
    batch.status = .queued
    batch.startedAt = .now
    batch.totalBytes = manifest.reduce(0) { $0 + ($1.size ?? 0) }

    for manifestItem in manifest {
      try Task.checkCancellation()
      let destination = try DestinationSafety.destination(
        root: root, relativePath: manifestItem.relativePath)
      let item = DownloadItem(
        batchID: batch.id, manifest: manifestItem, destinationPath: destination.path)
      modelContext?.insert(item)
      items.append(item)

      if isAlreadyComplete(item: item) {
        item.status = .completed
        item.completedBytes = max(item.totalBytes, fileSize(at: destination))
        continue
      }
      let gid = try await aria2.add(
        url: manifestItem.sourceURL, destination: destination, settings: snapshot)
      item.ariaGID = gid
      item.status = .active
    }
    batch.status =
      itemsForBatch(batch).allSatisfy { $0.status == .completed } ? .completed : .active
    try modelContext?.save()
    startPolling()
  }

  func pause(_ batch: DownloadBatch) async {
    for item in itemsForBatch(batch) where item.status == .active {
      if let gid = item.ariaGID { try? await aria2.pause(gid: gid) }
      item.status = .paused
    }
    batch.status = .paused
    try? modelContext?.save()
  }

  func resume(_ batch: DownloadBatch) async {
    guard let settings else { return }
    do {
      try await aria2.start(settings: AppSettingsSnapshot(settings))
      for item in itemsForBatch(batch) where [.paused, .cancelled, .failed].contains(item.status) {
        if let gid = item.ariaGID {
          do { try await aria2.resume(gid: gid) } catch {
            try await resubmit(item, settings: settings)
          }
        } else {
          try await resubmit(item, settings: settings)
        }
        item.status = .active
      }
      batch.status = .active
      batch.errorMessage = nil
      try modelContext?.save()
      startPolling()
    } catch { errorMessage = error.localizedDescription }
  }

  func cancel(_ batch: DownloadBatch) async {
    for item in itemsForBatch(batch) where [.active, .queued].contains(item.status) {
      if let gid = item.ariaGID { try? await aria2.pause(gid: gid) }
      item.status = .cancelled
    }
    batch.status = .cancelled
    try? modelContext?.save()
  }

  func retry(_ batch: DownloadBatch) async { await resume(batch) }

  func reveal(_ batch: DownloadBatch) {
    guard let item = itemsForBatch(batch).first else { return }
    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.destinationPath)])
  }

  func clearCompleted() {
    let completedIDs = Set(batches.filter { $0.status == .completed }.map(\.id))
    for item in items where completedIDs.contains(item.batchID) { modelContext?.delete(item) }
    for batch in batches where completedIDs.contains(batch.id) { modelContext?.delete(batch) }
    items.removeAll { completedIDs.contains($0.batchID) }
    batches.removeAll { completedIDs.contains($0.id) }
    try? modelContext?.save()
  }

  func deleteDownloadRecord(_ batch: DownloadBatch) async {
    let batchItems = itemsForBatch(batch)
    for item in batchItems {
      if let gid = item.ariaGID { try? await aria2.remove(gid: gid) }
      modelContext?.delete(item)
    }
    modelContext?.delete(batch)
    items.removeAll { $0.batchID == batch.id }
    batches.removeAll { $0.id == batch.id }
    try? modelContext?.save()
    showToast("Download removed from the list")
  }

  func removePartialData(_ batch: DownloadBatch) async {
    let batchItems = itemsForBatch(batch)
    for item in batchItems where item.status != .completed {
      if let gid = item.ariaGID { try? await aria2.remove(gid: gid) }
      let fileURL = URL(fileURLWithPath: item.destinationPath)
      try? FileManager.default.removeItem(at: fileURL)
      try? FileManager.default.removeItem(atPath: fileURL.path + ".aria2")
    }
    for item in batchItems { modelContext?.delete(item) }
    modelContext?.delete(batch)
    items.removeAll { $0.batchID == batch.id }
    batches.removeAll { $0.id == batch.id }
    try? modelContext?.save()
  }

  func chooseDownloadDirectory() {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = true
    panel.canChooseFiles = false
    panel.canCreateDirectories = true
    panel.allowsMultipleSelection = false
    guard panel.runModal() == .OK, let url = panel.url, let settings else { return }
    settings.downloadDirectory = url.path
    settings.downloadBookmark = try? url.bookmarkData(
      options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil)
    try? modelContext?.save()
  }

  func saveSettings() {
    guard let settings else { return }
    settings.concurrentDownloads = min(20, max(1, settings.concurrentDownloads))
    settings.connectionsPerFile = min(16, max(1, settings.connectionsPerFile))
    settings.splitCount = min(16, max(1, settings.splitCount))
    settings.retryCount = min(20, max(1, settings.retryCount))
    try? modelContext?.save()
  }

  private func showToast(_ message: String) {
    toastTask?.cancel()
    toastMessage = message
    toastTask = Task { [weak self] in
      try? await Task.sleep(for: .seconds(2))
      guard !Task.isCancelled else { return }
      self?.toastMessage = nil
    }
  }

  func shutdown() async {
    playbackChoiceTask?.cancel()
    player.close()
    inspector.close()
    library.cancel()
    pollTask?.cancel()
    globalSearchTask?.cancel()
    await aria2.shutdown()
  }

  func terminateImmediately() {
    playbackChoiceTask?.cancel()
    player.close()
    inspector.close()
    library.cancel()
    try? modelContext?.save()
    pollTask?.cancel()
    scanTask?.cancel()
    globalSearchTask?.cancel()
    aria2.terminateImmediately()
  }

  func itemsForBatch(_ batch: DownloadBatch) -> [DownloadItem] {
    items.filter { $0.batchID == batch.id }
  }

  private func recoverDownloads() async {
    guard let settings else { return }
    let recoverable = items.filter { [.active, .queued].contains($0.status) }
    guard !recoverable.isEmpty else { return }
    do {
      try await aria2.start(settings: AppSettingsSnapshot(settings))
      for item in recoverable {
        if isAlreadyComplete(item: item) {
          item.status = .completed
          item.completedBytes = max(
            item.totalBytes, fileSize(at: URL(fileURLWithPath: item.destinationPath)))
        } else {
          try await resubmit(item, settings: settings)
        }
      }
      try modelContext?.save()
      startPolling()
    } catch { errorMessage = error.localizedDescription }
  }

  private func resubmit(_ item: DownloadItem, settings: AppSettings) async throws {
    guard let source = URL(string: item.sourceURLString) else { throw DirectoryError.invalidURL }
    item.ariaGID = try await aria2.add(
      url: source,
      destination: URL(fileURLWithPath: item.destinationPath),
      settings: AppSettingsSnapshot(settings)
    )
    item.status = .active
  }

  private func startPolling() {
    guard pollTask == nil else { return }
    pollTask = Task { [weak self] in
      while !Task.isCancelled {
        await self?.pollDownloads()
        try? await Task.sleep(for: .seconds(1))
      }
    }
  }

  private func pollDownloads() async {
    let active = items.filter { $0.status == .active && $0.ariaGID != nil }
    if active.isEmpty {
      pollTask?.cancel()
      pollTask = nil
      return
    }
    for item in active {
      guard let gid = item.ariaGID else { continue }
      do {
        let state = try await aria2.status(gid: gid)
        item.totalBytes = state.totalLength
        item.completedBytes = state.completedLength
        item.downloadSpeed = state.downloadSpeed
        item.errorMessage = state.errorMessage
        switch state.status {
        case "complete": item.status = .completed
        case "paused": item.status = .paused
        case "error", "removed": item.status = .failed
        case "waiting": item.status = .queued
        default: item.status = .active
        }
      } catch {
        item.errorMessage = error.localizedDescription
      }
    }
    for batch in batches {
      let batchItems = itemsForBatch(batch)
      guard !batchItems.isEmpty else { continue }
      batch.totalBytes = batchItems.reduce(0) { $0 + $1.totalBytes }
      batch.completedBytes = batchItems.reduce(0) { $0 + $1.completedBytes }
      batch.downloadSpeed = batchItems.reduce(0) { $0 + $1.downloadSpeed }
      if batchItems.allSatisfy({ $0.status == .completed }) {
        batch.status = .completed
        batch.completedAt = batch.completedAt ?? .now
      } else if batchItems.contains(where: { $0.status == .failed }) {
        batch.status = .failed
      } else if batchItems.contains(where: { $0.status == .active }) {
        batch.status = .active
      }
    }
    try? modelContext?.save()
  }

  private func isAlreadyComplete(item: DownloadItem) -> Bool {
    let url = URL(fileURLWithPath: item.destinationPath)
    guard FileManager.default.fileExists(atPath: url.path),
      !FileManager.default.fileExists(atPath: url.path + ".aria2")
    else { return false }
    let size = fileSize(at: url)
    return item.totalBytes == 0 ? size > 0 : size == item.totalBytes
  }

  private func fileSize(at url: URL) -> Int64 {
    let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
    return (attributes?[.size] as? NSNumber)?.int64Value ?? 0
  }

  private func normalizedHTTPURL(_ text: String) -> URL? {
    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
    let withScheme = trimmed.contains("://") ? trimmed : "http://" + trimmed
    guard var components = URLComponents(string: withScheme),
      let scheme = components.scheme?.lowercased(), ["http", "https"].contains(scheme),
      components.host != nil
    else { return nil }
    if !components.path.hasSuffix("/") { components.path += "/" }
    return components.url
  }
}
