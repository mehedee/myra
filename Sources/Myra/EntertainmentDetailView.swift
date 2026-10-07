import SwiftUI

/// Keeps source identity visible while allowing a persistent manual metadata correction.
struct EntertainmentDetailView: View {
  let title: EntertainmentTitle
  @ObservedObject var store: EntertainmentStore
  let play: (EntertainmentTitle) -> Void
  var pickFilters: String? = nil
  var anotherPick: (() -> Void)? = nil
  var aiAction: ((MyraAIFeature, EntertainmentTitle) -> Void)? = nil
  @Environment(\.dismiss) private var dismiss
  @State private var correctedTitle = ""
  @State private var correctedYear = ""
  @State private var correctedKind: EntertainmentKind = .movie
  @State private var providerID = ""
  @State private var selectedVersion = ""
  @State private var applyToAll = true
  @State private var correctionError: String?

  private var current: EntertainmentTitle { store.catalogue.first { $0.id == title.id } ?? title }

  var body: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 18) {
        HStack {
          VStack(alignment: .leading, spacing: 6) {
            if let pickFilters {
              Label("Your pick", systemImage: "dice").font(.headline).foregroundStyle(.secondary)
              Text("An unwatched title matching \(pickFilters).")
                .font(.caption).foregroundStyle(.secondary)
            }
            Text(current.displayName).font(.largeTitle.bold())
          }
          Spacer()
          if let anotherPick {
            Button(action: anotherPick) { Label("Another Pick", systemImage: "dice") }
          }
          Button(anotherPick == nil ? "Done" : "Close") { dismiss() }
            .keyboardShortcut(.cancelAction)
        }
        HStack(alignment: .top, spacing: 22) {
          AsyncImage(url: current.posterURL) { image in
            image.resizable().scaledToFit()
          } placeholder: {
            Image(systemName: "film.stack").font(.system(size: 60)).frame(width: 170, height: 230)
              .background(.quaternary)
          }
          .frame(width: 170, height: 250).clipShape(RoundedRectangle(cornerRadius: 12))
          VStack(alignment: .leading, spacing: 12) {
            Text(
              [current.year, current.kind.rawValue.capitalized].compactMap { $0 }.joined(
                separator: " · ")
            )
            .foregroundStyle(.secondary)
            if let metadata = current.metadata {
              Label(
                "TMDB \(metadata.rating, specifier: "%.1f") / 10 • \(metadata.voteCount) votes",
                systemImage: "star.fill"
              )
              .foregroundStyle(.yellow)
              Text(metadata.overview.isEmpty ? "No synopsis available." : metadata.overview)
                .textSelection(.enabled)
              Text(metadata.genres.joined(separator: " · ")).foregroundStyle(.secondary)
              if let release = metadata.releaseDate { Text("Released: \(release)") }
              Text("Original language: \(metadata.language)").foregroundStyle(.secondary)
              Link(
                "View on TMDB",
                destination: URL(
                  string:
                    "https://www.themoviedb.org/\(current.kind == .series ? "tv" : "movie")/\(metadata.providerID)"
                )!)
            } else {
              Text(
                "Metadata has not been matched yet. Add a TMDB API key in Settings and update metadata, or correct the filename match below."
              )
              .foregroundStyle(.secondary)
            }
            HStack {
              Button(current.resumeVersion == nil ? "Play" : "Resume") { play(current) }
                .buttonStyle(.borderedProminent)
              Button(
                store.personal.watchlist.contains(current.id)
                  ? "Remove from Watchlist" : "Add to Watchlist"
              ) { store.toggleWatchlist(current.id) }
              Button(
                store.personal.watched.contains(current.id) ? "Mark unwatched" : "Mark watched"
              ) { store.toggleWatched(current.id) }
            }
            if current.kind == .series {
              Button(
                store.personal.followed.contains(current.id) ? "Unfollow series" : "Follow series"
              ) { store.toggleFollow(current.id) }
            }
            if !store.personal.collections.isEmpty {
              Menu("Add to / remove from collection") {
                ForEach(store.personal.collections) { collection in
                  Button(
                    "\(collection.titleIDs.contains(current.id) ? "✓ " : "")\(collection.name)"
                  ) {
                    store.toggleCollection(titleID: current.id, collectionID: collection.id)
                  }
                }
              }
            }
          }
        }
        if let aiAction {
          HStack {
            Label("Explore with Myra", systemImage: "sparkles").font(.headline)
            Spacer()
            Menu("AI tools") {
              Button("More Like This") { aiAction(.similar, current) }
              Button("Compare Titles") { aiAction(.compare, current) }
              Button("Short Summary") { aiAction(.summary, current) }
              Button("Why This Title?") { aiAction(.explain, current) }
              Button("Suggest Mood Tags") { aiAction(.moodTags, current) }
              Button("Translate Description") { aiAction(.translate, current) }
              Button("Episode Recap") { aiAction(.recap, current) }
                .disabled(current.kind != .series)
              Button("Suggest Match Corrections") { aiAction(.cleanup, current) }
            }
          }
        }
        Divider()
        if current.kind == .series {
          Text("Available Episodes").font(.title2.bold())
          SeasonEpisodeList(
            versions: current.versions, isSeries: true,
            currentID: nil, lastPlayedID: lastPlayedID
          ) { version in
            var selected = current
            selected.versions = [version]
            play(selected)
          }.frame(height: 380)

        } else {
          Text("Available Versions").font(.title2.bold())
          ForEach(current.versions) { version in
            EntertainmentVersionRow(version: version, lastPlayed: lastPlayedID == version.id) {
              var single = current
              single.versions = [version]
              play(single)
            }
          }
        }
        DisclosureGroup("Correct Match / Grouping") {
          VStack(alignment: .leading, spacing: 10) {
            Text(
              "A shared folder is not proof of identity. Correct a file’s title and year to merge alternate versions, or give an incorrectly grouped file its own identity. TMDB ID is optional."
            )
            .font(.caption).foregroundStyle(.secondary)
            Picker("File", selection: $selectedVersion) {
              ForEach(current.versions) { Text($0.media.entry.name).tag($0.id) }
            }
            TextField("Movie or series title", text: $correctedTitle)
            TextField("Year (optional)", text: $correctedYear)
            Picker("Type", selection: $correctedKind) {
              Text("Movie").tag(EntertainmentKind.movie)
              Text("Series").tag(EntertainmentKind.series)
            }
            TextField("TMDB ID (optional, numeric)", text: $providerID)
            Toggle("Apply to every version in this title", isOn: $applyToAll).toggleStyle(.checkbox)
            if let correctionError { Text(correctionError).foregroundStyle(.orange) }
            Button("Save correction") { saveCorrection() }
              .disabled(correctedTitle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
          }.textFieldStyle(.roundedBorder).padding(.top, 12)
        }
        Text("Metadata provided by TMDB. Myra is not endorsed or certified by TMDB.")
          .font(.caption).foregroundStyle(.secondary)
      }.padding(24)
    }.frame(minWidth: 720, idealWidth: 820, minHeight: 560, idealHeight: 720)
      .onAppear {
        correctedTitle = current.name
        correctedYear = current.year ?? ""
        correctedKind = current.kind
        selectedVersion = current.versions.first?.id ?? ""
      }
  }

  private var lastPlayedID: String? {
    current.versions.max {
      (store.personal.history[$0.id]?.updated ?? .distantPast)
        < (store.personal.history[$1.id]?.updated ?? .distantPast)
    }.flatMap { store.personal.history[$0.id] == nil ? nil : $0.id }
  }

  private var episodeGroups: [EntertainmentEpisodeGroup] {
    EntertainmentEpisodeGroup.groups(current.versions)
  }

  private func saveCorrection() {
    let year = correctedYear.trimmingCharacters(in: .whitespacesAndNewlines)
    let id = providerID.trimmingCharacters(in: .whitespacesAndNewlines)
    guard year.isEmpty || (year.count == 4 && Int(year).map { (1900...2099).contains($0) } == true)
    else {
      correctionError = "Enter a four-digit year between 1900 and 2099."
      return
    }
    guard id.isEmpty || Int(id).map({ $0 > 0 }) == true else {
      correctionError = "TMDB ID must be a positive number."
      return
    }
    correctionError = nil
    let versions =
      applyToAll ? current.versions : current.versions.filter { $0.id == selectedVersion }
    for version in versions {
      store.correctMatch(
        versionID: version.id,
        title: correctedTitle.trimmingCharacters(in: .whitespacesAndNewlines),
        year: year.isEmpty ? nil : year, kind: correctedKind, providerID: Int(id))
    }
    dismiss()
  }
}

