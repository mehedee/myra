using static Myra.Core.Tests.AiFixtures;

namespace Myra.Core.Tests;

/// Shortlist, intent normalisation, privacy and validation (ported from MyraAITests.swift).
public class AiContextTests
{
    [Fact]
    public void SparseSmartPickIgnoresAbsentAndInventedConstraints()
    {
        var sparse = Title(metadata: false, duration: 0);
        var defaults = new AiSearchIntent
        {
            Query = "something uplifting", Genre = "", Language = "en", Kind = "any",
            MinimumYear = 2020, MaximumYear = 0, MinimumRating = 8, MaximumMinutes = 120,
        };
        var context = Prepare([sparse], AiFeature.SmartPick, "Pick something uplifting", defaults);
        Assert.Equal([sparse.Id], context.Aliases.Values.Select(t => t.Id));
        Assert.Null(context.MaximumMinutes);
    }

    [Fact]
    public void ExplicitConstraintsRemainStrictAndKindsAcceptAliases()
    {
        var intent = new AiSearchIntent { Genre = "Drama", Language = "en", Kind = "movies", MinimumYear = 2015 };
        var matched = Prepare([Title()], AiFeature.SmartPick, "English drama movies after 2015", intent);
        Assert.Single(matched.Aliases);
        var missing = Prepare([Title(metadata: false)], AiFeature.SmartPick, "English drama movies after 2015", intent);
        Assert.Empty(missing.Aliases);
        var later = Prepare([Title()], AiFeature.SmartPick, "drama movies after 2020", intent with { MinimumYear = 2020 });
        Assert.Empty(later.Aliases);
    }

    [Fact]
    public void SpecificNaturalSearchRemainsStrict()
    {
        var context = Prepare([Title()], AiFeature.NaturalSearch, "Find Interstellar", new AiSearchIntent { Query = "Interstellar" });
        Assert.Empty(context.Aliases);
        var found = Prepare([Title()], AiFeature.NaturalSearch, "Find Arrival", new AiSearchIntent { Query = "Arrival" });
        Assert.Single(found.Aliases);
    }

    [Fact]
    public void MoodRequestsRankInsteadOfFiltering()
    {
        var calm = Title(id: "calm") with { Metadata = Title().Metadata! with { Overview = "A calm and gentle story" } };
        var loud = Title(id: "loud") with { Metadata = Title().Metadata! with { Overview = "Explosions" } };
        var context = Prepare([loud, calm], AiFeature.SmartPick, "something calm",
            new AiSearchIntent { Query = "", Genre = "Romance" });
        Assert.Equal(["calm", "loud"], context.Aliases.OrderBy(p => p.Key).Select(p => p.Value.Id));
    }

    [Fact]
    public void UnknownRuntimeIsAllowedForPickButExcludedForTimedPlan()
    {
        var sparse = Title(metadata: false, duration: 0);
        var pick = Prepare([sparse], AiFeature.SmartPick, "Pick a movie", new AiSearchIntent { Kind = "movie" });
        Assert.Single(pick.Aliases);
        var plan = Prepare([sparse], AiFeature.Plan, "120 minutes", new AiSearchIntent { MaximumMinutes = 120 });
        Assert.Empty(plan.Aliases);
        Assert.Equal(120, plan.MaximumMinutes);
        Assert.Contains("known runtime", plan.EmptyResultMessage(AiFeature.Plan));
        var noBudget = Prepare([Title()], AiFeature.Plan, "Plan tonight");
        Assert.Contains("Enter a time budget", noBudget.EmptyResultMessage(AiFeature.Plan));
    }

    [Fact]
    public void PlanBudgetComesFromRequestTextAndExcludesLongerTitles()
    {
        var plan = Prepare([Title()], AiFeature.Plan, "Plan tonight\nAvailable time: 20 minutes.");
        Assert.Equal(20, plan.MaximumMinutes);
        Assert.Empty(plan.Aliases);
        var fits = Prepare([Title()], AiFeature.Plan, "about 45 mins");
        Assert.Single(fits.Aliases);
        Assert.Contains("Budget: 45 minutes", fits.Prompt);
    }

    [Fact]
    public void FalseHistoryDefaultsDoNotExcludeTitles()
    {
        var context = Prepare([Title()], AiFeature.SmartPick, "",
            new AiSearchIntent { Unwatched = false, Unfinished = false, Watchlisted = false });
        Assert.Single(context.Aliases);
    }

