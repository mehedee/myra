import Foundation
import SwiftSoup

enum DirectoryError: LocalizedError {
  case invalidURL
  case outsideCategoryRoot
  case unsupportedListing
  case badResponse(Int)
  case unsafeDestination

  var errorDescription: String? {
    switch self {
    case .invalidURL: "The address is not a valid HTTP URL."
    case .outsideCategoryRoot: "The address is outside this category."
    case .unsupportedListing: "This page is not a supported directory listing."
    case .badResponse(let code): "The server returned HTTP status \(code)."
    case .unsafeDestination: "A downloaded path would leave the configured folder."
    }
  }
}

struct URLBoundary: Sendable {
  let root: URL
  var sourceID: UUID?

  init(root: URL) throws {
    guard let scheme = root.scheme?.lowercased(), ["http", "https"].contains(scheme),
      root.host != nil
    else {
      throw DirectoryError.invalidURL
    }
    self.root = root.standardizedDirectoryURL
  }

  func contains(_ candidate: URL) -> Bool {
    let candidate = candidate.standardizedDirectoryURL
    guard candidate.scheme?.lowercased() == root.scheme?.lowercased(),
      candidate.host?.lowercased() == root.host?.lowercased(),
      candidate.port == root.port
    else { return false }

    guard let encodedRootPath = safeEncodedPath(root),
      let candidatePath = safeEncodedPath(candidate)
    else { return false }
    let rootPath = encodedRootPath.hasSuffix("/") ? encodedRootPath : encodedRootPath + "/"
    return candidatePath == String(rootPath.dropLast()) || candidatePath.hasPrefix(rootPath)
  }

  private func safeEncodedPath(_ url: URL) -> String? {
    let path =
      URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedPath ?? url.path
    for encodedSegment in path.split(separator: "/", omittingEmptySubsequences: false) {
      var decoded = String(encodedSegment)
      for _ in 0..<2 { decoded = decoded.removingPercentEncoding ?? decoded }
      if decoded == "." || decoded == ".." || decoded.contains("/") || decoded.contains("\\") {
        return nil
      }
    }
    return path
  }
}

extension URL {
  var standardizedDirectoryURL: URL {
    guard var components = URLComponents(url: self, resolvingAgainstBaseURL: true) else {
      return self
    }
    components.fragment = nil
    components.query = nil
    let standardized = (components.url ?? self).standardized
    return standardized
  }
}

