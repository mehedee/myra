import Foundation

enum Aria2Error: LocalizedError {
  case executableNotFound
  case launchFailed(String)
  case rpc(String)
  case invalidResponse

  var errorDescription: String? {
    switch self {
    case .executableNotFound:
      "aria2c was not found. Install it with Homebrew or choose its path in Settings."
    case .launchFailed(let message): "aria2c could not start: \(message)"
    case .rpc(let message): "aria2 reported: \(message)"
    case .invalidResponse: "aria2 returned an invalid response."
    }
  }
}

private enum JSONValue: Encodable, Sendable {
  case string(String)
  case strings([String])
  case dictionary([String: String])

  func encode(to encoder: Encoder) throws {
    var container = encoder.singleValueContainer()
    switch self {
    case .string(let value): try container.encode(value)
    case .strings(let value): try container.encode(value)
    case .dictionary(let value): try container.encode(value)
    }
  }
}

private struct RPCRequest: Encodable, Sendable {
  let jsonrpc = "2.0"
  let id: String
  let method: String
  let params: [JSONValue]
}

private struct RPCErrorPayload: Decodable, Sendable {
  let code: Int
  let message: String
}

private struct RPCResponse<Result: Decodable & Sendable>: Decodable, Sendable {
  let result: Result?
  let error: RPCErrorPayload?
}

private struct RawAria2Status: Decodable, Sendable {
  let gid: String
  let status: String
  let totalLength: String
  let completedLength: String
  let downloadSpeed: String
  let errorMessage: String?
}

actor Aria2RPCClient {
  private let endpoint: URL
  private let secret: String
  private let session: URLSession

  init(endpoint: URL, secret: String, session: URLSession = .shared) {
    self.endpoint = endpoint
    self.secret = secret
    self.session = session
  }

  func ping() async throws {
    let _: String = try await call("aria2.getVersion", params: [], resultKey: "version")
  }

  func add(url: URL, destination: URL, settings: AppSettingsSnapshot) async throws -> String {
    let options = [
      "dir": destination.deletingLastPathComponent().path,
      "out": destination.lastPathComponent,
      "continue": "true",
      "allow-overwrite": "false",
      "auto-file-renaming": "false",
      "max-connection-per-server": String(settings.connectionsPerFile),
      "split": String(settings.splitCount),
      "max-tries": String(settings.retryCount),
      "max-download-limit": settings.speedLimit,
    ]
    return try await call(
      "aria2.addUri", params: [.strings([url.absoluteString]), .dictionary(options)])
  }

  func status(gid: String) async throws -> Aria2Status {
    let keys = ["gid", "status", "totalLength", "completedLength", "downloadSpeed", "errorMessage"]
    let raw: RawAria2Status = try await call(
      "aria2.tellStatus", params: [.string(gid), .strings(keys)])
    return Aria2Status(
      gid: raw.gid,
      status: raw.status,
      totalLength: Int64(raw.totalLength) ?? 0,
      completedLength: Int64(raw.completedLength) ?? 0,
      downloadSpeed: Int64(raw.downloadSpeed) ?? 0,
      errorMessage: raw.errorMessage
    )
  }

  func pause(gid: String) async throws {
    let _: String = try await call("aria2.forcePause", params: [.string(gid)])
  }
  func resume(gid: String) async throws {
    let _: String = try await call("aria2.unpause", params: [.string(gid)])
  }
  func remove(gid: String) async throws {
    let _: String = try await call("aria2.forceRemove", params: [.string(gid)])
  }
  func shutdown() async throws { let _: String = try await call("aria2.shutdown", params: []) }

  private func call<T: Decodable & Sendable>(_ method: String, params: [JSONValue]) async throws
    -> T
  {
    var request = URLRequest(url: endpoint)
    request.httpMethod = "POST"
    request.timeoutInterval = 15
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    request.httpBody = try JSONEncoder().encode(
      RPCRequest(
        id: UUID().uuidString,
        method: method,
        params: [.string("token:\(secret)")] + params
      ))
    let (data, response) = try await session.data(for: request)
    guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
      throw Aria2Error.invalidResponse
    }
    let envelope = try JSONDecoder().decode(RPCResponse<T>.self, from: data)
    if let error = envelope.error { throw Aria2Error.rpc(error.message) }
    guard let result = envelope.result else { throw Aria2Error.invalidResponse }
    return result
  }

  private func call(_ method: String, params: [JSONValue], resultKey: String) async throws -> String
  {
    struct Version: Decodable, Sendable { let version: String }
    let result: Version = try await call(method, params: params)
    return result.version
  }
}

