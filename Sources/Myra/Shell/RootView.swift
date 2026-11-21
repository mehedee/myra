#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftData
import SwiftUI

/// Sidebar destinations. Settings lives in the Settings window (⌘,), not the sidebar.
enum AppSection: String, CaseIterable, Identifiable {
  case home
  case watchlist
  case insights
  case sources
  case downloads
  case collections
  case attention
  case transfers

  var id: String { rawValue }

  var title: String {
    switch self {
    case .home: "Home"
    case .watchlist: "Watchlist"
    case .insights: "Insights"
    case .sources: "Sources"
    case .downloads: "Downloads"
    case .collections: "Collections"
    case .attention: "Needs Attention"
    case .transfers: "Transfers"
    }
  }

  var icon: String {
    switch self {
    case .home: "house"
    case .watchlist: "bookmark"
    case .insights: "chart.bar"
    case .sources: "server.rack"
    case .downloads: "arrow.down.circle"
    case .collections: "square.stack"
    case .attention: "exclamationmark.triangle"
    case .transfers: "arrow.left.arrow.right"
    }
  }

  /// The placeholder for the toolbar search field on this page.
  var searchPrompt: String {
    switch self {
    case .home: "Search or ask Myra"
    case .watchlist: "Search watchlist"
    case .insights: "Search titles"
    case .sources: "Search sources"
    case .downloads: "Search downloads"
    case .collections: "Search collections"
    case .attention: "Search unmatched files"
    case .transfers: "Search torrents"
    }
  }
}

/// Counts shown beside sidebar items. The sidebar hides a badge whose count is zero.
enum ShellBadges {
  /// Downloads that are waiting or running. Paused and finished batches are not counted.
  static func downloads(_ batches: [DownloadBatch]) -> Int {
    batches.filter { $0.status == .active || $0.status == .queued }.count
  }

  /// Torrent transfers that are running: not finished and not paused.
  static func transfers(_ rows: [TorrentTransferRow]) -> Int {
    rows.filter { !$0.isComplete && !$0.isPaused }.count
  }
}

/// Text for the sidebar footer's last-scan line.
enum ShellFooterText {
  static func scanned(_ date: Date?, now: Date = .now) -> String {
    guard let date else { return "Not yet scanned" }
    return "Scanned " + RelativeDateTimeFormatter().localizedString(for: date, relativeTo: now)
  }
}

/// The collapsible sidebar's measurements and stored state. Expanded is 232 pt, collapsed is the
/// 78 pt icon rail, and the choice is remembered between launches.
enum SidebarState {
  static let collapsedKey = "myraSidebarCollapsed"
  static let expandedWidth: CGFloat = 232
  static let railWidth: CGFloat = 78

  static func width(collapsed: Bool) -> CGFloat { collapsed ? railWidth : expandedWidth }
}

