import AppKit
import SwiftData
import SwiftUI

private enum AppSection: String, CaseIterable, Identifiable {
  case home = "Home"
  case library = "Library"
  case offline = "Offline Library"
  case settings = "Settings"

  var id: String { rawValue }
  var icon: String {
    switch self {
    case .home: "house"
    case .library: "folder"
    case .offline: "externaldrive"
    case .settings: "gearshape"
    }
  }
}

struct RootView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var player: EmbeddedPlayerModel
  @Environment(\.modelContext) private var modelContext
  @State private var section: AppSection? = .home
  @State private var isDownloadDrawerExpanded = false

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _player = ObservedObject(wrappedValue: coordinator.player)
  }

  private var fullscreenPlayback: Bool { player.media != nil && player.isFullscreen }

  var body: some View {
    GeometryReader { geometry in
      ZStack {
        CodexBackground()
        VStack(spacing: 0) {
          ZStack {
            NavigationSplitView {
              List(AppSection.allCases, selection: $section) { item in
                Label(item.rawValue, systemImage: item.icon).tag(item)
              }
              .scrollContentBackground(.hidden)
              .background(.thinMaterial)
              .navigationTitle("Myra")
              .navigationSplitViewColumnWidth(min: 170, ideal: 190)
            } detail: {
              Group {
                switch section ?? .home {
                case .home:
                  HomeView(coordinator: coordinator, store: coordinator.entertainment)
                case .library:
                  LibraryPlayerContainer(coordinator: coordinator)
                case .offline:
                  OfflineLibraryView(coordinator: coordinator)
                case .settings:
                  if let settings = coordinator.settings {
                    SettingsView(coordinator: coordinator, settings: settings)
                  } else {
                    ProgressView()
                  }
                }
              }
              .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
              .background(.ultraThinMaterial)
              .task { coordinator.configure(modelContext: modelContext) }
              .alert(
                "Myra",
                isPresented: Binding(
                  get: { coordinator.errorMessage != nil },
                  set: { if !$0 { coordinator.errorMessage = nil } }
                )
              ) {
                Button("OK") { coordinator.errorMessage = nil }
              } message: {
                Text(coordinator.errorMessage ?? "Unknown error")
              }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .opacity(player.media == nil ? 1 : 0)
            .allowsHitTesting(player.media == nil)
            .accessibilityHidden(player.media != nil)
            if player.media != nil {
              EmbeddedPlayerView(coordinator: coordinator, player: player)
            }
          }
          if !fullscreenPlayback {
            LibraryIndexStatusView(
              library: coordinator.library, refresh: coordinator.refreshLibraryIndex)
            if coordinator.hasDownloadQueue {
              DownloadDrawer(
                coordinator: coordinator,
                isExpanded: $isDownloadDrawerExpanded,
                expandedHeight: min(340, geometry.size.height * 0.43)
              )
            }
          }
        }
      }
      .sheet(item: $coordinator.playbackChoices) { choices in
        PlaybackChoicesView(choices: choices, play: coordinator.playChosenVersion)
      }
      .animation(.snappy(duration: 0.28), value: isDownloadDrawerExpanded)
      .overlay(alignment: .topTrailing) {
        if let message = coordinator.toastMessage, !fullscreenPlayback {
          ToastView(message: message)
            .padding(.top, 12)
            .padding(.trailing, 16)
            .transition(.move(edge: .top).combined(with: .opacity))
        }
      }
    }
    .ignoresSafeArea(edges: fullscreenPlayback ? .all : [])
    .background(PlayerWindowAttachment(player: player))
    .font(.system(size: 14, weight: .regular, design: .default))
    .environment(\.defaultMinListRowHeight, 42)
    .tint(.accentColor)
    .preferredColorScheme(preferredColorScheme)
    .onChange(of: coordinator.hasDownloadQueue) { _, visible in
      if !visible { isDownloadDrawerExpanded = false }
    }
    .toolbar(fullscreenPlayback ? .hidden : .automatic, for: .windowToolbar)
    .toolbarBackground(.regularMaterial, for: .windowToolbar)
    .toolbarBackground(.visible, for: .windowToolbar)
    .toolbar {
      if player.media != nil {
        ToolbarItem(placement: .navigation) {
          Button {
            player.close()
          } label: {
            Label("Back to Library", systemImage: "chevron.left")
          }
        }.continuousHeaderBackground()
        ToolbarItem(placement: .principal) {
          Text(player.media.map { MediaIdentity.parse(filename: $0.entry.name).title } ?? "Player")
            .font(.headline).lineLimit(1).help(player.media?.entry.name ?? "")
        }.continuousHeaderBackground()
        ToolbarItemGroup(placement: .primaryAction) {
          Text(player.status).font(.caption).foregroundStyle(.secondary)
          Button {
            player.toggleFullscreen()
          } label: {
            Image(systemName: "arrow.up.left.and.arrow.down.right")
          }
          .help("Enter fullscreen").accessibilityIdentifier("player.fullscreen")
        }.continuousHeaderBackground()
      } else {
        ToolbarItem(placement: .principal) {
          if section == .home {
            HomeToolbarTitle(store: coordinator.entertainment)
          } else {
            Text((section ?? .home).rawValue).font(.headline)
          }
        }.continuousHeaderBackground()
        ToolbarItem(placement: .primaryAction) {
          Button {
            coordinator.cycleTheme()
          } label: {
            Image(systemName: themeIcon)
          }
          .help("Theme: \(themeMode.displayName). Click for \(themeMode.next.displayName).")
        }.continuousHeaderBackground()
      }
    }
  }

  private var themeMode: AppThemeMode { coordinator.settings?.theme ?? .system }

  private var preferredColorScheme: ColorScheme? {
    switch themeMode {
    case .system: nil
    case .light: .light
    case .dark: .dark
    }
  }

  private var themeIcon: String {
    switch themeMode {
    case .system: "circle.lefthalf.filled"
    case .light: "sun.max.fill"
    case .dark: "moon.stars.fill"
    }
  }
}

