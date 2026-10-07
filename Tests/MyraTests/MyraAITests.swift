import XCTest

@testable import Myra

private actor MyraFixtureTransport: MyraAITransport {
  var calls = 0
  let delay: Bool
  let intentJSON: String
  init(delay: Bool = false, intentJSON: String = "{}") {
    self.delay = delay
    self.intentJSON = intentJSON
  }
  func generate(
    provider: MyraAIProvider, model: String, key: String, instructions: String, prompt: String,
    maximumTokens: Int, language: String
  ) async throws -> String {
    calls += 1
    if delay { try await Task.sleep(for: .seconds(2)) }
    if instructions.contains("Extract catalogue") { return intentJSON }
    return
      "{\"summary\":\"Saved catalogue facts\",\"recommendations\":[{\"titleID\":\"t1\",\"reason\":\"Indexed title\"}],\"actions\":[],\"tags\":[]}"
  }
  func models(provider: MyraAIProvider, key: String) async throws -> [String] { ["fixture"] }
}

private actor MyraLiveFixtureCapture: MyraAITransport {
  private(set) var responses: [String] = []
  let client = MyraAIProviderClient()
  func generate(
    provider: MyraAIProvider, model: String, key: String, instructions: String, prompt: String,
    maximumTokens: Int, language: String
  ) async throws -> String {
    let result = try await client.generate(
      provider: provider, model: model, key: key, instructions: instructions, prompt: prompt,
      maximumTokens: maximumTokens, language: language)
    responses.append(result)
    return result
  }
  func models(provider: MyraAIProvider, key: String) async throws -> [String] {
    try await client.models(provider: provider, key: key)
  }
}

final class MyraAITests: XCTestCase {
  private func title(kind: EntertainmentKind = .movie) -> EntertainmentTitle {
    let root = URL(string: "https://private.example/Movies/")!
    let name = "Arrival.2016.1080p.mkv"
    let media = GlobalSearchResult(
      categoryID: UUID(), categoryName: "Private", categoryRoot: root,
      entry: DirectoryEntry(name: name, url: root.appending(path: name), kind: .file),
      relativePath: name, artworkURL: nil)
    return .init(
      id: "arrival", name: "Arrival", year: "2016", kind: kind,
      versions: [
        .init(
          media: media, firstDiscovered: .now, duration: 1800, season: kind == .series ? 1 : nil,
          episode: kind == .series ? 1 : nil)
      ],
      metadata: .init(
        providerID: 1, title: "Arrival", overview: "A thoughtful story", posterURL: nil, rating: 8,
        voteCount: 100, releaseDate: "2016-01-01", genres: ["Drama"], language: "en",
        runtimeMinutes: 120))
  }
  func testSparseSmartPickIgnoresAbsentAndInventedConstraints() throws {
    var sparse = title()
    sparse.metadata = nil
    sparse.versions[0].duration = 0
    let defaults = MyraSearchIntent(
      query: "something uplifting", genre: "", language: "en", kind: "any",
      minimumYear: 2020, maximumYear: 0, minimumRating: 8, maximumMinutes: 120)
    let context = try MyraAIContext.prepare(
      titles: [sparse], personal: .init(), config: .init(), feature: .smartPick,
      prompt: "Pick something uplifting", selectedIDs: [], intent: defaults, subtitleText: nil)
    XCTAssertEqual(context.aliases.values.map(\.id), [sparse.id])
    XCTAssertNil(context.maximumMinutes)
  }

  func testExplicitConstraintsRemainStrictAndKindsAcceptAliases() throws {
    let intent = MyraSearchIntent(genre: "Drama", language: "en", kind: "movies", minimumYear: 2015)
    let matched = try MyraAIContext.prepare(
      titles: [title()], personal: .init(), config: .init(), feature: .smartPick,
      prompt: "English drama movies after 2015", selectedIDs: [], intent: intent, subtitleText: nil)
    XCTAssertEqual(matched.aliases.count, 1)
    var sparse = title()
    sparse.metadata = nil
    let missing = try MyraAIContext.prepare(
      titles: [sparse], personal: .init(), config: .init(), feature: .smartPick,
      prompt: "English drama movies after 2015", selectedIDs: [], intent: intent, subtitleText: nil)
    XCTAssertTrue(missing.aliases.isEmpty)
  }

