namespace Myra.Core.Tests;

/// Shared AI fixtures. No test contacts a real provider.
internal static class AiFixtures
{
    public static readonly Uri PrivateRoot = new("https://private.example/Movies/");

    public const string GroundedAnswer =
        "{\"summary\":\"Saved catalogue facts\",\"recommendations\":[{\"titleID\":\"t1\",\"reason\":\"Indexed title\"}],\"actions\":[],\"tags\":[]}";

    public static EntertainmentVersion Version(string name, double duration = 1800, int? season = null, int? episode = null) =>
        new(new GlobalSearchResult(Guid.NewGuid(), "Private", PrivateRoot,
                new DirectoryEntry(name, new Uri(PrivateRoot, name), EntryKind.File), name, null),
            DateTimeOffset.FromUnixTimeSeconds(100))
        {
            Duration = duration,
            Season = season,
            Episode = episode,
        };

    /// Port of MyraAITests.title(kind:).
    public static EntertainmentTitle Title(EntertainmentKind kind = EntertainmentKind.Movie, string id = "arrival", bool metadata = true, double duration = 1800)
    {
        var series = kind == EntertainmentKind.Series;
        return new EntertainmentTitle(id, "Arrival", "2016", kind,
            [Version("Arrival.2016.1080p.mkv", duration, series ? 1 : null, series ? 1 : null)])
        {
            Metadata = metadata
                ? new EntertainmentMetadata
                {
                    ProviderId = 1, Title = "Arrival", Overview = "A thoughtful story", Rating = 8, VoteCount = 100,
                    ReleaseDate = "2016-01-01", Genres = ["Drama"], Language = "en",
                }
                : null,
        };
    }

    public static AiContext Prepare(
        IReadOnlyList<EntertainmentTitle> titles, AiFeature feature, string prompt, AiSearchIntent? intent = null,
        IReadOnlyCollection<string>? selected = null, string? subtitles = null, EntertainmentPersonalData? personal = null,
        AiSettings? settings = null, bool cloud = false, int byteLimit = AiContext.CloudByteLimit) =>
        AiContext.Prepare(new AiContextInput
        {
            Titles = titles,
            Feature = feature,
            Prompt = prompt,
            Intent = intent,
            SelectedIds = selected ?? [],
            SubtitleText = subtitles,
            Personal = personal ?? new EntertainmentPersonalData(),
            Settings = settings ?? new AiSettings(),
            IsCloud = cloud,
            ByteLimit = byteLimit,
        });
}

/// Scripted provider (macOS MyraFixtureTransport). Records every generation request.
internal sealed class FakeAiProvider(
    string id = "fixture", bool cloud = false, string intentJson = "{}", string answer = AiFixtures.GroundedAnswer,
    TimeSpan? delay = null, int byteLimit = AiContext.CloudByteLimit) : IAiProvider
{
    private readonly List<AiGenerationRequest> _requests = [];

    public string Id => id;
    public string Title => "Fixture";
    public bool IsCloud => cloud;
    public string? SecretName => cloud ? SecretNames.OpenAiApiKey : null;
    public int ContextByteLimit => byteLimit;
    public int MaximumOutputTokens => cloud ? 2000 : 800;
    public string? Unavailable { get; set; }
    public string Answer { get; set; } = answer;

    public IReadOnlyList<AiGenerationRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public int Calls => Requests.Count;

    public string? Availability() => Unavailable;

    public async Task<string> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken)
    {
        lock (_requests) _requests.Add(request);
        if (delay is { } wait) await Task.Delay(wait, cancellationToken);
        return request.Instructions.Contains("Extract catalogue") ? intentJson : Answer;
    }

    public Task<IReadOnlyList<string>> ModelsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["fixture-model"]);
}

/// AI service on a temporary folder with in-memory secrets.
internal sealed class AiServiceFixture : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public AiServiceFixture(params IAiProvider[] providers) : this(null, providers)
    {
    }

    public AiServiceFixture(Func<DateTimeOffset>? now, params IAiProvider[] providers)
    {
        Secrets = new InMemorySecretStore();
        Service = new AiService(Secrets, providers, SettingsPath, UsagePath, now);
    }

    public InMemorySecretStore Secrets { get; }
    public AiService Service { get; }
    public string SettingsPath => Path.Combine(_temp.Path, "MyraAI.json");
    public string UsagePath => Path.Combine(_temp.Path, "MyraAIUsage.json");

    public void Dispose()
    {
        Service.Dispose();
        _temp.Dispose();
    }
}