    [Fact]
    public void UnrequestedHistoryFiltersAreDropped()
    {
        var personal = new EntertainmentPersonalData();
        var context = Prepare([Title()], AiFeature.NaturalSearch, "Korean thrillers",
            new AiSearchIntent { Watchlisted = true, Unfinished = true }, personal: personal);
        Assert.Single(context.Aliases);
    }

    [Fact]
    public void ContextSanitizesPrivateLinksAndOmitsCloudHistory()
    {
        var personal = new EntertainmentPersonalData();
        personal.Watchlist.Add("arrival");
        var context = Prepare([Title()], AiFeature.Summary,
            @"https://10.0.0.1/secret /Users/mehedee/private /home/me/Movies C:\Users\me\Videos \\nas\share\x 192.168.1.4",
            selected: ["arrival"], personal: personal, cloud: true);
        Assert.DoesNotContain("10.0.0.1", context.Prompt);
        Assert.DoesNotContain("private.example", context.Prompt);
        Assert.DoesNotContain("/Users/", context.Prompt);
        Assert.DoesNotContain("/home/me", context.Prompt);
        Assert.DoesNotContain(@"C:\Users", context.Prompt);
        Assert.DoesNotContain(@"\\nas", context.Prompt);
        Assert.DoesNotContain("192.168.1.4", context.Prompt);
        Assert.DoesNotContain("watchlisted", context.Prompt);
        Assert.DoesNotContain("Arrival.2016.1080p.mkv", context.Prompt);
        Assert.Contains("\"id\":\"t1\"", context.Prompt);
        Assert.DoesNotContain("arrival\"", context.Prompt);
    }

    [Fact]
    public void CleanupSendsOnlyFileNamesNeverUrls()
    {
        var context = Prepare([Title()], AiFeature.Cleanup, "clean up", selected: ["arrival"]);
        Assert.Contains("Arrival.2016.1080p.mkv", context.Prompt);
        Assert.DoesNotContain("private.example", context.Prompt);
        Assert.DoesNotContain("Movies/", context.Prompt);
    }

