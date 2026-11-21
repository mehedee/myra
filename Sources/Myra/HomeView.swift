#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Measurements taken from the approved Home section of the redesign preview.
enum HomeLayout {
  /// `.content` side padding.
  static let pageInset: CGFloat = 26
  /// `.hero` height and corner radius.
  static let heroHeight: CGFloat = 290
  static let heroRadius: CGFloat = 18
  /// `.shelf` column width for posters, and `.shelf.wide` for Up Next stills.
  static let posterWidth: CGFloat = 148
  static let stillWidth: CGFloat = 260
  /// Gap between shelf columns.
  static let shelfSpacing: CGFloat = 16
  /// Left inset so the first Up Next card is not clipped by the scroll edge.
  static let upNextLeadingInset: CGFloat = 16
  /// A shelf previews this many cards; "See All" opens the whole category as a grid.
  static let shelfPreviewLimit = 60

  static func visibleCount(total: Int) -> Int {
    min(total, shelfPreviewLimit)
  }

  static let seeAllTitle = "See All"

  static func backdropCandidates(_ title: EntertainmentTitle) -> [ArtworkCandidate] {
    var candidates: [ArtworkCandidate] = []
    if let backdrop = title.metadata?.backdropURL {
      candidates.append(.image(.tmdbBackdrop, backdrop))
    }
    if let poster = title.posterURL {
      candidates.append(.image(.tmdbPoster, poster))
    }
    return candidates.isEmpty ? [.titleCard] : candidates
  }

  static func posterCandidates(_ title: EntertainmentTitle) -> [ArtworkCandidate] {
    guard let poster = title.posterURL else { return [.titleCard] }
    return [.image(.tmdbPoster, poster)]
  }
}

