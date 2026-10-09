import AppKit
import Foundation
import UniformTypeIdentifiers
import VLCKit

struct PlayerTrack: Identifiable, Equatable, Sendable {
  let id: Int32
  let name: String
  let language: String?
}

enum EnglishTrackPreference {
  static func preferred(in tracks: [PlayerTrack], subtitles: Bool) -> Int32? {
    var best: (id: Int32, score: Int)?
    for track in tracks where track.id >= 0 {
      let language = track.language?.trimmingCharacters(in: .whitespacesAndNewlines)
        .lowercased().replacingOccurrences(of: "_", with: "-")
        .split(separator: "-").first.map(String.init)
      let name = track.name.lowercased()
      let english =
        language == "en" || language == "eng" || language == "english"
        || name.contains("english")
        || name.range(of: "\\b(?:en|eng)\\b", options: .regularExpression) != nil
      guard english else { continue }
      var score = 0
      if name.contains("commentary") || name.contains("description") { score -= 10 }
      if subtitles && name.contains("forced") { score -= 5 }
      if best == nil || score > best!.score { best = (track.id, score) }
    }
    return best?.id
  }
}

struct PlayerChapter: Identifiable, Equatable {
  let id: Int32
  let name: String
}

struct PlaybackBufferingState {
  private var lastTime: Double?
  private var lastProgressAt: Double = 0

  mutating func reset(now: Double) {
    lastTime = nil
    lastProgressAt = now
  }

  mutating func update(state: VLCMediaPlayerState, time: Double, now: Double, scrubbing: Bool)
    -> Bool
  {
    let inactive = state == .paused || state == .stopped || state == .ended || state == .error
    if inactive || scrubbing || (lastTime.map { abs(time - $0) > 0.001 } ?? (time > 0)) {
      lastProgressAt = now
    }
    lastTime = time
    return !inactive && !scrubbing && now - lastProgressAt >= 2
  }
}

struct PlayerControlsVisibility {
  var fullscreen = false
  var hovering = false
  var menuTracking = false
  private(set) var lastInteraction: Double = 0

  mutating func interact(now: Double) { lastInteraction = now }
  func isVisible(now: Double, scrubbing: Bool) -> Bool {
    !fullscreen || hovering || menuTracking || scrubbing || now - lastInteraction < 2
  }
}

@MainActor
final class EmbeddedPlayerModel: ObservableObject {
  @Published private(set) var media: GlobalSearchResult?
  @Published private(set) var isPlaying = false
  @Published private(set) var isSeekable = false
  @Published private(set) var status = "Stopped"
  @Published private(set) var isBuffering = false
  @Published private(set) var isFullscreen = false
  @Published private(set) var controlsVisible = true
  @Published private(set) var elapsed: Double = 0
  @Published private(set) var duration: Double = 0
  @Published private(set) var position: Double = 0
  @Published private(set) var audioTracks: [PlayerTrack] = []
  @Published private(set) var subtitleTracks: [PlayerTrack] = []
  @Published private(set) var chapters: [PlayerChapter] = []
  @Published private(set) var selectedAudio: Int32 = -1
  @Published private(set) var selectedSubtitle: Int32 = -1
  @Published private(set) var selectedChapter: Int32 = -1
  @Published private(set) var volume: Double = 100
  @Published var isMuted = false
  @Published var speed: Float = 1
  @Published var audioDelay: Double = 0
  @Published var subtitleDelay: Double = 0
  @Published var repeatEnabled = false
  @Published var isScrubbing = false
  @Published var scrubPosition: Double = 0
  @Published var errorMessage: String?
  @Published var resumePosition: Double?
  private(set) var engine: VLCMediaPlayer?
  private weak var videoView: VLCVideoView?
  private weak var window: NSWindow?
  private var pollTask: Task<Void, Never>?
  private var manualAudioSelection = false
  private var manualSubtitleSelection = false
  private var library: LibraryController?
  private var savedAt = Date.distantPast
  private var loadID = UUID()
  private var bufferingState = PlaybackBufferingState()
  private var controlsState = PlayerControlsVisibility()
  private var windowObservers: [NSObjectProtocol] = []
  private var eventMonitor: Any?
  private var previousMouseMovedEvents = false
  private let fullscreenChrome = PlayerFullscreenChrome()
  private var fullscreenTransition = false
  private var fullscreenExitRequested = false
  private var returnAfterFullscreen = false
  private var isClosing = false
  private var fullscreenTimeout: Task<Void, Never>?
  private var completion = PlaybackCompletionState()
  private var completedCurrentMedia = false
  @Published private(set) var sequence: PlaybackSequence?
  @Published private(set) var sequenceLoading = false
  @Published var versionChoices: [GlobalSearchResult] = []
  private var choosingInitialLocalVersion = false
  @Published var personalState = PlayerPersonalState.load()
  var onPlaybackEnded: ((GlobalSearchResult) -> Void)?
  var onPlaybackStarted: ((GlobalSearchResult) -> Void)?
  var shouldRecordPosition: ((GlobalSearchResult) -> Bool)?
  var onPositionChanged: ((GlobalSearchResult, Double, Double) -> Void)?
  private var sequenceTask: Task<PlaybackSequence, Error>?
  private var automaticResumeSeconds: Double?
  private var endTask: Task<Void, Never>?
  private var playbackObserver: NSObjectProtocol?
  private weak var activeMenu: NSMenu?
  @Published var subtitleSearch: OnlineSubtitleSearchModel?
  var onNotice: ((String) -> Void)?
  var playbackWindow: NSWindow? { window }

