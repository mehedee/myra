#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import Foundation

/// The facts the menu bar item shows, captured from the coordinator at one moment.
struct MenuBarSnapshot: Equatable, Sendable {
  var nowPlaying: String?
  var isPlaying: Bool
  var indexing: IndexRefreshState
  var indexedVideos: Int
  var activeDownloads: Int
  var downloadBytesPerSecond: Int64
}

/// Menu bar wording, kept free of AppKit so it can be unit tested.
enum MenuBarText {
  /// The number beside the icon. Empty when nothing is downloading, so the icon stands alone.
  static func statusTitle(_ snapshot: MenuBarSnapshot) -> String {
    snapshot.activeDownloads > 0 ? "\(snapshot.activeDownloads)" : ""
  }

  static func nowPlaying(_ snapshot: MenuBarSnapshot) -> String {
    guard let title = snapshot.nowPlaying, !title.isEmpty else { return "Nothing playing" }
    return (snapshot.isPlaying ? "Playing · " : "Paused · ") + title
  }

  static func indexing(_ snapshot: MenuBarSnapshot) -> String {
    switch snapshot.indexing {
    case .refreshing:
      return "Indexing · \(snapshot.indexedVideos.formatted()) videos found"
    case .paused:
      return "Indexing paused"
    default:
      return "Library up to date"
    }
  }

  static func downloads(_ snapshot: MenuBarSnapshot) -> String {
    guard snapshot.activeDownloads > 0 else { return "No active downloads" }
    let noun = snapshot.activeDownloads == 1 ? "download" : "downloads"
    let speed = snapshot.downloadBytesPerSecond > 0
      ? " · \(DownloadFormat.bytes(snapshot.downloadBytesPerSecond))/s" : ""
    return "\(snapshot.activeDownloads) \(noun)" + speed
  }

  static func pauseIndexingTitle(_ snapshot: MenuBarSnapshot) -> String {
    snapshot.indexing == .paused ? "Resume Indexing" : "Pause Indexing"
  }

  /// Pausing applies only while a refresh is running.
  static func canToggleIndexing(_ snapshot: MenuBarSnapshot) -> Bool {
    snapshot.indexing == .refreshing || snapshot.indexing == .paused
  }
}

#if os(macOS)
/// The NSStatusItem with now playing, indexing and downloads, plus Open Myra, Pause or Resume
/// Indexing, and Quit. Created only when the Advanced switch is on.
@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
  private weak var coordinator: AppCoordinator?
  private var statusItem: NSStatusItem?
  private var timer: Timer?

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    super.init()
  }

  func install() {
    guard statusItem == nil else { return }
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let menu = NSMenu()
    menu.delegate = self
    item.menu = menu
    item.button?.image = NSImage(
      systemSymbolName: "play.rectangle.fill", accessibilityDescription: "Myra")
    item.button?.image?.isTemplate = true
    item.button?.imagePosition = .imageLeading
    statusItem = item
    refresh()
    timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
      Task { @MainActor in self?.refresh() }
    }
  }

  func remove() {
    timer?.invalidate()
    timer = nil
    if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
    statusItem = nil
  }

  /// Updates the icon text and tooltip. Runs every two seconds while the item is installed.
  func refresh() {
    guard let statusItem, let snapshot = currentSnapshot() else { return }
    statusItem.button?.title = MenuBarText.statusTitle(snapshot)
    statusItem.button?.toolTip = "Myra · " + MenuBarText.indexing(snapshot)
  }

  func menuNeedsUpdate(_ menu: NSMenu) {
    menu.removeAllItems()
    guard let snapshot = currentSnapshot() else { return }
    menu.addItem(infoItem(MenuBarText.nowPlaying(snapshot), symbol: "play.fill"))
    menu.addItem(infoItem(MenuBarText.indexing(snapshot), symbol: "externaldrive"))
    menu.addItem(infoItem(MenuBarText.downloads(snapshot), symbol: "arrow.down.circle"))
    menu.addItem(.separator())
    let open = NSMenuItem(title: "Open Myra", action: #selector(openMyra), keyEquivalent: "")
    open.target = self
    menu.addItem(open)
    let pause = NSMenuItem(
      title: MenuBarText.pauseIndexingTitle(snapshot), action: #selector(toggleIndexing),
      keyEquivalent: "")
    pause.target = self
    pause.isEnabled = MenuBarText.canToggleIndexing(snapshot)
    menu.addItem(pause)
    menu.addItem(.separator())
    let quit = NSMenuItem(title: "Quit Myra", action: #selector(quit), keyEquivalent: "q")
    quit.target = self
    menu.addItem(quit)
  }

  private func infoItem(_ title: String, symbol: String) -> NSMenuItem {
    let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
    item.isEnabled = false
    item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
    return item
  }

  private func currentSnapshot() -> MenuBarSnapshot? {
    guard let coordinator else { return nil }
    let active = coordinator.batches.filter { $0.status == .active }
    return MenuBarSnapshot(
      nowPlaying: coordinator.player.media.map {
        URL(fileURLWithPath: $0.entry.name).deletingPathExtension().lastPathComponent
      },
      isPlaying: coordinator.player.isPlaying,
      indexing: coordinator.library.refreshState,
      indexedVideos: coordinator.library.progress.matchesFound,
      activeDownloads: ShellBadges.downloads(coordinator.batches),
      downloadBytesPerSecond: active.reduce(0) { $0 + $1.downloadSpeed })
  }

  @objc private func openMyra() {
    NSApp.activate(ignoringOtherApps: true)
    let window = NSApp.windows.first { $0.canBecomeMain && !$0.isMiniaturized }
    window?.makeKeyAndOrderFront(nil)
  }

  @objc private func toggleIndexing() {
    coordinator?.library.isPaused.toggle()
    refresh()
  }

  @objc private func quit() {
    NSApp.terminate(nil)
  }
}
#endif
