#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI
import UniformTypeIdentifiers

// Small seams between the macOS and iPadOS builds. Shared code calls these instead of AppKit or
// UIKit directly, so each call site needs no conditional compilation.

#if canImport(AppKit)
typealias PlatformColor = NSColor
typealias PlatformImage = NSImage
#else
typealias PlatformColor = UIColor
typealias PlatformImage = UIImage
#endif

extension PlatformColor {
  /// A colour that resolves to `dark` in Dark Mode and to `light` otherwise.
  static func myraDynamic(_ resolve: @escaping @Sendable (_ isDark: Bool) -> PlatformColor)
    -> PlatformColor
  {
    #if canImport(AppKit)
    NSColor(name: nil) { appearance in
      resolve(appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua)
    }
    #else
    UIColor { traits in resolve(traits.userInterfaceStyle == .dark) }
    #endif
  }

  /// The system hairline colour between content areas.
  static var myraSeparator: PlatformColor {
    #if canImport(AppKit)
    .separatorColor
    #else
    .separator
    #endif
  }

  /// An sRGB colour from 0–1 components.
  static func myraSRGB(red: CGFloat, green: CGFloat, blue: CGFloat, alpha: CGFloat)
    -> PlatformColor
  {
    #if canImport(AppKit)
    NSColor(srgbRed: red, green: green, blue: blue, alpha: alpha)
    #else
    UIColor(red: red, green: green, blue: blue, alpha: alpha)
    #endif
  }
}

extension PlatformImage {
  /// Loads an image file from disk.
  static func myraLoad(contentsOf url: URL) -> PlatformImage? {
    #if canImport(AppKit)
    NSImage(contentsOf: url)
    #else
    UIImage(contentsOfFile: url.path)
    #endif
  }
}

extension PlatformColor {
  /// The background of controls such as text fields and grouped rows.
  static var myraControlBackground: PlatformColor {
    #if canImport(AppKit)
    .controlBackgroundColor
    #else
    .secondarySystemBackground
    #endif
  }
}

extension Color {
  init(platformColor: PlatformColor) {
    #if canImport(AppKit)
    self.init(nsColor: platformColor)
    #else
    self.init(uiColor: platformColor)
    #endif
  }
}

extension Image {
  init(platformImage: PlatformImage) {
    #if canImport(AppKit)
    self.init(nsImage: platformImage)
    #else
    self.init(uiImage: platformImage)
    #endif
  }
}

enum MyraDevice {
  /// The device word for user-facing text: "Mac" or "iPad".
  static var name: String {
    #if os(macOS)
    "Mac"
    #else
    "iPad"
    #endif
  }
}

@MainActor
enum PlatformServices {
  /// Replaces the general pasteboard's contents with `text`.
  static func copy(_ text: String) {
    #if canImport(AppKit)
    NSPasteboard.general.clearContents()
    NSPasteboard.general.setString(text, forType: .string)
    #else
    UIPasteboard.general.string = text
    #endif
  }

  /// Opens a URL with the system: a web page, a Shortcuts link, or another app's URL scheme.
  static func open(_ url: URL) {
    #if canImport(AppKit)
    NSWorkspace.shared.open(url)
    #else
    UIApplication.shared.open(url)
    #endif
  }

  /// Shows a downloaded file's folder: Finder on macOS, the Files app on iPadOS.
  static func reveal(_ fileURL: URL) {
    #if canImport(AppKit)
    NSWorkspace.shared.activateFileViewerSelecting([fileURL])
    #else
    let folder = fileURL.hasDirectoryPath ? fileURL : fileURL.deletingLastPathComponent()
    var components = URLComponents(url: folder, resolvingAgainstBaseURL: false)
    components?.scheme = "shareddocuments"
    if let url = components?.url { UIApplication.shared.open(url) }
    #endif
  }
}

extension View {
  /// The plain text-link button look. iPadOS has no `.link` style, so it uses a borderless button.
  @ViewBuilder func myraLinkButtonStyle() -> some View {
    #if os(macOS)
    buttonStyle(.link)
    #else
    buttonStyle(.borderless)
    #endif
  }
}

extension View {
  /// Radio buttons on macOS; iPadOS has none, so it uses an inline picker.
  @ViewBuilder func myraRadioPickerStyle() -> some View {
    #if os(macOS)
    pickerStyle(.radioGroup)
    #else
    pickerStyle(.inline)
    #endif
  }

  /// A checkbox on macOS and a switch on iPadOS.
  @ViewBuilder func myraCheckboxToggleStyle() -> some View {
    #if os(macOS)
    toggleStyle(.checkbox)
    #else
    toggleStyle(.switch)
    #endif
  }
}

/// A JSON file for the system save sheet (`fileExporter`), used for backups on iPadOS.
struct MyraJSONDocument: FileDocument {
  static let readableContentTypes: [UTType] = [.json]
  var data: Data

  init(data: Data) { self.data = data }

  init(configuration: ReadConfiguration) throws {
    data = configuration.file.regularFileContents ?? Data()
  }

  func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
    FileWrapper(regularFileWithContents: data)
  }
}
