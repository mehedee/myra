import Foundation

/// iPadOS cannot run aria2c, so this replaces `Aria2Controller` with a URLSession background
/// session. It keeps the same API and status strings ("active", "paused", "complete", "error",
/// "removed"), so `AppCoordinator` drives it like aria2.
///
/// File layout matches the aria2 contract that the coordinator relies on:
/// - The destination file appears only when the transfer has finished.
/// - `<destination>.aria2` holds resume data while a transfer is paused or interrupted.
/// - An existing destination file is never replaced (aria2 runs with `--allow-overwrite=false`).
@MainActor
final class Aria2Controller {
  static let backgroundSessionIdentifier = "com.mehedee.Myra.iPad.downloads"

  private let engine = PadDownloadEngine(identifier: Aria2Controller.backgroundSessionIdentifier)

  var isRunning: Bool { true }

  func start(settings: AppSettingsSnapshot) async throws {
    await engine.adoptExistingTasks()
  }

  func add(url: URL, destination: URL, settings: AppSettingsSnapshot) async throws -> String {
    try FileManager.default.createDirectory(
      at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
    // A transfer iPadOS kept running across a relaunch must be adopted, not started twice.
    await engine.adoptExistingTasks()
    return engine.add(url: url, destination: destination)
  }

  func status(gid: String) async throws -> Aria2Status {
    guard let status = engine.status(gid: gid) else {
      throw Aria2Error.rpc("Download \(gid) is not known to this session.")
    }
    return status
  }

  func pause(gid: String) async throws { await engine.pause(gid: gid) }

  func resume(gid: String) async throws {
    guard engine.resume(gid: gid) else {
      throw Aria2Error.rpc("Download \(gid) is not known to this session.")
    }
  }

  func remove(gid: String) async throws { engine.remove(gid: gid) }

  /// Background transfers keep running after Myra closes, so shutdown leaves them alone.
  func shutdown() async {}
  func terminateImmediately() {}

  /// There is no executable to find on iPadOS. Settings shows the built-in engine instead.
  static func discoverExecutable(override: String) throws -> URL {
    URL(fileURLWithPath: "/System/Library/Frameworks/CFNetwork.framework")
  }

  /// Called from the app delegate when iPadOS wakes Myra to finish background transfers.
  static var backgroundCompletionHandler: (() -> Void)? {
    get { PadDownloadEngine.backgroundCompletionHandler }
    set { PadDownloadEngine.backgroundCompletionHandler = newValue }
  }
}

final class PadDownloadEngine: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
  nonisolated(unsafe) static var backgroundCompletionHandler: (() -> Void)?

  private struct Transfer {
    var url: URL
    var destination: URL
    var task: URLSessionDownloadTask?
    /// The task whose callbacks may change this record. Callbacks from any other task are stale.
    var taskID: Int?
    var state: String
    var total: Int64
    var completed: Int64
    var speed: Int64
    var error: String?
    var lastSample: (date: Date, bytes: Int64)
  }

  private let lock = NSLock()
  private var transfers: [String: Transfer] = [:]
  private var session: URLSession!
  private var adoption: Task<Void, Never>?

  init(identifier: String) {
    super.init()
    let configuration = URLSessionConfiguration.background(withIdentifier: identifier)
    configuration.sessionSendsLaunchEvents = true
    configuration.isDiscretionary = false
    configuration.allowsCellularAccess = true
    session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
  }

  /// Re-attaches transfers that iPadOS kept running while Myra was not in memory. Every caller
  /// waits for the same single pass, so no transfer starts before adoption has finished.
  func adoptExistingTasks() async {
    let pass = lock.withLock { () -> Task<Void, Never> in
      if let adoption { return adoption }
      let pass = Task { [session] in
        let tasks = await session!.allTasks
        self.adopt(tasks)
      }
      adoption = pass
      return pass
    }
    await pass.value
  }

