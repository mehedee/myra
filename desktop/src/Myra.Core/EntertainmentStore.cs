using System.Text.Json;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// Raised after a catalogue reload finds newly indexed episodes of followed series.
/// Notify mirrors the user's opt-in; showing a desktop notification is the UI's job.
public sealed class NewEpisodesEventArgs(IReadOnlySet<string> episodeIds, bool notify) : EventArgs
{
    public IReadOnlySet<string> EpisodeIds { get; } = episodeIds;
    public bool Notify { get; } = notify;
    public string Message => $"{EpisodeIds.Count} new episode(s) were indexed for series you follow.";
}

/// Home catalogue plus personal records (watchlist, watched, collections, follows, history,
/// corrections). Personal data lives in EntertainmentPersonal.json, outside the disposable index.
/// A corrupt personal file is preserved untouched and the store becomes read-only.
///
/// Thread-safe. Changed fires on the calling or a worker thread; the UI must marshal.
/// Personal and Catalogue return immutable-by-convention snapshots: do not modify them.
public sealed partial class EntertainmentStore
{
    public const int EnrichmentBatch = 50;

    private readonly Func<LibraryIndex> _index;
    private readonly ISecretStore _secrets;
    private readonly DiscoveryService _discovery;
    private readonly Func<bool> _isIndexing;
    private readonly bool _automaticEnrichment;
    private readonly Lock _lock = new();
    private EntertainmentPersonalData _personal = new();
    private IReadOnlyList<EntertainmentTitle> _catalogue = [];
    private HashSet<string> _newEpisodeIds = [];
    private HashSet<string> _enrichmentIds = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loading;
    private bool _reloadAgain;
    private int _enriching;

    public EntertainmentStore(
        Func<LibraryIndex> index,
        string? storagePath = null,
        ISecretStore? secrets = null,
        DiscoveryService? discovery = null,
        bool automaticEnrichment = true,
        Func<bool>? isIndexing = null)
    {
        _index = index;
        StoragePath = storagePath ?? AppPaths.PersonalLibraryPath;
        _secrets = secrets ?? SecretStore.CreateDefault();
        _discovery = discovery ?? new DiscoveryService();
        _automaticEnrichment = automaticEnrichment;
        _isIndexing = isIndexing ?? (() => false);
        if (!File.Exists(StoragePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<EntertainmentPersonalData>(File.ReadAllText(StoragePath), SwiftJson.Options)
                         ?? throw new InvalidImportException("Empty personal library.");
            loaded.Validate();
            _personal = loaded;
        }
        catch (Exception error)
        {
            // Any failure (including unexpected shapes) keeps the file untouched and the app usable.
            CanWrite = false;
            ErrorMessage = "Personal library could not be loaded. The existing file has been preserved: " + error.Message;
        }
    }

    public string StoragePath { get; }
    public bool CanWrite { get; private set; } = true;
    public string? ErrorMessage { get; private set; }
    public bool IsEnriching => Volatile.Read(ref _enriching) == 1;

    /// Incremented whenever catalogue, personal data or new-episode markers change.
    public long ProjectionRevision { get; private set; }

    public EntertainmentPersonalData Personal
    {
        get { lock (_lock) return _personal; }
    }

    public IReadOnlyList<EntertainmentTitle> Catalogue
    {
        get { lock (_lock) return _catalogue; }
    }

    public IReadOnlySet<string> NewEpisodeIds
    {
        get { lock (_lock) return _newEpisodeIds; }
    }

    public event EventHandler? Changed;
    public event EventHandler<NewEpisodesEventArgs>? NewEpisodesDetected;

    private void RaiseChanged()
    {
        lock (_lock) ProjectionRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// Shutdown: cancels catalogue preparation and enrichment, then waits up to timeout for them to stop.
    public bool StopBackgroundWork(TimeSpan timeout)
    {
        _stopping.Cancel();
        Task? loading;
        lock (_lock) loading = _loading;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        try
        {
            if (loading is not null && !loading.Wait(timeout)) return false;
        }
        catch (AggregateException)
        {
        }
        while (IsEnriching && Environment.TickCount64 < deadline) Thread.Sleep(20);
        return !IsEnriching;
    }

    public void ClearError()
    {
        ErrorMessage = null;
        RaiseChanged();
    }

    // ---------- Catalogue ----------

    /// Rebuilds (or restores) the catalogue. Concurrent calls coalesce into one more pass and all
    /// callers resume when the catalogue is current. Automatic enrichment then starts in the background.
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping.IsCancellationRequested) return Task.CompletedTask;
        if (!cancellationToken.CanBeCanceled) cancellationToken = _stopping.Token;
        lock (_lock)
        {
            if (_loading is { IsCompleted: false } running)
            {
                _reloadAgain = true;
                return running;
            }
            _loading = Task.Run(() => LoadLoopAsync(cancellationToken), CancellationToken.None);
            return _loading;
        }
    }

