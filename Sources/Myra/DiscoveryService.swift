import Foundation
import Security

enum TMDBKeyStore {
  private static var query: [String: Any] {
    [
      kSecClass as String: kSecClassGenericPassword,
      kSecAttrService as String: "com.mehedee.Myra.tmdb", kSecAttrAccount as String: "read-token",
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
      let token = String(data: data, encoding: .utf8)
    else { throw DiscoveryError.keychain }
    return token
  }
  static func save(_ raw: String) throws {
    let token = raw.trimmingCharacters(in: .whitespacesAndNewlines)
    if token.isEmpty {
      let status = SecItemDelete(query as CFDictionary)
      guard status == errSecSuccess || status == errSecItemNotFound else {
        throw DiscoveryError.keychain
      }
      return
    }
    let status = SecItemUpdate(
      query as CFDictionary, [kSecValueData as String: Data(token.utf8)] as CFDictionary)
    if status == errSecItemNotFound {
      var item = query
      item[kSecValueData as String] = Data(token.utf8)
      item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
      guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else {
        throw DiscoveryError.keychain
      }
    } else if status != errSecSuccess {
      throw DiscoveryError.keychain
    }
  }
}

enum DiscoveryError: LocalizedError {
  case keychain, missingToken, invalidResponse, ambiguous
  case invalidImport(String)
  var errorDescription: String? {
    switch self {
    case .keychain: "Could not access the TMDB credential in Keychain."
    case .missingToken: "Add a TMDB API Read Access Token in Settings to enrich Home."
    case .invalidResponse: "TMDB is unavailable or returned an invalid response. Try again later."
    case .ambiguous: "No confident metadata match. Use Correct Match to choose the title and year."
    case .invalidImport(let reason): reason
    }
  }
}

private final class TMDBRedirectPolicy: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
  func urlSession(
    _ session: URLSession, task: URLSessionTask,
    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
    completionHandler: @escaping @Sendable (URLRequest?) -> Void
  ) {
    completionHandler(
      request.url?.scheme == "https" && request.url?.host == "api.themoviedb.org" ? request : nil)
  }
}

