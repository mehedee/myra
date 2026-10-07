import Foundation
import SwiftData

enum EntryKind: String, Codable, Sendable {
  case folder
  case file
}

struct DirectoryEntry: Identifiable, Hashable, Sendable, Codable {
  let id: URL
  let name: String
  let url: URL
  let kind: EntryKind
  let size: Int64?
  let modifiedAt: Date?

  init(name: String, url: URL, kind: EntryKind, size: Int64? = nil, modifiedAt: Date? = nil) {
    self.id = url
    self.name = name
    self.url = url
    self.kind = kind
    self.size = size
    self.modifiedAt = modifiedAt
  }
}

struct DirectoryListing: Sendable, Codable {
  let url: URL
  let entries: [DirectoryEntry]
  let artworkURL: URL?
}

enum SearchScope: String, CaseIterable, Sendable {
  case current = "Current"
  case global = "Global"
}

struct LibrarySearchState {
  private(set) var scope: SearchScope = .global
  private(set) var query = ""
  private(set) var currentQuery = ""

  mutating func select(_ scope: SearchScope) {
    guard self.scope != scope else { return }
    if scope == .global { currentQuery = query } else { query = currentQuery }
    self.scope = scope
  }

  mutating func updateQuery(_ text: String) {
    query = text
    if scope == .current { currentQuery = text }
  }

  var showingGlobalResults: Bool {
    scope == .global && !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
  }
}

struct GlobalSearchRoot: Identifiable, Hashable, Sendable, Codable {
  let id: UUID
  let name: String
  let url: URL
}

struct GlobalSearchResultID: Hashable, Sendable {
  let categoryID: UUID
  let url: URL
}

struct GlobalSearchResult: Identifiable, Hashable, Sendable, Codable {
  let categoryID: UUID
  let categoryName: String
  let categoryRoot: URL
  let entry: DirectoryEntry
  let relativePath: String
  let artworkURL: URL?

  var id: GlobalSearchResultID {
    GlobalSearchResultID(categoryID: categoryID, url: entry.url)
  }

  var parentPath: String {
    let parent = URL(fileURLWithPath: relativePath).deletingLastPathComponent().path
    return parent == "." || parent == "/" ? "" : parent
  }
}

struct GlobalSearchFailure: Identifiable, Hashable, Sendable {
  let categoryID: UUID
  let categoryName: String
  var failedFolders: Int
  var message: String

  var id: UUID { categoryID }
}

struct GlobalSearchProgress: Equatable, Sendable {
  var sourcesCompleted: Int = 0
  var sourcesTotal: Int = 0
  var foldersVisited: Int = 0
  var matchesFound: Int = 0
  var failedSources: Int = 0
}

struct GlobalSearchSnapshot: Sendable {
  let results: [GlobalSearchResult]
  let progress: GlobalSearchProgress
  let failures: [GlobalSearchFailure]
}

enum GlobalSearchError: LocalizedError, Equatable {
  case queryTooShort
  case noSources

  var errorDescription: String? {
    switch self {
    case .queryTooShort: "Enter at least 3 characters for Global search."
    case .noSources: "Add at least one directory before using Global search."
    }
  }
}

enum MediaFileType {
  private static let videoExtensions: Set<String> = [
    "mp4", "mkv", "avi", "mov", "m4v", "ts", "webm",
  ]
  private static let imageExtensions: Set<String> = ["jpg", "jpeg", "png", "webp"]

  static func isVideo(_ entry: DirectoryEntry) -> Bool {
    entry.kind == .file && videoExtensions.contains(entry.url.pathExtension.lowercased())
  }

  static func isArtwork(_ entry: DirectoryEntry) -> Bool {
    entry.kind == .file && imageExtensions.contains(entry.url.pathExtension.lowercased())
  }
}

enum DirectorySortField: String, CaseIterable, Sendable {
  case name
  case date
}

enum DirectoryEntrySorter {
  static func sorted(
    _ entries: [DirectoryEntry], by field: DirectorySortField, ascending: Bool
  ) -> [DirectoryEntry] {
    entries.sorted { left, right in
      if left.kind != right.kind { return left.kind == .folder }

      let comparison: ComparisonResult
      switch field {
      case .name:
        comparison = left.name.localizedStandardCompare(right.name)
      case .date:
        switch (left.modifiedAt, right.modifiedAt) {
        case (let leftDate?, let rightDate?):
          comparison = leftDate.compare(rightDate)
        case (nil, nil):
          comparison = left.name.localizedStandardCompare(right.name)
        case (nil, _):
          return false
        case (_, nil):
          return true
        }
      }

      if comparison == .orderedSame {
        return left.name.localizedStandardCompare(right.name) == .orderedAscending
      }
      return ascending ? comparison == .orderedAscending : comparison == .orderedDescending
    }
  }
}

struct DownloadManifestItem: Identifiable, Hashable, Sendable {
  let id: UUID
  let sourceURL: URL
  let relativePath: String
  let size: Int64?

