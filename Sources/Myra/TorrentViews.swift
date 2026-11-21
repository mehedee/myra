import SwiftUI
#if os(iOS)
import MobileVLCKit
#else
import VLCKit
#endif

private func byteText(_ value: Int64) -> String {
  ByteCountFormatter.string(fromByteCount: max(0, value), countStyle: .file)
}

/// Transfers page: a Transfers | Search segmented control. Transfers holds the add-link bar,
/// Now Streaming card and Active list; Search is the in-app Torznab search.
struct TorrentsView: View {
  @ObservedObject var controller: TorrentController
  /// The shell's toolbar search; it narrows the Active list.
  var searchText = ""
  /// Opens Settings > Torrents (indexer management). Supplied by the shell.
  var onManageIndexers: (() -> Void)?
  @State private var link = ""
  @State private var removing: TorrentIdentity?

  var body: some View {
    VStack(spacing: 0) {
      HStack {
        SegmentedControl(
          options: [
            SegmentOption(value: TorrentsTab.transfers, title: "Transfers"),
            SegmentOption(value: TorrentsTab.search, title: "Search"),
          ],
          selection: $controller.selectedTab)
        Spacer(minLength: 0)
      }
      .padding(.horizontal, 26)
      .padding(.top, 8)
      .padding(.bottom, 14)
      if controller.selectedTab == .transfers {
        transfersPage
      } else {
        TorrentSearchView(
          controller: controller, model: controller.search, store: controller.indexers,
          onManageIndexers: onManageIndexers)
      }
      if let notice = controller.notice {
        HStack {
          Image(systemName: "checkmark.circle").foregroundStyle(.green)
          Text(notice).font(.callout)
          Spacer()
          Button {
            controller.notice = nil
          } label: {
            Image(systemName: "xmark")
          }.buttonStyle(.plain).accessibilityLabel("Dismiss notice")
        }.padding(12).background(.thinMaterial)
      }
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    .dropDestination(for: URL.self) { urls, _ in
      let links = urls.filter { TorrentLinkRouter.route($0) != .ignored }
      links.forEach { controller.handleOpenURL($0) }
      return !links.isEmpty
    }
    .task { await controller.load() }
    .alert(
      "Torrent operation failed",
      isPresented: Binding(
        get: { controller.error != nil }, set: { if !$0 { controller.error = nil } })
    ) {
      Button("OK") { controller.error = nil }
    } message: {
      Text(controller.error ?? "")
    }
    .confirmationDialog(
      "Remove torrent and cached files?",
      isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }),
      titleVisibility: .visible
    ) {
      Button("Remove Cached Files", role: .destructive) {
        if let id = removing { Task { await controller.remove(id) } }
        removing = nil
      }
      Button("Cancel", role: .cancel) { removing = nil }
    } message: {
      Text(
        "Cancel the transfer and turn off Keep Download first. User library files are never removed."
      )
    }
  }

  /// The dashed add bar (`.addbar`): a link field with the import action, a Continue button, then the privacy hint.
  private var addLinkBar: some View {
    VStack(alignment: .leading, spacing: 0) {
      HStack(spacing: 10) {
        HStack(spacing: 8) {
          Image(systemName: "link")
            .font(.system(size: 14))
            .foregroundStyle(MyraColor.text3)
            .accessibilityHidden(true)
          TextField("Paste a magnet link or drop a .torrent file", text: $link)
            .textFieldStyle(.plain)
            .onSubmit(addLink)
          Button {
            controller.chooseTorrentFile()
          } label: {
            Image(systemName: "doc.badge.plus")
              .font(.system(size: 14))
              .foregroundStyle(MyraColor.text2)
          }
          .buttonStyle(.plain)
          .disabled(!controller.ready)
          .help("Import a .torrent file")
          .accessibilityLabel("Import .torrent…")
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(MyraColor.content, in: RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(MyraColor.line, lineWidth: 1))
        Button("Continue", action: addLink)
          .buttonStyle(MyraButtonStyle(prominent: true))
          .disabled(!controller.ready || link.trimmingCharacters(in: .whitespaces).isEmpty)
          .accessibilityLabel("Add")
      }
      .padding(14)
      .background(MyraColor.fill, in: RoundedRectangle(cornerRadius: 14))
      .overlay(
        RoundedRectangle(cornerRadius: 14)
          .strokeBorder(MyraColor.lineStrong, style: StrokeStyle(lineWidth: 1, dash: [5, 4])))
      Text(
        "Torrent peers and trackers can see your IP address. This is not anonymous. Only play content you have permission to use. Networking starts only when you add a link."
      )
      .font(.system(size: 12))
      .foregroundStyle(MyraColor.text3)
      .padding(.top, 6)
      .padding(.horizontal, 2)
    }
  }

  private func addLink() {
    let value = link.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !value.isEmpty else { return }
    Task {
      await controller.addMagnet(value)
      if controller.error == nil { link = "" }
    }
  }

  private var transfersPage: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 0) {
        addLinkBar
        if let streaming = controller.streamingRow {
          SectionHeader(title: "Now Streaming")
          NowStreamingCard(controller: controller, row: streaming)
        }
        if controller.transferRows.isEmpty {
          ContentUnavailableView(
            "No torrents yet", systemImage: "network",
            description: Text("Paste a magnet link, drop a .torrent file, or discover an open video."))
            .frame(maxWidth: .infinity, minHeight: 220)
        } else {
          let query = searchText.trimmingCharacters(in: .whitespaces)
          let active = controller.transferRows.filter {
            !$0.isStreaming && (query.isEmpty || $0.name.localizedCaseInsensitiveContains(query))
          }
          if !active.isEmpty {
            SectionHeader(title: "Active", count: active.count)
            RowCard(rows: active.map(activeRow))
          }
        }
      }
      .padding(.horizontal, 26)
      .padding(.top, 4)
      .padding(.bottom, 30)
    }
  }

  /// One Active row: the transfer's state, byte progress, speed and peers, with its controls.
  private func activeRow(_ row: TorrentTransferRow) -> RowCardItem {
    let job = controller.jobs.first { $0.id == row.id }
    let identity = try? TorrentIdentity(rawValue: row.id)
    let kept = job?.keepDownload == true
    let fraction = row.bytesTotal > 0 ? min(1, Double(row.bytesDone) / Double(row.bytesTotal)) : 0

    var actions: [RowCardAction] = []
    if job?.selectedFileIndex == nil {
      actions.append(
        RowCardAction(id: "choose", title: "Choose Video…", style: .button) {
          controller.selectingJob = identity
        })
    }
    actions.append(
      RowCardAction(
        id: "pause", title: row.isPaused ? "Resume" : "Pause",
        systemImage: row.isPaused ? "play.fill" : "pause.fill"
      ) {
        guard let identity else { return }
        if row.isPaused {
          Task { await controller.resume(identity) }
        } else {
          controller.pause(identity)
        }
      })
    actions.append(
      RowCardAction(id: "cancel", title: "Cancel", systemImage: "xmark") {
        if let identity { controller.cancel(identity) }
      })
    // A kept torrent cannot be removed until Keep Download is turned off, so its remove control is withheld.
    if !kept {
      actions.append(
        RowCardAction(id: "remove", title: "Remove…", systemImage: "trash") {
          removing = identity
        })
    }

    let status: StatusPill
    if row.isComplete {
      status = StatusPill(text: "Complete", style: .ok)
    } else if row.isPaused {
      status = StatusPill(text: "Paused", style: .warn)
    } else {
      status = StatusPill(text: row.state.isEmpty ? "Finding peers" : row.state, style: .live)
    }

    return RowCardItem(
      id: row.id, title: row.name,
      thumbnailSymbol: row.isComplete ? "checkmark" : "arrow.down",
      thumbnailColors: LibraryPalette.gradient(for: row.name),
      tags: kept ? ["Kept"] : [],
      meta: [
        "\(byteText(row.bytesDone)) of \(byteText(row.bytesTotal))",
        "↓ \(byteText(row.downloadSpeed))/s",
        "\(row.peers) peers",
      ],
      status: status, progress: fraction, actions: actions)
  }
}

