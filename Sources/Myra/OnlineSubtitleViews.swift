import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class OnlineSubtitleSearchModel: ObservableObject, Identifiable {
  let id = UUID()
  let media: GlobalSearchResult
  @Published var title: String
  @Published var year: String
  @Published var season: String
  @Published var episode: String
  @Published var language = "en"
  @Published var selection: Int?
  @Published private(set) var results: [OnlineSubtitleResult] = []
  @Published private(set) var isBusy = false
  @Published private(set) var isDownloading = false
  @Published private(set) var hasMore = false
  @Published private(set) var message: String?
  @Published private(set) var loaded = false
  @Published private(set) var localSubtitles: [URL] = []
  @Published private(set) var remainingDownloads: Int?
  @Published private(set) var cachedSubtitles: [URL] = []
  private let load: (URL) -> Bool
  private let service: OpenSubtitlesService
  private var task: Task<Void, Never>?
  private var requestID = UUID()
  private var page = 0
  private var submitted: MediaIdentity?
  private var submittedLanguage = "en"

  init(
    media: GlobalSearchResult, service: OpenSubtitlesService = .shared,
    load: @escaping (URL) -> Bool
  ) {
    self.media = media
    self.service = service
    self.load = load
    let identity = MediaIdentity.parse(filename: media.entry.name)
    title = identity.title
    year = identity.year ?? ""
    season = identity.season ?? ""
    episode = identity.episode ?? ""
    language = UserDefaults.standard.string(forKey: "subtitlePreferredLanguage") ?? "en"
  }

  func search(more: Bool = false) {
    task?.cancel()
    requestID = UUID()
    let id = requestID
    let identity =
      more
      ? submitted
      : MediaIdentity(
        title: title.trimmingCharacters(in: .whitespacesAndNewlines),
        year: year.isEmpty ? nil : year, season: season.isEmpty ? nil : season,
        episode: episode.isEmpty ? nil : episode)
    guard let identity else { return }
    let language = more ? submittedLanguage : self.language
    let nextPage = more ? page + 1 : 1
    if !more {
      results = []
      selection = nil
      hasMore = false
    }
    submitted = identity
    submittedLanguage = language
    message = nil
    isBusy = true
    isDownloading = false
    task = Task { [weak self] in
      do {
        let credentials = try SubtitleCredentialStore.read()
        guard let self else { return }
        var response = try await self.service.search(
          identity, language: language, page: nextPage, credentials: credentials)
        let fallback = UserDefaults.standard.string(forKey: "subtitleFallbackLanguage") ?? ""
        if response.results.isEmpty, nextPage == 1, !fallback.isEmpty, fallback != language {
          try Task.checkCancellation()
          response = try await self.service.search(
            identity, language: fallback, page: 1, credentials: credentials)
          guard !Task.isCancelled, self.requestID == id else { return }
          self.language = fallback
          self.submittedLanguage = fallback
          self.message = "No results in your first language; showing fallback-language results."
        }
        guard !Task.isCancelled, self.requestID == id else { return }
        var seen = Set(self.results.map(\.id))
        for result in response.results where seen.insert(result.id).inserted {
          self.results.append(result)
        }
        self.page = nextPage
        self.hasMore = response.hasMore
        self.isBusy = false
        if self.results.isEmpty {
          self.message =
            "No subtitles found. Adjust the title, episode or language and search again."
        }
      } catch {
        guard let self, !Task.isCancelled, self.requestID == id else { return }
        self.isBusy = false
        self.message = error.localizedDescription
      }
    }
  }

  func downloadSelected() {
    guard !isBusy, let result = results.first(where: { $0.id == selection }) else { return }
    requestID = UUID()
    let id = requestID
    isBusy = true
    isDownloading = true
    message = nil
    task = Task { [weak self] in
      do {
        let credentials = try SubtitleCredentialStore.read()
        let cache = try SubtitleCache()
        guard let self else { return }
        let download = try await self.service.download(
          result, credentials: credentials, cache: cache)
        guard !Task.isCancelled, self.requestID == id else { return }
        try? await cache.remember(
          fileID: result.id, identity: MediaIdentity.parse(filename: self.media.entry.name))
        guard self.load(download.url) else {
          self.isBusy = false
          self.isDownloading = false
          self.message = "The playing video changed, or VLC could not load this subtitle."
          return
        }
        self.remainingDownloads = download.remaining
        self.loaded = true
        self.isBusy = false
        self.isDownloading = false
      } catch {
        guard let self, !Task.isCancelled, self.requestID == id else { return }
        self.isBusy = false
        self.isDownloading = false
        self.message = error.localizedDescription
      }
    }
  }

  func discoverLocalSubtitles() async {
    if let cache = try? SubtitleCache() {
      cachedSubtitles = await cache.cached(
        identity: MediaIdentity.parse(filename: media.entry.name))
    }
    guard media.entry.url.isFileURL else { return }
    let directory = media.entry.url.deletingLastPathComponent()
    let stem = media.entry.url.deletingPathExtension().lastPathComponent.lowercased()
    let files =
      (try? FileManager.default.contentsOfDirectory(
        at: directory,
        includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey])) ?? []
    localSubtitles = files.filter { file in
      guard ["srt", "ass", "ssa", "vtt"].contains(file.pathExtension.lowercased()),
        file.deletingPathExtension().lastPathComponent.lowercased().hasPrefix(stem),
        let attributes = try? file.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
        attributes.isRegularFile == true, let size = attributes.fileSize,
        size > 0, size <= SubtitleCache.maximumFileBytes
      else { return false }
      return true
    }.sorted { $0.lastPathComponent < $1.lastPathComponent }
  }

  func useLocal(_ url: URL) {
    guard url.isFileURL,
      ["srt", "ass", "ssa", "vtt"].contains(url.pathExtension.lowercased()),
      let attributes = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
      attributes.isRegularFile == true, let size = attributes.fileSize,
      size > 0, size <= SubtitleCache.maximumFileBytes
    else {
      message = "Choose a supported subtitle file smaller than 5 MB."
      return
    }
    if load(url) {
      loaded = true
    } else {
      message = "The video changed or this subtitle could not be loaded."
    }
  }

  func importSubtitle() {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = false
    panel.allowsMultipleSelection = false
    panel.allowedContentTypes = ["srt", "ass", "ssa", "vtt"].compactMap {
      UTType(filenameExtension: $0)
    }
    panel.begin { [weak self] result in
      guard result == .OK, let url = panel.url else { return }
      Task { @MainActor in self?.useLocal(url) }
    }
  }

  var webSearchURL: URL {
    var components = URLComponents(string: "https://www.opensubtitles.org/en/search2")!
    let terms = [
      title, year, season.isEmpty ? "" : "S" + season,
      episode.isEmpty ? "" : "E" + episode,
    ].filter { !$0.isEmpty }.joined(separator: " ")
    components.queryItems = [URLQueryItem(name: "MovieName", value: terms)]
    return components.url!
  }

  func cancel() {
    task?.cancel()
    task = nil
    requestID = UUID()
    isBusy = false
    isDownloading = false
  }
}