  func start(
    _ media: GlobalSearchResult, library: LibraryController,
    sequence: PlaybackSequence? = nil,
    startPaused: Bool = false,
    resumeSeconds: Double? = nil,
    sequenceLoader: (@Sendable () async throws -> PlaybackSequence)? = nil
  ) {
    endTask?.cancel()
    endTask = nil
    sequenceTask?.cancel()
    self.sequence = sequence
    sequenceTask =
      sequence == nil ? sequenceLoader.map { loader in Task { try await loader() } } : nil
    self.library = library
    openMedia(
      media, automatically: false, beginPlayback: !startPaused, resumeSeconds: resumeSeconds)
    sequenceLoading = self.sequenceTask != nil
    if let task = self.sequenceTask {
      let id = loadID
      Task { [weak self] in
        do {
          let queue = try await task.value
          guard let self, self.loadID == id else { return }
          self.sequence = queue
          self.sequenceLoading = false
        } catch {
          guard let self, self.loadID == id else { return }
          self.sequenceLoading = false
          self.onNotice?("Folder navigation unavailable: \(error.localizedDescription)")
        }
      }
    }
  }

  var currentVersions: [GlobalSearchResult] {
    guard let media else { return [] }
    return sequence?.groups.first(where: { $0.contains { $0.entry.url == media.entry.url } }) ?? [
      media
    ]
  }

  var canGoPrevious: Bool {
    status != "Opening…" && !sequenceLoading && versionChoices.isEmpty
      && media.map { sequence?.previous(before: $0.entry.url) != nil } == true
  }
  var canGoNext: Bool {
    status != "Opening…" && !sequenceLoading && versionChoices.isEmpty
      && media.map { sequence?.next(after: $0.entry.url) != nil } == true
  }

  func navigate(offset: Int) {
    guard status != "Opening…", !sequenceLoading, versionChoices.isEmpty, let media else { return }
    requestPlayback(sequence?.neighbours(of: media.entry.url, offset: offset) ?? [])
  }

  func requestPlayback(_ choices: [GlobalSearchResult]) {
    guard let first = choices.first else { return }
    endTask?.cancel()
    endTask = nil
    if choices.count > 1 {
      versionChoices = choices
      engine?.pause()
      noteInteraction()
    } else {
      openMedia(first, automatically: false)
    }
  }

  func chooseVersion(_ choice: GlobalSearchResult) {
    guard versionChoices.contains(where: { $0.id == choice.id }) else { return }
    versionChoices = []
    choosingInitialLocalVersion = false
    openMedia(choice, automatically: false)
  }

  func cancelVersionChoice() {
    versionChoices = []
    if choosingInitialLocalVersion {
      choosingInitialLocalVersion = false
      close()
    }
    noteInteraction()
  }

  func savePersonalState() {
    personalState.speed = speed
    do { try personalState.save() } catch {
      onNotice?("Could not save player preferences: \(error.localizedDescription)")
    }
  }

  var markerKey: String {
    media.map { MediaIdentity.parse(filename: $0.entry.name).cacheKey } ?? ""
  }
  var seriesMarkerKey: String {
    guard let media else { return "" }
    var identity = MediaIdentity.parse(filename: media.entry.name)
    identity.episode = nil
    identity.season = nil
    return "series|" + identity.cacheKey
  }
  var currentMarkers: PlayerSkipMarkers {
    personalState.markers[markerKey] ?? personalState.markers[seriesMarkerKey]
      ?? PlayerSkipMarkers()
  }
  func setMarkers(_ markers: PlayerSkipMarkers, series: Bool) -> Bool {
    guard duration > 0,
      [markers.intro, markers.outro].compactMap({ $0 }).allSatisfy({ $0.valid(duration: duration) })
    else { return false }
    personalState.markers[series ? seriesMarkerKey : markerKey] = markers
    savePersonalState()
    return true
  }
  func clearMarkers(series: Bool) {
    personalState.markers.removeValue(forKey: series ? seriesMarkerKey : markerKey)
    savePersonalState()
  }
  var activeSkip: (String, PlayerSkipRange)? {
    if let range = currentMarkers.intro, range.valid(duration: duration), range.contains(elapsed) {
      return ("Skip Intro", range)
    }
    if let range = currentMarkers.outro, range.valid(duration: duration), range.contains(elapsed) {
      return ("Skip Outro", range)
    }
    return nil
  }