/// The Now Streaming panel (`.streaming`): a 16:9 still, the title with a live pill, the buffer bar and the stream stats.
private struct NowStreamingCard: View {
  @ObservedObject var controller: TorrentController
  let row: TorrentTransferRow
  @Environment(\.myraAccent) private var accent
  private var transfer: TorrentTransfer { controller.transfers[row.id] ?? TorrentTransfer() }
  private var identity: TorrentIdentity? { try? TorrentIdentity(rawValue: row.id) }
  private var kept: Bool { controller.jobs.first { $0.id == row.id }?.keepDownload == true }
  private var verified: Double { min(1, max(0, transfer.streamPercent / 100)) }

  var body: some View {
    HStack(alignment: .center, spacing: 18) {
      still
        .frame(width: 220)
      VStack(alignment: .leading, spacing: 0) {
        HStack(spacing: 8) {
          Text(row.name)
            .font(.system(size: 15, weight: .bold))
            .lineLimit(2)
            .help(row.name)
          StatusPill(
            text: row.bufferedSeconds < 5 ? "Buffering" : "Playing",
            style: .live)
        }
        HStack(spacing: 18) {
          kv("State", row.state.isEmpty ? "Starting" : row.state)
          kv("Verified", "\(Int(transfer.streamPercent.rounded()))%")
        }
        .padding(.top, 4)
        .padding(.bottom, 10)
        bufferBar
        HStack(spacing: 18) {
          kv("Buffered ahead", clock(row.bufferedSeconds))
          kv("↓", "\(byteText(row.downloadSpeed))/s")
          kv("Peers", "\(row.peers)")
          kv("Cache", byteText(transfer.downloaded))
        }
        .padding(.top, 8)
        HStack(spacing: 8) {
          Button {
            TorrentPlayerWindow.bringToFront()
          } label: {
            Label("Return to Player", systemImage: "play.fill")
          }
          .buttonStyle(MyraButtonStyle(prominent: true))
          Button {
            guard let identity else { return }
            Task { await controller.setKeep(identity, keep: true) }
          } label: {
            Label(kept ? "Kept" : "Keep a Copy", systemImage: "arrow.down.to.line")
          }
          .buttonStyle(MyraButtonStyle())
          .disabled(kept || identity == nil)
          Text(
            kept
              ? "Kept after watching."
              : "Removed from the cache after watching unless you keep a copy."
          )
          .font(.system(size: 12))
          .foregroundStyle(MyraColor.text2)
          .lineLimit(2)
        }
        .padding(.top, 12)
      }
      .frame(maxWidth: .infinity, alignment: .leading)
    }
    .padding(14)
    .background(MyraColor.content, in: RoundedRectangle(cornerRadius: MyraRadius.panel))
    .overlay(
      RoundedRectangle(cornerRadius: MyraRadius.panel).stroke(MyraColor.line, lineWidth: 1))
  }

