#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
@preconcurrency import CoreSpotlight
import Foundation
import UniformTypeIdentifiers
import UserNotifications

/// The Settings > Advanced and Downloads > Automation switches. Each reads UserDefaults with the
/// same keys `@AppStorage` writes, so the views and the coordinator always agree.
enum MyraSystemPreferences {
  static let automaticDownloadsKey = "automaticDownloads.enabled"
  static let windowStartKey = "automaticDownloads.windowStartMinute"
  static let windowEndKey = "automaticDownloads.windowEndMinute"
  static let menuBarKey = "system.menuBar.enabled"
  static let spotlightKey = "system.spotlight.enabled"
  static let dockBadgeKey = "system.dockBadge.enabled"
  static let notificationsKey = "system.notifications.enabled"

  /// Off by default: automatic downloads start network transfers without a prompt.
  static func automaticDownloadsEnabled(_ defaults: UserDefaults = .standard) -> Bool {
    defaults.object(forKey: automaticDownloadsKey) as? Bool ?? false
  }

  static func offPeakWindow(_ defaults: UserDefaults = .standard) -> OffPeakWindow {
    let start = defaults.object(forKey: windowStartKey) as? Int ?? OffPeakWindow.standard.startMinute
    let end = defaults.object(forKey: windowEndKey) as? Int ?? OffPeakWindow.standard.endMinute
    let window = OffPeakWindow(startMinute: start, endMinute: end)
    return window.isValid ? window : .standard
  }

  static func menuBarEnabled(_ defaults: UserDefaults = .standard) -> Bool {
    defaults.object(forKey: menuBarKey) as? Bool ?? true
  }

  static func spotlightEnabled(_ defaults: UserDefaults = .standard) -> Bool {
    defaults.object(forKey: spotlightKey) as? Bool ?? true
  }

  static func dockBadgeEnabled(_ defaults: UserDefaults = .standard) -> Bool {
    defaults.object(forKey: dockBadgeKey) as? Bool ?? true
  }

  /// Off by default; macOS permission is requested only when the user turns this on.
  static func notificationsEnabled(_ defaults: UserDefaults = .standard) -> Bool {
    defaults.object(forKey: notificationsKey) as? Bool ?? false
  }
}

/// The Dock badge text: the number of downloads in progress, or nothing when there are none.
enum DockBadge {
  static func label(activeDownloads: Int) -> String? {
    activeDownloads > 0 ? "\(activeDownloads)" : nil
  }
}

/// Events worth a notification. Each is posted only when its setting is on and macOS allows it.
enum SystemNotice: Equatable, Sendable {
  case downloadFinished(title: String)
  /// Posted only when the index changed, so a refresh with no new titles stays quiet.
  case indexingFinished
  case sourceFailures(summary: String)
}

struct SystemNoticeContent: Equatable, Sendable {
  let identifier: String
  let title: String
  let body: String
}

enum SystemNoticeFormatter {
  static func content(for notice: SystemNotice) -> SystemNoticeContent {
    switch notice {
    case .downloadFinished(let title):
      SystemNoticeContent(
        identifier: "Myra.download.\(title)", title: "Download finished", body: title)
    case .indexingFinished:
      SystemNoticeContent(
        identifier: "Myra.indexing", title: "Library updated",
        body: "New titles were added to your library.")
    case .sourceFailures(let summary):
      SystemNoticeContent(
        identifier: "Myra.sources", title: "Source problem",
        body: summary.isEmpty ? "A source could not be scanned." : summary)
    }
  }
}

/// One title offered to Spotlight. Only the display name and kind are sent: never file paths or contents.
struct SpotlightTitleEntry: Equatable, Sendable {
  let identifier: String
  let title: String
  let contentDescription: String
}

enum SpotlightTitleIndex {
  static let domainIdentifier = "com.mehedee.Myra.titles"
  private static let prefix = "myra.title."

  static func identifier(for titleID: String) -> String { prefix + titleID }

  static func titleID(from identifier: String) -> String? {
    identifier.hasPrefix(prefix) ? String(identifier.dropFirst(prefix.count)) : nil
  }

  static func entries(for titles: [EntertainmentTitle]) -> [SpotlightTitleEntry] {
    titles.map { title in
      let kind = title.kind == .series ? "TV Series" : "Movie"
      let description = [kind, title.year].compactMap { $0 }.joined(separator: " · ")
      return SpotlightTitleEntry(
        identifier: identifier(for: title.id), title: title.displayName,
        contentDescription: "Myra · " + description)
    }
  }
}

/// A route requested from outside the window: an App Intent, or a Spotlight result.
enum MyraSystemRoute: Equatable, Sendable {
  case play(title: String)
  case search(query: String)
  case openTitle(id: String)
}

/// Holds routes requested before the coordinator is ready, and delivers them once it is.
@MainActor
final class MyraSystemRouter {
  static let shared = MyraSystemRouter()

  private var pending: [MyraSystemRoute] = []

  var handler: (@MainActor (MyraSystemRoute) -> Void)? {
    didSet {
      guard let handler else { return }
      let routes = pending
      pending.removeAll()
      routes.forEach(handler)
    }
  }

