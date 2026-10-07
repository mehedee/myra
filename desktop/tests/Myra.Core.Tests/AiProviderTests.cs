using System.Net;
using System.Text.Json.Nodes;

namespace Myra.Core.Tests;

/// Cloud HTTP contracts (ported from MyraAIProviderTests.swift). Fake handlers only; no paid calls.
public class AiProviderTests
{
    private const string Fixture =
        "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"fixture\"}]}],\"content\":[{\"type\":\"text\",\"text\":\"fixture\"}]}";

    private static AiGenerationRequest Generation(string model = "fixture-model", string key = "fixture-key") =>
        new(model, key, "instructions", "catalogue", 800, "en");

    [Fact]
    public async Task CloudRequestContractsUseFixedEndpointsWithoutStorage()
    {
        var handler = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, Fixture)));
        var openAI = await new OpenAiProvider(handler.Client()).GenerateAsync(Generation(), CancellationToken.None);
        var claude = await new ClaudeProvider(handler.Client()).GenerateAsync(Generation(), CancellationToken.None);
        Assert.Equal("fixture", openAI);
        Assert.Equal("fixture", claude);

        var requests = handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://api.openai.com/v1/responses", requests[0].Url.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal("Bearer fixture-key", requests[0].Header("Authorization"));
        Assert.Null(requests[0].Header("x-api-key"));
        var openBody = JsonNode.Parse(requests[0].Body!)!.AsObject();
        Assert.False(openBody["store"]!.GetValue<bool>());
        Assert.Equal(800, openBody["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("fixture-model", openBody["model"]!.GetValue<string>());
        Assert.Equal("instructions", openBody["instructions"]!.GetValue<string>());
        Assert.Equal("catalogue", openBody["input"]!.GetValue<string>());

        Assert.Equal("https://api.anthropic.com/v1/messages", requests[1].Url.AbsoluteUri);
        Assert.Equal("fixture-key", requests[1].Header("x-api-key"));
        Assert.Equal("2023-06-01", requests[1].Header("anthropic-version"));
        Assert.Null(requests[1].Header("Authorization"));
        var claudeBody = JsonNode.Parse(requests[1].Body!)!.AsObject();
        Assert.Equal("instructions", claudeBody["system"]!.GetValue<string>());
        Assert.Equal(800, claudeBody["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", claudeBody["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("catalogue", claudeBody["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyTextPartsAreJoined()
    {
        var handler = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200,
            "{\"output\":[{\"type\":\"reasoning\",\"content\":[]},{\"content\":[{\"type\":\"output_text\",\"text\":\"a\"},{\"type\":\"refusal\",\"text\":\"no\"},{\"type\":\"output_text\",\"text\":\"b\"}]}]}")));
        Assert.Equal("a\nb", await new OpenAiProvider(handler.Client()).GenerateAsync(Generation(), CancellationToken.None));
        var empty = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, "{\"content\":[{\"type\":\"tool_use\"}]}")));
        var error = await Assert.ThrowsAsync<AiException>(() => new ClaudeProvider(empty.Client()).GenerateAsync(Generation(), CancellationToken.None));
        Assert.Equal(AiErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task RedirectsAreRejectedAndNeverFollowed()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Text(302, "");
            response.Headers.Location = new Uri("https://untrusted.example/");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<AiException>(() => new OpenAiProvider(handler.Client()).GenerateAsync(Generation(), CancellationToken.None));
        Assert.Equal((AiErrorKind.Http, 302), (error.Kind, error.Status));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Host == "untrusted.example");

        // A handler that followed a redirect anyway: the answer did not come from the fixed URL.
        var followed = new FollowingHandler();
        var moved = await Assert.ThrowsAsync<AiException>(() => new ClaudeProvider(new HttpClient(followed)).GenerateAsync(Generation(), CancellationToken.None));
        Assert.Equal(AiErrorKind.InvalidResponse, moved.Kind);
    }

    private sealed class FollowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = FakeHttpHandler.Text(200, Fixture);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://untrusted.example/v1/messages");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ErrorsMapToUserMessagesWithoutSendingInvalidRequests()
    {
        var handler = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(401, "{\"error\":\"bad key\"}")));
        var provider = new OpenAiProvider(handler.Client());
        var http = await Assert.ThrowsAsync<AiException>(() => provider.GenerateAsync(Generation(), CancellationToken.None));
        Assert.Equal("AI provider request failed (HTTP 401). Check your model, account, and API limits.", http.Message);

        var noKey = await Assert.ThrowsAsync<AiException>(() => provider.GenerateAsync(Generation(key: ""), CancellationToken.None));
        Assert.Equal(AiErrorKind.MissingKey, noKey.Kind);
        var injected = await Assert.ThrowsAsync<AiException>(() => provider.GenerateAsync(Generation(key: "a\r\nX-Evil: 1"), CancellationToken.None));
        Assert.Equal(AiErrorKind.MissingKey, injected.Kind);
        var noModel = await Assert.ThrowsAsync<AiException>(() => provider.GenerateAsync(Generation(model: ""), CancellationToken.None));
        Assert.Equal("Select a text model in AI Settings.", noModel.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedOrNonObjectResponsesAreRejected()
    {
        var large = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, "{\"x\":\"" + new string('a', CloudAiProvider.ResponseLimit) + "\"}")));
        Assert.Equal(AiErrorKind.InvalidResponse,
            (await Assert.ThrowsAsync<AiException>(() => new OpenAiProvider(large.Client()).GenerateAsync(Generation(), CancellationToken.None))).Kind);
        var array = new FakeHttpHandler(_ => Task.FromResult(FakeHttpHandler.Text(200, "[1,2]")));
        Assert.Equal(AiErrorKind.InvalidResponse,
            (await Assert.ThrowsAsync<AiException>(() => new OpenAiProvider(array.Client()).GenerateAsync(Generation(), CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task ModelListingUsesTheFixedModelsEndpoint()
    {
        var handler = FakeHttpHandler.ByPath(new() { ["/v1/models"] = (200, "{\"data\":[{\"id\":\"claude-b\"},{\"id\":\"claude-a\"},{\"name\":\"x\"}]}") });
        var models = await new ClaudeProvider(handler.Client()).ModelsAsync("fixture-key", CancellationToken.None);
        Assert.Equal(["claude-a", "claude-b"], models);
        var request = handler.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/models", request.Url.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task CancellationStopsARunningRequest()
    {
        var handler = new FakeHttpHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return FakeHttpHandler.Text(200, Fixture);
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiProvider(handler.Client()).GenerateAsync(Generation(), cancel.Token));
    }

    [Fact]
    public void PrivacyTextRedactsLinksPathsAndAddresses()
    {
        var text = AiPrivacy.Text("smb://nas/share file:///etc ftp://x /Volumes/Disk/a /mnt/media/b D:\\Films\\c \\\\server\\share\\d 10.1.2.3 Arrival", 800);
        Assert.Equal("[private link] [private link] [private link] [private path] [private path] [private path] [private path] [private address] Arrival", text);
        Assert.Equal(5, AiPrivacy.Text("abcdefghij", 5).Length);
        Assert.Equal(1, AiPrivacy.Text("a😀", 2).Length);
    }

    [Fact]
    public void SubtitleFilesAreValidatedAndDecoded()
    {
        using var temp = new TemporaryDirectory();
        var srt = Path.Combine(temp.Path, "episode.srt");
        File.WriteAllBytes(srt, [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("1\nHello")]);
        Assert.Equal("1\nHello", AiWorkspace.ReadSubtitleFile(srt));
        var utf8 = Path.Combine(temp.Path, "episode.vtt");
        File.WriteAllText(utf8, "WEBVTT\nBonjour é");
        Assert.Equal("WEBVTT\nBonjour é", AiWorkspace.ReadSubtitleFile(utf8));
        var exe = Path.Combine(temp.Path, "episode.exe");
        File.WriteAllText(exe, "x");
        Assert.Contains("SRT, ASS, SSA, VTT", Assert.Throws<AiException>(() => AiWorkspace.ReadSubtitleFile(exe)).Message);
        var large = Path.Combine(temp.Path, "large.srt");
        File.WriteAllBytes(large, new byte[AiWorkspace.SubtitleSizeLimit + 1]);
        Assert.Contains("2 MB", Assert.Throws<AiException>(() => AiWorkspace.ReadSubtitleFile(large)).Message);
        var empty = Path.Combine(temp.Path, "empty.txt");
        File.WriteAllText(empty, "");
        Assert.Contains("UTF-8 or UTF-16", Assert.Throws<AiException>(() => AiWorkspace.ReadSubtitleFile(empty)).Message);
    }

    [Fact]
    public void WorkspaceHelpersComposeRequestsAndKeepPlannedEpisodes()
    {
        var episode = AiFixtures.Version("Show.S01E03.mkv", 1500, 1, 3);
        Assert.Equal("Plan\nAvailable time: 90 minutes.", AiWorkspace.ComposePrompt(AiFeature.Plan, "Plan", 90));
        Assert.Equal("Recap\nRecap only Season 1, Episode 3. Source: e.srt.", AiWorkspace.ComposePrompt(AiFeature.Recap, "Recap", recapEpisode: episode, subtitleName: "e.srt"));
        var series = new EntertainmentTitle("series|show|", "Show", null, EntertainmentKind.Series, [AiFixtures.Version("Show.S01E01.mkv", 1500, 1, 1), episode]);
        Assert.Equal([episode.Id], AiWorkspace.PlaybackTitle(series, episode.Id).Versions.Select(v => v.Id));
        Assert.Same(series, AiWorkspace.PlaybackTitle(series, null));
        Assert.Single(AiWorkspace.MatchingTitles([series, AiFixtures.Title()], "arr"));
    }
}