  private var still: some View {
    ZStack(alignment: .bottomLeading) {
      RoundedRectangle(cornerRadius: 10)
        .fill(
          LinearGradient(
            colors: LibraryPalette.gradient(for: row.name), startPoint: .topLeading,
            endPoint: .bottomTrailing))
      Text(row.name)
        .font(.system(size: 13, weight: .bold))
        .foregroundStyle(.white)
        .lineLimit(1)
        .padding(10)
    }
    .aspectRatio(16.0 / 9.0, contentMode: .fit)
    .accessibilityHidden(true)
  }

  /// The buffer bar (`.buffer`). The verified part is drawn as the buffered-ahead segment.
  private var bufferBar: some View {
    GeometryReader { geometry in
      ZStack(alignment: .leading) {
        Capsule().fill(MyraColor.fill2)
        Capsule()
          .fill(accent.color.opacity(0.4))
          .frame(width: geometry.size.width * verified)
      }
    }
    .frame(height: 6)
    .clipShape(Capsule())
    .accessibilityElement(children: .ignore)
    .accessibilityLabel("Stream verified")
    .accessibilityValue("\(Int((verified * 100).rounded())) percent")
  }

  private func kv(_ label: String, _ value: String) -> some View {
    (Text(label + " ").foregroundStyle(MyraColor.text2)
      + Text(value).fontWeight(.semibold).foregroundStyle(MyraColor.text))
      .font(.system(size: 12))
      .monospacedDigit()
      .lineLimit(1)
  }

