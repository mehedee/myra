import Foundation
import VLCKit

/// An ephemeral, folder-scoped queue. Browser filters and Global results never define episode order.
struct PlaybackSequence: Sendable {
  let videos: [GlobalSearchResult]

  init(playing: GlobalSearchResult, entries: [DirectoryEntry], artworkURL: URL?) throws {
    let root = playing.categoryRoot.standardizedDirectoryURL
    guard Self.contains(playing.entry.url, root: root), MediaFileType.isVideo(playing.entry) else {
      throw DirectoryError.outsideCategoryRoot
    }
    let parent = playing.entry.url.deletingLastPathComponent().standardizedDirectoryURL
    var seen = Set<URL>()
    var candidates: [DirectoryEntry] = []
    for entry in entries + [playing.entry] {
      let url = entry.url.standardizedDirectoryURL
      guard MediaFileType.isVideo(entry), Self.contains(url, root: root),
        url.deletingLastPathComponent().standardizedDirectoryURL == parent,
        seen.insert(url).inserted
      else { continue }
      candidates.append(entry)
    }
    candidates.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    let rootPath = root.path
    videos = candidates.map { entry in
      let path = String(entry.url.path.dropFirst(rootPath.count))
        .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
      return GlobalSearchResult(
        categoryID: playing.categoryID, categoryName: playing.categoryName,
        categoryRoot: playing.categoryRoot, entry: entry, relativePath: path,
        artworkURL: artworkURL ?? playing.artworkURL)
    }
  }

  static func contains(_ url: URL, root: URL) -> Bool {
    if root.isFileURL {
      return url.isFileURL
        && url.resolvingSymlinksInPath().deletingLastPathComponent().standardizedFileURL.path
          == root.resolvingSymlinksInPath().standardizedFileURL.path
    }
    return (try? URLBoundary(root: root).contains(url)) == true
  }

  var groups: [[GlobalSearchResult]] {
    var grouped: [[GlobalSearchResult]] = []
    var indexes: [String: Int] = [:]
    for video in videos {
      let identity = MediaIdentity.parse(filename: video.entry.name)
      // Explicit release labels permit matching yearless variants only in this folder.
      let release = EntertainmentVersion(media: video, firstDiscovered: .distantPast)
      let normalized = identity.title.folding(
        options: [.caseInsensitive, .diacriticInsensitive],
        locale: Locale(identifier: "en_US_POSIX"))
      let key: String
      if identity.year != nil || identity.episode != nil {
        key = [normalized, identity.year ?? "", identity.season ?? "", identity.episode ?? ""]
          .joined(separator: "|")
      } else if !release.quality.isEmpty {
        key =
          "folder|" + video.entry.url.deletingLastPathComponent().absoluteString + "|" + normalized
      } else {
        key = video.entry.url.absoluteString
      }
      if let index = indexes[key] {
        grouped[index].append(video)
      } else {
        indexes[key] = grouped.count
        grouped.append([video])
      }
    }
    return grouped
  }

  func neighbours(of url: URL, offset: Int) -> [GlobalSearchResult] {
    let items = groups
    guard let index = items.firstIndex(where: { $0.contains { $0.entry.url == url } }),
      items.indices.contains(index + offset)
    else { return [] }
    return items[index + offset]
  }

  func previous(before url: URL) -> GlobalSearchResult? {
    neighbours(of: url, offset: -1).first
  }

  func next(after url: URL) -> GlobalSearchResult? {
    neighbours(of: url, offset: 1).first
  }
}

/// VLC may report the same terminal state repeatedly; each media load finishes only once.
struct PlaybackCompletionState {
  private var hasPlayed = false
  private var handled = false

  mutating func reset() {
    hasPlayed = false
    handled = false
  }

  mutating func observe(_ state: VLCMediaPlayerState) -> Bool {
    if state == .playing || state == .paused { hasPlayed = true }
    guard !handled, state == .ended || (state == .stopped && hasPlayed) else { return false }
    handled = true
    return true
  }
}