actor DirectoryService {
  private let session: URLSession

  init(session: URLSession = .shared) {
    self.session = session
  }

  func validate(rootURL: URL) async throws -> DirectoryListing {
    let boundary = try URLBoundary(root: rootURL)
    return try await listing(at: boundary.root, boundary: boundary)
  }

  func listing(at url: URL, boundary: URLBoundary) async throws -> DirectoryListing {
    try await conditionalListing(at: url, boundary: boundary, cached: nil).listing
  }

  func conditionalListing(at url: URL, boundary: URLBoundary, cached: IndexedFolder?) async throws
    -> IndexedFolder
  {
    guard boundary.contains(url) else { throw DirectoryError.outsideCategoryRoot }
    var request = URLRequest(url: url)
    request.timeoutInterval = 30
    request.cachePolicy = .reloadIgnoringLocalCacheData
    if let tag = cached?.etag {
      request.setValue(tag, forHTTPHeaderField: "If-None-Match")
    } else if let modified = cached?.lastModified {
      request.setValue(modified, forHTTPHeaderField: "If-Modified-Since")
    }
    request.setValue("Myra/1.0", forHTTPHeaderField: "User-Agent")
    let (data, response) = try await session.data(for: request)
    guard let http = response as? HTTPURLResponse else { throw DirectoryError.unsupportedListing }
    guard let finalURL = http.url, boundary.contains(finalURL) else {
      throw DirectoryError.outsideCategoryRoot
    }
    if http.statusCode == 304, var cached {
      cached.checked = .now
      return cached
    }
    guard (200..<300).contains(http.statusCode) else {
      throw DirectoryError.badResponse(http.statusCode)
    }
    guard let html = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .isoLatin1)
    else {
      throw DirectoryError.unsupportedListing
    }
    let parsedEntries = try Self.parse(html: html, baseURL: url, boundary: boundary)
    guard !parsedEntries.isEmpty || html.localizedCaseInsensitiveContains("Parent Directory") else {
      throw DirectoryError.unsupportedListing
    }
    let artworkURL = parsedEntries.first(where: MediaFileType.isArtwork)?.url
    let visibleEntries = parsedEntries.filter {
      $0.kind == .folder || MediaFileType.isVideo($0)
    }
    return IndexedFolder(
      listing: DirectoryListing(url: url, entries: visibleEntries, artworkURL: artworkURL),
      checked: .now, etag: http.value(forHTTPHeaderField: "ETag"),
      lastModified: http.value(forHTTPHeaderField: "Last-Modified"))
  }

  static func parse(html: String, baseURL: URL, boundary: URLBoundary) throws -> [DirectoryEntry] {
    let document = try SwiftSoup.parse(html, baseURL.absoluteString)
    var entries: [DirectoryEntry] = []
    var seen = Set<String>()

    for row in try document.select("tr").array() {
      guard let anchor = try row.select("a[href]").first() else { continue }
      let href = try anchor.attr("href")
      guard
        let entryURL = URL(string: href, relativeTo: baseURL)?.absoluteURL.standardizedDirectoryURL,
        isUsableLink(href: href, url: entryURL, baseURL: baseURL, boundary: boundary)
      else { continue }

      let imageAlt = (try? row.select("img").first()?.attr("alt")) ?? ""
      let folder = imageAlt.localizedCaseInsensitiveContains("folder") || href.hasSuffix("/")
      let name = cleanName((try? anchor.text()) ?? entryURL.lastPathComponent, url: entryURL)
      guard !name.isEmpty, seen.insert(entryURL.absoluteString).inserted else { continue }

      let cells = try row.select("td").array()
      let texts = cells.compactMap { try? $0.text() }
      let size = folder ? nil : texts.reversed().compactMap(parseByteSize).first
      let date = texts.compactMap(parseDate).first
      entries.append(
        DirectoryEntry(
          name: name, url: entryURL, kind: folder ? .folder : .file, size: size, modifiedAt: date))
    }

    if entries.isEmpty {
      for anchor in try document.select("a[href]").array() {
        let href = try anchor.attr("href")
        guard
          let entryURL = URL(string: href, relativeTo: baseURL)?.absoluteURL
            .standardizedDirectoryURL,
          isUsableLink(href: href, url: entryURL, baseURL: baseURL, boundary: boundary)
        else { continue }
        let folder = href.hasSuffix("/")
        let name = cleanName((try? anchor.text()) ?? entryURL.lastPathComponent, url: entryURL)
        guard !name.isEmpty, seen.insert(entryURL.absoluteString).inserted else { continue }
        entries.append(DirectoryEntry(name: name, url: entryURL, kind: folder ? .folder : .file))
      }
    }

    return entries.sorted {
      if $0.kind != $1.kind { return $0.kind == .folder }
      return $0.name.localizedStandardCompare($1.name) == .orderedAscending
    }
  }

  private static func isUsableLink(href: String, url: URL, baseURL: URL, boundary: URLBoundary)
    -> Bool
  {
    let lower = href.lowercased()
    guard href != "..", href != "../", href != ".", href != "./",
      !href.hasPrefix("#"), !lower.hasPrefix("javascript:"), !lower.hasPrefix("mailto:"),
      boundary.contains(url), url != baseURL.standardizedDirectoryURL
    else { return false }
    return true
  }

  private static func cleanName(_ text: String, url: URL) -> String {
    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
    if !trimmed.isEmpty, trimmed.localizedCaseInsensitiveCompare("Parent Directory") != .orderedSame
    {
      return trimmed.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }
    return url.lastPathComponent.removingPercentEncoding ?? url.lastPathComponent
  }

  private static func parseByteSize(_ text: String) -> Int64? {
    let compact = text.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
      .replacingOccurrences(of: "IB", with: "B")
    guard let regex = byteSizeRegex,
      let match = regex.firstMatch(in: compact, range: NSRange(compact.startIndex..., in: compact)),
      let numberRange = Range(match.range(at: 1), in: compact),
      let unitRange = Range(match.range(at: 2), in: compact),
      let number = Double(compact[numberRange])
    else { return nil }
    let units = ["B": 0, "KB": 1, "MB": 2, "GB": 3, "TB": 4, "PB": 5, "EB": 6]
    guard let power = units[String(compact[unitRange])] else { return nil }
    return Int64(number * pow(1024, Double(power)))
  }

  private static let byteSizeRegex = try? NSRegularExpression(
    pattern: #"^([0-9]+(?:\.[0-9]+)?)\s*([KMGTPE]?B)$"#)
  private static let dateFormatters: [DateFormatter] = [
    "yyyy-MM-dd HH:mm", "dd-MMM-yyyy HH:mm", "yyyy-MM-dd HH:mm:ss",
  ].map { format in
    let formatter = DateFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.dateFormat = format
    return formatter
  }

  private static func parseDate(_ text: String) -> Date? {
    let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
    guard value.count >= 16, value.contains(":") else { return nil }
    for formatter in dateFormatters {
      if let date = formatter.date(from: value) { return date }
    }
    return nil
  }

}