  private func clock(_ seconds: Double) -> String {
    let total = max(0, Int(seconds.rounded()))
    return String(format: "%d:%02d", total / 60, total % 60)
  }
}

/// Opens as soon as a link is added. Metadata keeps resolving while it is open.
struct TorrentAddSheet: View {
  @ObservedObject var controller: TorrentController
  let identity: TorrentIdentity
  @Environment(\.dismiss) private var dismiss
  @State private var chosen: Int?
  @State private var keepAfterWatching = false
  @State private var intent = TorrentStartMode.play

  private var job: TorrentJob? { controller.jobs.first { $0.identity == identity } }
  private var files: [TorrentFile] { TorrentFile.playbackOrder(job?.files ?? []) }
  private var selected: TorrentFile? { files.first { $0.index == chosen } }
  private var state: String { controller.transfers[identity.rawValue]?.state ?? "" }

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text(job?.name ?? "Magnet link").font(.title2.weight(.semibold)).lineLimit(2)
        .help(job?.name ?? "")
      Text(state.isEmpty ? "Resolving metadata…" : state)
        .font(.caption).foregroundStyle(.secondary)
      if files.isEmpty {
        ProgressView("Resolving metadata…")
        Text(
          "Metadata needs reachable peers or trackers. Close this sheet and cancel the transfer if it does not resolve."
        )
        .font(.caption).foregroundStyle(.secondary)
      } else {
        List(files, id: \.index) { file in
          Button {
            chosen = file.index
          } label: {
            HStack {
              VStack(alignment: .leading) {
                Text(file.episode.map { "Season \($0.season) · Episode \($0.number)" } ?? file.name)
                  .lineLimit(1)
                Text(file.path).font(.caption).foregroundStyle(.secondary).lineLimit(1)
              }
              Spacer()
              Text(byteText(file.size)).font(.caption)
              if chosen == file.index {
                Image(systemName: "checkmark").foregroundStyle(.tint)
              }
            }
            .contentShape(Rectangle())
          }
          .buttonStyle(.plain)
          .accessibilityAddTraits(chosen == file.index ? .isSelected : [])
        }
      }
      Toggle("Keep after watching", isOn: $keepAfterWatching)
      Text(
        "Off: the cache for this torrent is deleted when playback closes. Download always keeps the file in the cache until it is saved."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let error = controller.error {
        Text(error).foregroundStyle(.red).font(.caption)
      }
      HStack {
        Button("Close") { dismiss() }
        Spacer()
        Button("Download") { download() }
          .buttonStyle(MyraButtonStyle(prominent: intent == .download))
          .keyboardShortcut(intent == .download ? .defaultAction : nil)
          .disabled(selected == nil || !controller.hasConsent)
        Button("Play Now") { play() }
          .buttonStyle(MyraButtonStyle(prominent: intent == .play))
          .keyboardShortcut(intent == .play ? .defaultAction : nil)
          .disabled(selected == nil || !controller.hasConsent)
      }
    }
    .padding(24)
    .frame(minWidth: 620, minHeight: 420, maxHeight: 600)
    .onAppear {
      // A search result chooses which action leads; the next sheet starts from Play Now again.
      intent = controller.startIntent
      controller.startIntent = .play
      keepAfterWatching = job?.keepDownload ?? false
      chosen = TorrentFile.defaultSelection(in: job?.files ?? [])?.index
    }
    .onChange(of: files.map(\.index)) { _, _ in
      if !files.contains(where: { $0.index == chosen }) {
        chosen = TorrentFile.defaultSelection(in: files)?.index
      }
    }
  }

  private func play() {
    guard let file = selected else { return }
    Task {
      await controller.setKeep(identity, keep: keepAfterWatching)
      if await controller.select(identity, file: file, play: true) { dismiss() }
    }
  }

  private func download() {
    guard let file = selected else { return }
    Task {
      if await controller.select(identity, file: file, play: false, download: true) {
        dismiss()
      }
    }
  }
}

