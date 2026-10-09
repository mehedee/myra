namespace Myra.Core;

/// Reviewed Myra AI actions. Used by AiService.ApplyAsync / UndoAsync; the UI should call those.
public sealed partial class EntertainmentStore
{
    /// Applies one reviewed proposal atomically (validate, write, publish). Never renames media files and
    /// never changes playback history. Returns the published personal snapshot. A correction changes
    /// grouping: call ReloadAsync afterwards (AiService.ApplyAsync does). Throws AiException(UnsafeAction).
    public EntertainmentPersonalData ApplyReviewedAiAction(AiAction action)
    {
        EntertainmentPersonalData updated;
        lock (_lock)
        {
            if (!CanWrite) throw new AiException(AiErrorKind.UnsafeAction);
            var ids = action.TitleIds.Distinct(StringComparer.Ordinal).ToList();
            var selected = _catalogue.Where(t => ids.Contains(t.Id)).ToList();
            if (selected.Count != ids.Count || selected.Count == 0) throw new AiException(AiErrorKind.UnsafeAction);
            updated = _personal.Clone();
            switch (action.Kind)
            {
                case AiActionKind.AddWatchlist:
                    updated.Watchlist.UnionWith(ids);
                    break;
                case AiActionKind.MarkWatched:
                    updated.Watched.UnionWith(ids);
                    break;
                case AiActionKind.Collection:
                    if (action.Value.Length is 0 or > 200) throw new AiException(AiErrorKind.UnsafeAction);
                    updated.Collections.Add(new EntertainmentCollection { Name = action.Value, TitleIds = [.. ids] });
                    break;
                case AiActionKind.Correction:
                    ApplyCorrection(updated, selected, action);
                    break;
                default:
                    throw new AiException(AiErrorKind.UnsafeAction);
            }
            try
            {
                updated.Validate();
                Write(updated);
            }
            catch (Exception error) when (error is InvalidImportException or IOException or UnauthorizedAccessException)
            {
                throw new AiException(AiErrorKind.UnsafeAction, inner: error);
            }
            _personal = updated;
            ProjectionRevision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// Correction moves one title's files to a new identity. Watchlist, watched, follow and collection
    /// membership move to the regrouped ID. The year stays when the proposal has none.
    private static void ApplyCorrection(EntertainmentPersonalData updated, List<EntertainmentTitle> selected, AiAction action)
    {
        var name = action.Value.Trim();
        if (selected.Count != 1 || name.Length is 0 or > 300 || action.MediaKind is not { } kind
            || (action.Year is not null && !EntertainmentGrouping.YearPattern().IsMatch(action.Year)))
            throw new AiException(AiErrorKind.UnsafeAction);
        var title = selected[0];
        foreach (var version in title.Versions)
            updated.MatchCorrections[version.Id] = new EntertainmentMatchCorrection(name, action.Year ?? title.Year, kind);
        var regrouped = EntertainmentGrouping.Group(title.Versions, updated.MatchCorrections);
        if (regrouped.Count != 1) throw new AiException(AiErrorKind.UnsafeAction);
        var previousId = title.Id;
        var nextId = regrouped[0].Id;
        if (previousId == nextId) return;
        if (updated.Watchlist.Remove(previousId)) updated.Watchlist.Add(nextId);
        if (updated.Watched.Remove(previousId)) updated.Watched.Add(nextId);
        if (updated.Followed.Remove(previousId)) updated.Followed.Add(nextId);
        foreach (var collection in updated.Collections)
            if (collection.TitleIds.Remove(previousId)) collection.TitleIds.Add(nextId);
    }

    /// Restores a snapshot taken before a reviewed action. Playback history and known episodes keep
    /// their current values. Returns true when grouping may change (call ReloadAsync).
    public bool RestoreReviewedAiSnapshot(EntertainmentPersonalData snapshot)
    {
        bool regroup;
        lock (_lock)
        {
            if (!CanWrite) throw new AiException(AiErrorKind.UnsafeAction);
            var updated = snapshot.Clone();
            updated.History = new Dictionary<string, EntertainmentPlaybackRecord>(_personal.History);
            updated.KnownEpisodeIds = [.. _personal.KnownEpisodeIds];
            try
            {
                updated.Validate();
                Write(updated);
            }
            catch (Exception error) when (error is InvalidImportException or IOException or UnauthorizedAccessException)
            {
                throw new AiException(AiErrorKind.UnsafeAction, inner: error);
            }
            regroup = updated.MatchCorrections.Count > 0 || _personal.MatchCorrections.Count > 0;
            _personal = updated;
            ProjectionRevision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return regroup;
    }
}
