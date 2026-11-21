import SwiftUI

/// The Search segment of the Transfers page: query, indexer chips, filters, sort and results.
/// Nothing is sent to an indexer until the user presses Search or Return.
struct TorrentSearchView: View {
  @ObservedObject var controller: TorrentController
  @ObservedObject var model: TorrentSearchModel
  @ObservedObject var store: TorrentIndexerStore
  /// Opens Settings > Torrents. Supplied by the shell; the link is hidden when nil.
  var onManageIndexers: (() -> Void)?
  @FocusState private var queryFocused: Bool

  var body: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 0) {
        searchBar
        indexerRow
        filterRow
        content
      }
      .padding(.horizontal, 26)
      .padding(.bottom, 30)
    }
    .onAppear { queryFocused = true }
    .onChange(of: controller.searchFocusToken) { _, _ in queryFocused = true }
  }

  // MARK: Controls

  private var searchBar: some View {
    HStack(spacing: 10) {
      HStack(spacing: 10) {
        Image(systemName: "magnifyingglass")
          .foregroundStyle(MyraColor.text3)
          .accessibilityHidden(true)
        TextField("Search torrents by title, e.g. Sintel 1080p", text: $model.query)
          .textFieldStyle(.plain)
          .font(.system(size: 15))
          .focused($queryFocused)
          .onSubmit { model.search() }
          .accessibilityLabel("Search torrents")
      }
      .padding(.horizontal, 14)
      .padding(.vertical, 10)
      .background(MyraColor.content, in: RoundedRectangle(cornerRadius: 12))
      .overlay(RoundedRectangle(cornerRadius: 12).stroke(MyraColor.lineStrong, lineWidth: 1))
      if model.isSearching {
        Button("Cancel") { model.cancel() }.buttonStyle(MyraButtonStyle())
      }
      Button("Search") { model.search() }
        .buttonStyle(MyraButtonStyle(prominent: true))
        .disabled(model.query.trimmingCharacters(in: .whitespaces).isEmpty)
    }
    .padding(.top, 4)
  }

  private var indexerRow: some View {
    HStack(alignment: .top, spacing: 10) {
      rowLabel("Indexers")
      ChipBar(
        items: store.indexers.map { indexer in
          ChipItem(id: indexer.id, title: indexer.name + (isOffline(indexer) ? " · offline" : ""))
        },
        isOn: { id in store.indexers.first { $0.id == id }?.enabled == true }
      ) { id in
        let on = store.indexers.first { $0.id == id }?.enabled == true
        store.setEnabled(!on, for: id)
      }
      Spacer(minLength: 0)
      if let onManageIndexers {
        Button("Manage indexers…", action: onManageIndexers)
          .myraLinkButtonStyle()
          .font(.system(size: 12))
      }
    }
    .padding(.top, 12)
  }

  private var filterRow: some View {
    HStack(spacing: 10) {
      rowLabel("Filter")
      ChipBar(
        items: TorrentQuality.allCases.map { ChipItem(id: $0, title: $0.rawValue) },
        isOn: { model.filter.qualities.contains($0) }
      ) { quality in
        if model.filter.qualities.contains(quality) {
          model.filter.qualities.remove(quality)
        } else {
          model.filter.qualities.insert(quality)
        }
      }
      minSeedersMenu
      Spacer(minLength: 0)
      Picker("Sort", selection: $model.filter.sort) {
        ForEach(TorrentSearchSort.allCases) { Text($0.title).tag($0) }
      }
      .pickerStyle(.menu)
      .fixedSize()
      .accessibilityLabel("Sort results")
    }
    .padding(.top, 8)
  }

  private var minSeedersMenu: some View {
    Menu {
      Picker("Minimum seeders", selection: $model.filter.minSeeders) {
        ForEach([1, 2, 5, 10, 25, 50], id: \.self) { Text("\($0)").tag($0) }
      }
    } label: {
      Text("Min seeders · \(model.filter.minSeeders)")
        .font(.system(size: 12))
    }
    .menuStyle(.borderlessButton)
    .fixedSize()
    .accessibilityLabel("Minimum seeders")
    .accessibilityValue("\(model.filter.minSeeders)")
  }

  private func rowLabel(_ text: String) -> some View {
    Text(text)
      .font(.system(size: 12, weight: .semibold))
      .foregroundStyle(MyraColor.text2)
      .frame(width: 56, alignment: .leading)
      .padding(.top, 5)
  }

  private func isOffline(_ indexer: TorrentIndexer) -> Bool {
    if case .failed = model.state(for: indexer.id) { return true }
    return false
  }

  // MARK: States

  @ViewBuilder
  private var content: some View {
    if let notice = model.notice {
      Text(notice).font(.system(size: 12)).foregroundStyle(.orange).padding(.top, 12)
    }
    if store.enabled.isEmpty {
      emptyState(
        symbol: "slider.horizontal.3", title: "No indexers turned on",
        text: "Turn on at least one indexer to search."
      ) {
        if let onManageIndexers {
          Button("Open Torrent Settings", action: onManageIndexers).buttonStyle(MyraButtonStyle())
        }
      }
    } else if model.phase == .idle {
      emptyState(
        symbol: "magnifyingglass", title: "Search from inside Myra",
        text:
          "Results come only from indexers you turned on. Nothing is sent until you press Search. Choose Play Now to stream, or Download to keep a copy."
      ) { EmptyView() }
    } else {
      if model.isSearching {
        HStack(spacing: 8) {
          ProgressView().controlSize(.small)
          Text(searchingText).font(.system(size: 12)).foregroundStyle(MyraColor.text2)
        }
        .padding(.top, 14)
      }
      if !model.failedStatuses.isEmpty { offlineBanner }
      if controller.importing {
        HStack {
          ProgressView().controlSize(.small)
          Text("Fetching torrent metadata…")
          Button("Cancel") { controller.cancelImport() }
        }
        .font(.system(size: 12)).padding(.top, 12)
      }
      results
    }
  }

  private var searchingText: String {
    let waiting = model.statuses.filter { $0.state == .searching }.count
    return waiting == 1 ? "Waiting for 1 indexer…" : "Searching \(waiting) indexers…"
  }

  private var offlineBanner: some View {
    let failed = model.failedStatuses
    let total = model.statuses.count
    return VStack(alignment: .leading, spacing: 6) {
      HStack(alignment: .top, spacing: 10) {
        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        Text(
          "\(failed.map(\.name).joined(separator: ", ")) did not respond. Showing results from \(total - failed.count) of \(total) indexers."
        )
        .font(.system(size: 12))
        Spacer(minLength: 8)
        Button("Retry") { model.retryFailed() }
          .myraLinkButtonStyle()
          .disabled(model.isSearching)
      }
      ForEach(failed) { status in
        if case .failed(let failure) = status.state {
          Text("\(status.name): \(failure.message)")
            .font(.system(size: 11)).foregroundStyle(MyraColor.text2)
            .padding(.leading, 26)
        }
      }
    }
    .padding(12)
    .background(Color.orange.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
    .padding(.top, 12)
  }

  @ViewBuilder
  private var results: some View {
    let shown = model.visible
    if !shown.isEmpty {
      HStack(spacing: 8) {
        Text("Results").font(.system(size: 15, weight: .semibold))
        Text("\(shown.count)").font(.system(size: 12)).foregroundStyle(MyraColor.text2)
      }
      .padding(.top, 18).padding(.bottom, 8)
      RowCard(rows: shown.map(row))
      Text(
        "Peers and trackers can see your IP address. Only play or download content you have the right to. Archive and Blender licences are declared by their sources; verify permission there."
      )
      .font(.system(size: 12)).foregroundStyle(MyraColor.text3).padding(.top, 12)
    } else if !model.isSearching {
      emptyState(
        symbol: "questionmark.circle", title: "No matches for “\(model.searchedQuery)”",
        text: "Try fewer words, lower the minimum seeders, or turn on another indexer."
      ) {
        if !model.filter.isDefault {
          Button("Reset filters") { model.resetFilters() }.buttonStyle(MyraButtonStyle())
        }
      }
    }
  }

  private func emptyState<Actions: View>(
    symbol: String, title: String, text: String, @ViewBuilder actions: () -> Actions
  ) -> some View {
    VStack(spacing: 6) {
      Image(systemName: symbol).font(.system(size: 30)).foregroundStyle(MyraColor.text3)
        .accessibilityHidden(true)
      Text(title).font(.system(size: 15, weight: .semibold))
      Text(text).font(.system(size: 13)).foregroundStyle(MyraColor.text2)
        .multilineTextAlignment(.center).frame(maxWidth: 440)
      actions().padding(.top, 6)
    }
    .frame(maxWidth: .infinity)
    .padding(.vertical, 44)
  }

  // MARK: Rows

  private func row(_ result: TorrentSearchResult) -> RowCardItem {
    var meta = [result.indexerName + (result.alsoFrom.isEmpty ? "" : " +\(result.alsoFrom.count)")]
    if let published = result.published {
      meta.append(published.formatted(.relative(presentation: .named)))
    }
    if let size = result.size {
      meta.append(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
    }
    if let seeders = result.seeders {
      meta.append("↑ \(seeders)" + (result.leechers.map { "  ↓ \($0)" } ?? ""))
    } else if result.license != nil {
      meta.append("Open licence")
    }
    let busy = controller.importing || !controller.ready
    var actions = [
      RowCardAction(id: "play", title: "Play Now", systemImage: "play.fill") {
        guard !busy else { return }
        controller.start(result, mode: .play)
      },
      RowCardAction(id: "download", title: "Download", systemImage: "arrow.down.to.line") {
        guard !busy else { return }
        controller.start(result, mode: .download)
      },
    ]
    if let magnet = result.shareableMagnet {
      actions.append(
        RowCardAction(id: "copy", title: "Copy magnet link", systemImage: "doc.on.doc") {
          PlatformServices.copy(magnet)
          controller.notice = "Magnet link copied."
        })
    }
    if result.archiveID != nil || result.license != nil, let source = result.sourceURL {
      actions.append(
        RowCardAction(id: "source", title: "View source", systemImage: "arrow.up.right.square") {
          PlatformServices.open(source)
        })
    }
    return RowCardItem(
      id: result.id, title: result.title,
      thumbnailSymbol: result.license == nil ? "film.stack" : "building.columns",
      thumbnailColors: LibraryPalette.gradient(for: result.title),
      tags: result.quality.map { [$0.rawValue] } ?? [], meta: meta, actions: actions)
  }
}
