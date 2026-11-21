#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Keeps source identity visible while allowing a persistent manual metadata correction.
/// Styled with the Home page's shared components: section headers, row cards and button styles.
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
  /// The note being edited. Nil until loaded, so a blank placeholder is never saved over a note.
  @State private var noteDraft: String?

  private var current: EntertainmentTitle { store.catalogue.first { $0.id == title.id } ?? title }

  var body: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 24) {
        header
        hero
        personalSection
        castSection
        if let aiAction {
          SectionHeader(title: "Explore with Myra")
          aiMenu(aiAction)
        }
        if current.kind == .series {
          SectionHeader(title: "Available Episodes", count: current.versions.count)
          SeasonEpisodeList(
            versions: current.versions, isSeries: true,
            currentID: nil, lastPlayedID: lastPlayedID
          ) { version in
            var selected = current
            selected.versions = [version]
            play(selected)
          }.frame(height: 380)
        } else {
          SectionHeader(title: "Available Versions", count: current.versions.count)
          RowCard(
            rows: current.versions.map { version in
              EntertainmentVersionRows.item(
                version: version, title: version.media.entry.name, tags: [],
                lastPlayed: lastPlayedID == version.id, nowPlaying: false
              ) {
                var single = current
                single.versions = [version]
                play(single)
              }
            })
        }
        correction
        Text("Metadata provided by TMDB. Myra is not endorsed or certified by TMDB.")
          .font(.caption).foregroundStyle(MyraColor.text3)
      }
      .padding(.horizontal, 28)
      .padding(.bottom, 28)
    }
    .contentMargins(.top, 20, for: .scrollContent)
    .frame(minWidth: 720, idealWidth: 820, minHeight: 560, idealHeight: 720)
    .onAppear {
      correctedTitle = current.name
      correctedYear = current.year ?? ""
      correctedKind = current.kind
      selectedVersion = current.versions.first?.id ?? ""
      noteDraft = store.personal.notes?[current.id] ?? ""
    }
    .onDisappear {
      if let noteDraft { store.setNote(current.id, text: noteDraft) }
    }
  }

  private var currentRating: Int { store.personal.ratings?[current.id] ?? 0 }

  /// Your rating and private notes. Both live in the personal data, keyed by the title, so a
  /// metadata refresh never changes them.
  private var personalSection: some View {
    VStack(alignment: .leading, spacing: 10) {
      SectionHeader(title: "Your Rating")
      HStack(spacing: 6) {
        ForEach(1...5, id: \.self) { star in
          Button {
            store.setRating(current.id, stars: star)
          } label: {
            Image(systemName: star <= currentRating ? "star.fill" : "star")
              .font(.system(size: 18))
              .foregroundStyle(star <= currentRating ? Color.yellow : MyraColor.text3)
          }
          .buttonStyle(.plain)
          .accessibilityLabel("\(star) star\(star == 1 ? "" : "s")")
          .accessibilityAddTraits(star == currentRating ? .isSelected : [])
        }
        if currentRating > 0 {
          Button("Clear") { store.setRating(current.id, stars: nil) }
            .myraLinkButtonStyle()
            .font(.system(size: 12))
            .padding(.leading, 6)
        }
      }
      SectionHeader(title: "Private Notes")
      TextEditor(
        text: Binding(get: { noteDraft ?? "" }, set: { noteDraft = $0 })
      )
      .font(.system(size: 13))
      .frame(minHeight: 84)
      .scrollContentBackground(.hidden)
      .padding(6)
      .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 8))
      .accessibilityLabel("Private notes")
      // Saves once typing pauses; leaving the sheet saves at once (see onDisappear).
      .task(id: noteDraft) {
        guard let draft = noteDraft else { return }
        try? await Task.sleep(for: .milliseconds(600))
        guard !Task.isCancelled else { return }
        store.setNote(current.id, text: draft)
      }
      Text("Only on this \(MyraDevice.name). Ratings and notes are kept through metadata refreshes and included in your backup.")
        .font(.caption).foregroundStyle(MyraColor.text3)
    }
  }

  /// Cast and crew from the title's stored metadata. Nothing is looked up when it is absent.
  private var castSection: some View {
    VStack(alignment: .leading, spacing: 10) {
      SectionHeader(title: "Cast & Crew")
      if let credits = current.metadata?.credits, !credits.isEmpty {
        FlowLayout(spacing: 16) {
          ForEach(Array(credits.enumerated()), id: \.offset) { _, credit in
            HStack(spacing: 8) {
              Text(Self.initials(credit.name))
                .font(.system(size: 12, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(Circle().fill(MyraColor.text2))
              VStack(alignment: .leading, spacing: 2) {
                Text(credit.name).font(.system(size: 13, weight: .semibold))
                Text(credit.role)
                  .font(.system(size: 12)).foregroundStyle(MyraColor.text2)
              }
            }
          }
        }
      } else {
        Text("Cast and crew are not in this title's metadata yet.")
          .font(.system(size: 13))
          .foregroundStyle(MyraColor.text2)
      }
    }
  }

  private static func initials(_ name: String) -> String {
    name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
  }

  /// Opens the trailer only when the stored metadata carries a link. No lookup happens here.
  private func openTrailer() {
    guard let url = current.metadata?.openableTrailerURL else { return }
    PlatformServices.open(url)
  }

  private var header: some View {
    HStack(alignment: .top) {
      VStack(alignment: .leading, spacing: 6) {
        if let pickFilters {
          Label("Your pick", systemImage: "dice")
            .font(.system(size: 13, weight: .semibold)).foregroundStyle(MyraColor.text2)
          Text("An unwatched title matching \(pickFilters).")
            .font(.caption).foregroundStyle(MyraColor.text2)
        }
        Text(current.displayName)
          .font(.system(size: 26, weight: .bold))
          .lineLimit(2)
      }
      Spacer(minLength: 12)
      if let anotherPick {
        Button(action: anotherPick) { Label("Another Pick", systemImage: "dice") }
          .buttonStyle(MyraButtonStyle())
      }
      Button(anotherPick == nil ? "Done" : "Close") { dismiss() }
        .buttonStyle(MyraButtonStyle())
        .keyboardShortcut(.cancelAction)
    }
  }

  /// Poster (2:3, 10pt radius), facts, metadata and the action row.
  private var hero: some View {
    HStack(alignment: .top, spacing: 24) {
      Color.clear
        .aspectRatio(2.0 / 3.0, contentMode: .fit)
        .overlay {
          FallbackArtworkView(title: current.displayName, candidates: posterCandidates)
        }
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .shadow(color: .black.opacity(0.15), radius: 1.5, y: 1)
        .frame(width: 180)
      VStack(alignment: .leading, spacing: 12) {
        Text(
          [current.year, current.kind.rawValue.capitalized].compactMap { $0 }.joined(
            separator: " · ")
        )
        .font(.system(size: 12))
        .foregroundStyle(MyraColor.text2)
        if let metadata = current.metadata {
          Label(
            "TMDB \(metadata.rating, specifier: "%.1f") / 10 • \(metadata.voteCount) votes",
            systemImage: "star.fill"
          )
          .font(.system(size: 12, weight: .semibold))
          .foregroundStyle(.yellow)
          Text(metadata.overview.isEmpty ? "No synopsis available." : metadata.overview)
            .font(.system(size: 13))
            .lineSpacing(3)
            .textSelection(.enabled)
          if !metadata.genres.isEmpty {
            tagLine(metadata.genres)
          }
          VStack(alignment: .leading, spacing: 4) {
            if let release = metadata.releaseDate {
              Text("Released: \(release)")
            }
            Text("Original language: \(metadata.language)")
            Link(
              "View on TMDB",
              destination: URL(
                string:
                  "https://www.themoviedb.org/\(current.kind == .series ? "tv" : "movie")/\(metadata.providerID)"
              )!)
          }
          .font(.system(size: 12))
          .foregroundStyle(MyraColor.text2)
        } else {
          Text(
            "Metadata has not been matched yet. Add a TMDB API key in Settings and update metadata, or correct the filename match below."
          )
          .font(.system(size: 13))
          .foregroundStyle(MyraColor.text2)
        }
        actionRow
      }
      .frame(maxWidth: .infinity, alignment: .leading)
    }
  }

  private var actionRow: some View {
    FlowLayout(spacing: 8) {
      Button(current.resumeVersion == nil ? "Play" : "Resume") { play(current) }
        .buttonStyle(MyraButtonStyle(prominent: true))
      let hasTrailer = current.metadata?.openableTrailerURL != nil
      Button("Trailer") { openTrailer() }
        .buttonStyle(MyraButtonStyle())
        .disabled(!hasTrailer)
        .help(hasTrailer ? "Open the trailer" : "No trailer link is in this title's metadata")
      // A toggle: filled with the accent while the title is saved.
      let saved = store.personal.watchlist.contains(current.id)
      Button {
        store.toggleWatchlist(current.id)
      } label: {
        Label("Watchlist", systemImage: saved ? "bookmark.fill" : "bookmark")
      }
      .buttonStyle(MyraButtonStyle(prominent: saved))
      .help(saved ? "Remove from Watchlist" : "Add to Watchlist")
      .accessibilityLabel(saved ? "Remove from Watchlist" : "Add to Watchlist")
      .accessibilityAddTraits(saved ? .isSelected : [])
      Button(
        store.personal.watched.contains(current.id) ? "Mark unwatched" : "Mark watched"
      ) { store.toggleWatched(current.id) }
        .buttonStyle(MyraButtonStyle())
      if current.kind == .series {
        Button(
          store.personal.followed.contains(current.id) ? "Unfollow series" : "Follow series"
        ) { store.toggleFollow(current.id) }
          .buttonStyle(MyraButtonStyle())
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
        .menuStyle(.borderlessButton)
        .fixedSize()
      }
    }
  }

  private func tagLine(_ genres: [String]) -> some View {
    FlowLayout(spacing: 6) {
      ForEach(genres, id: \.self) { genre in
        Text(genre.uppercased())
          .font(.system(size: 10, weight: .bold))
          .tracking(0.4)
          .foregroundStyle(MyraColor.text2)
          .padding(.horizontal, 6)
          .padding(.vertical, 1)
          .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 4))
      }
    }
  }

  private func aiMenu(_ aiAction: @escaping (MyraAIFeature, EntertainmentTitle) -> Void)
    -> some View
  {
    Menu {
      // The same three contextual actions as the Ask Myra box.
      ForEach(MyraAskAction.actions(forTitle: current)) { action in
        Button(action.title) { aiAction(action.feature, current) }
      }
    } label: {
      Label("Ask Myra", systemImage: "sparkles")
    }
    .menuStyle(.borderlessButton)
    .fixedSize()
  }

  private var correction: some View {
    DisclosureGroup("Correct Match / Grouping") {
      VStack(alignment: .leading, spacing: 10) {
        Text(
          "A shared folder is not proof of identity. Correct a file’s title and year to merge alternate versions, or give an incorrectly grouped file its own identity. TMDB ID is optional."
        )
        .font(.caption).foregroundStyle(MyraColor.text2)
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
        Toggle("Apply to every version in this title", isOn: $applyToAll).myraCheckboxToggleStyle()
        if let correctionError { Text(correctionError).foregroundStyle(.orange) }
        Button("Save correction") { saveCorrection() }
          .buttonStyle(MyraButtonStyle())
          .disabled(correctedTitle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
      }.textFieldStyle(.roundedBorder).padding(.top, 12)
    }
  }

  private var posterCandidates: [ArtworkCandidate] {
    guard let posterURL = current.posterURL else { return [.titleCard] }
    return [.image(.tmdbPoster, posterURL)]
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

  /// Why the best release is picked, and the title's "Always use" choice, shown above the list.
  @ViewBuilder private var bestVersionNote: some View {
    if let choice = BestVersionPicker.choose(
      versions: title.versions, titleQuality: store.personal.titleQualities?[title.id],
      preferredQuality: store.personal.preferences.preferredQuality)
    {
      HStack(spacing: 10) {
        Label(choice.reason, systemImage: "sparkles")
          .font(.callout).foregroundStyle(.secondary)
        Spacer()
        if store.personal.titleQualities?[title.id] != nil {
          Button("Use my default") { store.setTitleQuality(titleID: title.id, quality: nil) }
        }
        let height = BestVersionPicker.height(of: choice.version)
        if height > 0, store.personal.titleQualities?[title.id] != "\(height)p" {
          Button("Always use \(height)p for this title") {
            store.setTitleQuality(titleID: title.id, quality: "\(height)p")
          }
        }
      }
    }
  }

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
      if title.kind == .movie, title.versions.count > 1 {
        bestVersionNote
      }
      SeasonEpisodeList(
        versions: title.versions, isSeries: title.kind == .series,
        currentID: nil, lastPlayedID: lastPlayed, choose: choose)

    }.padding(24).frame(minWidth: 640, idealWidth: 750, minHeight: 340, idealHeight: 520)
  }
}

