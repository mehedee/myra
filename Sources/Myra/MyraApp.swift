#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import CoreSpotlight
import SwiftData
import SwiftUI

enum AppPersistence {
  static let applicationSupportFolder = "com.mehedee.Myra"
  static let storeFilename = "Myra.store"

  /// Developer switch: `MYRA_PROFILE_DIR` moves all app data into an isolated folder, so visual
  /// checks never open a personal library. Unset in normal use.
  static let profileOverrideVariable = "MYRA_PROFILE_DIR"

  /// True when an isolated developer profile is in use. Such a profile never starts network
  /// requests with the Mac's saved credentials on its own.
  static var isUsingProfileOverride: Bool {
    !(ProcessInfo.processInfo.environment[profileOverrideVariable] ?? "").isEmpty
  }

  static func persistentStoreURL(fileManager: FileManager = .default) throws -> URL {
    let applicationSupport: URL
    if let override = ProcessInfo.processInfo.environment[profileOverrideVariable],
      !override.isEmpty
    {
      applicationSupport = URL(fileURLWithPath: override, isDirectory: true)
      try fileManager.createDirectory(at: applicationSupport, withIntermediateDirectories: true)
    } else {
      applicationSupport = try fileManager.url(
        for: .applicationSupportDirectory,
        in: .userDomainMask,
        appropriateFor: nil,
        create: true
      )
    }
    let directory = applicationSupport.appending(
      path: applicationSupportFolder, directoryHint: .isDirectory)
    try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory.appending(path: storeFilename)
  }

  static func makeContainer(storeURL: URL? = nil, inMemory: Bool = false) throws
    -> ModelContainer
  {
    let configuration: ModelConfiguration
    if inMemory {
      configuration = ModelConfiguration(isStoredInMemoryOnly: true)
    } else {
      configuration = ModelConfiguration(url: try storeURL ?? persistentStoreURL())
    }
    return try ModelContainer(
      for: Category.self, DownloadBatch.self, DownloadItem.self, AppSettings.self,
      configurations: configuration
    )
  }

  @MainActor
  static func makePersistentContainer(fileManager: FileManager = .default) throws -> ModelContainer
  {
    let storeURL = try persistentStoreURL(fileManager: fileManager)
    let isNewStore = !fileManager.fileExists(atPath: storeURL.path)
    let container = try makeContainer(storeURL: storeURL)
    if isNewStore {
      try? migrateLegacyStore(
        to: container,
        applicationSupportURL: storeURL.deletingLastPathComponent().deletingLastPathComponent())
    }
    return container
  }

  @MainActor
  private static func migrateLegacyStore(
    to destination: ModelContainer, applicationSupportURL: URL
  ) throws {
    let legacyURL = applicationSupportURL.appending(path: "default.store")
    guard FileManager.default.fileExists(atPath: legacyURL.path) else { return }

    let legacy = try makeContainer(storeURL: legacyURL)
    let source = legacy.mainContext
    let target = destination.mainContext
    guard try target.fetchCount(FetchDescriptor<Category>()) == 0 else { return }

    for category in try source.fetch(FetchDescriptor<Category>()) {
      target.insert(
        Category(
          id: category.id,
          name: category.name,
          rootURLString: category.rootURLString,
          createdAt: category.createdAt
        ))
    }
    for batch in try source.fetch(FetchDescriptor<DownloadBatch>()) {
      let copy = DownloadBatch(id: batch.id, title: batch.title, status: batch.status)
      copy.createdAt = batch.createdAt
      copy.startedAt = batch.startedAt
      copy.completedAt = batch.completedAt
      copy.totalBytes = batch.totalBytes
      copy.completedBytes = batch.completedBytes
      copy.downloadSpeed = batch.downloadSpeed
      copy.errorMessage = batch.errorMessage
      target.insert(copy)
    }
    for item in try source.fetch(FetchDescriptor<DownloadItem>()) {
      guard let sourceURL = URL(string: item.sourceURLString) else { continue }
      let manifest = DownloadManifestItem(
        id: item.id,
        sourceURL: sourceURL,
        relativePath: item.relativePath,
        size: item.totalBytes
      )
      let copy = DownloadItem(
        batchID: item.batchID, manifest: manifest, destinationPath: item.destinationPath)
      copy.status = item.status
      copy.ariaGID = item.ariaGID
      copy.completedBytes = item.completedBytes
      copy.downloadSpeed = item.downloadSpeed
      copy.errorMessage = item.errorMessage
      target.insert(copy)
    }
    if let settings = try source.fetch(FetchDescriptor<AppSettings>()).first {
      let copy = AppSettings()
      copy.downloadDirectory = settings.downloadDirectory
      copy.downloadBookmark = settings.downloadBookmark
      copy.aria2PathOverride = settings.aria2PathOverride
      copy.concurrentDownloads = settings.concurrentDownloads
      copy.connectionsPerFile = settings.connectionsPerFile
      copy.splitCount = settings.splitCount
      copy.retryCount = settings.retryCount
      copy.speedLimit = settings.speedLimit
      copy.themeRaw = settings.themeRaw
      target.insert(copy)
    }
    try target.save()
  }
}

#if os(macOS)
final class MyraAppDelegate: NSObject, NSApplicationDelegate {
  var terminationHandler: (() -> Void)?

  func applicationDidFinishLaunching(_ notification: Notification) {
    DispatchQueue.main.async {
      for window in NSApplication.shared.windows {
        window.titlebarAppearsTransparent = true
        window.backgroundColor = .clear
        window.isOpaque = false
      }
    }
  }

  func applicationWillTerminate(_ notification: Notification) {
    terminationHandler?()
  }
}

@main
struct MyraApp: App {
  private let container: ModelContainer
  @StateObject private var coordinator = AppCoordinator()
  @NSApplicationDelegateAdaptor(MyraAppDelegate.self) private var appDelegate

  init() {
    do {
      let isTesting = ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
      container =
        isTesting
        ? try AppPersistence.makeContainer(inMemory: true)
        : try AppPersistence.makePersistentContainer()
    } catch {
      fatalError("Unable to initialize Myra data: \(error.localizedDescription)")
    }
  }

  var body: some Scene {
    WindowGroup {
      RootView(coordinator: coordinator)
        .modelContainer(container)
        .frame(minWidth: 1_000, minHeight: 650)
        .onOpenURL { coordinator.torrents.handleOpenURL($0) }
        .onContinueUserActivity(CSSearchableItemActionType) { activity in
          // A Spotlight result for a title: Myra opens and searches for that title.
          guard
            let identifier = activity.userInfo?[CSSearchableItemActivityIdentifier] as? String,
            let titleID = SpotlightTitleIndex.titleID(from: identifier)
          else { return }
          MyraSystemRouter.shared.post(.openTitle(id: titleID))
        }
        .onAppear {
          MyraAppearance.apply()
          coordinator.updater.start()
          appDelegate.terminationHandler = { coordinator.terminateImmediately() }
        }
    }
    .defaultSize(width: 1_240, height: 800)
    // The shell draws its own compact header beside the window buttons, as in the preview.
    .windowStyle(.hiddenTitleBar)
    .commands {
      MyraCommands(coordinator: coordinator, player: coordinator.player)
    }
    Settings {
      if let settings = coordinator.settings {
        SettingsView(coordinator: coordinator, settings: settings)
          .modelContainer(container)
          .frame(minWidth: 760, minHeight: 600)
      } else {
        ProgressView().frame(width: 400, height: 260)
      }
    }
  }
}
#endif
