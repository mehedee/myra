import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// An on-demand workspace. Playback and catalogue mutations remain explicit user actions.
struct MyraAIWorkspaceView: View {
  @ObservedObject var store: EntertainmentStore
  @ObservedObject private var ai: MyraAIStore
  let play: (EntertainmentTitle) -> Void
  @Environment(\.dismiss) private var dismiss
  @State private var feature: MyraAIFeature
  @State private var prompt = ""
  @State private var titleQuery = ""
  @State private var selectedIDs: Set<String>
  @State private var episodeID = ""
  @State private var subtitleText: String?
  @State private var subtitleName = ""
  @State private var importingSubtitle = false
  @State private var localMessage: String?
  @State private var appliedActions: Set<String> = []
  @State private var showingSettings = false
  @State private var availableMinutes = 120
  @State private var requestTask: Task<Void, Never>?
  @State private var result: MyraAIResponse?

  init(
    store: EntertainmentStore, initialFeature: MyraAIFeature = .smartPick,
    initialTitle: EntertainmentTitle? = nil, ai: MyraAIStore = .shared,
    play: @escaping (EntertainmentTitle) -> Void
  ) {
    self.store = store
    self.ai = ai
    self.play = play
    _feature = State(initialValue: initialFeature)
    _selectedIDs = State(initialValue: initialTitle.map { [$0.id] } ?? [])
  }

  private var selectedTitles: [EntertainmentTitle] {
    store.catalogue.filter { selectedIDs.contains($0.id) }
  }

  private var matchingTitles: [EntertainmentTitle] {
    var results: [EntertainmentTitle] = []
    for title in store.catalogue {
      if titleQuery.isEmpty || title.displayName.localizedCaseInsensitiveContains(titleQuery) {
        results.append(title)
        if results.count == 60 { break }
      }
    }
    return results
  }

  private var watchedEpisodes: [EntertainmentVersion] {
    guard let title = selectedTitles.first, title.kind == .series else { return [] }
    return title.versions.filter { version in
      guard version.episode != nil else { return false }
      if store.personal.watched.contains(title.id) { return true }
      guard let record = store.personal.history[version.id], record.duration > 0 else {
        return false
      }
      return record.seconds >= record.duration - 10
    }.sorted {
      if $0.season != $1.season { return ($0.season ?? 0) < ($1.season ?? 0) }
      return ($0.episode ?? 0) < ($1.episode ?? 0)
    }
  }

  private var requiresTitles: Bool {
    [.explain, .summary, .similar, .compare, .moodTags, .cleanup, .recap, .translate].contains(
      feature)
  }

  private var canRun: Bool {
    guard !ai.isBusy, ai.settings.provider != .disabled,
      ai.settings.enabledFeatures.contains(feature), !store.catalogue.isEmpty
    else { return false }
    if requiresTitles && selectedIDs.isEmpty { return false }
    if feature == .compare && selectedIDs.count < 2 { return false }
    if feature == .recap && (episodeID.isEmpty || subtitleText == nil) { return false }
    return true
  }