struct EntertainmentEpisodeGroup: Identifiable {
  let id: String
  let season: Int?
  let episode: Int?
  let versions: [EntertainmentVersion]
  var label: String {
    guard let season, let episode else { return versions.first?.media.entry.name ?? "Video" }
    return String(format: "Season %d · Episode %02d", season, episode)
  }

  static func groups(_ versions: [EntertainmentVersion]) -> [EntertainmentEpisodeGroup] {
    var grouped: [String: [EntertainmentVersion]] = [:]
    for version in versions {
      let key = version.episode.map { "\(version.season ?? 0)|\($0)" } ?? version.id
      grouped[key, default: []].append(version)
    }
    return grouped.map { key, values in
      EntertainmentEpisodeGroup(
        id: key, season: values.first?.season, episode: values.first?.episode, versions: values)
    }.sorted {
      if ($0.season ?? 0) != ($1.season ?? 0) { return ($0.season ?? 0) < ($1.season ?? 0) }
      if ($0.episode ?? 0) != ($1.episode ?? 0) { return ($0.episode ?? 0) < ($1.episode ?? 0) }
      return $0.label.localizedStandardCompare($1.label) == .orderedAscending
    }
  }
}

/// Explicit version selection preserves source identity and groups series by season.
struct EntertainmentVersionChooser: View {
  let title: EntertainmentTitle
  @ObservedObject var store: EntertainmentStore
  let choose: (EntertainmentVersion) -> Void
  @Environment(\.dismiss) private var dismiss