  func testSpecificNaturalSearchRemainsStrict() throws {
    let context = try MyraAIContext.prepare(
      titles: [title()], personal: .init(), config: .init(), feature: .naturalSearch,
      prompt: "Find Interstellar", selectedIDs: [], intent: .init(query: "Interstellar"),
      subtitleText: nil)
    XCTAssertTrue(context.aliases.isEmpty)
  }

  func testUnknownRuntimeIsAllowedForPickButExcludedForTimedPlan() throws {
    var sparse = title()
    sparse.metadata = nil
    sparse.versions[0].duration = 0
    let pick = try MyraAIContext.prepare(
      titles: [sparse], personal: .init(), config: .init(), feature: .smartPick,
      prompt: "Pick a movie", selectedIDs: [], intent: .init(kind: "movie"), subtitleText: nil)
    XCTAssertEqual(pick.aliases.count, 1)
    let plan = try MyraAIContext.prepare(
      titles: [sparse], personal: .init(), config: .init(), feature: .plan,
      prompt: "120 minutes", selectedIDs: [], intent: .init(maximumMinutes: 120), subtitleText: nil)
    XCTAssertTrue(plan.aliases.isEmpty)
    XCTAssertEqual(plan.maximumMinutes, 120)
  }

  func testFalseHistoryDefaultsDoNotExcludeTitles() throws {
    let context = try MyraAIContext.prepare(
      titles: [title()], personal: .init(), config: .init(), feature: .smartPick,
      prompt: "", selectedIDs: [],
      intent: .init(unwatched: false, unfinished: false, watchlisted: false), subtitleText: nil)
    XCTAssertEqual(context.aliases.count, 1)
  }

