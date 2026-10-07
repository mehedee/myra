import SwiftUI

struct IndexManagementView: View {
  @ObservedObject var library: LibraryController
  @Environment(\.dismiss) private var dismiss
  @State private var selected: Set<String> = []
  @State private var expanded: Set<String> = []
  @State private var confirmFull = false

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      HStack {
        VStack(alignment: .leading) {
          Text("Index Management").font(.title2.bold())
          Text("Choose folders to refresh. Manual-only folders stay in your library.")
            .font(.callout).foregroundStyle(.secondary)
        }
        Spacer()
        Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
      }
      ScrollView {
        LazyVStack(alignment: .leading, spacing: 10) {
          ForEach(library.roots) { root in
            let rootScope = IndexScope(root: root, folder: root.url)
            VStack(alignment: .leading, spacing: 8) {
              folderRow(
                library.folderRows.first { $0.scope == rootScope }
                  ?? IndexFolderRow(scope: rootScope, checked: nil, files: 0), name: root.name)
              if let failure = library.failures.first(where: { $0.categoryID == root.id }) {
                Text(failure.message).font(.caption).foregroundStyle(.orange)
              }
              DisclosureGroup(
                isExpanded: Binding(
                  get: { expanded.contains(rootScope.id) },
                  set: {
                    if $0 { expanded.insert(rootScope.id) } else { expanded.remove(rootScope.id) }
                  })
              ) {
                folderTree(root: root, parent: root.url)
              } label: {
                Text("Folders").foregroundStyle(.secondary)
              }
            }.padding(12).background(.quaternary, in: RoundedRectangle(cornerRadius: 12))
          }
        }
      }
      HStack {
        Button("Refresh due folders") { library.refreshDue() }.disabled(library.isRefreshing)
        Spacer()
        Button("Full rescan…") { confirmFull = true }.disabled(
          selected.isEmpty || library.isRefreshing)
        Button("Refresh selected") { refresh(full: false) }
          .buttonStyle(.borderedProminent).disabled(selected.isEmpty || library.isRefreshing)
      }
      if library.isRefreshing {
        HStack {
          Text(
            library.isPaused
              ? "Paused — your saved library is available"
              : "Refreshing — your saved library is available")
          Spacer()
          Button(library.isPaused ? "Continue" : "Pause") { library.isPaused.toggle() }
          Button("Cancel") { library.cancel() }
        }.font(.callout)
      }
      Text(
        "Refresh selected includes subfolders and respects manual-only branches. Full rescan checks every folder in the selected scope, including manual-only branches."
      )
      .font(.caption).foregroundStyle(.secondary)
    }.padding(24).frame(minWidth: 800, minHeight: 520)
      .task { await library.loadFolderRows() }
      .confirmationDialog("Fully rescan the selected folders?", isPresented: $confirmFull) {
        Button("Full rescan") { refresh(full: true) }
      } message: {
        Text("Your published library remains available until the scan completes.")
      }
  }

  private func refresh(full: Bool) {
    let scopes = library.folderRows.filter { selected.contains($0.id) }.map(\.scope)
    library.refreshSelected(scopes, full: full)
  }

  private func children(root: GlobalSearchRoot, parent: URL) -> [IndexFolderRow] {
    library.folderRows.filter {
      $0.scope.root.id == root.id && $0.scope.folder != root.url
        && $0.scope.folder.deletingLastPathComponent().path.trimmingCharacters(
          in: CharacterSet(charactersIn: "/"))
          == parent.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }
  }

  private func folderTree(root: GlobalSearchRoot, parent: URL) -> AnyView {
    AnyView(
      VStack(alignment: .leading, spacing: 8) {
        ForEach(children(root: root, parent: parent)) { row in
          VStack(alignment: .leading, spacing: 6) {
            folderRow(row, name: row.scope.folder.lastPathComponent)
            if !children(root: root, parent: row.scope.folder).isEmpty {
              DisclosureGroup("Subfolders") { folderTree(root: root, parent: row.scope.folder) }
            }
          }.padding(.leading, 12)
        }
      })
  }

  private func folderRow(_ row: IndexFolderRow, name: String) -> some View {
    HStack(spacing: 12) {
      Toggle(
        isOn: Binding(
          get: { selected.contains(row.id) },
          set: {
            if $0 { selected.insert(row.id) } else { selected.remove(row.id) }
          })
      ) { Text(name).lineLimit(1) }.toggleStyle(.checkbox)
        .help(row.scope.folder.absoluteString)
      Spacer()
      Text("\(row.files) files").font(.caption).foregroundStyle(.secondary)
      Text(row.checked?.formatted(date: .abbreviated, time: .shortened) ?? "Never checked")
        .font(.caption).foregroundStyle(.secondary)
      Picker(
        "Refresh schedule",
        selection: Binding<IndexSchedule?>(
          get: { library.policies.overrides[row.id] },
          set: { library.setSchedule($0, scope: row.scope) })
      ) {
        Text("Inherit (\(library.policies.schedule(for: row.scope).rawValue))").tag(
          Optional<IndexSchedule>.none)
        ForEach(IndexSchedule.allCases, id: \.self) { Text($0.rawValue).tag(Optional($0)) }
      }.labelsHidden().frame(width: 170)
    }
  }
}