  private var lastPlayed: String? {
    title.versions.max {
      (store.personal.history[$0.id]?.updated ?? .distantPast)
        < (store.personal.history[$1.id]?.updated ?? .distantPast)
    }.flatMap { store.personal.history[$0.id] == nil ? nil : $0.id }
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 14) {
      HStack {
        VStack(alignment: .leading, spacing: 4) {
          Text(title.kind == .series ? "Choose an episode" : "Choose a version").font(
            .title2.bold())
          Text(title.displayName).foregroundStyle(.secondary)
        }
        Spacer()
        Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
      }
      Text(
        "Availability is based on your index. Quality labels come from filenames; unknown properties are shown explicitly."
      )
      .font(.caption).foregroundStyle(.secondary)
      SeasonEpisodeList(
        versions: title.versions, isSeries: title.kind == .series,
        currentID: nil, lastPlayedID: lastPlayed, choose: choose)

    }.padding(24).frame(minWidth: 640, idealWidth: 750, minHeight: 340, idealHeight: 520)
  }
}

struct EntertainmentVersionRow: View {
  let version: EntertainmentVersion
  let lastPlayed: Bool
  let choose: () -> Void
  var body: some View {
    HStack(alignment: .top, spacing: 12) {
      Image(systemName: "film").font(.title2).foregroundStyle(.tint)
      VStack(alignment: .leading, spacing: 5) {
        Text(version.media.entry.name).font(.headline).textSelection(.enabled)
        Text(version.quality.isEmpty ? "Quality / encoding unknown" : version.quality)
          .font(.caption).foregroundStyle(.secondary)
        Text(
          "Source: \(version.media.categoryName) • \(version.media.entry.size.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "Size unknown")"
        )
        .font(.caption).foregroundStyle(.secondary)
        Text(version.media.relativePath).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
        if version.progressSeconds >= 5 {
          Text(
            "Saved progress: \(Int(version.progressSeconds / 60)) min \(Int(version.progressSeconds.truncatingRemainder(dividingBy: 60))) sec"
          )
          .font(.caption)
        }
        if lastPlayed {
          Label("Previously played", systemImage: "clock.arrow.circlepath").font(.caption)
            .foregroundStyle(.tint)
        }
      }
      Spacer()
      Button(version.progressSeconds >= 5 ? "Resume" : "Play", action: choose).buttonStyle(
        .borderedProminent)
    }.padding(12).background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
  }
}

struct EntertainmentSeason: Identifiable {
  let number: Int?
  let episodes: [EntertainmentEpisodeGroup]
  var id: String { number.map(String.init) ?? "other" }
  var label: String { number.map { "Season \($0)" } ?? "Other files" }

  static func seasons(_ versions: [EntertainmentVersion]) -> [EntertainmentSeason] {
    let episodes = EntertainmentEpisodeGroup.groups(versions)
    let numbers = Set(episodes.map(\.season)).sorted { ($0 ?? Int.max) < ($1 ?? Int.max) }
    return numbers.map { number in
      EntertainmentSeason(number: number, episodes: episodes.filter { $0.season == number })
    }
  }
}

/// Shared by Home and playback; only the selected season is expanded initially.
struct SeasonEpisodeList: View {
  let versions: [EntertainmentVersion]
  let isSeries: Bool
  let currentID: String?
  let lastPlayedID: String?
  let choose: (EntertainmentVersion) -> Void
  @State private var expandedSeasons: Set<String> = []