  init(id: UUID = UUID(), sourceURL: URL, relativePath: String, size: Int64?) {
    self.id = id
    self.sourceURL = sourceURL
    self.relativePath = relativePath
    self.size = size
  }
}

struct ScanProgress: Sendable {
  var foldersVisited: Int = 0
  var filesFound: Int = 0
  var knownBytes: Int64 = 0
}

enum TransferStatus: String, Codable, CaseIterable, Sendable {
  case preparing
  case queued
  case active
  case paused
  case completed
  case failed
  case cancelled
}

enum AppThemeMode: String, CaseIterable, Codable, Sendable {
  case system
  case light
  case dark

  var next: AppThemeMode {
    switch self {
    case .system: .light
    case .light: .dark
    case .dark: .system
    }
  }

  var displayName: String {
    switch self {
    case .system: "System"
    case .light: "Light"
    case .dark: "Dark"
    }
  }
}

@Model
final class Category {
  @Attribute(.unique) var id: UUID
  var name: String
  var rootURLString: String
  var createdAt: Date

  init(id: UUID = UUID(), name: String, rootURLString: String, createdAt: Date = .now) {
    self.id = id
    self.name = name
    self.rootURLString = rootURLString
    self.createdAt = createdAt
  }

  var rootURL: URL? { URL(string: rootURLString) }
}

@Model
final class DownloadBatch {
  @Attribute(.unique) var id: UUID
  var title: String
  var statusRaw: String
  var createdAt: Date
  var startedAt: Date?
  var completedAt: Date?
  var totalBytes: Int64
  var completedBytes: Int64
  var downloadSpeed: Int64
  var errorMessage: String?

  init(id: UUID = UUID(), title: String, status: TransferStatus = .preparing) {
    self.id = id
    self.title = title
    self.statusRaw = status.rawValue
    self.createdAt = .now
    self.totalBytes = 0
    self.completedBytes = 0
    self.downloadSpeed = 0
  }

  var status: TransferStatus {
    get { TransferStatus(rawValue: statusRaw) ?? .failed }
    set { statusRaw = newValue.rawValue }
  }

  var progress: Double {
    totalBytes > 0 ? min(1, Double(completedBytes) / Double(totalBytes)) : 0
  }

  var elapsed: TimeInterval {
    guard let startedAt else { return 0 }
    return (completedAt ?? .now).timeIntervalSince(startedAt)
  }

  var estimatedSecondsRemaining: TimeInterval? {
    guard totalBytes > completedBytes, downloadSpeed > 0 else { return nil }
    return Double(totalBytes - completedBytes) / Double(downloadSpeed)
  }
}

@Model
final class DownloadItem {
  @Attribute(.unique) var id: UUID
  var batchID: UUID
  var sourceURLString: String
  var relativePath: String
  var destinationPath: String
  var statusRaw: String
  var ariaGID: String?
  var totalBytes: Int64
  var completedBytes: Int64
  var downloadSpeed: Int64
  var errorMessage: String?

  init(batchID: UUID, manifest: DownloadManifestItem, destinationPath: String) {
    self.id = manifest.id
    self.batchID = batchID
    self.sourceURLString = manifest.sourceURL.absoluteString
    self.relativePath = manifest.relativePath
    self.destinationPath = destinationPath
    self.statusRaw = TransferStatus.queued.rawValue
    self.totalBytes = manifest.size ?? 0
    self.completedBytes = 0
    self.downloadSpeed = 0
  }

  var status: TransferStatus {
    get { TransferStatus(rawValue: statusRaw) ?? .failed }
    set { statusRaw = newValue.rawValue }
  }
}

@Model
final class AppSettings {
  @Attribute(.unique) var id: String
  var downloadDirectory: String
  var downloadBookmark: Data?
  var aria2PathOverride: String
  var concurrentDownloads: Int
  var connectionsPerFile: Int
  var splitCount: Int
  var retryCount: Int
  var speedLimit: String
  var themeRaw: String = AppThemeMode.system.rawValue

  init() {
    self.id = "primary"
    self.downloadDirectory =
      FileManager.default.homeDirectoryForCurrentUser
      .appending(path: "Downloads/Myra", directoryHint: .isDirectory).path
    self.aria2PathOverride = ""
    self.concurrentDownloads = 4
    self.connectionsPerFile = 8
    self.splitCount = 8
    self.retryCount = 5
    self.speedLimit = "0"
    self.themeRaw = AppThemeMode.system.rawValue
  }

  var theme: AppThemeMode {
    get { AppThemeMode(rawValue: themeRaw) ?? .system }
    set { themeRaw = newValue.rawValue }
  }
}

struct Aria2Status: Sendable {
  let gid: String
  let status: String
  let totalLength: Int64
  let completedLength: Int64
  let downloadSpeed: Int64
  let errorMessage: String?
}
