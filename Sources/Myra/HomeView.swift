import AppKit
import SwiftUI

/// Discovery is read from the local catalogue; remote enrichment is a separate, bounded operation.
struct HomeView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var store: EntertainmentStore
  @State private var query = ""
  @State private var genre = ""
  @State private var language = ""
  @State private var year = ""
  @State private var source = ""
  @State private var minimumRating = 0.0
  @State private var hideWatched = false
  @State private var details: EntertainmentTitle?
  @State private var choosing: EntertainmentTitle?
  @State private var pendingChoice: EntertainmentTitle?
  @State private var showingCollections = false
  @State private var showingTransfer = false
  @State private var pickPresentation: EntertainmentPickPresentation?
  @State private var showingAI = false
  @State private var aiFeature: MyraAIFeature = .smartPick
  @State private var aiTitle: EntertainmentTitle?
  @State private var pendingAI = false
  @State private var randomPick: EntertainmentTitle?
  @State private var randomPickFilters = "your current filters"
  @State private var expandedShelves: Set<String> = []

  @State private var prepared = HomeCatalogueProjection()
  @State private var preparedFilter: HomeCatalogueFilter?
  private static let projectionWorker = HomeProjectionWorker()
  private var filter: HomeCatalogueFilter {
    .init(
      query: query, genre: genre, language: language, year: year, source: source,
      minimumRating: minimumRating, hideWatched: hideWatched, revision: store.projectionRevision)
  }
  private var filtered: [EntertainmentTitle] { prepared.filtered }
  private var genres: [String] { prepared.genres }
  private var languages: [String] { prepared.languages }
  private var years: [String] { prepared.years }
  private var sources: [String] { prepared.sources }
  private var ratedMovies: [EntertainmentTitle] { prepared.movies }
  private var recommendedSeries: [EntertainmentTitle] { prepared.series }
  private var latest: [EntertainmentTitle] { prepared.latest }
  private var watchlist: [EntertainmentTitle] { prepared.watchlist }

  var body: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 24) {
        filters
        if let message = store.errorMessage {
          HStack {
            Image(systemName: "exclamationmark.triangle")
            Text(message)
            Spacer()
          }
          .font(.callout).foregroundStyle(.orange)
        }
        if store.catalogue.isEmpty {
          ContentUnavailableView(
            "Your entertainment home", systemImage: "film.stack",
            description: Text(
              "Add your directory sources in Library and refresh the index. Indexed videos will appear here."
            )
          )
          .frame(minHeight: 280)
        } else {
          shelf(
            "Continue Watching", subtitle: "Resume unfinished movies and episodes",
            titles: filtered.filter {
              $0.resumeVersion != nil && !store.personal.watched.contains($0.id)
            })
          shelf(
            "Top Rated Movies", subtitle: "TMDB ratings weighted by vote count • minimum 20 votes",
            titles: ratedMovies)
          shelf(
            "Series to Watch", subtitle: "Unwatched shows available in your sources",
            titles: recommendedSeries)
          shelf(
            "Latest Releases", subtitle: "Release dates from matched titles in your index",
            titles: latest)
          shelf(
            "Recently Added", subtitle: "First discovered by your local index",
            titles: prepared.recent)
          shelf("Watchlist", subtitle: "Saved for later", titles: watchlist)
          if !store.newEpisodeIDs.isEmpty {
            shelf(
              "New Episodes", subtitle: "Newly indexed episodes of shows you follow",
              titles: filtered.filter { $0.versions.contains { store.hasNewEpisode($0) } })
          }
          ForEach(store.personal.collections) { collection in
            shelf(
              collection.name, subtitle: "Personal collection",
              titles: filtered.filter { collection.titleIDs.contains($0.id) })
          }
          Text(
            "Availability reflects the latest index, and a source may be offline. Metadata provided by TMDB. Myra uses the TMDB API but is not endorsed or certified by TMDB."
          )
          .font(.caption).foregroundStyle(.secondary)
          Link(
            "TMDB attribution and information",
            destination: URL(string: "https://www.themoviedb.org")!
          ).font(.caption)
        }
      }.padding(.horizontal, 28).padding(.bottom, 28)
    }
    .contentMargins(.top, 36, for: .scrollContent)
    .mediaHeaderScrollEffect()
    .task(id: filter) {
      let request = filter
      let catalogue = store.catalogue
      let personal = store.personal
      do {
        try await Task.sleep(for: .milliseconds(100))
        let projection = try await Self.projectionWorker.prepare(
          catalogue, personal: personal, filter: request)
        try Task.checkCancellation()
        prepared = projection
        preparedFilter = request
      } catch is CancellationError {} catch {}
    }
    .sheet(item: $details, onDismiss: presentPendingChoice) { title in
      EntertainmentDetailView(
        title: title, store: store, play: { start($0) },
        aiAction: { feature, title in
          aiFeature = feature
          aiTitle = title
          pendingAI = true
          details = nil
        })
    }
    .sheet(item: $pickPresentation, onDismiss: presentPendingChoice) { pick in
      EntertainmentPickView(
        title: pick.title, store: store, filters: pick.filters,
        anotherPick: { pickSomething() }, resetFilters: { resetFilters() }, play: { start($0) })
    }
    .sheet(item: $choosing) { title in
      EntertainmentVersionChooser(title: title, store: store) { version in
        choosing = nil
        details = nil
        if version.progressSeconds >= 5 {
          coordinator.resumeVersion(version)
        } else {
          coordinator.playChosenVersion(version.media)
        }
      }
    }
    .sheet(isPresented: $showingAI, onDismiss: presentPendingChoice) {
      MyraAIWorkspaceView(store: store, initialFeature: aiFeature, initialTitle: aiTitle) {
        start($0)
      }
    }
    .sheet(isPresented: $showingCollections) { EntertainmentCollectionsView(store: store) }
    .sheet(isPresented: $showingTransfer) {
      EntertainmentTransferView(coordinator: coordinator, store: store)
    }
  }

  private var filters: some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack(spacing: 12) {
        HStack(spacing: 8) {
          Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
          TextField("Find a movie or series", text: $query).textFieldStyle(.plain)
            .accessibilityLabel("Search movies and series")
          if !query.isEmpty {
            Button {
              query = ""
            } label: {
              Image(systemName: "xmark.circle.fill")
            }
            .buttonStyle(.plain).accessibilityLabel("Clear search")
          }
        }
        .padding(.horizontal, 12).frame(minWidth: 180, minHeight: 38)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.secondary.opacity(0.2)))
        ViewThatFits(in: .horizontal) {
          homeActions(compact: false)
          homeActions(compact: true)
        }
      }
      ScrollView(.horizontal) {
        HStack(spacing: 12) {
          filterPicker("Genre", selection: $genre, values: genres)
          filterPicker("Language", selection: $language, values: languages)
          filterPicker("Year", selection: $year, values: years)
          filterPicker("Source", selection: $source, values: sources)
          Picker("Rating", selection: $minimumRating) {
            Text("Any rating").tag(0.0)
            ForEach([5.0, 6, 7, 8, 9], id: \.self) { Text("\(Int($0))+ / 10").tag($0) }
          }.labelsHidden().fixedSize()
          Toggle("Hide watched", isOn: $hideWatched).toggleStyle(.checkbox).fixedSize()
          Button("Reset") { resetFilters() }
        }.padding(.vertical, 2)
      }.scrollIndicators(.hidden)
    }
  }

  private func homeActions(compact: Bool) -> some View {
    HStack(spacing: 8) {
      Button {
        pickSomething()
      } label: {
        actionLabel("Pick something", icon: "dice", compact: compact)
      }
      .help("Pick something to watch")
      .disabled(preparedFilter != filter || store.catalogue.isEmpty)
      Menu {
        Button("Help Me Choose") { openAI(.smartPick) }
        Button("Library Assistant") { openAI(.assistant) }
        Button("Search in Natural Language") { openAI(.naturalSearch) }
        Button("Plan Tonight") { openAI(.plan) }
        Button("Viewing Insights") { openAI(.insights) }
      } label: {
        actionLabel("Myra AI", icon: "sparkles", compact: compact)
      }
      Button {
        Task { await store.enrich() }
      } label: {
        actionLabel(
          store.isEnriching ? "Updating…" : "Update metadata", icon: "sparkles", compact: compact)
      }.help("Update metadata").disabled(store.isEnriching || store.catalogue.isEmpty)
      Menu {
        Button("Manage collections") { showingCollections = true }
        Button("Export / Import") { showingTransfer = true }
      } label: {
        actionLabel("Personal Library", icon: "person.crop.square", compact: compact)
      }
      .help("Personal Library")
    }
  }

  @ViewBuilder private func actionLabel(_ title: String, icon: String, compact: Bool) -> some View {
    if compact {
      Image(systemName: icon).accessibilityLabel(title)
    } else {
      Label(title, systemImage: icon)
    }
  }

  private func filterPicker(_ label: String, selection: Binding<String>, values: [String])
    -> some View
  {
    Picker(label, selection: selection) {
      Text("All \(label.lowercased())s").tag("")
      ForEach(values, id: \.self) { Text($0).tag($0) }
    }.labelsHidden().frame(width: label == "Language" ? 125 : 110)
  }

  private func shelf(_ name: String, subtitle: String, titles: [EntertainmentTitle]) -> some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack {
        Text(name).font(.title2.bold())
        Text("\(titles.count)").foregroundStyle(.secondary)
        Spacer()
        if titles.count > 60 {
          Button(expandedShelves.contains(name) ? "Show fewer" : "Show all") {
            if !expandedShelves.insert(name).inserted { expandedShelves.remove(name) }
          }.font(.caption)
        }
      }
      Text(subtitle).font(.caption).foregroundStyle(.secondary)
      if titles.isEmpty {
        Text(
          name == "Top Rated Movies" || name == "Latest Releases"
            ? "No matched titles for these filters. Add a TMDB key in Settings and update metadata."
            : "No titles here yet. Try adjusting your filters."
        )
        .foregroundStyle(.secondary).padding(.vertical, 12)
      } else {
        ScrollView(.horizontal) {
          LazyHStack(alignment: .top, spacing: 16) {
            ForEach(expandedShelves.contains(name) ? titles : Array(titles.prefix(60))) { title in
              EntertainmentCard(
                title: title, store: store,
                play: { start(title) }, details: { details = title })
            }
          }.padding(.bottom, 6)
        }
      }
    }
  }

  private func start(_ title: EntertainmentTitle, resume: Bool = true) {
    if resume, let version = title.resumeVersion {
      details = nil
      pickPresentation = nil
      showingAI = false
      coordinator.resumeVersion(version)
      return
    }
    guard !title.versions.isEmpty else { return }
    if title.versions.count == 1, let version = title.versions.first {
      details = nil
      pickPresentation = nil
      showingAI = false
      coordinator.playChosenVersion(version.media)
    } else if details != nil || pickPresentation != nil || showingAI {
      pendingChoice = title
      details = nil
      pickPresentation = nil
      showingAI = false
    } else {
      choosing = title
    }
  }

  private func weightedRating(_ title: EntertainmentTitle) -> Double {
    guard let metadata = title.metadata else { return 0 }
    let votes = Double(metadata.voteCount)
    return (votes * metadata.rating + 50 * 6.5) / (votes + 50)
  }

  private func openAI(_ feature: MyraAIFeature) {
    aiFeature = feature
    aiTitle = nil
    showingAI = true
  }

  private func presentPendingChoice() {
    if pendingAI {
      pendingAI = false
      showingAI = true
      return
    }
    if let pending = pendingChoice {
      pendingChoice = nil
      choosing = pending
    }
  }

  private func resetFilters() {
    query = ""
    genre = ""
    language = ""
    year = ""
    source = ""
    minimumRating = 0
    hideWatched = false
  }

  private func pickSomething() {
    guard preparedFilter == filter else { return }
    randomPick = EntertainmentPick.select(
      from: filtered, watched: store.personal.watched, excluding: randomPick?.id)
    randomPickFilters = filterDescription
    pickPresentation = .init(
      id: pickPresentation?.id ?? UUID(), title: randomPick, filters: randomPickFilters)
  }

  private var filterDescription: String {
    var terms = [String]()
    if !query.isEmpty { terms.append("search ‘\(query)’") }
    if !genre.isEmpty { terms.append(genre) }
    if !language.isEmpty { terms.append(language) }
    if !year.isEmpty { terms.append(year) }
    if !source.isEmpty { terms.append(source) }
    if minimumRating > 0 { terms.append("rating \(Int(minimumRating))+") }
    return terms.isEmpty ? "your current filters (all sources)" : terms.joined(separator: ", ")
  }
}