  func startLocal(_ url: URL, library: LibraryController) {
    guard url.isFileURL, FileManager.default.isReadableFile(atPath: url.path) else {
      onNotice?("The downloaded video is missing or unreadable.")
      return
    }
    let root = url.deletingLastPathComponent().resolvingSymlinksInPath()
    let entry = DirectoryEntry(
      name: url.lastPathComponent, url: url.resolvingSymlinksInPath(), kind: .file)
    let result = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Offline", categoryRoot: root, entry: entry,
      relativePath: entry.name, artworkURL: nil)
    let siblings =
      (try? FileManager.default.contentsOfDirectory(
        at: root, includingPropertiesForKeys: [.isRegularFileKey])) ?? []
    let entries = siblings.compactMap { candidate -> DirectoryEntry? in
      guard
        candidate.resolvingSymlinksInPath().deletingLastPathComponent().standardizedFileURL.path
          == root.standardizedFileURL.path,
        (try? candidate.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
      else { return nil }
      return DirectoryEntry(name: candidate.lastPathComponent, url: candidate, kind: .file)
    }
    let queue = try? PlaybackSequence(playing: result, entries: entries, artworkURL: nil)
    let versions =
      queue?.groups.first(where: { $0.contains { $0.entry.url == result.entry.url } }) ?? [result]
    start(result, library: library, sequence: queue, startPaused: versions.count > 1)
    if versions.count > 1 {
      choosingInitialLocalVersion = true
      versionChoices = versions
    }
  }

  private func openMedia(
    _ media: GlobalSearchResult, automatically: Bool, beginPlayback: Bool = true,
    resumeSeconds: Double? = nil
  ) {
    guard MediaFileType.isVideo(media.entry),
      PlaybackSequence.contains(media.entry.url, root: media.categoryRoot)
    else {
      errorMessage = "The video URL is outside its category."
      return
    }
    activeMenu?.cancelTracking()
    persistPosition()
    pollTask?.cancel()
    removePlaybackObserver()
    engine?.stop()
    engine?.drawable = nil
    subtitleSearch?.cancel()
    subtitleSearch = nil
    loadID = UUID()
    completion.reset()
    completedCurrentMedia = false
    isClosing = false
    returnAfterFullscreen = false
    self.media = media
    onPlaybackStarted?(media)
    fullscreenChrome.update(window: window, fullscreen: isFullscreen)
    manualAudioSelection = false
    manualSubtitleSelection = false
    audioTracks = []
    subtitleTracks = []
    chapters = []
    elapsed = 0
    duration = 0
    position = 0
    isSeekable = false
    isScrubbing = false
    if !automatically {
      speed = personalState.speed
      audioDelay = 0
      subtitleDelay = 0
    }
    resumePosition = nil
    automaticResumeSeconds = resumeSeconds.flatMap { $0.isFinite && $0 >= 5 ? $0 : nil }
    errorMessage = nil
    status = "Opening…"
    isBuffering = false
    bufferingState.reset(now: ProcessInfo.processInfo.systemUptime)
    noteInteraction()
    var options = ["--quiet", "--no-video-title-show", "--no-play-and-pause"]
    if personalState.audioLanguage != "default" {
      options.append(
        "--audio-language="
          + (personalState.audioLanguage == "en" ? "eng,en" : personalState.audioLanguage))
    }
    if personalState.subtitleLanguage == "off" {
      options.append("--sub-track=-1")
    } else if personalState.subtitleLanguage != "default" {
      options.append(
        "--sub-language="
          + (personalState.subtitleLanguage == "en" ? "eng,en" : personalState.subtitleLanguage))
    }
    let engine = VLCMediaPlayer(options: options)
    let source = VLCMedia(url: media.entry.url)
    source.addOptions(["network-caching": 1500])
    engine.media = source
    engine.audio?.volume = Int32(volume)
    engine.audio?.isMuted = isMuted
    self.engine = engine
    // Ended can be followed by Stopped between two poll ticks. Observe VLC's ordered
    // notifications synchronously so a natural finish cannot be mistaken for Stop.
    playbackObserver = NotificationCenter.default.addObserver(
      forName: Notification.Name(VLCMediaPlayerStateChanged), object: engine, queue: .main
    ) { [weak self] notification in
      guard let source = notification.object as? VLCMediaPlayer else { return }
      let sourceID = ObjectIdentifier(source)
      MainActor.assumeIsolated {
        guard let self, let current = self.engine,
          ObjectIdentifier(current) == sourceID, !self.isClosing
        else { return }
        self.handleCompletion(current.state)
      }
    }
    if let videoView { engine.drawable = videoView }
    if beginPlayback { engine.play() }
    let id = loadID
    if !automatically, resumeSeconds == nil, let library {
      Task { [weak self] in
        guard let index = try? await library.database(),
          let saved = try? await index.playbackPosition(url: media.entry.url)
        else { return }
        guard let self, self.loadID == id else { return }
        self.resumePosition = saved
      }
    }
    pollTask = Task { [weak self] in
      while !Task.isCancelled {
        self?.updatePlaybackState()
        try? await Task.sleep(for: .milliseconds(250))
      }
    }
  }

