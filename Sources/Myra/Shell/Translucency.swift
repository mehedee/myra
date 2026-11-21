#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// The Settings → General translucency choice. Strong is the default, as in the approved preview.
/// The alphas are the preview's `--a-win`, `--a-side` and `--a-content`: how much of each surface's
/// own colour covers the blurred desktop behind the window.
enum MyraTranslucency: String, CaseIterable, Identifiable, Sendable {
  case off, subtle, standard, strong

  static let defaultsKey = "myraTranslucency"
  static let `default`: MyraTranslucency = .strong

  var id: String { rawValue }

  var title: String {
    switch self {
    case .off: "Off"
    case .subtle: "Subtle"
    case .standard: "Standard"
    case .strong: "Strong"
    }
  }

  /// Resolves a stored preference. Missing or unknown values fall back to Strong.
  init(storedValue: String?) {
    self = storedValue.flatMap(MyraTranslucency.init(rawValue:)) ?? .default
  }

  /// Tint over the window's blurred backdrop.
  var windowAlpha: Double {
    switch self {
    case .off: 1
    case .subtle: 0.92
    case .standard: 0.78
    case .strong: 0.55
    }
  }

  var sidebarAlpha: Double {
    switch self {
    case .off: 1
    case .subtle: 0.90
    case .standard: 0.72
    case .strong: 0.50
    }
  }

  var contentAlpha: Double {
    switch self {
    case .off: 1
    case .subtle: 0.97
    case .standard: 0.94
    case .strong: 0.88
    }
  }

  /// The system material behind the window. Off draws no material.
  #if os(macOS)
  var material: NSVisualEffectView.Material? {
    switch self {
    case .off: nil
    case .subtle: .windowBackground
    case .standard: .sidebar
    case .strong: .underWindowBackground
    }
  }
  #else
  var material: Material? {
    switch self {
    case .off: nil
    case .subtle: .thickMaterial
    case .standard: .regularMaterial
    case .strong: .ultraThinMaterial
    }
  }
  #endif

  /// Respecting Reduce Transparency: the surfaces become opaque, whatever the choice.
  func resolved(reduceTransparency: Bool) -> MyraTranslucency {
    reduceTransparency ? .off : self
  }
}

private struct MyraTranslucencyKey: EnvironmentKey {
  static let defaultValue = MyraTranslucency.default
}

extension EnvironmentValues {
  /// The effective translucency, already adjusted for Reduce Transparency.
  var myraTranslucency: MyraTranslucency {
    get { self[MyraTranslucencyKey.self] }
    set { self[MyraTranslucencyKey.self] = newValue }
  }
}

#if os(macOS)
/// The window's backing: a real `NSVisualEffectView` that blurs what is behind the window, with the
/// window colour over it. It also makes the hosting window non-opaque so the blur can show through.
struct TranslucentWindowBackground: NSViewRepresentable {
  let level: MyraTranslucency

  func makeNSView(context: Context) -> BackdropView { BackdropView() }

  func updateNSView(_ view: BackdropView, context: Context) {
    view.level = level
  }

  final class BackdropView: NSVisualEffectView {
    var level: MyraTranslucency = .default {
      didSet { configure() }
    }

    override func viewDidMoveToWindow() {
      super.viewDidMoveToWindow()
      configure()
    }

    private func configure() {
      blendingMode = .behindWindow
      state = .active
      if let material = level.material {
        self.material = material
        isHidden = false
      } else {
        isHidden = true
      }
      guard let window else { return }
      // The hidden title bar and a clear background let the material show; Off keeps the window
      // opaque so it costs nothing.
      window.titlebarAppearsTransparent = true
      window.isOpaque = level == .off
      window.backgroundColor = level == .off ? .windowBackgroundColor : .clear
    }
  }
}
#endif

/// The three surfaces of the shell. Each is a colour at the level's alpha over the layer beneath.
struct TranslucentSurface: View {
  enum Role { case window, sidebar, content }

  let role: Role
  @Environment(\.myraTranslucency) private var level

  init(role: Role) { self.role = role }

  var body: some View {
    base.opacity(alpha)
  }

  private var alpha: Double {
    switch role {
    case .window: level.windowAlpha
    case .sidebar: level.sidebarAlpha
    case .content: level.contentAlpha
    }
  }

  private var base: Color {
    switch role {
    case .window: MyraColor.windowBase
    case .sidebar: MyraColor.sidebarBase
    case .content: MyraColor.content
    }
  }
}

#if os(iOS)
/// iPadOS has no window-behind blur; the shell's backing is a SwiftUI material instead.
struct TranslucentWindowBackground: View {
  let level: MyraTranslucency

  var body: some View {
    if let material = level.material {
      Rectangle().fill(material).ignoresSafeArea()
    } else {
      MyraColor.windowBase.ignoresSafeArea()
    }
  }
}
#endif
