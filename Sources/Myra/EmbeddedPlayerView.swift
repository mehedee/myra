import AppKit
import SwiftUI
import VLCKit

struct LibraryPlayerContainer: View {
  @ObservedObject var coordinator: AppCoordinator
  @ObservedObject private var player: EmbeddedPlayerModel
  @ObservedObject private var inspector: MediaInspectorModel

  init(coordinator: AppCoordinator) {
    self.coordinator = coordinator
    _player = ObservedObject(wrappedValue: coordinator.player)
    _inspector = ObservedObject(wrappedValue: coordinator.inspector)
  }

  var body: some View {
    ZStack {
      LibraryView(coordinator: coordinator)
        .opacity(player.media == nil ? 1 : 0)
        .allowsHitTesting(player.media == nil)
        .accessibilityHidden(player.media != nil)
    }
    .overlay(alignment: .trailing) {
      GeometryReader { geometry in
        if inspector.selected != nil && player.media == nil {
          MediaInspectorView(model: inspector, library: coordinator.library, play: coordinator.play)
            .frame(width: min(460, max(0, geometry.size.width)))
            .frame(maxHeight: .infinity)
            .background(.regularMaterial)
            .overlay(alignment: .leading) {
              Rectangle().fill(Color(nsColor: .separatorColor)).frame(width: 1)
            }
            .shadow(color: .black.opacity(0.22), radius: 14, x: -5, y: 0)
            .transition(.move(edge: .trailing).combined(with: .opacity))
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .trailing)
        }
      }
    }
    .animation(.snappy(duration: 0.22), value: inspector.selected?.id)
  }
}

struct EmbeddedPlayerView: View {
  var coordinator: AppCoordinator? = nil
  @ObservedObject var player: EmbeddedPlayerModel
  @Environment(\.colorScheme) private var colorScheme
  @State private var showRemaining = false
  @State private var showEpisodeBrowser = false
  @State private var showPreferences = false
  @State private var showMarkers = false

  var body: some View {
    ZStack {
      Color.black
      VStack(spacing: 0) {
        video
        if !player.isFullscreen { controls }
      }
      if player.isFullscreen {
        VStack(spacing: 0) {
          header.background(.regularMaterial)
          Spacer(minLength: 0)
          controls
        }
        .opacity(player.controlsVisible ? 1 : 0)
        .allowsHitTesting(player.controlsVisible)
        .accessibilityHidden(!player.controlsVisible)
        .animation(.easeInOut(duration: 0.18), value: player.controlsVisible)
      }
    }
    .environment(\.colorScheme, player.isFullscreen ? .dark : colorScheme)
    .ignoresSafeArea(edges: player.isFullscreen ? .all : [])
    .sheet(
      isPresented: Binding(
        get: { !player.versionChoices.isEmpty }, set: { if !$0 { player.cancelVersionChoice() } })
    ) {
      PlayerVersionChooser(player: player)
    }
    .sheet(isPresented: $showEpisodeBrowser) {
      PlayerEpisodeBrowser(player: player, coordinator: coordinator)
    }
    .sheet(isPresented: $showPreferences) { PlayerPreferencesView(player: player) }
    .sheet(isPresented: $showMarkers) { PlayerMarkersView(player: player) }
    .sheet(item: $player.subtitleSearch) { model in
      OnlineSubtitleSearchView(model: model)
    }
  }

