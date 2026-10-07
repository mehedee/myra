using System.Text.Json;
using static Myra.Core.Tests.AiFixtures;

namespace Myra.Core.Tests;

/// Orchestration: consent, limits, cache, cancellation, every feature, and privacy of outgoing requests
/// (ported from MyraAITests.swift; HTTP checks use FakeHttpHandler, never the network).
public class AiServiceTests
{
    private static AiRequest Request(AiFeature feature, string prompt, IReadOnlyList<EntertainmentTitle> titles,
        EntertainmentPersonalData? personal = null, long revision = 1, IReadOnlyList<string>? selected = null,
        string? subtitles = null, string? recapVersion = null) => new()
    {
        Feature = feature, Prompt = prompt, Titles = titles, Personal = personal ?? new EntertainmentPersonalData(),
        Revision = revision, SelectedIds = selected ?? [], SubtitleText = subtitles, RecapVersionId = recapVersion,
    };

    [Fact]
    public async Task DefaultsAreDisabledAndNothingRuns()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        Assert.Equal(AiProviderIds.Disabled, fixture.Service.Settings.Provider);
        Assert.Null(fixture.Service.CurrentProvider);
        Assert.Contains("AI disabled", fixture.Service.Status);
        var outcome = await fixture.Service.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        Assert.Equal(AiOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("This AI feature is disabled in Settings.", fixture.Service.ErrorMessage);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task SparsePickCompletesAndPlanReportsRuntimeOnlyForPlan()
    {
        var provider = new FakeAiProvider(intentJson: "{\"query\":\"something uplifting\",\"genre\":\"\",\"language\":\"en\",\"kind\":\"\",\"maximumYear\":0}");
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var sparse = Title(metadata: false, duration: 0);

        await ai.ExecuteAsync(Request(AiFeature.SmartPick, "Pick something uplifting", [sparse]));
        Assert.Null(ai.ErrorMessage);
        Assert.Equal(sparse.Id, ai.Response?.Recommendations[0].TitleId);

        await ai.ExecuteAsync(Request(AiFeature.Plan, "120 minutes", [sparse]));
        Assert.Null(ai.Response);
        Assert.Contains("known runtime", ai.ErrorMessage);

        var watched = new EntertainmentPersonalData();
        watched.Watched.Add(sparse.Id);
        await ai.ExecuteAsync(Request(AiFeature.SmartPick, "", [sparse], watched, revision: 2));
        Assert.Contains("unwatched", ai.ErrorMessage);
        Assert.DoesNotContain("runtime", ai.ErrorMessage);

        await ai.ExecuteAsync(Request(AiFeature.Plan, "Plan tonight", [sparse], revision: 3));
        Assert.Contains("Enter a time budget", ai.ErrorMessage);
    }

    [Fact]
    public async Task CacheQuotaAndCancellationWhenSettingsChange()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var request = Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]);

        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(request)).Status);
        Assert.NotNull(ai.Response);
        Assert.Equal(AiOutcomeStatus.Cached, (await ai.ExecuteAsync(request)).Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, ai.RequestsToday);
        Assert.True(ai.EstimatedInputTokens > 0);
        Assert.True(ai.EstimatedOutputTokens > 0);

        ai.Settings = ai.Settings with { DailyRequestLimit = 1 };
        var limited = await ai.ExecuteAsync(request with { Prompt = "new" });
        Assert.Equal(AiErrorKind.Limit, limited.Error?.Kind);
        Assert.NotNull(ai.ErrorMessage);
        Assert.Equal(1, provider.Calls);

        var slowProvider = new FakeAiProvider(delay: TimeSpan.FromSeconds(5));
        using var slowFixture = new AiServiceFixture(slowProvider);
        var slow = slowFixture.Service;
        slow.Settings = slow.Settings with { Provider = "fixture", DailyRequestLimit = 20 };
        var run = slow.ExecuteAsync(request);
        for (var i = 0; i < 100 && slowProvider.Calls == 0; i++) await Task.Delay(10);
        Assert.True(slow.IsBusy);
        Assert.Equal(AiOutcomeStatus.Busy, (await slow.ExecuteAsync(request)).Status);
        slow.Settings = slow.Settings with { Provider = AiProviderIds.Disabled };
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AiOutcomeStatus.Cancelled, outcome.Status);
        Assert.False(slow.IsBusy);
        Assert.Null(slow.Response);
        Assert.Null(slow.ErrorMessage);
    }

    [Fact]
    public async Task ExplicitCancelDiscardsTheResult()
    {
        var provider = new FakeAiProvider(delay: TimeSpan.FromSeconds(5));
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var run = ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        for (var i = 0; i < 100 && provider.Calls == 0; i++) await Task.Delay(10);
        ai.Cancel();
        Assert.Equal(AiOutcomeStatus.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Null(ai.Response);
    }

    [Fact]
    public async Task DailyLimitResetsOnANewLocalDayAndCountsIntentGenerations()
    {
        var now = new DateTimeOffset(2026, 10, 8, 23, 0, 0, TimeSpan.Zero);
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(() => now, provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture", DailyRequestLimit = 2 };
        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(Request(AiFeature.SmartPick, "pick", [Title()]))).Status);
        Assert.Equal(2, ai.RequestsToday);
        Assert.Equal(AiErrorKind.Limit, (await ai.ExecuteAsync(Request(AiFeature.SmartPick, "another", [Title()]))).Error?.Kind);
        now = now.AddHours(2);
        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(Request(AiFeature.SmartPick, "tomorrow", [Title()]))).Status);
        Assert.Equal(2, ai.RequestsToday);
        Assert.Contains("\"requests\":2", File.ReadAllText(fixture.UsagePath));
    }

    [Fact]
    public async Task EveryFeatureProducesGroundedFixtureResults()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture", DailyRequestLimit = 100 };
        var personal = new EntertainmentPersonalData();
        personal.Watched.Add("arrival");
        Assert.Equal(16, AiFeatures.All.Count);
        foreach (var feature in AiFeatures.All)
        {
            var candidate = Title(feature == AiFeature.Recap ? EntertainmentKind.Series : EntertainmentKind.Movie);
            var reference = candidate with { Id = "reference" };
            var outcome = await ai.ExecuteAsync(Request(feature, "120 minutes",
                feature == AiFeature.Similar ? [reference, candidate] : [candidate],
                feature == AiFeature.SmartPick ? new EntertainmentPersonalData() : personal,
                selected: feature == AiFeature.Similar ? [reference.Id] : [candidate.Id],
                subtitles: feature == AiFeature.Recap ? "The completed episode ends with a reunion." : null,
                recapVersion: feature == AiFeature.Recap ? candidate.Versions[0].Id : null));
            Assert.True(ai.ErrorMessage is null, $"{feature}: {ai.ErrorMessage}");
            Assert.Equal(AiOutcomeStatus.Completed, outcome.Status);
            Assert.Equal(candidate.Id, ai.Response?.Recommendations[0].TitleId);
            if (feature == AiFeature.Plan) Assert.Equal(candidate.Versions[0].Id, ai.Response?.Recommendations[0].VersionId);
        }
    }

    [Fact]
    public async Task CloudConsentBlocksBeforeAnyRequest()
    {
        var provider = new FakeAiProvider(id: AiProviderIds.Claude, cloud: true);
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        fixture.Secrets.Save(SecretNames.OpenAiApiKey, "fixture-key");
        ai.Settings = ai.Settings with { Provider = AiProviderIds.Claude };
        Assert.Contains("may incur charges", ai.Status);
        var outcome = await ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        Assert.Equal(AiErrorKind.Consent, outcome.Error?.Kind);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, ai.RequestsToday);

        ai.Settings = ai.Settings with { CloudConsent = true };
        var series = Title(EntertainmentKind.Series);
        var personal = new EntertainmentPersonalData();
        personal.Watched.Add(series.Id);
        var recap = await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], personal, selected: [series.Id],
            subtitles: "Subtitle text", recapVersion: series.Versions[0].Id));
        Assert.Equal(AiErrorKind.Consent, recap.Error?.Kind);
        Assert.Contains("subtitle", recap.Error!.Message);
        Assert.Equal(0, provider.Calls);

        ai.Settings = ai.Settings with { ShareSubtitles = true };
        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], personal, selected: [series.Id],
            subtitles: "Subtitle text", recapVersion: series.Versions[0].Id))).Status);
    }

    [Fact]
    public async Task CloudRequestsNeedAKeyAndHistoryPermission()
    {
        var provider = new FakeAiProvider(id: AiProviderIds.OpenAI, cloud: true, intentJson: "{\"unwatched\":true}");
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = AiProviderIds.OpenAI, CloudConsent = true, Model = "fixture-model" };
        var missing = await ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        Assert.Equal(AiErrorKind.MissingKey, missing.Error?.Kind);
        Assert.Equal(0, provider.Calls);

        ai.SaveKey("  fixture-key  ");
        Assert.True(ai.HasKey());
        Assert.Equal("fixture-key", fixture.Secrets.Read(SecretNames.OpenAiApiKey));
        Assert.Throws<AiException>(() => ai.SaveKey("bad\nkey"));

        var history = await ai.ExecuteAsync(Request(AiFeature.NaturalSearch, "unwatched dramas", [Title()]));
        Assert.Equal(AiErrorKind.Consent, history.Error?.Kind);
        Assert.Equal(AiPermission.History, history.Error?.Permission);
        Assert.Contains("history sharing", history.Error!.Message);
        Assert.Equal(0, provider.Calls);

        var plain = await ai.ExecuteAsync(Request(AiFeature.NaturalSearch, "thoughtful dramas", [Title()]));
        Assert.Equal(AiOutcomeStatus.Completed, plain.Status);
        Assert.Equal("fixture-key", provider.Requests[0].Key);

        ai.RemoveKey();
        Assert.False(ai.HasKey());
        Assert.DoesNotContain("fixture-key", File.ReadAllText(fixture.SettingsPath));
    }

    [Fact]
    public async Task RecapNeedsACompletedSelectedEpisodeAndSubtitles()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var series = Title(EntertainmentKind.Series, duration: 0);
        var version = series.Versions[0].Id;

        var unwatched = await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], selected: [series.Id], subtitles: "text", recapVersion: version));
        Assert.Contains("completed selected episode", unwatched.Error?.Message);

        var personal = new EntertainmentPersonalData();
        personal.History[version] = new EntertainmentPlaybackRecord(1795, 1800, DateTimeOffset.Now);
        var noSubtitles = await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], personal, selected: [series.Id], subtitles: "  ", recapVersion: version));
        Assert.Equal(AiErrorKind.MissingContext, noSubtitles.Error?.Kind);
        var notSelected = await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], personal, selected: [], subtitles: "text", recapVersion: version));
        Assert.Equal(AiErrorKind.MissingContext, notSelected.Error?.Kind);
        Assert.Equal(0, provider.Calls);

        Assert.Equal(AiOutcomeStatus.Completed,
            (await ai.ExecuteAsync(Request(AiFeature.Recap, "recap", [series], personal, selected: [series.Id], subtitles: "text", recapVersion: version))).Status);
        Assert.Equal([version], AiWorkspace.CompletedEpisodes(series, personal).Select(v => v.Id));
    }

    [Fact]
    public async Task UngroundedAnswersAreRejectedWithoutChanges()
    {
        var provider = new FakeAiProvider(answer: "{\"summary\":\"x\",\"recommendations\":[{\"titleID\":\"movie|other|2020\",\"reason\":\"r\"}],\"actions\":[],\"tags\":[]}");
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var outcome = await ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        Assert.Equal(AiErrorKind.InvalidResponse, outcome.Error?.Kind);
        Assert.Null(ai.Response);
        provider.Answer = "not json";
        Assert.Equal(AiErrorKind.InvalidResponse, (await ai.ExecuteAsync(Request(AiFeature.Summary, "other", [Title()], selected: ["arrival"]))).Error?.Kind);
    }

    [Fact]
    public async Task UnavailableLocalProviderReportsItsReason()
    {
        var provider = new FakeAiProvider { Unavailable = "The local model is not installed." };
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        Assert.Equal("The local model is not installed.", ai.Status);
        await ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]));
        Assert.Equal("The local model is not installed.", ai.ErrorMessage);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void SettingsPersistNormalizedAndSurviveCorruption()
    {
        using var fixture = new AiServiceFixture(new FakeAiProvider());
        fixture.Service.Settings = new AiSettings
        {
            Provider = AiProviderIds.OpenAI, Model = "gpt-fixture", DailyRequestLimit = 9000, MaximumOutputTokens = 10,
            Feedback = "See https://secret.example/x", CloudConsent = true,
        }.WithFeature(AiFeature.Recap, false);
        var reopened = new AiService(fixture.Secrets, [new FakeAiProvider()], fixture.SettingsPath, fixture.UsagePath);
        var settings = reopened.Settings;
        Assert.Equal((AiProviderIds.OpenAI, "gpt-fixture", 500, 256, true), (settings.Provider, settings.Model, settings.DailyRequestLimit, settings.MaximumOutputTokens, settings.CloudConsent));
        Assert.DoesNotContain("secret.example", settings.Feedback);
        Assert.False(settings.IsEnabled(AiFeature.Recap));
        Assert.Equal(15, settings.EnabledFeatures.Count);
        Assert.Contains("\"enabledFeatures\"", File.ReadAllText(fixture.SettingsPath));
        Assert.Equal("", settings.WithProvider(AiProviderIds.Claude).Model);

        File.WriteAllText(fixture.SettingsPath, "{ not json");
        var recovered = new AiService(fixture.Secrets, [new FakeAiProvider()], fixture.SettingsPath, fixture.UsagePath);
        Assert.Equal(AiProviderIds.Disabled, recovered.Settings.Provider);
    }

    [Fact]
    public async Task RefreshModelsListsWithoutCatalogueData()
    {
        var handler = FakeHttpHandler.ByPath(new() { ["/v1/models"] = (200, "{\"data\":[{\"id\":\"b-model\"},{\"id\":\"a-model\"},{\"id\":\"\"}]}") });
        using var fixture = new AiServiceFixture(new OpenAiProvider(handler.Client()));
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = AiProviderIds.OpenAI };
        await ai.RefreshModelsAsync();
        Assert.Contains("Save your own provider API key", ai.Status);
        ai.SaveKey("fixture-key");
        await ai.TestConnectionAsync();
        Assert.Equal(["a-model", "b-model"], ai.Models);
        Assert.StartsWith("2 models listed", ai.Status);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    /// End to end through the real OpenAI and Claude clients with a fake HTTP handler: no source URL,
    /// path, host or real title ID ever leaves in a request body.
    [Theory]
    [InlineData(AiProviderIds.OpenAI)]
    [InlineData(AiProviderIds.Claude)]
    public async Task OutgoingRequestBodiesContainNoUrlsPathsOrRealIds(string providerId)
    {
        var handler = new FakeHttpHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            var text = body.Contains("Extract catalogue") ? "{\"query\":\"Arrival\"}" : GroundedAnswer;
            var payload = JsonSerializer.Serialize(new
            {
                output = new[] { new { content = new[] { new { type = "output_text", text } } } },
                content = new[] { new { type = "text", text } },
            });
            return FakeHttpHandler.Text(200, payload);
        });
        IAiProvider provider = providerId == AiProviderIds.OpenAI ? new OpenAiProvider(handler.Client()) : new ClaudeProvider(handler.Client());
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = providerId, CloudConsent = true, Model = "fixture-model", DailyRequestLimit = 50 };
        ai.SaveKey("fixture-key");
        var title = Title() with { Name = "Arrival" };
        foreach (var feature in new[] { AiFeature.NaturalSearch, AiFeature.Summary, AiFeature.Compare, AiFeature.SmartPick })
        {
            var outcome = await ai.ExecuteAsync(Request(feature, "Find Arrival from /home/me/Movies or https://private.example/Movies/", [title],
                selected: [title.Id]));
            Assert.True(outcome.Status == AiOutcomeStatus.Completed, $"{feature}: {outcome.Error?.Message}");
            Assert.Equal("arrival", outcome.Response!.Recommendations[0].TitleId);
        }
        Assert.Equal(6, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("https", request.Url.Scheme);
            Assert.DoesNotContain("private.example", request.Body);
            Assert.DoesNotContain("/home/me", request.Body);
            Assert.DoesNotContain("Arrival.2016.1080p.mkv", request.Body);
            Assert.DoesNotContain("arrival\\\"", request.Body);
            Assert.DoesNotContain("\"arrival\"", request.Body);
            Assert.DoesNotContain("fixture-key", request.Body);
        }
    }
}