  func savedPosition(for result: GlobalSearchResult) async -> Double? {
    guard let library, let index = try? await library.database() else { return nil }
    return try? await index.playbackPosition(url: result.entry.url)
  }

  func attach(view: VLCVideoView) {
    videoView = view
    // SwiftUI temporarily detaches the video surface during fullscreen layout changes.
    // A nil surface window must not discard the root view's stable window binding.
    if let window = view.window, self.window == nil { setWindow(window) }
    if engine?.drawable as AnyObject? !== view { engine?.drawable = view }
  }
  func detach(view: VLCVideoView) {
    guard videoView === view else { return }
    engine?.drawable = nil
    videoView = nil
  }
  func setWindow(_ newWindow: NSWindow?) {
    guard window !== newWindow else { return }
    removeWindowTracking()
    window = newWindow
    guard let newWindow else { return }
    previousMouseMovedEvents = newWindow.acceptsMouseMovedEvents
    newWindow.acceptsMouseMovedEvents = true
    updateFullscreen(newWindow.styleMask.contains(.fullScreen))
    for (name, fullscreen) in [
      (NSWindow.didEnterFullScreenNotification, true),
      (NSWindow.didExitFullScreenNotification, false),
    ] {
      windowObservers.append(
        NotificationCenter.default.addObserver(forName: name, object: newWindow, queue: .main) {
          [weak self] _ in
          MainActor.assumeIsolated {
            guard let self else { return }
            let exitRequested = self.fullscreenExitRequested
            self.fullscreenExitRequested = false
            self.fullscreenTransition = false
            self.fullscreenTimeout?.cancel()
            self.updateFullscreen(fullscreen)
            if fullscreen && (exitRequested || self.returnAfterFullscreen) {
              self.exitFullscreen()
            } else if !fullscreen && self.returnAfterFullscreen {
              self.finishClosingPlayer()
            }
          }
        })
    }
    for (name, entering) in [
      (NSWindow.willEnterFullScreenNotification, true),
      (NSWindow.willExitFullScreenNotification, false),
    ] {
      windowObservers.append(
        NotificationCenter.default.addObserver(forName: name, object: newWindow, queue: .main) {
          [weak self] _ in
          MainActor.assumeIsolated {
            guard let self else { return }
            self.fullscreenTransition = true
            if !entering { self.fullscreenExitRequested = true }
            if entering { self.updateFullscreen(true) }
            self.scheduleFullscreenRecovery()
          }
        })
    }
    eventMonitor = NSEvent.addLocalMonitorForEvents(matching: [
      .mouseMoved, .leftMouseDown, .rightMouseDown, .leftMouseDragged, .keyDown, .scrollWheel,
    ]) { [weak self] event in
      let handled = MainActor.assumeIsolated {
        if let self, event.window === self.window, self.media != nil, self.isFullscreen {
          self.noteInteraction()
          if event.type == .keyDown, self.handleKeyEvent(event) { return true }
        }
        return false
      }
      return handled ? nil : event
    }
  }

  private func removeWindowTracking() {
    fullscreenChrome.restore()
    if let eventMonitor { NSEvent.removeMonitor(eventMonitor) }
    eventMonitor = nil
    for observer in windowObservers { NotificationCenter.default.removeObserver(observer) }
    windowObservers.removeAll()
    window?.acceptsMouseMovedEvents = previousMouseMovedEvents
  }

  private func updateFullscreen(_ value: Bool) {
    fullscreenChrome.update(window: window, fullscreen: value && media != nil)
    if isFullscreen != value { isFullscreen = value }
    controlsState.fullscreen = value
    controlsState.hovering = false
    controlsState.menuTracking = false
    noteInteraction()
  }