/// One-time privacy consent. Shown when a link is added without consent, or from Settings > Review.
struct TorrentConsentSheet: View {
  @ObservedObject var controller: TorrentController
  @Environment(\.dismiss) private var dismiss

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Label("Torrent networking", systemImage: "network")
        .font(.title2.weight(.semibold))
      Text(
        "Torrent peers and trackers can see your IP address. This is not anonymous. Myra connects to torrent peers only for links you add, never on launch. Only play or download content you have permission to use."
      )
      .fixedSize(horizontal: false, vertical: true)
      if let date = controller.consentGrantedAt {
        Text("Accepted \(date.formatted(date: .abbreviated, time: .shortened)).")
          .font(.caption).foregroundStyle(.secondary)
      } else {
        Text("Not accepted. Networking starts only after you add a link.")
          .font(.caption).foregroundStyle(.secondary)
      }
      HStack {
        if controller.hasConsent {
          Button("Withdraw Consent", role: .destructive) {
            controller.withdrawConsent()
            dismiss()
          }
          Spacer()
          Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
        } else {
          Button("Not Now") {
            controller.declineConsent()
            dismiss()
          }
          Spacer()
          Button("Allow and Continue") {
            controller.grantConsent()
            dismiss()
          }
          .keyboardShortcut(.defaultAction)
        }
      }
    }
    .padding(24)
    .frame(width: 480)
  }
}

/// Brings the playback window forward. The player owns its window; this only locates it.
@MainActor
enum TorrentPlayerWindow {
  /// The player model leaves Picture in Picture and raises its window; the window lookup is the fallback.
  static func bringToFront() {
    NotificationCenter.default.post(name: .myraReturnToPlayer, object: nil)
    #if os(macOS)
    NSApp.activate(ignoringOtherApps: true)
    NSApp.windows.first { containsVideo($0.contentView) }?.makeKeyAndOrderFront(nil)
    #endif
  }

  #if os(macOS)
  private static func containsVideo(_ view: NSView?) -> Bool {
    guard let view else { return false }
    return view is VLCVideoView || view.subviews.contains { containsVideo($0) }
  }
  #endif
}

struct TorrentSettingsSection: View {
  @ObservedObject var controller: TorrentController
  @State private var showReview = false
  var body: some View {
    TorrentIndexerSettingsGroup(store: controller.indexers, search: controller.search)
    SettingsGroup(title: "Privacy") {
      SettingsRow(
        title: "Torrent networking",
        detail: controller.consentGrantedAt.map {
          "Agreed \($0.formatted(date: .abbreviated, time: .omitted)). Peers connect only after you add a link."
        } ?? "Not agreed yet. You are asked once, when you add a link."
      ) {
        Button("Review…") { showReview = true }
          .buttonStyle(MyraButtonStyle())
      }
    }
    SettingsGroup(
      title: "Streaming",
      note:
        "The cache is separate from your library and HTTP downloads. Active and kept files are protected. BitTorrent may upload verified pieces while downloading even when seeding is off."
    ) {
      SettingsRow(title: "Stream cache limit") {
        SettingsSelect(
          title: "Stream cache limit", selection: $controller.preferences.cacheLimitGB,
          options: SettingsChoices.numbers(
            [5, 10, 20, 50, 100, 200, 500], current: controller.preferences.cacheLimitGB,
            title: { "\($0) GB" }))
      }
      SettingsToggleRow(
        title: "Seed after download", detail: "Off by default",
        isOn: $controller.preferences.seedAfterCompletion)
      SettingsRow(title: "Upload limit") {
        SettingsSelect(
          title: "Upload limit", selection: $controller.preferences.uploadLimitKB,
          options: SettingsChoices.numbers(
            [0, 100, 250, 500, 1000, 2000, 5000], current: controller.preferences.uploadLimitKB,
            title: { $0 == 0 ? "Unlimited" : "\($0) KB/s" }))
      }
    }
    .onChange(of: controller.preferences) { _, _ in
      guard controller.ready else { return }
      Task { await controller.savePreferences() }
    }
    .sheet(isPresented: $showReview) {
      TorrentConsentSheet(controller: controller)
    }
  }
}
