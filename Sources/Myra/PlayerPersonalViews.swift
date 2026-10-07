import SwiftUI

struct PlayerVersionChooser: View {
  @ObservedObject var player: EmbeddedPlayerModel
  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Choose a version").font(.title2)
      Text("Quality and source labels are inferred from filenames.").foregroundStyle(.secondary)
      ForEach(player.versionChoices) { choice in
        Button {
          player.chooseVersion(choice)
        } label: {
          PlayerFileLabel(player: player, choice: choice)
            .frame(maxWidth: .infinity, alignment: .leading).padding(8)
        }
      }
      Button("Cancel") { player.cancelVersionChoice() }.keyboardShortcut(.cancelAction)
    }.padding(24).frame(width: 580)
  }
}

struct PlayerEpisodeBrowser: View {
  @ObservedObject var player: EmbeddedPlayerModel
  var coordinator: AppCoordinator? = nil
  @Environment(\.dismiss) private var dismiss

  private var indexedTitle: EntertainmentTitle? {
    guard let id = player.media?.entry.url.absoluteString else { return nil }
    return coordinator?.entertainment.catalogue.first { $0.versions.contains { $0.id == id } }
  }
  private var versions: [EntertainmentVersion] {
    if let title = indexedTitle { return title.versions }
    return (player.sequence?.groups ?? []).flatMap { $0 }.map { media in
      let identity = MediaIdentity.parse(filename: media.entry.name)
      return EntertainmentVersion(
        media: media, firstDiscovered: .distantPast,
        season: identity.season.flatMap(Int.init), episode: identity.episode.flatMap(Int.init))
    }
  }

  var body: some View {
    VStack(alignment: .leading, spacing: 14) {
      HStack {
        VStack(alignment: .leading, spacing: 4) {
          Text(versions.contains { $0.episode != nil } ? "Choose an episode" : "Choose a file")
            .font(.title2.bold())
          Text(indexedTitle?.displayName ?? "Files in this folder").foregroundStyle(.secondary)
        }
        Spacer()
        Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
      }
      SeasonEpisodeList(
        versions: versions, isSeries: versions.contains { $0.episode != nil },
        currentID: player.media?.entry.url.absoluteString,
        lastPlayedID: indexedTitle?.resumeVersion?.id
      ) { version in
        dismiss()
        Task { @MainActor in
          try? await Task.sleep(for: .milliseconds(250))
          if let coordinator {
            if version.progressSeconds >= 5 {
              coordinator.resumeVersion(version)
            } else {
              coordinator.playChosenVersion(version.media)
            }
          } else {
            player.requestPlayback([version.media])
          }
        }
      }
    }.padding(24).frame(minWidth: 640, idealWidth: 750, minHeight: 340, idealHeight: 520)
  }
}

struct PlayerPreferencesView: View {
  @ObservedObject var player: EmbeddedPlayerModel
  @Environment(\.dismiss) private var dismiss
  private let languages = [
    ("default", "Source default"), ("en", "English"), ("bn", "Bengali"), ("hi", "Hindi"),
    ("fr", "French"), ("es", "Spanish"), ("ja", "Japanese"), ("ko", "Korean"),
  ]
  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Playback preferences").font(.title2)
      Text("Apply to future playback. Manual track choices remain active for the current video.")
        .foregroundStyle(.secondary)
      Form {
        Picker("Audio language", selection: $player.personalState.audioLanguage) {
          ForEach(languages, id: \.0) { Text($0.1).tag($0.0) }
        }
        Picker("Subtitle language", selection: $player.personalState.subtitleLanguage) {
          Text("Disabled").tag("off")
          ForEach(languages, id: \.0) { Text($0.1).tag($0.0) }
        }
        Picker(
          "Playback speed", selection: Binding(get: { player.speed }, set: { player.setSpeed($0) })
        ) {
          ForEach([Float(0.5), 0.75, 1, 1.25, 1.5, 2], id: \.self) {
            Text(String(format: "%.2g×", $0)).tag($0)
          }
        }
        Toggle("Automatically play the next video", isOn: $player.personalState.autoplay)
        Toggle(
          "Automatically skip configured intros / outros",
          isOn: $player.personalState.automaticSkipping)
      }
      Button("Done") {
        player.savePersonalState()
        dismiss()
      }.keyboardShortcut(.defaultAction)
    }.padding(24).frame(width: 520)
      .onDisappear { player.savePersonalState() }
  }
}

struct PlayerMarkersView: View {
  @ObservedObject var player: EmbeddedPlayerModel
  @Environment(\.dismiss) private var dismiss
  @State private var intro = false
  @State private var outro = false
  @State private var introStart = 0.0
  @State private var introEnd = 60.0
  @State private var outroStart = 0.0
  @State private var outroEnd = 0.0
  @State private var series = false
  @State private var invalid = false
  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Intro / outro markers").font(.title2)
      Text("Enter seconds. Episode overrides take priority over series markers.").foregroundStyle(
        .secondary)
      Form {
        Toggle("Intro", isOn: $intro)
        HStack {
          TextField("Start", value: $introStart, format: .number)
          TextField("End", value: $introEnd, format: .number)
        }.disabled(!intro)
        Toggle("Outro", isOn: $outro)
        HStack {
          TextField("Start", value: $outroStart, format: .number)
          TextField("End", value: $outroEnd, format: .number)
        }.disabled(!outro)
        if player.media.map({ MediaIdentity.parse(filename: $0.entry.name).episode != nil }) == true
        {
          Toggle("Use for all episodes of this series", isOn: $series)
        }
      }
      if invalid {
        Text(
          "Each enabled range must start at zero or later and end after its start, within this video's duration."
        ).foregroundStyle(.red)
      }
      HStack {
        Button("Clear") {
          player.clearMarkers(series: series)
          dismiss()
        }
        Spacer()
        Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
        Button("Save") {
          invalid = !player.setMarkers(
            PlayerSkipMarkers(
              intro: intro ? .init(start: introStart, end: introEnd) : nil,
              outro: outro ? .init(start: outroStart, end: outroEnd) : nil), series: series)
          if !invalid { dismiss() }
        }.keyboardShortcut(.defaultAction)
      }
    }.padding(24).frame(width: 520)
      .onAppear {
        let markers = player.currentMarkers
        intro = markers.intro != nil
        outro = markers.outro != nil
        introStart = markers.intro?.start ?? 0
        introEnd = markers.intro?.end ?? min(60, player.duration)
        outroStart = markers.outro?.start ?? max(0, player.duration - 60)
        outroEnd = markers.outro?.end ?? player.duration
      }
  }
}

private struct PlayerFileLabel: View {
  @ObservedObject var player: EmbeddedPlayerModel
  let choice: GlobalSearchResult
  @State private var progress: Double?
  var body: some View {
    VStack(alignment: .leading, spacing: 3) {
      Text(choice.entry.name).lineLimit(2)
      Text(
        choice.categoryName + " · "
          + (choice.entry.size.map {
            ByteCountFormatter.string(fromByteCount: $0, countStyle: .file)
          } ?? "Size unknown")
      )
      .font(.caption).foregroundStyle(.secondary)
      if let progress {
        Label("Resume at " + PlayerTime.string(progress), systemImage: "clock").font(.caption)
          .foregroundStyle(.secondary)
      }
    }.task(id: choice.id) { progress = await player.savedPosition(for: choice) }
  }
}
