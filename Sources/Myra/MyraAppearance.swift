#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// The Myra icon choices. Aperture is the default and the packaged bundle icon.
/// Stored values from the retired cinema, orbit, minimal and signature families migrate to Aperture.
enum MyraIconFamily: String, CaseIterable, Identifiable {
  case aperture
  case spotlight
  case ribbonM = "ribbon-m"

  var id: String { rawValue }

  var title: String {
    switch self {
    case .aperture: "Aperture"
    case .spotlight: "Spotlight"
    case .ribbonM: "Ribbon M"
    }
  }

  /// Resolves a stored preference. Missing, retired or unknown values fall back to Aperture.
  init(storedValue: String?) {
    self = storedValue.flatMap(MyraIconFamily.init(rawValue:)) ?? .aperture
  }
}

/// Icon Composer renditions. Clear and Tinted replace the earlier light, dark and glass previews.
enum MyraIconAppearance: String, CaseIterable, Identifiable {
  case automatic, light, dark, clear, tinted

  var id: String { rawValue }

  var title: String {
    switch self {
    case .automatic: "Follow System"
    case .light: "Default"
    case .dark: "Dark"
    case .clear: "Clear"
    case .tinted: "Tinted"
    }
  }

  /// Stored values from the previous glass choice map to Clear.
  init(storedValue: String?) {
    switch storedValue {
    case "glass": self = .clear
    case let value?: self = MyraIconAppearance(rawValue: value) ?? .automatic
    case nil: self = .automatic
    }
  }

  /// The rendition suffix of the runtime PNG, or nil when the system appearance decides.
  var renditionSuffix: String {
    switch self {
    case .automatic, .light: "default"
    case .dark: "dark"
    case .clear: "clear"
    case .tinted: "tinted"
    }
  }

}

/// A narrow AppKit bridge changes only the running Dock icon, never the signed bundle.
@MainActor
final class MyraAppearance {
  static let familyKey = "myraIconChoice"
  static let appearanceKey = "myraIconAppearance"
  /// The key used by the retired four-family picker. Its value is migrated, then removed.
  static let retiredFamilyKey = "myraIconFamily"
  private static var observers: [NSObjectProtocol] = []

  static func apply(defaults: UserDefaults = .standard) {
    #if os(iOS)
    // iPadOS uses the packaged icon; alternate icons would need registering in Info.plist.
    migrateStoredFamily(defaults: defaults)
    #else
    if observers.isEmpty {
      observers.append(
        DistributedNotificationCenter.default().addObserver(
          forName: Notification.Name("AppleInterfaceThemeChangedNotification"), object: nil,
          queue: .main
        ) { _ in Task { @MainActor in apply() } })
      observers.append(
        NotificationCenter.default.addObserver(
          forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { _ in Task { @MainActor in apply() } })
    }
    let family = migrateStoredFamily(defaults: defaults)
    let preference = MyraIconAppearance(storedValue: defaults.string(forKey: appearanceKey))
    // Aperture with Follow System uses the packaged bundle icon and the system's native rendering.
    if family == .aperture && preference == .automatic {
      NSApplication.shared.applicationIconImage = nil
      return
    }
    let dark =
      NSApplication.shared.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    if let image = image(family: family, appearance: preference, dark: dark) {
      NSApplication.shared.applicationIconImage = image
    }
    #endif
  }

  /// Moves a value stored by the retired family picker to Aperture and returns the current choice.
  @discardableResult
  static func migrateStoredFamily(defaults: UserDefaults = .standard) -> MyraIconFamily {
    if defaults.object(forKey: retiredFamilyKey) != nil {
      if defaults.object(forKey: familyKey) == nil {
        defaults.set(MyraIconFamily.aperture.rawValue, forKey: familyKey)
      }
      defaults.removeObject(forKey: retiredFamilyKey)
    }
    return MyraIconFamily(storedValue: defaults.string(forKey: familyKey))
  }

  /// The PNG resource name for a choice, for example `spotlight-dark`.
  static func resourceName(family: MyraIconFamily, appearance: MyraIconAppearance, dark: Bool)
    -> String
  {
    let resolved = appearance == .automatic ? (dark ? MyraIconAppearance.dark : .light) : appearance
    return "\(family.rawValue)-\(resolved.renditionSuffix)"
  }

  static func image(family: MyraIconFamily, appearance: MyraIconAppearance, dark: Bool) -> PlatformImage?
  {
    guard let url = resourceURL(named: resourceName(family: family, appearance: appearance, dark: dark))
    else { return nil }
    return PlatformImage.myraLoad(contentsOf: url)
  }

  static func resourceURL(named name: String) -> URL? {
    #if SWIFT_PACKAGE
      let bundles = [Bundle.module, Bundle.main]
    #else
      let bundles = [Bundle.main]
    #endif
    for bundle in bundles {
      if let url = bundle.url(forResource: name, withExtension: "png", subdirectory: "MyraIcons") {
        return url
      }
    }
    return nil
  }
}

/// The preview's "App icon" picker (`.iconpick`): three icons, the chosen one outlined in the
/// accent colour. The choice changes only the running Dock icon and is restored at launch.
struct MyraIconPicker: View {
  @AppStorage(MyraAppearance.familyKey) private var family = MyraIconFamily.aperture.rawValue
  @AppStorage(MyraAppearance.appearanceKey) private var appearance = MyraIconAppearance.automatic
    .rawValue
  @Environment(\.colorScheme) private var colorScheme
  @Environment(\.myraAccent) private var accent

  private var selectedFamily: MyraIconFamily { MyraIconFamily(storedValue: family) }

  var body: some View {
    HStack(spacing: 10) {
      ForEach(MyraIconFamily.allCases) { option in
        let isSelected = selectedFamily == option
        Button {
          family = option.rawValue
        } label: {
          VStack(spacing: 4) {
            if let icon = MyraAppearance.image(
              family: option, appearance: MyraIconAppearance(storedValue: appearance),
              dark: colorScheme == .dark)
            {
              Image(platformImage: icon).resizable().frame(width: 40, height: 40)
                .accessibilityHidden(true)
            }
            Text(option.title)
              .font(.system(size: 11))
              .foregroundStyle(isSelected ? MyraColor.text : MyraColor.text2)
          }
          .padding(6)
          .background(
            isSelected ? accent.color.opacity(0.13) : .clear, in: RoundedRectangle(cornerRadius: 12)
          )
          .overlay(
            RoundedRectangle(cornerRadius: 12)
              .stroke(isSelected ? accent.color : .clear, lineWidth: 1.5))
          .contentShape(RoundedRectangle(cornerRadius: 12))
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(option.title) icon")
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
      }
    }
    .onAppear { MyraAppearance.migrateStoredFamily() }
    .onChange(of: family) { _, _ in MyraAppearance.apply() }
    .onChange(of: appearance) { _, _ in MyraAppearance.apply() }
    .onChange(of: colorScheme) { _, _ in MyraAppearance.apply() }
  }
}
