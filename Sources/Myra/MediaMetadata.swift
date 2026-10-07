import Combine
import Foundation
import Security

enum OMDbKeyStore {
  private static var query: [String: Any] {
    [
      kSecClass as String: kSecClassGenericPassword,
      kSecAttrService as String: "com.mehedee.Myra.omdb",
      kSecAttrAccount as String: "api-key",
    ]
  }
  static func read() throws -> String {
    var request = query
    request[kSecReturnData as String] = true
    request[kSecMatchLimit as String] = kSecMatchLimitOne
    var result: CFTypeRef?
    let status = SecItemCopyMatching(request as CFDictionary, &result)
    if status == errSecItemNotFound { return "" }
    guard status == errSecSuccess, let data = result as? Data,
      let key = String(data: data, encoding: .utf8)
    else { throw MetadataError.keychain }
    return key
  }
  static func save(_ raw: String) throws {
    let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
    if key.isEmpty {
      let status = SecItemDelete(query as CFDictionary)
      guard status == errSecSuccess || status == errSecItemNotFound else {
        throw MetadataError.keychain
      }
      return
    }
    let attributes = [kSecValueData as String: Data(key.utf8)]
    let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
    if status == errSecItemNotFound {
      var item = query
      item[kSecValueData as String] = Data(key.utf8)
      item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
      guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else {
        throw MetadataError.keychain
      }
    } else if status != errSecSuccess {
      throw MetadataError.keychain
    }
  }
}

enum MetadataError: LocalizedError {
  case missingKey, keychain, network, badResponse
  case notFound(String)
  var errorDescription: String? {
    switch self {
    case .missingKey: "Add an OMDb API key in Settings to load online information."
    case .keychain: "Myra could not access the OMDb key in Keychain."
    case .network: "Online metadata is unavailable. Check your connection and try again."
    case .badResponse: "OMDb returned an invalid response."
    case .notFound(let reason): reason
    }
  }
}

struct MediaIdentity: Equatable, Sendable {
  var title: String
  var year: String?
  var season: String?
  var episode: String?
  var imdbID: String?
  var cacheKey: String {
    [title.lowercased(), year ?? "", season ?? "", episode ?? "", imdbID ?? ""].joined(
      separator: "|")
  }

  static func parse(filename: String) -> MediaIdentity {
    var name = (filename as NSString).deletingPathExtension
      .replacingOccurrences(of: ".", with: " ").replacingOccurrences(of: "_", with: " ")
    var identity = MediaIdentity(title: name)
    if let match = name.range(of: "(?i)\\bS(\\d{1,2})E(\\d{1,3})\\b", options: .regularExpression) {
      let episodeToken = String(name[match])
      let parts = episodeToken.uppercased().dropFirst().split(separator: "E")
      identity.season = parts.first.flatMap { Int($0) }.map(String.init)
      identity.episode = parts.last.flatMap { Int($0) }.map(String.init)
      name = String(name[..<match.lowerBound])
    }
    if let match = name.range(of: "\\b(?:19|20)\\d{2}\\b", options: .regularExpression),
      !name[..<match.lowerBound].trimmingCharacters(
        in: .whitespacesAndNewlines.union(.punctuationCharacters)
      ).isEmpty
    {
      identity.year = String(name[match])
      name = String(name[..<match.lowerBound])
    }
    if let match = name.range(
      of:
        "(?i)\\b(?:2160p|1080p|720p|480p|4k|bluray|blu-ray|webrip|web-dl|hdtv|dvdrip|x264|x265|h264|h265)\\b",
      options: .regularExpression)
    {
      name = String(name[..<match.lowerBound])
    }
    identity.title = name.trimmingCharacters(
      in: .whitespacesAndNewlines.union(.punctuationCharacters))
    if identity.title.isEmpty { identity.title = (filename as NSString).deletingPathExtension }
    return identity
  }
}

struct MovieMetadata: Codable, Equatable, Sendable {
  let title: String?
  let year: String?
  let imdbRating: String?
  let imdbID: String?
  let plot: String?
  let genre: String?
  let director: String?
  let actors: String?
  let runtime: String?
  let rated: String?
  let released: String?
  let language: String?
  let country: String?
  let awards: String?
  let poster: String?
  let response: String?
  let error: String?

  enum CodingKeys: String, CodingKey {
    case title = "Title"
    case year = "Year"
    case imdbRating, imdbID
    case plot = "Plot"
    case genre = "Genre"
    case director = "Director"
    case actors = "Actors"
    case runtime = "Runtime"
    case rated = "Rated"
    case released = "Released"
    case language = "Language"
    case country = "Country"
    case awards = "Awards"
    case poster = "Poster"
    case response = "Response"
    case error = "Error"
  }
  static func available(_ value: String?) -> String? {
    guard let value, value != "N/A", !value.isEmpty else { return nil }
    return value
  }
}

