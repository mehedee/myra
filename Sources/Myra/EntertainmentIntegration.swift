import Foundation
import SwiftData

/// Portable, non-secret preferences. Imported local paths require explicit reconnection.
struct EntertainmentAppPreferences: Codable, Sendable {
  var theme: String
  var concurrentDownloads: Int
  var connectionsPerFile: Int
  var splitCount: Int
  var retryCount: Int
  var speedLimit: String
  var previousDownloadDirectory: String?
}

extension AppCoordinator {
  func exportEntertainmentSources() -> [EntertainmentSource] {
    categories.map { category in
      var components = URLComponents(string: category.rootURLString)
      components?.user = nil
      components?.password = nil
      // URL query strings may contain access tokens; portable sources require reconnection.
      components?.query = nil
      components?.fragment = nil
      return EntertainmentSource(
        id: category.id, name: category.name, url: components?.url?.absoluteString ?? "")
    }
  }

  func exportEntertainmentPreferences() -> EntertainmentAppPreferences? {
    guard let settings else { return nil }
    return EntertainmentAppPreferences(
      theme: settings.themeRaw, concurrentDownloads: settings.concurrentDownloads,
      connectionsPerFile: settings.connectionsPerFile, splitCount: settings.splitCount,
      retryCount: settings.retryCount, speedLimit: settings.speedLimit,
      previousDownloadDirectory: settings.downloadDirectory)
  }
}