    private async Task LoadLoopAsync(CancellationToken cancellationToken)
    {
        bool again;
        do
        {
            lock (_lock) _reloadAgain = false;
            try
            {
                var personal = Personal;
                var prepared = await Task.Run(() => EntertainmentCatalogue.Prepare(_index(), personal, cancellationToken), cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    again = _reloadAgain;
                    if (!again)
                    {
                        // Playback may advance during preparation. Merge the latest history records.
                        _catalogue = prepared.Titles.Select(t => WithHistory(t, _personal)).ToList();
                        _enrichmentIds = [.. prepared.EnrichmentIds];
                    }
                }
                if (!again)
                {
                    RaiseChanged();
                    DiscoverEpisodes();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                // Background work: report every failure in the UI instead of faulting an unobserved task.
                ErrorMessage = error.Message;
                RaiseChanged();
                lock (_lock) again = _reloadAgain;
            }
        } while (again);

        bool enrich;
        lock (_lock) enrich = _automaticEnrichment && _enrichmentIds.Count > 0;
        if (enrich && HasTmdbToken()) _ = EnrichAsync(_stopping.Token);
    }

    private bool HasTmdbToken()
    {
        try
        {
            return _secrets.Read(SecretNames.TmdbReadToken).Trim().Length > 0;
        }
        catch (SecretStoreException)
        {
            return false;
        }
    }

    private static EntertainmentTitle WithHistory(EntertainmentTitle title, EntertainmentPersonalData personal)
    {
        if (!title.Versions.Any(v => personal.History.ContainsKey(v.Id))) return title;
        return title with
        {
            Versions = title.Versions.Select(v => personal.History.TryGetValue(v.Id, out var record)
                ? v with { ProgressSeconds = record.Seconds, Duration = record.Duration, LastPlayed = record.Updated }
                : v).ToList(),
        };
    }

    /// Enriches up to 50 titles that lack fresh TMDB data. Pauses while indexing runs. Ambiguous titles
    /// are remembered for 7 days and reported so the user can Correct Match.
    public async Task EnrichAsync(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = _stopping.Token;
        if (cancellationToken.IsCancellationRequested || _isIndexing() || Interlocked.CompareExchange(ref _enriching, 1, 0) != 0) return;
        RaiseChanged();
        try
        {
            var token = _secrets.Read(SecretNames.TmdbReadToken).Trim();
            if (token.Length == 0) throw new DiscoveryException(DiscoveryErrorKind.MissingToken);
            var index = _index();
            List<EntertainmentTitle> targets;
            lock (_lock) targets = _catalogue.Where(t => _enrichmentIds.Contains(t.Id)).Take(EnrichmentBatch).ToList();
            var uncertain = 0;
            foreach (var target in targets)
            {
                while (_isIndexing()) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var correction = EntertainmentCatalogue.CorrectionFor(target, Personal);
                try
                {
                    var metadata = await _discovery.LookupAsync(target, correction, token, index, cancellationToken).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _catalogue = _catalogue.Select(t => t.Id == target.Id ? t with { Metadata = metadata } : t).ToList();
                        _enrichmentIds.Remove(target.Id);
                    }
                    RaiseChanged();
                }
                catch (DiscoveryException error) when (error.Kind == DiscoveryErrorKind.Ambiguous)
                {
                    uncertain++;
                    await Task.Run(() => index.SaveMetadata("tmdb-attempt|" + target.MetadataCacheKey(correction?.ProviderId), "ambiguous"), cancellationToken).ConfigureAwait(false);
                    lock (_lock) _enrichmentIds.Remove(target.Id);
                }
            }
            if (uncertain > 0)
                ErrorMessage = $"{uncertain} titles need a confident match. Use Correct Match. Enrichment processes up to {EnrichmentBatch} titles per run.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
        finally
        {
            Volatile.Write(ref _enriching, 0);
            RaiseChanged();
        }
    }

    public EntertainmentTitle? TitleContaining(GlobalSearchResult media)
    {
        var id = media.Entry.Url.AbsoluteUri;
        return Catalogue.FirstOrDefault(t => t.Versions.Any(v => v.Id == id));
    }

    /// Alternate releases of the same movie/episode as media (same season and episode), from the catalogue.
    public List<EntertainmentVersion> Variants(GlobalSearchResult media)
    {
        var id = media.Entry.Url.AbsoluteUri;
        if (TitleContaining(media) is not { } title || title.Versions.FirstOrDefault(v => v.Id == id) is not { } selected) return [];
        return title.Versions.Where(v => v.Season == selected.Season && v.Episode == selected.Episode).ToList();
    }

    public bool HasNewEpisode(EntertainmentVersion version) => NewEpisodeIds.Contains(FollowedEpisodes.EpisodeId(version));

    private void DiscoverEpisodes()
    {
        HashSet<string> found;
        HashSet<string> current;
        lock (_lock)
        {
            current = FollowedEpisodes.Current(_catalogue, _personal.Followed);
            found = [.. current.Except(_personal.KnownEpisodeIds)];
            if (found.Count > 0) _newEpisodeIds = [.. _newEpisodeIds.Union(found)];
        }
        if (found.Count == 0) return;
        Mutate(data => data.KnownEpisodeIds.UnionWith(current));
        NewEpisodesDetected?.Invoke(this, new NewEpisodesEventArgs(found, Personal.Preferences.Notifications));
    }

    // ---------- Personal records ----------

    public bool ToggleWatchlist(string titleId) => Mutate(data => Toggle(data.Watchlist, titleId));
    public bool ToggleWatched(string titleId) => Mutate(data => Toggle(data.Watched, titleId));

    /// Following records the series' current episodes as the baseline, so only later ones are new.
    public bool ToggleFollow(string titleId)
    {
        var title = Catalogue.FirstOrDefault(t => t.Id == titleId);
        return Mutate(data =>
        {
            Toggle(data.Followed, titleId);
            if (data.Followed.Contains(titleId) && title is not null)
                data.KnownEpisodeIds.UnionWith(title.Versions.Select(FollowedEpisodes.EpisodeId));
        });
    }

    public bool CreateCollection(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        return Mutate(data => data.Collections.Add(new EntertainmentCollection { Name = Truncate(name, 200) }));
    }

    public bool RenameCollection(Guid id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        return Mutate(data =>
        {
            if (data.Collections.FirstOrDefault(c => c.Id == id) is { } collection) collection.Name = Truncate(name, 200);
        });
    }

    public bool DeleteCollection(Guid id) => Mutate(data => data.Collections.RemoveAll(c => c.Id == id));

    public bool ToggleCollection(string titleId, Guid collectionId) => Mutate(data =>
    {
        if (data.Collections.FirstOrDefault(c => c.Id == collectionId) is { } collection) Toggle(collection.TitleIds, titleId);
    });

    /// Opt-in flag for followed-show notifications. Asking the OS for permission is the UI's job.
    public bool SetNotifications(bool enabled) => Mutate(data => data.Preferences.Notifications = enabled);

    /// Corrects one or more files' identity. Watchlist, watched, follow and collection membership move
    /// from the old title ID to the new one. Reloads the catalogue. Returns false for invalid input.
    public async Task<bool> CorrectMatchAsync(
        IReadOnlyList<string> versionIds, string title, string? year, EntertainmentKind kind, int? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var name = title.Trim();
        year = string.IsNullOrWhiteSpace(year) ? null : year.Trim();
        if (versionIds.Count == 0 || name.Length == 0 || name.Length > 300 || providerId is <= 0
            || (year is not null && !EntertainmentGrouping.YearPattern().IsMatch(year)))
        {
            ErrorMessage = "Enter a title, an optional four-digit year, and a positive TMDB ID.";
            RaiseChanged();
            return false;
        }
        var catalogue = Catalogue;
        var nextId = EntertainmentGrouping.TitleId(kind, name, year);
        var changed = Mutate(data =>
        {
            foreach (var versionId in versionIds)
            {
                var previousId = catalogue.FirstOrDefault(t => t.Versions.Any(v => v.Id == versionId))?.Id;
                data.MatchCorrections[versionId] = new EntertainmentMatchCorrection(name, year, kind, providerId);
                if (previousId is null || previousId == nextId) continue;
                if (data.Watchlist.Remove(previousId)) data.Watchlist.Add(nextId);
                if (data.Watched.Remove(previousId)) data.Watched.Add(nextId);
                if (data.Followed.Remove(previousId)) data.Followed.Add(nextId);
                foreach (var collection in data.Collections)
                    if (collection.TitleIds.Remove(previousId)) collection.TitleIds.Add(nextId);
            }
        });
        if (changed) await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// Saves playback progress for history, Continue Watching and resume. Ignores invalid numbers.
    public void RecordPlayback(GlobalSearchResult media, double seconds, double duration, DateTimeOffset? now = null)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(duration) || seconds < 0 || duration < 0) return;
        var id = media.Entry.Url.AbsoluteUri;
        var record = new EntertainmentPlaybackRecord(seconds, duration, now ?? DateTimeOffset.Now);
        if (!Mutate(data => data.History[id] = record)) return;
        lock (_lock)
        {
            _catalogue = _catalogue.Select(t => t.Versions.Any(v => v.Id == id)
                ? t with { Versions = t.Versions.Select(v => v.Id == id ? v with { ProgressSeconds = seconds, Duration = duration, LastPlayed = record.Updated } : v).ToList() }
                : t).ToList();
        }
        RaiseChanged();
    }

