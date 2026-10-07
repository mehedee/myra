import AppKit
import SwiftUI

struct DiscoverySettingsSection: View {
  @ObservedObject var store: EntertainmentStore
  @ObservedObject var player: EmbeddedPlayerModel
  @State private var token = ""
  @State private var message: String?
  @State private var showPreferences = false

  var body: some View {
    MyraAISettingsSection()
    Section("Home Discovery") {
      SecureField("TMDB API Read Access Token", text: $token)
      HStack {
        Button("Save Token") {
          do {
            try TMDBKeyStore.save(token)
            message = token.isEmpty ? "Token removed." : "Token saved in Keychain."
            if !token.isEmpty { Task { await store.enrich() } }
            token = ""
          } catch { message = error.localizedDescription }
        }
        Link(
          "Get a TMDB token", destination: URL(string: "https://www.themoviedb.org/settings/api")!)
      }
      Text(
        "Use the API Read Access Token, rather than the API key. Only title information and provider IDs are sent; source video URLs are excluded."
      )
      .font(.caption).foregroundStyle(.secondary)
      if let message { Text(message).font(.caption) }
      Toggle(
        "Notify me about new episodes of followed series",
        isOn: Binding(
          get: { store.personal.preferences.notifications }, set: { store.setNotifications($0) }))
      Button("Playback Preferences…") { showPreferences = true }
    }
    Section(
      "About Myra "
        + (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
          ?? "3.0.0")
    ) {
      TMDBAttributionView()
    }
    .sheet(isPresented: $showPreferences) { PlayerPreferencesView(player: player) }
  }
}

struct TMDBAttributionView: View {
  private var logo: NSImage? {
    #if SWIFT_PACKAGE
      let url = Bundle.module.url(forResource: "TMDB", withExtension: "png")
    #else
      let url = Bundle.main.url(forResource: "TMDB", withExtension: "png")
    #endif
    return url.flatMap(NSImage.init(contentsOf:))
  }
  var body: some View {
    HStack(spacing: 16) {
      if let logo { Image(nsImage: logo).resizable().scaledToFit().frame(width: 65, height: 65) }
      VStack(alignment: .leading, spacing: 6) {
        Text("This product uses the TMDB API but is not endorsed or certified by TMDB.")
        Link("The Movie Database", destination: URL(string: "https://www.themoviedb.org")!)
        Text("Metadata and images for personal, noncommercial use.").font(.caption).foregroundStyle(
          .secondary)
      }
    }
  }
}