  private var header: some View {
    VStack(spacing: 0) {
      HStack(spacing: 12) {
        Button {
          player.close()
        } label: {
          Label("Back to Library", systemImage: "chevron.left")
        }
        VStack(alignment: .leading, spacing: 2) {
          Text(player.media.map { MediaIdentity.parse(filename: $0.entry.name).title } ?? "Player")
            .font(.headline).lineLimit(1)
          Text(player.media?.entry.name ?? "").font(.caption).foregroundStyle(.secondary)
            .lineLimit(1).playbackHelp(
              player.media?.entry.name ?? "", fullscreen: player.isFullscreen)
        }
        Spacer()
        Text(player.status).font(.caption).foregroundStyle(.secondary)
        Button {
          if player.isFullscreen { player.exitFullscreen() } else { player.toggleFullscreen() }
        } label: {
          Image(
            systemName: player.isFullscreen
              ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right")
        }
        .playbackHelp(
          player.isFullscreen ? "Exit fullscreen" : "Enter fullscreen",
          fullscreen: player.isFullscreen
        )
        .accessibilityIdentifier("player.fullscreen")
      }
      .padding(12)
      Divider()
    }
    .onHover { player.hoverControls($0) }
  }

  private var video: some View {
    ZStack {
      Color.black
      VLCVideoSurface(model: player)
      if let error = player.errorMessage {
        VStack(spacing: 12) {
          Image(systemName: "exclamationmark.triangle").font(.largeTitle)
          Text(error).multilineTextAlignment(.center).frame(maxWidth: 440)
          if player.currentVersions.count > 1 {
            Button("Try another version") { player.requestPlayback(player.currentVersions) }
          }
        }
        .padding(24).foregroundStyle(.white).background(
          .black.opacity(0.75), in: RoundedRectangle(cornerRadius: 12))
      } else if player.isBuffering {
        ProgressView("Buffering…").padding(18).background(
          .regularMaterial, in: RoundedRectangle(cornerRadius: 10))
      }
      if let skip = player.activeSkip, player.isSeekable {
        VStack {
          Spacer()
          HStack {
            Spacer()
            Button(skip.0) { player.seek(to: skip.1.end / player.duration) }.padding(16)
          }
        }
      }
      if let resume = player.resumePosition, player.isSeekable,
        !player.isFullscreen || player.controlsVisible
      {
        VStack {
          HStack {
            Text("Resume from \(PlayerTime.string(resume))?")
            Button("Resume") { player.resumeSavedPosition() }
            Button("Start Over") { player.resumePosition = nil }
          }
          .padding(12).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
          .padding(16)
          Spacer()
        }
      }
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }

  private var controls: some View {
    VStack(spacing: 6) {
      HStack(spacing: 10) {
        Text(
          PlayerTime.string(
            player.isScrubbing ? player.scrubPosition * player.duration : player.elapsed)
        )
        .monospacedDigit().frame(minWidth: 60, alignment: .trailing)
        VLCSeekBar(model: player).frame(height: 22).disabled(!player.isSeekable)
        Button {
          showRemaining.toggle()
        } label: {
          Text(
            (showRemaining ? "−" : "")
              + PlayerTime.string(
                showRemaining ? max(0, player.duration - player.elapsed) : player.duration)
          )
          .monospacedDigit().frame(minWidth: 60, alignment: .leading)
        }.buttonStyle(.plain).playbackHelp(
          "Toggle duration / remaining time", fullscreen: player.isFullscreen)
      }
      GeometryReader { geometry in
        ZStack {
          HStack {
            HStack(spacing: 10) {
              playbackOptions
              Button {
                player.repeatEnabled.toggle()
              } label: {
                Image(systemName: "repeat").foregroundStyle(
                  player.repeatEnabled ? Color.accentColor : Color.primary)
              }.help("Repeat this video")
              Button {
                showEpisodeBrowser = true
              } label: {
                Label("Episodes / Files", systemImage: "list.bullet.rectangle")
              }.labelStyle(.iconOnly).help("Episodes / Files")
                .disabled(player.sequenceLoading || player.sequence == nil)
              Button {
                showPreferences = true
              } label: {
                Image(systemName: "gearshape")
              }.help("Playback preferences")
              Button {
                showMarkers = true
              } label: {
                Image(systemName: "scissors")
              }
              .help("Intro / outro markers").disabled(!player.isSeekable)
            }
            Spacer(minLength: 240)
            HStack(spacing: 8) {
              Button {
                player.toggleMute()
              } label: {
                Image(systemName: player.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
              }.accessibilityLabel("Mute")
              Slider(
                value: Binding(get: { player.volume }, set: { player.setVolume($0) }), in: 0...100
              )
              .frame(width: 100).accessibilityLabel("Volume")
              Text("\(Int(player.volume))%").font(.caption).monospacedDigit().frame(width: 38)
            }
          }
          transport.position(x: geometry.size.width / 2, y: geometry.size.height / 2)
        }
      }
      .frame(height: 30)
      .buttonStyle(.borderless)
      if !player.isSeekable && player.isPlaying {
        Text("This source does not support seeking.").font(.caption).foregroundStyle(.secondary)
      }
    }
    .padding(.horizontal, 14).padding(.vertical, 8).background(.regularMaterial)
    .onHover { player.hoverControls($0) }
  }

  private var transport: some View {
    HStack(spacing: 16) {
      Button {
        player.navigate(offset: -1)
      } label: {
        Image(systemName: "backward.end.fill")
      }
      .disabled(!player.canGoPrevious).accessibilityIdentifier("player.previous").help(
        "Previous video")
      Button {
        player.jump(seconds: -10)
      } label: {
        Image(systemName: "gobackward.10")
      }.disabled(!player.isSeekable)
      Button {
        player.togglePlayback()
      } label: {
        Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
      }.font(.title3)
      Button {
        player.stop()
      } label: {
        Image(systemName: "stop.fill")
      }
      Button {
        player.jump(seconds: 10)
      } label: {
        Image(systemName: "goforward.10")
      }.disabled(!player.isSeekable)
      Button {
        player.navigate(offset: 1)
      } label: {
        Image(systemName: "forward.end.fill")
      }
      .disabled(!player.canGoNext).accessibilityIdentifier("player.next").help("Next video")
    }.fixedSize()
  }

  private var playbackOptions: some View {
    PlayerOptionsMenus(player: player, compact: true).fixedSize()
  }
}

extension View {
  @ViewBuilder fileprivate func playbackHelp(_ text: String, fullscreen: Bool) -> some View {
    if fullscreen {
      accessibilityLabel(text)
    } else {
      help(text).accessibilityLabel(text)
    }
  }
}

private final class PlayerVideoView: VLCVideoView {
  var onKey: ((NSEvent) -> Bool)?
  var onFullscreen: (() -> Void)?
  var onWindow: ((NSWindow?) -> Void)?
  override var acceptsFirstResponder: Bool { true }
  override func keyDown(with event: NSEvent) {
    if onKey?(event) != true { super.keyDown(with: event) }
  }
  override func mouseDown(with event: NSEvent) {
    window?.makeFirstResponder(self)
    if event.clickCount == 2 { onFullscreen?() } else { super.mouseDown(with: event) }
  }
  override func viewDidMoveToWindow() {
    super.viewDidMoveToWindow()
    onWindow?(window)
    window?.makeFirstResponder(self)
  }
}

private struct VLCVideoSurface: NSViewRepresentable {
  @ObservedObject var model: EmbeddedPlayerModel
  func makeCoordinator() -> Coordinator { Coordinator(model: model) }
  func makeNSView(context: Context) -> PlayerVideoView {
    let view = PlayerVideoView(frame: .zero)
    view.onFullscreen = { [weak model] in model?.toggleFullscreen() }
    view.onWindow = { [weak model] in
      if let window = $0, model?.playbackWindow == nil { model?.setWindow(window) }
    }
    view.onKey = { [weak model] event in
      model?.handleKeyEvent(event) == true
    }
    model.attach(view: view)
    return view
  }
  func updateNSView(_ view: PlayerVideoView, context: Context) { model.attach(view: view) }
  static func dismantleNSView(_ view: PlayerVideoView, coordinator: Coordinator) {
    coordinator.model.detach(view: view)
  }
  final class Coordinator {
    let model: EmbeddedPlayerModel
    init(model: EmbeddedPlayerModel) { self.model = model }
  }
}

final class SeekSlider: NSSlider {
  var begin: (() -> Void)?
  var change: ((Double) -> Void)?
  var commit: ((Double) -> Void)?
  var duration: Double = 0
  var allowsTooltips = true {
    didSet { if !allowsTooltips { toolTip = nil } }
  }
  private var hoverArea: NSTrackingArea?

  override func mouseDown(with event: NSEvent) {
    guard isEnabled else { return }
    window?.makeFirstResponder(self)
    begin?()
    updateValue(event)
    while let next = window?.nextEvent(matching: [.leftMouseDragged, .leftMouseUp]) {
      updateValue(next)
      if next.type == .leftMouseUp { break }
    }
    commit?(doubleValue)
  }
  private func updateValue(_ event: NSEvent) {
    let point = convert(event.locationInWindow, from: nil)
    doubleValue = SeekBarGeometry.fraction(x: point.x, bounds: bounds)
    change?(doubleValue)
  }
  override func updateTrackingAreas() {
    super.updateTrackingAreas()
    if let hoverArea { removeTrackingArea(hoverArea) }
    let area = NSTrackingArea(
      rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self)
    addTrackingArea(area)
    hoverArea = area
  }
  override func mouseMoved(with event: NSEvent) {
    guard allowsTooltips else {
      toolTip = nil
      return
    }
    let point = convert(event.locationInWindow, from: nil)
    let fraction = SeekBarGeometry.fraction(x: point.x, bounds: bounds)
    toolTip =
      isEnabled
      ? "Seek to \(PlayerTime.string(fraction * duration))" : "This source is not seekable"
  }
  override func keyDown(with event: NSEvent) {
    guard isEnabled, event.keyCode == 123 || event.keyCode == 124 else {
      super.keyDown(with: event)
      return
    }
    begin?()
    doubleValue = min(
      1, max(0, doubleValue + (event.keyCode == 123 ? -1 : 1) * 10 / max(1, duration)))
    change?(doubleValue)
    commit?(doubleValue)
  }
}

private struct VLCSeekBar: NSViewRepresentable {
  @ObservedObject var model: EmbeddedPlayerModel
  func makeNSView(context: Context) -> SeekSlider {
    let slider = SeekSlider(value: 0, minValue: 0, maxValue: 1, target: nil, action: nil)
    let cell = PlayerSeekSliderCell()
    cell.minValue = 0
    cell.maxValue = 1
    cell.sliderType = .linear
    slider.cell = cell
    slider.setAccessibilityLabel("Video playback position")
    slider.setAccessibilityIdentifier("player.seekbar")
    slider.isContinuous = true
    slider.begin = { [weak model] in model?.isScrubbing = true }
    slider.change = { [weak model] in model?.scrubPosition = $0 }
    slider.commit = { [weak model] value in
      model?.seek(to: value)
      model?.isScrubbing = false
    }
    return slider
  }
  func updateNSView(_ slider: SeekSlider, context: Context) {
    slider.isEnabled = model.isSeekable
    slider.duration = model.duration
    slider.allowsTooltips = !model.isFullscreen
    if !model.isScrubbing { slider.doubleValue = model.position }
  }
}

enum SeekBarGeometry {
  static func track(in bounds: NSRect) -> NSRect {
    NSRect(x: bounds.minX + 9, y: bounds.midY - 3, width: max(1, bounds.width - 18), height: 6)
  }
  static func fraction(x: CGFloat, bounds: NSRect) -> Double {
    let track = track(in: bounds)
    return min(1, max(0, Double((x - track.minX) / track.width)))
  }
}

final class PlayerSeekSliderCell: NSSliderCell {
  override func barRect(flipped: Bool) -> NSRect {
    SeekBarGeometry.track(in: controlView?.bounds ?? NSRect(x: 0, y: 0, width: 100, height: 22))
  }
  override func knobRect(flipped: Bool) -> NSRect {
    let track = barRect(flipped: flipped)
    let fraction =
      maxValue > minValue ? min(1, max(0, (doubleValue - minValue) / (maxValue - minValue))) : 0
    return NSRect(
      x: track.minX + track.width * fraction - 8, y: track.midY - 8, width: 16, height: 16)
  }
  override func drawBar(inside rect: NSRect, flipped: Bool) {
    let rect = barRect(flipped: flipped)
    let dark = controlView?.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    let track = NSBezierPath(roundedRect: rect, xRadius: 3, yRadius: 3)
    (dark ? NSColor.white.withAlphaComponent(0.30) : NSColor.black.withAlphaComponent(0.24))
      .setFill()
    track.fill()
    (dark ? NSColor.white.withAlphaComponent(0.55) : NSColor.black.withAlphaComponent(0.40))
      .setStroke()
    track.lineWidth = 1
    track.stroke()
    let fraction =
      maxValue > minValue ? min(1, max(0, (doubleValue - minValue) / (maxValue - minValue))) : 0
    if fraction > 0 {
      let progress = NSRect(
        x: rect.minX, y: rect.minY, width: rect.width * fraction, height: rect.height)
      (isEnabled ? NSColor.controlAccentColor : NSColor.secondaryLabelColor).setFill()
      NSBezierPath(roundedRect: progress, xRadius: 3, yRadius: 3).fill()
    }
  }
  override func drawKnob(_ knobRect: NSRect) {
    let knob = NSBezierPath(ovalIn: knobRect.insetBy(dx: 1, dy: 1))
    (isEnabled ? NSColor.white : NSColor.lightGray).setFill()
    knob.fill()
    (isEnabled ? NSColor.controlAccentColor : NSColor.gray).setStroke()
    knob.lineWidth = 2
    knob.stroke()
  }
}