  var body: some View {
    ScrollView {
      LazyVStack(alignment: .leading, spacing: 12) {
        if isSeries {
          ForEach(EntertainmentSeason.seasons(versions)) { season in
            DisclosureGroup(
              isExpanded: Binding(
                get: { expandedSeasons.contains(season.id) },
                set: {
                  if $0 {
                    expandedSeasons.insert(season.id)
                  } else {
                    expandedSeasons.remove(season.id)
                  }
                }
              )
            ) {
              VStack(spacing: 8) {
                ForEach(season.episodes) { episode in episodeRow(episode) }
              }.padding(.top, 10)
            } label: {
              HStack {
                Text(season.label).font(.headline)
                Spacer()
                Text(
                  "\(season.episodes.count) \(season.episodes.count == 1 ? "episode" : "episodes")"
                ).font(.caption).foregroundStyle(.secondary)
              }.padding(.vertical, 6)
            }
            .padding(14).background(
              .quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
          }
        } else {
          ForEach(versions) { version in
            EntertainmentVersionRow(version: version, lastPlayed: version.id == lastPlayedID) {
              choose(version)
            }
          }
        }
      }
    }
    .onAppear {
      let selected =
        versions.first { $0.id == currentID } ?? versions.first { $0.id == lastPlayedID }
        ?? versions.first
      expandedSeasons = [selected?.season.map(String.init) ?? "other"]
    }
  }

  @ViewBuilder private func episodeRow(_ episode: EntertainmentEpisodeGroup) -> some View {
    if episode.versions.count > 1 {
      DisclosureGroup {
        ForEach(episode.versions) { version in
          EntertainmentVersionRow(version: version, lastPlayed: version.id == lastPlayedID) {
            choose(version)
          }
        }
      } label: {
        HStack {
          episodeLabel(episode)
          Spacer()
          Text("\(episode.versions.count) versions").font(.caption).foregroundStyle(.secondary)
        }
      }.padding(10).background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 8))
    } else if let version = episode.versions.first {
      HStack(spacing: 12) {
        episodeLabel(episode)
        Spacer()
        VStack(alignment: .trailing, spacing: 4) {
          Text(version.quality.isEmpty ? "Quality unknown" : version.quality)
          Text(version.media.categoryName)
        }.font(.caption).foregroundStyle(.secondary).lineLimit(1)
        Button(version.progressSeconds >= 5 ? "Resume" : "Play") { choose(version) }
          .buttonStyle(.borderedProminent)
      }.padding(10).background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 8))
        .help(version.media.entry.name)
    }
  }

  private func episodeLabel(_ episode: EntertainmentEpisodeGroup) -> some View {
    VStack(alignment: .leading, spacing: 4) {
      HStack(spacing: 6) {
        if episode.versions.contains(where: { $0.id == currentID }) {
          Image(systemName: "play.circle.fill").foregroundStyle(.tint)
        }
        Text(episode.episode.map { String(format: "Episode %02d", $0) } ?? episode.label).font(
          .headline)
        if episode.versions.contains(where: { $0.id == lastPlayedID }) {
          Text("Last played").font(.caption).foregroundStyle(.tint)
        }
      }
      if let progress = episode.versions.first(where: { $0.progressSeconds >= 5 }) {
        Text("Resume at " + PlayerTime.string(progress.progressSeconds)).font(.caption)
          .foregroundStyle(.secondary)
        if progress.duration > 0 {
          ProgressView(value: min(1, progress.progressSeconds / progress.duration)).frame(
            maxWidth: 180)
        }
      }
    }
  }
}

/// Uses the same information and playback actions as Details, without a Home shelf.
struct EntertainmentPickView: View {
  let title: EntertainmentTitle?
  @ObservedObject var store: EntertainmentStore
  let filters: String
  let anotherPick: () -> Void
  var resetFilters: (() -> Void)? = nil
  let play: (EntertainmentTitle) -> Void
  @Environment(\.dismiss) private var dismiss

  var body: some View {
    if let title {
      EntertainmentDetailView(
        title: title, store: store, play: play, pickFilters: filters, anotherPick: anotherPick
      ).id(title.id)
    } else {
      VStack(spacing: 20) {
        ContentUnavailableView(
          "No unwatched titles match", systemImage: "dice",
          description: Text("Try broadening your Home filters or refreshing your library."))
        if let resetFilters {
          Button("Reset Home filters") {
            dismiss()
            resetFilters()
          }
        }
        Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
      }.padding(24).frame(minWidth: 720, minHeight: 560)
    }
  }
}

/// Retains current filters and only repeats a title when it is the sole eligible choice.
enum EntertainmentPick {
  static func select(
    from titles: [EntertainmentTitle], watched: Set<String>, excluding previousID: String?
  ) -> EntertainmentTitle? {
    var eligible = titles.filter { !watched.contains($0.id) }
    if eligible.count > 1 { eligible.removeAll { $0.id == previousID } }
    return eligible.randomElement()
  }
}
