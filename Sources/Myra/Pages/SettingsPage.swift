#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Toolbar tabs in the Settings window, in display order.
enum MyraSettingsTab: String, CaseIterable, Identifiable, Sendable {
  case general, library, downloads, playback, ai, torrents, data, advanced

  static let defaultTab: MyraSettingsTab = .general

  /// The tabs this build shows. Torrents are not part of the iPad build.
  static var visibleTabs: [MyraSettingsTab] {
    #if os(macOS)
    allCases
    #else
    allCases.filter { $0 != .torrents }
    #endif
  }

  var id: String { rawValue }

  var title: String {
    switch self {
    case .general: "General"
    case .library: "Library"
    case .downloads: "Downloads"
    case .playback: "Playback"
    case .ai: "AI"
    case .torrents: "Torrents"
    case .data: "Data"
    case .advanced: "Advanced"
    }
  }

  var symbol: String {
    switch self {
    case .general: "gearshape"
    case .library: "books.vertical"
    case .downloads: "arrow.down.circle"
    case .playback: "play.rectangle"
    case .ai: "sparkles"
    case .torrents: "network"
    case .data: "externaldrive"
    case .advanced: "bolt"
    }
  }
}

/// Inline validation rules for values typed into Settings. Changes are applied as they are valid.
enum MyraSettingsValidation {
  /// aria2 speed limits: 0 for unlimited, or a whole number with an optional K or M suffix.
  static func isValidSpeedLimit(_ text: String) -> Bool {
    text.range(of: #"^(0|[1-9][0-9]*)[KkMm]?$"#, options: .regularExpression) != nil
  }

  static func speedLimitMessage(_ text: String) -> String? {
    isValidSpeedLimit(text) ? nil : "Use 0 for no limit, or a number with K or M, such as 500K or 10M."
  }

  /// A TMDB read access token is one unbroken string. Empty input is handled by the caller.
  static func tokenMessage(_ token: String) -> String? {
    let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
    if trimmed.isEmpty { return nil }
    return trimmed.rangeOfCharacter(from: .whitespacesAndNewlines) == nil
      ? nil : "Paste the read access token without spaces or line breaks."
  }
}

/// The Settings window, laid out as the approved preview: an icon tab bar, grouped rows and a footer.
/// Owned controls save as they change. Embedded sections keep their own Save actions.
struct SettingsView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var store: EntertainmentStore
  @Bindable var settings: AppSettings
  @AppStorage(MyraAccent.defaultsKey) private var accentRaw = MyraAccent.default.rawValue
  @AppStorage(MyraAppearance.appearanceKey) private var iconAppearance = MyraIconAppearance
    .automatic.rawValue
  @AppStorage(MyraTranslucency.defaultsKey) private var translucencyRaw = MyraTranslucency
    .default.rawValue
  @State private var selectedTab: MyraSettingsTab = .defaultTab
  @State private var token = ""
  @State private var tokenMessage: String?

  init(coordinator: AppCoordinator, settings: AppSettings) {
    self.coordinator = coordinator
    self.settings = settings
    _store = ObservedObject(wrappedValue: coordinator.entertainment)
  }

  private var accent: Color { (MyraAccent(rawValue: accentRaw) ?? .default).color }

  var body: some View {
    VStack(spacing: 0) {
      SettingsTabBar(selection: $selectedTab, accent: accent)
      Form {
        switch selectedTab {
        case .general: generalSections
        case .library: librarySections
        case .downloads: downloadsSections
        case .playback: playbackSections
        case .ai: MyraAISettingsSection()
        case .torrents: TorrentSettingsSection(controller: coordinator.torrents)
        case .data: DataSettingsSection(backups: coordinator.backups)
        case .advanced: AdvancedSettingsSection()
        }
      }
      .formStyle(.grouped)
      .scrollContentBackground(.hidden)
      SettingsFooter()
    }
    .frame(maxWidth: SettingsMetrics.windowWidth)
    .frame(maxWidth: .infinity, maxHeight: .infinity)
    .background(MyraColor.window)
    .navigationTitle("Settings")
    // The Settings window is its own scene, so it needs the accent the main window applies.
    .environment(\.myraAccent, MyraAccent(rawValue: accentRaw) ?? .default)
    .tint(accent)
    .onChange(of: settingsSnapshot) { _, _ in coordinator.saveSettings() }
  }

  /// Every stored download and appearance value. A change to any of them is saved at once.
  private var settingsSnapshot: [String] {
    [
      settings.themeRaw, settings.downloadDirectory, settings.aria2PathOverride,
      String(settings.concurrentDownloads), String(settings.connectionsPerFile),
      String(settings.splitCount), String(settings.retryCount), settings.speedLimit,
    ]
  }

