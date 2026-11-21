import Foundation

struct TorrentEngineEvent: Decodable, Sendable {
  let event: String
  var identity: String?
  var name: String?
  var files: [TorrentFile]?
  var index: Int?
  var url: URL?
  var message: String?
  var downloaded: Int64?
  var total: Int64?
  var downloadSpeed: Int64?
  var uploadSpeed: Int64?
  var peers: Int?
  var paused: Bool?
  var bufferedSeconds: Double?
  var bufferedBytes: Int64?
  var percent: Double?
}

#if os(macOS)
/// The app owns the helper's pipes and lifetime. No shell, daemon, or public RPC endpoint.
@MainActor
final class TorrentEngineClient {
  private let process = Process()
  private let input = Pipe()
  private let output = Pipe()
  private var buffer = Data()
  private var stopped = false
  var receive: ((TorrentEngineEvent) -> Void)?
  var failed: ((String) -> Void)?

  static func executableURL(bundle: Bundle = .main) throws -> URL {
    let url = bundle.bundleURL.appending(path: "Contents/Helpers/myra-torrent-engine")
    if FileManager.default.isExecutableFile(atPath: url.path) { return url }
    #if DEBUG
      // Development helper is opt-in; production never searches PATH for an engine.
      if let value = ProcessInfo.processInfo.environment["MYRA_TORRENT_ENGINE"] {
        let candidate = URL(fileURLWithPath: value)
        if FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
      }
    #endif
    throw TorrentError.database("The bundled torrent engine is missing. Reinstall the application.")
  }

  func start(source: String, cache: URL, executable: URL? = nil, testPeerPort: Int? = nil) throws {
    guard !process.isRunning, !stopped else { return }
    process.executableURL = try executable ?? Self.executableURL()
    var arguments = ["run", source, cache.path, cache.appending(path: "resume.dat").path]
    if let testPeerPort { arguments += ["loopback", String(testPeerPort)] }
    process.arguments = arguments
    process.standardInput = input
    process.standardOutput = output
    process.standardError = FileHandle.nullDevice
    output.fileHandleForReading.readabilityHandler = { [weak self] handle in
      let bytes = handle.availableData
      Task { @MainActor [weak self] in self?.accept(bytes) }
    }
    process.terminationHandler = { [weak self] child in
      let status = child.terminationStatus
      Task { @MainActor [weak self] in
        guard let self, !self.stopped else { return }
        self.failed?(
          status == 0 ? "Torrent engine stopped." : "Torrent engine exited unexpectedly.")
      }
    }
    try process.run()
  }

  private func accept(_ bytes: Data) {
    guard !stopped else { return }
    if bytes.isEmpty {
      output.fileHandleForReading.readabilityHandler = nil
      return
    }
    buffer.append(bytes)
    guard buffer.count <= 2 * 1024 * 1024 else {
      failed?("Torrent engine response is too large.")
      stop()
      return
    }
    while let end = buffer.firstIndex(of: 10) {
      let frame = Data(buffer[..<end])
      buffer.removeSubrange(...end)
      do { receive?(try JSONDecoder().decode(TorrentEngineEvent.self, from: frame)) } catch {
        failed?("Torrent engine returned invalid metadata.")
        stop()
        return
      }
    }
  }
  func command(_ operation: String, values: [String: Any] = [:]) throws {
    guard process.isRunning, !stopped else {
      throw TorrentError.database("Torrent is not running.")
    }
    var object = values
    object["op"] = operation
    var bytes = try JSONSerialization.data(withJSONObject: object)
    guard bytes.count <= 8192 else { throw TorrentError.invalidSettings }
    bytes.append(10)
    try input.fileHandleForWriting.write(contentsOf: bytes)
  }
  func stop() {
    guard !stopped else { return }
    try? command("stop")
    stopped = true
    output.fileHandleForReading.readabilityHandler = nil
    try? input.fileHandleForWriting.close()
    // Give the native engine time to persist verified resume state and join its workers.
    let child = process
    Task { @MainActor in
      try? await Task.sleep(for: .seconds(5))
      if child.isRunning { child.terminate() }
    }
  }
  var isRunning: Bool { process.isRunning && !stopped }
  func waitForExit() async {
    let deadline = Date.now.addingTimeInterval(6)
    while process.isRunning && Date.now < deadline { try? await Task.sleep(for: .milliseconds(50)) }
    if process.isRunning { process.terminate() }
    while process.isRunning { try? await Task.sleep(for: .milliseconds(50)) }
  }

  nonisolated static func inspect(file: URL, executable: URL) async throws -> TorrentEngineEvent {
    try await Task.detached {
      let process = Process()
      let output = Pipe()
      process.executableURL = executable
      process.arguments = ["inspect", file.path]
      process.standardOutput = output
      process.standardError = FileHandle.nullDevice
      try process.run()
      let data = try output.fileHandleForReading.readToEnd() ?? Data()
      process.waitUntilExit()
      guard process.terminationStatus == 0, data.count <= 2 * 1024 * 1024 else {
        throw TorrentError.database(
          "The torrent file is invalid, unsafe, or contains no supported videos.")
      }
      let event = try JSONDecoder().decode(TorrentEngineEvent.self, from: data)
      guard event.event == "metadata" else { throw TorrentError.invalidIdentity }
      return event
    }.value
  }
}
#endif
