#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// The Downloads page: the segmented control and storage line, then either transfers in progress
/// or the completed library grouped by title. Completed local downloads keep missing files visible and unplayable.
struct OfflineLibraryView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var store: EntertainmentStore
  @ObservedObject private var torrents: TorrentController
  @State private var segment: DownloadsSegment = .defaultSegment
  @State private var hideMissing = false
  @State private var choosing: EntertainmentTitle?
  private let localSourceID = UUID(uuidString: "A90C94FE-EA65-4EE6-AEBE-38F6AB267F73")!

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _store = ObservedObject(wrappedValue: coordinator.entertainment)
    _torrents = ObservedObject(wrappedValue: coordinator.torrents)
  }

  private var titles: [EntertainmentTitle] {
    var versions: [EntertainmentVersion] = []
    for item in coordinator.items where item.status == .completed {
      let url = URL(fileURLWithPath: item.destinationPath)
      let entry = DirectoryEntry(
        name: url.lastPathComponent, url: url, kind: .file, size: item.completedBytes)
      guard MediaFileType.isVideo(entry) else { continue }
      let root = url.deletingLastPathComponent()
      let result = GlobalSearchResult(
        categoryID: localSourceID, categoryName: "Downloaded", categoryRoot: root,
        entry: entry, relativePath: item.relativePath, artworkURL: nil)
      let record = store.personal.history[url.absoluteString]
      versions.append(
        EntertainmentVersion(
          media: result, firstDiscovered: .distantPast,
          progressSeconds: record?.seconds ?? 0, duration: record?.duration ?? 0))
    }
    return EntertainmentGrouping.group(versions, corrections: store.personal.matchCorrections)
  }

  /// The shell's toolbar search field.
  private var query: String { coordinator.searchText.trimmingCharacters(in: .whitespaces) }

  private var visible: [EntertainmentTitle] {
    titles.filter { title in
      (query.isEmpty || title.displayName.localizedCaseInsensitiveContains(query)
        || title.versions.contains { $0.media.relativePath.localizedCaseInsensitiveContains(query) })
        && (!hideMissing || title.versions.contains { exists($0) })
    }
  }

  /// Transfers still running: aria2 batches that are not complete, plus unfinished torrents.
  private var inProgressCount: Int {
    DownloadsModel.inProgress(coordinator.batches).count
      + torrents.transferRows.filter { !$0.isComplete }.count
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 0) {
      header
      switch segment {
      case .inProgress:
        DownloadsInProgressView(coordinator: coordinator, torrents: coordinator.torrents)
      case .scheduled:
        ScheduledDownloadsView(queue: coordinator.automaticQueue, query: coordinator.searchText)
      case .library:
        libraryContent
      }
    }
    .padding(.horizontal, 26)
    .padding(.top, 4)
    .padding(.bottom, 30)
    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    .sheet(item: $choosing) { title in
      OfflineVersionChooser(title: title) { version in
        choosing = nil
        coordinator.playLocal(url: version.media.entry.url)
      }
    }
  }

  /// The segmented control on the left, and the free space and save folder on the right (`#dlSeg`).
  private var header: some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack(spacing: 12) {
        SegmentedControl(
          options: [
            SegmentOption(
              value: DownloadsSegment.inProgress, title: "In Progress · \(inProgressCount)"),
            SegmentOption(
              value: DownloadsSegment.scheduled,
              title: "Scheduled · \(coordinator.automaticQueue.pendingCount)"),
            SegmentOption(value: DownloadsSegment.library, title: "Library · \(titles.count)"),
          ],
          selection: $segment)
        .accessibilityLabel("Downloads section")
        Spacer(minLength: 12)
        if let folder = coordinator.settings?.downloadDirectory {
          Text(DownloadsModel.storageSummary(folder: folder))
            .font(.system(size: 13))
            .foregroundStyle(MyraColor.text2)
            .monospacedDigit()
            .lineLimit(1)
        }
      }
    }
    .padding(.top, 8)
    .padding(.bottom, 14)
  }

  private var libraryContent: some View {
    VStack(alignment: .leading, spacing: 0) {
      HStack(spacing: 12) {
        Toggle("Hide missing", isOn: $hideMissing).myraCheckboxToggleStyle()
        Spacer()
      }
      .padding(.bottom, 12)
      Text(
        "Completed video downloads, grouped by title. Playback works without your remote sources."
      )
      .font(.system(size: 12))
      .foregroundStyle(MyraColor.text2)
      .padding(.bottom, 12)
      if visible.isEmpty {
        ContentUnavailableView(
          "No downloaded videos", systemImage: "arrow.down.circle",
          description: Text("Completed video downloads appear here. Try clearing your filters."))
        .frame(maxWidth: .infinity, minHeight: 240)
      } else {
        ScrollView {
          LazyVGrid(
            columns: [GridItem(.adaptive(minimum: 148), spacing: 14, alignment: .top)], spacing: 16
          ) {
            ForEach(visible) { title in
              libraryCard(title)
            }
          }
          .padding(.bottom, 6)
        }
      }
    }
  }

  private func libraryCard(_ title: EntertainmentTitle) -> some View {
    let available = title.versions.filter { exists($0) }
    let missing = title.versions.count - available.count
    let subtitle: String =
      available.isEmpty
      ? "Reconnect its drive or download it again"
      : "\(available.count) available version\(available.count == 1 ? "" : "s")"
        + (missing > 0 ? " • \(missing) missing" : "")
    let rating = title.metadata?.rating ?? 0
    let progress = title.versions.first.flatMap { version -> Double? in
      version.duration > 0 ? min(1, version.progressSeconds / version.duration) : nil
    }
    return Button {
      start(title, available: available)
    } label: {
      MediaCard(
        title: title.displayName, subtitle: subtitle, posterURL: title.posterURL,
        rating: rating > 0 ? rating : nil, progress: progress)
    }
    .buttonStyle(.plain)
    .contextMenu {
      Button("Play") { start(title, available: available) }
        .disabled(available.isEmpty)
      Button("Files and versions") { choosing = title }
    }
    .accessibilityLabel(title.displayName)
    .accessibilityValue(subtitle)
  }

  private func exists(_ version: EntertainmentVersion) -> Bool {
    FileManager.default.fileExists(atPath: version.media.entry.url.path)
  }

  private func start(_ title: EntertainmentTitle, available: [EntertainmentVersion]) {
    // A title with multiple known versions always presents the choice, including missing versions.
    if title.versions.count > 1 {
      choosing = title
    } else if let version = available.first {
      coordinator.playLocal(url: version.media.entry.url)
    }
  }
}

private struct OfflineVersionChooser: View {
  let title: EntertainmentTitle
  let choose: (EntertainmentVersion) -> Void
  @Environment(\.dismiss) private var dismiss
  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      HStack {
        Text(title.displayName).font(.title2.bold())
        Spacer()
        Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
      }
      ScrollView {
        VStack(alignment: .leading, spacing: 12) {
          ForEach(EntertainmentEpisodeGroup.groups(title.versions)) { group in
            if title.kind == .series { Text(group.label).font(.headline) }
            ForEach(group.versions) { version in
              let url = version.media.entry.url
              let exists = FileManager.default.fileExists(atPath: url.path)
              VStack(alignment: .leading, spacing: 6) {
                EntertainmentVersionRow(version: version, lastPlayed: false) { choose(version) }
                  .disabled(!exists)
                HStack {
                  Text(url.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                  Spacer()
                  if exists {
                    Button("Reveal") { PlatformServices.reveal(url) }
                  } else {
                    Label("Missing file", systemImage: "exclamationmark.triangle").foregroundStyle(
                      .orange
                    ).font(.caption)
                  }
                }
              }
            }
          }
        }
      }
    }.padding(24).frame(minWidth: 640, idealWidth: 750, minHeight: 380, idealHeight: 520)
  }
}