  private var accentBinding: Binding<MyraAccent> {
    Binding(
      get: { MyraAccent(rawValue: accentRaw) ?? .default },
      set: { accentRaw = $0.rawValue })
  }

  private var appVersion: String {
    Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "3.0.0"
  }

  @ViewBuilder private var generalSections: some View {
    #if os(macOS)
    SettingsGroup(title: "App icon") {
      SettingsRow(
        title: "Icon",
        detail: "Shown in the Dock. Finder shows the default Aperture icon. Changes apply at once."
      ) {
        MyraIconPicker()
      }
      SettingsRow(title: "Icon style") {
        SettingsSelect(
          title: "Icon style", selection: $iconAppearance,
          options: MyraIconAppearance.allCases.map {
            SettingsOption(value: $0.rawValue, title: $0.title)
          })
      }
    }
    #endif
    SettingsGroup(title: "Appearance") {
      SettingsRow(title: "Theme") {
        SettingsSelect(
          title: "Theme", selection: $settings.theme,
          options: AppThemeMode.allCases.map { SettingsOption(value: $0, title: $0.displayName) })
      }
      SettingsRow(
        title: "Accent colour",
        detail: "Buttons, selection, sidebar and in-app icons follow this colour"
      ) {
        MyraAccentPicker(selection: accentBinding)
      }
      SettingsRow(
        title: "Translucency",
        detail: "Blur behind the window, sidebar and header. Respects Reduce Transparency."
      ) {
        SegmentedControl(
          options: MyraTranslucency.allCases.map { SegmentOption(value: $0, title: $0.title) },
          selection: Binding(
            get: { MyraTranslucency(storedValue: translucencyRaw) },
            set: { translucencyRaw = $0.rawValue })
        )
        .accessibilityLabel("Translucency")
      }
    }
    SettingsUpdatesGroup(updater: coordinator.updater)
    SettingsGroup(title: "Notifications") {
      SettingsToggleRow(
        title: "Notify me about new episodes of followed series",
        isOn: Binding(
          get: { store.personal.preferences.notifications },
          set: { store.setNotifications($0) }))
    }
    Section("About Myra \(appVersion)") {
      TMDBAttributionView()
    }
  }

  @ViewBuilder private var librarySections: some View {
    MediaSettingsSection(library: coordinator.library, refresh: coordinator.refreshLibraryIndex)
    SettingsGroup(
      title: "Home Discovery",
      note:
        "Use the API Read Access Token, rather than the API key. Only title information and provider IDs are sent; source video URLs are excluded."
    ) {
      SettingsRow(
        title: "TMDB API Read Access Token",
        detail: "Press Return to save the token in Keychain."
      ) {
        SecureField("Token", text: $token)
          .textFieldStyle(.roundedBorder).labelsHidden()
          .frame(width: 220)
          .onSubmit { saveToken() }
      }
      if let message = MyraSettingsValidation.tokenMessage(token) {
        SettingsInlineMessage(text: message, color: .orange)
      }
      if let tokenMessage {
        SettingsInlineMessage(text: tokenMessage)
      }
      SettingsRow(title: "Saved token") {
        Button("Remove Token", role: .destructive) { removeToken() }
          .buttonStyle(MyraButtonStyle())
          .help("Remove the TMDB token from Keychain")
      }
      SettingsRow(title: "Get a TMDB token") {
        Link(
          "Open TMDB Settings",
          destination: URL(string: "https://www.themoviedb.org/settings/api")!
        )
        .buttonStyle(MyraButtonStyle())
      }
    }
  }

