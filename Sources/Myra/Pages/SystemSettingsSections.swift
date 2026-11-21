import SwiftUI

/// Settings > Advanced: the system integrations. Each switch is saved at once and read by
/// `SystemIntegrationController` through the same UserDefaults keys.
struct AdvancedSettingsSection: View {
  @AppStorage(MyraSystemPreferences.menuBarKey) private var menuBar = true
  @AppStorage(MyraSystemPreferences.spotlightKey) private var spotlight = true
  @AppStorage(MyraSystemPreferences.dockBadgeKey) private var dockBadge = true
  @AppStorage(MyraSystemPreferences.notificationsKey) private var notifications = false
  @State private var notificationMessage: String?

  var body: some View {
    SettingsGroup(
      title: "System integration",
      note: "Each item can be switched off. Myra sends nothing off this \(MyraDevice.name) for these features."
    ) {
      #if os(macOS)
      SettingsToggleRow(
        title: "Menu bar item",
        detail: "Now playing, indexing and downloads in the menu bar",
        isOn: $menuBar)
      #endif
      SettingsToggleRow(
        title: "Spotlight",
        detail: "Search your title names from Spotlight. File names, paths and contents are not shared.",
        isOn: $spotlight)
      SettingsRow(
        title: "Shortcuts",
        detail: "Play Title and Search Library appear in the Shortcuts app"
      ) {
        StatusPill(text: "Available", style: .ok)
      }
      SettingsRow(
        title: "Widgets",
        detail: "Continue Watching and Tonight’s Pick. Not part of this build."
      ) {
        StatusPill(text: "Deferred", style: .neutral)
      }
      #if os(macOS)
      SettingsToggleRow(
        title: "Dock badge",
        detail: "Shows how many downloads are in progress",
        isOn: $dockBadge)
      #else
      SettingsToggleRow(
        title: "App icon badge",
        detail: "Shows how many downloads are in progress. Needs notification permission.",
        isOn: $dockBadge)
      #endif
      SettingsToggleRow(
        title: "Notify when finished",
        detail: "Finished downloads, library updates with new titles, and source problems",
        isOn: Binding(get: { notifications }, set: setNotifications))
      if let notificationMessage {
        SettingsInlineMessage(text: notificationMessage, color: .orange)
      }
    }
  }

  /// macOS permission is asked for only when the user turns notifications on.
  private func setNotifications(_ enabled: Bool) {
    guard enabled else {
      notifications = false
      notificationMessage = nil
      return
    }
    Task {
      let granted = await SystemIntegrationController.requestNotificationPermission()
      notifications = granted
      notificationMessage =
        granted ? nil : "Notifications are off for Myra in System Settings. Turn them on there to receive them."
    }
  }
}

/// Settings > Downloads > Automation: the switch and the off-peak window for new episodes.
struct AutomationSettingsGroup: View {
  @AppStorage(MyraSystemPreferences.automaticDownloadsKey) private var enabled = false
  @AppStorage(MyraSystemPreferences.windowStartKey) private var windowStart =
    OffPeakWindow.standard.startMinute
  @AppStorage(MyraSystemPreferences.windowEndKey) private var windowEnd =
    OffPeakWindow.standard.endMinute

  private static let hourOptions: [SettingsOption<Int>] = (0..<24).map {
    SettingsOption(value: $0 * 60, title: OffPeakWindow.clockText($0 * 60))
  }

  var body: some View {
    SettingsGroup(
      title: "Automation",
      note: "Myra must be open for automatic downloads to start. Nothing starts at login."
    ) {
      SettingsToggleRow(
        title: "Download new episodes of followed series",
        detail: "Found during indexing, then queued for the window below",
        isOn: $enabled)
      SettingsRow(title: "Download window", detail: "Starts only inside this window") {
        HStack(spacing: 8) {
          SettingsSelect(title: "Window start", selection: $windowStart, options: Self.hourOptions)
          Text("to").foregroundStyle(MyraColor.text2)
          SettingsSelect(title: "Window end", selection: $windowEnd, options: Self.hourOptions)
        }
      }
      if windowStart == windowEnd {
        SettingsInlineMessage(text: "Choose two different times for the window.", color: .orange)
      }
    }
  }
}