struct RootView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var player: EmbeddedPlayerModel
  @Environment(\.modelContext) private var modelContext
  @AppStorage(MyraAccent.defaultsKey) private var accentRaw = MyraAccent.default.rawValue
  @AppStorage(SidebarState.collapsedKey) private var sidebarCollapsed = false
  @AppStorage(MyraTranslucency.defaultsKey) private var translucencyRaw = MyraTranslucency
    .default.rawValue
  @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
  @State private var section: AppSection = .home
  /// A See All grid opened from a Home shelf. It replaces Home until Back is pressed.
  @State private var seeAll: SeeAllRequest?
  /// A header action waiting for the page that owns it (new collection, accept confident matches).
  @State private var pageAction: ShellPageAction?
  /// First-run setup, shown only while no library exists.
  @State private var showingSetup = false
  @StateObject private var filters = LibraryFilterModel()
  @FocusState private var searchFocused: Bool
  @ObservedObject private var torrents: TorrentController

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _player = ObservedObject(wrappedValue: coordinator.player)
    _torrents = ObservedObject(wrappedValue: coordinator.torrents)
  }

  private var fullscreenPlayback: Bool { player.media != nil && player.isFullscreen }

  private var accent: MyraAccent { MyraAccent(rawValue: accentRaw) ?? .default }

  private var translucency: MyraTranslucency {
    MyraTranslucency(storedValue: translucencyRaw).resolved(reduceTransparency: reduceTransparency)
  }

  private func toggleSidebar() {
    withAnimation(.easeInOut(duration: 0.22)) { sidebarCollapsed.toggle() }
  }

  private func show(_ target: AppSection) {
    seeAll = nil
    section = target
  }

  private var commandContext: MyraCommandContext {
    MyraCommandContext(coordinator: coordinator, player: player)
  }

  /// Overall progress for the toolbar ring and its popover. Built from controller state only.
  private var downloads: DownloadOverview {
    DownloadOverview.make(batches: coordinator.batches, torrents: torrents.transferRows)
  }

  var body: some View {
    ZStack {
      // A real blur of what is behind the window, then the window colour at the chosen level.
      TranslucentWindowBackground(level: translucency)
        .ignoresSafeArea()
      TranslucentSurface(role: .window)
        .ignoresSafeArea()
      shell
        .opacity(player.showsPlayerInWindow ? 0 : 1)
        .allowsHitTesting(!player.showsPlayerInWindow)
        .accessibilityHidden(player.showsPlayerInWindow)
      if player.showsPlayerInWindow {
        EmbeddedPlayerView(coordinator: coordinator, player: player)
      }
    }
    // The header sits in the top row with the window buttons, so the shell runs under the hidden
    // title bar.
    .ignoresSafeArea(edges: fullscreenPlayback ? .all : .top)
    .sheet(item: $coordinator.playbackChoices) { choices in
      PlaybackChoicesView(
        choices: choices, play: coordinator.playChosenVersion,
        rememberQuality: { coordinator.entertainment.setTitleQuality(titleID: $0, quality: $1) })
    }
    .sheet(isPresented: $showingSetup) {
      FirstRunSetupView(coordinator: coordinator, isPresented: $showingSetup)
    }
    // Torrent sheets live here so a link opened from any section still gets its sheet.
    .sheet(item: $torrents.selectingJob) { identity in
      TorrentAddSheet(controller: torrents, identity: identity)
    }
    .sheet(
      isPresented: Binding(
        get: { torrents.consentRequest != nil },
        set: { if !$0 { torrents.declineConsent() } })
    ) {
      TorrentConsentSheet(controller: torrents)
    }
    .overlay(alignment: .topTrailing) {
      if let message = coordinator.toastMessage, !fullscreenPlayback {
        ToastView(message: message)
          .padding(.top, 12)
          .padding(.trailing, 16)
          .transition(.move(edge: .top).combined(with: .opacity))
      }
    }
    #if os(macOS)
    .background(PlayerWindowAttachment(player: player))
    #endif
    .font(.system(size: 13, weight: .regular, design: .default))
    .environment(\.defaultMinListRowHeight, 42)
    .environment(\.myraAccent, accent)
    .environment(\.myraTranslucency, translucency)
    .tint(accent.color)
    .preferredColorScheme(preferredColorScheme)
    .onChange(of: section, initial: true) { _, value in
      seeAll = nil
      coordinator.currentSection = value
      // Each page has its own search context, as each has its own placeholder.
      coordinator.searchText = ""
    }
    .onReceive(NotificationCenter.default.publisher(for: MyraCommand.notification)) { note in
      guard let command = MyraCommand(notification: note) else { return }
      handle(command)
    }
    .navigationTitle("Myra")
    // The shell draws its own 52pt header and the player draws its own top bar, so the window has
    // no native toolbar.
    #if os(macOS)
    .toolbar(.hidden, for: .windowToolbar)
    #else
    .toolbar(.hidden, for: .navigationBar)
    #endif
  }

  /// Sidebar, header and the active page, laid out as the approved preview window.
  private var shell: some View {
    HStack(spacing: 0) {
      ShellSidebar(
        section: Binding(get: { section }, set: { show($0) }),
        collapsed: sidebarCollapsed,
        toggle: toggleSidebar,
        coordinator: coordinator,
        library: coordinator.library,
        entertainment: coordinator.entertainment,
        torrents: torrents)
      VStack(spacing: 0) {
        header
        pageContent
          .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
          .background(TranslucentSurface(role: .content))
      }
      .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
    .task {
      coordinator.configure(modelContext: modelContext)
      if SetupCheck.needsFirstRun(
        sourceCount: coordinator.categories.count,
        libraryFileExists: SetupCheck.libraryFileExists())
      {
        showingSetup = true
      }
    }
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

  @ViewBuilder private var pageContent: some View {
    switch section {
    case .home:
      if let seeAll {
        SeeAllPage(
          coordinator: coordinator, store: coordinator.entertainment, filters: filters,
          request: seeAll)
      } else {
        HomeView(
          coordinator: coordinator, store: coordinator.entertainment, filters: filters,
          openSeeAll: { seeAll = $0 }, openWatchlist: { show(.watchlist) })
      }
    case .watchlist:
      WatchlistPage(coordinator: coordinator, store: coordinator.entertainment)
    case .insights:
      InsightsPage(store: coordinator.entertainment)
    case .sources:
      LibraryPlayerContainer(coordinator: coordinator)
    case .downloads:
      OfflineLibraryView(coordinator: coordinator)
    case .collections:
      CollectionsPage(
        coordinator: coordinator, store: coordinator.entertainment, pageAction: $pageAction)
    case .attention:
      NeedsAttentionPage(store: coordinator.entertainment, pageAction: $pageAction)
    case .transfers:
      TorrentsView(controller: coordinator.torrents, searchText: coordinator.searchText)
    }
  }

  /// Back (on See All), the title, the page's action pill, the search field, the indexing
  /// indicator and the download button: one compact header on every page.
  private var header: some View {
    HStack(spacing: 10) {
      if let seeAll {
        Button {
          self.seeAll = nil
        } label: {
          HStack(spacing: 4) {
            Image(systemName: "chevron.left").font(.system(size: 12, weight: .semibold))
            Text("Home")
          }
          .foregroundStyle(accent.color)
          .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Back to Home")
        Text(seeAll.name)
          .font(.system(size: 15, weight: .bold))
          .lineLimit(1)
      } else {
        Text(section.title)
          .font(.system(size: 15, weight: .bold))
          .lineLimit(1)
          .fixedSize()
      }
      Spacer(minLength: 12)
      pageActions
      SearchField(
        text: $coordinator.searchText,
        prompt: seeAll == nil ? section.searchPrompt : "Search this category",
        isFocused: $searchFocused)
      HeaderIndexing(
        coordinator: coordinator, library: coordinator.library,
        entertainment: coordinator.entertainment)
      if downloads.hasDownloads {
        DownloadsRingButton(overview: downloads) {
          MyraCommand.showDownloads.post()
        }
      }
    }
    .padding(.leading, 18)
    .padding(.trailing, 14)
    .frame(height: 52)
    .background {
      Rectangle().fill(MyraColor.windowBase.opacity(min(1, translucency.sidebarAlpha + 0.08)))
    }
    .overlay(alignment: .bottom) { Rectangle().fill(MyraColor.line).frame(height: 1) }
    .background(WindowDragArea())
  }

  /// The action pill for the current page. Each button posts the same command as its menu item.
  @ViewBuilder private var pageActions: some View {
    switch section {
    case .home where seeAll != nil:
      EmptyView()
    case .home:
      ToolbarPill {
        commandButton("Pick", systemImage: "dice", .pickSomething)
        commandButton("Ask Myra", systemImage: "sparkles", .askMyra)
      }
    case .watchlist:
      ToolbarPill {
        commandButton("Pick from Watchlist", systemImage: "dice", .pickSomething)
      }
    case .insights:
      EmptyView()
    case .sources:
      ToolbarPill {
        commandButton("Refresh", systemImage: "arrow.clockwise", .refreshLibrary)
        commandButton("Add Source", systemImage: "plus", .addSource, primary: true)
      }
    case .downloads:
      ToolbarPill {
        commandButton("Pause All", systemImage: "pause.fill", .pauseAllDownloads)
        commandButton("Clear Completed", .clearCompletedDownloads)
      }
    case .collections:
      ToolbarPill {
        ToolbarButton(title: "New Collection", systemImage: "plus", primary: true) {
          pageAction = .newCollection
        }
      }
    case .attention:
      ToolbarPill {
        ToolbarButton(title: "Accept All Confident", systemImage: "checkmark", primary: true) {
          pageAction = .acceptConfident
        }
      }
    case .transfers:
      ToolbarPill {
        commandButton("Open Link", systemImage: "link", .openTorrentLink, primary: true)
      }
    }
  }

  private func commandButton(
    _ title: String, systemImage: String? = nil, _ command: MyraCommand, primary: Bool = false
  ) -> some View {
    ToolbarButton(title: title, systemImage: systemImage, primary: primary) {
      command.post()
    }
    .disabled(!command.availability(in: commandContext).isEnabled)
  }

  /// Handles menu commands. Page-owned commands are disabled in the menu until a page handles them.
  private func handle(_ command: MyraCommand) {
    switch command {
    case .showHome: show(.home)
    case .showWatchlist: show(.watchlist)
    case .showInsights: show(.insights)
    case .showSources: show(.sources)
    case .showDownloads: show(.downloads)
    case .showTransfers: show(.transfers)
    case .toggleSidebar: toggleSidebar()
    case .openTorrentLink: show(.transfers)
    case .importTorrentFile:
      show(.transfers)
      coordinator.torrents.chooseTorrentFile()
    case .refreshLibrary:
      coordinator.refreshLibraryIndex()
    case .pauseAllDownloads:
      for batch in coordinator.batches where batch.status == .active {
        Task { await coordinator.pause(batch) }
      }
    case .resumeAllDownloads:
      for batch in coordinator.batches where batch.status == .paused {
        Task { await coordinator.resume(batch) }
      }
    case .clearCompletedDownloads:
      coordinator.clearCompleted()
    case .setAppearance(let mode):
      guard let settings = coordinator.settings else { return }
      coordinator.objectWillChange.send()
      settings.theme = mode
      try? modelContext.save()
    case .togglePlayback:
      guard player.media != nil else { return }
      player.togglePlayback()
    case .skipBack:
      guard player.media != nil else { return }
      player.jump(seconds: -10)
    case .skipForward:
      guard player.media != nil else { return }
      player.jump(seconds: 10)
    case .previousEpisode:
      guard player.media != nil else { return }
      player.navigate(offset: -1)
    case .nextEpisode:
      guard player.media != nil else { return }
      player.navigate(offset: 1)
    case .toggleFullScreen:
      #if os(macOS)
      NSApp.keyWindow?.toggleFullScreen(nil)
      #else
      player.toggleFullscreen()
      #endif
    case .find:
      searchFocused = true
    case .addSource, .pickSomething, .askMyra:
      // Handled by the visible page, which observes the notification. Menu gating keeps these
      // enabled only where such a page is showing.
      break
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
}

/// The header's indexing indicator, bound to the engine through `EngineSeams`.
private struct HeaderIndexing: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var library: LibraryController
  @ObservedObject var entertainment: EntertainmentStore

  var body: some View {
    IndexingIndicator(
      snapshot: IndexingSnapshot.make(
        library: library, catalogueCount: entertainment.catalogue.count,
        categories: coordinator.categories),
      actions: .make(library: library) { coordinator.refreshLibraryIndex() },
      manage: { library.showIndexManagement = true })
  }
}

/// The sidebar: Home, Watchlist, the Library and Torrents groups with count badges, and the index
/// footer. Collapsed, it is a 78 pt icon rail with badge dots and tooltips.
private struct ShellSidebar: View {
  @Binding var section: AppSection
  let collapsed: Bool
  let toggle: () -> Void
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var library: LibraryController
  @ObservedObject var entertainment: EntertainmentStore
  @ObservedObject var torrents: TorrentController

  @Environment(\.myraAccent) private var accent

  private var snapshot: IndexingSnapshot {
    IndexingSnapshot.make(
      library: library, catalogueCount: entertainment.catalogue.count,
      categories: coordinator.categories)
  }

  var body: some View {
    VStack(alignment: collapsed ? .center : .leading, spacing: 2) {
      if collapsed { toggleButton.padding(.bottom, 6) }
      navItem(.home, badge: 0)
      navItem(
        .watchlist,
        badge: WatchlistStore.badgeCount(
          in: entertainment.catalogue, saved: entertainment.personal.watchlist))
      navItem(.insights, badge: 0)
      groupLabel("Library")
      navItem(.sources, badge: coordinator.categories.count)
      navItem(.downloads, badge: ShellBadges.downloads(coordinator.batches))
      navItem(.collections, badge: 0)
      navItem(.attention, badge: 0)
      #if os(macOS)
      groupLabel("Torrents")
      navItem(.transfers, badge: ShellBadges.transfers(torrents.transferRows))
      #endif
      Spacer(minLength: 12)
      #if os(iOS)
      settingsItem
      #endif
      footer
    }
    .padding(.horizontal, collapsed ? 8 : 10)
    // The window buttons sit in the sidebar's top row, as the preview's `.lights` row.
    #if os(macOS)
    .padding(.top, 42)
    #else
    .padding(.top, collapsed ? 12 : 44)
    #endif
    .padding(.bottom, 12)
    .frame(width: SidebarState.width(collapsed: collapsed))
    .frame(maxHeight: .infinity)
    .overlay(alignment: .topTrailing) {
      if !collapsed { toggleButton.padding(.top, 9).padding(.trailing, 8) }
    }
    .background {
      TranslucentSurface(role: .sidebar)
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
    .overlay(RoundedRectangle(cornerRadius: 12).stroke(MyraColor.line, lineWidth: 1))
    .padding(.leading, 8)
    .padding(.vertical, 8)
  }

  private var toggleButton: some View {
    SidebarToggleButton(collapsed: collapsed, action: toggle)
  }

  #if os(iOS)
  /// iPadOS has no Settings window, so the sidebar opens Settings as a sheet.
  private var settingsItem: some View {
    Button {
      NotificationCenter.default.post(name: .myraShowSettings, object: nil)
    } label: {
      HStack(spacing: 9) {
        Image(systemName: "gearshape")
          .font(.system(size: 15))
          .frame(width: 17, height: 17)
          .foregroundStyle(accent.color)
        if !collapsed { Text("Settings") }
        if !collapsed { Spacer(minLength: 0) }
      }
      .font(.system(size: 13))
      .padding(.horizontal, 8)
      .padding(.vertical, 6)
      .frame(maxWidth: .infinity, alignment: collapsed ? .center : .leading)
      .contentShape(RoundedRectangle(cornerRadius: 8))
    }
    .buttonStyle(.plain)
    .accessibilityLabel("Settings")
    .accessibilityIdentifier("sidebar.settings")
  }
  #endif

  private func navItem(_ item: AppSection, badge: Int) -> some View {
    let selected = section == item
    return Button {
      section = item
    } label: {
      Group {
        if collapsed {
          Image(systemName: item.icon)
            .font(.system(size: 16))
            .foregroundStyle(accent.color)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 9)
            .overlay(alignment: .topTrailing) {
              if badge > 0 {
                Circle().fill(accent.color).frame(width: 8, height: 8)
                  .padding(.top, 5).padding(.trailing, 13)
              }
            }
        } else {
          HStack(spacing: 9) {
            Image(systemName: item.icon)
              .font(.system(size: 15))
              .frame(width: 17, height: 17)
              .foregroundStyle(accent.color)
            Text(item.title)
            Spacer(minLength: 0)
            if badge > 0 {
              Text(badge.formatted())
                .font(.system(size: 11))
                .foregroundStyle(MyraColor.text2)
                .monospacedDigit()
            }
          }
          .padding(.horizontal, 8)
          .padding(.vertical, 6)
        }
      }
      .font(.system(size: 13, weight: selected ? .semibold : .regular))
      .frame(maxWidth: .infinity, alignment: .leading)
      .background(selected ? MyraColor.fill2 : .clear, in: RoundedRectangle(cornerRadius: 8))
      .contentShape(RoundedRectangle(cornerRadius: 8))
    }
    .buttonStyle(.plain)
    .help(collapsed ? (badge > 0 ? "\(item.title) · \(badge)" : item.title) : "")
    .accessibilityLabel(badge > 0 ? "\(item.title), \(badge)" : item.title)
    .accessibilityAddTraits(selected ? .isSelected : [])
  }

  @ViewBuilder private func groupLabel(_ title: String) -> some View {
    if collapsed {
      Rectangle().fill(MyraColor.line).frame(height: 1)
        .padding(.horizontal, 6).padding(.vertical, 8)
    } else {
      Text(title)
        .font(.system(size: 11, weight: .semibold))
        .foregroundStyle(MyraColor.text3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.leading, 8)
        .padding(.top, 12)
        .padding(.bottom, 4)
    }
  }

  /// The index footer. Expanded it names the state, the count and the last scan; collapsed it is a
  /// single status icon. The sheets and the alert hang off it so they work in both states.
  private var footer: some View {
    Group {
      if collapsed { miniFooter } else { fullFooter }
    }
    .sheet(isPresented: $library.showIndexManagement) { IndexManagementView(library: library) }
    .alert(
      "Library Index Refresh",
      isPresented: Binding(
        get: { library.completionMessage != nil },
        set: { if !$0 { library.completionMessage = nil } })
    ) {
      Button("OK") { library.completionMessage = nil }
    } message: {
      Text(library.completionMessage ?? "")
    }
  }

  private var miniFooter: some View {
    Image(systemName: snapshot.isRunning ? "arrow.triangle.2.circlepath" : "checkmark")
      .font(.system(size: 15))
      .foregroundStyle(snapshot.isRunning ? accent.color : MyraColor.text2)
      .frame(maxWidth: .infinity, minHeight: 28)
      .help(snapshot.isRunning ? "Indexing library" : "Library up to date")
      .accessibilityLabel(snapshot.isRunning ? "Indexing library" : "Library up to date")
  }

  private var fullFooter: some View {
    VStack(alignment: .leading, spacing: 6) {
      Text(
        snapshot.isRunning
          ? (snapshot.isPaused ? "Indexing paused" : "Indexing library") : "Library up to date"
      )
      .font(.system(size: 12, weight: .bold))
      HStack(spacing: 8) {
        Text(
          snapshot.isRunning
            ? "\(snapshot.videosFound.formatted()) videos found"
            : "\(snapshot.libraryTotal.formatted()) titles")
        Spacer(minLength: 4)
        Text(
          snapshot.isRunning
            ? "\(snapshot.currentSource)/\(max(snapshot.sourceCount, 1)) sources"
            : ShellFooterText.scanned(snapshot.lastScan))
      }
      .font(.system(size: 12))
      .foregroundStyle(MyraColor.text2)
      .monospacedDigit()
      .lineLimit(1)
      if snapshot.isRunning {
        ProgressBarView(fraction: snapshot.fraction)
        Button("Cancel indexing") { library.cancel() }
          .controlSize(.small)
      } else {
        HStack(spacing: 8) {
          Button("Refresh now") { coordinator.refreshLibraryIndex() }
            .controlSize(.small)
          Menu("Manage index") {
            Button("Choose folders…") { library.showIndexManagement = true }
            Button("Full rescan…") { library.showIndexManagement = true }
          }
          .font(.system(size: 11)).menuIndicator(.hidden).fixedSize()
        }
      }
      if !library.isRefreshing, let error = library.errorMessage {
        Text(error).font(.system(size: 11)).foregroundStyle(.orange).lineLimit(2)
      }
      if !library.isRefreshing, library.errorMessage == nil, !library.failures.isEmpty {
        Menu("\(library.failures.count) unavailable") {
          ForEach(library.failures) { failure in
            Text("\(failure.categoryName): \(failure.summary)")
          }
        }
        .font(.system(size: 11)).foregroundStyle(.orange)
        .menuIndicator(.hidden).fixedSize()
      }
    }
    .padding(8)
    .frame(maxWidth: .infinity, alignment: .leading)
    .background(MyraColor.fill, in: RoundedRectangle(cornerRadius: 10))
  }
}

/// The sidebar's collapse button (`.sbtoggle`): 26 pt, with the shortcut in its tooltip.
private struct SidebarToggleButton: View {
  let collapsed: Bool
  let action: () -> Void
  @State private var hovering = false

  var body: some View {
    Button(action: action) {
      Image(systemName: "sidebar.left")
        .font(.system(size: 15))
        .foregroundStyle(hovering ? MyraColor.text : MyraColor.text2)
        .frame(width: 26, height: 26)
        .background(hovering ? MyraColor.fill2 : .clear, in: RoundedRectangle(cornerRadius: 7))
        .contentShape(RoundedRectangle(cornerRadius: 7))
    }
    .buttonStyle(.plain)
    .onHover { hovering = $0 }
    .help(collapsed ? "Show sidebar (⌃⌘S)" : "Collapse sidebar (⌃⌘S)")
    .accessibilityLabel(collapsed ? "Expand sidebar" : "Collapse sidebar")
    .accessibilityIdentifier("sidebar.toggle")
  }
}

#if os(macOS)
/// Lets the header's empty space move and zoom the window, as the hidden title bar would.
struct WindowDragArea: NSViewRepresentable {
  func makeNSView(context: Context) -> NSView { DragView() }
  func updateNSView(_ nsView: NSView, context: Context) {}

  private final class DragView: NSView {
    override var mouseDownCanMoveWindow: Bool { true }

    override func mouseDown(with event: NSEvent) {
      if event.clickCount == 2 {
        window?.performZoom(nil)
      } else {
        window?.performDrag(with: event)
      }
    }
  }
}
#endif

#if os(iOS)
/// iPadOS windows are not dragged by their content, so the header needs no drag area.
struct WindowDragArea: View {
  var body: some View { Color.clear }
}
#endif