private struct EntertainmentCard: View {
  let title: EntertainmentTitle
  @ObservedObject var store: EntertainmentStore
  let play: () -> Void
  let details: () -> Void

  var body: some View {
    VStack(alignment: .leading, spacing: 8) {
      Button(action: details) {
        ZStack(alignment: .bottomLeading) {
          AsyncImage(url: title.posterURL) { image in
            image.resizable().scaledToFill()
          } placeholder: {
            ZStack {
              Rectangle().fill(.quaternary)
              Image(systemName: title.kind == .series ? "tv" : "film").font(.largeTitle)
                .foregroundStyle(.secondary)
            }
          }
          .frame(width: 170, height: 235).clipped()
          if store.personal.watched.contains(title.id) {
            Label("Watched", systemImage: "checkmark.circle.fill")
              .font(.caption.bold()).padding(6).background(.regularMaterial)
          }
        }.clipShape(RoundedRectangle(cornerRadius: 12))
      }.buttonStyle(.plain)
      Text(title.displayName).font(.headline).lineLimit(2).frame(height: 38, alignment: .topLeading)
      HStack {
        Text(title.year ?? title.kind.rawValue.capitalized)
        Spacer()
        if let metadata = title.metadata {
          Label(String(format: "%.1f", metadata.rating), systemImage: "star.fill").foregroundStyle(
            .yellow
          )
          .help("TMDB rating from \(metadata.voteCount) votes")
        }
      }.font(.caption).foregroundStyle(.secondary)
      Text(
        Array(Set(title.versions.map { $0.media.categoryName })).sorted().joined(separator: ", ")
      )
      .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
      if let resume = title.resumeVersion, resume.duration > 0 {
        ProgressView(value: min(1, resume.progressSeconds / resume.duration))
        Text("Resume at \(Int(resume.progressSeconds / 60)) min").font(.caption2).foregroundStyle(
          .secondary)
      }
      HStack {
        Button(action: play) {
          Label(title.resumeVersion == nil ? "Play" : "Resume", systemImage: "play.fill")
        }
        .buttonStyle(.borderedProminent)
        Button {
          store.toggleWatchlist(title.id)
        } label: {
          Image(
            systemName: store.personal.watchlist.contains(title.id) ? "bookmark.fill" : "bookmark")
        }.help("Toggle Watchlist")
        Menu {
          Button(store.personal.watched.contains(title.id) ? "Mark unwatched" : "Mark watched") {
            store.toggleWatched(title.id)
          }
          Button("Details and versions", action: details)
          if title.kind == .series {
            Button(store.personal.followed.contains(title.id) ? "Unfollow series" : "Follow series")
            { store.toggleFollow(title.id) }
          }
          if !store.personal.collections.isEmpty {
            Menu("Collections") {
              ForEach(store.personal.collections) { collection in
                Button("\(collection.titleIDs.contains(title.id) ? "✓ " : "")\(collection.name)") {
                  store.toggleCollection(titleID: title.id, collectionID: collection.id)
                }
              }
            }
          }
        } label: {
          Image(systemName: "ellipsis")
        }.menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 22)
      }
    }.frame(width: 170)
  }
}

