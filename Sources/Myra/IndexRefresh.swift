import Foundation

struct IndexScope: Identifiable, Sendable, Hashable {
  let root: GlobalSearchRoot
  let folder: URL
  var id: String { root.id.uuidString + "|" + folder.absoluteString }
  var prefix: String {
    folder.absoluteString.hasSuffix("/") ? folder.absoluteString : folder.absoluteString + "/"
  }

  static func compact(_ scopes: [IndexScope]) -> [IndexScope] {
    var result: [IndexScope] = []
    for scope in scopes.sorted(by: { $0.prefix.count < $1.prefix.count }) {
      if !result.contains(where: {
        $0.root.id == scope.root.id && scope.prefix.hasPrefix($0.prefix)
      }) {
        result.append(scope)
      }
    }
    return result
  }
}

enum IndexSchedule: String, Codable, CaseIterable, Sendable {
  case daily = "Daily"
  case weekly = "Weekly"
  case manual = "Manual only"
  var interval: TimeInterval { self == .weekly ? 7 * 86400 : self == .daily ? 86400 : .infinity }
  func isDue(_ date: Date?, now: Date = .now) -> Bool {
    self != .manual && (date == nil || now.timeIntervalSince(date!) >= interval)
  }
}

struct IndexPolicies: Codable, Sendable {
  var overrides: [String: IndexSchedule] = [:]
  func schedule(for scope: IndexScope) -> IndexSchedule {
    var best: (Int, IndexSchedule)?
    for (key, value) in overrides {
      let base = scope.root.id.uuidString + "|"
      guard key.hasPrefix(base) else { continue }
      let path = String(key.dropFirst(base.count))
      let prefix = path.hasSuffix("/") ? path : path + "/"
      if scope.prefix.hasPrefix(prefix), best == nil || prefix.count > best!.0 {
        best = (prefix.count, value)
      }
    }
    return best?.1 ?? .daily
  }
}

struct IndexedFolder: Codable, Sendable {
  var listing: DirectoryListing
  var checked: Date
  var etag: String?
  var lastModified: String?
}

struct IndexFolderRow: Identifiable, Sendable {
  let scope: IndexScope
  let checked: Date?
  let files: Int
  var id: String { scope.id }
}

struct IndexRefreshSummary: Sendable {
  var added = 0
  var changed = 0
  var removed = 0
  var checked = 0
  var unchanged = 0
  var text: String {
    "Checked \(checked) folders · \(unchanged) unchanged · \(added) files added · \(changed) updated · \(removed) removed"
  }
}

// Each refresh has its own folder cache writer; published snapshots remain immutable until promotion.
actor IndexListingLoader {
  let service: DirectoryService
  let active: LibraryIndex
  let staging: LibraryIndex
  let roots: [GlobalSearchRoot]
  let policies: IndexPolicies
  let selected: [IndexScope]
  let automatic: Bool
  let full: Bool
  var summary = IndexRefreshSummary()
  init(
    service: DirectoryService, active: LibraryIndex, staging: LibraryIndex,
    roots: [GlobalSearchRoot], policies: IndexPolicies, selected: [IndexScope], automatic: Bool,
    full: Bool
  ) {
    self.service = service
    self.active = active
    self.staging = staging
    self.roots = roots
    self.policies = policies
    self.selected = selected
    self.automatic = automatic
    self.full = full
  }
  func listing(at url: URL, boundary: URLBoundary) async throws -> DirectoryListing {
    guard
      let root = roots.first(where: {
        boundary.sourceID == nil
          ? ($0.url == boundary.root || $0.url.standardizedDirectoryURL == boundary.root)
          : $0.id == boundary.sourceID
      })
    else {
      throw DirectoryError.outsideCategoryRoot
    }
    let scope = IndexScope(root: root, folder: url)
    let old = try await active.folder(scope)
    let schedule = policies.schedule(for: scope)
    let manualBlocked = isManualBlocked(scope)
    let useCache = !full && (manualBlocked || (automatic && !schedule.isDue(old?.checked)))
    var value: IndexedFolder
    if useCache {
      guard let old else {
        // A manual-only branch cannot be safely reconciled without an existing complete snapshot.
        throw LibraryIndexError.database(
          "Refresh this manual-only folder once to establish its snapshot.")
      }
      value = old
    } else {
      value = try await service.conditionalListing(
        at: url, boundary: boundary, cached: full ? nil : old)
      summary.checked += 1
      if let old,
        old.listing.entries == value.listing.entries
          && old.listing.artworkURL == value.listing.artworkURL
      {
        summary.unchanged += 1
      }
    }
    if !full, let old {
      var entries = value.listing.entries
      let observed = Set(entries.map(\.url))
      for entry in old.listing.entries where entry.kind == .folder && !observed.contains(entry.url)
      {
        let child = IndexScope(root: root, folder: entry.url)
        if isManualBlocked(child) { entries.append(entry) }
      }
      value.listing = DirectoryListing(
        url: value.listing.url, entries: entries, artworkURL: value.listing.artworkURL)
    }
    try await staging.saveFolder(value, scope: scope)
    return value.listing
  }
  private func isManualBlocked(_ scope: IndexScope) -> Bool {
    let requestedPrefix =
      selected.filter { $0.root.id == scope.root.id && scope.prefix.hasPrefix($0.prefix) }.map {
        $0.prefix.count
      }.max() ?? 0
    var manualPrefix = 0
    for (key, policy) in policies.overrides where policy == .manual {
      let base = scope.root.id.uuidString + "|"
      guard key.hasPrefix(base) else { continue }
      let path = String(key.dropFirst(base.count))
      let prefix = path.hasSuffix("/") ? path : path + "/"
      if scope.prefix.hasPrefix(prefix) { manualPrefix = max(manualPrefix, prefix.count) }
    }
    return policies.schedule(for: scope) == .manual && (automatic || manualPrefix > requestedPrefix)
  }

}
