import Foundation

/// iPadOS does not allow Myra to start the native torrent helper process, so torrents are not
/// part of the iPad build. This stub keeps `TorrentController` compiling and fails every start
/// with a clear message. The iPad shell hides torrent entry points.
@MainActor
final class TorrentEngineClient {
  nonisolated static let unavailableMessage = "Torrents are not available in Myra for iPad."

  var receive: ((TorrentEngineEvent) -> Void)?
  var failed: ((String) -> Void)?

  static func executableURL(bundle: Bundle = .main) throws -> URL {
    throw TorrentError.database(unavailableMessage)
  }

  func start(source: String, cache: URL, executable: URL? = nil, testPeerPort: Int? = nil) throws {
    throw TorrentError.database(Self.unavailableMessage)
  }

  func command(_ operation: String, values: [String: Any] = [:]) throws {
    throw TorrentError.database(Self.unavailableMessage)
  }

  func stop() {}
  var isRunning: Bool { false }
  func waitForExit() async {}

  nonisolated static func inspect(file: URL, executable: URL) async throws -> TorrentEngineEvent {
    throw TorrentError.database(unavailableMessage)
  }
}
