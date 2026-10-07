import Foundation
import XCTest

@testable import Myra

private final class MyraHTTPFixture: URLProtocol, @unchecked Sendable {
  nonisolated(unsafe) static var requests: [URLRequest] = []
  static let lock = NSLock()
  override class func canInit(with request: URLRequest) -> Bool { true }
  override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
  override func startLoading() {
    var captured = request
    if captured.httpBody == nil, let stream = request.httpBodyStream {
      stream.open()
      defer { stream.close() }
      var body = Data()
      var buffer = [UInt8](repeating: 0, count: 1024)
      while stream.hasBytesAvailable {
        let count = stream.read(&buffer, maxLength: buffer.count)
        if count <= 0 { break }
        body.append(contentsOf: buffer.prefix(count))
      }
      captured.httpBody = body
    }
    Self.lock.lock()
    Self.requests.append(captured)
    Self.lock.unlock()
    let data = Data(
      "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"fixture\"}]}],\"content\":[{\"type\":\"text\",\"text\":\"fixture\"}]}"
        .utf8)
    client?.urlProtocol(
      self,
      didReceive: HTTPURLResponse(
        url: request.url!, statusCode: 200, httpVersion: nil,
        headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
    client?.urlProtocol(self, didLoad: data)
    client?.urlProtocolDidFinishLoading(self)
  }
  override func stopLoading() {}
  static func takeRequests() -> [URLRequest] {
    lock.lock()
    defer { lock.unlock() }
    let value = requests
    requests = []
    return value
  }
}
final class MyraAIProviderTests: XCTestCase {
  func testCloudRequestContractsUseFixedEndpointsWithoutStorage() async throws {
    _ = MyraHTTPFixture.takeRequests()
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [MyraHTTPFixture.self]
    let client = MyraAIProviderClient(session: URLSession(configuration: configuration))
    let openAI = try await client.generate(
      provider: .openAI, model: "fixture-model", key: "fixture-key", instructions: "instructions",
      prompt: "catalogue", maximumTokens: 800, language: "en")
    let claude = try await client.generate(
      provider: .claude, model: "fixture-model", key: "fixture-key", instructions: "instructions",
      prompt: "catalogue", maximumTokens: 800, language: "en")
    XCTAssertEqual(openAI, "fixture")
    XCTAssertEqual(claude, "fixture")
    let requests = MyraHTTPFixture.takeRequests()
    XCTAssertEqual(requests.count, 2)
    XCTAssertEqual(requests[0].url?.absoluteString, "https://api.openai.com/v1/responses")
    XCTAssertEqual(requests[0].value(forHTTPHeaderField: "Authorization"), "Bearer fixture-key")
    XCTAssertEqual(requests[1].url?.absoluteString, "https://api.anthropic.com/v1/messages")
    XCTAssertEqual(requests[1].value(forHTTPHeaderField: "anthropic-version"), "2023-06-01")
    let body = try XCTUnwrap(requests[0].httpBody)
    let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
    XCTAssertEqual(json["store"] as? Bool, false)
    XCTAssertEqual(json["max_output_tokens"] as? Int, 800)
  }
  func testRedirectDelegateRejectsCredentialForwarding() {
    let guardDelegate = MyraAIRedirectGuard()
    let session = URLSession(configuration: .ephemeral)
    let task = session.dataTask(with: URL(string: "https://api.openai.com/v1/models")!)
    let response = HTTPURLResponse(
      url: task.originalRequest!.url!, statusCode: 302, httpVersion: nil, headerFields: nil)!
    guardDelegate.urlSession(
      session, task: task, willPerformHTTPRedirection: response,
      newRequest: URLRequest(url: URL(string: "https://untrusted.example/")!)
    ) { request in XCTAssertNil(request) }
    session.invalidateAndCancel()
  }
}
