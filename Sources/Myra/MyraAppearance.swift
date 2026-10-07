import AppKit
import SwiftUI

enum MyraIconFamily: String, CaseIterable, Identifiable {
  case signature, cinema, orbit, minimal
  var id: String { rawValue }
  var title: String { rawValue.capitalized }
}

enum MyraIconAppearance: String, CaseIterable, Identifiable {
  case automatic, light, dark, glass
  var id: String { rawValue }
  var title: String { self == .automatic ? "Follow System" : rawValue.capitalized }
}

/// A narrow AppKit bridge changes only the running Dock icon, never the signed bundle.
@MainActor
final class MyraAppearance {
  static let familyKey = "myraIconFamily"
  static let appearanceKey = "myraIconAppearance"
  private static var observers: [NSObjectProtocol] = []

  static func apply(defaults: UserDefaults = .standard) {
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
    let family = MyraIconFamily(rawValue: defaults.string(forKey: familyKey) ?? "") ?? .signature
    let preference =
      MyraIconAppearance(rawValue: defaults.string(forKey: appearanceKey) ?? "") ?? .automatic
    // The packaged layered primary icon keeps the system's native rendering when possible.
    if family == .signature && preference == .automatic {
      NSApplication.shared.applicationIconImage = nil
      return
    }
    let dark =
      NSApplication.shared.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    if let image = image(family: family, appearance: preference, dark: dark) {
      NSApplication.shared.applicationIconImage = image
    }
  }

  static func image(family: MyraIconFamily, appearance: MyraIconAppearance, dark: Bool) -> NSImage?
  {
    let resolved = appearance == .automatic ? (dark ? MyraIconAppearance.dark : .light) : appearance
    let name = "\(family.rawValue)-\(resolved.rawValue)"
    #if SWIFT_PACKAGE
      let bundles = [Bundle.module, Bundle.main]
    #else
      let bundles = [Bundle.main]
    #endif
    for bundle in bundles {
      if let url = bundle.url(forResource: name, withExtension: "png", subdirectory: "MyraIcons"),
        let image = NSImage(contentsOf: url)
      {
        return image
      }
    }
    return nil
  }
}

struct MyraAppearanceSettingsSection: View {
  @AppStorage(MyraAppearance.familyKey) private var family = MyraIconFamily.signature.rawValue
  @AppStorage(MyraAppearance.appearanceKey) private var appearance = MyraIconAppearance.automatic
    .rawValue
  @Environment(\.colorScheme) private var colorScheme

  var body: some View {
    Section("Myra Icon") {
      Picker("Appearance", selection: $appearance) {
        ForEach(MyraIconAppearance.allCases) { option in Text(option.title).tag(option.rawValue) }
      }
      HStack(spacing: 16) {
        ForEach(MyraIconFamily.allCases) { option in
          Button {
            family = option.rawValue
          } label: {
            VStack(spacing: 6) {
              if let icon = MyraAppearance.image(
                family: option,
                appearance: MyraIconAppearance(rawValue: appearance) ?? .automatic,
                dark: colorScheme == .dark
              ) {
                Image(nsImage: icon).resizable().frame(width: 64, height: 64)
              }
              Text(option.title).font(.caption)
              Image(systemName: family == option.rawValue ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(family == option.rawValue ? Color.accentColor : Color.secondary)
            }
            .padding(8)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 12))
          }
          .buttonStyle(.plain)
          .accessibilityLabel("\(option.title) icon")
          .accessibilityAddTraits(family == option.rawValue ? .isSelected : [])
        }
      }
      Text(
        "Your choice changes the running Dock icon and is restored at launch. Finder uses Myra's packaged Signature icon. Glass previews are rendered images; the default Signature icon follows Apple's native layered appearance."
      )
      .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
    .onChange(of: family) { _, _ in MyraAppearance.apply() }
    .onChange(of: appearance) { _, _ in MyraAppearance.apply() }
    .onChange(of: colorScheme) { _, _ in MyraAppearance.apply() }
  }
}