  @MainActor func testSparsePickCompletesAndPlanReportsRuntimeOnlyForPlan() async throws {
    let suite = "Myra-sparse-pick-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let transport = MyraFixtureTransport(
      intentJSON:
        "{\"query\":\"something uplifting\",\"genre\":\"\",\"language\":\"en\",\"kind\":\"\",\"maximumYear\":0}"
    )
    let ai = MyraAIStore(defaults: defaults, transport: transport)
    var sparse = title()
    sparse.metadata = nil
    sparse.versions[0].duration = 0
    await ai.execute(
      feature: .smartPick, prompt: "Pick something uplifting", titles: [sparse],
      personal: .init(), revision: 1)
    XCTAssertNil(ai.errorMessage)
    XCTAssertEqual(ai.response?.recommendations.first?.titleID, sparse.id)
    await ai.execute(
      feature: .plan, prompt: "120 minutes", titles: [sparse], personal: .init(), revision: 1)
    XCTAssertNil(ai.response)
    XCTAssertTrue(ai.errorMessage?.contains("known runtime") ?? false)
    var watched = EntertainmentPersonalData()
    watched.watched.insert(sparse.id)
    await ai.execute(
      feature: .smartPick, prompt: "", titles: [sparse], personal: watched, revision: 2)
    XCTAssertTrue(ai.errorMessage?.contains("unwatched") ?? false)
    XCTAssertFalse(ai.errorMessage?.contains("runtime") ?? true)
    await ai.execute(
      feature: .plan, prompt: "Plan tonight", titles: [sparse], personal: .init(), revision: 3)
    XCTAssertTrue(ai.errorMessage?.contains("Enter a time budget") ?? false)
  }

  func testContextSanitizesPrivateLinksAndOmitCloudHistory() throws {
    var config = MyraAISettings()
    config.provider = .openAI
    var personal = EntertainmentPersonalData()
    personal.watchlist.insert("arrival")
    let context = try MyraAIContext.prepare(
      titles: [title()], personal: personal, config: config, feature: .summary,
      prompt: "https://10.0.0.1/secret /Users/mehedee/private", selectedIDs: ["arrival"],
      intent: nil, subtitleText: nil)
    XCTAssertFalse(context.prompt.contains("10.0.0.1"))
    XCTAssertFalse(context.prompt.contains("private.example"))
    XCTAssertFalse(context.prompt.contains("/Users/"))
    XCTAssertFalse(context.prompt.contains("watchlisted"))
  }
  @MainActor func testUnknownIDsAndOverBudgetPlanAreRejected() throws {
    let wire = MyraAIWireResponse(
      summary: "Test", recommendations: [.init(titleID: "unknown", reason: "Test")], actions: [],
      tags: [])
    XCTAssertThrowsError(
      try MyraAIStore.validate(
        wire, aliases: ["t1": title()], feature: .summary, maximumMinutes: nil, provider: "fixture")
    )
    let plan = MyraAIWireResponse(
      summary: "Test", recommendations: [.init(titleID: "t1", reason: "Test")], actions: [],
      tags: [])
    XCTAssertThrowsError(
      try MyraAIStore.validate(
        plan, aliases: ["t1": title()], feature: .plan, maximumMinutes: 10, provider: "fixture"))
    let valid = try MyraAIStore.validate(
      plan, aliases: ["t1": title(kind: .series)], feature: .plan, maximumMinutes: 30,
      provider: "fixture")
    XCTAssertEqual(valid.recommendations.first?.versionID, title(kind: .series).versions.first?.id)
  }
  func testPlanNeverBorrowsAnotherEpisodesDuration() {
    var series = title(kind: .series)
    series.metadata = nil
    var next = series.versions[0]
    next.episode = 2
    let old = next.media
    next.media = .init(
      categoryID: old.categoryID, categoryName: old.categoryName, categoryRoot: old.categoryRoot,
      entry: .init(
        name: "Other.S01E02.mkv",
        url: URL(string: "https://private.example/Series/Other.S01E02.mkv")!, kind: .file),
      relativePath: "Other.S01E02.mkv", artworkURL: nil)
    series.versions[0].duration = 0
    series.versions.append(next)
    XCTAssertNil(MyraAIContext.runtime(series))
  }
  func testHistoryIntentAndRecapContextAreRestricted() throws {
    var intent = MyraSearchIntent()
    intent.watchlisted = true
    let context = try MyraAIContext.prepare(
      titles: [title()], personal: .init(), config: .init(), feature: .assistant,
      prompt: "watchlist", selectedIDs: [], intent: intent, subtitleText: nil)
    XCTAssertTrue(context.aliases.isEmpty)
    let recap = try MyraAIContext.prepare(
      titles: [title(kind: .series)], personal: .init(), config: .init(), feature: .recap,
      prompt: "recap", selectedIDs: ["arrival"], intent: nil,
      subtitleText: "Only this completed episode")
    XCTAssertFalse(recap.prompt.contains("A thoughtful story"))
    XCTAssertTrue(recap.prompt.contains("Only this completed episode"))
  }
  @MainActor func testCacheQuotaAndCancellationWhenSettingsChange() async throws {
    let suite = "Myra-engine-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let transport = MyraFixtureTransport()
    let ai = MyraAIStore(defaults: defaults, transport: transport)
    await ai.execute(
      feature: .summary, prompt: "summary", titles: [title()], personal: .init(), revision: 1,
      selectedIDs: ["arrival"])
    XCTAssertNotNil(ai.response)
    await ai.execute(
      feature: .summary, prompt: "summary", titles: [title()], personal: .init(), revision: 1,
      selectedIDs: ["arrival"])
    let calls = await transport.calls
    XCTAssertEqual(calls, 1)
    ai.settings.dailyRequestLimit = 1
    await ai.execute(
      feature: .summary, prompt: "new", titles: [title()], personal: .init(), revision: 1,
      selectedIDs: ["arrival"])
    XCTAssertNotNil(ai.errorMessage)
    let slow = MyraAIStore(defaults: defaults, transport: MyraFixtureTransport(delay: true))
    slow.settings.dailyRequestLimit = 20
    let run = Task {
      await slow.execute(
        feature: .summary, prompt: "summary", titles: [title()], personal: .init(), revision: 1,
        selectedIDs: ["arrival"])
    }
    try await Task.sleep(for: .milliseconds(50))
    slow.settings.provider = .disabled
    await run.value
    XCTAssertFalse(slow.isBusy)
    XCTAssertNil(slow.response)
  }
  @MainActor
  func testEveryFeatureProducesGroundedFixtureResults() async throws {
    let suite = "Myra-all-features-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let ai = MyraAIStore(defaults: defaults, transport: MyraFixtureTransport())
    ai.settings.dailyRequestLimit = 100
    var personal = EntertainmentPersonalData()
    personal.watched.insert("arrival")
    for feature in MyraAIFeature.allCases {
      let candidate = title(kind: feature == .recap ? .series : .movie)
      var reference = candidate
      reference.id = "reference"
      await ai.execute(
        feature: feature, prompt: "120 minutes",
        titles: feature == .similar ? [reference, candidate] : [candidate],
        personal: feature == .smartPick ? .init() : personal, revision: 1,
        selectedIDs: feature == .similar ? [reference.id] : [candidate.id],
        subtitleText: feature == .recap ? "The completed episode ends with a reunion." : nil,
        recapVersionID: feature == .recap ? candidate.versions.first?.id : nil)
      XCTAssertNil(ai.errorMessage, "\(feature): \(ai.errorMessage ?? "")")
      XCTAssertEqual(ai.response?.recommendations.first?.titleID, candidate.id, "\(feature)")
    }
  }
  func testShrinkingShortlistPreservesFullStatisticsAndHistoryConsent() throws {
    let values = (0..<30).map { offset in
      var value = title()
      value.id = "t\(offset)"
      value.name = "Long \(offset)"
      value.metadata?.overview = String(repeating: "long description ", count: 100)
      return value
    }
    let context = try MyraAIContext.prepare(
      titles: values, personal: .init(), config: .init(), feature: .insights,
      prompt: "statistics", selectedIDs: [], intent: nil, subtitleText: nil)
    XCTAssertTrue(context.prompt.contains("titles=30"))
    XCTAssertLessThanOrEqual(context.prompt.utf8.count, 4000)
    var cloud = MyraAISettings()
    cloud.provider = .openAI
    var intent = MyraSearchIntent()
    intent.unfinished = true
    XCTAssertThrowsError(
      try MyraAIContext.prepare(
        titles: values, personal: .init(), config: cloud, feature: .assistant,
        prompt: "unfinished", selectedIDs: [], intent: intent, subtitleText: nil))
  }
  @MainActor
  func testAvailableAppleModelGeneratesGroundedSummaryWhenOptedIn() async throws {
    guard ProcessInfo.processInfo.environment["MYRA_APPLE_LIVE"] == "1" else {
      throw XCTSkip("Opt-in on-device generation")
    }
    if let reason = MyraAIProviderClient.appleAvailability() { throw XCTSkip(reason) }
    let suite = "Myra-apple-live-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let capture = MyraLiveFixtureCapture()
    let ai = MyraAIStore(defaults: defaults, transport: capture)
    await ai.execute(
      feature: .summary, prompt: "Summarise only the supplied description", titles: [title()],
      personal: .init(), revision: 1, selectedIDs: ["arrival"])
    if ai.errorMessage != nil {
      print("Controlled Apple fixture responses: \(await capture.responses)")
    }
    XCTAssertNil(ai.errorMessage)
    XCTAssertNotNil(ai.response)
    var sparse = title()
    sparse.metadata = nil
    sparse.versions[0].duration = 0
    await ai.execute(
      feature: .smartPick, prompt: "Pick something relaxing", titles: [sparse],
      personal: .init(), revision: 2)
    if ai.errorMessage != nil {
      print("Controlled Apple pick fixture responses: \(await capture.responses)")
    }
    XCTAssertNil(ai.errorMessage)
    XCTAssertNotNil(ai.response)
    XCTAssertTrue(ai.response?.recommendations.allSatisfy { $0.titleID == "arrival" } ?? false)
  }
  @MainActor func testCloudConsentBlocksBeforeAnyRequest() async throws {
    let suite = "Myra-consent-\(UUID())"
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let transport = MyraFixtureTransport()
    let ai = MyraAIStore(defaults: defaults, transport: transport)
    ai.settings.provider = .claude
    await ai.execute(
      feature: .summary, prompt: "summary", titles: [title()], personal: .init(), revision: 1,
      selectedIDs: ["arrival"])
    let calls = await transport.calls
    XCTAssertEqual(calls, 0)
    XCTAssertNotNil(ai.errorMessage)
  }
}
