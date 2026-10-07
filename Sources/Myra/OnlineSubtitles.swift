import Foundation
import Security

struct SubtitleCredentials: Equatable, Sendable {
  var apiKey = ""
  var username = ""
  var password = ""
}

enum SubtitleError: LocalizedError {
  case missingKey, keychain, authentication, quota, rateLimited, invalidResponse
  case unsafeURL, oversized, invalidFile, invalidSearch, network
  case server(Int)
  var errorDescription: String? {
    switch self {
    case .missingKey:
      "Add your OpenSubtitles.com API key in Settings first. This is separate from OMDb."
    case .keychain: "Myra could not access subtitle credentials in Keychain."
    case .authentication:
      "OpenSubtitles rejected the API key or account. Check Subtitle settings and sign in again."
    case .quota:
      "The OpenSubtitles download allowance is exhausted or unavailable. Check your account allowance before trying again."
    case .rateLimited: "OpenSubtitles is rate limiting requests. Please wait before trying again."
    case .invalidResponse: "OpenSubtitles returned an invalid response."
    case .unsafeURL: "The subtitle service returned an untrusted address."
    case .oversized: "The subtitle response exceeds the safe size limit."
    case .invalidFile: "The download is not a supported text subtitle."
    case .invalidSearch: "Enter a title and valid year/season/episode numbers."
    case .network: "Subtitles are unavailable. Check your connection and try again."
    case .server(let status): "OpenSubtitles returned HTTP \(status). Try again later."
    }
  }
}

enum SubtitleCredentialStore {
  private static func query(_ account: String) -> [String: Any] {
    [
      kSecClass as String: kSecClassGenericPassword,
      kSecAttrService as String: "com.mehedee.Myra.opensubtitles",
      kSecAttrAccount as String: account,
    ]
  }
  private static func read(_ account: String) throws -> String {
    var request = query(account)
    request[kSecReturnData as String] = true
    request[kSecMatchLimit as String] = kSecMatchLimitOne
    var result: CFTypeRef?
    let status = SecItemCopyMatching(request as CFDictionary, &result)
    if status == errSecItemNotFound { return "" }
    guard status == errSecSuccess, let data = result as? Data,
      let value = String(data: data, encoding: .utf8)
    else { throw SubtitleError.keychain }
    return value
  }
  private static func save(_ value: String, account: String) throws {
    let query = query(account)
    if value.isEmpty {
      let result = SecItemDelete(query as CFDictionary)
      guard result == errSecSuccess || result == errSecItemNotFound else {
        throw SubtitleError.keychain
      }
      return
    }
    let attributes = [kSecValueData as String: Data(value.utf8)]
    let result = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
    if result == errSecItemNotFound {
      var item = query
      item[kSecValueData as String] = Data(value.utf8)
      item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
      guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else {
        throw SubtitleError.keychain
      }
    } else if result != errSecSuccess {
      throw SubtitleError.keychain
    }
  }
  static func read() throws -> SubtitleCredentials {
    try SubtitleCredentials(
      apiKey: read("api-key"), username: read("username"), password: read("password"))
  }
  static func save(_ credentials: SubtitleCredentials) throws {
    try save(credentials.apiKey.trimmingCharacters(in: .whitespacesAndNewlines), account: "api-key")
    try save(
      credentials.username.trimmingCharacters(in: .whitespacesAndNewlines), account: "username")
    try save(credentials.password, account: "password")
  }
}

struct OnlineSubtitleResult: Identifiable, Equatable, Sendable {
  let id: Int
  let filename: String
  let release: String
  let language: String
  let fps: Double?
  let downloads: Int
  let hearingImpaired: Bool
  let trusted: Bool
}

struct SubtitleSearchPage: Sendable {
  let results: [OnlineSubtitleResult]
  let hasMore: Bool
}