  func noteInteraction() {
    controlsState.interact(now: ProcessInfo.processInfo.systemUptime)
    if !controlsVisible { controlsVisible = true }
  }

  func hoverControls(_ hovering: Bool) {
    controlsState.hovering = hovering
    noteInteraction()
  }
  func setMenuTracking(_ tracking: Bool, menu: NSMenu? = nil) {
    activeMenu = tracking ? menu : nil
    controlsState.menuTracking = tracking
    noteInteraction()
    if !tracking { window?.makeFirstResponder(videoView) }
  }

  func toggleFullscreen() {
    guard media != nil, let window else { return }
    if isFullscreen || window.styleMask.contains(.fullScreen) {
      exitFullscreen()
      return
    }
    guard !fullscreenTransition else { return }
    fullscreenTransition = true
    scheduleFullscreenRecovery()
    window.toggleFullScreen(nil)
  }

  func exitFullscreen() {
    guard media != nil, let window, !fullscreenExitRequested else { return }
    if window.styleMask.contains(.fullScreen) {
      fullscreenExitRequested = true
      fullscreenTransition = true
      scheduleFullscreenRecovery()
      window.toggleFullScreen(nil)
    } else if fullscreenTransition {
      // A click during the enter animation is applied as soon as macOS finishes entering.
      fullscreenExitRequested = true
    } else if returnAfterFullscreen {
      finishClosingPlayer()
    }
  }

  private func scheduleFullscreenRecovery() {
    fullscreenTimeout?.cancel()
    fullscreenTimeout = Task { [weak self] in
      do { try await Task.sleep(for: .seconds(5)) } catch { return }
      guard let self else { return }
      let exitRequested = self.fullscreenExitRequested
      self.fullscreenExitRequested = false
      self.fullscreenTransition = false
      self.updateFullscreen(self.window?.styleMask.contains(.fullScreen) == true)
      if self.returnAfterFullscreen {
        if self.isFullscreen {
          self.isClosing = false
          self.returnAfterFullscreen = false
          self.errorMessage = "macOS did not exit fullscreen. Please try Back to Library again."
        } else {
          self.finishClosingPlayer()
        }
      } else if exitRequested && self.isFullscreen {
        self.errorMessage = "macOS did not exit fullscreen. Please click Exit fullscreen again."
      }
    }
  }

  /// Window-scoped routing remains usable after clicking transport controls.
  /// Text fields, sheets and native menus retain their normal keyboard handling.
  func handleKeyEvent(_ event: NSEvent) -> Bool {
    guard media != nil, !isClosing, event.window === window,
      window?.attachedSheet == nil, !controlsState.menuTracking,
      !(window?.firstResponder is NSTextView), !(window?.firstResponder is NSTextField),
      !event.modifierFlags.contains(.command), !event.modifierFlags.contains(.control)
    else { return false }
    switch event.keyCode {
    case 49: togglePlayback()
    case 123, 124:
      let distance: Double =
        event.modifierFlags.contains(.shift)
        ? 60
        : event.modifierFlags.contains(.option) ? 3 : 10
      jump(seconds: event.keyCode == 123 ? -distance : distance)
    case 126: setVolume(volume + 5)
    case 125: setVolume(volume - 5)
    case 53:
      guard isFullscreen || window?.styleMask.contains(.fullScreen) == true else { return false }
      exitFullscreen()
    default: return false
    }
    noteInteraction()
    return true
  }

  func togglePlayback() {
    guard let engine else { return }
    if engine.isPlaying { engine.pause() } else { engine.play() }
  }
  func stop() {
    close()
  }
  func close() {
    guard !isClosing else { return }
    isClosing = true
    activeMenu?.cancelTracking()
    persistPosition()
    removePlaybackObserver()
    loadID = UUID()
    pollTask?.cancel()
    pollTask = nil
    endTask?.cancel()
    sequenceTask?.cancel()
    subtitleSearch?.cancel()
    subtitleSearch = nil
    engine?.stop()
    if fullscreenTransition || window?.styleMask.contains(.fullScreen) == true {
      returnAfterFullscreen = true
      if !fullscreenTransition { exitFullscreen() }
      return
    }
    finishClosingPlayer()
  }

