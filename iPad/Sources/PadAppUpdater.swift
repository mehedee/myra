import Combine
import SwiftUI

/// iPadOS installs Myra from Xcode or TestFlight, so there is no in-app updater.
/// This type keeps the members the shared views and coordinator read.
@MainActor
final class AppUpdater: ObservableObject {
  @Published private(set) var canCheckForUpdates = false
  @Published private(set) var lastCheck: Date?
  @Published private(set) var status = "Updates are installed from Xcode or TestFlight on iPad."
  @Published private(set) var configurationProblem: String?
  @Published private(set) var automaticallyChecks = false

  func start() {}
  func checkForUpdates() {}
  func setAutomaticChecks(_ enabled: Bool) {}
}

struct UpdateCheckButton: View {
  @ObservedObject var updater: AppUpdater
  var body: some View { EmptyView() }
}

struct UpdateSettingsSection: View {
  @ObservedObject var updater: AppUpdater
  var body: some View {
    Section("Updates") {
      LabeledContent(
        "Installed version",
        value:
          "\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development") (\(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"))"
      )
      Text(updater.status).font(.caption).foregroundStyle(.secondary)
    }
  }
}