  var body: some View {
    VStack(spacing: 0) {
      HStack {
        Label("Myra Assistant", systemImage: "sparkles").font(.title2.bold())
        Spacer()
        Button("AI Settings") { showingSettings = true }
        Button("Close") {
          ai.cancel()
          dismiss()
        }.keyboardShortcut(.cancelAction)
      }.padding(20)
      Divider()
      HSplitView {
        ScrollView {
          VStack(alignment: .leading, spacing: 16) {
            Picker("What would you like to do?", selection: $feature) {
              ForEach(MyraAIFeature.allCases, id: \.self) { Text($0.title).tag($0) }
            }.disabled(ai.isBusy)
            Text(feature.hint).font(.callout).foregroundStyle(.secondary)
            Text(ai.status).font(.caption).foregroundStyle(.secondary)
            if !ai.settings.enabledFeatures.contains(feature) {
              Text("This feature is disabled. Enable it in AI Settings.").foregroundStyle(.orange)
            }
            TextEditor(text: $prompt).frame(minHeight: 90, maxHeight: 140)
              .padding(6).background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
              .accessibilityLabel("Your request or preferences")
            Text(
              "Describe your mood, constraints, or question. Avoid passwords and private network details."
            )
            .font(.caption).foregroundStyle(.secondary)
            if feature == .plan {
              Stepper(
                "Time available: \(availableMinutes) minutes", value: $availableMinutes,
                in: 15...600, step: 15)
            }
            if requiresTitles || feature == .collection || feature == .action {
              titleSelection
            }
            if feature == .recap { recapControls }
            HStack {
              Button(ai.isBusy ? "Working…" : "Ask Myra") { run() }
                .buttonStyle(.borderedProminent).disabled(!canRun)
                .keyboardShortcut(.return, modifiers: [.command])
              if ai.isBusy {
                ProgressView().controlSize(.small)
                Button("Cancel") {
                  ai.cancel()
                  requestTask?.cancel()
                }
              }
            }
            Text("AI can make mistakes. Availability and playback always come from your library.")
              .font(.caption).foregroundStyle(.secondary)
            if ai.settings.provider == .openAI || ai.settings.provider == .claude {
              Label(
                "This request uses your selected cloud provider and may incur charges.",
                systemImage: "cloud"
              )
              .font(.caption).foregroundStyle(.secondary)
            }
          }.padding(20)
        }.frame(minWidth: 310, idealWidth: 360, maxWidth: 430)
        ScrollView {
          VStack(alignment: .leading, spacing: 18) {
            if let message = localMessage { Text(message).foregroundStyle(.orange) }
            if let message = ai.errorMessage {
              Text(message).foregroundStyle(.orange).textSelection(.enabled)
            }
            if let response = result {
              Text(response.summary).textSelection(.enabled)
              if !response.tags.isEmpty {
                Text("Suggested tags: " + response.tags.joined(separator: " · "))
                  .font(.callout).foregroundStyle(.secondary)
              }
              ForEach(response.recommendations.indices, id: \.self) { offset in
                let recommendation = response.recommendations[offset]
                if let title = store.catalogue.first(where: { $0.id == recommendation.titleID }) {
                  recommendationCard(
                    title, reason: recommendation.reason, versionID: recommendation.versionID)
                }
              }
              if !response.actions.isEmpty {
                Divider()
                Text("Review proposed changes").font(.headline)
                Text("Nothing changes until you choose Apply. Review corrections before saving.")
                  .font(.caption).foregroundStyle(.secondary)
                ForEach(response.actions) { action in actionPreview(action) }
              }
              Text(response.sourceNote).font(.caption).foregroundStyle(.secondary)
              Text("Generated with \(response.provider)").font(.caption).foregroundStyle(.secondary)
              if ai.canUndo {
                Button("Undo Last Library Change") {
                  do {
                    try ai.undo(in: store)
                    appliedActions.removeAll()
                    localMessage = "Change undone."
                  } catch { localMessage = error.localizedDescription }
                }
              }
            } else {
              ContentUnavailableView(
                "Find your next favourite", systemImage: "sparkles",
                description: Text(
                  "Choose a tool and describe what you need. Myra uses your indexed library and available metadata."
                )
              )
              .frame(minHeight: 300)
            }
          }.padding(20).frame(maxWidth: .infinity, alignment: .leading)
        }.frame(minWidth: 430)
      }
    }.frame(minWidth: 840, idealWidth: 1040, minHeight: 600, idealHeight: 740)
      .onAppear { ai.refreshAvailability() }
      .onDisappear {
        requestTask?.cancel()
        ai.cancel()
        subtitleText = nil
      }
      .onChange(of: feature) { _, _ in
        localMessage = nil
        appliedActions.removeAll()
        result = nil
      }
      .onChange(of: episodeID) { _, _ in
        subtitleText = nil
        subtitleName = ""
      }
      .sheet(isPresented: $showingSettings) { MyraAISettingsWindow(ai: ai) }
      .fileImporter(isPresented: $importingSubtitle, allowedContentTypes: [.plainText, .data]) {
        result in
        importSubtitle(result)
      }
  }

