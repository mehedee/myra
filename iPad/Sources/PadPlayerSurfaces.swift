import MobileVLCKit
import SwiftUI
import UIKit
import UniformTypeIdentifiers

// iPadOS versions of the three AppKit pieces of `EmbeddedPlayerView`: the video surface, the seek
// bar and the options menus. The rest of the player UI is shared with macOS.

/// The view VLC draws into. Tap toggles the controls; double tap toggles fullscreen.
struct VLCVideoSurface: UIViewRepresentable {
  @ObservedObject var model: EmbeddedPlayerModel

  func makeCoordinator() -> Coordinator { Coordinator(model: model) }

  func makeUIView(context: Context) -> UIView {
    let view = PadVideoView(frame: .zero)
    view.model = model
    view.backgroundColor = .black
    view.isUserInteractionEnabled = true
    let double = UITapGestureRecognizer(
      target: context.coordinator, action: #selector(Coordinator.doubleTap))
    double.numberOfTapsRequired = 2
    let single = UITapGestureRecognizer(
      target: context.coordinator, action: #selector(Coordinator.singleTap))
    single.require(toFail: double)
    view.addGestureRecognizer(double)
    view.addGestureRecognizer(single)
    model.attach(view: view)
    return view
  }

  func updateUIView(_ view: UIView, context: Context) { model.attach(view: view) }

  static func dismantleUIView(_ view: UIView, coordinator: Coordinator) {
    coordinator.model.detach(view: view)
  }

  @MainActor final class Coordinator: NSObject {
    let model: EmbeddedPlayerModel
    init(model: EmbeddedPlayerModel) { self.model = model }
    @objc func singleTap() {
      if model.controlsVisible && model.isFullscreen {
        model.hideControls()
      } else {
        model.noteInteraction()
      }
    }
    @objc func doubleTap() { model.toggleFullscreen() }
  }
}

/// VLC's drawable. It also takes hardware keyboard focus, so Space, the arrow keys and Escape
/// control playback as on the Mac.
final class PadVideoView: UIView {
  weak var model: EmbeddedPlayerModel?

  override var canBecomeFirstResponder: Bool { true }

  override func didMoveToWindow() {
    super.didMoveToWindow()
    if window != nil { becomeFirstResponder() }
  }

  override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
    var unhandled = Set<UIPress>()
    for press in presses {
      guard let key = press.key, let mapped = Self.playerKey(key.keyCode),
        !key.modifierFlags.contains(.command), !key.modifierFlags.contains(.control),
        model?.handleKey(
          mapped, coarse: key.modifierFlags.contains(.shift),
          fine: key.modifierFlags.contains(.alternate)) == true
      else {
        unhandled.insert(press)
        continue
      }
    }
    if !unhandled.isEmpty { super.pressesBegan(unhandled, with: event) }
  }

  private static func playerKey(_ code: UIKeyboardHIDUsage) -> EmbeddedPlayerModel.PadKey? {
    switch code {
    case .keyboardSpacebar: .space
    case .keyboardLeftArrow: .left
    case .keyboardRightArrow: .right
    case .keyboardUpArrow: .up
    case .keyboardDownArrow: .down
    case .keyboardEscape: .escape
    default: nil
    }
  }
}

/// A touch seek bar: drag anywhere on it to scrub, release to seek.
struct VLCSeekBar: View {
  @ObservedObject var model: EmbeddedPlayerModel
  /// Fraction of the bar buffered ahead of the playhead; drawn translucent behind the played part.
  var bufferedFraction: Double = 0
  @Environment(\.isEnabled) private var isEnabled

  var body: some View {
    GeometryReader { geometry in
      let width = max(1, geometry.size.width)
      let value = model.isScrubbing ? model.scrubPosition : model.position
      ZStack(alignment: .leading) {
        Capsule().fill(.white.opacity(0.20)).frame(height: 6)
        Capsule().fill(.white.opacity(0.35))
          .frame(width: width * min(1, max(0, bufferedFraction)), height: 6)
        Capsule().fill(.white).frame(width: width * value, height: 6)
        Circle().fill(.white)
          .frame(width: model.isScrubbing ? 22 : 14, height: model.isScrubbing ? 22 : 14)
          .offset(x: width * value - (model.isScrubbing ? 11 : 7))
          .opacity(isEnabled ? 1 : 0)
      }
      .frame(maxHeight: .infinity)
      .contentShape(Rectangle().inset(by: -14))
      .gesture(
        DragGesture(minimumDistance: 0)
          .onChanged { drag in
            guard isEnabled else { return }
            if !model.isScrubbing { model.isScrubbing = true }
            model.scrubPosition = min(1, max(0, drag.location.x / width))
            model.noteInteraction()
          }
          .onEnded { drag in
            guard isEnabled else { return }
            model.seek(to: min(1, max(0, drag.location.x / width)))
            model.isScrubbing = false
          }
      )
    }
    .accessibilityElement()
    .accessibilityLabel("Video playback position")
    .accessibilityValue(PlayerTime.string(model.position * model.duration))
    .accessibilityIdentifier("player.seekbar")
    .accessibilityAdjustableAction { direction in
      let step = 10 / max(1, model.duration)
      model.seek(to: min(1, max(0, model.position + (direction == .increment ? step : -step))))
    }
  }
}

/// Audio, Subtitles, Speed, Video and Chapters as SwiftUI menus.
struct PlayerOptionsMenus: View {
  @ObservedObject var player: EmbeddedPlayerModel
  var compact = false