    /// A naturally finished movie becomes watched; the file's progress is set to its full duration.
    public void MarkPlaybackEnded(GlobalSearchResult media)
    {
        var id = media.Entry.Url.AbsoluteUri;
        if (TitleContaining(media) is not { } title) return;
        if (title.Kind == EntertainmentKind.Movie) Mutate(data => data.Watched.Add(title.Id));
        if (Personal.History.TryGetValue(id, out var record)) RecordPlayback(media, record.Duration, record.Duration);
    }

    private static void Toggle(HashSet<string> set, string value)
    {
        if (!set.Add(value)) set.Remove(value);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    /// Applies a change to a copy, validates it, writes it atomically and then publishes it.
    private bool Mutate(Action<EntertainmentPersonalData> change)
    {
        lock (_lock)
        {
            if (!CanWrite)
            {
                ErrorMessage = "Personal data is read-only because the existing file could not be loaded.";
            }
            else
            {
                var updated = _personal.Clone();
                change(updated);
                try
                {
                    updated.Validate();
                    Write(updated);
                    _personal = updated;
                    ProjectionRevision++;
                }
                catch (Exception error) when (error is InvalidImportException or IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = error.Message;
                    return false;
                }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return CanWrite;
    }

    private void Write(EntertainmentPersonalData data) =>
        AtomicFile.WriteAllText(StoragePath, JsonSerializer.Serialize(data, SwiftJson.Options));

    // ---------- Portable export / import (personal part) ----------

    /// Builds a portable archive. URL credentials, query strings and fragments are removed from every
    /// library reference, including sources and marker keys. Throws when two references collide.
    public byte[] ExportArchive(IReadOnlyList<EntertainmentSource> sources, EntertainmentAppPreferences? appPreferences, PlayerPersonalState? playerState)
    {
        var cleaned = Personal.Clone();
        cleaned.Watchlist = PortableIds(cleaned.Watchlist);
        cleaned.Watched = PortableIds(cleaned.Watched);
        cleaned.Followed = PortableIds(cleaned.Followed);
        cleaned.KnownEpisodeIds = PortableIds(cleaned.KnownEpisodeIds);
        foreach (var collection in cleaned.Collections) collection.TitleIds = PortableIds(collection.TitleIds);
        cleaned.History = PortableDictionary(cleaned.History);
        cleaned.MatchCorrections = PortableDictionary(cleaned.MatchCorrections);
        ArchivePlayerState? player = playerState is null ? null : ArchivePlayerState.From(playerState) with
        {
            Markers = PortableDictionary(playerState.Markers),
        };
        var archive = new EntertainmentArchive
        {
            Personal = cleaned,
            Sources = sources.Select(s => s with { Url = PortableId(s.Url) }).ToList(),
            AppPreferences = appPreferences,
            PlayerState = player,
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(archive, SwiftJson.Indented);
        PreviewImport(data);
        return data;
    }

    /// Parses and validates an archive (20 MB limit, version 1, sources, personal data, player state).
    public static EntertainmentArchive PreviewImport(byte[] data)
    {
        if (data.Length > 20_000_000) throw new InvalidImportException("Backup exceeds 20 MB.");
        EntertainmentArchive? archive;
        try
        {
            archive = JsonSerializer.Deserialize<EntertainmentArchive>(data, SwiftJson.Options);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidImportException("The backup is not a valid Myra personal library file: " + error.Message);
        }
        if (archive is null || archive.Personal is null || archive.Sources is null) throw new InvalidImportException("The backup is incomplete.");
        if (archive.SchemaVersion != 1) throw new InvalidImportException("Unsupported backup version.");
        archive.Personal.Validate();
        if (archive.Sources.Count > 1000 || archive.Sources.Any(s => s is null)
            || archive.Sources.Select(s => s.Id).Distinct().Count() != archive.Sources.Count)
            throw new InvalidImportException("Invalid or duplicate sources.");
        foreach (var source in archive.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 300
                || !Uri.TryCreate(source.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
                || url.Host.Length == 0 || url.UserInfo.Length > 0)
                throw new InvalidImportException("Sources must use HTTP(S) URLs without embedded credentials.");
            try
            {
                _ = new UrlBoundary(url);
            }
            catch (DirectoryException)
            {
                throw new InvalidImportException("Sources must use HTTP(S) URLs without embedded credentials.");
            }
        }
        if (archive.PlayerState is { } state)
        {
            if (!float.IsFinite(state.Speed) || state.Speed < 0.25f || state.Speed > 4 || state.Markers is null || state.Markers.Count > 100_000
                || state.AudioLanguage is not { Length: > 0 and <= 20 } || state.SubtitleLanguage is not { Length: > 0 and <= 20 })
                throw new InvalidImportException("Invalid player preferences.");
            foreach (var markers in state.Markers.Values)
                foreach (var range in new[] { markers?.Intro, markers?.Outro })
                    if (range is not null && !(double.IsFinite(range.Start) && double.IsFinite(range.End) && range.Start >= 0 && range.End > range.Start))
                        throw new InvalidImportException("Invalid skip ranges.");
        }
        return archive;
    }

    /// Applies the personal part of an archive. The current personal file is copied to
    /// "EntertainmentPersonal.backup-{id}.json" first. Merge unions sets, adds unknown collections,
    /// prefers imported corrections and keeps the newer history record per file.
    public string? ApplyArchive(EntertainmentArchive archive, bool replace)
    {
        PreviewImport(JsonSerializer.SerializeToUtf8Bytes(archive, SwiftJson.Options));
        if (!CanWrite) throw new InvalidImportException("The personal library is unavailable; its existing file has been preserved.");
        string? backup = null;
        lock (_lock)
        {
            if (File.Exists(StoragePath))
            {
                backup = Path.Combine(Path.GetDirectoryName(StoragePath)!,
                    Path.GetFileNameWithoutExtension(StoragePath) + $".backup-{Guid.NewGuid().ToString().ToUpperInvariant()}.json");
                File.Copy(StoragePath, backup);
            }
            var result = replace ? archive.Personal.Clone() : Merge(_personal, archive.Personal);
            result.Validate();
            Write(result);
            _personal = result;
            ProjectionRevision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return backup;
    }

    internal static EntertainmentPersonalData Merge(EntertainmentPersonalData current, EntertainmentPersonalData incoming)
    {
        var result = current.Clone();
        result.Watchlist.UnionWith(incoming.Watchlist);
        result.Watched.UnionWith(incoming.Watched);
        result.Followed.UnionWith(incoming.Followed);
        result.KnownEpisodeIds.UnionWith(incoming.KnownEpisodeIds);
        foreach (var collection in incoming.Collections)
        {
            if (result.Collections.FirstOrDefault(c => c.Id == collection.Id) is { } existing) existing.TitleIds.UnionWith(collection.TitleIds);
            else result.Collections.Add(collection.Clone());
        }
        foreach (var (key, value) in incoming.MatchCorrections) result.MatchCorrections[key] = value;
        foreach (var (key, value) in incoming.History)
            if (!result.History.TryGetValue(key, out var old) || old.Updated <= value.Updated) result.History[key] = value;
        return result;
    }

    [GeneratedRegex(@"https?://[^|\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();

    /// Removes URL credentials, queries and fragments from every URL inside an identity reference.
    public static string PortableId(string value) => UrlInText().Replace(value, match =>
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var url))
            throw new InvalidImportException("A library URL could not be prepared for export.");
        return url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    });

    private static HashSet<string> PortableIds(HashSet<string> values)
    {
        var result = values.Select(PortableId).ToHashSet();
        if (result.Count != values.Count) throw DuplicateReferences();
        return result;
    }

    private static Dictionary<string, TValue> PortableDictionary<TValue>(Dictionary<string, TValue> values)
    {
        var result = new Dictionary<string, TValue>();
        foreach (var (key, value) in values)
            if (!result.TryAdd(PortableId(key), value)) throw DuplicateReferences();
        return result;
    }

    private static InvalidImportException DuplicateReferences() => new(
        "Two library references differ only by credentials or query tokens. Resolve duplicate sources before exporting.");
}
