#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Menu routing: every menu item posts a `MyraCommand` on `NotificationCenter.default`
/// under `MyraCommand.notification`, with the command identifier in `userInfo["command"]`.
/// `RootView` handles navigation, downloads, appearance and playback. Pages may observe the
/// same notification for page-owned actions. Items without a handler are disabled with a
/// `.help` reason in `MyraCommandContext`-driven availability, so they never fail silently.
enum MyraCommand: Hashable, Sendable {
  case showHome, showWatchlist, showInsights, showSources, showDownloads, showTransfers
  /// Collapses the sidebar to its icon rail, or expands it again.
  case toggleSidebar
  case addSource, openTorrentLink, importTorrentFile, refreshLibrary, find
  case pauseAllDownloads, resumeAllDownloads, clearCompletedDownloads
  case setAppearance(AppThemeMode)
  case togglePlayback, skipBack, skipForward, previousEpisode, nextEpisode
  case toggleFullScreen
  /// Toolbar actions on Home. Routed like menu commands; the Home page performs them.
  case pickSomething, askMyra

  static let notification = Notification.Name("com.mehedee.Myra.menuCommand")
  static let identifierKey = "command"

  static let allCommands: [MyraCommand] =
    [
      .showHome, .showWatchlist, .showInsights, .showSources, .showDownloads, .showTransfers,
      .toggleSidebar,
      .addSource, .openTorrentLink, .importTorrentFile, .refreshLibrary, .find,
      .pauseAllDownloads, .resumeAllDownloads, .clearCompletedDownloads,
      .togglePlayback, .skipBack, .skipForward, .previousEpisode, .nextEpisode,
      .toggleFullScreen, .pickSomething, .askMyra,
    ] + AppThemeMode.allCases.map(MyraCommand.setAppearance)

  /// Stable string identifier used in notification user info.
  var identifier: String {
    switch self {
    case .showHome: "showHome"
    case .showWatchlist: "showWatchlist"
    case .showInsights: "showInsights"
    case .toggleSidebar: "toggleSidebar"
    case .showSources: "showSources"
    case .showDownloads: "showDownloads"
    case .showTransfers: "showTransfers"
    case .addSource: "addSource"
    case .openTorrentLink: "openTorrentLink"
    case .importTorrentFile: "importTorrentFile"
    case .refreshLibrary: "refreshLibrary"
    case .find: "find"
    case .pauseAllDownloads: "pauseAllDownloads"
    case .resumeAllDownloads: "resumeAllDownloads"
    case .clearCompletedDownloads: "clearCompletedDownloads"
    case .setAppearance(let mode): "setAppearance.\(mode.rawValue)"
    case .togglePlayback: "togglePlayback"
    case .skipBack: "skipBack"
    case .skipForward: "skipForward"
    case .previousEpisode: "previousEpisode"
    case .nextEpisode: "nextEpisode"
    case .toggleFullScreen: "toggleFullScreen"
    case .pickSomething: "pickSomething"
    case .askMyra: "askMyra"
    }
  }

  init?(identifier: String) {
    guard let command = Self.allCommands.first(where: { $0.identifier == identifier }) else {
      return nil
    }
    self = command
  }

  init?(notification: Notification) {
    guard let identifier = notification.userInfo?[Self.identifierKey] as? String else {
      return nil
    }
    self.init(identifier: identifier)
  }

  func post(center: NotificationCenter = .default) {
    center.post(
      name: Self.notification, object: nil, userInfo: [Self.identifierKey: identifier])
  }
}

/// Live state the menus need to decide which items can act right now.
struct MyraCommandContext: Equatable, Sendable {
  var isPlayingMedia = false
  var hasActiveDownloads = false
  var hasPausedDownloads = false
  var hasCompletedDownloads = false
  var currentSection: AppSection = .home
  /// The playing item is an episode, so Previous and Next Episode apply. Movies have neither.
  var isPlayingEpisode = false
  var canGoPreviousEpisode = false
  var canGoNextEpisode = false
}

extension MyraCommandContext {
  /// The live state of the app, shared by the menu bar and the toolbar so both enable the same actions.
  @MainActor
  init(coordinator: AppCoordinator, player: EmbeddedPlayerModel) {
    self.init(
      isPlayingMedia: player.media != nil,
      hasActiveDownloads: coordinator.batches.contains { $0.status == .active },
      hasPausedDownloads: coordinator.batches.contains { $0.status == .paused },
      hasCompletedDownloads: coordinator.batches.contains { $0.status == .completed },
      currentSection: coordinator.currentSection,
      isPlayingEpisode: player.isPlayingEpisode,
      canGoPreviousEpisode: player.isPlayingEpisode && player.canGoPrevious,
      canGoNextEpisode: player.isPlayingEpisode && player.canGoNext)
  }
}

enum MyraCommandAvailability: Equatable {
  case enabled
  case disabled(reason: String)

  var isEnabled: Bool {
    if case .enabled = self { return true }
    return false
  }
}