  @ViewBuilder private var downloadsSections: some View {
    #if os(iOS)
    SettingsGroup(
      title: "Location",
      note: "Downloads continue in the background while Myra is closed. Open the Files app to manage them."
    ) {
      SettingsRow(title: "Save downloads to", detail: "On My iPad › Myra › Downloads") {
        Button("Show in Files") {
          PlatformServices.reveal(URL(fileURLWithPath: settings.downloadDirectory, isDirectory: true))
        }
        .buttonStyle(MyraButtonStyle())
      }
    }
    AutomationSettingsGroup()
    #else
    SettingsGroup(title: "Location") {
      SettingsRow(
        title: "Save downloads to",
        detail: (settings.downloadDirectory as NSString).abbreviatingWithTildeInPath
      ) {
        Button("Choose…") { coordinator.chooseDownloadDirectory() }
          .buttonStyle(MyraButtonStyle())
      }
      SettingsRow(title: "aria2", detail: aria2Detail) {
        StatusPill(
          text: aria2Executable == nil ? "Missing" : "Ready",
          style: aria2Executable == nil ? .err : .ok)
      }
      SettingsRow(title: "aria2 path", detail: "Leave empty to detect it automatically") {
        TextField("Auto-detect", text: $settings.aria2PathOverride)
          .textFieldStyle(.roundedBorder).labelsHidden()
          .frame(width: 220)
      }
    }
    AutomationSettingsGroup()
    SettingsGroup(
      title: "Performance",
      note: "Changes apply immediately. Invalid values are flagged inline instead of being saved."
    ) {
      SettingsRow(title: "Simultaneous files") {
        SettingsSelect(
          title: "Simultaneous files", selection: $settings.concurrentDownloads,
          options: SettingsChoices.numbers(
            Array(1...20), current: settings.concurrentDownloads))
      }
      SettingsRow(title: "Connections per file") {
        SettingsSelect(
          title: "Connections per file", selection: $settings.connectionsPerFile,
          options: SettingsChoices.numbers(Array(1...16), current: settings.connectionsPerFile))
      }
      SettingsRow(title: "Split count") {
        SettingsSelect(
          title: "Split count", selection: $settings.splitCount,
          options: SettingsChoices.numbers(Array(1...16), current: settings.splitCount))
      }
      SettingsRow(title: "Retry count") {
        SettingsSelect(
          title: "Retry count", selection: $settings.retryCount,
          options: SettingsChoices.numbers(Array(1...20), current: settings.retryCount))
      }
      SettingsRow(title: "Speed limit", detail: "Per file") {
        SettingsSelect(
          title: "Speed limit", selection: $settings.speedLimit,
          options: SettingsChoices.strings(Self.speedLimits, current: settings.speedLimit))
      }
    }
    #endif
  }

  @ViewBuilder private var playbackSections: some View {
    PlayerSettingsGroup(player: coordinator.player)
    PlaybackVersionsGroup(store: store)
    SubtitleSettingsSection()
  }

  /// aria2 speed values: 0 is unlimited.
  static let speedLimits: [(String, String)] = [
    ("0", "Unlimited"), ("500K", "500 KB/s"), ("1M", "1 MB/s"), ("2M", "2 MB/s"),
    ("5M", "5 MB/s"), ("10M", "10 MB/s"), ("20M", "20 MB/s"),
  ]

  private var aria2Executable: URL? {
    try? Aria2Controller.discoverExecutable(override: settings.aria2PathOverride)
  }

  private var aria2Detail: String {
    guard let aria2Executable else { return "Not found. Install it with Homebrew: brew install aria2" }
    let detected = settings.aria2PathOverride.trimmingCharacters(in: .whitespaces).isEmpty
    return aria2Executable.path + (detected ? " · detected automatically" : "")
  }

  private func saveToken() {
    let value = token.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !value.isEmpty else { return }
    guard MyraSettingsValidation.tokenMessage(value) == nil else { return }
    do {
      try TMDBKeyStore.save(value)
      token = ""
      tokenMessage = "Token saved in Keychain."
      Task { await store.enrich() }
    } catch {
      tokenMessage = error.localizedDescription
    }
  }

  private func removeToken() {
    do {
      try TMDBKeyStore.save("")
      token = ""
      tokenMessage = "Token removed."
    } catch {
      tokenMessage = error.localizedDescription
    }
  }
}

/// The preview's `.tabs` bar: seven icon-over-label tabs, centred, with the selected tab filled and in accent.
private struct SettingsTabBar: View {
  @Binding var selection: MyraSettingsTab
  let accent: Color

  var body: some View {
    #if os(iOS)
    // A narrow iPad window (Split View) cannot fit every tab, so the row scrolls there.
    ViewThatFits(in: .horizontal) {
      row
      ScrollView(.horizontal, showsIndicators: false) { row.fixedSize(horizontal: true, vertical: false) }
        .background(MyraColor.fill)
    }
    #else
    row
    #endif
  }

