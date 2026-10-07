import AppKit
import SwiftUI

/// Groups completed local downloads, while keeping missing files visible and unplayable.
struct OfflineLibraryView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var store: EntertainmentStore
  @State private var query = ""
  @State private var hideMissing = false
  @State private var choosing: EntertainmentTitle?
  private let localSourceID = UUID(uuidString: "A90C94FE-EA65-4EE6-AEBE-38F6AB267F73")!

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _store = ObservedObject(wrappedValue: coordinator.entertainment)
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

  private var visible: [EntertainmentTitle] {
    titles.filter { title in
      (query.isEmpty || title.displayName.localizedCaseInsensitiveContains(query)
        || title.versions.contains { $0.media.relativePath.localizedCaseInsensitiveContains(query) })
        && (!hideMissing || title.versions.contains { exists($0) })
    }
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      HStack {
        Label("Offline Library", systemImage: "externaldrive.fill").font(.largeTitle.bold())
        Spacer()
        Toggle("Hide missing", isOn: $hideMissing).toggleStyle(.checkbox)
      }
      Text(
        "Completed video downloads, grouped by title. Playback works without your remote sources."
      )
      .foregroundStyle(.secondary)
      TextField("Find a downloaded title", text: $query).textFieldStyle(.roundedBorder)
      if visible.isEmpty {
        ContentUnavailableView(
          "No downloaded videos", systemImage: "arrow.down.circle",
          description: Text("Completed video downloads appear here. Try clearing your filters."))
      } else {
        List(visible) { title in
          let available = title.versions.filter { exists($0) }
          let missing = title.versions.count - available.count
          HStack(spacing: 14) {
            Image(
              systemName: available.isEmpty
                ? "exclamationmark.triangle" : (title.kind == .series ? "tv" : "film")
            )
            .font(.title2).foregroundStyle(available.isEmpty ? Color.orange : .accentColor)
            VStack(alignment: .leading, spacing: 4) {
              Text(title.displayName).font(.headline).lineLimit(2)
              Text(
                "\(available.count) available version(s)\(missing > 0 ? " • \(missing) missing" : "")"
              )
              .font(.caption).foregroundStyle(.secondary)
              if available.isEmpty {
                Text("Reconnect its drive or download it again").font(.caption).foregroundStyle(
                  .orange)
              }
            }
            Spacer()
            Button("Files and versions") { choosing = title }
            Button {
              start(title, available: available)
            } label: {
              Label("Play", systemImage: "play.fill")
            }
            .buttonStyle(.borderedProminent).disabled(available.isEmpty)
          }.padding(.vertical, 8)
        }.scrollContentBackground(.hidden)
      }
    }.padding(24)
      .sheet(item: $choosing) { title in
        OfflineVersionChooser(title: title) { version in
          choosing = nil
          coordinator.playLocal(url: version.media.entry.url)
        }
      }
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
                    Button("Reveal") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
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