extension MyraCommand {
  func availability(in context: MyraCommandContext) -> MyraCommandAvailability {
    switch self {
    case .addSource:
      context.currentSection == .sources
        ? .enabled : .disabled(reason: "Open Sources to add a source.")
    case .find:
      [AppSection.home, .watchlist, .sources, .downloads].contains(context.currentSection)
        ? .enabled
        : .disabled(reason: "Find is available on Home, Watchlist, Sources and Downloads.")
    case .togglePlayback, .skipBack, .skipForward:
      context.isPlayingMedia ? .enabled : .disabled(reason: "Open a title to control playback.")
    case .previousEpisode:
      !context.isPlayingEpisode
        ? .disabled(reason: "Previous Episode applies to series.")
        : (context.canGoPreviousEpisode ? .enabled : .disabled(reason: "This is the first episode."))
    case .nextEpisode:
      !context.isPlayingEpisode
        ? .disabled(reason: "Next Episode applies to series.")
        : (context.canGoNextEpisode ? .enabled : .disabled(reason: "This is the last episode."))
    case .pauseAllDownloads:
      context.hasActiveDownloads ? .enabled : .disabled(reason: "No downloads are running.")
    case .resumeAllDownloads:
      context.hasPausedDownloads ? .enabled : .disabled(reason: "No downloads are paused.")
    case .clearCompletedDownloads:
      context.hasCompletedDownloads
        ? .enabled : .disabled(reason: "No completed downloads to clear.")
    default:
      .enabled
    }
  }
}

/// The app's menu bar, following Apple's menu conventions.
struct MyraCommands: Commands {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var player: EmbeddedPlayerModel
  @AppStorage(SidebarState.collapsedKey) private var sidebarCollapsed = false

  private var context: MyraCommandContext {
    MyraCommandContext(coordinator: coordinator, player: player)
  }

  var body: some Commands {
    #if os(macOS)
    CommandGroup(replacing: .appInfo) {
      Button("About Myra") { NSApplication.shared.orderFrontStandardAboutPanel(nil) }
    }
    #endif
    CommandGroup(after: .appInfo) {
      UpdateCheckButton(updater: coordinator.updater)
    }

    CommandGroup(replacing: .newItem) {
      item(.addSource, "Add Source…").keyboardShortcut("n", modifiers: .command)
      Divider()
      item(.openTorrentLink, "Open Torrent Link…").keyboardShortcut("l", modifiers: .command)
      item(.importTorrentFile, "Import .torrent File…").keyboardShortcut("o", modifiers: .command)
      Divider()
      item(.refreshLibrary, "Refresh Library").keyboardShortcut("r", modifiers: .command)
    }

    CommandGroup(replacing: .textEditing) {
      item(.find, "Find").keyboardShortcut("f", modifiers: .command)
    }

    CommandGroup(before: .sidebar) {
      item(.showHome, "Home").keyboardShortcut("1", modifiers: .command)
      item(.showSources, "Sources").keyboardShortcut("2", modifiers: .command)
      item(.showDownloads, "Downloads").keyboardShortcut("3", modifiers: .command)
      item(.showTransfers, "Transfers").keyboardShortcut("4", modifiers: .command)
      item(.showInsights, "Insights").keyboardShortcut("7", modifiers: .command)
      item(.showWatchlist, "Watchlist").keyboardShortcut("8", modifiers: .command)
      Divider()
      item(.toggleSidebar, sidebarCollapsed ? "Show Sidebar" : "Hide Sidebar")
        .keyboardShortcut("s", modifiers: [.control, .command])
      Menu("Appearance") {
        Picker("Appearance", selection: appearanceBinding) {
          ForEach(AppThemeMode.allCases, id: \.self) { mode in
            Text(mode.displayName).tag(mode)
          }
        }
        .pickerStyle(.inline)
      }
      item(.toggleFullScreen, "Enter Full Screen").keyboardShortcut("f", modifiers: [.control, .command])
      Divider()
    }

    CommandMenu("Playback") {
      item(.togglePlayback, "Play/Pause").keyboardShortcut(.space, modifiers: [])
      Divider()
      item(.skipBack, "Skip Back 10 Seconds")
      item(.skipForward, "Skip Forward 10 Seconds")
      Divider()
      item(.previousEpisode, "Previous Episode").keyboardShortcut("[", modifiers: .command)
      item(.nextEpisode, "Next Episode").keyboardShortcut("]", modifiers: .command)
    }

    CommandMenu("Downloads") {
      item(.showDownloads, "Show Downloads")
      Divider()
      item(.pauseAllDownloads, "Pause All")
      item(.resumeAllDownloads, "Resume All")
      Divider()
      item(.clearCompletedDownloads, "Clear Completed")
        .keyboardShortcut("k", modifiers: [.command, .shift])
    }
  }

  private var appearanceBinding: Binding<AppThemeMode> {
    Binding(
      get: { coordinator.settings?.theme ?? .system },
      set: { MyraCommand.setAppearance($0).post() })
  }

  private func item(_ command: MyraCommand, _ title: String) -> some View {
    let availability = command.availability(in: context)
    return Button(title) { command.post() }
      .disabled(!availability.isEnabled)
      .help(reason(for: availability) ?? title)
  }

  private func reason(for availability: MyraCommandAvailability) -> String? {
    if case .disabled(let reason) = availability { return reason }
    return nil
  }
}