  private func finishClosingPlayer() {
    fullscreenTimeout?.cancel()
    fullscreenTransition = false
    fullscreenExitRequested = false
    returnAfterFullscreen = false
    engine?.stop()
    engine?.drawable = nil
    engine = nil
    media = nil
    isPlaying = false
    resumePosition = nil
    isScrubbing = false
    isBuffering = false
    sequence = nil
    sequenceLoading = false
    versionChoices = []
    choosingInitialLocalVersion = false
    sequenceTask = nil
    endTask = nil
    completion.reset()
    completedCurrentMedia = false
    isClosing = false
    updateFullscreen(false)
  }
  func setVolume(_ value: Double) {
    volume = value.isFinite ? min(100, max(0, value)) : volume
    engine?.audio?.volume = Int32(volume)
  }
  func toggleMute() {
    isMuted.toggle()
    engine?.audio?.isMuted = isMuted
  }
  func setSpeed(_ value: Float) {
    speed = min(4, max(0.25, value))
    engine?.rate = speed
    savePersonalState()
  }
  func seek(to value: Double) {
    guard isSeekable else { return }
    let value = min(1, max(0, value))
    engine?.position = Float(value)
    position = value
    elapsed = value * duration
    bufferingState.reset(now: ProcessInfo.processInfo.systemUptime)
    noteInteraction()
  }
  func jump(seconds: Double) {
    guard isSeekable, duration > 0 else { return }
    seek(to: (elapsed + seconds) / duration)
  }
  func resumeSavedPosition() {
    guard let resumePosition, duration > 0, isSeekable else { return }
    seek(to: resumePosition / duration)
    self.resumePosition = nil
  }
  func chooseAudio(_ id: Int32) {
    manualAudioSelection = true
    engine?.currentAudioTrackIndex = id
    selectedAudio = id
  }
  func chooseSubtitle(_ id: Int32) {
    manualSubtitleSelection = true
    engine?.currentVideoSubTitleIndex = id
    selectedSubtitle = id
  }
  func chooseChapter(_ id: Int32) { engine?.currentChapterIndex = id }
  func setDelays() {
    engine?.currentAudioPlaybackDelay = Int(audioDelay * 1_000_000)
    engine?.currentVideoSubTitleDelay = Int(subtitleDelay * 1_000_000)
  }
  func setAspect(_ value: String?) {
    if let value {
      value.withCString { engine?.videoAspectRatio = UnsafeMutablePointer(mutating: $0) }
    } else {
      engine?.videoAspectRatio = nil
    }
  }
  func setCrop(_ value: String?) {
    if let value {
      value.withCString { engine?.videoCropGeometry = UnsafeMutablePointer(mutating: $0) }
    } else {
      engine?.videoCropGeometry = nil
    }
  }
  func setDeinterlace(_ mode: VLCDeinterlace) { engine?.setDeinterlace(mode, withFilter: "yadif") }

  func loadSubtitle() {
    let panel = NSOpenPanel()
    panel.allowsMultipleSelection = false
    panel.canChooseDirectories = false
    panel.allowedContentTypes = ["srt", "ass", "vtt", "sub"].compactMap {
      UTType(filenameExtension: $0)
    }
    panel.begin { [weak self] result in
      guard result == .OK, let url = panel.url else { return }
      Task { @MainActor [weak self] in
        _ = self?.loadSubtitle(from: url)
      }
    }
  }

  @discardableResult
  func loadSubtitle(from url: URL, for mediaID: GlobalSearchResultID? = nil) -> Bool {
    guard mediaID == nil || media?.id == mediaID, url.isFileURL,
      FileManager.default.isReadableFile(atPath: url.path), let engine, !isClosing
    else { return false }
    if engine.addPlaybackSlave(url, type: .subtitle, enforce: true) == 0 {
      manualSubtitleSelection = true
      return true
    }
    errorMessage = "VLC could not load this subtitle file."
    return false
  }

  func findOnlineSubtitles() {
    guard let media else { return }
    subtitleSearch = OnlineSubtitleSearchModel(media: media) { [weak self] url in
      self?.loadSubtitle(from: url, for: media.id) == true
    }
  }