struct LibraryView: View {
  @ObservedObject var coordinator: AppCoordinator
  @State private var showingAddCategory = false
  @State private var editingCategory: Category?
  @State private var deletingCategory: Category?
  @State private var search = LibrarySearchState()
  @State private var sortField: DirectorySortField = .name
  @State private var sortAscending = true
  @State private var previousInspector: GlobalSearchResult?

  private var searchText: String {
    get { search.query }
    nonmutating set { search.updateQuery(newValue) }
  }
  private var searchScope: SearchScope {
    get { search.scope }
    nonmutating set { search.select(newValue) }
  }

  var body: some View {
    GeometryReader { geometry in
      HSplitView {
        categoryList
          .frame(minWidth: 190, idealWidth: 220, maxWidth: 280, maxHeight: .infinity)
        browser
          .frame(minWidth: 560, maxHeight: .infinity, alignment: .top)
      }
      .frame(width: geometry.size.width, height: geometry.size.height)
    }
    .navigationTitle("Myra")
    .toolbar {
      ToolbarItem(placement: .principal) {
        searchAndNavigationBar
      }
      ToolbarItemGroup {
        Button {
          showingAddCategory = true
        } label: {
          Label("Add Category", systemImage: "plus")
        }
        Button {
          if showingGlobalResults {
            coordinator.startGlobalSearch(query: searchText)
          } else {
            Task {
              if let category = coordinator.currentCategory {
                await coordinator.selectCategory(category)
              }
            }
          }
        } label: {
          Label(
            showingGlobalResults ? "Refresh Search" : "Refresh", systemImage: "arrow.clockwise")
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
          Label("Download Selected", systemImage: "arrow.down.circle.fill")
        }
        .disabled(
          showingGlobalResults
            ? coordinator.globalSearchSelection.isEmpty || coordinator.isEnqueueingGlobalDownloads
            : coordinator.selection.isEmpty || coordinator.isScanning)
      }
    }
    .sheet(isPresented: $showingAddCategory) {
      CategoryEditorSheet(title: "Add Category", initialName: "", initialURL: "") { name, url in
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
    .task {
      if coordinator.currentCategory == nil, let first = coordinator.categories.first {
        await coordinator.selectCategory(first)
      }
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

  private var categoryList: some View {
    VStack(spacing: 0) {
      List(
        selection: Binding(
          get: { coordinator.currentCategory?.id },
          set: { id in
            guard let id, let category = coordinator.categories.first(where: { $0.id == id }) else {
              return
            }
            if searchScope == .global { searchScope = .current }
            Task { await coordinator.selectCategory(category) }
          }
        )
      ) {
        ForEach(coordinator.categories) { category in
          Label(category.name, systemImage: "folder.fill")
            .tag(category.id)
            .contextMenu {
              Button("Edit") { editingCategory = category }
              Button("Delete", role: .destructive) { deletingCategory = category }
            }
        }
      }
      .scrollContentBackground(.hidden)
      if coordinator.categories.isEmpty {
        CategoryEmptyState { showingAddCategory = true }
      }
      Divider()
      HStack {
        Button {
          showingAddCategory = true
        } label: {
          Image(systemName: "plus")
        }
        Button {
          if let category = coordinator.currentCategory { editingCategory = category }
        } label: {
          Image(systemName: "pencil")
        }
        .disabled(coordinator.currentCategory == nil)
        Spacer()
      }
      .buttonStyle(.borderless)
      .padding(10)
    }
  }

  private var browser: some View {
    VStack(spacing: 0) {
      ZStack {
        currentBrowser
          .opacity(showingGlobalResults ? 0 : 1)
          .allowsHitTesting(!showingGlobalResults)
          .accessibilityHidden(showingGlobalResults)
        if showingGlobalResults { globalBrowser }
      }
      Divider()
      HStack {
        if showingGlobalResults {
          Text("\(coordinator.globalSearchResults.count) matching videos")
          if !coordinator.globalSearchSelection.isEmpty {
            Text("• \(coordinator.globalSearchSelection.count) selected")
          }
        } else {
          Text("\(visibleEntries.count) of \(coordinator.entries.count) items")
          if !coordinator.selection.isEmpty { Text("• \(coordinator.selection.count) selected") }
        }
        Spacer()
      }
      .font(.caption)
      .foregroundStyle(.secondary)
      .padding(.horizontal, 12)
      .frame(height: 28)
    }
  }

  @ViewBuilder private var currentBrowser: some View {
    VStack(spacing: 0) {
      if coordinator.isLoading {
        ProgressView("Loading directory…").frame(maxWidth: .infinity, maxHeight: .infinity)
      } else if coordinator.currentCategory == nil {
        LibraryWelcomeView { showingAddCategory = true }
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
        if visibleEntries.contains(where: MediaFileType.isVideo) {
          thumbnailGrid
        } else {
          directoryHeader
          Divider()
          directoryList
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

  private var directoryList: some View {
    List(visibleEntries) { entry in
      DirectoryEntryRow(
        entry: entry,
        isSelected: coordinator.selection.contains(entry.url),
        toggle: { coordinator.toggleSelection(entry) },
        copyURL: { coordinator.copyURL(entry) },
        openInVLC: { coordinator.openInVLC(entry) },
        pushToTV: { coordinator.pushToTV(entry) },
        play: { coordinator.play(entry) },
        isVLCInstalled: coordinator.isVLCInstalled,
        open: {
          if entry.kind == .folder {
            Task { await coordinator.navigate(to: entry.url) }
          } else {
            coordinator.inspect(entry)
          }
        }
      )
      .listRowInsets(EdgeInsets(top: 0, leading: 12, bottom: 0, trailing: 12))
      .listRowSeparator(.hidden)
    }
    .listStyle(.inset)
    .scrollContentBackground(.hidden)
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

  private var searchAndNavigationBar: some View {
    HStack(spacing: 6) {
      if !showingGlobalResults {
        Button {
          Task { await coordinator.navigateUp() }
        } label: {
          Image(systemName: "chevron.left")
        }
        .disabled(
          coordinator.currentURL == coordinator.currentCategory?.rootURL?.standardizedDirectoryURL)
        if let category = coordinator.currentCategory, let root = category.rootURL {
          Button(category.name) { Task { await coordinator.navigate(to: root) } }
          ForEach(breadcrumbs(root: root, current: coordinator.currentURL), id: \.url) { crumb in
            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
            Button(crumb.name) { Task { await coordinator.navigate(to: crumb.url) } }
          }
        } else {
          Text("No category selected").foregroundStyle(.secondary)
        }
      } else {
        Image(systemName: "network").foregroundStyle(.cyan)
        Text(coordinator.isGlobalSearching ? "Searching index…" : "All directories • Local index")
          .foregroundStyle(.secondary)
        if coordinator.isGlobalSearching {
          Button("Cancel", systemImage: "xmark.circle") { coordinator.cancelGlobalSearch() }
        }
        if !coordinator.globalSearchFailures.isEmpty {
          Menu {
            ForEach(coordinator.globalSearchFailures) { failure in
              Text(
                "\(failure.categoryName): \(failure.failedFolders) folder(s) — \(failure.message)"
              )
            }
          } label: {
            Label(
              "\(coordinator.globalSearchFailures.count) unavailable",
              systemImage: "exclamationmark.triangle.fill")
          }
          .foregroundStyle(.orange)
        }
      }
      Spacer(minLength: 12)
      if searchScope == .global {
        Toggle("Fuzzy", isOn: $coordinator.fuzzyGlobalSearch)
          .toggleStyle(.button)
          .help("Match typos and reordered title words. Turn off for exact matching.")
          .accessibilityLabel("Fuzzy global search")
      }
      Picker("Search Scope", selection: Binding(get: { searchScope }, set: { searchScope = $0 })) {
        ForEach(SearchScope.allCases, id: \.self) { scope in Text(scope.rawValue).tag(scope) }
      }
      .pickerStyle(.segmented)
      .labelsHidden()
      .frame(width: 150)
      HStack(spacing: 6) {
        Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
        TextField(
          searchScope == .global ? "Search all video files" : "Search files and folders",
          text: Binding(get: { searchText }, set: { searchText = $0 })
        )
        .textFieldStyle(.plain)
        .onSubmit {
          if searchScope == .global { coordinator.startGlobalSearch(query: searchText) }
        }
        if !searchText.isEmpty {
          Button {
            searchText = ""
            if searchScope == .global { coordinator.clearGlobalSearch() }
          } label: {
            Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
          }
          .buttonStyle(.plain)
        }
      }
      .padding(.horizontal, 9)
      .frame(width: 240, height: 28)
      .background(
        Color(nsColor: .textBackgroundColor),
        in: RoundedRectangle(cornerRadius: 7, style: .continuous)
      )
      .overlay {
        RoundedRectangle(cornerRadius: 7, style: .continuous)
          .stroke(.secondary.opacity(0.22), lineWidth: 1)
      }
    }
    .buttonStyle(.plain)
    .padding(.horizontal, 12)
    .frame(minWidth: 480, idealWidth: 720, maxWidth: .infinity)
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

  private var directoryHeader: some View {
    HStack(spacing: 10) {
      Color.clear.frame(width: 20)
      Color.clear.frame(width: 22)
      sortButton("File / Folder Name", field: .name)
        .frame(maxWidth: .infinity, alignment: .leading)
      sortButton("Date Modified", field: .date)
        .frame(width: 175, alignment: .leading)
      Text("Actions").frame(width: 240, alignment: .center)
      Text("Size").frame(width: 95, alignment: .trailing)
      Color.clear.frame(width: 14)
    }
    .font(.system(size: 12.5, weight: .semibold))
    .foregroundStyle(.secondary)
    .padding(.horizontal, 12)
    .frame(height: 34)
    .background(Color(nsColor: .controlBackgroundColor))
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
  let toggle: () -> Void
  let copyURL: () -> Void
  let openInVLC: () -> Void
  let pushToTV: () -> Void
  let play: () -> Void
  let isVLCInstalled: Bool
  let open: () -> Void

  var body: some View {
    HStack(spacing: 10) {
      Button(action: toggle) {
        Image(systemName: isSelected ? "checkmark.square.fill" : "square")
          .foregroundStyle(isSelected ? Color.accentColor : .secondary)
      }
      .buttonStyle(.plain)
      Image(systemName: entry.kind == .folder ? "folder.fill" : "doc.fill")
        .foregroundStyle(entry.kind == .folder ? .blue : .secondary)
        .frame(width: 22)
      Button(action: open) {
        Text(entry.name)
          .lineLimit(1)
          .truncationMode(.tail)
          .frame(maxWidth: .infinity, alignment: .leading)
      }
      .buttonStyle(.plain)
      .help(entry.name)
      if let modifiedAt = entry.modifiedAt {
        Text(modifiedAt, format: .dateTime.year().month().day().hour().minute())
          .lineLimit(1)
          .foregroundStyle(.secondary)
          .frame(width: 175, alignment: .leading)
      } else {
        Text("—").foregroundStyle(.tertiary).frame(width: 175, alignment: .leading)
      }
      Group {
        if entry.kind == .file {
          HStack(spacing: 8) {
            Button(action: copyURL) {
              Label("Copy", systemImage: "doc.on.doc")
            }
            .help("Copy the file URL")
            Button(action: openInVLC) {
              Label("VLC", systemImage: "play.rectangle.fill")
            }
            .disabled(!isVLCInstalled)
            .help(
              isVLCInstalled
                ? "Stream this file in VLC" : "VLC is not installed in Applications")
            Button(action: pushToTV) {
              Label("TV", systemImage: "tv")
            }
            .help("Send this URL with the Play on Sam Online Shortcut")
            Button(action: play) { Label("Play", systemImage: "play.fill") }
          }
          .labelStyle(.titleAndIcon)
          .buttonStyle(.borderless)
          .controlSize(.small)
        } else {
          Color.clear
        }
      }
      .frame(width: 240)
      Text(entry.size.map(ByteCountFormatter.string) ?? "—")
        .lineLimit(1)
        .foregroundStyle(.secondary).frame(width: 95, alignment: .trailing)
      Group {
        if entry.kind == .folder {
          Image(systemName: "chevron.right").foregroundStyle(.tertiary)
        } else {
          Color.clear
        }
      }
      .frame(width: 14)
    }
    .contentShape(Rectangle())
    .frame(height: 44)
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
            .foregroundStyle(isSelected ? Color.accentColor : .white)
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
            Image(nsImage: VLCIconAsset.image).resizable().scaledToFit()
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
      Color(nsColor: .controlBackgroundColor),
      in: RoundedRectangle(cornerRadius: 14, style: .continuous)
    )
    .overlay {
      RoundedRectangle(cornerRadius: 14, style: .continuous)
        .stroke(
          isSelected ? Color.accentColor : .secondary.opacity(0.18), lineWidth: isSelected ? 2 : 1)
    }
    .help(entry.name)
  }

  @ViewBuilder private var thumbnail: some View {
    if entry.kind == .folder {
      ZStack {
        Color.accentColor.opacity(0.12)
        Image(systemName: "folder.fill")
          .font(.system(size: 46))
          .foregroundStyle(.blue)
      }
    } else if let artworkURL {
      AsyncImage(url: artworkURL) { phase in
        switch phase {
        case .success(let image):
          image.resizable().scaledToFill()
        case .failure:
          videoPlaceholder
        case .empty:
          ZStack {
            videoPlaceholder
            ProgressView()
          }
        @unknown default:
          videoPlaceholder
        }
      }
    } else {
      videoPlaceholder
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
  static let image: NSImage = {
    #if SWIFT_PACKAGE
      let url = Bundle.module.url(forResource: "VLC", withExtension: "icns")
    #else
      let url = Bundle.main.url(forResource: "VLC", withExtension: "icns")
    #endif
    if let url, let image = NSImage(contentsOf: url) { return image }
    return NSImage(systemSymbolName: "play.rectangle.fill", accessibilityDescription: "VLC")!
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

private struct LibraryWelcomeView: View {
  let addCategory: () -> Void

  var body: some View {
    VStack(spacing: 22) {
      ZStack {
        RoundedRectangle(cornerRadius: 22, style: .continuous)
          .fill(Color.accentColor.opacity(0.12))
        RoundedRectangle(cornerRadius: 22, style: .continuous)
          .stroke(Color.accentColor.opacity(0.22), lineWidth: 1)
        Image(systemName: "arrow.down.to.line.compact")
          .font(.system(size: 40, weight: .medium))
          .foregroundStyle(Color.accentColor)
      }
      .frame(width: 88, height: 88)

      VStack(spacing: 8) {
        Text("Your media library starts here")
          .font(.system(size: 25, weight: .semibold))
        Text("Connect an ISP directory, explore its folders, and send any selection to aria2.")
          .foregroundStyle(.secondary)
          .multilineTextAlignment(.center)
          .frame(maxWidth: 470)
      }

      Button(action: addCategory) {
        Label("Add your first category", systemImage: "plus")
          .padding(.horizontal, 6)
      }
      .buttonStyle(.borderedProminent)
      .controlSize(.large)

      HStack(spacing: 22) {
        WelcomeFeature(icon: "folder", title: "Browse folders")
        WelcomeFeature(icon: "arrow.triangle.branch", title: "Keep hierarchy")
        WelcomeFeature(icon: "bolt", title: "aria2 powered")
      }
    }
    .padding(42)
    .frame(maxWidth: 620)
    .background(
      Color(nsColor: .controlBackgroundColor),
      in: RoundedRectangle(cornerRadius: 18, style: .continuous)
    )
    .overlay {
      RoundedRectangle(cornerRadius: 18, style: .continuous)
        .stroke(.secondary.opacity(0.16), lineWidth: 1)
    }
    .padding(36)
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}

private struct WelcomeFeature: View {
  let icon: String
  let title: String

  var body: some View {
    Label(title, systemImage: icon)
      .font(.system(size: 12.5, weight: .medium))
      .foregroundStyle(.secondary)
  }
}

private struct ToastView: View {
  let message: String

  var body: some View {
    Label(message, systemImage: "checkmark.circle.fill")
      .font(.system(size: 13, weight: .medium))
      .padding(.horizontal, 13)
      .frame(height: 36)
      .background(
        Color(nsColor: .controlBackgroundColor),
        in: Capsule(style: .continuous)
      )
      .overlay { Capsule(style: .continuous).stroke(.secondary.opacity(0.2), lineWidth: 1) }
      .shadow(color: .black.opacity(0.16), radius: 10, y: 3)
  }
}

private struct CodexBackground: View {
  @Environment(\.colorScheme) private var colorScheme

  var body: some View {
    ZStack {
      NativeVisualEffectView()
      Group {
        if colorScheme == .dark {
          Color(red: 0.025, green: 0.028, blue: 0.042).opacity(0.66)
        } else {
          Color.white.opacity(0.56)
        }
      }
    }
    .ignoresSafeArea()
  }
}

private struct NativeVisualEffectView: NSViewRepresentable {
  func makeNSView(context: Context) -> NSVisualEffectView {
    let view = NSVisualEffectView()
    view.material = .underWindowBackground
    view.blendingMode = .behindWindow
    view.state = .active
    return view
  }

  func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

private struct DownloadDrawer: View {
  @ObservedObject var coordinator: AppCoordinator
  @Binding var isExpanded: Bool
  let expandedHeight: CGFloat
  @State private var partialDataBatch: DownloadBatch?
  @State private var deletingBatch: DownloadBatch?

  private var visibleItems: [DownloadItem] { coordinator.items }

  private var totalBytes: Int64 { visibleItems.reduce(0) { $0 + $1.totalBytes } }
  private var completedBytes: Int64 { visibleItems.reduce(0) { $0 + $1.completedBytes } }
  private var speed: Int64 { visibleItems.reduce(0) { $0 + $1.downloadSpeed } }
  private var progress: Double {
    totalBytes > 0 ? min(1, Double(completedBytes) / Double(totalBytes)) : 0
  }

  var body: some View {
    VStack(spacing: 0) {
      VStack(spacing: 0) {
        HStack(spacing: 10) {
          Button {
            isExpanded.toggle()
          } label: {
            HStack(spacing: 10) {
              Image(
                systemName: visibleItems.isEmpty ? "checkmark.circle" : "arrow.down.circle.fill"
              )
              .foregroundStyle(visibleItems.isEmpty ? .green : .cyan)
              Text("Download Queue")
                .font(.system(size: 13.5, weight: .semibold))
              if !visibleItems.isEmpty {
                Text("\(visibleItems.count) files")
                Text("•")
                Text("\(ByteCountFormatter.string(speed))/s")
                Spacer()
                Text("\(Int(progress * 100))%")
                  .monospacedDigit()
              } else {
                Spacer()
              }
              Image(systemName: isExpanded ? "chevron.down" : "chevron.up")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
          }
          .buttonStyle(.plain)

          Button("Pause", systemImage: "pause.fill") {
            for batch in coordinator.batches where [.active, .queued].contains(batch.status) {
              Task { await coordinator.pause(batch) }
            }
          }
          .disabled(!coordinator.batches.contains { [.active, .queued].contains($0.status) })
          Button("Clear", systemImage: "checkmark.circle") { coordinator.clearCompleted() }
            .disabled(!coordinator.batches.contains { $0.status == .completed })
        }
        .foregroundStyle(.primary)
        .padding(.horizontal, 14)
        .frame(height: 40)
        if !visibleItems.isEmpty {
          ProgressView(value: progress)
            .progressViewStyle(.linear)
            .tint(.cyan)
        }
      }

      if isExpanded {
        Divider()
        if coordinator.batches.isEmpty {
          ContentUnavailableView(
            "Queue Is Clear", systemImage: "checkmark.circle",
            description: Text("New downloads will appear here."))
        } else {
          List(coordinator.batches) { batch in
            DownloadBatchRow(batch: batch, itemCount: coordinator.itemsForBatch(batch).count) {
              Task { await coordinator.pause(batch) }
            } resume: {
              Task { await coordinator.resume(batch) }
            } cancel: {
              Task { await coordinator.cancel(batch) }
            } retry: {
              Task { await coordinator.retry(batch) }
            } reveal: {
              coordinator.reveal(batch)
            } removePartialData: {
              partialDataBatch = batch
            } deleteRecord: {
              deletingBatch = batch
            }
          }
          .scrollContentBackground(.hidden)
        }
      }
    }
    .frame(height: isExpanded ? expandedHeight : 42, alignment: .top)
    .background(.regularMaterial)
    .overlay(alignment: .top) { Divider() }
    .shadow(color: .black.opacity(0.28), radius: 16, y: -5)
    .confirmationDialog(
      "Remove partial download data?",
      isPresented: Binding(
        get: { partialDataBatch != nil },
        set: { if !$0 { partialDataBatch = nil } }
      )
    ) {
      Button("Remove Partial Data", role: .destructive) {
        if let batch = partialDataBatch { Task { await coordinator.removePartialData(batch) } }
        partialDataBatch = nil
      }
    } message: {
      Text("Downloaded partial files and their queue record will be permanently removed.")
    }
    .confirmationDialog(
      "Delete this download from the queue?",
      isPresented: Binding(
        get: { deletingBatch != nil },
        set: { if !$0 { deletingBatch = nil } }
      )
    ) {
      Button("Delete from Queue", role: .destructive) {
        if let batch = deletingBatch { Task { await coordinator.deleteDownloadRecord(batch) } }
        deletingBatch = nil
      }
    } message: {
      Text("The transfer will stop, but completed and partial files will remain on disk.")
    }
  }
}

private struct DownloadBatchRow: View {
  let batch: DownloadBatch
  let itemCount: Int
  let pause: () -> Void
  let resume: () -> Void
  let cancel: () -> Void
  let retry: () -> Void
  let reveal: () -> Void
  let removePartialData: () -> Void
  let deleteRecord: () -> Void

  var body: some View {
    VStack(alignment: .leading, spacing: 8) {
      HStack {
        Image(systemName: statusIcon).foregroundStyle(statusColor)
        Text(batch.title).font(.headline).lineLimit(1)
        Spacer()
        Text(batch.status.rawValue.capitalized).foregroundStyle(.secondary)
      }
      ProgressView(value: batch.progress)
      HStack(spacing: 12) {
        Text(
          "\(ByteCountFormatter.string(batch.completedBytes)) of \(ByteCountFormatter.string(batch.totalBytes))"
        )
        Text("\(ByteCountFormatter.string(batch.downloadSpeed))/s")
        Text("Elapsed \(DurationFormatter.string(batch.elapsed))")
        if let eta = batch.estimatedSecondsRemaining {
          Text("ETA \(DurationFormatter.string(eta))")
        }
        Text("\(itemCount) files")
        Spacer()
        controls
      }
      .font(.caption)
      .foregroundStyle(.secondary)
      if let error = batch.errorMessage { Text(error).font(.caption).foregroundStyle(.red) }
    }
    .padding(.vertical, 8)
  }

  @ViewBuilder private var controls: some View {
    HStack(spacing: 7) {
      switch batch.status {
      case .active, .queued:
        DownloadControlButton("Pause", icon: "pause.fill", action: pause)
        DownloadControlButton("Cancel", icon: "xmark", role: .destructive, action: cancel)
      case .paused, .cancelled:
        DownloadControlButton("Resume", icon: "play.fill", action: resume)
        DownloadControlButton(
          "Remove Files", icon: "trash.slash", role: .destructive, action: removePartialData)
      case .failed:
        DownloadControlButton("Retry", icon: "arrow.clockwise", action: retry)
        DownloadControlButton(
          "Remove Files", icon: "trash.slash", role: .destructive, action: removePartialData)
      case .completed:
        DownloadControlButton("Reveal", icon: "folder", action: reveal)
      case .preparing:
        ProgressView().controlSize(.small)
      }
      if batch.status != .preparing {
        DownloadControlButton(
          "Delete", icon: "trash", role: .destructive, action: deleteRecord)
      }
    }
  }

  private var statusIcon: String {
    switch batch.status {
    case .completed: "checkmark.circle.fill"
    case .failed: "exclamationmark.triangle.fill"
    case .paused: "pause.circle.fill"
    case .cancelled: "xmark.circle"
    default: "arrow.down.circle.fill"
    }
  }

  private var statusColor: Color {
    switch batch.status {
    case .completed: .green
    case .failed: .red
    case .paused, .cancelled: .orange
    default: .accentColor
    }
  }
}

private struct DownloadControlButton: View {
  let title: String
  let icon: String
  let role: ButtonRole?
  let action: () -> Void

  init(
    _ title: String, icon: String, role: ButtonRole? = nil, action: @escaping () -> Void
  ) {
    self.title = title
    self.icon = icon
    self.role = role
    self.action = action
  }

  var body: some View {
    Button(role: role, action: action) {
      Label(title, systemImage: icon)
    }
    .buttonStyle(.bordered)
    .controlSize(.small)
    .foregroundStyle(role == .destructive ? Color.red : Color.primary)
  }
}

struct SettingsView: View {
  @ObservedObject var coordinator: AppCoordinator
  @Bindable var settings: AppSettings

  var body: some View {
    Form {
      MediaSettingsSection(library: coordinator.library, refresh: coordinator.refreshLibraryIndex)
      MyraAppearanceSettingsSection()
      SubtitleSettingsSection()
      DiscoverySettingsSection(store: coordinator.entertainment, player: coordinator.player)
      Section("Downloads") {
        LabeledContent("Directory") {
          HStack {
            Text(settings.downloadDirectory).lineLimit(1).truncationMode(.middle)
            Button("Choose…") { coordinator.chooseDownloadDirectory() }
          }
        }
        LabeledContent("aria2c") {
          TextField("Auto-detect", text: $settings.aria2PathOverride)
            .textFieldStyle(.roundedBorder).frame(minWidth: 280)
        }
        Text("Leave the aria2 path empty to search PATH and common Homebrew locations.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Section("Advanced Performance") {
        Stepper(
          "Simultaneous files: \(settings.concurrentDownloads)",
          value: $settings.concurrentDownloads, in: 1...20)
        Stepper(
          "Connections per file: \(settings.connectionsPerFile)",
          value: $settings.connectionsPerFile, in: 1...16)
        Stepper("Split count: \(settings.splitCount)", value: $settings.splitCount, in: 1...16)
        Stepper("Retry count: \(settings.retryCount)", value: $settings.retryCount, in: 1...20)
        LabeledContent("Per-file speed limit") {
          TextField("0 (unlimited)", text: $settings.speedLimit).frame(width: 150)
        }
        Text("aria2 accepts values such as 0, 500K, or 10M.")
          .font(.caption).foregroundStyle(.secondary)
      }
      HStack {
        Spacer()
        Button("Save Settings") { coordinator.saveSettings() }.buttonStyle(.borderedProminent)
      }
    }
    .formStyle(.grouped)
    .navigationTitle("Settings")
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

private struct HomeToolbarTitle: View {
  @ObservedObject var store: EntertainmentStore
  var body: some View {
    HStack(spacing: 10) {
      Text("Home").font(.headline)
      Text("\(store.catalogue.count.formatted()) titles").font(.caption).foregroundStyle(.secondary)
    }
  }
}

extension ToolbarContent {
  /// A continuous native toolbar owns the material instead of separate title/action capsules.
  @ToolbarContentBuilder func continuousHeaderBackground() -> some ToolbarContent {
    if #available(macOS 26.0, *) {
      self.sharedBackgroundVisibility(.hidden)
    } else {
      self
    }
  }
}

extension View {
  @ViewBuilder func mediaHeaderScrollEffect() -> some View {
    if #available(macOS 26.0, *) {
      self.scrollEdgeEffectStyle(.soft, for: .top).backgroundExtensionEffect()
    } else {
      self
    }
  }
}
