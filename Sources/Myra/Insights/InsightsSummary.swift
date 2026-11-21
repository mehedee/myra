import Foundation

/// The period the Insights page summarises.
enum InsightsPeriod: String, CaseIterable, Identifiable, Sendable {
  case thisYear, lastYear, allTime

  var id: String { rawValue }

  var title: String {
    switch self {
    case .thisYear: "This year"
    case .lastYear: "Last year"
    case .allTime: "All time"
    }
  }

  /// The calendar year a year period covers. Nil for all time.
  func year(now: Date, calendar: Calendar) -> Int? {
    let current = calendar.component(.year, from: now)
    switch self {
    case .thisYear: return current
    case .lastYear: return current - 1
    case .allTime: return nil
    }
  }
}

/// One playback fact: a version's last recorded play and how far it got.
struct InsightsSample: Equatable, Sendable {
  var titleID: String
  var titleName: String
  var kind: EntertainmentKind
  /// Genres from the title's stored metadata, in metadata order. Empty when none are stored.
  var genres: [String]
  /// When playback was last recorded. Nil for a title marked watched with no playback record.
  var playedAt: Date?
  var seconds: Double
}

struct InsightsTitleTotal: Equatable, Identifiable, Sendable {
  var id: String
  var name: String
  var kind: EntertainmentKind
  var hours: Double
}

struct InsightsGenre: Equatable, Sendable {
  var name: String
  /// This genre's share of the hours that have genre data, from 0 to 1.
  var share: Double
}

/// What the Insights page shows for one period. Built only from the profile's watch history and
/// the stored metadata; nothing is fetched.
struct InsightsSummary: Equatable, Sendable {
  var period: InsightsPeriod
  /// "2026" for a year period, "All time" otherwise.
  var periodLabel: String
  var hoursWatched: Double
  /// Titles marked watched that have playback inside the period.
  var titlesFinished: Int
  /// Consecutive days with playback, ending today or yesterday. Not affected by the period.
  var currentStreakDays: Int
  var topGenre: InsightsGenre?
  /// The five titles with the most hours in the period, most hours first.
  var mostWatched: [InsightsTitleTotal]
  /// Hours per calendar month, January first, for a year period. Empty for all time.
  var hoursByMonth: [Double]
  /// False when the profile has no playback history at all.
  var hasHistory: Bool

  /// Builds the summary. Pure: the same samples, date and calendar always give the same result.
  static func make(
    samples: [InsightsSample], watchedTitleIDs: Set<String>, period: InsightsPeriod,
    now: Date, calendar: Calendar
  ) -> InsightsSummary {
    let year = period.year(now: now, calendar: calendar)
    let inPeriod = samples.filter { sample in
      guard let year else { return true }
      guard let playedAt = sample.playedAt else { return false }
      return calendar.component(.year, from: playedAt) == year
    }

    let hours = inPeriod.reduce(0) { $0 + $1.seconds } / 3600
    let finished = Set(inPeriod.map(\.titleID)).intersection(watchedTitleIDs).count

    var titleHours: [String: InsightsTitleTotal] = [:]
    for sample in inPeriod where sample.seconds > 0 {
      if titleHours[sample.titleID] == nil {
        titleHours[sample.titleID] = InsightsTitleTotal(
          id: sample.titleID, name: sample.titleName, kind: sample.kind, hours: 0)
      }
      titleHours[sample.titleID]?.hours += sample.seconds / 3600
    }
    let mostWatched = titleHours.values.sorted {
      if $0.hours != $1.hours { return $0.hours > $1.hours }
      if $0.name != $1.name { return $0.name.localizedStandardCompare($1.name) == .orderedAscending }
      return $0.id < $1.id
    }
    .prefix(5)

    // Each title counts toward its first genre only, so the shares add up to 100 percent.
    var genreSeconds: [String: Double] = [:]
    for sample in inPeriod where sample.seconds > 0 {
      guard let primary = sample.genres.first else { continue }
      genreSeconds[primary, default: 0] += sample.seconds
    }
    let genreTotal = genreSeconds.values.reduce(0, +)
    let topGenre = genreSeconds.max { left, right in
      // Ties go to the alphabetically earlier genre.
      left.value != right.value ? left.value < right.value : left.key > right.key
    }.map { InsightsGenre(name: $0.key, share: $0.value / genreTotal) }

    var monthly: [Double] = []
    if year != nil {
      monthly = Array(repeating: 0, count: 12)
      for sample in inPeriod {
        guard let playedAt = sample.playedAt else { continue }
        let month = calendar.component(.month, from: playedAt) - 1
        if (0..<12).contains(month) { monthly[month] += sample.seconds / 3600 }
      }
    }

    let playDays = Set(samples.compactMap(\.playedAt).map { calendar.startOfDay(for: $0) })
    return InsightsSummary(
      period: period,
      periodLabel: year.map(String.init) ?? "All time",
      hoursWatched: hours,
      titlesFinished: finished,
      currentStreakDays: currentStreak(playDays: playDays, now: now, calendar: calendar),
      topGenre: topGenre,
      mostWatched: Array(mostWatched),
      hoursByMonth: monthly,
      hasHistory: !samples.isEmpty)
  }

