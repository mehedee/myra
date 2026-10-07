import Foundation

enum DestinationSafety {
  static func sanitize(_ component: String) -> String {
    let forbidden = CharacterSet(charactersIn: "/:\0").union(.controlCharacters)
    let cleaned = component.components(separatedBy: forbidden).joined(separator: "_")
      .trimmingCharacters(in: .whitespacesAndNewlines)
    if cleaned.isEmpty || cleaned == "." || cleaned == ".." { return "Untitled" }
    return String(cleaned.prefix(240))
  }

  static func destination(root: URL, relativePath: String) throws -> URL {
    let root = root.standardizedFileURL
    let destination = root.appending(path: relativePath).standardizedFileURL
    let rootPath = root.path.hasSuffix("/") ? root.path : root.path + "/"
    guard destination.path.hasPrefix(rootPath), destination.path != root.path else {
      throw DirectoryError.unsafeDestination
    }
    return destination
  }
}

struct GlobalDownloadGroup: Identifiable, Sendable {
  let id: UUID
  let categoryName: String
  let manifest: [DownloadManifestItem]
}

enum GlobalDownloadManifestBuilder {
  static func groups(for results: [GlobalSearchResult]) -> [GlobalDownloadGroup] {
    Dictionary(grouping: results, by: \.categoryID)
      .compactMap { categoryID, values in
        guard let first = values.first else { return nil }
        let categoryFolder = DestinationSafety.sanitize(first.categoryName)
        var seen = Set<GlobalSearchResultID>()
        let manifest =
          values
          .filter { seen.insert($0.id).inserted }
          .sorted { $0.relativePath.localizedStandardCompare($1.relativePath) == .orderedAscending }
          .map { result in
            let safePath = result.relativePath.split(separator: "/")
              .map { DestinationSafety.sanitize(String($0)) }
              .joined(separator: "/")
            return DownloadManifestItem(
              sourceURL: result.entry.url,
              relativePath: categoryFolder + "/" + safePath,
              size: result.entry.size
            )
          }
        return GlobalDownloadGroup(
          id: categoryID, categoryName: first.categoryName, manifest: manifest)
      }
      .sorted {
        $0.categoryName.localizedStandardCompare($1.categoryName) == .orderedAscending
      }
  }
}

actor DownloadPlanner {
  private let directoryService: DirectoryService
  private let maximumConcurrentFolders: Int

  init(directoryService: DirectoryService, maximumConcurrentFolders: Int = 4) {
    self.directoryService = directoryService
    self.maximumConcurrentFolders = max(1, maximumConcurrentFolders)
  }

  func prepare(
    selections: [DirectoryEntry],
    categoryRoot: URL,
    progress: @escaping @Sendable (ScanProgress) async -> Void
  ) async throws -> [DownloadManifestItem] {
    let boundary = try URLBoundary(root: categoryRoot)
    let selected = deduplicated(selections)
    var manifest: [DownloadManifestItem] = []
    var scanProgress = ScanProgress()
    var visited = Set<String>()
    var queue: [(url: URL, localComponents: [String])] = []

    for entry in selected {
      let safeName = DestinationSafety.sanitize(entry.name)
      if entry.kind == .file {
        manifest.append(
          DownloadManifestItem(sourceURL: entry.url, relativePath: safeName, size: entry.size))
        scanProgress.filesFound += 1
        scanProgress.knownBytes += entry.size ?? 0
      } else {
        queue.append((entry.url, [safeName]))
      }
    }
    await progress(scanProgress)

    while !queue.isEmpty {
      try Task.checkCancellation()
      let count = min(maximumConcurrentFolders, queue.count)
      let batch = Array(queue.prefix(count))
      queue.removeFirst(count)

      let results = try await withThrowingTaskGroup(of: (URL, [String], DirectoryListing).self) {
        group in
        for folder in batch where visited.insert(folder.url.absoluteString).inserted {
          group.addTask { [directoryService] in
            let listing = try await directoryService.listing(at: folder.url, boundary: boundary)
            return (folder.url, folder.localComponents, listing)
          }
        }
        var values: [(URL, [String], DirectoryListing)] = []
        for try await result in group { values.append(result) }
        return values
      }

      for (_, components, listing) in results {
        scanProgress.foldersVisited += 1
        for entry in listing.entries {
          let next = components + [DestinationSafety.sanitize(entry.name)]
          if entry.kind == .folder {
            if !visited.contains(entry.url.absoluteString) { queue.append((entry.url, next)) }
          } else {
            manifest.append(
              DownloadManifestItem(
                sourceURL: entry.url,
                relativePath: next.joined(separator: "/"),
                size: entry.size
              ))
            scanProgress.filesFound += 1
            scanProgress.knownBytes += entry.size ?? 0
          }
        }
        await progress(scanProgress)
      }
    }

    var unique = Set<String>()
    return manifest.filter {
      unique.insert($0.sourceURL.absoluteString + "|" + $0.relativePath).inserted
    }
  }

  private func deduplicated(_ selections: [DirectoryEntry]) -> [DirectoryEntry] {
    let sorted = selections.sorted { $0.url.absoluteString.count < $1.url.absoluteString.count }
    var accepted: [DirectoryEntry] = []
    for entry in sorted {
      let covered = accepted.contains { parent in
        parent.kind == .folder && entry.url.absoluteString.hasPrefix(parent.url.absoluteString)
      }
      if !covered { accepted.append(entry) }
    }
    return accepted
  }
}
