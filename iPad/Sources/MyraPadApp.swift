import AVFoundation
import CoreSpotlight
import SwiftData
import SwiftUI
import UIKit

extension Notification.Name {
  /// Posted by the sidebar's Settings item. The app root presents Settings as a sheet.
  static let myraShowSettings = Notification.Name("Myra.showSettings")
}

final class MyraPadAppDelegate: NSObject, UIApplicationDelegate {
  var terminationHandler: (() -> Void)?

  func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
  ) -> Bool {
    // Playback category: sound plays with the silent switch on and continues in the background.
    try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
    return true
  }

  /// iPadOS wakes Myra when background downloads finish while it is not running.
  func application(
    _ application: UIApplication, handleEventsForBackgroundURLSession identifier: String,
    completionHandler: @escaping () -> Void
  ) {
    guard identifier == Aria2Controller.backgroundSessionIdentifier else {
      completionHandler()
      return
    }
    MainActor.assumeIsolated { Aria2Controller.backgroundCompletionHandler = completionHandler }
  }

  func applicationWillTerminate(_ application: UIApplication) {
    terminationHandler?()
  }
}

@main
struct MyraPadApp: App {
  private let container: ModelContainer
  @StateObject private var coordinator = AppCoordinator()
  @UIApplicationDelegateAdaptor(MyraPadAppDelegate.self) private var appDelegate
  @State private var showingSettings = false

  init() {
    do {
      container = try AppPersistence.makePersistentContainer()
    } catch {
      fatalError("Unable to initialize Myra data: \(error.localizedDescription)")
    }
  }

  var body: some Scene {
    WindowGroup {
      RootView(coordinator: coordinator)
        .modelContainer(container)
        .onContinueUserActivity(CSSearchableItemActionType) { activity in
          // A Spotlight result for a title: Myra opens and searches for that title.
          guard
            let identifier = activity.userInfo?[CSSearchableItemActivityIdentifier] as? String,
            let titleID = SpotlightTitleIndex.titleID(from: identifier)
          else { return }
          MyraSystemRouter.shared.post(.openTitle(id: titleID))
        }
        .onReceive(NotificationCenter.default.publisher(for: .myraShowSettings)) { _ in
          showingSettings = true
        }
        .sheet(isPresented: $showingSettings) {
          if let settings = coordinator.settings {
            SettingsView(coordinator: coordinator, settings: settings)
              .modelContainer(container)
          } else {
            ProgressView()
          }
        }
        .modifier(PlayerSystemChrome(player: coordinator.player))
        .onAppear {
          MyraAppearance.apply()
          appDelegate.terminationHandler = { coordinator.terminateImmediately() }
        }
    }
  }
}

/// Hides the status bar during playback and the Home indicator in fullscreen playback.
private struct PlayerSystemChrome: ViewModifier {
  @ObservedObject var player: EmbeddedPlayerModel

  func body(content: Content) -> some View {
    content
      .persistentSystemOverlays(player.isFullscreen ? .hidden : .automatic)
      .statusBarHidden(player.media != nil)
  }
}