  /// Counts back from today, or from yesterday when today has no playback yet, one day at a time.
  static func currentStreak(playDays: Set<Date>, now: Date, calendar: Calendar) -> Int {
    var day = calendar.startOfDay(for: now)
    if !playDays.contains(day) {
      guard let yesterday = calendar.date(byAdding: .day, value: -1, to: day) else { return 0 }
      day = yesterday
    }
    var count = 0
    while playDays.contains(day), let previous = calendar.date(byAdding: .day, value: -1, to: day)
    {
      count += 1
      day = previous
    }
    return count
  }

  /// The plain-text summary for the clipboard. Contains no links and nothing is sent anywhere.
  func shareText() -> String {
    var lines = ["Myra Insights, \(periodLabel)"]
    guard hasHistory else {
      lines.append("No watch history yet.")
      return lines.joined(separator: "\n")
    }
    lines.append("Hours watched: \(String(format: "%.1f", hoursWatched))")
    lines.append("Titles finished: \(titlesFinished)")
    lines.append(
      "Current watch streak: \(currentStreakDays) \(currentStreakDays == 1 ? "day" : "days")")
    if let topGenre {
      lines.append("Top genre: \(topGenre.name) (\(Int((topGenre.share * 100).rounded()))%)")
    } else {
      lines.append("Top genre: no genre data yet")
    }
    if !mostWatched.isEmpty {
      lines.append("")
      lines.append("Most watched:")
      for (offset, title) in mostWatched.enumerated() {
        lines.append("\(offset + 1). \(title.name), \(String(format: "%.1f", title.hours)) h")
      }
    }
    lines.append("")
    lines.append("Computed on this \(MyraDevice.name) from watch history. Nothing was uploaded.")
    return lines.joined(separator: "\n")
  }
}

enum InsightsHistory {
  /// Playback samples from the catalogue. Each version with a recorded play gives one sample at its
  /// last play, with the position it reached. A title marked watched that has no playback record
  /// gives one undated sample, using the movie runtime when metadata has it.
  static func samples(catalogue: [EntertainmentTitle], personal: EntertainmentPersonalData)
    -> [InsightsSample]
  {
    var samples: [InsightsSample] = []
    var titlesWithPlayback: Set<String> = []
    for title in catalogue {
      let genres = title.metadata?.genres ?? []
      for version in title.versions {
        guard let playedAt = version.lastPlayed else { continue }
        let reached =
          version.duration > 0 ? min(version.progressSeconds, version.duration) : version.progressSeconds
        samples.append(
          InsightsSample(
            titleID: title.id, titleName: title.displayName, kind: title.kind, genres: genres,
            playedAt: playedAt, seconds: max(0, reached)))
        titlesWithPlayback.insert(title.id)
      }
    }
    for title in catalogue
    where personal.watched.contains(title.id) && !titlesWithPlayback.contains(title.id) {
      let minutes = title.kind == .movie ? (title.metadata?.runtimeMinutes ?? 0) : 0
      samples.append(
        InsightsSample(
          titleID: title.id, titleName: title.displayName, kind: title.kind,
          genres: title.metadata?.genres ?? [], playedAt: nil, seconds: Double(max(0, minutes)) * 60))
    }
    return samples
  }
}