  private func updatePlaybackState() {
    guard let engine, !isClosing else { return }
    if isPlaying != engine.isPlaying { isPlaying = engine.isPlaying }
    if isSeekable != engine.isSeekable { isSeekable = engine.isSeekable }
    elapsed = max(0, Double(engine.time.intValue) / 1000)
    let now = ProcessInfo.processInfo.systemUptime
    let buffering = bufferingState.update(
      state: engine.state, time: elapsed, now: now, scrubbing: isScrubbing)
    if isBuffering != buffering { isBuffering = buffering }
    let visible = controlsState.isVisible(now: now, scrubbing: isScrubbing)
    if controlsVisible != visible { controlsVisible = visible }
    duration = max(0, Double(engine.media?.length.intValue ?? 0) / 1000)
    if let saved = automaticResumeSeconds, duration > 0, isSeekable {
      automaticResumeSeconds = nil
      seek(to: min(saved, max(0, duration - 1)) / duration)
    }
    if !isScrubbing { position = max(0, min(1, Double(engine.position))) }
    switch engine.state {
    case .opening: status = "Opening…"
    case .buffering: status = isBuffering ? "Buffering…" : (elapsed > 0 ? "Playing" : "Opening…")
    case .playing: status = "Playing"
    case .paused: status = "Paused"
    case .stopped: status = "Stopped"
    case .ended:
      status = "Finished"
    case .error:
      status = "Playback failed"
      errorMessage =
        "VLC could not play this stream. Check the source connection or try the external VLC action."
    default: break
    }
    if personalState.automaticSkipping, isSeekable, let skip = activeSkip {
      seek(to: skip.1.end / duration)
    }
    if handleCompletion(engine.state) { return }
    // Native menu snapshots remain unchanged for the entire tracking session.
    guard !controlsState.menuTracking else { return }
    let information = engine.media?.tracksInformation as? [[String: Any]] ?? []
    let audio = tracks(
      names: engine.audioTrackNames, ids: engine.audioTrackIndexes, information: information)
    if audioTracks != audio { audioTracks = audio }
    let subtitles = tracks(
      names: engine.videoSubTitlesNames, ids: engine.videoSubTitlesIndexes, information: information
    )
    if subtitleTracks != subtitles { subtitleTracks = subtitles }
    // Track-added/buffering notifications can outlive the Playing state while playback continues.
    if engine.isPlaying || engine.state == .paused || engine.state == .buffering {
      if !manualAudioSelection,
        let id = PlayerLanguagePreference.preferred(
          in: audioTracks, language: personalState.audioLanguage, subtitles: false),
        engine.currentAudioTrackIndex != id
      {
        engine.currentAudioTrackIndex = id
      }
      if !manualSubtitleSelection {
        let id =
          PlayerLanguagePreference.preferred(
            in: subtitleTracks, language: personalState.subtitleLanguage, subtitles: true)
          ?? (personalState.subtitleLanguage == "default" ? engine.currentVideoSubTitleIndex : -1)
        if engine.currentVideoSubTitleIndex != id { engine.currentVideoSubTitleIndex = id }
      }
    }
    if selectedAudio != engine.currentAudioTrackIndex {
      selectedAudio = engine.currentAudioTrackIndex
    }
    if selectedSubtitle != engine.currentVideoSubTitleIndex {
      selectedSubtitle = engine.currentVideoSubTitleIndex
    }
    if selectedChapter != engine.currentChapterIndex {
      selectedChapter = engine.currentChapterIndex
    }
    if engine.state == .playing, engine.rate != speed { engine.rate = speed }
    let chapterInfo =
      engine.chapterDescriptions(ofTitle: engine.currentTitleIndex) as? [[String: Any]] ?? []
    let updatedChapters = chapterInfo.enumerated().map { offset, item in
      PlayerChapter(
        id: Int32(offset),
        name: item[VLCChapterDescriptionName] as? String ?? "Chapter \(offset + 1)")
    }
    if chapters != updatedChapters { chapters = updatedChapters }
    if Date.now.timeIntervalSince(savedAt) > 10, elapsed > 0, engine.state != .ended {
      savedAt = .now
      persistPosition()
    }
  }

  @discardableResult
  private func handleCompletion(_ state: VLCMediaPlayerState) -> Bool {
    guard !isClosing, completion.observe(state) else { return false }
    if state == .ended {
      completedCurrentMedia = true
      persistPosition(completed: true)
      if let media { onPlaybackEnded?(media) }
      if repeatEnabled, let media {
        openMedia(media, automatically: true)
      } else if personalState.autoplay {
        advanceAfterCompletion()
      } else {
        close()
      }
    } else {
      close()
    }
    return true
  }

  private func removePlaybackObserver() {
    if let playbackObserver { NotificationCenter.default.removeObserver(playbackObserver) }
    playbackObserver = nil
  }

  private func advanceAfterCompletion() {
    guard endTask == nil, let finished = media else { return }
    let id = loadID
    endTask = Task { [weak self] in
      guard let self else { return }
      do {
        var queue = self.sequence
        if queue == nil, let task = self.sequenceTask { queue = try await task.value }
        guard !Task.isCancelled, self.loadID == id, !self.isClosing else { return }
        self.sequence = queue
        self.sequenceLoading = false
        if let next = queue?.next(after: finished.entry.url) {
          self.endTask = nil
          let variants = queue?.neighbours(of: finished.entry.url, offset: 1) ?? [next]
          if variants.count > 1 {
            self.requestPlayback(variants)
          } else {
            self.openMedia(next, automatically: true)
          }
        } else {
          self.close()
        }
      } catch {
        guard !Task.isCancelled, self.loadID == id else { return }
        self.onNotice?(
          "Playback finished, but the next video could not be found: \(error.localizedDescription)")
        self.close()
      }
    }
  }