struct AppSettingsSnapshot: Sendable {
  let aria2PathOverride: String
  let concurrentDownloads: Int
  let connectionsPerFile: Int
  let splitCount: Int
  let retryCount: Int
  let speedLimit: String

  init(_ settings: AppSettings) {
    aria2PathOverride = settings.aria2PathOverride
    concurrentDownloads = settings.concurrentDownloads
    connectionsPerFile = settings.connectionsPerFile
    splitCount = settings.splitCount
    retryCount = settings.retryCount
    speedLimit = settings.speedLimit
  }
}

@MainActor
final class Aria2Controller {
  private var process: Process?
  private var client: Aria2RPCClient?

  var isRunning: Bool { process?.isRunning == true }

  func start(settings: AppSettingsSnapshot) async throws {
    if isRunning { return }
    let executable = try Self.discoverExecutable(override: settings.aria2PathOverride)
    let port = Int.random(in: 31_000...49_000)
    let secret = UUID().uuidString.replacingOccurrences(of: "-", with: "")
    let process = Process()
    process.executableURL = executable
    process.arguments = [
      "--enable-rpc=true",
      "--rpc-listen-all=false",
      "--rpc-listen-port=\(port)",
      "--rpc-secret=\(secret)",
      "--max-concurrent-downloads=\(settings.concurrentDownloads)",
      "--continue=true",
      "--auto-file-renaming=false",
      "--allow-overwrite=false",
      "--file-allocation=none",
      "--summary-interval=0",
      "--console-log-level=warn",
    ]
    process.standardOutput = FileHandle.nullDevice
    process.standardError = FileHandle.nullDevice
    do { try process.run() } catch { throw Aria2Error.launchFailed(error.localizedDescription) }

    let client = Aria2RPCClient(
      endpoint: URL(string: "http://127.0.0.1:\(port)/jsonrpc")!, secret: secret)
    self.process = process
    self.client = client
    for attempt in 0..<20 {
      do {
        try await client.ping()
        return
      } catch  where attempt < 19 {
        try await Task.sleep(for: .milliseconds(100))
      }
    }
    process.terminate()
    self.process = nil
    self.client = nil
    throw Aria2Error.launchFailed("the JSON-RPC service did not become ready")
  }

  func add(url: URL, destination: URL, settings: AppSettingsSnapshot) async throws -> String {
    guard let client else { throw Aria2Error.launchFailed("aria2 is not running") }
    try FileManager.default.createDirectory(
      at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
    return try await client.add(url: url, destination: destination, settings: settings)
  }

  func status(gid: String) async throws -> Aria2Status {
    guard let client else { throw Aria2Error.launchFailed("aria2 is not running") }
    return try await client.status(gid: gid)
  }

  func pause(gid: String) async throws { try await client?.pause(gid: gid) }
  func resume(gid: String) async throws { try await client?.resume(gid: gid) }
  func remove(gid: String) async throws { try await client?.remove(gid: gid) }

  func shutdown() async {
    try? await client?.shutdown()
    process?.waitUntilExit()
    process = nil
    client = nil
  }

  func terminateImmediately() {
    if process?.isRunning == true { process?.terminate() }
    process = nil
    client = nil
  }

  static func discoverExecutable(override: String) throws -> URL {
    var candidates: [String] = []
    if !override.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
      candidates.append(override)
    }
    if let path = ProcessInfo.processInfo.environment["PATH"] {
      candidates += path.split(separator: ":").map { String($0) + "/aria2c" }
    }
    candidates += ["/opt/homebrew/bin/aria2c", "/usr/local/bin/aria2c", "/usr/bin/aria2c"]
    if let found = candidates.first(where: FileManager.default.isExecutableFile(atPath:)) {
      return URL(fileURLWithPath: found)
    }
    throw Aria2Error.executableNotFound
  }
}