  func post(_ route: MyraSystemRoute) {
    if let handler {
      handler(route)
    } else {
      pending.append(route)
    }
  }
}

/// System glue owned by the coordinator: the menu bar item, Dock badge, notifications and Spotlight.
/// Each feature is applied only when its setting is on, and turned off by removing what it added.
@MainActor
final class SystemIntegrationController {
  private weak var coordinator: AppCoordinator?
  #if os(macOS)
  private var menuBar: MenuBarController?
  #endif
  private var appliedMenuBar = false
  private var appliedSpotlight: Bool?
  /// The entries last sent to Spotlight. Catalogue reloads during indexing are skipped when nothing changed.
  private var indexedSpotlightEntries: [SpotlightTitleEntry]?

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
  }

  /// True inside the XCTest host. Status items, Spotlight and Dock state are left alone there.
  static var isRunningTests: Bool {
    ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
  }

  /// Applies every switch once at launch, and again whenever a switch changes.
  func apply() {
    guard !Self.isRunningTests else { return }
    applyMenuBar()
    applyDockBadge()
    applySpotlightState()
  }

  func downloadsChanged() {
    applyDockBadge()
  }

  func titlesChanged(_ titles: [EntertainmentTitle]) {
    guard MyraSystemPreferences.spotlightEnabled() else { return }
    let entries = SpotlightTitleIndex.entries(for: titles)
    guard entries != indexedSpotlightEntries else { return }
    indexedSpotlightEntries = entries
    let items = entries.map { entry -> CSSearchableItem in
      let attributes = CSSearchableItemAttributeSet(contentType: UTType.movie)
      attributes.title = entry.title
      attributes.displayName = entry.title
      attributes.contentDescription = entry.contentDescription
      return CSSearchableItem(
        uniqueIdentifier: entry.identifier,
        domainIdentifier: SpotlightTitleIndex.domainIdentifier, attributeSet: attributes)
    }
    let index = CSSearchableIndex.default()
    index.deleteSearchableItems(withDomainIdentifiers: [SpotlightTitleIndex.domainIdentifier]) {
      _ in
      guard !items.isEmpty else { return }
      index.indexSearchableItems(items) { _ in }
    }
  }

  func deliver(_ notice: SystemNotice) {
    guard MyraSystemPreferences.notificationsEnabled() else { return }
    let content = SystemNoticeFormatter.content(for: notice)
    Task {
      let center = UNUserNotificationCenter.current()
      let settings = await center.notificationSettings()
      guard settings.authorizationStatus == .authorized
        || settings.authorizationStatus == .provisional
      else { return }
      let message = UNMutableNotificationContent()
      message.title = content.title
      message.body = content.body
      let request = UNNotificationRequest(
        identifier: content.identifier, content: message, trigger: nil)
      try? await center.add(request)
    }
  }

  /// Called from the Settings switch. macOS permission is requested only from here, when the user enables notifications.
  static func requestNotificationPermission() async -> Bool {
    // The iPad app icon badge needs badge permission; the Mac Dock badge does not.
    #if os(iOS)
    let options: UNAuthorizationOptions = [.alert, .sound, .badge]
    #else
    let options: UNAuthorizationOptions = [.alert, .sound]
    #endif
    return (try? await UNUserNotificationCenter.current().requestAuthorization(options: options))
      ?? false
  }

  private func applyMenuBar() {
    #if os(macOS)
    let wanted = MyraSystemPreferences.menuBarEnabled()
    guard wanted != appliedMenuBar else {
      if wanted { menuBar?.refresh() }
      return
    }
    appliedMenuBar = wanted
    if wanted {
      guard let coordinator else { return }
      let controller = MenuBarController(coordinator: coordinator)
      controller.install()
      menuBar = controller
    } else {
      menuBar?.remove()
      menuBar = nil
    }
    #endif
  }

  private func applyDockBadge() {
    guard let coordinator else { return }
    let active = ShellBadges.downloads(coordinator.batches)
    let label =
      MyraSystemPreferences.dockBadgeEnabled() ? DockBadge.label(activeDownloads: active) : nil
    #if os(macOS)
    let dockTile = NSApplication.shared.dockTile
    guard dockTile.badgeLabel != label else { return }
    dockTile.badgeLabel = label
    #else
    // The app icon badge on iPadOS takes a number and needs notification permission.
    let count = label == nil ? 0 : active
    Task { try? await UNUserNotificationCenter.current().setBadgeCount(count) }
    #endif
  }

  private func applySpotlightState() {
    let enabled = MyraSystemPreferences.spotlightEnabled()
    guard enabled != appliedSpotlight else { return }
    appliedSpotlight = enabled
    if enabled {
      if let titles = coordinator?.entertainment.catalogue { titlesChanged(titles) }
    } else {
      indexedSpotlightEntries = nil
      CSSearchableIndex.default().deleteSearchableItems(
        withDomainIdentifiers: [SpotlightTitleIndex.domainIdentifier]) { _ in }
    }
  }
}