struct OnlineSubtitleSearchView: View {
  @ObservedObject var model: OnlineSubtitleSearchModel
  @Environment(\.dismiss) private var dismiss

  var body: some View {
    VStack(alignment: .leading, spacing: 14) {
      Text("Find Subtitles").font(.title2.bold())
      Text(model.media.entry.name).font(.caption).foregroundStyle(.secondary)
        .lineLimit(1).help(model.media.entry.name)
      Text(
        "First check the player's embedded subtitle tracks. Local files and previously downloaded subtitles need no account or paid subscription."
      )
      .font(.caption).foregroundStyle(.secondary)
      HStack {
        Button("Import Subtitle…") { model.importSubtitle() }
        Link("Find on the Web", destination: model.webSearchURL)
      }
      if !model.localSubtitles.isEmpty {
        Menu("Nearby Subtitle Files") {
          ForEach(model.localSubtitles, id: \.self) { url in
            Button(url.lastPathComponent) { model.useLocal(url) }
          }
        }
      }
      if !model.cachedSubtitles.isEmpty {
        Menu("Cached Subtitles for This Title") {
          ForEach(model.cachedSubtitles, id: \.self) { url in
            Button(url.lastPathComponent) { model.useLocal(url) }
          }
        }
      }
      Divider()
      Text("Optional Free-Account Search").font(.headline)
      TextField("Movie or series title", text: $model.title).textFieldStyle(.roundedBorder)
        .onSubmit { model.search() }
      HStack {
        TextField("Year", text: $model.year).frame(width: 70)
        TextField("Season", text: $model.season).frame(width: 70)
        TextField("Episode", text: $model.episode).frame(width: 70)
        Picker("Language", selection: $model.language) {
          Text("English").tag("en")
          Text("Bengali").tag("bn")
          Text("Hindi").tag("hi")
          Text("Spanish").tag("es")
          Text("French").tag("fr")
          Text("German").tag("de")
          Text("Arabic").tag("ar")
          Text("Japanese").tag("ja")
          Text("Korean").tag("ko")
          Text("Portuguese (Brazil)").tag("pt-br")
          Text("All languages").tag("")
        }.frame(maxWidth: 230)
        Button("Search") { model.search() }.disabled(model.isBusy)
      }.textFieldStyle(.roundedBorder)
      List(selection: $model.selection) {
        ForEach(model.results) { result in
          VStack(alignment: .leading, spacing: 4) {
            Text(result.release).font(.headline).lineLimit(2).help(result.release)
            Text(result.filename).font(.caption).lineLimit(1).foregroundStyle(.secondary)
            HStack(spacing: 10) {
              Text(
                Locale.current.localizedString(forLanguageCode: result.language) ?? result.language)
              if let fps = result.fps, fps > 0 { Text(String(format: "%.3g fps", fps)) }
              Text("\(result.downloads) downloads")
              if result.trusted { Text("Trusted") }
              if result.hearingImpaired { Text("Hearing impaired") }
            }.font(.caption).foregroundStyle(.secondary)
          }.padding(.vertical, 4).tag(result.id)
        }
      }.frame(minHeight: 220, idealHeight: 300)
      if let remaining = model.remainingDownloads {
        Text("Provider reports \(remaining) downloads remaining.").font(.caption)
      }
      if model.hasMore {
        Button("More Results") { model.search(more: true) }.disabled(model.isBusy)
      }
      if model.isBusy {
        ProgressView(
          model.isDownloading ? "Downloading selected subtitle…" : "Searching OpenSubtitles…"
        )
        .controlSize(.small)
      }
      if let message = model.message { Text(message).font(.callout).foregroundStyle(.secondary) }
      Text(
        "OpenSubtitles.com • Only title/episode information is sent, never your media URL. Downloads use your provider allowance and are cached locally."
      )
      .font(.caption).foregroundStyle(.secondary)
      HStack {
        Link(
          "Subtitle Account / API Key",
          destination: URL(string: "https://www.opensubtitles.com/en/consumers")!)
        Spacer()
        Button("Cancel") {
          model.cancel()
          dismiss()
        }.keyboardShortcut(.cancelAction)
        Button("Download & Use") { model.downloadSelected() }
          .buttonStyle(.borderedProminent).disabled(model.selection == nil || model.isBusy)
      }
    }.padding(22).frame(width: 700)
      .task { await model.discoverLocalSubtitles() }
      .onDisappear { model.cancel() }
      .onChange(of: model.loaded) { _, loaded in if loaded { dismiss() } }
  }
}

