#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import CoreGraphics
import SwiftUI

/// Shared layout constants for the macOS shell and new components.
enum MyraSpacing {
  static let xxs: CGFloat = 4
  static let xs: CGFloat = 8
  static let s: CGFloat = 12
  static let m: CGFloat = 16
  static let l: CGFloat = 24
}

enum MyraRadius {
  static let control: CGFloat = 8
  static let card: CGFloat = 14
  /// Panels and rows containers (`--radius` in the preview).
  static let panel: CGFloat = 12
}

/// Surface and text colours from the approved redesign preview, resolved per appearance.
/// The accent is deliberately absent: read it from `\.myraAccent` so the Settings choice applies.
/// Status colours are system colours and are used directly by `StatusPill`.
enum MyraColor {
  static let window = dynamic((0xF6F6F8, 1), (0x1C1C1F, 1))
  static let content = dynamic((0xFFFFFF, 1), (0x161618, 1))
  /// The window and sidebar colours without alpha; `TranslucentSurface` applies the Settings level.
  static let windowBase = dynamic((0xF6F6F8, 1), (0x1C1C1F, 1))
  static let sidebarBase = dynamic((0xECECF1, 1), (0x28282E, 1))
  static let sidebar = dynamic((0xECECF1, 0.78), (0x28282E, 0.72))
  static let glass = dynamic((0xFFFFFF, 0.72), (0x323238, 0.66))
  static let glassStrong = dynamic((0xFFFFFF, 0.9), (0x2C2C32, 0.94))

  static let text = dynamic((0x1D1D1F, 1), (0xF5F5F7, 1))
  static let text2 = dynamic((0x6E6E73, 1), (0xA1A1A8, 1))
  static let text3 = dynamic((0x9A9AA0, 1), (0x6F6F76, 1))

  static let fill = dynamic((0x000000, 0.045), (0xFFFFFF, 0.06))
  static let fill2 = dynamic((0x000000, 0.08), (0xFFFFFF, 0.11))
  static let line = dynamic((0x000000, 0.08), (0xFFFFFF, 0.08))
  static let lineStrong = dynamic((0x000000, 0.14), (0xFFFFFF, 0.16))

  /// A dynamic colour from a 0xRRGGBB value and an alpha, for light and dark appearance.
  static func dynamic(_ light: (UInt32, CGFloat), _ dark: (UInt32, CGFloat)) -> Color {
    Color(
      platformColor: PlatformColor.myraDynamic { isDark in
        let value = isDark ? dark : light
        return MyraAccent.srgb(value.0).withAlphaComponent(value.1)
      })
  }
}