enum SubtitleURLPolicy {
  static let apiHosts: Set<String> = ["api.opensubtitles.com", "vip-api.opensubtitles.com"]
  static func accepts(_ url: URL, download: Bool) -> Bool {
    guard url.scheme?.lowercased() == "https", url.user == nil, url.password == nil,
      url.port == nil || url.port == 443, let host = url.host?.lowercased()
    else { return false }
    if download { return host == "opensubtitles.com" || host.hasSuffix(".opensubtitles.com") }
    return apiHosts.contains(host)
  }
}

private final class SubtitleRedirectPolicy: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
  func urlSession(
    _ session: URLSession, task: URLSessionTask,
    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
    completionHandler: @escaping @Sendable (URLRequest?) -> Void
  ) {
    guard let url = request.url else {
      completionHandler(nil)
      return
    }
    let apiRequest = task.originalRequest?.value(forHTTPHeaderField: "Api-Key") != nil
    guard SubtitleURLPolicy.accepts(url, download: !apiRequest),
      !apiRequest || url.host == task.originalRequest?.url?.host
    else {
      completionHandler(nil)
      return
    }
    var safe = request
    if !apiRequest {
      safe.setValue(nil, forHTTPHeaderField: "Api-Key")
      safe.setValue(nil, forHTTPHeaderField: "Authorization")
    }
    completionHandler(safe)
  }
}

actor SubtitleCache {
  static let maximumFileBytes = 5 * 1024 * 1024
  private let directory: URL
  init(directory: URL? = nil) throws {
    let base =
      try directory
      ?? FileManager.default.url(
        for: .cachesDirectory, in: .userDomainMask, appropriateFor: nil, create: true
      )
      .appending(path: "com.mehedee.Myra/Subtitles", directoryHint: .isDirectory)
    self.directory = URL(fileURLWithPath: base.standardizedFileURL.path, isDirectory: true)
    try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
    guard base.resolvingSymlinksInPath().standardizedFileURL.path == base.standardizedFileURL.path
    else {
      throw SubtitleError.unsafeURL
    }
  }
  func cached(fileID: Int) -> URL? {
    guard fileID > 0 else { return nil }
    let file = directory.appending(path: "MyraSub-\(fileID).srt", directoryHint: .notDirectory)
    guard file.resolvingSymlinksInPath().path == file.standardizedFileURL.path,
      let values = try? file.resourceValues(forKeys: [
        .isRegularFileKey, .fileSizeKey, .contentModificationDateKey,
      ]),
      values.isRegularFile == true, let size = values.fileSize, size > 0,
      size <= Self.maximumFileBytes,
      (values.contentModificationDate ?? .distantPast) > Date.now.addingTimeInterval(-30 * 86400)
    else { return nil }
    return file
  }
  func remember(fileID: Int, identity: MediaIdentity) throws {
    guard cached(fileID: fileID) != nil else { return }
    let manifest = directory.appending(path: "title-cache.json")
    guard manifest.resolvingSymlinksInPath().path == manifest.standardizedFileURL.path else {
      throw SubtitleError.unsafeURL
    }
    var titles = cachedTitles()
    var files = titles[identity.cacheKey] ?? []
    if !files.contains(fileID) { files.append(fileID) }
    titles[identity.cacheKey] = Array(files.suffix(20))
    try JSONEncoder().encode(titles).write(to: manifest, options: .atomic)
  }

  func cached(identity: MediaIdentity) -> [URL] {
    let titles = cachedTitles()
    return (titles[identity.cacheKey] ?? []).compactMap { cached(fileID: $0) }
  }

  private func cachedTitles() -> [String: [Int]] {
    let manifest = directory.appending(path: "title-cache.json")
    guard manifest.resolvingSymlinksInPath().path == manifest.standardizedFileURL.path,
      let attributes = try? manifest.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
      attributes.isRegularFile == true, (attributes.fileSize ?? Int.max) <= 2 * 1024 * 1024,
      let data = try? Data(contentsOf: manifest),
      let titles = try? JSONDecoder().decode([String: [Int]].self, from: data)
    else { return [:] }
    return titles
  }

  func store(_ data: Data, fileID: Int) throws -> URL {
    guard fileID > 0 else { throw SubtitleError.invalidFile }
    guard data.count <= Self.maximumFileBytes else { throw SubtitleError.oversized }
    let text =
      String(data: data, encoding: .utf8)
      ?? String(data: data, encoding: .utf16)
      ?? String(data: data, encoding: .isoLatin1)
    guard let text, !text.lowercased().contains("<html"),
      text.range(
        of: "\\d{1,2}:\\d{2}:\\d{2}[,.]\\d{3}\\s+-->\\s+\\d{1,2}:\\d{2}:\\d{2}[,.]\\d{3}",
        options: .regularExpression) != nil
    else { throw SubtitleError.invalidFile }
    try Task.checkCancellation()
    // Never use a provider-supplied filename as a local path.
    let file = directory.appending(path: "MyraSub-\(fileID).srt", directoryHint: .notDirectory)
    guard file.resolvingSymlinksInPath().path == file.standardizedFileURL.path else {
      throw SubtitleError.unsafeURL
    }
    try data.write(to: file, options: [.atomic])
    prune(preserving: file)
    return file
  }
  private func prune(preserving protected: URL) {
    let keys: Set<URLResourceKey> = [
      .isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey, .fileSizeKey,
    ]
    guard
      let files = try? FileManager.default.contentsOfDirectory(
        at: directory, includingPropertiesForKeys: Array(keys))
    else { return }
    var owned: [(url: URL, date: Date, bytes: Int)] = []
    for file in files
    where file.lastPathComponent.hasPrefix("MyraSub-") && file.pathExtension == "srt" {
      guard let values = try? file.resourceValues(forKeys: keys),
        values.isRegularFile == true, values.isSymbolicLink != true,
        let date = values.contentModificationDate, let bytes = values.fileSize
      else { continue }
      owned.append((file, date, bytes))
    }
    owned.sort { $0.date < $1.date }
    var total = owned.reduce(0) { $0 + $1.bytes }
    let cutoff = Date.now.addingTimeInterval(-30 * 86400)
    for item in owned where item.url.path != protected.path {
      if item.date < cutoff || total > 100 * 1024 * 1024 {
        if (try? FileManager.default.removeItem(at: item.url)) != nil { total -= item.bytes }
      }
    }
  }
}