  private func adopt(_ tasks: [URLSessionTask]) {
    lock.withLock {
      for case let task as URLSessionDownloadTask in tasks {
        guard let gid = task.taskDescription, let url = task.originalRequest?.url,
          let destinationPath = Self.destinationPath(from: gid), transfers[gid] == nil
        else { continue }
        transfers[gid] = Transfer(
          url: url, destination: URL(fileURLWithPath: destinationPath), task: task,
          taskID: task.taskIdentifier, state: "active",
          total: max(0, task.countOfBytesExpectedToReceive),
          completed: task.countOfBytesReceived, speed: 0, error: nil,
          lastSample: (.now, task.countOfBytesReceived))
      }
    }
  }

  func add(url: URL, destination: URL) -> String {
    let gid = Self.gid(for: destination)
    let existing = lock.withLock { transfers[gid] }
    if let existing, existing.url == url {
      if existing.state == "active" { return gid }
      // Finished while a pause or cancel was in flight: report it, do not download it again.
      if existing.state == "complete",
        FileManager.default.fileExists(atPath: destination.path)
      {
        return gid
      }
    }
    existing?.task?.cancel()

    let task: URLSessionDownloadTask
    if let resumeData = Self.resumeData(at: destination, for: url) {
      task = session.downloadTask(withResumeData: resumeData)
    } else {
      task = session.downloadTask(with: url)
    }
    task.taskDescription = gid
    lock.withLock {
      transfers[gid] = Transfer(
        url: url, destination: destination, task: task, taskID: task.taskIdentifier,
        state: "active", total: 0, completed: 0, speed: 0, error: nil, lastSample: (.now, 0))
    }
    task.resume()
    return gid
  }

  func status(gid: String) -> Aria2Status? {
    lock.withLock {
      guard let transfer = transfers[gid] else { return nil }
      return Aria2Status(
        gid: gid, status: transfer.state, totalLength: transfer.total,
        completedLength: transfer.completed, downloadSpeed: transfer.speed,
        errorMessage: transfer.error)
    }
  }

  func pause(gid: String) async {
    guard let (task, taskID) = lock.withLock({ () -> (URLSessionDownloadTask, Int)? in
      guard let transfer = transfers[gid], transfer.state == "active",
        let task = transfer.task, let taskID = transfer.taskID
      else { return nil }
      return (task, taskID)
    }) else { return }
    let data = await task.cancelByProducingResumeData()
    lock.withLock {
      // The transfer may have finished, failed or been replaced while the cancel was in flight.
      guard var transfer = transfers[gid], transfer.taskID == taskID,
        transfer.state == "active"
      else { return }
      if let data { try? data.write(to: Self.resumeURL(for: transfer.destination)) }
      transfer.task = nil
      transfer.taskID = nil
      transfer.state = "paused"
      transfer.speed = 0
      transfers[gid] = transfer
    }
  }

  func resume(gid: String) -> Bool {
    guard let transfer = lock.withLock({ transfers[gid] }) else { return false }
    _ = add(url: transfer.url, destination: transfer.destination)
    return true
  }

  func remove(gid: String) {
    let task = lock.withLock { () -> URLSessionDownloadTask? in
      guard var transfer = transfers[gid] else { return nil }
      let task = transfer.task
      transfer.state = "removed"
      transfer.task = nil
      transfer.taskID = nil
      transfer.speed = 0
      transfers[gid] = transfer
      return task
    }
    task?.cancel()
  }

  // MARK: URLSessionDownloadDelegate

  func urlSession(
    _ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64
  ) {
    update(downloadTask) { transfer in
      let now = Date.now
      let elapsed = now.timeIntervalSince(transfer.lastSample.date)
      if elapsed >= 1 {
        transfer.speed = Int64(Double(totalBytesWritten - transfer.lastSample.bytes) / elapsed)
        transfer.lastSample = (now, totalBytesWritten)
      }
      transfer.completed = totalBytesWritten
      if totalBytesExpectedToWrite > 0 { transfer.total = totalBytesExpectedToWrite }
    }
  }