actor DiscoveryService {
  private let session: URLSession
  private var lastRequest: Date = .distantPast
  init(session: URLSession? = nil) {
    let config = URLSessionConfiguration.ephemeral
    config.timeoutIntervalForRequest = 15
    config.timeoutIntervalForResource = 20
    config.urlCache = nil
    self.session =
      session
      ?? URLSession(configuration: config, delegate: TMDBRedirectPolicy(), delegateQueue: nil)
  }
  private struct Search: Decodable { var results: [Item] }
  private struct Season: Codable { var episodes: [Episode] }
  private struct Episode: Codable {
    var episodeNumber: Int
    var seasonNumber: Int
    var airDate: String?
    enum CodingKeys: String, CodingKey {
      case episodeNumber = "episode_number"
      case seasonNumber = "season_number"
      case airDate = "air_date"
    }
  }
  private struct Genre: Decodable { var name: String }
  private struct Item: Decodable {
    var id: Int
    var title: String?
    var name: String?
    var overview: String?
    var posterPath: String?
    var voteAverage: Double?
    var voteCount: Int?
    var releaseDate: String?
    var firstAirDate: String?
    var originalLanguage: String?
    var genres: [Genre]?
    var runtime: Int?
    var episodeRunTime: [Int]?
    enum CodingKeys: String, CodingKey {
      case id, title, name, overview, genres, runtime
      case episodeRunTime = "episode_run_time"
      case posterPath = "poster_path"
      case voteAverage = "vote_average"
      case voteCount = "vote_count"
      case releaseDate = "release_date"
      case firstAirDate = "first_air_date"
      case originalLanguage = "original_language"
    }
  }
  private func request<T: Decodable>(
    _ path: String, query: [URLQueryItem] = [], token: String, type: T.Type
  ) async throws -> T {
    let wait = 0.3 - Date.now.timeIntervalSince(lastRequest)
    if wait > 0 { try await Task.sleep(for: .seconds(wait)) }
    try Task.checkCancellation()
    var components = URLComponents(string: "https://api.themoviedb.org/3/\(path)")!
    components.queryItems = query
    var request = URLRequest(url: components.url!)
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    request.setValue("application/json", forHTTPHeaderField: "Accept")
    lastRequest = .now
    let (data, response) = try await session.data(for: request)
    try Task.checkCancellation()
    guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
      data.count <= 3_000_000
    else { throw DiscoveryError.invalidResponse }
    return try JSONDecoder().decode(type, from: data)
  }
  func lookup(
    title: EntertainmentTitle, correction: EntertainmentMatchCorrection?, token: String,
    index: LibraryIndex
  ) async throws -> EntertainmentMetadata {
    let cacheKey = title.metadataCacheKey(providerID: correction?.providerID)
    if let cached = try await index.cachedMetadata(key: cacheKey),
      let data = cached.data(using: .utf8),
      let result = try? JSONDecoder().decode(EntertainmentMetadata.self, from: data)
    {
      return result
    }
    guard !token.isEmpty else { throw DiscoveryError.missingToken }
    let category = title.kind == .movie ? "movie" : "tv"
    let id: Int
    if let corrected = correction?.providerID {
      id = corrected
    } else {
      var query = [
        URLQueryItem(name: "query", value: title.name),
        URLQueryItem(name: "include_adult", value: "false"),
      ]
      if let year = title.year {
        query.append(
          URLQueryItem(
            name: title.kind == .movie ? "primary_release_year" : "first_air_date_year", value: year
          ))
      }
      let search = try await request(
        "search/\(category)", query: query, token: token, type: Search.self)
      let exact = search.results.filter {
        ($0.title ?? $0.name ?? "").compare(
          title.name, options: [.caseInsensitive, .diacriticInsensitive]) == .orderedSame
          && (title.year == nil
            || ($0.releaseDate ?? $0.firstAirDate ?? "").hasPrefix(title.year!))
      }
      guard exact.count == 1, let match = exact.first else { throw DiscoveryError.ambiguous }
      id = match.id
    }
    let item = try await request("\(category)/\(id)", token: token, type: Item.self)
    var result = EntertainmentMetadata(
      providerID: item.id, title: item.title ?? item.name ?? title.name,
      overview: item.overview ?? "",
      posterURL: item.posterPath.flatMap { URL(string: "https://image.tmdb.org/t/p/w500\($0)") },
      rating: item.voteAverage ?? 0, voteCount: item.voteCount ?? 0,
      releaseDate: item.releaseDate ?? item.firstAirDate, genres: item.genres?.map(\.name) ?? [],
      language: item.originalLanguage ?? "",
      runtimeMinutes: item.runtime ?? item.episodeRunTime?.first)
    if title.kind == .series {
      var dates: [String: String] = [:]
      let seasons = Set(title.versions.compactMap(\.season)).sorted()
      for season in seasons.prefix(50) {
        let seasonKey = "tmdb-season|\(id)|\(season)"
        let payload: Season
        if let cached = try await index.cachedMetadata(key: seasonKey),
          let data = cached.data(using: .utf8),
          let decoded = try? JSONDecoder().decode(Season.self, from: data)
        {
          payload = decoded
        } else {
          payload = try await request("tv/\(id)/season/\(season)", token: token, type: Season.self)
          if let cache = String(data: try JSONEncoder().encode(payload), encoding: .utf8) {
            try await index.saveMetadata(key: seasonKey, payload: cache)
          }
        }
        let available = Set(title.versions.filter { $0.season == season }.compactMap(\.episode))
        for episode in payload.episodes where available.contains(episode.episodeNumber) {
          if let date = episode.airDate, !date.isEmpty {
            dates["\(season)|\(episode.episodeNumber)"] = date
          }
        }
      }
      result.episodeReleaseDates = dates
    }
    let data = try JSONEncoder().encode(result)
    if let payload = String(data: data, encoding: .utf8) {
      try await index.saveMetadata(key: cacheKey, payload: payload)
      try await index.saveMetadata(
        key: "tmdb-base|\(title.id)|\(correction?.providerID ?? 0)", payload: payload)
    }
    return result
  }
}