/// One version as a row of the Home-style `RowCard`, with its play or resume action.
struct EntertainmentVersionRow: View {
  let version: EntertainmentVersion
  let lastPlayed: Bool
  let choose: () -> Void
  var body: some View {
    RowCard(
      rows: [
        EntertainmentVersionRows.item(
          version: version, title: version.media.entry.name, tags: [], lastPlayed: lastPlayed,
          nowPlaying: false, choose: choose)
      ])
  }
}

/// Maps versions to `RowCard` rows. Shared by movies, episodes and the player's episode browser.
enum EntertainmentVersionRows {
  static func item(
    version: EntertainmentVersion, title: String, tags: [String], lastPlayed: Bool,
    nowPlaying: Bool, choose: @escaping () -> Void
  ) -> RowCardItem {
    var meta = [version.quality.isEmpty ? "Quality unknown" : version.quality]
    meta.append(version.media.categoryName)
    if let size = version.media.entry.size {
      meta.append(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
    }
    let hasProgress = version.progressSeconds >= 5
    if hasProgress {
      meta.append("Resume at " + PlayerTime.string(version.progressSeconds))
    }
    var status: StatusPill?
    if nowPlaying {
      status = StatusPill(text: "Now playing", style: .live)
    } else if lastPlayed {
      status = StatusPill(text: "Previously played", style: .neutral)
    }
    var progress: Double?
    if hasProgress, version.duration > 0 {
      progress = min(1, version.progressSeconds / version.duration)
    }
    return RowCardItem(
      id: version.id, title: title, tags: tags, meta: meta, status: status, progress: progress,
      actions: [
        RowCardAction(
          id: "play-\(version.id)", title: hasProgress ? "Resume" : "Play",
          systemImage: "play.fill", style: .button, action: choose)
      ])
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
/// Each season is a section header over a `RowCard` of its episodes.
struct SeasonEpisodeList: View {
  let versions: [EntertainmentVersion]
  let isSeries: Bool
  let currentID: String?
  let lastPlayedID: String?
  let choose: (EntertainmentVersion) -> Void
  @State private var expandedSeasons: Set<String> = []

  var body: some View {
    ScrollView {
      LazyVStack(alignment: .leading, spacing: 16) {
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
              RowCard(rows: season.episodes.flatMap { episodeItems($0) })
                .padding(.top, 8)
            } label: {
              HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(season.label)
                  .font(.system(size: 15, weight: .semibold))
                Text(
                  "\(season.episodes.count) \(season.episodes.count == 1 ? "episode" : "episodes")"
                )
                .font(.system(size: 12))
                .foregroundStyle(MyraColor.text3)
                Spacer(minLength: 8)
              }
              .contentShape(Rectangle())
            }
          }
        } else {
          RowCard(
            rows: versions.map { version in
              EntertainmentVersionRows.item(
                version: version, title: version.media.entry.name, tags: [],
                lastPlayed: version.id == lastPlayedID,
                nowPlaying: version.id == currentID
              ) { choose(version) }
            })
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

  /// One episode's rows: a single version reads as the episode; several versions are listed by file.
  private func episodeItems(_ episode: EntertainmentEpisodeGroup) -> [RowCardItem] {
    let episodeLabel = episode.episode.map { String(format: "Episode %02d", $0) } ?? episode.label
    let grouped = episode.versions.count > 1
    return episode.versions.map { version in
      EntertainmentVersionRows.item(
        version: version,
        title: grouped ? version.media.entry.name : episodeLabel,
        tags: grouped ? [episodeLabel] : [],
        lastPlayed: version.id == lastPlayedID,
        nowPlaying: version.id == currentID
      ) { choose(version) }
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
