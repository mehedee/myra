import AppKit
import SwiftUI
import VLCKit

/// Native pop-up menus are built once per opening, not during each transport refresh.
struct PlayerOptionsMenus: NSViewRepresentable {
  let player: EmbeddedPlayerModel
  var compact = false

  func makeCoordinator() -> Coordinator { Coordinator(player: player) }

  func makeNSView(context: Context) -> NSStackView {
    let stack = NSStackView()
    stack.orientation = .horizontal
    stack.spacing = 12
    for (index, title) in ["Audio", "Subtitles", "Speed", "Video", "Chapters"].enumerated() {
      let button = NSButton(
        title: title, target: context.coordinator, action: #selector(Coordinator.open(_:)))
      button.tag = index
      button.isBordered = false
      button.font = .systemFont(ofSize: 13)
      button.image = NSImage(systemSymbolName: "chevron.down", accessibilityDescription: nil)
      button.imagePosition = .imageTrailing
      if compact, index != 2 {
        let symbols = [
          "waveform", "captions.bubble", "speedometer", "rectangle.on.rectangle", "list.bullet",
        ]
        button.title = ""
        button.image = NSImage(systemSymbolName: symbols[index], accessibilityDescription: title)
        button.imagePosition = .imageOnly
        button.toolTip = title
      }
      button.setAccessibilityLabel(title)
      stack.addArrangedSubview(button)
    }
    updateNSView(stack, context: context)
    return stack
  }

  func updateNSView(_ view: NSStackView, context: Context) {
    guard !context.coordinator.isTracking else { return }
    let buttons = view.arrangedSubviews.compactMap { $0 as? NSButton }
    buttons.first(where: { $0.tag == 2 })?.title = String(format: "%.2g×", player.speed)
    buttons.first(where: { $0.tag == 4 })?.isHidden = player.chapters.isEmpty
  }

  @MainActor final class Coordinator: NSObject {
    let player: EmbeddedPlayerModel
    private(set) var isTracking = false
    private(set) var activeMenu: NSMenu?
    private var actions: [MenuAction] = []

    init(player: EmbeddedPlayerModel) { self.player = player }

    @objc func open(_ sender: NSButton) {
      guard !isTracking, player.media != nil else { return }
      let menu = makeMenu(kind: sender.tag)
      activeMenu = menu
      isTracking = true
      player.setMenuTracking(true, menu: menu)
      defer {
        isTracking = false
        activeMenu = nil
        player.setMenuTracking(false)
      }
      menu.popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.maxY + 4), in: sender)
    }

    func makeMenu(kind: Int) -> NSMenu {
      actions.removeAll()
      let menu = NSMenu()
      menu.autoenablesItems = false
      switch kind {
      case 0:
        for track in player.audioTracks {
          add(track.name, to: menu, checked: track.id == player.selectedAudio) { [player] in
            player.chooseAudio(track.id)
          }
        }
        menu.addItem(.separator())
        delays(to: menu, audio: true)
      case 1:
        add("Off", to: menu, checked: player.selectedSubtitle == -1) { [player] in
          player.chooseSubtitle(-1)
        }
        for track in player.subtitleTracks where track.id >= 0 {
          add(track.name, to: menu, checked: track.id == player.selectedSubtitle) { [player] in
            player.chooseSubtitle(track.id)
          }
        }
        menu.addItem(.separator())
        add("Find Online Subtitles…", to: menu) { [player] in player.findOnlineSubtitles() }
        add("Load Subtitle File…", to: menu) { [player] in player.loadSubtitle() }
        delays(to: menu, audio: false)
      case 2:
        for rate: Float in [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4] {
          add(String(format: "%.2g×", rate), to: menu, checked: rate == player.speed) { [player] in
            player.setSpeed(rate)
          }
        }
      case 3:
        let aspect = submenu("Aspect Ratio", in: menu)
        add("Default", to: aspect) { [player] in player.setAspect(nil) }
        for ratio in ["16:9", "4:3", "16:10", "1:1", "2.35:1"] {
          add(ratio, to: aspect) { [player] in player.setAspect(ratio) }
        }
        let crop = submenu("Crop", in: menu)
        add("Default", to: crop) { [player] in player.setCrop(nil) }
        for ratio in ["16:9", "4:3", "16:10", "1:1", "2.35:1"] {
          add(ratio, to: crop) { [player] in player.setCrop(ratio) }
        }
        let deinterlace = submenu("Deinterlace", in: menu)
        add("Automatic", to: deinterlace) { [player] in player.setDeinterlace(.auto) }
        add("On", to: deinterlace) { [player] in player.setDeinterlace(.on) }
        add("Off", to: deinterlace) { [player] in player.setDeinterlace(.off) }
      default:
        for chapter in player.chapters {
          add(chapter.name, to: menu, checked: chapter.id == player.selectedChapter) { [player] in
            player.chooseChapter(chapter.id)
          }
        }
      }
      return menu
    }

    private func delays(to menu: NSMenu, audio: Bool) {
      let sub = submenu(audio ? "Audio Synchronization" : "Subtitle Synchronization", in: menu)
      add("Earlier by 0.1s", to: sub) { [player] in
        if audio { player.audioDelay -= 0.1 } else { player.subtitleDelay -= 0.1 }
        player.setDelays()
      }
      add("Later by 0.1s", to: sub) { [player] in
        if audio { player.audioDelay += 0.1 } else { player.subtitleDelay += 0.1 }
        player.setDelays()
      }
      let delay = audio ? player.audioDelay : player.subtitleDelay
      add(String(format: "Reset (%.1fs)", delay), to: sub) { [player] in
        if audio { player.audioDelay = 0 } else { player.subtitleDelay = 0 }
        player.setDelays()
      }
    }

    private func submenu(_ title: String, in parent: NSMenu) -> NSMenu {
      let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
      let sub = NSMenu(title: title)
      sub.autoenablesItems = false
      item.submenu = sub
      parent.addItem(item)
      return sub
    }

    private func add(
      _ title: String, to menu: NSMenu, checked: Bool = false, action: @escaping () -> Void
    ) {
      let target = MenuAction(action)
      actions.append(target)
      let item = NSMenuItem(title: title, action: #selector(MenuAction.run), keyEquivalent: "")
      item.target = target
      item.state = checked ? .on : .off
      menu.addItem(item)
    }
  }

  @MainActor private final class MenuAction: NSObject {
    let action: () -> Void
    init(_ action: @escaping () -> Void) { self.action = action }
    @objc func run() { action() }
  }
}
