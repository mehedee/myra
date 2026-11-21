#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI
#if os(iOS)
import MobileVLCKit
#else
import VLCKit
#endif

extension Notification.Name {
  /// Posted when the Transfers page asks for the player back. The player model answers it.
  static let myraReturnToPlayer = Notification.Name("Myra.returnToPlayer")
}

/// Decisions about playback outputs that the VLCKit engine can support.
enum PlayerPictureInPictureSupport {
  /// AVKit's Picture in Picture needs AVPlayer or a sample-buffer layer. VLCKit draws into an
  /// NSView, so Myra floats that view in an always-on-top panel instead.
  static let approach =
    "Floating always-on-top panel that takes over the VLC video view; AVKit cannot present VLCKit output."

  /// AirPlay routing through AVRoutePickerView needs an AVPlayer output. The VLC engine has none,
  /// so the AirPlay control is not shown in the player.
  static let airPlayAvailable = false
  static let airPlayUnavailableReason =
    "Playback uses VLCKit, which has no AVPlayer output for AVRoutePickerView."
}

#if os(macOS)
/// A floating, always-on-top panel that hosts the VLC video view while Picture in Picture is on.
@MainActor
final class PlayerPictureInPictureWindow: NSObject, NSWindowDelegate {
  let videoView: VLCVideoView
  /// Called only when the user closes the panel, not when Myra closes it.
  var onUserClosed: (() -> Void)?
  private let panel: NSPanel
  private var closingProgrammatically = false

  init(player: EmbeddedPlayerModel) {
    let frame = NSRect(x: 0, y: 0, width: 480, height: 270)
    videoView = VLCVideoView(frame: frame)
    panel = NSPanel(
      contentRect: frame,
      styleMask: [.titled, .closable, .resizable, .fullSizeContentView, .utilityWindow],
      backing: .buffered, defer: false)
    super.init()
    panel.title = "Myra"
    panel.titleVisibility = .hidden
    panel.titlebarAppearsTransparent = true
    panel.isMovableByWindowBackground = true
    panel.isFloatingPanel = true
    panel.level = .floating
    panel.hidesOnDeactivate = false
    panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    panel.backgroundColor = .black
    panel.contentAspectRatio = NSSize(width: 16, height: 9)
    panel.minSize = NSSize(width: 320, height: 180)
    panel.isReleasedWhenClosed = false
    panel.delegate = self
    panel.setAccessibilityLabel("Picture in Picture")

    let host = NSView(frame: frame)
    host.autoresizingMask = [.width, .height]
    videoView.frame = host.bounds
    videoView.autoresizingMask = [.width, .height]
    host.addSubview(videoView)
    let controls = NSHostingView(
      rootView: PlayerPictureInPictureControls(player: player)
        .environment(\.colorScheme, .dark))
    controls.frame = host.bounds
    controls.autoresizingMask = [.width, .height]
    host.addSubview(controls)
    panel.contentView = host
    panel.center()
  }

  func show() {
    panel.orderFrontRegardless()
  }

  /// Closes the panel without telling the player that the user dismissed it.
  func close() {
    closingProgrammatically = true
    panel.close()
  }

  func windowWillClose(_ notification: Notification) {
    guard !closingProgrammatically else { return }
    onUserClosed?()
  }
}
#endif

/// Transport controls over the floating video: play or pause, return to the main window.
struct PlayerPictureInPictureControls: View {
  @ObservedObject var player: EmbeddedPlayerModel

  var body: some View {
    VStack {
      Spacer(minLength: 0)
      HStack(spacing: 10) {
        Button {
          player.togglePlayback()
        } label: {
          Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
        }
        .accessibilityLabel(player.isPlaying ? "Pause" : "Play")
        .help("Play or pause")
        Spacer(minLength: 0)
        Button {
          player.returnFromPictureInPicture()
        } label: {
          Image(systemName: "arrow.up.left.and.arrow.down.right")
        }
        .accessibilityLabel("Return to player")
        .help("Return to the player window")
      }
      .buttonStyle(PlayerGlyphStyle(size: 30))
      .padding(8)
      .playerGlass(radius: 12)
      .padding(10)
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}