  var body: some View {
    HStack(spacing: 14) {
      menu("Audio", symbol: "waveform") {
        ForEach(player.audioTracks, id: \.id) { track in
          check(track.name, track.id == player.selectedAudio) { player.chooseAudio(track.id) }
        }
        Divider()
        delays(audio: true)
      }
      menu("Subtitles", symbol: "captions.bubble") {
        check("Off", player.selectedSubtitle == -1) { player.chooseSubtitle(-1) }
        ForEach(player.subtitleTracks.filter { $0.id >= 0 }, id: \.id) { track in
          check(track.name, track.id == player.selectedSubtitle) {
            player.chooseSubtitle(track.id)
          }
        }
        Divider()
        Button("Find Online Subtitles…") { player.findOnlineSubtitles() }
        Button("Load Subtitle File…") { player.loadSubtitle() }
        delays(audio: false)
      }
      Menu {
        ForEach([Float(0.25), 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4], id: \.self) { rate in
          check(String(format: "%.2g×", rate), rate == player.speed) { player.setSpeed(rate) }
        }
      } label: {
        Text(String(format: "%.2g×", player.speed)).font(.system(size: 15, weight: .semibold))
          .frame(minWidth: 44, minHeight: 44)
      }
      .accessibilityLabel("Speed")
      menu("Video", symbol: "rectangle.on.rectangle") {
        Menu("Aspect Ratio") {
          Button("Default") { player.setAspect(nil) }
          ForEach(Self.ratios, id: \.self) { ratio in
            Button(ratio) { player.setAspect(ratio) }
          }
        }
        Menu("Crop") {
          Button("Default") { player.setCrop(nil) }
          ForEach(Self.ratios, id: \.self) { ratio in
            Button(ratio) { player.setCrop(ratio) }
          }
        }
        Menu("Deinterlace") {
          Button("Automatic") { player.setDeinterlace(.auto) }
          Button("On") { player.setDeinterlace(.on) }
          Button("Off") { player.setDeinterlace(.off) }
        }
      }
      if !player.chapters.isEmpty {
        menu("Chapters", symbol: "list.bullet") {
          ForEach(player.chapters, id: \.id) { chapter in
            check(chapter.name, chapter.id == player.selectedChapter) {
              player.chooseChapter(chapter.id)
            }
          }
        }
      }
    }
    .foregroundStyle(.white)
    .fileImporter(
      isPresented: $player.isPickingSubtitle,
      allowedContentTypes: ["srt", "ass", "vtt", "sub"].compactMap {
        UTType(filenameExtension: $0)
      }
    ) { result in
      guard case .success(let url) = result, let local = Self.localCopy(of: url) else { return }
      player.loadSubtitle(from: local)
    }
  }

  private static let ratios = ["16:9", "4:3", "16:10", "1:1", "2.35:1"]

  private func menu<Content: View>(
    _ title: String, symbol: String, @ViewBuilder content: () -> Content
  ) -> some View {
    Menu {
      content()
    } label: {
      if compact {
        Image(systemName: symbol).font(.system(size: 17)).frame(width: 44, height: 44)
      } else {
        Label(title, systemImage: symbol)
      }
    }
    .accessibilityLabel(title)
  }

  private func check(_ title: String, _ checked: Bool, action: @escaping () -> Void)
    -> some View
  {
    Button(action: action) {
      if checked { Label(title, systemImage: "checkmark") } else { Text(title) }
    }
  }

  @ViewBuilder private func delays(audio: Bool) -> some View {
    Menu(audio ? "Audio Synchronization" : "Subtitle Synchronization") {
      Button("Earlier by 0.1s") {
        if audio {
          player.audioDelay -= 0.1
          player.setDelays()
        } else {
          player.nudgeSubtitleSync(by: -PlayerSubtitleSync.stepMilliseconds)
        }
      }
      Button("Later by 0.1s") {
        if audio {
          player.audioDelay += 0.1
          player.setDelays()
        } else {
          player.nudgeSubtitleSync(by: PlayerSubtitleSync.stepMilliseconds)
        }
      }
      let delay = audio ? player.audioDelay : Double(player.subtitleSyncMilliseconds) / 1_000
      Button(String(format: "Reset (%.1fs)", delay)) {
        if audio {
          player.audioDelay = 0
          player.setDelays()
        } else {
          player.setSubtitleSync(0)
        }
      }
    }
  }

  /// Files from the picker are security scoped; VLC needs a plain readable path.
  private static func localCopy(of url: URL) -> URL? {
    let scoped = url.startAccessingSecurityScopedResource()
    defer { if scoped { url.stopAccessingSecurityScopedResource() } }
    let target = FileManager.default.temporaryDirectory.appending(path: url.lastPathComponent)
    try? FileManager.default.removeItem(at: target)
    do {
      try FileManager.default.copyItem(at: url, to: target)
      return target
    } catch { return nil }
  }
}