private struct GlobalSearchFolder: Sendable {
  let root: GlobalSearchRoot
  let boundary: URLBoundary
  let url: URL
  let relativeComponents: [String]
}

private struct GlobalSearchFolderID: Hashable, Sendable {
  let categoryID: UUID
  let url: URL
}

private enum GlobalSearchFolderOutcome: Sendable {
  case success(GlobalSearchFolder, DirectoryListing)
  case failure(GlobalSearchFolder, String)
}

actor GlobalSearchService {
  typealias ListingLoader = @Sendable (URL, URLBoundary) async throws -> DirectoryListing

  private let listingLoader: ListingLoader
  private let maximumConcurrentFolders: Int

  init(directoryService: DirectoryService, maximumConcurrentFolders: Int = 4) {
    self.listingLoader = { url, boundary in
      try await directoryService.listing(at: url, boundary: boundary)
    }
    self.maximumConcurrentFolders = max(1, maximumConcurrentFolders)
  }

  init(maximumConcurrentFolders: Int = 4, listingLoader: @escaping ListingLoader) {
    self.listingLoader = listingLoader
    self.maximumConcurrentFolders = max(1, maximumConcurrentFolders)
  }

  func search(
    query rawQuery: String,
    roots: [GlobalSearchRoot],
    matchAllVideos: Bool = false,
    scopes: [IndexScope]? = nil,
    batchSink: (@Sendable ([GlobalSearchResult]) async throws -> Void)? = nil,
    concurrencyLimit: (@Sendable () async -> Int)? = nil,
    update: @escaping @Sendable (GlobalSearchSnapshot) async -> Void
  ) async throws -> GlobalSearchSnapshot {
    let query = rawQuery.trimmingCharacters(in: .whitespacesAndNewlines)
    guard matchAllVideos || query.count >= 3 else { throw GlobalSearchError.queryTooShort }
    guard !roots.isEmpty else { throw GlobalSearchError.noSources }

    var queue: [GlobalSearchFolder] = []
    var visited = Set<GlobalSearchFolderID>()
    var outstandingFolders: [UUID: Int] = [:]
    let initial = scopes ?? roots.map { IndexScope(root: $0, folder: $0.url) }
    for scope in initial {
      let root = scope.root
      var boundary = try URLBoundary(root: root.url)
      boundary.sourceID = root.id
      guard boundary.contains(scope.folder) else { throw DirectoryError.outsideCategoryRoot }
      let relative = scope.folder.path.dropFirst(boundary.root.path.count).split(separator: "/")
        .map(String.init)
      let folder = GlobalSearchFolder(
        root: root, boundary: boundary, url: scope.folder, relativeComponents: relative)
      if visited.insert(GlobalSearchFolderID(categoryID: root.id, url: scope.folder)).inserted {
        queue.append(folder)
        outstandingFolders[root.id, default: 0] += 1
      }
    }

    var results: [GlobalSearchResult] = []
    var resultIDs = Set<GlobalSearchResultID>()
    var failures: [UUID: GlobalSearchFailure] = [:]
    var completedSources = Set<UUID>()
    var progress = GlobalSearchProgress(sourcesTotal: roots.count)
    await update(
      snapshot(
        results: batchSink == nil ? results : [], progress: progress, failures: failures,
        sorted: !matchAllVideos))

    var queueOffset = 0
    while queueOffset < queue.count {
      try Task.checkCancellation()
      let requested = await concurrencyLimit?() ?? maximumConcurrentFolders
      let count = min(max(1, min(requested, maximumConcurrentFolders)), queue.count - queueOffset)
      let batch = Array(queue[queueOffset..<(queueOffset + count)])
      queueOffset += count
      if queueOffset >= 256 {
        queue.removeFirst(queueOffset)
        queueOffset = 0
      }

      let outcomes = try await withThrowingTaskGroup(of: GlobalSearchFolderOutcome.self) { group in
        for folder in batch {
          group.addTask { [listingLoader] in
            do {
              let listing = try await listingLoader(folder.url, folder.boundary)
              return .success(folder, listing)
            } catch is CancellationError {
              throw CancellationError()
            } catch {
              return .failure(folder, error.localizedDescription)
            }
          }
        }
        var values: [GlobalSearchFolderOutcome] = []
        for try await outcome in group { values.append(outcome) }
        return values
      }

      for outcome in outcomes {
        try Task.checkCancellation()
        let folder: GlobalSearchFolder
        switch outcome {
        case .success(let scannedFolder, let listing):
          folder = scannedFolder
          progress.foldersVisited += 1

          for entry in listing.entries {
            if entry.kind == .folder {
              let id = GlobalSearchFolderID(categoryID: folder.root.id, url: entry.url)
              if visited.insert(id).inserted {
                queue.append(
                  GlobalSearchFolder(
                    root: folder.root,
                    boundary: folder.boundary,
                    url: entry.url,
                    relativeComponents: folder.relativeComponents + [entry.name]
                  ))
                outstandingFolders[folder.root.id, default: 0] += 1
              }
            } else if MediaFileType.isVideo(entry),
              matchAllVideos || entry.name.localizedCaseInsensitiveContains(query)
            {
              let relativePath = (folder.relativeComponents + [entry.name]).joined(separator: "/")
              let result = GlobalSearchResult(
                categoryID: folder.root.id,
                categoryName: folder.root.name,
                categoryRoot: folder.root.url,
                entry: entry,
                relativePath: relativePath,
                artworkURL: listing.artworkURL
              )
              if resultIDs.insert(result.id).inserted {
                results.append(result)
                if let batchSink, results.count >= 250 {
                  try await batchSink(results)
                  results.removeAll(keepingCapacity: true)
                  await Task.yield()
                }
              }
            }
          }
        case .failure(let failedFolder, let message):
          folder = failedFolder
          if var failure = failures[folder.root.id] {
            failure.failedFolders += 1
            failures[folder.root.id] = failure
          } else {
            failures[folder.root.id] = GlobalSearchFailure(
              categoryID: folder.root.id,
              categoryName: folder.root.name,
              failedFolders: 1,
              message: "\(folder.url.path): \(message)"
            )
          }
        }

        outstandingFolders[folder.root.id, default: 1] -= 1
        if outstandingFolders[folder.root.id] == 0,
          completedSources.insert(folder.root.id).inserted
        {
          progress.sourcesCompleted += 1
        }
        progress.matchesFound = resultIDs.count
        progress.failedSources = failures.count
        await update(
          snapshot(
            results: batchSink == nil ? results : [], progress: progress, failures: failures,
            sorted: !matchAllVideos))
      }
    }

    if let batchSink, !results.isEmpty {
      try await batchSink(results)
      results.removeAll(keepingCapacity: false)
    }
    return snapshot(
      results: batchSink == nil ? results : [], progress: progress, failures: failures,
      sorted: !matchAllVideos)
  }

  private func snapshot(
    results: [GlobalSearchResult],
    progress: GlobalSearchProgress,
    failures: [UUID: GlobalSearchFailure],
    sorted: Bool = true
  ) -> GlobalSearchSnapshot {
    GlobalSearchSnapshot(
      results: sorted
        ? results.sorted {
          if $0.categoryName != $1.categoryName {
            return $0.categoryName.localizedStandardCompare($1.categoryName) == .orderedAscending
          }
          return $0.relativePath.localizedStandardCompare($1.relativePath) == .orderedAscending
        } : results,
      progress: progress,
      failures: failures.values.sorted {
        $0.categoryName.localizedStandardCompare($1.categoryName) == .orderedAscending
      }
    )
  }
}
