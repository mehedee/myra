import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct EntertainmentCollectionsView: View {
  @ObservedObject var store: EntertainmentStore
  @Environment(\.dismiss) private var dismiss
  @State private var name = ""
  @State private var renaming: UUID?
  @State private var rename = ""
  @State private var deleting: EntertainmentCollection?

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      HStack {
        Text("Personal Collections").font(.title2.bold())
        Spacer()
        Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
      }
      Text(
        "Organize titles without moving or deleting their source files. Add titles from their card menu or details."
      )
      .foregroundStyle(.secondary)
      HStack {
        TextField("New collection name", text: $name).textFieldStyle(.roundedBorder)
          .onSubmit { create() }
        Button("Create", action: create).disabled(
          name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
      }
      List {
        ForEach(store.personal.collections) { collection in
          HStack {
            if renaming == collection.id {
              TextField("Collection name", text: $rename).textFieldStyle(.roundedBorder)
                .onSubmit { finishRename(collection.id) }
              Button("Save") { finishRename(collection.id) }.disabled(
                rename.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
              Button("Cancel") { renaming = nil }
            } else {
              Label(collection.name, systemImage: "rectangle.stack")
              Spacer()
              Text("\(collection.titleIDs.count) titles").foregroundStyle(.secondary)
              Button("Rename") {
                renaming = collection.id
                rename = collection.name
              }
              Button("Delete", role: .destructive) { deleting = collection }
            }
          }.padding(.vertical, 6)
        }
      }.scrollContentBackground(.hidden)
      Toggle(
        "Notify me when followed series gain new episodes",
        isOn: Binding(
          get: { store.personal.preferences.notifications }, set: { store.setNotifications($0) }))
      Text(
        "Following a show establishes its current episodes as the baseline. Notifications require macOS permission."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let error = store.errorMessage { Text(error).font(.caption).foregroundStyle(.orange) }
    }.padding(24).frame(minWidth: 600, idealWidth: 680, minHeight: 420)
      .alert(
        "Delete collection?",
        isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })
      ) {
        Button("Cancel", role: .cancel) { deleting = nil }
        Button("Delete", role: .destructive) {
          if let deleting { store.deleteCollection(id: deleting.id) }
          deleting = nil
        }
      } message: {
        Text("Only the collection is removed. Its movies and source files remain available.")
      }
  }

  private func create() {
    store.createCollection(name: name)
    name = ""
  }
  private func finishRename(_ id: UUID) {
    store.renameCollection(id: id, name: rename)
    renaming = nil
  }
}