  private var titleSelection: some View {
    VStack(alignment: .leading, spacing: 8) {
      Text(feature == .compare ? "Choose two or more titles" : "Choose titles").font(.headline)
      ForEach(selectedTitles) { title in
        HStack {
          Text(title.displayName).lineLimit(1)
          Spacer()
          Button {
            selectedIDs.remove(title.id)
            episodeID = ""
          } label: {
            Image(systemName: "xmark.circle")
          }
          .buttonStyle(.plain).accessibilityLabel("Remove \(title.displayName)")
        }
      }
      TextField("Find a title in your library", text: $titleQuery).textFieldStyle(.roundedBorder)
      ScrollView {
        LazyVStack(alignment: .leading, spacing: 6) {
          ForEach(matchingTitles) { title in
            Button {
              if feature == .recap {
                selectedIDs = [title.id]
                episodeID = ""
              } else if selectedIDs.count < 10 {
                selectedIDs.insert(title.id)
              }
            } label: {
              HStack {
                Text(title.displayName).lineLimit(1)
                if let year = title.year { Text(year).foregroundStyle(.secondary) }
                Spacer()
                if selectedIDs.contains(title.id) { Image(systemName: "checkmark") }
              }
            }.buttonStyle(.plain)
              .disabled(
                (feature == .recap && title.kind != .series) || selectedIDs.contains(title.id))
          }
        }
      }.frame(height: 130)
      Text("Showing up to 60 matches. Select up to 10 titles.").font(.caption).foregroundStyle(
        .secondary)
    }
  }

  private var recapControls: some View {
    VStack(alignment: .leading, spacing: 8) {
      Text("Recap a watched episode").font(.headline)
      Picker("Episode", selection: $episodeID) {
        Text("Select an episode").tag("")
        ForEach(watchedEpisodes) { version in
          Text(
            String(format: "Season %d · Episode %02d", version.season ?? 0, version.episode ?? 0)
          ).tag(version.id)
        }
      }
      if watchedEpisodes.isEmpty {
        Text("Only completed episodes or episodes of a series you marked watched are eligible.")
          .font(.caption).foregroundStyle(.secondary)
      }
      Button("Import This Episode’s Subtitles…") { importingSubtitle = true }.disabled(
        episodeID.isEmpty)
      if !subtitleName.isEmpty { Text(subtitleName).font(.caption).lineLimit(2) }
      Text(
        "Select subtitles for this episode only. Subtitles can contain spoilers; a recap cannot guarantee spoiler-free output. Cloud transmission requires separate permission in AI Settings."
      )
      .font(.caption).foregroundStyle(.secondary)
    }
  }

  private func recommendationCard(
    _ title: EntertainmentTitle, reason: String, versionID: String?
  ) -> some View {
    let playbackTitle = Self.playbackTitle(title, versionID: versionID)
    return HStack(alignment: .top, spacing: 14) {
      AsyncImage(url: title.posterURL) { image in
        image.resizable().scaledToFill()
      } placeholder: {
        Image(systemName: "film.stack").frame(maxWidth: .infinity, maxHeight: .infinity).background(
          .quaternary)
      }.frame(width: 76, height: 108).clipShape(RoundedRectangle(cornerRadius: 8))
      VStack(alignment: .leading, spacing: 8) {
        Text(title.displayName).font(.headline)
        Text(
          [title.year, title.kind.rawValue.capitalized].compactMap { $0 }.joined(separator: " · ")
        )
        .font(.caption).foregroundStyle(.secondary)
        if let versionID, let version = title.versions.first(where: { $0.id == versionID }),
          let episode = version.episode
        {
          Text(String(format: "Season %d · Episode %02d", version.season ?? 0, episode))
            .font(.caption).foregroundStyle(.secondary)
        }
        if let minutes = MyraAIContext.runtime(playbackTitle) {
          Text("Known runtime: \(minutes) minutes").font(.caption).foregroundStyle(.secondary)
        }
        Text(reason).font(.callout).textSelection(.enabled)
        HStack {
          Button(playbackTitle.resumeVersion == nil ? "Play" : "Resume") { play(playbackTitle) }
            .buttonStyle(.borderedProminent)
          Button(
            store.personal.watchlist.contains(title.id)
              ? "Remove from Watchlist" : "Add to Watchlist"
          ) {
            store.toggleWatchlist(title.id)
          }
        }
      }
    }.padding(12).frame(maxWidth: .infinity, alignment: .leading)
      .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
  }