  private var row: some View {
    HStack(spacing: 2) {
      Spacer(minLength: 0)
      ForEach(MyraSettingsTab.visibleTabs) { tab in
        let isSelected = selection == tab
        Button {
          selection = tab
        } label: {
          VStack(spacing: 3) {
            Image(systemName: tab.symbol)
              .font(.system(size: SettingsMetrics.tabIconSize))
              .frame(width: SettingsMetrics.tabIconSize, height: SettingsMetrics.tabIconSize)
            Text(tab.title)
              .font(.system(size: SettingsMetrics.tabLabelSize))
          }
          .frame(minWidth: SettingsMetrics.tabMinWidth)
          .padding(.horizontal, 10)
          .padding(.vertical, 6)
          .background(
            isSelected ? MyraColor.fill2 : Color.clear,
            in: RoundedRectangle(cornerRadius: SettingsMetrics.tabRadius))
          .foregroundStyle(isSelected ? accent : MyraColor.text2)
          .contentShape(RoundedRectangle(cornerRadius: SettingsMetrics.tabRadius))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(tab.title)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
      }
      Spacer(minLength: 0)
    }
    .padding(SettingsMetrics.tabBarPadding)
    .frame(maxWidth: .infinity)
    .background(MyraColor.fill)
    .overlay(alignment: .bottom) {
      Rectangle().fill(MyraColor.line).frame(height: 1)
    }
    .accessibilityElement(children: .contain)
    .accessibilityLabel("Settings sections")
  }
}

/// The preview's `.foot`: the automatic-save note on the left and a Close button on the right.
private struct SettingsFooter: View {
  #if os(iOS)
  @Environment(\.dismiss) private var dismiss
  #endif
  var body: some View {
    HStack(spacing: 8) {
      Text(SettingsMetrics.footerText)
        .font(.system(size: 12))
        .foregroundStyle(MyraColor.text2)
      Spacer(minLength: 8)
      Button("Close") {
        #if os(iOS)
        dismiss()
        #else
        NSApp.keyWindow?.performClose(nil)
        #endif
      }
      .buttonStyle(MyraButtonStyle())
      .keyboardShortcut(.cancelAction)
    }
    .padding(.horizontal, 22)
    .padding(.vertical, 12)
    .background(MyraColor.fill)
    .overlay(alignment: .top) {
      Rectangle().fill(MyraColor.line).frame(height: 1)
    }
  }
}

/// The Updates panel: the automatic-check switch, the installed version with Check Now, and the status note.
private struct SettingsUpdatesGroup: View {
  @ObservedObject var updater: AppUpdater

  private var installedVersion: String {
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
    return "Myra \(version ?? "Development") (\(build ?? "—"))"
  }

  private var statusNote: String {
    var lines = [
      "Checks quietly when Myra is open, at most once a day. Installation always requires your confirmation."
    ]
    if let date = updater.lastCheck {
      lines.append("Last checked \(date.formatted()).")
    }
    lines.append(updater.configurationProblem ?? updater.status)
    return lines.joined(separator: "\n")
  }

  var body: some View {
    SettingsGroup(title: "Updates", note: statusNote) {
      SettingsToggleRow(
        title: "Automatically check for updates",
        isOn: Binding(get: { updater.automaticallyChecks }, set: updater.setAutomaticChecks)
      )
      .disabled(updater.configurationProblem != nil)
      SettingsRow(title: "Current version", detail: installedVersion) {
        Button("Check Now") { updater.checkForUpdates() }
          .buttonStyle(MyraButtonStyle())
          .disabled(!updater.canCheckForUpdates)
      }
    }
  }
}

/// The Playback tab's Player group: the player's own saved preferences, applied to future
/// playback and saved as they change.
private struct PlayerSettingsGroup: View {
  @ObservedObject var player: EmbeddedPlayerModel

  private static let languages: [(String, String)] = [
    ("default", "Source default"), ("en", "English"), ("bn", "Bengali"), ("hi", "Hindi"),
    ("fr", "French"), ("es", "Spanish"), ("ja", "Japanese"), ("ko", "Korean"),
  ]

  var body: some View {
    SettingsGroup(
      title: "Player",
      note: "Applies to future playback. Track choices you make in the player stay for that video."
    ) {
      SettingsToggleRow(title: "Autoplay next episode", isOn: $player.personalState.autoplay)
      SettingsToggleRow(
        title: "Skip intros and credits", detail: "Uses the markers you set in the player",
        isOn: $player.personalState.automaticSkipping)
      SettingsRow(title: "Default speed") {
        SettingsSelect(
          title: "Default speed",
          selection: Binding(get: { player.speed }, set: { player.setSpeed($0) }),
          options: [Float(0.5), 0.75, 1, 1.25, 1.5, 2].map {
            SettingsOption(value: $0, title: String(format: "%.2g×", $0))
          })
      }
      SettingsRow(title: "Audio language") {
        SettingsSelect(
          title: "Audio language", selection: $player.personalState.audioLanguage,
          options: SettingsChoices.strings(
            Self.languages, current: player.personalState.audioLanguage))
      }
      SettingsRow(title: "Subtitle language") {
        SettingsSelect(
          title: "Subtitle language", selection: $player.personalState.subtitleLanguage,
          options: SettingsChoices.strings(
            [("off", "Off")] + Self.languages, current: player.personalState.subtitleLanguage))
      }
    }
    .onChange(of: player.personalState) { _, _ in player.savePersonalState() }
  }
}