  func urlSession(
    _ session: URLSession, downloadTask: URLSessionDownloadTask,
    didFinishDownloadingTo location: URL
  ) {
    // The temporary file is deleted when this method returns, so it is moved under the lock.
    // Holding the lock means a pause or removal cannot slip in between the check and the move.
    lock.withLock {
      guard let gid = downloadTask.taskDescription, var transfer = transfers[gid],
        transfer.taskID == downloadTask.taskIdentifier, transfer.state == "active"
      else { return }
      defer { transfers[gid] = transfer }
      let destination = transfer.destination
      if let response = downloadTask.response as? HTTPURLResponse,
        !(200..<300).contains(response.statusCode)
      {
        transfer.state = "error"
        transfer.error = "The server answered HTTP \(response.statusCode)."
        return
      }
      let fileManager = FileManager.default
      guard !fileManager.fileExists(atPath: destination.path) else {
        transfer.state = "error"
        transfer.error = "A file already exists at \(destination.lastPathComponent). It was kept."
        return
      }
      do {
        try fileManager.createDirectory(
          at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try fileManager.moveItem(at: location, to: destination)
        try? fileManager.removeItem(at: Self.resumeURL(for: destination))
        let size =
          (try? fileManager.attributesOfItem(atPath: destination.path)[.size] as? NSNumber)?
          .int64Value ?? 0
        transfer.state = "complete"
        transfer.completed = size
        transfer.total = max(transfer.total, size)
        transfer.speed = 0
        transfer.task = nil
      } catch {
        transfer.state = "error"
        transfer.error = error.localizedDescription
      }
    }
  }

  func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?)
  {
    guard let error else { return }
    let nsError = error as NSError
    update(task) { transfer in
      // A pause or removal cancels the task on purpose and has already set the state.
      guard transfer.state == "active" else { return }
      if let data = nsError.userInfo[NSURLSessionDownloadTaskResumeData] as? Data {
        try? data.write(to: Self.resumeURL(for: transfer.destination))
      }
      transfer.state = nsError.code == NSURLErrorCancelled ? "paused" : "error"
      transfer.error = nsError.code == NSURLErrorCancelled ? nil : error.localizedDescription
      transfer.speed = 0
      transfer.task = nil
      transfer.taskID = nil
    }
  }

  func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
    DispatchQueue.main.async {
      Self.backgroundCompletionHandler?()
      Self.backgroundCompletionHandler = nil
    }
  }

  // MARK: Helpers

  /// Applies a change only when `task` is still the transfer's current task.
  private func update(_ task: URLSessionTask, _ change: (inout Transfer) -> Void) {
    guard let gid = task.taskDescription else { return }
    lock.withLock {
      guard var transfer = transfers[gid], transfer.taskID == task.taskIdentifier else { return }
      change(&transfer)
      transfers[gid] = transfer
    }
  }

  /// The identifier carries the destination, so transfers survive an app relaunch.
  static func gid(for destination: URL) -> String {
    "myra:" + destination.standardizedFileURL.path
  }

  static func destinationPath(from gid: String) -> String? {
    gid.hasPrefix("myra:") ? String(gid.dropFirst(5)) : nil
  }

  static func resumeURL(for destination: URL) -> URL {
    URL(fileURLWithPath: destination.path + ".aria2")
  }

  /// Resume data for `url` at `destination`. Data saved for a different source is discarded,
  /// so a new source can never complete with another server's bytes.
  static func resumeData(at destination: URL, for url: URL) -> Data? {
    let sidecar = resumeURL(for: destination)
    guard let data = try? Data(contentsOf: sidecar) else { return nil }
    let info =
      (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any]
    if let saved = info?["NSURLSessionDownloadURL"] as? String, saved == url.absoluteString {
      return data
    }
    try? FileManager.default.removeItem(at: sidecar)
    return nil
  }
}
