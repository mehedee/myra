import AppKit
import SwiftUI

/// Fullscreen playback owns only the chrome properties it changes, not the window delegate.
@MainActor
final class PlayerFullscreenChrome {
  private struct Snapshot {
    let fullSizeContent: Bool
    let titleVisibility: NSWindow.TitleVisibility
    let transparentTitlebar: Bool
    let separator: NSTitlebarSeparatorStyle
    let background: NSColor
    let toolbar: NSToolbar?
    let toolbarVisible: Bool
    let buttons: [(NSButton, Bool)]
  }
  private weak var window: NSWindow?
  private var snapshot: Snapshot?

  func update(window newWindow: NSWindow?, fullscreen: Bool) {
    if window !== newWindow { restore() }
    guard fullscreen, let newWindow else {
      restore()
      return
    }
    window = newWindow
    if snapshot == nil {
      let buttons = [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton]
        .compactMap { newWindow.standardWindowButton($0) }
      snapshot = Snapshot(
        fullSizeContent: newWindow.styleMask.contains(.fullSizeContentView),
        titleVisibility: newWindow.titleVisibility,
        transparentTitlebar: newWindow.titlebarAppearsTransparent,
        separator: newWindow.titlebarSeparatorStyle, background: newWindow.backgroundColor,
        toolbar: newWindow.toolbar, toolbarVisible: newWindow.toolbar?.isVisible ?? false,
        buttons: buttons.map { ($0, $0.isHidden) })
    }
    newWindow.styleMask.insert(.fullSizeContentView)
    newWindow.titleVisibility = .hidden
    newWindow.titlebarAppearsTransparent = true
    newWindow.titlebarSeparatorStyle = .none
    newWindow.backgroundColor = .black
    newWindow.toolbar?.isVisible = false
    for (button, _) in snapshot?.buttons ?? [] { button.isHidden = true }
  }

  func restore() {
    defer {
      self.snapshot = nil
      self.window = nil
    }
    guard let window, let snapshot else { return }
    if snapshot.fullSizeContent {
      window.styleMask.insert(.fullSizeContentView)
    } else {
      window.styleMask.remove(.fullSizeContentView)
    }
    window.titleVisibility = snapshot.titleVisibility
    window.titlebarAppearsTransparent = snapshot.transparentTitlebar
    window.titlebarSeparatorStyle = snapshot.separator
    window.backgroundColor = snapshot.background
    if window.toolbar === snapshot.toolbar { window.toolbar?.isVisible = snapshot.toolbarVisible }
    for (button, hidden) in snapshot.buttons { button.isHidden = hidden }
  }
}

/// The root window outlives the VLC surface, including during native fullscreen transitions.
struct PlayerWindowAttachment: NSViewRepresentable {
  let player: EmbeddedPlayerModel

  func makeNSView(context: Context) -> WindowAnchor {
    let view = WindowAnchor()
    view.onWindow = { [weak player] window in
      if let window { player?.setWindow(window) }
    }
    return view
  }

  func updateNSView(_ view: WindowAnchor, context: Context) {
    if let window = view.window { player.setWindow(window) }
  }

  final class WindowAnchor: NSView {
    var onWindow: ((NSWindow?) -> Void)?
    override func viewDidMoveToWindow() {
      super.viewDidMoveToWindow()
      onWindow?(window)
    }
  }
}
