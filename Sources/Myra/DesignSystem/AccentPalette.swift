#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Accent colour choices in macOS order. The raw value is the stable identifier stored in
/// UserDefaults under `defaultsKey`; it is deliberately not stored in SwiftData.
enum MyraAccent: String, CaseIterable, Identifiable, Sendable {
  case multicolor
  case blue
  case purple
  case pink
  case red
  case orange
  case yellow
  case green
  case graphite

  static let defaultsKey = "myraAccent"
  static let `default`: MyraAccent = .multicolor

  var id: String { rawValue }

  var title: String {
    switch self {
    case .multicolor: "Multicolor"
    case .blue: "Blue"
    case .purple: "Purple"
    case .pink: "Pink"
    case .red: "Red"
    case .orange: "Orange"
    case .yellow: "Yellow"
    case .green: "Green"
    case .graphite: "Graphite"
    }
  }

  /// Light and dark RGB values in 0xRRGGBB form. Multicolor is Myra's indigo from the preview;
  /// the rest follow
  /// the macOS system accent palette.
  var hexValues: (light: UInt32, dark: UInt32) {
    switch self {
    case .multicolor: (0x5B4BDB, 0x8B7CFF)
    case .blue: (0x007AFF, 0x0A84FF)
    case .purple: (0xAF52DE, 0xBF5AF2)
    case .pink: (0xFF2D55, 0xFF375F)
    case .red: (0xFF3B30, 0xFF453A)
    case .orange: (0xFF9500, 0xFF9F0A)
    case .yellow: (0xFFCC00, 0xFFD60A)
    case .green: (0x34C759, 0x32D74B)
    case .graphite: (0x8E8E93, 0x98989D)
    }
  }

  /// A dynamic colour that resolves to the light or dark variant from the current appearance.
  var nsColor: PlatformColor {
    let values = hexValues
    return PlatformColor.myraDynamic { isDark in
      MyraAccent.srgb(isDark ? values.dark : values.light)
    }
  }

  var color: Color { Color(platformColor: nsColor) }

  static func srgb(_ hex: UInt32) -> PlatformColor {
    PlatformColor.myraSRGB(
      red: CGFloat((hex >> 16) & 0xFF) / 255,
      green: CGFloat((hex >> 8) & 0xFF) / 255,
      blue: CGFloat(hex & 0xFF) / 255,
      alpha: 1)
  }
}

/// Reads and writes the accent choice in UserDefaults. Unknown stored values fall back to the default.
enum MyraAccentStore {
  static func load(defaults: UserDefaults = .standard) -> MyraAccent {
    MyraAccent(rawValue: defaults.string(forKey: MyraAccent.defaultsKey) ?? "") ?? .default
  }

  static func save(_ accent: MyraAccent, defaults: UserDefaults = .standard) {
    defaults.set(accent.rawValue, forKey: MyraAccent.defaultsKey)
  }
}

private struct MyraAccentKey: EnvironmentKey {
  static let defaultValue: MyraAccent = .default
}

extension EnvironmentValues {
  /// The accent chosen in Settings, available to any view below the app root.
  var myraAccent: MyraAccent {
    get { self[MyraAccentKey.self] }
    set { self[MyraAccentKey.self] = newValue }
  }
}
