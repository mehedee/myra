import SwiftUI

struct MediaInspectorView: View {
  @ObservedObject var model: MediaInspectorModel
  @ObservedObject var library: LibraryController
  let play: (GlobalSearchResult) -> Void
  @State private var correction = ""
  @State private var correctionYear = ""
  @State private var correctionID = ""

  var body: some View {
    ScrollView {
      if let media = model.selected {
        VStack(alignment: .leading, spacing: 16) {
          HStack {
            Text("Information").font(.headline)
            Spacer()
            Button {
              model.close()
            } label: {
              Image(systemName: "xmark")
            }
            .buttonStyle(.plain).help("Close Information")
          }
          Text("Online Information").font(.headline)
          if model.isLoading { ProgressView("Loading from OMDb…") }
          if let error = model.metadataError {
            Text(error).font(.caption).foregroundStyle(.secondary)
            Button("Retry") { model.load(library: library) }
          }
          if let artwork = artwork(media) {
            AsyncImage(url: artwork) { image in
              image.resizable().scaledToFit()
            } placeholder: {
              RoundedRectangle(cornerRadius: 8).fill(.quaternary).frame(height: 150)
            }
            .frame(maxHeight: 240).clipShape(RoundedRectangle(cornerRadius: 8))
          }
          Text(MovieMetadata.available(model.metadata?.title) ?? model.identity.title)
            .font(.title2.bold()).textSelection(.enabled)
          if let rating = MovieMetadata.available(model.metadata?.imdbRating) {
            Label("IMDb \(rating) / 10", systemImage: "star.fill").foregroundStyle(.yellow)
          }
          if let plot = MovieMetadata.available(model.metadata?.plot) {
            Text(plot).textSelection(.enabled)
          }
          Button {
            play(media)
          } label: {
            Label("Play", systemImage: "play.fill")
          }
          .buttonStyle(.borderedProminent)
          if let metadata = model.metadata {
            field("Year", metadata.year)
            field("Released", metadata.released)
            field("Runtime", metadata.runtime)
            field("Genre", metadata.genre)
            field("Director", metadata.director)
            field("Cast", metadata.actors)
            field("Language", metadata.language)
            field("Country", metadata.country)
            field("Rated", metadata.rated)
            field("Awards", metadata.awards)
            if let id = metadata.imdbID,
              id.range(of: "^tt[0-9]{7,10}$", options: .regularExpression) != nil,
              let url = URL(string: "https://www.imdb.com/title/\(id)/")
            {
              Link("View on IMDb", destination: url)
            }
            Text("Metadata via OMDb • CC BY-NC 4.0").font(.caption2).foregroundStyle(.secondary)
          }
          DisclosureGroup("Correct Match") {
            VStack(alignment: .leading, spacing: 8) {
              Text("Automatic filename matches may be incorrect. Enter a title/year or IMDb ID.")
                .font(.caption).foregroundStyle(.secondary)
              TextField("Title", text: $correction)
              TextField("Release year (optional)", text: $correctionYear)
              TextField("IMDb ID, e.g. tt0133093", text: $correctionID)
              Button("Look Up") {
                let year = correctionYear.trimmingCharacters(in: .whitespacesAndNewlines)
                let id = correctionID.trimmingCharacters(in: .whitespacesAndNewlines)
                model.identity = MediaIdentity(
                  title: correction.trimmingCharacters(in: .whitespacesAndNewlines),
                  year: year.isEmpty ? nil : year, imdbID: id.isEmpty ? nil : id)
                model.load(library: library)
              }
              .disabled(
                correction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                  && correctionID.isEmpty)
            }.textFieldStyle(.roundedBorder).padding(.top, 8)
          }
          Divider()
          Text("Local Information").font(.headline)
          field("File name", media.entry.name)
          field("Category", media.categoryName)
          field(
            "Size",
            media.entry.size.map {
              Foundation.ByteCountFormatter.string(fromByteCount: $0, countStyle: .file)
            } ?? "Unavailable")
          field("Duration", model.technicalDetails["Duration"] ?? "Unavailable")
          field("FPS + Resolution", StreamDetailsPresentation.videoFormat(model.technicalDetails))
          if model.isProbing { ProgressView("Inspecting stream…") }
          field("Path", media.relativePath)
          if let modified = media.entry.modifiedAt {
            field("Modified", modified.formatted(date: .abbreviated, time: .shortened))
          }
          ForEach(model.technicalDetails.keys.sorted(), id: \.self) { key in
            if !["Duration", "Resolution", "Frame rate"].contains(key) {
              field(key, model.technicalDetails[key])
            }
          }
          if let error = model.technicalError {
            Text(error).foregroundStyle(.secondary).font(.caption)
          }
        }
        .padding(16)
        .onAppear { resetCorrection() }
        .onChange(of: media.id) { _, _ in resetCorrection() }
      }
    }
    .frame(maxWidth: .infinity)
  }