private final class OMDbRedirectPolicy: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
  func urlSession(
    _ session: URLSession, task: URLSessionTask,
    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
    completionHandler: @escaping @Sendable (URLRequest?) -> Void
  ) {
    let safe = request.url?.scheme == "https" && request.url?.host == "www.omdbapi.com"
    completionHandler(safe ? request : nil)
  }
}

actor MetadataService {
  private let session: URLSession
  init(session: URLSession? = nil) {
    if let session {
      self.session = session
    } else {
      let config = URLSessionConfiguration.ephemeral
      config.timeoutIntervalForRequest = 15
      config.urlCache = nil
      self.session = URLSession(
        configuration: config, delegate: OMDbRedirectPolicy(), delegateQueue: nil)
    }
  }
  func lookup(_ identity: MediaIdentity, key: String, index: LibraryIndex?) async throws
    -> MovieMetadata
  {
    if let cached = try await index?.cachedMetadata(key: identity.cacheKey),
      let data = cached.data(using: .utf8),
      let metadata = try? JSONDecoder().decode(MovieMetadata.self, from: data)
    {
      return metadata
    }
    guard !key.isEmpty else { throw MetadataError.missingKey }
    var components = URLComponents(string: "https://www.omdbapi.com/")!
    var items = [
      URLQueryItem(name: "apikey", value: key), URLQueryItem(name: "plot", value: "full"),
    ]
    if let id = identity.imdbID, id.range(of: "^tt[0-9]{7,10}$", options: .regularExpression) != nil
    {
      items.append(URLQueryItem(name: "i", value: id))
    } else {
      items.append(URLQueryItem(name: "t", value: identity.title))
      if let year = identity.year { items.append(URLQueryItem(name: "y", value: year)) }
    }
    if let season = identity.season { items.append(URLQueryItem(name: "Season", value: season)) }
    if let episode = identity.episode {
      items.append(URLQueryItem(name: "Episode", value: episode))
    }
    components.queryItems = items
    guard let url = components.url else { throw MetadataError.badResponse }
    let data: Data
    let response: URLResponse
    do { (data, response) = try await session.data(from: url) } catch {
      try Task.checkCancellation()
      throw MetadataError.network
    }
    try Task.checkCancellation()
    guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
      data.count <= 3_000_000
    else { throw MetadataError.badResponse }
    let metadata: MovieMetadata
    do { metadata = try JSONDecoder().decode(MovieMetadata.self, from: data) } catch {
      throw MetadataError.badResponse
    }
    guard metadata.response == "True" else {
      throw MetadataError.notFound(metadata.error ?? "No matching movie or episode was found.")
    }
    if let payload = String(data: data, encoding: .utf8) {
      try await index?.saveMetadata(key: identity.cacheKey, payload: payload)
    }
    return metadata
  }
}

@MainActor
final class MediaInspectorModel: ObservableObject {
  @Published var selected: GlobalSearchResult?
  @Published var metadata: MovieMetadata?
  @Published var metadataError: String?
  @Published var isLoading = false
  @Published var identity = MediaIdentity(title: "")
  @Published var technicalDetails: [String: String] = [:]
  @Published var technicalError: String?
  @Published var isProbing = false
  private let service = MetadataService()
  private var lookupTask: Task<Void, Never>?
  private var probeTask: Task<Void, Never>?
  private var requestID = UUID()

  func select(_ media: GlobalSearchResult, library: LibraryController) {
    lookupTask?.cancel()
    probeTask?.cancel()
    selected = media
    metadata = nil
    technicalDetails = [:]
    technicalError = nil
    identity = MediaIdentity.parse(filename: media.entry.name)
    load(library: library)
    probe(media)
  }

  func load(library: LibraryController) {
    lookupTask?.cancel()
    requestID = UUID()
    let id = requestID
    let lookupIdentity = identity
    isLoading = true
    metadata = nil
    metadataError = nil
    lookupTask = Task { [weak self] in
      guard let self else { return }
      do {
        let key = try OMDbKeyStore.read()
        let index = try? await library.database()
        let value = try await service.lookup(lookupIdentity, key: key, index: index)
        try Task.checkCancellation()
        guard requestID == id else { return }
        metadata = value
      } catch is CancellationError {} catch {
        guard requestID == id else { return }
        metadataError = error.localizedDescription
      }
      if requestID == id { isLoading = false }
    }
  }

  func close() {
    lookupTask?.cancel()
    probeTask?.cancel()
    requestID = UUID()
    selected = nil
    isLoading = false
    isProbing = false
  }

  private func probe(_ media: GlobalSearchResult) {
    isProbing = true
    probeTask = Task { [weak self] in
      guard let self else { return }
      do {
        let details = try await VLCMediaProbe.inspect(url: media.entry.url)
        try Task.checkCancellation()
        guard selected?.id == media.id else { return }
        technicalDetails = details
      } catch is CancellationError {} catch {
        guard selected?.id == media.id else { return }
        technicalError = "Stream details unavailable. You can still try Play or VLC."
      }
      if selected?.id == media.id { isProbing = false }
    }
  }
}
