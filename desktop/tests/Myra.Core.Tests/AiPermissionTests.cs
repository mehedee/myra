using System.Text.Json;
using static Myra.Core.Tests.AiFixtures;

namespace Myra.Core.Tests;

/// Cloud permissions, plan budgets, the daily limit for two-step features and recap episode rules.
/// HTTP goes through FakeHttpHandler, never the network.
public class AiPermissionTests
{
    private static AiRequest Request(AiFeature feature, string prompt, IReadOnlyList<EntertainmentTitle> titles,
        EntertainmentPersonalData? personal = null, IReadOnlyList<string>? selected = null,
        string? subtitles = null, string? recapVersion = null) => new()
    {
        Feature = feature, Prompt = prompt, Titles = titles, Personal = personal ?? new EntertainmentPersonalData(),
        Revision = 1, SelectedIds = selected ?? [], SubtitleText = subtitles, RecapVersionId = recapVersion,
    };

    /// The real OpenAI client over a fake HTTP handler, with cloud consent, a key and a model.
    private static (FakeHttpHandler Handler, AiServiceFixture Fixture) CloudFixture(string intent, string answer = GroundedAnswer)
    {
        var handler = new FakeHttpHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            var text = body.Contains("Extract catalogue") ? intent : answer;
            return FakeHttpHandler.Text(200, JsonSerializer.Serialize(new
            {
                output = new[] { new { content = new[] { new { type = "output_text", text } } } },
            }));
        });
        var fixture = new AiServiceFixture(new OpenAiProvider(handler.Client()));
        fixture.Service.Settings = fixture.Service.Settings with
        {
            Provider = AiProviderIds.OpenAI, CloudConsent = true, Model = "fixture-model", DailyRequestLimit = 20,
        };
        fixture.Service.SaveKey("fixture-key");
        return (handler, fixture);
    }

    /// Review finding: the history check ran only after the paid intent generation.
    [Fact]
    public async Task HistoryPermissionIsCheckedBeforeAnyHttpRequestOrQuota()
    {
        var (handler, fixture) = CloudFixture("{\"genre\":\"Comedy\",\"watchlisted\":true}");
        using var owned = fixture;
        var ai = fixture.Service;
        var request = Request(AiFeature.NaturalSearch, "comedies I saved to watch", [Title()]);
        Assert.Equal([AiPermission.History], ai.MissingPermissions(request));

        // History is off, or the user declined the prompt: nothing is sent and no generation is counted.
        var refused = await ai.ExecuteAsync(request);
        Assert.Equal(AiOutcomeStatus.Failed, refused.Status);
        Assert.Equal(AiErrorKind.Consent, refused.Error?.Kind);
        Assert.Equal(AiPermission.History, refused.Error?.Permission);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, ai.RequestsToday);
        Assert.Equal(0, ai.EstimatedInputTokens);

        // Accepted: exactly the two generations of a two-step feature.
        ai.Settings = ai.Settings with { ShareHistory = true };
        Assert.Empty(ai.MissingPermissions(request));
        var comedy = Title() with { Metadata = Title().Metadata! with { Genres = ["Comedy"] } };
        var personal = new EntertainmentPersonalData();
        personal.Watchlist.Add(comedy.Id);
        var accepted = await ai.ExecuteAsync(request with { Titles = [comedy], Personal = personal });
        Assert.True(accepted.Status == AiOutcomeStatus.Completed, accepted.Error?.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, ai.RequestsToday);
    }

    [Theory]
    [InlineData(AiFeature.NaturalSearch, "comedies I saved to watch", true)]
    [InlineData(AiFeature.SmartPick, "something I haven’t seen", true)]
    [InlineData(AiFeature.SmartPick, "something I haven't seen", true)]
    [InlineData(AiFeature.Assistant, "resume my unfinished shows", true)]
    [InlineData(AiFeature.Plan, "my WATCH LIST, 90 minutes", true)]
    [InlineData(AiFeature.Insights, "", true)]
    [InlineData(AiFeature.NaturalSearch, "korean thrillers after 2018", false)]
    [InlineData(AiFeature.Summary, "is it on my watchlist", false)]
    public void HistoryWordsComeFromOneList(AiFeature feature, string prompt, bool needsHistory)
    {
        Assert.Equal(needsHistory, AiConsent.MayUseHistory(feature, prompt));
        // A history filter from the model survives exactly when the consent check fires.
        if (feature.ExtractsIntent())
        {
            var intent = new AiSearchIntent { Unwatched = true, Unfinished = true, Watchlisted = true }.Normalized(prompt);
            Assert.Equal(needsHistory, intent.Unwatched == true || intent.Unfinished == true || intent.Watchlisted == true);
        }
    }

    [Fact]
    public void MissingPermissionsListsEachCloudPermissionInPromptOrder()
    {
        var (_, fixture) = CloudFixture("{}");
        using var owned = fixture;
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { CloudConsent = false };
        var series = Title(EntertainmentKind.Series);
        Assert.Equal([AiPermission.Cloud, AiPermission.Subtitles],
            ai.MissingPermissions(Request(AiFeature.Recap, "recap", [series], subtitles: "text")).ToList());
        Assert.Equal([AiPermission.Cloud, AiPermission.History], ai.MissingPermissions(Request(AiFeature.Insights, "", [series])).ToList());
        ai.Settings = ai.Settings with { CloudConsent = true, ShareHistory = true, ShareSubtitles = true };
        Assert.Empty(ai.MissingPermissions(Request(AiFeature.Insights, "unwatched", [series], subtitles: "text")));

        using var local = new AiServiceFixture(new FakeAiProvider());
        local.Service.Settings = local.Service.Settings with { Provider = "fixture" };
        Assert.Empty(local.Service.MissingPermissions(Request(AiFeature.Insights, "unwatched", [series], subtitles: "text")));
    }

    /// Review finding: a budget from the model could replace the user's "Available time".
    [Fact]
    public async Task PlanBudgetFromTheModelCannotExceedTheUsersBudget()
    {
        var provider = new FakeAiProvider(intentJson: "{\"maximumMinutes\":300}");
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture" };
        var epic = Title(duration: 280 * 60);
        var outcome = await ai.ExecuteAsync(Request(AiFeature.Plan, AiWorkspace.ComposePrompt(AiFeature.Plan, "an epic", 120), [epic]));
        Assert.Equal(AiOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("120-minute budget", outcome.Error?.Message);
        Assert.Null(ai.Response);

        var wire = new AiWireResponse { Summary = "Plan", Recommendations = [new AiWireRecommendation { TitleId = "t1", Reason = "Long" }] };
        var aliases = new Dictionary<string, EntertainmentTitle> { ["t1"] = epic };
        Assert.Throws<AiException>(() => AiService.Validate(wire, aliases, AiFeature.Plan, 120, "Fixture"));

        Assert.Null(new AiSearchIntent { MaximumMinutes = 300 }.Normalized("Available time: 120 minutes.").MaximumMinutes);
        Assert.Equal(90, new AiSearchIntent { MaximumMinutes = 90 }.Normalized("Available time: 120 minutes.").MaximumMinutes);
        Assert.Null(new AiSearchIntent { MaximumMinutes = 90 }.Normalized("plan tonight").MaximumMinutes);
        Assert.Equal(120, Prepare([Title()], AiFeature.Plan, "Available time: 120 minutes.", new AiSearchIntent { MaximumMinutes = 300 }).MaximumMinutes);
        Assert.Equal(90, Prepare([Title()], AiFeature.Plan, "Available time: 120 minutes.", new AiSearchIntent { MaximumMinutes = 90 }).MaximumMinutes);
    }

    /// Review finding: a two-step request could spend the last generation on its first half.
    [Fact]
    public async Task TwoStepFeaturesNeedTwoRemainingGenerations()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture", DailyRequestLimit = 1 };
        var pick = await ai.ExecuteAsync(Request(AiFeature.SmartPick, "pick", [Title()]));
        Assert.Equal(AiErrorKind.Limit, pick.Error?.Kind);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, ai.RequestsToday);
        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(Request(AiFeature.Summary, "summary", [Title()], selected: ["arrival"]))).Status);
        Assert.Equal(1, ai.RequestsToday);

        ai.Settings = ai.Settings with { DailyRequestLimit = 3 };
        Assert.Equal(AiOutcomeStatus.Completed, (await ai.ExecuteAsync(Request(AiFeature.SmartPick, "pick", [Title()]))).Status);
        Assert.Equal(3, ai.RequestsToday);
    }

    /// Review finding: the episode picker listed season-less episodes that the service rejected.
    [Fact]
    public async Task RecapEpisodePickerAndServiceAgree()
    {
        var provider = new FakeAiProvider();
        using var fixture = new AiServiceFixture(provider);
        var ai = fixture.Service;
        ai.Settings = ai.Settings with { Provider = "fixture", DailyRequestLimit = 50 };
        var seasonless = Version("Show.E05.mkv", 1800, null, 5);
        var finished = Version("Show.S01E02.mkv", 1800, 1, 2) with { ProgressSeconds = 1795 };
        var started = Version("Show.S01E03.mkv", 1800, 1, 3) with { ProgressSeconds = 60 };
        var series = new EntertainmentTitle("show", "Show", null, EntertainmentKind.Series, [seasonless, finished, started]);
        Assert.Equal([finished.Id], AiWorkspace.CompletedEpisodes(series, new EntertainmentPersonalData()).Select(v => v.Id));

        var personal = new EntertainmentPersonalData();
        personal.Watched.Add(series.Id);
        var listed = AiWorkspace.CompletedEpisodes(series, personal).Select(v => v.Id).ToHashSet();
        Assert.True(listed.SetEquals([finished.Id, started.Id]));
        foreach (var version in series.Versions)
        {
            var outcome = await ai.ExecuteAsync(Request(AiFeature.Recap, "recap " + version.Id, [series], personal, selected: [series.Id],
                subtitles: "text", recapVersion: version.Id));
            Assert.Equal(listed.Contains(version.Id), outcome.Status == AiOutcomeStatus.Completed);
        }
    }
}