struct SubtitleSettingsSection: View {
  @State private var credentials = SubtitleCredentials()
  @AppStorage("subtitlePreferredLanguage") private var preferredLanguage = "en"
  @AppStorage("subtitleFallbackLanguage") private var fallbackLanguage = ""
  @State private var status: String?
  @State private var isSigningIn = false

  var body: some View {
    Section("Free-First Subtitles — Optional OpenSubtitles Account") {
      Picker("Preferred Subtitle Language", selection: $preferredLanguage) {
        Text("English").tag("en")
        Text("Bengali").tag("bn")
        Text("Hindi").tag("hi")
        Text("All Languages").tag("")
      }
      Picker("Fallback Language", selection: $fallbackLanguage) {
        Text("No Automatic Fallback").tag("")
        Text("English").tag("en")
        Text("Bengali").tag("bn")
        Text("Hindi").tag("hi")
      }
      SecureField("API Key", text: $credentials.apiKey)
      TextField("Username (optional)", text: $credentials.username)
      SecureField("Password (optional)", text: $credentials.password)
      HStack {
        Button("Save Subtitle Credentials") {
          do {
            try SubtitleCredentialStore.save(credentials)
            Task { await OpenSubtitlesService.shared.resetSession() }
            status = "Subtitle credentials saved in Keychain."
          } catch { status = error.localizedDescription }
        }
        Button("Save & Sign In") {
          isSigningIn = true
          status = nil
          let submitted = credentials
          Task {
            defer { isSigningIn = false }
            do {
              try SubtitleCredentialStore.save(submitted)
              let allowance = try await OpenSubtitlesService.shared.signIn(submitted)
              status =
                allowance.map { "Signed in. Provider account allowance: \($0) downloads." }
                ?? "Signed in."
            } catch { status = error.localizedDescription }
          }
        }.disabled(
          isSigningIn || credentials.apiKey.isEmpty || credentials.username.isEmpty
            || credentials.password.isEmpty)
        if isSigningIn { ProgressView().controlSize(.small) }
      }
      Link(
        "Get an OpenSubtitles API Key",
        destination: URL(string: "https://www.opensubtitles.com/en/consumers")!)
      Text(
        "No paid subscription is required by Myra. Embedded tracks, nearby files, cached downloads, and manual import remain available without this API. A consumer API key is required for online search; your provider account determines the actual allowance. No automatic downloads."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let status { Text(status).font(.caption).foregroundStyle(.secondary) }
    }.task {
      do { credentials = try SubtitleCredentialStore.read() } catch {
        status = error.localizedDescription
      }
    }
  }
}
