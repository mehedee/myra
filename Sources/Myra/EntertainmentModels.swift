import Foundation

enum EntertainmentKind: String, Codable, Sendable, CaseIterable { case movie, series }

struct EntertainmentMetadata: Codable, Sendable, Equatable {
  var providerID: Int
  var title: String
  var overview: String
  var posterURL: URL?
  var rating: Double
  var voteCount: Int
  var releaseDate: String?
  var genres: [String]
  var language: String
  var runtimeMinutes: Int? = nil
  var episodeReleaseDates: [String: String]? = nil
}

struct EntertainmentVersion: Identifiable, Sendable, Hashable, Codable {
  var media: GlobalSearchResult
  var firstDiscovered: Date
  var progressSeconds: Double = 0
  var duration: Double = 0
  var lastPlayed: Date? = nil
  var season: Int?
  var episode: Int?
  var id: String { media.entry.url.absoluteString }
  var quality: String {
    let name = media.entry.name
    let tokens = [
      "2160p", "1080p", "720p", "480p", "4K", "BluRay", "WEB-DL", "WEBRip", "HDR", "x265", "x264",
    ]
    return tokens.filter { name.localizedCaseInsensitiveContains($0) }.joined(separator: " · ")
  }
}

struct EntertainmentTitle: Identifiable, Sendable, Codable {
  var id: String
  var name: String
  var year: String?
  var kind: EntertainmentKind
  var versions: [EntertainmentVersion]
  var metadata: EntertainmentMetadata?
  var firstDiscovered: Date { versions.map(\.firstDiscovered).min() ?? .distantPast }
  var latestDiscovered: Date { versions.map(\.firstDiscovered).max() ?? .distantPast }
  func metadataCacheKey(providerID: Int? = nil) -> String {
    let episodes = Set(
      versions.compactMap { version -> String? in
        guard let season = version.season, let episode = version.episode else { return nil }
        return "\(season):\(episode)"
      }
    ).sorted().joined(separator: ",")
    return "tmdb|\(id)|\(providerID ?? 0)|\(episodes)"
  }
  var displayName: String { metadata?.title ?? name }
  var posterURL: URL? { metadata?.posterURL ?? versions.first?.media.artworkURL }
  var latestReleaseDate: String? {
    if kind == .series { return metadata?.episodeReleaseDates?.values.max() }
    return metadata?.releaseDate
  }
  var resumeVersion: EntertainmentVersion? {
    // Never jump back to an older episode when the latest episode has finished.
    let latest = versions.filter { $0.lastPlayed != nil }.max {
      ($0.lastPlayed ?? .distantPast) < ($1.lastPlayed ?? .distantPast)
    }
    guard let latest, latest.progressSeconds >= 5,
      latest.duration == 0 || latest.duration > latest.progressSeconds + 10
    else { return nil }
    return latest
  }
}

struct EntertainmentCollection: Codable, Identifiable, Sendable {
  var id: UUID = UUID()
  var name: String
  var titleIDs: Set<String> = []
}

struct EntertainmentPreferences: Codable, Sendable, Equatable {
  var notifications: Bool = false
}

struct EntertainmentMatchCorrection: Codable, Sendable {
  var title: String
  var year: String?
  var kind: EntertainmentKind
  var providerID: Int?
}

struct EntertainmentPlaybackRecord: Codable, Sendable {
  var seconds: Double
  var duration: Double
  var updated: Date
}

struct EntertainmentPersonalData: Codable, Sendable {
  var schemaVersion: Int = 1
  var watchlist: Set<String> = []
  var watched: Set<String> = []
  var collections: [EntertainmentCollection] = []
  var followed: Set<String> = []
  var knownEpisodeIDs: Set<String> = []
  var matchCorrections: [String: EntertainmentMatchCorrection] = [:]
  var preferences = EntertainmentPreferences()
  var history: [String: EntertainmentPlaybackRecord] = [:]
}

enum EntertainmentGrouping {
  static func group(
    _ versions: [EntertainmentVersion], corrections: [String: EntertainmentMatchCorrection] = [:]
  ) -> [EntertainmentTitle] {
    var titles: [String: EntertainmentTitle] = [:]
    for var version in versions {
      let identity = MediaIdentity.parse(filename: version.media.entry.name)
      let correction = corrections[version.id]
      version.season = identity.season.flatMap(Int.init)
      version.episode = identity.episode.flatMap(Int.init)
      let name = correction?.title ?? identity.title
      let year = correction?.year ?? identity.year
      let kind = correction?.kind ?? (version.episode == nil ? .movie : .series)
      let normalized = name.folding(
        options: [.caseInsensitive, .diacriticInsensitive],
        locale: Locale(identifier: "en_US_POSIX")
      ).trimmingCharacters(in: .whitespacesAndNewlines)
      // A year, episode token, or explicit correction is required to merge across files.
      let confident = year != nil || version.episode != nil || correction != nil
      let hasReleaseToken = !version.quality.isEmpty
      let folder = version.media.entry.url.deletingLastPathComponent().absoluteString
      let key: String
      if confident {
        key = "\(kind.rawValue)|\(normalized)|\(year ?? "")"
      } else if hasReleaseToken {
        key = "folder|\(folder)|\(normalized)"
      } else {
        key = "file|\(version.id)"
      }
      if titles[key] == nil {
        titles[key] = EntertainmentTitle(id: key, name: name, year: year, kind: kind, versions: [])
      }
      titles[key]?.versions.append(version)
    }
    return titles.values.map { title in
      var value = title
      value.versions.sort {
        if ($0.season ?? 0) != ($1.season ?? 0) { return ($0.season ?? 0) < ($1.season ?? 0) }
        if ($0.episode ?? 0) != ($1.episode ?? 0) { return ($0.episode ?? 0) < ($1.episode ?? 0) }
        return $0.media.entry.name.localizedStandardCompare($1.media.entry.name)
          == .orderedAscending
      }
      return value
    }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
  }
}