struct HomeCatalogueFilter: Hashable, Sendable {
  var query: String
  var genre: String
  var language: String
  var year: String
  var source: String
  var minimumRating: Double
  var hideWatched: Bool
  var revision: Int
}

struct HomeCatalogueProjection: Sendable {
  var filtered: [EntertainmentTitle] = []
  var movies: [EntertainmentTitle] = []
  var series: [EntertainmentTitle] = []
  var latest: [EntertainmentTitle] = []
  var recent: [EntertainmentTitle] = []
  var watchlist: [EntertainmentTitle] = []
  var genres: [String] = []
  var languages: [String] = []
  var years: [String] = []
  var sources: [String] = []
}

actor HomeProjectionWorker {
  func prepare(
    _ catalogue: [EntertainmentTitle], personal: EntertainmentPersonalData,
    filter: HomeCatalogueFilter
  ) throws -> HomeCatalogueProjection {
    var result = HomeCatalogueProjection()
    var genres: Set<String> = []
    var languages: Set<String> = []
    var years: Set<String> = []
    var sources: Set<String> = []
    for title in catalogue {
      try Task.checkCancellation()
      genres.formUnion(title.metadata?.genres ?? [])
      if let language = title.metadata?.language { languages.insert(language) }
      if let year = title.year { years.insert(year) }
      for version in title.versions { sources.insert(version.media.categoryName) }
      guard
        filter.query.isEmpty || title.displayName.localizedCaseInsensitiveContains(filter.query),
        filter.genre.isEmpty || title.metadata?.genres.contains(filter.genre) == true,
        filter.language.isEmpty || title.metadata?.language == filter.language,
        filter.year.isEmpty || title.year == filter.year
          || title.metadata?.releaseDate?.hasPrefix(filter.year) == true,
        filter.source.isEmpty
          || title.versions.contains(where: { $0.media.categoryName == filter.source }),
        filter.minimumRating == 0 || (title.metadata?.rating ?? 0) >= filter.minimumRating,
        !filter.hideWatched || !personal.watched.contains(title.id)
      else { continue }
      result.filtered.append(title)
      if title.kind == .movie && (title.metadata?.voteCount ?? 0) >= 20 {
        result.movies.append(title)
      }
      if title.kind == .series && !personal.watched.contains(title.id) {
        result.series.append(title)
      }
      if title.latestReleaseDate != nil { result.latest.append(title) }
      if personal.watchlist.contains(title.id) { result.watchlist.append(title) }
    }
    func score(_ title: EntertainmentTitle) -> Double {
      guard let metadata = title.metadata else { return 0 }
      let votes = Double(metadata.voteCount)
      return (votes * metadata.rating + 50 * 6.5) / (votes + 50)
    }
    result.movies.sort { score($0) > score($1) }
    result.series.sort { score($0) > score($1) }
    result.latest.sort { ($0.latestReleaseDate ?? "") > ($1.latestReleaseDate ?? "") }
    result.recent = result.filtered.sorted { $0.latestDiscovered > $1.latestDiscovered }
    result.genres = genres.sorted()
    result.languages = languages.sorted()
    result.years = years.sorted(by: >)
    result.sources = sources.sorted()
    return result
  }
}

/// The sheet receives the chosen title atomically, including on its first presentation.
struct EntertainmentPickPresentation: Identifiable {
  let id: UUID
  let title: EntertainmentTitle?
  let filters: String
}