  @ViewBuilder private func field(_ name: String, _ value: String?) -> some View {
    if let value = MovieMetadata.available(value) {
      VStack(alignment: .leading, spacing: 3) {
        Text(name).font(.caption).foregroundStyle(.secondary)
        Text(value).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
      }
    }
  }
  private func resetCorrection() {
    correction = model.identity.title
    correctionYear = model.identity.year ?? ""
    correctionID = ""
  }
  private func artwork(_ media: GlobalSearchResult) -> URL? {
    if let local = media.artworkURL { return local }
    guard let value = model.metadata?.poster, let url = URL(string: value), url.scheme == "https"
    else { return nil }
    return url
  }
}

enum StreamDetailsPresentation {
  static func videoFormat(_ details: [String: String]) -> String {
    let resolution = details["Resolution"]?.replacingOccurrences(of: "×", with: "x")
      .replacingOccurrences(of: " ", with: "")
    let fps = details["Frame rate"]?.replacingOccurrences(of: "fps", with: "")
      .trimmingCharacters(in: .whitespacesAndNewlines)
    if let resolution, let fps { return "\(resolution) @ \(fps)fps" }
    if let resolution { return resolution }
    if let fps { return "\(fps)fps" }
    return "Unavailable"
  }
}

struct LibraryIndexStatusView: View {
  @ObservedObject var library: LibraryController
  let refresh: () -> Void
  var body: some View {
    HStack(spacing: 10) {
      if library.isRefreshing {
        ProgressView().controlSize(.small)
        Text(
          "Indexing: \(library.progress.sourcesCompleted)/\(library.progress.sourcesTotal) sources • \(library.progress.foldersVisited) folders • \(library.progress.matchesFound) videos"
        )
        Spacer()
        Button("Cancel") { library.cancel() }
      } else {
        Image(
          systemName: library.failures.isEmpty && library.errorMessage == nil
            ? "externaldrive.badge.checkmark" : "exclamationmark.triangle")
        if let error = library.errorMessage {
          Text(error).foregroundStyle(.orange)
        } else if let refreshed = library.lastRefresh {
          Text("Index updated \(refreshed.formatted(date: .abbreviated, time: .shortened))")
        } else {
          Text("Library index has not been refreshed")
        }
        Spacer()
      }
      Menu {
        Button("Refresh due folders", action: refresh)
        Button("Choose folders…") { library.showIndexManagement = true }
        Button("Full rescan…") { library.showIndexManagement = true }
      } label: {
        Image(systemName: "arrow.clockwise")
      }
      .menuIndicator(.hidden)
      .buttonStyle(.borderless)
      .help("Refresh library index")
      .accessibilityLabel("Refresh library index")
      .accessibilityIdentifier("index.refresh")
      if !library.failures.isEmpty {
        Menu("\(library.failures.count) unavailable") {
          ForEach(library.failures) { failure in
            Text("\(failure.categoryName): \(failure.message)")
          }
        }.foregroundStyle(.orange)
      }
    }
    .font(.caption).padding(.horizontal, 14).frame(height: 30)
    .background(.regularMaterial)
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
}

struct MediaSettingsSection: View {
  @ObservedObject var library: LibraryController
  let refresh: () -> Void
  @State private var key = ""
  @State private var status: String?

  var body: some View {
    Section("Media Information") {
      SecureField("OMDb API Key (optional)", text: $key)
      HStack {
        Button("Save API Key") {
          do {
            try OMDbKeyStore.save(key)
            status = key.isEmpty ? "API key removed." : "API key saved in Keychain."
          } catch { status = error.localizedDescription }
        }
        Link("Get an OMDb Key", destination: URL(string: "https://www.omdbapi.com/apikey.aspx")!)
      }
      Text(
        "Only movie title/year or IMDb ID is sent to OMDb. Your media URL is never sent. Personal, noncommercial use."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let status { Text(status).font(.caption).foregroundStyle(.secondary) }
    }
    Section("Library Index") {
      Picker("Indexing impact", selection: $library.indexingMode) {
        ForEach(LibraryController.IndexingMode.allCases, id: \.self) { Text($0.rawValue).tag($0) }
      }
      Text(
        "Balanced scans two folders at a time. Low Impact scans one; playback and Low Power Mode also reduce concurrency."
      )
      .font(.caption).foregroundStyle(.secondary)
      Text(
        "Home loads your saved library first. Only due folders are checked automatically, using their daily, weekly, or manual schedule."
      )
      if let date = library.lastRefresh { LabeledContent("Last refresh", value: date.formatted()) }
      Button("Refresh due folders", action: refresh).disabled(library.isRefreshing)
      Button("Manage folders and schedules…") { library.showIndexManagement = true }
      if library.isRefreshing {
        Text("Indexing. Home updates when the scan finishes; your library stays available.").font(
          .caption)
        Button(library.isPaused ? "Continue Indexing" : "Pause Indexing") {
          library.isPaused.toggle()
        }
        Button("Cancel Indexing") { library.cancel() }
      }
      Text(
        "Manual refresh shows an alert when completed. Unavailable sources retain their previous indexed files."
      )
      .font(.caption).foregroundStyle(.secondary)
    }
    .task {
      do { key = try OMDbKeyStore.read() } catch { status = error.localizedDescription }
    }
  }
}