  /// Keeps a timed episode recommendation attached to its validated media selection.
  static func playbackTitle(_ title: EntertainmentTitle, versionID: String?) -> EntertainmentTitle {
    guard let versionID, let version = title.versions.first(where: { $0.id == versionID }) else {
      return title
    }
    var selected = title
    selected.versions = [version]
    return selected
  }

  private func actionPreview(_ action: MyraAIAction) -> some View {
    VStack(alignment: .leading, spacing: 8) {
      Text(action.label).font(.headline)
      if !action.value.isEmpty { Text(action.value).textSelection(.enabled) }
      ForEach(action.titleIDs, id: \.self) { id in
        if let title = store.catalogue.first(where: { $0.id == id }) {
          Text(title.displayName).font(.caption).foregroundStyle(.secondary)
        }
      }
      if let year = action.year { Text("Year: \(year)").font(.caption) }
      if let kind = action.mediaKind { Text("Type: \(kind.rawValue)").font(.caption) }
      Button(appliedActions.contains(action.id) ? "Applied" : "Apply") {
        do {
          if action.kind == .play {
            guard let id = action.titleIDs.first,
              let title = store.catalogue.first(where: { $0.id == id })
            else { return }
            play(title)
          } else if action.kind == .showResults {
            selectedIDs = Set(action.titleIDs)
            localMessage = "Selected matching titles. Use their playback buttons above."
          } else {
            try ai.apply(action, to: store)
            appliedActions.insert(action.id)
            localMessage = "Change saved."
          }
        } catch { localMessage = error.localizedDescription }
      }.disabled(appliedActions.contains(action.id) || ai.isBusy)
    }.padding(12).frame(maxWidth: .infinity, alignment: .leading)
      .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
  }

  private func run() {
    localMessage = nil
    appliedActions.removeAll()
    var request = prompt
    if feature == .plan { request += "\nAvailable time: \(availableMinutes) minutes." }
    if feature == .recap, let version = watchedEpisodes.first(where: { $0.id == episodeID }) {
      request +=
        "\nRecap only Season \(version.season ?? 0), Episode \(version.episode ?? 0). Source: \(subtitleName)."
    }
    result = nil
    requestTask = Task {
      await ai.execute(
        feature: feature, prompt: request, titles: store.catalogue,
        personal: store.personal, revision: store.projectionRevision,
        selectedIDs: selectedIDs.sorted(), subtitleText: feature == .recap ? subtitleText : nil,
        recapVersionID: feature == .recap ? episodeID : nil)
      if !Task.isCancelled { result = ai.response }
    }
  }

  private func importSubtitle(_ result: Result<URL, Error>) {
    do {
      let url = try result.get()
      guard ["srt", "ass", "ssa", "vtt", "txt"].contains(url.pathExtension.lowercased()) else {
        localMessage = "Choose an SRT, ASS, SSA, VTT, or text subtitle file."
        return
      }
      let access = url.startAccessingSecurityScopedResource()
      defer { if access { url.stopAccessingSecurityScopedResource() } }
      let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
      guard ((attributes[.size] as? NSNumber)?.intValue ?? Int.max) <= 2_000_000 else {
        localMessage = "Choose a subtitle file smaller than 2 MB."
        return
      }
      let data = try Data(contentsOf: url)
      guard let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .utf16),
        !text.isEmpty
      else {
        localMessage = "The subtitle file must contain readable UTF-8 or UTF-16 text."
        return
      }
      subtitleText = text
      subtitleName = url.lastPathComponent
      localMessage = "Imported for this request only."
    } catch { localMessage = error.localizedDescription }
  }
}

struct MyraAISettingsWindow: View {
  var ai: MyraAIStore = .shared
  @Environment(\.dismiss) private var dismiss
  var body: some View {
    VStack {
      HStack {
        Text("AI Settings").font(.title2.bold())
        Spacer()
        Button("Done") { dismiss() }
      }
      Form { MyraAISettingsSection(ai: ai) }.formStyle(.grouped)
    }.padding(20).frame(minWidth: 620, minHeight: 600)
  }
}

struct MyraAISettingsSection: View {
  @ObservedObject private var ai: MyraAIStore
  @State private var key = ""
  @State private var message: String?
  @State private var feedback = ""

  init(ai: MyraAIStore = .shared) { self.ai = ai }