/// Imports are validated and previewed before the user chooses a merge or replacement.
struct EntertainmentTransferView: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject var store: EntertainmentStore
  @Environment(\.dismiss) private var dismiss
  @State private var archive: EntertainmentArchive?
  @State private var replace = false
  @State private var isImporting = false
  @State private var message: String?
  @State private var error: String?

  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      HStack {
        Text("Export / Import").font(.title2.bold())
        Spacer()
        Button("Done") { dismiss() }.keyboardShortcut(.cancelAction).disabled(isImporting)
      }
      Text(
        "Back up your sources, personal library, playback preferences, history, match corrections, and skip markers. API keys, passwords, and machine credentials are excluded. Source URL query parameters are omitted; sources that rely on them need reconnecting after import."
      )
      .foregroundStyle(.secondary)
      HStack {
        Button {
          export()
        } label: {
          Label("Export backup…", systemImage: "square.and.arrow.up")
        }
        Button {
          openImport()
        } label: {
          Label("Choose backup…", systemImage: "square.and.arrow.down")
        }
      }.disabled(isImporting)
      if let archive {
        Divider()
        Text("Import preview").font(.headline)
        ScrollView {
          VStack(alignment: .leading, spacing: 10) {
            Text(
              "\(archive.sources.count) sources • \(archive.personal.watchlist.count) watchlist titles • \(archive.personal.collections.count) collections"
            )
            Text(
              "\(archive.personal.watched.count) watched titles • \(archive.personal.followed.count) followed shows • \(archive.personal.history.count) playback records"
            )
            Text(
              "\(archive.personal.matchCorrections.count) match corrections • \(archive.playerState?.markers.count ?? 0) skip marker configurations"
            )
            ForEach(archive.sources) { source in
              VStack(alignment: .leading, spacing: 3) {
                Text(source.name).font(.headline)
                Text(source.url).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
              }
            }
            if let path = archive.appPreferences?.previousDownloadDirectory {
              Text("Previous download directory: \(path)").font(.caption).textSelection(.enabled)
              Text(
                "This path is informational. Reconnect your download directory in Settings on this Mac; local downloaded files are not included in the backup."
              )
              .font(.caption).foregroundStyle(.orange)
            }
            if let state = archive.playerState {
              let localPaths = state.markers.keys.filter { $0.hasPrefix("file:") }.count
              if localPaths > 0 {
                Text(
                  "\(localPaths) local playback marker keys may need matching paths on this Mac."
                )
                .font(.caption).foregroundStyle(.orange)
              }
            }
          }.frame(maxWidth: .infinity, alignment: .leading)
        }
        Picker("Import behaviour", selection: $replace) {
          Text("Merge with current library").tag(false)
          Text("Replace sources and personal library").tag(true)
        }.pickerStyle(.radioGroup)
        Text(
          replace
            ? "Current sources and personal records will be replaced. A backup is saved before replacement. Downloaded files and credentials are preserved."
            : "Combine lists and collections. Newer playback records win; imported match corrections replace conflicting corrections. Imported playback and app preferences are applied."
        )
        .font(.caption).foregroundStyle(.secondary)
        HStack {
          Button("Cancel preview") { self.archive = nil }
          Spacer()
          if isImporting { ProgressView().controlSize(.small) }
          Button(replace ? "Replace from backup" : "Merge backup") { apply(archive) }
            .buttonStyle(.borderedProminent).disabled(isImporting)
        }
      } else {
        Spacer()
      }
      if let message { Text(message).foregroundStyle(.green).textSelection(.enabled) }
      if let error { Text(error).foregroundStyle(.orange).textSelection(.enabled) }
    }.padding(24).frame(minWidth: 650, idealWidth: 740, minHeight: 480, idealHeight: 640)
  }

  private func export() {
    let panel = NSSavePanel()
    panel.allowedContentTypes = [.json]
    panel.nameFieldStringValue = "Myra-Entertainment-Backup.json"
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      let data = try store.exportArchive(
        sources: coordinator.exportEntertainmentSources(),
        appPreferences: coordinator.exportEntertainmentPreferences())
      try data.write(to: url, options: .atomic)
      error = nil
      message = "Backup saved to \(url.path)."
    } catch { self.error = error.localizedDescription }
  }

  private func openImport() {
    let panel = NSOpenPanel()
    panel.allowedContentTypes = [.json]
    panel.canChooseDirectories = false
    panel.allowsMultipleSelection = false
    guard panel.runModal() == .OK, let url = panel.url else { return }
    do {
      let values = try url.resourceValues(forKeys: [.fileSizeKey])
      guard (values.fileSize ?? 0) <= 20_000_000 else {
        throw DiscoveryError.invalidImport("Backup exceeds 20 MB.")
      }
      archive = try store.previewImport(Data(contentsOf: url))
      replace = false
      message = nil
      error = nil
    } catch {
      self.error = error.localizedDescription
      archive = nil
    }
  }

  private func apply(_ archive: EntertainmentArchive) {
    isImporting = true
    error = nil
    Task {
      do {
        try await coordinator.importEntertainmentArchive(archive, replace: replace)
        self.archive = nil
        message = "Import complete. Reconnect any local paths in Settings."
      } catch { self.error = error.localizedDescription }
      isImporting = false
    }
  }
}