  private func tracks(names: [Any]?, ids: [Any]?, information: [[String: Any]]) -> [PlayerTrack] {
    let names = names ?? []
    let ids = ids ?? []
    var result: [PlayerTrack] = []
    for (offset, value) in ids.enumerated() {
      guard let number = value as? NSNumber else { continue }
      let id = number.int32Value
      let details = information.first {
        ($0[VLCMediaTracksInformationId] as? NSNumber)?.int32Value == id
      }
      let name = offset < names.count ? names[offset] as? String : nil
      result.append(
        PlayerTrack(
          id: id, name: name ?? (id == -1 ? "Disabled" : "Track \(id)"),
          language: details?[VLCMediaTracksInformationLanguage] as? String))
    }
    return result
  }

  private func persistPosition(completed: Bool = false) {
    guard let media, let library, elapsed > 0,
      completed || (!completedCurrentMedia && engine?.state != .ended)
    else { return }
    onPositionChanged?(media, elapsed, duration)
    guard shouldRecordPosition?(media) != false else { return }
    let seconds = completed ? 0 : elapsed
    let duration = duration
    Task {
      guard let index = try? await library.database() else { return }
      guard self.shouldRecordPosition?(media) != false else { return }
      try? await index.savePlaybackPosition(
        url: media.entry.url, seconds: seconds, duration: duration)
    }
  }
}

@MainActor
enum VLCMediaProbe {
  static func inspect(url: URL) async throws -> [String: String] {
    let media = VLCMedia(url: url)
    let result = media.parse(options: VLCMediaParsingOptions(rawValue: 1), timeout: 10_000)
    guard result == 0 else { throw MetadataError.network }
    defer { media.parseStop() }
    let deadline = Date.now.addingTimeInterval(11)
    while media.parsedStatus != .done {
      try Task.checkCancellation()
      guard Date.now < deadline,
        media.parsedStatus != .failed && media.parsedStatus != .timeout
          && media.parsedStatus != .skipped
      else {
        throw MetadataError.network
      }
      try await Task.sleep(for: .milliseconds(150))
    }
    var details: [String: String] = [:]
    if media.length.intValue > 0 {
      details["Duration"] = PlayerTime.string(Double(media.length.intValue) / 1000)
    }
    let tracks = media.tracksInformation as? [[String: Any]] ?? []
    var audioLanguages: [String] = []
    var subtitles: [String] = []
    var audioCodecs: [String] = []
    for track in tracks {
      let type = track[VLCMediaTracksInformationType] as? String ?? ""
      let language = track[VLCMediaTracksInformationLanguage] as? String ?? "Unknown language"
      let description = track[VLCMediaTracksInformationDescription] as? String
      let codec = (track[VLCMediaTracksInformationCodec] as? NSNumber).map {
        VLCMedia.codecName(forFourCC: $0.uint32Value, trackType: type)
      }
      if type == VLCMediaTracksInformationTypeVideo {
        if let width = track[VLCMediaTracksInformationVideoWidth] as? NSNumber,
          let height = track[VLCMediaTracksInformationVideoHeight] as? NSNumber,
          width.intValue > 0 && height.intValue > 0
        {
          details["Resolution"] = "\(width) × \(height)"
        }
        if let codec, !codec.isEmpty { details["Video codec"] = codec }
        if let numerator = track[VLCMediaTracksInformationFrameRate] as? NSNumber,
          let denominator = track[VLCMediaTracksInformationFrameRateDenominator] as? NSNumber,
          denominator.doubleValue > 0
        {
          details["Frame rate"] = String(
            format: "%.3g fps", numerator.doubleValue / denominator.doubleValue)
        }
      } else if type == VLCMediaTracksInformationTypeAudio {
        audioLanguages.append(description.map { "\(language) (\($0))" } ?? language)
        if let codec, !codec.isEmpty { audioCodecs.append(codec) }
      } else if type == VLCMediaTracksInformationTypeText {
        subtitles.append(description.map { "\(language) (\($0))" } ?? language)
      }
    }
    if !audioLanguages.isEmpty { details["Audio tracks"] = audioLanguages.joined(separator: ", ") }
    if !audioCodecs.isEmpty { details["Audio codecs"] = audioCodecs.joined(separator: ", ") }
    if !subtitles.isEmpty { details["Subtitle tracks"] = subtitles.joined(separator: ", ") }
    guard !details.isEmpty else { throw MetadataError.badResponse }
    return details
  }
}

enum PlayerTime {
  static func string(_ seconds: Double) -> String {
    let value = max(0, Int(seconds.isFinite ? seconds : 0))
    if value >= 3600 {
      return String(format: "%d:%02d:%02d", value / 3600, (value % 3600) / 60, value % 60)
    }
    return String(format: "%d:%02d", value / 60, value % 60)
  }
}