actor OpenSubtitlesService {
  static let shared = OpenSubtitlesService()
  private let session: URLSession
  private var authenticated: SubtitleCredentials?
  private var token = ""
  private var apiHost = "api.opensubtitles.com"
  private var tokenExpires = Date.distantPast

  init(session: URLSession? = nil) {
    if let session {
      self.session = session
    } else {
      let config = URLSessionConfiguration.ephemeral
      config.timeoutIntervalForRequest = 20
      config.timeoutIntervalForResource = 45
      config.urlCache = nil
      config.httpCookieStorage = nil
      self.session = URLSession(
        configuration: config, delegate: SubtitleRedirectPolicy(), delegateQueue: nil)
    }
  }
  func resetSession() {
    authenticated = nil
    token = ""
    apiHost = "api.opensubtitles.com"
  }

  @discardableResult
  func signIn(_ credentials: SubtitleCredentials) async throws -> Int? {
    guard !credentials.username.isEmpty, !credentials.password.isEmpty else {
      throw SubtitleError.authentication
    }
    resetSession()
    var request = try request(path: "login", credentials: credentials)
    request.httpMethod = "POST"
    request.httpBody = try JSONEncoder().encode(
      LoginBody(username: credentials.username, password: credentials.password))
    let data = try await response(request, limit: 256 * 1024)
    guard let result = try? JSONDecoder().decode(LoginResponse.self, from: data),
      !result.token.isEmpty, SubtitleURLPolicy.apiHosts.contains(result.baseURL)
    else { throw SubtitleError.invalidResponse }
    authenticated = credentials
    token = result.token
    apiHost = result.baseURL
    tokenExpires = Date.now.addingTimeInterval(20 * 60)
    return result.user?.allowedDownloads
  }

  private func authenticateIfNeeded(_ credentials: SubtitleCredentials) async throws {
    if authenticated != credentials || Date.now >= tokenExpires { resetSession() }
    if !credentials.username.isEmpty || !credentials.password.isEmpty {
      if token.isEmpty { try await signIn(credentials) }
    }
  }

  func search(
    _ identity: MediaIdentity, language: String, page: Int = 1,
    credentials: SubtitleCredentials
  ) async throws -> SubtitleSearchPage {
    guard !identity.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
      identity.title.count <= 200, page > 0,
      language.isEmpty
        || language.range(of: "^[a-z]{2,3}(?:-[a-z]{2})?$", options: .regularExpression) != nil
    else { throw SubtitleError.invalidSearch }
    try await authenticateIfNeeded(credentials)
    var request = try request(path: "subtitles", credentials: credentials)
    var parts = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)!
    var items = [
      URLQueryItem(name: "query", value: identity.title),
      URLQueryItem(name: "page", value: String(page)),
      URLQueryItem(name: "order_by", value: "download_count"),
      URLQueryItem(name: "order_direction", value: "desc"),
    ]
    if !language.isEmpty { items.append(URLQueryItem(name: "languages", value: language)) }
    for (name, value) in [
      ("year", identity.year), ("season_number", identity.season),
      ("episode_number", identity.episode),
    ] {
      if let value, !value.isEmpty {
        guard let number = Int(value), number >= 0, number <= 9999 else {
          throw SubtitleError.invalidSearch
        }
        items.append(URLQueryItem(name: name, value: String(number)))
      }
    }
    if let id = identity.imdbID?.replacingOccurrences(of: "tt", with: ""), Int(id) != nil {
      items.append(URLQueryItem(name: "imdb_id", value: id))
    }
    parts.queryItems = items.sorted { $0.name < $1.name }
    request.url = parts.url
    let data = try await response(request, limit: 2 * 1024 * 1024)
    guard let result = try? JSONDecoder().decode(SearchResponse.self, from: data) else {
      throw SubtitleError.invalidResponse
    }
    var seen = Set<Int>()
    var results: [OnlineSubtitleResult] = []
    for item in result.data {
      let attrs = item.attributes
      for file in attrs.files where file.fileID > 0 && seen.insert(file.fileID).inserted {
        results.append(
          OnlineSubtitleResult(
            id: file.fileID, filename: file.fileName,
            release: attrs.release?.isEmpty == false ? attrs.release! : file.fileName,
            language: attrs.language, fps: attrs.fps, downloads: attrs.downloadCount ?? 0,
            hearingImpaired: attrs.hearingImpaired ?? false, trusted: attrs.fromTrusted ?? false))
      }
    }
    return SubtitleSearchPage(results: results, hasMore: page < (result.totalPages ?? page))
  }

  func download(
    _ result: OnlineSubtitleResult, credentials: SubtitleCredentials,
    cache: SubtitleCache
  ) async throws -> (url: URL, remaining: Int?) {
    if let file = await cache.cached(fileID: result.id) { return (file, nil) }
    guard result.id > 0 else { throw SubtitleError.invalidFile }
    try await authenticateIfNeeded(credentials)
    var request = try request(path: "download", credentials: credentials)
    request.httpMethod = "POST"
    request.httpBody = try JSONEncoder().encode(DownloadBody(fileID: result.id))
    // No automatic retry: this POST may consume the user's provider allowance.
    let data = try await response(request, limit: 256 * 1024)
    guard let reply = try? JSONDecoder().decode(DownloadResponse.self, from: data),
      let link = URL(string: reply.link), SubtitleURLPolicy.accepts(link, download: true)
    else { throw SubtitleError.unsafeURL }
    var fileRequest = URLRequest(url: link)
    fileRequest.setValue("Myra v1.0", forHTTPHeaderField: "User-Agent")
    let subtitle = try await response(
      fileRequest, limit: SubtitleCache.maximumFileBytes, download: true)
    let file = try await cache.store(subtitle, fileID: result.id)
    return (file, reply.remaining)
  }

  private func request(path: String, credentials: SubtitleCredentials) throws -> URLRequest {
    let key = credentials.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !key.isEmpty, !key.contains("\n"), !key.contains("\r") else {
      throw SubtitleError.missingKey
    }
    let url = URL(string: "https://\(apiHost)/api/v1/\(path)")!
    var request = URLRequest(url: url)
    request.setValue(key, forHTTPHeaderField: "Api-Key")
    request.setValue("Myra v1.0", forHTTPHeaderField: "User-Agent")
    request.setValue("application/json", forHTTPHeaderField: "Accept")
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    if !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
    return request
  }

  private func response(_ request: URLRequest, limit: Int, download: Bool = false) async throws
    -> Data
  {
    do {
      let (bytes, response) = try await session.bytes(for: request)
      guard let http = response as? HTTPURLResponse, let url = http.url,
        SubtitleURLPolicy.accepts(url, download: download)
      else { throw SubtitleError.unsafeURL }
      switch http.statusCode {
      case 200..<300: break
      case 401, 403:
        resetSession()
        throw SubtitleError.authentication
      case 406: throw SubtitleError.quota
      case 429: throw SubtitleError.rateLimited
      default: throw SubtitleError.server(http.statusCode)
      }
      guard response.expectedContentLength <= limit else { throw SubtitleError.oversized }
      var data = Data()
      for try await byte in bytes {
        guard data.count < limit else { throw SubtitleError.oversized }
        data.append(byte)
        if data.count % 4096 == 0 { try Task.checkCancellation() }
      }
      try Task.checkCancellation()
      return data
    } catch is CancellationError { throw CancellationError() } catch let error as SubtitleError {
      throw error
    } catch {
      if Task.isCancelled { throw CancellationError() }
      throw SubtitleError.network
    }
  }

  private struct LoginBody: Encodable {
    let username: String
    let password: String
  }
  private struct LoginResponse: Decodable {
    let token: String
    let baseURL: String
    let user: User?
    enum CodingKeys: String, CodingKey {
      case token
      case baseURL = "base_url"
      case user
    }
    struct User: Decodable {
      let allowedDownloads: Int?
      enum CodingKeys: String, CodingKey { case allowedDownloads = "allowed_downloads" }
    }
  }
  private struct DownloadBody: Encodable {
    let fileID: Int
    let subFormat = "srt"
    enum CodingKeys: String, CodingKey {
      case fileID = "file_id"
      case subFormat = "sub_format"
    }
  }
  private struct DownloadResponse: Decodable {
    let link: String
    let remaining: Int?
  }
  private struct SearchResponse: Decodable {
    let data: [Item]
    let totalPages: Int?
    enum CodingKeys: String, CodingKey {
      case data
      case totalPages = "total_pages"
    }
    struct Item: Decodable {
      let attributes: Attributes
      struct Attributes: Decodable {
        let language: String
        let release: String?
        let fps: Double?
        let downloadCount: Int?
        let hearingImpaired: Bool?
        let fromTrusted: Bool?
        let files: [File]
        enum CodingKeys: String, CodingKey {
          case language, release, fps, files
          case downloadCount = "download_count"
          case hearingImpaired = "hearing_impaired"
          case fromTrusted = "from_trusted"
        }
        struct File: Decodable {
          let fileID: Int
          let fileName: String
          enum CodingKeys: String, CodingKey {
            case fileID = "file_id"
            case fileName = "file_name"
          }
        }
      }
    }
  }
}