  var body: some View {
    Section("Myra AI") {
      Picker("Provider", selection: $ai.settings.provider) {
        ForEach(MyraAIProvider.allCases) { Text($0.title).tag($0) }
      }.onChange(of: ai.settings.provider) { _, _ in
        key = ""
        ai.settings.model = ""
        ai.refreshAvailability()
      }
      Text(ai.status).font(.caption).foregroundStyle(.secondary)
      if ai.settings.provider == .openAI || ai.settings.provider == .claude {
        SecureField("API key", text: $key)
        HStack {
          Button("Save Key") {
            do {
              try ai.saveKey(key)
              key = ""
              message = "Saved in macOS Keychain."
            } catch { message = error.localizedDescription }
          }.disabled(key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
          Button("Remove Key") {
            do {
              try ai.removeKey()
              message = "Key removed."
            } catch { message = error.localizedDescription }
          }
          Button("Test Connection") { Task { await ai.testConnection() } }.disabled(ai.isBusy)
        }
        TextField("Model identifier", text: $ai.settings.model)
        HStack {
          Button("Refresh Models") { Task { await ai.refreshModels() } }.disabled(ai.isBusy)
          if !ai.models.isEmpty {
            Menu("Available Models") {
              ForEach(ai.models, id: \.self) { model in Button(model) { ai.settings.model = model }
              }
            }
          }
        }
        Toggle("Allow requests to this cloud provider", isOn: $ai.settings.cloudConsent)
        Toggle(
          "Include watch-history information in cloud requests", isOn: $ai.settings.shareHistory)
        Toggle(
          "Allow selected subtitle text in cloud recap requests", isOn: $ai.settings.shareSubtitles)
        Text(
          "Cloud requests may incur charges. Compact catalogue metadata and your request are sent to the selected provider. Media URLs, private paths, and network credentials are excluded. Subtitle text is sent only when you explicitly import it and enable permission."
        )
        .font(.caption).foregroundStyle(.secondary)
      }
      Toggle("Use my saved preferences for recommendations", isOn: $ai.settings.personalize)
      TextField("Response language (for example en or bn)", text: $ai.settings.language)
      Stepper(
        "Daily request limit: \(ai.settings.dailyRequestLimit)",
        value: $ai.settings.dailyRequestLimit, in: 1...500)
      Stepper(
        "Maximum response tokens: \(ai.settings.maximumOutputTokens)",
        value: $ai.settings.maximumOutputTokens, in: 256...2000, step: 128)
      Text(
        "Requests today: \(ai.requestsToday) · Estimated input tokens: \(ai.inputTokens) · Estimated output tokens: \(ai.outputTokens)"
      )
      .font(.caption).foregroundStyle(.secondary)
      Text(
        "Limits are local safeguards, not provider billing limits. Token totals are approximate character-based estimates, not provider billing records."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let message { Text(message).font(.caption) }
      if let error = ai.errorMessage { Text(error).font(.caption).foregroundStyle(.orange) }
      Button("Clear AI Cache") {
        ai.clearCache()
        message = "AI cache cleared."
      }
    }
    Section("Personal Preferences") {
      TextEditor(text: $feedback).frame(height: 75)
        .accessibilityLabel("Explicit viewing preferences")
      HStack {
        Button("Save Preferences") {
          ai.saveFeedback(feedback)
          message = "Preferences saved."
        }
        Button("Reset Preferences") {
          feedback = ""
          ai.saveFeedback("")
          message = "Preferences reset."
        }
      }
      Text(
        "Examples: less horror, more mysteries, prefer shorter movies. You control this text; Myra does not silently infer permanent preferences."
      )
      .font(.caption).foregroundStyle(.secondary)
    }
    Section("AI Features") {
      DisclosureGroup("Enabled Tools") {
        ForEach(MyraAIFeature.allCases, id: \.self) { feature in
          Toggle(
            feature.title,
            isOn: Binding(
              get: { ai.settings.enabledFeatures.contains(feature) },
              set: { enabled in
                if enabled {
                  ai.settings.enabledFeatures.insert(feature)
                } else {
                  ai.settings.enabledFeatures.remove(feature)
                }
              }))
        }
      }
    }
    .onAppear {
      feedback = ai.settings.feedback
      ai.refreshAvailability()
    }
  }
}
