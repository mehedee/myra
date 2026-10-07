import Foundation

struct PlayerSkipRange: Codable, Equatable, Sendable {
  var start: Double
  var end: Double
  func valid(duration: Double) -> Bool {
    start.isFinite && end.isFinite && start >= 0 && end > start && end <= duration
  }
  func contains(_ seconds: Double) -> Bool { seconds >= start && seconds < end }
}

struct PlayerSkipMarkers: Codable, Equatable, Sendable {
  var intro: PlayerSkipRange?
  var outro: PlayerSkipRange?
}

/// Durable viewing choices are separate from the rebuildable media index.
struct PlayerPersonalState: Codable, Equatable, Sendable {
  var speed: Float = 1
  var audioLanguage = "en"
  var subtitleLanguage = "en"
  var autoplay = true
  var automaticSkipping = false
  var markers: [String: PlayerSkipMarkers] = [:]

  static let defaultsKey = "Myra.playerPersonalState.v2"
  static func load(defaults: UserDefaults = .standard) -> Self {
    guard let data = defaults.data(forKey: defaultsKey),
      let state = try? JSONDecoder().decode(Self.self, from: data)
    else { return Self() }
    return state
  }
  func save(defaults: UserDefaults = .standard) throws {
    defaults.set(try JSONEncoder().encode(self), forKey: Self.defaultsKey)
  }
}

enum PlayerLanguagePreference {
  static func preferred(in tracks: [PlayerTrack], language: String, subtitles: Bool) -> Int32? {
    if language == "off" { return -1 }
    if language == "default" { return nil }
    if language == "en" {
      return EnglishTrackPreference.preferred(in: tracks, subtitles: subtitles)
    }
    let aliases: [String: [String]] = [
      "bn": ["bn", "ben", "bengali", "bangla"], "hi": ["hi", "hin", "hindi"],
      "fr": ["fr", "fra", "fre", "french"], "ja": ["ja", "jpn", "japanese"],
      "es": ["es", "spa", "spanish"], "ko": ["ko", "kor", "korean"],
    ]
    let tokens = aliases[language] ?? [language]
    return tracks.first { track in
      let code = track.language?.lowercased().split(separator: "-").first.map(String.init) ?? ""
      return track.id >= 0
        && (tokens.contains(code)
          || tokens.contains { token in
            track.name.lowercased().range(
              of: "\\b" + NSRegularExpression.escapedPattern(for: token) + "\\b",
              options: .regularExpression) != nil
          })
    }?.id
  }
}
