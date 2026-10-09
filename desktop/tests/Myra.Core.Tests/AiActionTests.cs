using System.Text.Json;

namespace Myra.Core.Tests;

/// Reviewed personal actions with preview and undo (ported from MyraAIActionTests.swift).
/// The provider fails every call: applying or undoing an action must never request a model.
public sealed class AiActionTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly AiServiceFixture _ai = new(new FakeAiProvider { Unavailable = "Action tests must not request a model" });

    public AiActionTests()
    {
        var root = new Uri("https://media.example/Movies/");
        var category = Guid.NewGuid();
        var results = new[] { "Arrival.2016.1080p.mkv", "Arrival.2016.2160p.mkv", "Dune.2021.1080p.mkv" }
            .Select(name => new GlobalSearchResult(category, "Fixture", root, new DirectoryEntry(name, new Uri(root, name), EntryKind.File), name, null))
            .ToList();
        _store.Index.Upsert(results, Guid.NewGuid());
        Store.ReloadAsync().GetAwaiter().GetResult();
        Assert.Equal(2, Store.Catalogue.Count);
    }

    private EntertainmentStore Store => _store.Store;
    private AiService Ai => _ai.Service;
    private EntertainmentTitle First => Store.Catalogue[0];
    private EntertainmentTitle Second => Store.Catalogue[1];

    private EntertainmentPersonalData Saved() =>
        JsonSerializer.Deserialize<EntertainmentPersonalData>(File.ReadAllText(_store.PersonalPath), SwiftJson.Options)!;

    public void Dispose()
    {
        _ai.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task ReviewedWatchlistAndCollectionActionsPersistAndUndoIndependently()
    {
        var first = First.Id;
        var second = Second.Id;
        Store.ToggleWatchlist(second);

        await Ai.ApplyAsync(new AiAction(AiActionKind.AddWatchlist, [first], ""), Store);
        Assert.Equal([first, second], Store.Personal.Watchlist.Order());
        Assert.Equal([first, second], Saved().Watchlist.Order());
        Assert.True(Ai.CanUndo);
        await Ai.UndoAsync(Store);
        Assert.Equal([second], Store.Personal.Watchlist);
        Assert.Equal([second], Saved().Watchlist);
        Assert.False(Ai.CanUndo);

        await Ai.ApplyAsync(new AiAction(AiActionKind.Collection, [first, second], "Thoughtful Weekend"), Store);
        var collection = Assert.Single(Store.Personal.Collections);
        Assert.Equal("Thoughtful Weekend", collection.Name);
        Assert.Equal([first, second], collection.TitleIds.Order());
        Assert.Equal([first, second], Saved().Collections.Single().TitleIds.Order());
        await Ai.UndoAsync(Store);
        Assert.Empty(Store.Personal.Collections);
        Assert.Empty(Saved().Collections);
        Assert.Equal([second], Store.Personal.Watchlist);

        await Ai.ApplyAsync(new AiAction(AiActionKind.MarkWatched, [second], ""), Store);
        Assert.Contains(second, Saved().Watched);
    }

    [Fact]
    public async Task CorrectionUndoRestoresGroupingAndPreservesHistoryAndCollections()
    {
        var original = Store.Catalogue.Single(t => t.Name == "Arrival");
        Assert.Equal(2, original.Versions.Count);
        Store.ToggleWatchlist(original.Id);
        Store.ToggleWatched(original.Id);
        Store.CreateCollection("Weekend");
        var collectionId = Store.Personal.Collections.Single().Id;
        Store.ToggleCollection(original.Id, collectionId);
        var media = original.Versions[0].Media;
        Store.RecordPlayback(media, 300, 7200);
        var initialHistory = Store.Personal.History[media.Entry.Url.AbsoluteUri];

        await Ai.ApplyAsync(new AiAction(AiActionKind.Correction, [original.Id], " The Arrival ", null, EntertainmentKind.Movie), Store);
        var corrected = Store.Catalogue.Single(t => t.Name == "The Arrival");
        Assert.NotEqual(original.Id, corrected.Id);
        Assert.Equal("2016", corrected.Year);
        Assert.Equal(2, corrected.Versions.Count);
        Assert.Contains(corrected.Id, Store.Personal.Watchlist);
        Assert.DoesNotContain(original.Id, Store.Personal.Watchlist);
        Assert.Contains(corrected.Id, Store.Personal.Watched);
        Assert.Equal([corrected.Id], Store.Personal.Collections.Single().TitleIds);
        Assert.Equal(2, Store.Personal.MatchCorrections.Count);
        Assert.Equal(2, Saved().MatchCorrections.Count);

        await Ai.UndoAsync(Store);
        Assert.Contains(Store.Catalogue, t => t.Id == original.Id);
        Assert.DoesNotContain(Store.Catalogue, t => t.Id == corrected.Id);
        Assert.Empty(Store.Personal.MatchCorrections);
        Assert.Equal([original.Id], Store.Personal.Watchlist);
        Assert.Equal([original.Id], Store.Personal.Watched);
        Assert.Equal([original.Id], Store.Personal.Collections.Single().TitleIds);
        Assert.Equal(initialHistory, Store.Personal.History[media.Entry.Url.AbsoluteUri]);
        Assert.Empty(Saved().MatchCorrections);
        Assert.Equal([original.Id], Saved().Watchlist);
        Assert.Equal(original.Versions.Select(v => v.Media.Entry.Name).Order(),
            Store.Catalogue.Single(t => t.Id == original.Id).Versions.Select(v => v.Media.Entry.Name).Order());
    }

    [Fact]
    public async Task UndoRejectsNewerPersonalEditsAndDifferentStore()
    {
        using var other = new StoreFixture();
        var first = First.Id;
        var second = Second.Id;
        await Ai.ApplyAsync(new AiAction(AiActionKind.AddWatchlist, [first], ""), Store);
        await Assert.ThrowsAsync<AiException>(() => Ai.UndoAsync(other.Store));
        Assert.Contains(first, Store.Personal.Watchlist);
        Store.ToggleWatched(second);
        var error = await Assert.ThrowsAsync<AiException>(() => Ai.UndoAsync(Store));
        Assert.Equal("The library changed after this action. Undo is no longer safe.", error.Message);
        Assert.Equal([first], Store.Personal.Watchlist);
        Assert.Equal([second], Store.Personal.Watched);
        Assert.Equal([second], Saved().Watched);
    }

    [Fact]
    public async Task RejectedProposalDoesNotOverwritePreviousUndoSnapshot()
    {
        var first = First.Id;
        await Ai.ApplyAsync(new AiAction(AiActionKind.AddWatchlist, [first], ""), Store);
        await Assert.ThrowsAsync<AiException>(() => Ai.ApplyAsync(new AiAction(AiActionKind.Play, [first], ""), Store));
        await Assert.ThrowsAsync<AiException>(() => Ai.ApplyAsync(new AiAction(AiActionKind.ShowResults, [first], ""), Store));
        await Assert.ThrowsAsync<AiException>(() => Ai.ApplyAsync(new AiAction(AiActionKind.Collection, ["missing-title"], "Invalid"), Store));
        await Assert.ThrowsAsync<AiException>(() => Ai.ApplyAsync(new AiAction(AiActionKind.Collection, [first], ""), Store));
        await Assert.ThrowsAsync<AiException>(() =>
            Ai.ApplyAsync(new AiAction(AiActionKind.Correction, [first], "Arrival", "invalid", EntertainmentKind.Movie), Store));
        await Assert.ThrowsAsync<AiException>(() =>
            Ai.ApplyAsync(new AiAction(AiActionKind.Correction, [first, Second.Id], "Arrival", null, EntertainmentKind.Movie), Store));
        Assert.True(Ai.CanUndo);
        Assert.Equal([first], Store.Personal.Watchlist);
        Assert.Empty(Store.Personal.Collections);
        Assert.Empty(Store.Personal.MatchCorrections);
        await Ai.UndoAsync(Store);
        Assert.Empty(Store.Personal.Watchlist);
        Assert.Empty(Saved().Watchlist);
    }

    [Fact]
    public async Task PreferenceActionSavesFeedbackAndUndoRestoresIt()
    {
        Ai.SaveFeedback("less horror");
        await Ai.ApplyAsync(new AiAction(AiActionKind.Preference, [], "more mysteries"), Store);
        Assert.Equal("more mysteries", Ai.Settings.Feedback);
        await Ai.UndoAsync(Store);
        Assert.Equal("less horror", Ai.Settings.Feedback);
        Assert.False(Ai.CanUndo);

        await Ai.ApplyAsync(new AiAction(AiActionKind.Preference, [], "shorter films"), Store);
        Ai.SaveFeedback("changed by hand");
        await Assert.ThrowsAsync<AiException>(() => Ai.UndoAsync(Store));
        Assert.Equal("changed by hand", Ai.Settings.Feedback);
    }

    [Fact]
    public async Task ReadOnlyPersonalStoreRejectsActions()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "p.json");
        File.WriteAllText(path, "{ corrupt");
        var store = new EntertainmentStore(() => _store.Index, path, new InMemorySecretStore(), automaticEnrichment: false);
        await store.ReloadAsync();
        Assert.False(store.CanWrite);
        await Assert.ThrowsAsync<AiException>(() => Ai.ApplyAsync(new AiAction(AiActionKind.AddWatchlist, [store.Catalogue[0].Id], ""), store));
        Assert.Equal("{ corrupt", File.ReadAllText(path));
        Assert.False(Ai.CanUndo);
    }
}