    [Fact]
    public void UnknownIdsAndOverBudgetPlanAreRejected()
    {
        var unknown = new AiWireResponse
        {
            Summary = "Test", Recommendations = [new AiWireRecommendation { TitleId = "unknown", Reason = "Test" }],
        };
        var aliases = new Dictionary<string, EntertainmentTitle> { ["t1"] = Title() };
        Assert.Throws<AiException>(() => AiService.Validate(unknown, aliases, AiFeature.Summary, null, "fixture"));
        var realId = unknown with { Recommendations = [new AiWireRecommendation { TitleId = "arrival", Reason = "Test" }] };
        Assert.Throws<AiException>(() => AiService.Validate(realId, aliases, AiFeature.Summary, null, "fixture"));

        var plan = new AiWireResponse { Summary = "Test", Recommendations = [new AiWireRecommendation { TitleId = "t1", Reason = "Test" }] };
        Assert.Throws<AiException>(() => AiService.Validate(plan, aliases, AiFeature.Plan, 10, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(plan, aliases, AiFeature.Plan, null, "fixture"));
        var series = Title(EntertainmentKind.Series);
        var valid = AiService.Validate(plan, new Dictionary<string, EntertainmentTitle> { ["t1"] = series }, AiFeature.Plan, 30, "fixture");
        Assert.Equal(series.Versions[0].Id, valid.Recommendations[0].VersionId);
        Assert.Equal("arrival", valid.Recommendations[0].TitleId);
        Assert.Equal(AiService.SourceNote, valid.SourceNote);
    }

    [Fact]
    public void PlanRejectsUnknownRuntimeEvenWithinBudget()
    {
        var plan = new AiWireResponse { Summary = "Test", Recommendations = [new AiWireRecommendation { TitleId = "t1", Reason = "Test" }] };
        var sparse = Title(metadata: false, duration: 0);
        Assert.Throws<AiException>(() =>
            AiService.Validate(plan, new Dictionary<string, EntertainmentTitle> { ["t1"] = sparse }, AiFeature.Plan, 500, "fixture"));
    }

    [Fact]
    public void PlanNeverBorrowsAnotherEpisodesDuration()
    {
        var series = Title(EntertainmentKind.Series, metadata: false);
        series = series with { Versions = [series.Versions[0] with { Duration = 0 }, Version("Other.S01E02.mkv", 1800, 1, 2)] };
        Assert.Null(AiContext.Runtime(series));
        Assert.Equal(series.Versions[0].Id, AiContext.PlannedVersion(series)?.Id);
    }

    [Fact]
    public void PlannedVersionPrefersTheResumeEpisode()
    {
        var first = Version("Show.S01E01.mkv", 1500, 1, 1);
        var second = Version("Show.S01E02.mkv", 2400, 1, 2) with { ProgressSeconds = 300, LastPlayed = DateTimeOffset.Now };
        var series = new EntertainmentTitle("series|show|", "Show", null, EntertainmentKind.Series, [first, second]);
        Assert.Equal(second.Id, AiContext.PlannedVersion(series)?.Id);
        Assert.Equal(40, AiContext.Runtime(series));
    }

    [Fact]
    public void ActionsAreValidatedAgainstFeatureKindAndShortlist()
    {
        var aliases = new Dictionary<string, EntertainmentTitle> { ["t1"] = Title() };
        AiWireResponse With(AiWireAction action) => new() { Summary = "s", Actions = [action] };
        var watchlist = new AiWireAction { Kind = "addWatchlist", TitleIds = ["t1", "t1"], Value = "" };
        var result = AiService.Validate(With(watchlist), aliases, AiFeature.Action, null, "fixture");
        Assert.Equal(["arrival"], result.Actions.Single().TitleIds);
        Assert.Throws<AiException>(() => AiService.Validate(With(watchlist), aliases, AiFeature.Summary, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(watchlist with { Kind = "deleteFiles" }), aliases, AiFeature.Action, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(watchlist with { TitleIds = ["t9"] }), aliases, AiFeature.Action, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(watchlist with { TitleIds = [] }), aliases, AiFeature.Action, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(watchlist with { Kind = "collection", Value = "" }), aliases, AiFeature.Collection, null, "fixture"));

        var correction = new AiWireAction { Kind = "correction", TitleIds = ["t1"], Value = "The Arrival", Year = "2016", MediaKind = "movie" };
        Assert.Equal(EntertainmentKind.Movie, AiService.Validate(With(correction), aliases, AiFeature.Cleanup, null, "fixture").Actions[0].MediaKind);
        Assert.Throws<AiException>(() => AiService.Validate(With(correction), aliases, AiFeature.Action, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(correction with { Year = "16" }), aliases, AiFeature.Cleanup, null, "fixture"));
        Assert.Throws<AiException>(() => AiService.Validate(With(correction with { MediaKind = null }), aliases, AiFeature.Cleanup, null, "fixture"));

        var preference = new AiWireAction { Kind = "preference", TitleIds = [], Value = "More mysteries" };
        Assert.Single(AiService.Validate(With(preference), aliases, AiFeature.Preferences, null, "fixture").Actions);
    }

    [Fact]
    public void ResponseTextIsSanitizedAndBounded()
    {
        var aliases = new Dictionary<string, EntertainmentTitle> { ["t1"] = Title() };
        var wire = new AiWireResponse
        {
            Summary = "See https://evil.example/x and /home/me/file",
            Recommendations = [new AiWireRecommendation { TitleId = "t1", Reason = "file:///etc/passwd" }],
            Tags = ["calm"],
        };
        var result = AiService.Validate(wire, aliases, AiFeature.SmartPick, null, "fixture");
        Assert.DoesNotContain("evil.example", result.Summary);
        Assert.DoesNotContain("/home/me", result.Summary);
        Assert.Equal("[private link]", result.Recommendations[0].Reason);
        var tooMany = wire with { Tags = Enumerable.Repeat("x", 13).ToList() };
        Assert.Throws<AiException>(() => AiService.Validate(tooMany, aliases, AiFeature.SmartPick, null, "fixture"));
    }

    [Fact]
    public void DecodeAcceptsFencedJsonAndRejectsIncompleteAnswers()
    {
        var fenced = "```json\n" + GroundedAnswer + "\n```";
        Assert.Equal("Saved catalogue facts", AiPrivacy.Decode<AiWireResponse>(fenced).Summary);
        Assert.Throws<AiException>(() => AiPrivacy.Decode<AiWireResponse>("{\"summary\":\"x\"}"));
        Assert.Throws<AiException>(() => AiPrivacy.Decode<AiWireResponse>("{\"summary\":null,\"recommendations\":[],\"actions\":[],\"tags\":[]}"));
        Assert.Throws<AiException>(() => AiPrivacy.Decode<AiWireResponse>("Sure! Here you go."));
        var intent = AiPrivacy.Decode<AiSearchIntent>("{\"kind\":\"\",\"maximumYear\":0,\"minimumYear\":\"2015\"}");
        Assert.Equal(2015, intent.MinimumYear);
    }

    [Fact]
    public void IntentNormalizationKeepsOnlyRequestBackedValues()
    {
        var intent = new AiSearchIntent
        {
            Query = " none ", Genre = "Thriller", Language = "ko", Kind = "tv show", MinimumYear = 2018, MinimumRating = 7.5,
            Unwatched = true, MaximumMinutes = -5,
        }.Normalized("unwatched korean thriller series after 2018 rated 7.5");
        Assert.Null(intent.Query);
        Assert.Equal("Thriller", intent.Genre);
        Assert.Equal("ko", intent.Language);
        Assert.Equal("series", intent.Kind);
        Assert.Equal(2018, intent.MinimumYear);
        Assert.Equal(7.5, intent.MinimumRating);
        Assert.True(intent.Unwatched);
        Assert.Null(intent.MaximumMinutes);

        var invented = new AiSearchIntent { Genre = "Horror", Language = "fr", Kind = "movie", MinimumYear = 1999, MinimumRating = 7 }
            .Normalized("something cosy for tonight");
        Assert.Equal((null, null, null, null, null), (invented.Genre, invented.Language, invented.Kind, invented.MinimumYear, invented.MinimumRating));
    }

    [Fact]
    public void HistoryIntentAndRecapContextAreRestricted()
    {
        var context = Prepare([Title()], AiFeature.Assistant, "watchlist", new AiSearchIntent { Watchlisted = true });
        Assert.Empty(context.Aliases);
        var recap = Prepare([Title(EntertainmentKind.Series)], AiFeature.Recap, "recap", selected: ["arrival"],
            subtitles: "Only this completed episode");
        Assert.DoesNotContain("A thoughtful story", recap.Prompt);
        Assert.Contains("Only this completed episode", recap.Prompt);
        var bounded = Prepare([Title(EntertainmentKind.Series)], AiFeature.Recap, "recap", selected: ["arrival"],
            subtitles: new string('a', 10_000));
        Assert.True(bounded.Prompt.Length < 5000);
    }

    [Fact]
    public void ShrinkingShortlistPreservesFullStatisticsAndHistoryConsent()
    {
        var values = Enumerable.Range(0, 30).Select(offset => Title(id: $"t{offset}") with
        {
            Name = $"Long {offset}",
            Metadata = Title().Metadata! with { Title = "", Overview = string.Concat(Enumerable.Repeat("long description ", 100)) },
        }).ToList();
        var context = Prepare(values, AiFeature.Insights, "statistics", byteLimit: 4000);
        Assert.Contains("titles=30", context.Prompt);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context.Prompt) <= 4000);
        Assert.InRange(context.Aliases.Count, 1, 11);

