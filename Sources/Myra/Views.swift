#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftData
import SwiftUI

struct LibraryView: View {
  @ObservedObject var coordinator: AppCoordinator
  @State private var showingAddCategory = false
  @State private var editingCategory: Category?
  @State private var deletingCategory: Category?
  @State private var search = LibrarySearchState()
  @State private var sortField: DirectorySortField = .name
  @State private var sortAscending = true
  @State private var previousInspector: GlobalSearchResult?
  @State private var showingSourceOverview: Bool
  @AppStorage("myraSourceLayout") private var storedLayout = ""
  @Environment(\.myraAccent) private var accent

  /// Returning to Sources keeps the source that was open in Browse.
  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _showingSourceOverview = State(initialValue: coordinator.currentCategory == nil)
  }

  /// Mirrors the shell's toolbar search field, which is this page's search box.
  private var searchText: String {
    get { search.query }
    nonmutating set {
      search.updateQuery(newValue)
      if coordinator.searchText != newValue { coordinator.searchText = newValue }
    }
  }
  private var searchScope: SearchScope {
    get { search.scope }
    nonmutating set { search.select(newValue) }
  }

  var body: some View {
    GeometryReader { geometry in
      Group {
        if showingGlobalResults {
          browser
        } else if coordinator.categories.isEmpty {
          sourceGrid(browsing: false)
        } else {
          // As the preview: the source grid, then Browse for the selected source below it.
          let browsing = !showingSourceOverview && coordinator.currentCategory != nil
          VStack(spacing: 0) {
            sourceGrid(browsing: browsing)
              .fixedSize(horizontal: false, vertical: browsing)
            if browsing { browser }
          }
        }
      }
      .frame(width: geometry.size.width, height: geometry.size.height, alignment: .top)
    }
    .navigationTitle("Myra")
    .onReceive(NotificationCenter.default.publisher(for: MyraCommand.notification)) { note in
      // Find focuses the shell's search field; this page only handles Add Source.
      switch MyraCommand(notification: note) {
      case .addSource:
        showingAddCategory = true
      default:
        break
      }
    }
    .onAppear {
      if search.query != coordinator.searchText { searchText = coordinator.searchText }
    }
    .onChange(of: coordinator.searchText) { _, text in
      if search.query != text { searchText = text }
    }
    .sheet(isPresented: $showingAddCategory) {
      CategoryEditorSheet(title: "Add Source", initialName: "", initialURL: "") { name, url in
        await coordinator.addCategory(name: name, urlText: url)
      }
    }
    .sheet(item: $editingCategory) { category in
      CategoryEditorSheet(
        title: "Edit Category", initialName: category.name, initialURL: category.rootURLString
      ) { name, url in
        await coordinator.updateCategory(category, name: name, urlText: url)
      }
    }
    .sheet(isPresented: Binding(get: { coordinator.isScanning }, set: { _ in })) {
      ScanProgressView(progress: coordinator.scanProgress) { coordinator.cancelScan() }
        .interactiveDismissDisabled()
    }
    .confirmationDialog(
      "Delete this category?",
      isPresented: Binding(
        get: { deletingCategory != nil },
        set: { if !$0 { deletingCategory = nil } }
      )
    ) {
      Button("Delete", role: .destructive) {
        if let deletingCategory { coordinator.deleteCategory(deletingCategory) }
        deletingCategory = nil
      }
    } message: {
      Text("Downloads already added to the queue will not be removed.")
    }
    .onChange(of: searchScope) { _, scope in
      if scope == .current {
        coordinator.cancelGlobalSearch()
        if let previousInspector {
          coordinator.inspect(previousInspector)
        } else {
          coordinator.inspector.close()
        }
      } else {
        previousInspector = coordinator.inspector.selected
        coordinator.startGlobalSearch(query: searchText)
      }
    }
    .onChange(of: searchText) { _, text in
      if searchScope == .global {
        if text.isEmpty {
          coordinator.clearGlobalSearch()
          if let previousInspector {
            coordinator.inspect(previousInspector)
          } else {
            coordinator.inspector.close()
          }
        } else {
          coordinator.startGlobalSearch(query: text)
        }
      }
    }
  }

  /// The source grid, including the empty state with only the Add Source card, as in the preview.
  private func sourceGrid(browsing: Bool) -> some View {
    SourcesOverviewPage(
      coordinator: coordinator, open: openSource,
      edit: { id in editingCategory = coordinator.categories.first { $0.id == id } },
      delete: { id in deletingCategory = coordinator.categories.first { $0.id == id } },
      addSource: { showingAddCategory = true },
      selectedID: browsing ? coordinator.currentCategory?.id : nil,
      scrolls: !browsing,
      hint: browsing || coordinator.categories.isEmpty
        ? nil : "Select a source to browse its folders.")
  }

  private func openSource(_ id: UUID) {
    guard let category = coordinator.categories.first(where: { $0.id == id }) else { return }
    showingSourceOverview = false
    Task { await coordinator.selectCategory(category) }
  }

  private var browser: some View {
    VStack(spacing: 0) {
      // The preview's order: the section header, the crumbs row, then the table.
      SectionHeader(title: showingGlobalResults ? "Search Results" : "Browse")
        .padding(.horizontal, 26)
      browseBar
      ZStack {
        currentBrowser
          .opacity(showingGlobalResults ? 0 : 1)
          .allowsHitTesting(!showingGlobalResults)
          .accessibilityHidden(showingGlobalResults)
        if showingGlobalResults { globalBrowser }
      }
    }
  }

  @ViewBuilder private var currentBrowser: some View {
    VStack(spacing: 0) {
      if coordinator.isLoading {
        ProgressView("Loading directory…").frame(maxWidth: .infinity, maxHeight: .infinity)
      } else if coordinator.currentCategory == nil {
        ContentUnavailableView {
          Label("No source open", systemImage: "server.rack")
        } description: {
          Text("Choose a source to browse its folders.")
        } actions: {
          Button("Show Sources") { showingSourceOverview = true }
            .buttonStyle(MyraButtonStyle(prominent: true))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
      } else if coordinator.entries.isEmpty {
        ContentUnavailableView("Empty Directory", systemImage: "folder")
          .frame(maxWidth: .infinity, maxHeight: .infinity)
      } else if visibleEntries.isEmpty {
        ContentUnavailableView(
          "No Matches", systemImage: "magnifyingglass",
          description: Text("No file or folder contains “\(searchText)”.")
        )
        .frame(maxWidth: .infinity, maxHeight: .infinity)
      } else {
        if SourceBrowserLayout.resolve(stored: storedLayout) == .grid {
          thumbnailGrid
        } else {
          browseSection
        }
      }
    }
  }

  @ViewBuilder private var globalBrowser: some View {
    if let error = coordinator.globalSearchErrorMessage,
      coordinator.globalSearchResults.isEmpty
    {
      ContentUnavailableView(
        "Global Search", systemImage: "exclamationmark.magnifyingglass",
        description: Text(error)
      )
      .frame(maxWidth: .infinity, maxHeight: .infinity)
    } else if coordinator.globalSearchResults.isEmpty {
      if coordinator.isGlobalSearching {
        VStack(spacing: 12) {
          ProgressView().controlSize(.large)
          Text("Searching the library index…").font(.headline)
          Text(globalProgressText).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
      } else if coordinator.globalSearchProgress.sourcesTotal > 0 {
        ContentUnavailableView(
          "No Matching Videos", systemImage: "film.stack",
          description: Text("No indexed title matched “\(searchText)”.")
        )
        .frame(maxWidth: .infinity, maxHeight: .infinity)
      } else {
        ContentUnavailableView(
          "Search Every Directory", systemImage: "magnifyingglass.circle",
          description: Text("Enter at least 3 characters. Results come from the local index.")
        )
        .frame(maxWidth: .infinity, maxHeight: .infinity)
      }
    } else {
      globalThumbnailGrid
    }
  }

  /// The Browse section from the preview: a breadcrumb above the file table.
  private var browseSection: some View {
    VStack(alignment: .leading, spacing: 0) {
      directoryTable
    }
    .padding(.horizontal, 26)
    .padding(.top, 4)
    .padding(.bottom, 30)
  }

  /// The file table from the preview: Name, Quality, Size, Modified and per-row actions.
  private var directoryTable: some View {
    ScrollView {
      LazyVStack(spacing: 0) {
        tableHeaderRow
        ForEach(visibleEntries) { entry in
          DirectoryEntryRow(
            entry: entry,
            isSelected: coordinator.selection.contains(entry.url),
            toggle: { coordinator.toggleSelection(entry) },
            copyURL: { coordinator.copyURL(entry) },
            openInVLC: { coordinator.openInVLC(entry) },
            pushToTV: { coordinator.pushToTV(entry) },
            play: { coordinator.play(entry) },
            download: {
              // Downloads queue the selection, so this row becomes the only selected entry first.
              coordinator.selection = [entry.url]
              coordinator.downloadSelection()
            },
            isVLCInstalled: coordinator.isVLCInstalled,
            open: {
              if entry.kind == .folder {
                Task { await coordinator.navigate(to: entry.url) }
              } else {
                coordinator.inspect(entry)
              }
            }
          )
          Rectangle().fill(MyraColor.line).frame(height: 1)
        }
      }
      .background(MyraColor.content, in: RoundedRectangle(cornerRadius: MyraRadius.panel))
      .overlay(
        RoundedRectangle(cornerRadius: MyraRadius.panel).stroke(MyraColor.line, lineWidth: 1))
    }
  }

  private var tableHeaderRow: some View {
    HStack(spacing: 0) {
      sortButton("Name", field: .name)
        .frame(maxWidth: .infinity, alignment: .leading)
      Text("Quality")
        .frame(width: 110, alignment: .leading)
      Text("Size")
        .frame(width: 96, alignment: .trailing)
      sortButton("Modified", field: .date)
        .frame(width: 150, alignment: .trailing)
      Color.clear.frame(width: 118)
    }
    .font(.system(size: 12))
    .foregroundStyle(MyraColor.text2)
    .padding(.horizontal, 14)
    .padding(.vertical, 8)
    .overlay(alignment: .bottom) {
      Rectangle().fill(MyraColor.line).frame(height: 1)
    }
  }

  private var thumbnailGrid: some View {
    ScrollView {
      LazyVGrid(
        columns: [GridItem(.adaptive(minimum: 210, maximum: 280), spacing: 16)],
        spacing: 18
      ) {
        ForEach(visibleEntries) { entry in
          DirectoryThumbnailCard(
            entry: entry,
            artworkURL: entry.kind == .file ? coordinator.artworkURL : nil,
            isSelected: coordinator.selection.contains(entry.url),
            isVLCInstalled: coordinator.isVLCInstalled,
            toggle: { coordinator.toggleSelection(entry) },
            copyURL: { coordinator.copyURL(entry) },
            openInVLC: { coordinator.openInVLC(entry) },
            pushToTV: { coordinator.pushToTV(entry) },
            play: { coordinator.play(entry) },
            open: {
              if entry.kind == .folder {
                Task { await coordinator.navigate(to: entry.url) }
              } else {
                coordinator.inspect(entry)
              }
            }
          )
        }
      }
      .padding(18)
    }
  }

  private var globalThumbnailGrid: some View {
    ScrollView {
      LazyVGrid(
        columns: [GridItem(.adaptive(minimum: 210, maximum: 280), spacing: 16)],
        spacing: 18
      ) {
        ForEach(coordinator.globalSearchResults) { result in
          DirectoryThumbnailCard(
            entry: result.entry,
            artworkURL: result.artworkURL,
            isSelected: coordinator.globalSearchSelection.contains(result.id),
            isVLCInstalled: coordinator.isVLCInstalled,
            sourceLabel: result.categoryName,
            pathLabel: result.parentPath,
            toggle: { coordinator.toggleGlobalSearchSelection(result) },
            copyURL: { coordinator.copyURL(result.entry) },
            openInVLC: { coordinator.openInVLC(result.entry) },
            pushToTV: { coordinator.pushToTV(result.entry) },
            play: { coordinator.play(result) },
            open: { coordinator.inspect(result) }
          )
        }
      }
      .padding(18)
      if coordinator.hasMoreGlobalResults {
        Button("Load More Results") { coordinator.loadMoreGlobalResults() }
          .disabled(coordinator.isGlobalSearching).padding(.bottom, 18)
      }
    }
  }

  /// The row under the header, as the preview's `.crumbs`: where you are on the left; counts,
  /// search scope, layout, reload and download on the right. The shell's field is the search box.
  private var browseBar: some View {
    HStack(spacing: 8) {
      if showingGlobalResults {
        Image(systemName: "network").foregroundStyle(accent.color)
        Text(coordinator.isGlobalSearching ? "Searching index…" : "All sources · Local index")
          .foregroundStyle(MyraColor.text2)
        if coordinator.isGlobalSearching {
          Button("Cancel") { coordinator.cancelGlobalSearch() }
            .buttonStyle(MyraButtonStyle())
        }
        if !coordinator.globalSearchFailures.isEmpty {
          Menu("\(coordinator.globalSearchFailures.count) unavailable") {
            ForEach(coordinator.globalSearchFailures) { failure in
              Text("\(failure.categoryName): \(failure.summary)")
            }
          }
          .menuIndicator(.hidden).fixedSize().foregroundStyle(.orange)
        }
      } else {
        IconButton(systemName: "chevron.left", help: "Go to the parent folder") {
          Task { await coordinator.navigateUp() }
        }
        .disabled(
          coordinator.currentURL == coordinator.currentCategory?.rootURL?.standardizedDirectoryURL)
        crumbs
      }
      Spacer(minLength: 12)
      Text(browseCountText)
        .foregroundStyle(MyraColor.text2)
        .monospacedDigit()
        .lineLimit(1)
      if searchScope == .global {
        Toggle("Fuzzy", isOn: $coordinator.fuzzyGlobalSearch)
          .toggleStyle(.button)
          .controlSize(.small)
          .help("Match typos and reordered title words. Turn off for exact matching.")
          .accessibilityLabel("Fuzzy global search")
      }
      SegmentedControl(
        options: [
          SegmentOption(value: SearchScope.current, title: "This folder"),
          SegmentOption(value: SearchScope.global, title: "All sources"),
        ],
        selection: Binding(get: { searchScope }, set: { searchScope = $0 })
      )
      .accessibilityLabel("Search scope")
      if !showingGlobalResults {
        SegmentedControl(
          options: SourceBrowserLayout.allCases.map { SegmentOption(value: $0, title: $0.title) },
          selection: Binding(
            get: { SourceBrowserLayout.resolve(stored: storedLayout) },
            set: { storedLayout = $0.rawValue })
        )
        .help("Show files as a list or a grid")
        .accessibilityLabel("File layout")
      }
      IconButton(
        systemName: "arrow.clockwise",
        help: showingGlobalResults ? "Refresh search" : "Reload this folder"
      ) {
        if showingGlobalResults {
          coordinator.startGlobalSearch(query: searchText)
        } else if let category = coordinator.currentCategory {
          Task { await coordinator.selectCategory(category) }
        }
      }
      .disabled(
        showingGlobalResults
          ? searchText.trimmingCharacters(in: .whitespacesAndNewlines).count < 3
            || coordinator.isGlobalSearching
          : coordinator.currentCategory == nil || coordinator.isLoading)
      Button {
        if showingGlobalResults {
          coordinator.downloadGlobalSearchSelection()
        } else {
          coordinator.downloadSelection()
        }
      } label: {
        Label("Download Selected", systemImage: "arrow.down.circle")
      }
      .buttonStyle(MyraButtonStyle(prominent: true))
      .disabled(
        showingGlobalResults
          ? coordinator.globalSearchSelection.isEmpty || coordinator.isEnqueueingGlobalDownloads
          : coordinator.selection.isEmpty || coordinator.isScanning)
    }
    .font(.system(size: 13))
    .padding(.horizontal, 26)
    .padding(.top, 4)
    .padding(.bottom, 10)
  }

  /// "Sources › Category › folder", with the current folder in bold.
  @ViewBuilder private var crumbs: some View {
    HStack(spacing: 6) {
      Button("Sources") { showingSourceOverview = true }
        .buttonStyle(.plain)
        .foregroundStyle(MyraColor.text2)
        .help("Show all sources")
        .accessibilityLabel("Back to Sources")
      if let category = coordinator.currentCategory, let root = category.rootURL {
        let trail = breadcrumbs(root: root, current: coordinator.currentURL)
        Text("›").foregroundStyle(MyraColor.text3)
        Button(category.name) { Task { await coordinator.navigate(to: root) } }
          .buttonStyle(.plain)
          .fontWeight(trail.isEmpty ? .semibold : .regular)
          .foregroundStyle(trail.isEmpty ? MyraColor.text : MyraColor.text2)
        ForEach(Array(trail.enumerated()), id: \.element.url) { offset, crumb in
          Text("›").foregroundStyle(MyraColor.text3)
          Button(crumb.name) { Task { await coordinator.navigate(to: crumb.url) } }
            .buttonStyle(.plain)
            .fontWeight(offset == trail.count - 1 ? .semibold : .regular)
            .foregroundStyle(offset == trail.count - 1 ? MyraColor.text : MyraColor.text2)
        }
      }
    }
    .lineLimit(1)
  }

  /// "12 of 40 items · 2 selected", or the global match count.
  private var browseCountText: String {
    if showingGlobalResults {
      let selected = coordinator.globalSearchSelection.count
      return "\(coordinator.globalSearchResults.count) matching videos"
        + (selected > 0 ? " · \(selected) selected" : "")
    }
    let selected = coordinator.selection.count
    return "\(visibleEntries.count) of \(coordinator.entries.count) items"
      + (selected > 0 ? " · \(selected) selected" : "")
  }

  private var showingGlobalResults: Bool {
    search.showingGlobalResults
  }

  private var globalProgressText: String {
    let progress = coordinator.globalSearchProgress
    return
      "\(progress.sourcesCompleted)/\(progress.sourcesTotal) sources • \(progress.foldersVisited) folders • \(progress.matchesFound) matches"
  }

  private var visibleEntries: [DirectoryEntry] {
    let currentText = searchScope == .global ? search.currentQuery : searchText
    let filtered =
      currentText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
      ? coordinator.entries
      : coordinator.entries.filter {
        $0.name.localizedCaseInsensitiveContains(
          currentText.trimmingCharacters(in: .whitespacesAndNewlines))
      }
    return DirectoryEntrySorter.sorted(filtered, by: sortField, ascending: sortAscending)
  }

  private func sortButton(_ title: String, field: DirectorySortField) -> some View {
    Button {
      if sortField == field {
        sortAscending.toggle()
      } else {
        sortField = field
        sortAscending = true
      }
    } label: {
      HStack(spacing: 5) {
        Text(title)
        if sortField == field {
          Image(systemName: sortAscending ? "chevron.up" : "chevron.down")
            .font(.system(size: 9, weight: .bold))
        }
      }
      .contentShape(Rectangle())
    }
    .buttonStyle(.plain)
    .help("Sort \(sortAscending && sortField == field ? "descending" : "ascending") by \(title)")
  }

  private func breadcrumbs(root: URL, current: URL?) -> [(name: String, url: URL)] {
    guard let current, current != root.standardizedDirectoryURL else { return [] }
    let rootParts = root.pathComponents
    let currentParts = current.pathComponents
    guard currentParts.count > rootParts.count else { return [] }
    var url = root
    return currentParts.dropFirst(rootParts.count).map { component in
      url.append(path: component, directoryHint: .isDirectory)
      return (component.removingPercentEncoding ?? component, url)
    }
  }
}

private struct DirectoryEntryRow: View {
  let entry: DirectoryEntry
  let isSelected: Bool
  @Environment(\.myraAccent) private var accent
  let toggle: () -> Void
  let copyURL: () -> Void
  let openInVLC: () -> Void
  let pushToTV: () -> Void
  let play: () -> Void
  let download: () -> Void
  let isVLCInstalled: Bool
  let open: () -> Void
  @State private var isHovering = false

  var body: some View {
    HStack(spacing: 0) {
      HStack(spacing: 9) {
        Button(action: toggle) {
          Image(systemName: isSelected ? "checkmark.square.fill" : "square")
            .foregroundStyle(isSelected ? accent.color : MyraColor.text3)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(isSelected ? "Deselect \(entry.name)" : "Select \(entry.name)")
        Image(systemName: entry.kind == .folder ? "folder.fill" : "film")
          .font(.system(size: 16))
          .foregroundStyle(accent.color)
          .frame(width: 18)
          .accessibilityHidden(true)
        Button(action: open) {
          Text(entry.name)
            .font(.system(size: 13, weight: .medium))
            .lineLimit(1)
            .truncationMode(.tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(entry.name)
      }
      .frame(maxWidth: .infinity, alignment: .leading)
      Group {
        if entry.kind == .file, let resolution = SourceFileFormat.resolution(for: entry.name) {
          LibraryTag(text: resolution)
        }
      }
      .frame(width: 110, alignment: .leading)
      Text(
        entry.size.map { Foundation.ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }
          ?? "—"
      )
        .lineLimit(1)
        .foregroundStyle(MyraColor.text2)
        .frame(width: 96, alignment: .trailing)
      Group {
        if let modifiedAt = entry.modifiedAt {
          Text(modifiedAt, format: .dateTime.day().month(.abbreviated).year())
        } else {
          Text("—")
        }
      }
      .lineLimit(1)
      .foregroundStyle(MyraColor.text2)
      .frame(width: 150, alignment: .trailing)
      Group {
        if entry.kind == .file {
          HStack(spacing: 4) {
            IconButton(systemName: "play.fill", help: "Play", action: play)
            IconButton(systemName: "arrow.down.to.line", help: "Download", action: download)
            Menu {
              Button("Copy", action: copyURL)
                .help("Copy the file URL")
              Button("VLC", action: openInVLC).disabled(!isVLCInstalled)
              Button("TV", action: pushToTV)
            } label: {
              Image(systemName: "ellipsis")
                .font(.system(size: 14, weight: .medium))
                .frame(width: 28, height: 28)
                .foregroundStyle(MyraColor.text2)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("More actions")
            .accessibilityLabel("More actions for \(entry.name)")
          }
        } else {
          Image(systemName: "chevron.right")
            .font(.system(size: 11))
            .foregroundStyle(MyraColor.text3)
            .accessibilityHidden(true)
        }
      }
      .frame(width: 118, alignment: .trailing)
    }
    .padding(.horizontal, 14)
    .padding(.vertical, 9)
    .background(isHovering ? MyraColor.fill : .clear)
    .contentShape(Rectangle())
    .onHover { isHovering = $0 }
  }
}

private struct DirectoryThumbnailCard: View {
  let entry: DirectoryEntry
  let artworkURL: URL?
  let isSelected: Bool
  let isVLCInstalled: Bool
  var sourceLabel: String? = nil
  var pathLabel: String? = nil
  let toggle: () -> Void
  let copyURL: () -> Void
  let openInVLC: () -> Void
  let pushToTV: () -> Void
  let play: () -> Void
  let open: () -> Void
  @Environment(\.myraAccent) private var accent

  var body: some View {
    VStack(alignment: .leading, spacing: 9) {
      ZStack(alignment: .topTrailing) {
        Button(action: open) {
          thumbnail
            .frame(maxWidth: .infinity)
            .aspectRatio(16 / 10, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 11, style: .continuous))
        }
        .buttonStyle(.plain)
        Button(action: toggle) {
          Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
            .font(.system(size: 20, weight: .semibold))
            .foregroundStyle(isSelected ? accent.color : .white)
            .shadow(radius: 3)
        }
        .buttonStyle(.plain)
        .padding(9)
      }

      Text(entry.name)
        .font(.system(size: 13.5, weight: .semibold))
        .lineLimit(1)
        .truncationMode(.tail)
        .help(entry.name)
        .onTapGesture(perform: open)

      if let sourceLabel {
        HStack(spacing: 5) {
          Text(sourceLabel).fontWeight(.semibold)
          if let pathLabel, !pathLabel.isEmpty {
            Text("•")
            Text(pathLabel).lineLimit(1).truncationMode(.middle)
          }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .help(pathLabel.map { "\(sourceLabel) / \($0)" } ?? sourceLabel)
      }

      HStack(spacing: 8) {
        if entry.kind == .file {
          Button(action: copyURL) { Image(systemName: "doc.on.doc").frame(width: 30, height: 30) }
            .help("Copy the file URL").accessibilityLabel("Copy")
          Button(action: pushToTV) { Image(systemName: "tv").frame(width: 30, height: 30) }
            .help("Send to TV with Play on Sam Online").accessibilityLabel("TV")
          Button(action: play) { Image(systemName: "play.fill").frame(width: 30, height: 30) }
            .help("Play in Myra").accessibilityLabel("Play")
          Button(action: openInVLC) {
            Image(platformImage: VLCIconAsset.image).resizable().scaledToFit()
              .frame(width: 22, height: 22).frame(width: 30, height: 30)
          }
          .disabled(!isVLCInstalled)
          .help(isVLCInstalled ? "Stream this file in VLC" : "VLC is not installed")
          .accessibilityLabel("VLC")
        } else {
          Label("Open Folder", systemImage: "folder.fill")
            .foregroundStyle(.secondary)
        }
        Spacer(minLength: 0)
      }
      .labelStyle(.iconOnly)
      .buttonStyle(.borderless)
      .controlSize(.small)
    }
    .padding(10)
    .background(
      Color(platformColor: .myraControlBackground),
      in: RoundedRectangle(cornerRadius: 14, style: .continuous)
    )
    .overlay {
      RoundedRectangle(cornerRadius: 14, style: .continuous)
        .stroke(
          isSelected ? accent.color : .secondary.opacity(0.18), lineWidth: isSelected ? 2 : 1)
    }
    .help(entry.name)
  }

  @ViewBuilder private var thumbnail: some View {
    if entry.kind == .folder {
      ZStack {
        accent.color.opacity(0.12)
        Image(systemName: "folder.fill")
          .font(.system(size: 46))
          .foregroundStyle(accent.color)
      }
    } else {
      // A local thumbnail first, then the generated title card, so a missing image never shows a blank tile.
      FallbackArtworkView(
        title: entry.name,
        candidates: artworkURL.map { [ArtworkCandidate.image(.source, $0), .titleCard] }
          ?? [.titleCard])
    }
  }

  private var videoPlaceholder: some View {
    ZStack {
      Color.secondary.opacity(0.12)
      Image(systemName: "film.stack.fill")
        .font(.system(size: 42))
        .foregroundStyle(.secondary)
    }
  }
}

@MainActor
enum VLCIconAsset {
  static let image: PlatformImage = {
    #if os(iOS)
    return UIImage(systemName: "play.rectangle.fill")!
    #else
    #if SWIFT_PACKAGE
      let url = Bundle.module.url(forResource: "VLC", withExtension: "icns")
    #else
      let url = Bundle.main.url(forResource: "VLC", withExtension: "icns")
    #endif
    if let url, let image = NSImage(contentsOf: url) { return image }
    return NSImage(systemSymbolName: "play.rectangle.fill", accessibilityDescription: "VLC")!
    #endif
  }()
}

private struct CategoryEmptyState: View {
  let addCategory: () -> Void

  var body: some View {
    VStack(spacing: 10) {
      Image(systemName: "folder.badge.plus")
        .font(.system(size: 28, weight: .light))
        .foregroundStyle(.secondary)
      Text("No sources yet")
        .font(.system(size: 14, weight: .semibold))
      Text("Add a provider directory to begin.")
        .font(.caption)
        .foregroundStyle(.secondary)
        .multilineTextAlignment(.center)
      Button("Add Category", action: addCategory)
        .buttonStyle(.bordered)
        .controlSize(.small)
    }
    .padding(18)
    .frame(maxWidth: .infinity)
  }
}

struct ToastView: View {
  let message: String

  var body: some View {
    Label(message, systemImage: "checkmark.circle.fill")
      .font(.system(size: 13, weight: .medium))
      .padding(.horizontal, 13)
      .frame(height: 36)
      .background(
        Color(platformColor: .myraControlBackground),
        in: Capsule(style: .continuous)
      )
      .overlay { Capsule(style: .continuous).stroke(.secondary.opacity(0.2), lineWidth: 1) }
      .shadow(color: .black.opacity(0.16), radius: 10, y: 3)
  }
}

private struct CategoryEditorSheet: View {
  let title: String
  let save: (String, String) async -> Bool
  @State private var name: String
  @State private var url: String
  @State private var isSaving = false
  @Environment(\.dismiss) private var dismiss

  init(
    title: String, initialName: String, initialURL: String,
    save: @escaping (String, String) async -> Bool
  ) {
    self.title = title
    self.save = save
    _name = State(initialValue: initialName)
    _url = State(initialValue: initialURL)
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      Text(title).font(.title2.bold())
      Form {
        TextField("Name", text: $name, prompt: Text("Movies"))
        TextField("Directory URL", text: $url, prompt: Text("http://server/path/"))
      }
      HStack {
        Spacer()
        Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
        Button("Validate & Save") {
          isSaving = true
          Task {
            if await save(name, url) { dismiss() }
            isSaving = false
          }
        }
        .keyboardShortcut(.defaultAction)
        .disabled(
          name.trimmingCharacters(in: .whitespaces).isEmpty
            || url.trimmingCharacters(in: .whitespaces).isEmpty || isSaving)
      }
      if isSaving { ProgressView("Checking the directory…") }
    }
    .padding(24)
    .frame(width: 520)
  }
}

private struct ScanProgressView: View {
  let progress: ScanProgress
  let cancel: () -> Void

  var body: some View {
    VStack(spacing: 18) {
      ProgressView().controlSize(.large)
      Text("Preparing Download").font(.title2.bold())
      Text("Scanning folders before adding files to aria2.").foregroundStyle(.secondary)
      HStack(spacing: 24) {
        Label("\(progress.foldersVisited) folders", systemImage: "folder")
        Label("\(progress.filesFound) files", systemImage: "doc")
        Label(ByteCountFormatter.string(progress.knownBytes), systemImage: "internaldrive")
      }
      Button("Cancel", role: .cancel, action: cancel)
    }
    .padding(32)
    .frame(width: 520, height: 280)
  }
}

private enum ByteCountFormatter {
  static func string(_ bytes: Int64) -> String {
    guard bytes > 0 else { return "—" }
    return Foundation.ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
  }
}

private enum DurationFormatter {
  static func string(_ seconds: TimeInterval) -> String {
    let value = max(0, Int(seconds))
    if value >= 3600 {
      return String(format: "%d:%02d:%02d", value / 3600, (value % 3600) / 60, value % 60)
    }
    return String(format: "%d:%02d", value / 60, value % 60)
  }
}

extension View {
  @ViewBuilder func mediaHeaderScrollEffect() -> some View {
    if #available(macOS 26.0, iOS 26.0, *) {
      self.scrollEdgeEffectStyle(.soft, for: .top).backgroundExtensionEffect()
    } else {
      self
    }
  }
}