/// Discovery is read from the local catalogue; remote enrichment is a separate, bounded operation.
struct HomeView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var store: EntertainmentStore
  /// Shared with the shell's toolbar search field.
  private var query: String { coordinator.searchText }
  /// The pinned filter bar's state, shared with the See All pages.
  @ObservedObject var filters: LibraryFilterModel
  /// Opens a shelf as a See All grid, or the Watchlist page. Set by the shell.
  var openSeeAll: (SeeAllRequest) -> Void = { _ in }
  var openWatchlist: () -> Void = {}
  @State private var details: EntertainmentTitle?
  @State private var choosing: EntertainmentTitle?
  @State private var pendingChoice: EntertainmentTitle?
  @State private var showingCollections = false
  @State private var confirmingClearContinueWatching = false
  @State private var showingTransfer = false
  @State private var pickPresentation: EntertainmentPickPresentation?
  @State private var showingAI = false
  @State private var aiFeature: MyraAIFeature = .smartPick
  @State private var aiTitle: EntertainmentTitle?
  @State private var pendingAI = false
  @State private var randomPick: EntertainmentTitle?
  @State private var randomPickFilters = "your current filters"
  @State private var mood: MyraMood?
  @State private var tonight: [MyraRecommendation] = []
  @State private var tonightIndex = 0
  @State private var tonightHeadline = ""
  @State private var showingWhy = false
  @State private var aiPrompt = ""
  @State private var showingCustomise = false
  @FocusState private var searchFocused: Bool
  @Environment(\.myraAccent) private var accent

  @State private var prepared: HomeCatalogueProjection
  @State private var preparedFilter: HomeCatalogueFilter?
  private static let projectionWorker = HomeProjectionWorker.shared
  private var filter: HomeCatalogueFilter {
    .init(query: query, selection: filters.selection, revision: store.projectionRevision)
  }
  private var filtered: [EntertainmentTitle] { prepared.filtered }
  private var ratedMovies: [EntertainmentTitle] { prepared.movies }
  private var recommendedSeries: [EntertainmentTitle] { prepared.series }
  private var latest: [EntertainmentTitle] { prepared.latest }
  private var watchlist: [EntertainmentTitle] { prepared.watchlist }
  private var automaticShelves: [HomeAutomaticShelf] { prepared.shelves }

  init(
    coordinator: AppCoordinator, store: EntertainmentStore, initialMood: MyraMood? = nil,
    filters: LibraryFilterModel = LibraryFilterModel(),
    openSeeAll: @escaping (SeeAllRequest) -> Void = { _ in },
    openWatchlist: @escaping () -> Void = {}
  ) {
    self.coordinator = coordinator
    self.store = store
    self.filters = filters
    self.openSeeAll = openSeeAll
    self.openWatchlist = openWatchlist
    _mood = State(initialValue: initialMood)
    // Returning to Home draws the kept projection at once instead of an empty page.
    _prepared = State(initialValue: store.preparedProjection)
  }

  var body: some View {
    VStack(spacing: 0) {
      filterBar
      pageScroll
    }
  }

  /// The pinned filter bar (`.fbar`). Its trailing menu keeps the page's less common actions.
  private var filterBar: some View {
    LibraryFilterBar(
      model: filters, options: prepared.options, shown: filtered.count,
      total: store.catalogue.count
    ) {
      Menu {
        Button("Ask Myra…") { askMyra() }
        Divider()
        Button(store.isEnriching ? "Updating Metadata…" : "Update Metadata") {
          Task { await store.enrich() }
        }
        .disabled(store.isEnriching || store.catalogue.isEmpty)
        Button("Manage Collections…") { showingCollections = true }
        Button("Export / Import…") { showingTransfer = true }
      } label: {
        Image(systemName: "ellipsis.circle")
      }
      .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
      .help("More actions")
      .accessibilityLabel("More actions")
    }
  }

  private var pageScroll: some View {
    ScrollView {
      ScrollViewReader { proxy in
        VStack(alignment: .leading, spacing: 0) {
          if HomeAskMyra.isQuestion(query) { askMyraRow.padding(.top, 26) }
          if let message = store.errorMessage {
            HStack {
              Image(systemName: "exclamationmark.triangle")
              Text(message)
              Spacer()
            }
            .font(.callout).foregroundStyle(.orange)
            .padding(.top, 26)
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
            if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && filtered.isEmpty
              && preparedFilter == filter
            {
              ContentUnavailableView.search(text: query)
            }
            if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
              tonightSection
            }
            upNextRow(
              filtered.filter { $0.resumeVersion != nil && !store.personal.watched.contains($0.id) }
                .sorted {
                  ($0.resumeVersion?.lastPlayed ?? .distantPast)
                    > ($1.resumeVersion?.lastPlayed ?? .distantPast)
                })
            HomeBrowseByRow(
              shelves: automaticShelves,
              scrollTo: { id in withAnimation { proxy.scrollTo("shelf-\(id)", anchor: .top) } },
              customise: { showingCustomise = true }
            )
            .padding(.top, 26)
            shelf(
              "Watchlist", subtitle: "Saved to watch later", titles: watchlist,
              seeAll: openWatchlist)
            ForEach(automaticShelves) { shelf in
              self.shelf(
                shelf.name, subtitle: shelf.subtitle, titles: shelf.titles,
                anchor: "shelf-\(shelf.id)",
                seeAll: { openSeeAll(.init(name: shelf.name, source: .automatic(shelf.id))) })
            }
            shelf(
              "Top Rated Movies", subtitle: "TMDB ratings weighted by vote count • minimum 20 votes",
              titles: ratedMovies, seeAll: { openSeeAll(.init(name: "Top Rated Movies", source: .topRated)) })
            shelf(
              "Recently Added", subtitle: "First discovered by your local index",
              titles: prepared.recent,
              seeAll: { openSeeAll(.init(name: "Recently Added", source: .recent)) })
            shelf(
              "Series to Watch", subtitle: "Unwatched shows available in your sources",
              titles: recommendedSeries,
              seeAll: { openSeeAll(.init(name: "Series to Watch", source: .series)) })
            shelf(
              "Latest Releases", subtitle: "Release dates from matched titles in your index",
              titles: latest,
              seeAll: { openSeeAll(.init(name: "Latest Releases", source: .latest)) })
            if !store.newEpisodeIDs.isEmpty {
              shelf(
                "New Episodes", subtitle: "Newly indexed episodes of shows you follow",
                titles: filtered.filter { $0.versions.contains { store.hasNewEpisode($0) } },
                seeAll: { openSeeAll(.init(name: "New Episodes", source: .newEpisodes)) })
            }
            ForEach(store.personal.collections) { collection in
              shelf(
                collection.name, subtitle: "Personal collection",
                titles: filtered.filter { collection.titleIDs.contains($0.id) },
                seeAll: { openSeeAll(.init(name: collection.name, source: .collection(collection.id))) })
            }
            // Smart collections pinned in Collections. They update with the library.
            ForEach(store.smartCollections.filter(\.pinnedToHome)) { collection in
              shelf(
                collection.name,
                subtitle: "Smart collection · \(SmartCollectionRules.summary(collection.rules))",
                titles: SmartCollectionRules.titles(
                  matching: collection.rules, in: filtered, watched: store.personal.watched))
            }
            Text(
              "Availability reflects the latest index, and a source may be offline. Metadata provided by TMDB. Myra uses the TMDB API but is not endorsed or certified by TMDB."
            )
            .font(.caption).foregroundStyle(.secondary)
            .padding(.top, 30)
            Link(
              "TMDB attribution and information",
              destination: URL(string: "https://www.themoviedb.org")!
            ).font(.caption)
          }
        }
        .padding(.horizontal, HomeLayout.pageInset)
        .padding(.bottom, 30)
      }
    }
    .contentMargins(.top, 4, for: .scrollContent)
    .modifier(SearchFocusIfAvailable(isFocused: $searchFocused))
    .onReceive(NotificationCenter.default.publisher(for: MyraCommand.notification)) { note in
      // The shell's page actions post these commands; Home runs its own Pick and Ask actions.
      guard let command = MyraCommand(notification: note) else { return }
      switch command {
      case .find: searchFocused = true
      case .pickSomething: pickSomething()
      case .askMyra: askMyra()
      default: break
      }
    }
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
    .task(
      id: TonightRequest(
        mood: mood, filter: preparedFilter, personalRevision: store.personalChangeRevision)
    ) {
      guard preparedFilter != nil else { return }
      let context = tonightContext()
      let titles = filtered
      let personal = store.personal
      let results = await Task.detached(priority: .userInitiated) {
        MyraRecommender.recommend(titles: titles, personal: personal, context: context, limit: 9)
      }.value
      guard !Task.isCancelled else { return }
      tonight = results
      tonightIndex = 0
      tonightHeadline = MyraRecommender.headline(for: context)
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
      MyraAIWorkspaceView(
        store: store, initialFeature: aiFeature, initialTitle: aiTitle, initialPrompt: aiPrompt
      ) {
        start($0)
      }
    }
    .sheet(isPresented: $showingCollections) { EntertainmentCollectionsView(store: store) }
    .sheet(isPresented: $showingCustomise) { CustomiseHomeSheet(store: store) }
    .sheet(isPresented: $showingTransfer) {
      EntertainmentTransferView(coordinator: coordinator, store: store)
    }
    .confirmationDialog(
      "Clear Up Next?", isPresented: $confirmingClearContinueWatching,
      titleVisibility: .visible
    ) {
      Button("Clear Up Next", role: .destructive) {
        Task { await store.clearContinueWatching() }
      }
      Button("Cancel", role: .cancel) {}
    } message: {
      Text(
        "Remove unfinished resume entries from all sources? Your files, watched status, watchlist and collections will not be deleted. Current playback will continue."
      )
    }
  }

  private var askMyraRow: some View {
    Button {
      askMyra()
    } label: {
      HStack(spacing: 10) {
        Image(systemName: "sparkles").foregroundStyle(.tint)
        Text("Ask Myra: “\(query)”").lineLimit(1)
        Spacer()
        Text("Opens Myra AI · nothing is sent until you ask").font(.caption)
          .foregroundStyle(.secondary)
      }
      .padding(.horizontal, 14).padding(.vertical, 10)
      .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
      .contentShape(Rectangle())
    }
    .buttonStyle(.plain)
  }

  /// Up Next: wide 16:9 stills with a 16pt inset at the leading edge.
  private func upNextRow(_ titles: [EntertainmentTitle]) -> some View {
    VStack(alignment: .leading, spacing: 0) {
      if !titles.isEmpty {
        SectionHeader(title: "Up Next", count: titles.count)
        HStack(alignment: .firstTextBaseline, spacing: 8) {
          Text("Resume unfinished movies and the next episode of shows you follow")
            .font(.system(size: 12)).foregroundStyle(MyraColor.text2)
          Spacer(minLength: 8)
          Menu {
            Button("Clear Up Next…", role: .destructive) { confirmingClearContinueWatching = true }
              .disabled(store.isClearingContinueWatching)
          } label: {
            Image(systemName: "ellipsis.circle")
          }
          .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
          .accessibilityLabel("Up Next options")
        }
        .padding(.bottom, 12)
        ScrollView(.horizontal) {
          LazyHStack(alignment: .top, spacing: HomeLayout.shelfSpacing) {
            ForEach(titles.prefix(20)) { title in
              HomeStillCard(title: title, store: store, play: { start(title) }) {
                details = title
              }
              .frame(width: HomeLayout.stillWidth)
            }
          }
          .padding(.leading, HomeLayout.upNextLeadingInset)
          .padding(.bottom, 6)
        }
        .scrollIndicators(.hidden)
      }
    }
  }

  /// A titled shelf of posters with its count and a "See All" control. Empty shelves are hidden.
  @ViewBuilder
  private func shelf(
    _ name: String, subtitle: String, titles: [EntertainmentTitle], anchor: String? = nil,
    seeAll: @escaping () -> Void = {}
  ) -> some View {
    if !titles.isEmpty {
      VStack(alignment: .leading, spacing: 0) {
        // The shelf previews the first cards; See All opens the whole category as a grid.
        SectionHeader(
          title: name, count: titles.count, actionTitle: HomeLayout.seeAllTitle, action: seeAll)
        Text(subtitle)
          .font(.system(size: 12)).foregroundStyle(MyraColor.text2)
          .padding(.bottom, 4)
        // The hover ring sits outside the poster, so the strip is padded by the ring's room and
        // pulled back by the same amount; the ring is never cut at the shelf's edges.
        ScrollView(.horizontal) {
          LazyHStack(alignment: .top, spacing: HomeLayout.shelfSpacing) {
            ForEach(titles.prefix(HomeLayout.visibleCount(total: titles.count))) { title in
              HomePosterCard(
                title: title, store: store,
                play: { start(title) }, details: { details = title })
                .frame(width: HomeLayout.posterWidth, alignment: .topLeading)
            }
          }
          .padding(TitleGridLayout.ringInset)
        }
        .scrollIndicators(.hidden)
        .padding(-TitleGridLayout.ringInset)
      }
      .id(anchor ?? name)
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
    aiPrompt = ""
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

  private func askMyra() {
    aiFeature = .assistant
    aiTitle = nil
    aiPrompt = query
    showingAI = true
  }

  private func resetFilters() {
    coordinator.searchText = ""
    filters.clear()
  }

  private func tonightContext(now: Date = .now) -> MyraViewingContext {
    let feedback = store.personal.recommendationFeedback ?? .init()
    // Rotating the seed daily keeps picks stable through an evening but fresh tomorrow.
    let day = UInt64(Calendar.current.ordinality(of: .day, in: .era, for: now) ?? 0)
    return MyraViewingContext(
      date: now, mood: mood, liked: feedback.liked, disliked: feedback.disliked,
      dismissed: feedback.activeDismissals(at: now), seed: day)
  }

  private var tonightPick: MyraRecommendation? {
    tonight.isEmpty ? nil : tonight[tonightIndex % tonight.count]
  }

  @ViewBuilder private var tonightSection: some View {
    if let pick = tonightPick { tonightBanner(pick) }
    moodRow.padding(.top, 18)
    if let mood {
      shelf(
        "\(mood.title) picks", subtitle: "Ranked on this \(MyraDevice.name) from your mood, time and history",
        titles: tonight.filter { $0.id != tonightPick?.id }.map(\.title)
      )
    }
  }

  /// The `.hero` banner: blurred artwork under a dark gradient, clipped to its own frame.
  private func tonightBanner(_ pick: MyraRecommendation) -> some View {
    let title = pick.title
    var facts = [title.year ?? title.kind.rawValue.capitalized]
    facts += (title.metadata?.genres ?? []).prefix(2)
    if let minutes = MyraAIContext.runtime(title) {
      facts.append(MyraRecommender.duration(minutes))
    }
    if let rating = title.metadata?.rating, rating > 0 {
      facts.append(String(format: "★ %.1f", rating))
    }
    return ZStack(alignment: .bottomLeading) {
      // The black base and the clip confine the blurred artwork to the banner frame.
      Color.black
        .overlay {
          FallbackArtworkView(
            title: title.displayName, candidates: HomeLayout.backdropCandidates(title)
          )
          .blur(radius: 30)
          .opacity(0.7)
        }
        .clipped()
      LinearGradient(
        stops: [
          .init(color: .black.opacity(0.55), location: 0),
          .init(color: .black.opacity(0.1), location: 0.65),
        ],
        startPoint: .leading, endPoint: .trailing)
      VStack(alignment: .leading, spacing: 0) {
        Text("Tonight's pick · \(tonightHeadline)")
          .font(.system(size: 11, weight: .semibold))
          .tracking(0.88)
          .textCase(.uppercase)
          .foregroundStyle(.white.opacity(0.85))
        Text(title.displayName)
          .font(.system(size: 32, weight: .bold))
          .tracking(-0.64)
          .lineLimit(2)
          .padding(.top, 6).padding(.bottom, 4)
        Text(facts.joined(separator: " · "))
          .font(.system(size: 12))
          .foregroundStyle(.white.opacity(0.9))
          .padding(.bottom, 14)
        Label(pick.reasons.prefix(2).joined(separator: " · "), systemImage: "sparkles")
          .font(.system(size: 13))
          .foregroundStyle(.white.opacity(0.9))
          .padding(.bottom, 16)
        HStack(spacing: 8) {
          Button {
            start(title)
          } label: {
            Label(title.resumeVersion == nil ? "Play" : "Resume", systemImage: "play.fill")
          }
          .buttonStyle(HeroButtonStyle(prominent: true))
          // The preview's buttons. Another and Less Like This are in the banner's context menu.
          Button {
            store.react(.like, to: title.id)
          } label: {
            Label(
              "More Like This",
              systemImage: store.personal.recommendationFeedback?.liked.contains(title.id) == true
                ? "hand.thumbsup.fill" : "hand.thumbsup")
          }
          .buttonStyle(HeroButtonStyle())
          Button {
            store.react(.notTonight, to: title.id)
          } label: {
            Label("Not Tonight", systemImage: "moon.zzz")
          }
          .buttonStyle(HeroButtonStyle())
          Button("Why?") { showingWhy = true }
            .buttonStyle(HeroButtonStyle())
            .popover(isPresented: $showingWhy, arrowEdge: .bottom) { TonightWhyView(pick: pick) }
        }
      }
      .contextMenu {
        Button("Another Pick") { tonightIndex += 1 }
          .disabled(tonight.count < 2)
        Divider()
        RecommendationFeedbackItems(titleID: title.id, store: store)
      }
      .foregroundStyle(.white)
      .padding(.leading, 28).padding(.trailing, 28).padding(.bottom, 26)
      .frame(maxWidth: .infinity, alignment: .leading)
    }
    .frame(maxWidth: .infinity)
    .frame(height: HomeLayout.heroHeight)
    .clipShape(RoundedRectangle(cornerRadius: HomeLayout.heroRadius))
    .environment(\.colorScheme, .dark)
  }

  /// `.moods`: the label, then the mood chips on one wrapping row. The selected chip uses the accent.
  private var moodRow: some View {
    HStack(alignment: .center, spacing: 10) {
      Text("In the mood for")
        .font(.system(size: 13)).foregroundStyle(MyraColor.text2)
      FlowLayout(spacing: 6) {
        ForEach(MyraMood.allCases) { item in
          let selected = mood == item
          Button {
            mood = selected ? nil : item
          } label: {
            HomeChip(title: item.title, isOn: selected)
          }
          .buttonStyle(.plain)
          .accessibilityAddTraits(selected ? .isSelected : [])
        }
      }
    }
  }

  private func pickSomething() {
    guard preparedFilter == filter, !store.catalogue.isEmpty else { return }
    randomPick = EntertainmentPick.select(
      from: filtered, watched: store.personal.watched, excluding: randomPick?.id)
    randomPickFilters = filterDescription
    pickPresentation = .init(
      id: pickPresentation?.id ?? UUID(), title: randomPick, filters: randomPickFilters)
  }

  private var filterDescription: String {
    var terms = [String]()
    if !query.isEmpty { terms.append("search ‘\(query)’") }
    let selection = filters.selection
    if selection.kind != .all { terms.append(selection.kind.title.lowercased()) }
    for facet in LibraryFacet.allCases {
      terms.append(contentsOf: selection.values(for: facet).sorted())
    }
    if selection.hideWatched { terms.append("unwatched only") }
    return terms.isEmpty ? "your current filters (all sources)" : terms.joined(separator: ", ")
  }
}

/// A poster card (`.card`): a click opens details, and the context menu holds the title actions.
struct HomePosterCard: View {
  let title: EntertainmentTitle
  @ObservedObject var store: EntertainmentStore
  let play: () -> Void
  let details: () -> Void

  private var subtitle: String {
    [title.year ?? title.kind.rawValue.capitalized, title.metadata?.genres.first]
      .compactMap { $0 }
      .joined(separator: " · ")
  }

  var body: some View {
    // The card opens details on a click; the bookmark inside it is its own button.
    MediaCard(
      title: title.displayName, subtitle: subtitle,
      posterURL: title.posterURL,
      rating: (title.metadata?.rating ?? 0) > 0 ? title.metadata?.rating : nil,
      watched: store.personal.watched.contains(title.id),
      isSaved: store.personal.watchlist.contains(title.id),
      toggleSaved: { store.toggleWatchlist(title.id) }
    )
    .contentShape(Rectangle())
    .onTapGesture(perform: details)
    .accessibilityElement(children: .contain)
    .accessibilityLabel("\(title.displayName) details")
    .accessibilityAddTraits(.isButton)
    .accessibilityAction(named: "Open details", details)
    .contextMenu { menuItems }
  }

  @ViewBuilder private var menuItems: some View {
    Button(title.resumeVersion == nil ? "Play" : "Resume", action: play)
    Button("Details and Versions", action: details)
    Button(
      store.personal.watchlist.contains(title.id) ? "Remove from Watchlist" : "Add to Watchlist"
    ) { store.toggleWatchlist(title.id) }
    Button(store.personal.watched.contains(title.id) ? "Mark Unwatched" : "Mark Watched") {
      store.toggleWatched(title.id)
    }
    if title.kind == .series {
      Button(store.personal.followed.contains(title.id) ? "Unfollow Series" : "Follow Series") {
        store.toggleFollow(title.id)
      }
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
    Divider()
    RecommendationFeedbackItems(titleID: title.id, store: store)
  }
}

/// A 16:9 still (`.still`) for Up Next: artwork, a play button on hover, a progress bar at the bottom.
private struct HomeStillCard: View {
  let title: EntertainmentTitle
  @ObservedObject var store: EntertainmentStore
  let play: () -> Void
  let details: () -> Void
  @State private var hovering = false

  private var resume: EntertainmentVersion? { title.resumeVersion }
  private var progress: Double? {
    guard let resume, resume.duration > 0 else { return nil }
    return min(1, resume.progressSeconds / resume.duration)
  }
  private var subtitle: String {
    var parts: [String] = []
    if let season = resume?.season, let episode = resume?.episode {
      parts.append("S\(season), E\(episode)")
    }
    if let resume, resume.duration > 0 {
      let left = max(0, Int((resume.duration - resume.progressSeconds) / 60))
      parts.append("\(MyraRecommender.duration(left)) left")
    }
    return parts.isEmpty ? (title.year ?? "") : parts.joined(separator: " · ")
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 6) {
      Button(action: play) {
        Color.clear
          .aspectRatio(16.0 / 9.0, contentMode: .fit)
          .overlay {
            FallbackArtworkView(
              title: title.displayName, candidates: HomeLayout.posterCandidates(title))
          }
          .overlay(alignment: .bottomLeading) {
            VStack(alignment: .leading, spacing: 10) {
              Text(title.displayName)
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(.white)
                .shadow(color: .black.opacity(0.5), radius: 3, y: 1)
                .lineLimit(1)
              if let progress {
                ProgressBarView(fraction: progress, track: .white.opacity(0.3), tint: .white)
              }
            }
            .padding(.horizontal, 10)
            .padding(.bottom, progress == nil ? 10 : 8)
            .frame(maxWidth: .infinity, alignment: .leading)
          }
          .overlay {
            Image(systemName: "play.fill")
              .font(.system(size: 18))
              .foregroundStyle(.white)
              .frame(width: 44, height: 44)
              .background(.ultraThinMaterial, in: Circle())
              .background(Color.white.opacity(0.25), in: Circle())
              .opacity(hovering ? 1 : 0)
          }
          .clipShape(RoundedRectangle(cornerRadius: 10))
          .contentShape(RoundedRectangle(cornerRadius: 10))
      }
      .buttonStyle(.plain)
      .onHover { hovering = $0 }
      .accessibilityLabel("Resume \(title.displayName)")
      .contextMenu {
        Button("Resume", action: play)
        Button("Details and Versions", action: details)
        Button("Mark Watched") { store.toggleWatched(title.id) }
      }
      Text(subtitle)
        .font(.system(size: 12)).foregroundStyle(MyraColor.text2)
        .lineLimit(1)
    }
  }
}

/// The hero's buttons (`.btn`, `.btn.primary`, `.btn.glass`): 8pt radius, the accent when prominent.
private struct HeroButtonStyle: ButtonStyle {
  var prominent = false
  @Environment(\.myraAccent) private var accent

  func makeBody(configuration: Configuration) -> some View {
    configuration.label
      .font(.system(size: 13))
      .padding(.horizontal, 12).padding(.vertical, 6)
      .foregroundStyle(.white)
      .background(
        RoundedRectangle(cornerRadius: 8).fill(
          prominent
            ? accent.color
            : Color.white.opacity(configuration.isPressed ? 0.28 : 0.18))
      )
      .overlay {
        if !prominent {
          RoundedRectangle(cornerRadius: 8).stroke(Color.white.opacity(0.25), lineWidth: 1)
        }
      }
      .contentShape(RoundedRectangle(cornerRadius: 8))
  }
}

enum HomeAskMyra {
  /// A multi-word sentence reads as a request rather than a title search.
  static func isQuestion(_ query: String) -> Bool {
    let words = query.split(whereSeparator: \.isWhitespace)
    return words.count >= 3
  }
}

struct TonightWhyView: View {
  let pick: MyraRecommendation

  var body: some View {
    VStack(alignment: .leading, spacing: 8) {
      Text("Why \(pick.title.displayName)?").font(.headline)
      if pick.reasons.isEmpty {
        Text("A well-rated unwatched title from your library.")
      }
      ForEach(pick.reasons, id: \.self) { Label($0, systemImage: "checkmark") }
      Text(
        "Ranked on this \(MyraDevice.name) from your mood, the time of day and your viewing history. No AI request was made."
      )
      .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
    .padding(16).frame(width: 300)
  }
}

private struct TonightRequest: Hashable {
  var mood: MyraMood?
  var filter: HomeCatalogueFilter?
  var personalRevision: Int
}

/// Feedback items shared by the Tonight banner and card menus; saved in personal data.
struct RecommendationFeedbackItems: View {
  let titleID: String
  @ObservedObject var store: EntertainmentStore

  var body: some View {
    let feedback = store.personal.recommendationFeedback
    Button {
      store.react(.like, to: titleID)
    } label: {
      Label(
        "More Like This",
        systemImage: feedback?.liked.contains(titleID) == true
          ? "hand.thumbsup.fill" : "hand.thumbsup")
    }
    Button {
      store.react(.dislike, to: titleID)
    } label: {
      Label(
        "Less Like This",
        systemImage: feedback?.disliked.contains(titleID) == true
          ? "hand.thumbsdown.fill" : "hand.thumbsdown")
    }
    Button {
      store.react(.notTonight, to: titleID)
    } label: {
      Label("Not Tonight", systemImage: "moon.zzz")
    }
  }
}

enum HomeShelfPresentation {
  static func isVisible(count: Int, query: String) -> Bool {
    count > 0 || query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
  }
}

struct HomeCatalogueFilter: Hashable, Sendable {
  var query: String
  var selection: LibraryFilterSelection
  var revision: Int
}

struct HomeCatalogueProjection: Sendable {
  var filtered: [EntertainmentTitle] = []
  var movies: [EntertainmentTitle] = []
  var series: [EntertainmentTitle] = []
  var latest: [EntertainmentTitle] = []
  var recent: [EntertainmentTitle] = []
  var watchlist: [EntertainmentTitle] = []
  /// Facet options with counts for the filter bar, from the whole catalogue (folder and file-name
  /// fallbacks included), computed once per catalogue.
  var options = LibraryFilterOptions()
  /// Automatic shelves built from the filtered titles, in the user's arrangement, hidden shelves removed.
  var shelves: [HomeAutomaticShelf] = []
}

actor HomeProjectionWorker {
  /// Shared so the store can prepare the default view before Home is on screen.
  static let shared = HomeProjectionWorker()

  private var cached: HomeCatalogueIndex?

  /// The prepared index for this catalogue; rebuilt only when titles or their metadata changed.
  func index(for catalogue: [EntertainmentTitle]) -> HomeCatalogueIndex {
    let signature = HomeCatalogueIndex.Signature(catalogue)
    if let cached, cached.signature == signature { return cached }
    let built = HomeCatalogueIndex(titles: catalogue)
    cached = built
    return built
  }

  func prepare(
    _ catalogue: [EntertainmentTitle], personal: EntertainmentPersonalData,
    filter: HomeCatalogueFilter
  ) throws -> HomeCatalogueProjection {
    let mark = MyraPerformance.begin(PerfMark.homeRender)
    defer { mark.end("projection of \(catalogue.count) titles") }
    let prepared = index(for: catalogue)
    try Task.checkCancellation()
    return HomeFilterEngine.project(
      catalogue: catalogue, index: prepared, personal: personal, filter: filter)
  }
}

/// The sheet receives the chosen title atomically, including on its first presentation.
struct EntertainmentPickPresentation: Identifiable {
  let id: UUID
  let title: EntertainmentTitle?
  let filters: String
}