        var error = Assert.Throws<AiException>(() => Prepare(values, AiFeature.Assistant, "unfinished",
            new AiSearchIntent { Unfinished = true }, cloud: true));
        Assert.Equal((AiErrorKind.Consent, AiPermission.History), (error.Kind, error.Permission));
        Assert.Equal(AiPermission.History, Assert.Throws<AiException>(() => Prepare(values, AiFeature.Insights, "stats", cloud: true)).Permission);
        var shared = Prepare(values, AiFeature.Insights, "stats", cloud: true, settings: new AiSettings { ShareHistory = true });
        Assert.Contains("watchlisted", shared.Prompt);
    }

    [Fact]
    public void SimilarExcludesTheReferenceAndRanksSharedGenres()
    {
        var reference = Title(id: "reference");
        var drama = Title(id: "drama");
        var comedy = Title(id: "comedy") with { Metadata = Title().Metadata! with { Genres = ["Comedy"], Rating = 9 } };
        var context = Prepare([reference, comedy, drama], AiFeature.Similar, "", selected: ["reference"]);
        Assert.DoesNotContain(context.Aliases.Values, t => t.Id == "reference");
        Assert.Equal("drama", context.Aliases["t1"].Id);
        Assert.Contains("Reference description: A thoughtful story", context.Prompt);
    }

    [Fact]
    public void FeedbackIsIncludedOnlyWhenPersonalized()
    {
        var settings = new AiSettings { Feedback = "less horror" };
        Assert.Contains("less horror", Prepare([Title()], AiFeature.SmartPick, "", settings: settings).Prompt);
        Assert.DoesNotContain("less horror", Prepare([Title()], AiFeature.SmartPick, "", settings: settings with { Personalize = false }).Prompt);
    }
}
