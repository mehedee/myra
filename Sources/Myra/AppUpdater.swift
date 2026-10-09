import AppKit
import Combine
import Foundation
import Sparkle
import SwiftUI

/// Build-time trust configuration. Neither the feed nor the trusted key is user-overridable.
struct UpdateConfiguration {
  static let feedURL = URL(
    string: "https://github.com/mehedee/h5ai-streamer/releases/latest/download/appcast.xml")!
  static func problem(in info: [String: Any]) -> String? {
    guard let raw = info["SUFeedURL"] as? String, let url = URL(string: raw),
      url.scheme == "https", url.host != nil, url.user == nil, url.password == nil
    else { return "A secure update feed is not configured in this build." }
    guard let key = info["SUPublicEDKey"] as? String, Data(base64Encoded: key)?.count == 32 else {
      return "The trusted update-signing key is missing from this build."
    }
    guard info["SUVerifyUpdateBeforeExtraction"] as? Bool == true,
      info["SURequireSignedFeed"] as? Bool == true,
      info["SUSignedFeedFailureExpirationInterval"] as? Int == 0,
      info["SUAllowsAutomaticUpdates"] as? Bool == false
    else { return "Required update safety settings are missing from this build." }
    return nil
  }
}

/// Sparkle owns scheduling, version/OS compatibility, signature validation and transactional installation.
@MainActor
final class AppUpdater: NSObject, ObservableObject, SPUUpdaterDelegate {
  @Published private(set) var canCheckForUpdates = false
  @Published private(set) var lastCheck: Date?
  @Published private(set) var status = "Updates have not been checked yet."
  @Published private(set) var configurationProblem: String?
  @Published private(set) var automaticallyChecks = true
  private var controller: SPUStandardUpdaterController?
  private var started = false

  func start() {
    guard !started else { return }
    started = true
    guard Bundle.main.bundleURL.pathExtension == "app",
      ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil
    else {
      configurationProblem = "Update checks are available in the installed Myra app."
      return
    }
    if let problem = UpdateConfiguration.problem(in: Bundle.main.infoDictionary ?? [:]) {
      configurationProblem = problem
      status = problem
      return
    }
    let controller = SPUStandardUpdaterController(
      startingUpdater: false, updaterDelegate: self, userDriverDelegate: nil)
    self.controller = controller
    let updater = controller.updater
    updater.publisher(for: \.canCheckForUpdates).assign(to: &$canCheckForUpdates)
    updater.publisher(for: \.lastUpdateCheckDate).assign(to: &$lastCheck)
    updater.publisher(for: \.automaticallyChecksForUpdates).assign(to: &$automaticallyChecks)
    do { try updater.start() } catch {
      configurationProblem = error.localizedDescription
      status = error.localizedDescription
    }
  }

  func checkForUpdates() {
    guard canCheckForUpdates, let controller else { return }
    status = "Checking for updates…"
    controller.checkForUpdates(nil)
  }

  func setAutomaticChecks(_ enabled: Bool) {
    controller?.updater.automaticallyChecksForUpdates = enabled
  }

  func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
    status = "Myra \(item.displayVersionString) is available."
  }

  func updater(
    _ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?
  ) {
    if let error {
      let nsError = error as NSError
      status =
        nsError.domain == SUSparkleErrorDomain && nsError.code == SUError.noUpdateError.rawValue
        ? "You’re up to date." : "Update check failed: \(error.localizedDescription)"
    }
  }
}

struct UpdateCheckButton: View {
  @ObservedObject var updater: AppUpdater
  var body: some View {
    Button("Check for Updates…") { updater.checkForUpdates() }
      .disabled(!updater.canCheckForUpdates)
  }
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
      Toggle(
        "Automatically check for updates",
        isOn: Binding(get: { updater.automaticallyChecks }, set: updater.setAutomaticChecks)
      )
      .disabled(updater.configurationProblem != nil)
      Text(
        "Checks quietly when Myra is open, at most once a day. Installation always requires your confirmation."
      )
      .font(.caption).foregroundStyle(.secondary)
      UpdateCheckButton(updater: updater)
      if let date = updater.lastCheck { LabeledContent("Last checked", value: date.formatted()) }
      Text(updater.configurationProblem ?? updater.status).font(.caption).foregroundStyle(
        .secondary)
      Text(
        "Personal ad-hoc-signed build; not notarized. Update archives and feeds must carry the trusted release signature. Keep the previous DMG for recovery."
      )
      .font(.caption).foregroundStyle(.secondary)
    }
  }
}
