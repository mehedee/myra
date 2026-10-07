import SwiftUI

struct PlaybackChoices: Identifiable {
  let id = UUID()
  let versions: [EntertainmentVersion]
  let preferredURL: URL
}

struct PlaybackChoicesView: View {
  let choices: PlaybackChoices
  let play: (GlobalSearchResult) -> Void
  @Environment(\.dismiss) private var dismiss

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Choose a version").font(.title2.bold())
      Text("Different releases of the same title are grouped together.")
        .foregroundStyle(.secondary)
      ScrollView {
        VStack(spacing: 10) {
          ForEach(choices.versions) { version in
            Button {
              dismiss()
              play(version.media)
            } label: {
              VStack(alignment: .leading, spacing: 5) {
                HStack {
                  Text(version.media.entry.name).font(.headline)
                  if version.media.entry.url == choices.preferredURL {
                    Text(version.lastPlayed == nil ? "Selected" : "Previously played")
                      .font(.caption).foregroundStyle(.tint)
                  }
                }
                Text(version.quality.isEmpty ? "Quality unknown" : version.quality)
                HStack {
                  Text(version.media.categoryName)
                  if let size = version.media.entry.size {
                    Text(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
                  }
                  if version.progressSeconds > 0 {
                    Text("Resume at \(PlayerTime.string(version.progressSeconds))")
                  }
                }.font(.caption).foregroundStyle(.secondary)
              }
              .frame(maxWidth: .infinity, alignment: .leading).padding(12)
            }.buttonStyle(.bordered)
          }
        }
      }
      HStack {
        Spacer()
        Button("Cancel") { dismiss() }
      }
    }.padding(24).frame(width: 620, height: 420)
  }
}
