using System.Text.Json;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// A portable source reference. URLs carry no credentials, query or fragment.
public sealed record EntertainmentSource(Guid Id, string Name, string Url);

/// Portable, non-secret preferences. The download directory is shown for reconnection, never applied.
public sealed record EntertainmentAppPreferences(
    string Theme, int ConcurrentDownloads, int ConnectionsPerFile, int SplitCount, int RetryCount, string SpeedLimit,
    string? PreviousDownloadDirectory = null);

/// Player preferences inside an archive (macOS PlayerPersonalState keys). Volume stays local.
public sealed record ArchivePlayerState
{
    public float Speed { get; init; } = 1;
    public string AudioLanguage { get; init; } = "en";
    public string SubtitleLanguage { get; init; } = "en";
    public bool Autoplay { get; init; } = true;
    public bool AutomaticSkipping { get; init; }
    public Dictionary<string, PlayerSkipMarkers> Markers { get; init; } = [];

    public static ArchivePlayerState From(PlayerPersonalState state) => new()
    {
        Speed = state.Speed,
        AudioLanguage = state.AudioLanguage,
        SubtitleLanguage = state.SubtitleLanguage,
        Autoplay = state.Autoplay,
        AutomaticSkipping = state.AutomaticSkipping,
        Markers = new Dictionary<string, PlayerSkipMarkers>(state.Markers),
    };
}

/// The Personal Library export file. Compatible with the macOS app in both directions.
public sealed class EntertainmentArchive
{
    public int SchemaVersion { get; set; } = 1;
    public EntertainmentPersonalData Personal { get; set; } = new();
    public List<EntertainmentSource> Sources { get; set; } = [];
    public EntertainmentAppPreferences? AppPreferences { get; set; }
    public ArchivePlayerState? PlayerState { get; set; }
}

/// What an import would bring in, for the preview screen.
public sealed record PersonalImportPreview(EntertainmentArchive Archive)
{
    public int Sources => Archive.Sources.Count;
    public int Watchlist => Archive.Personal.Watchlist.Count;
    public int Watched => Archive.Personal.Watched.Count;
    public int Collections => Archive.Personal.Collections.Count;
    public int Followed => Archive.Personal.Followed.Count;
    public int History => Archive.Personal.History.Count;
    public int MatchCorrections => Archive.Personal.MatchCorrections.Count;
    public int MarkerConfigurations => Archive.PlayerState?.Markers.Count ?? 0;

    /// Markers keyed by local file paths may need the same files on this machine.
    public int LocalPathMarkers => Archive.PlayerState?.Markers.Keys.Count(k => k.StartsWith("file:", StringComparison.Ordinal)) ?? 0;

    public string? PreviousDownloadDirectory => Archive.AppPreferences?.PreviousDownloadDirectory;

    public string Summary =>
        $"{Sources} sources • {Watchlist} watchlist • {Watched} watched • {Collections} collections • {Followed} followed • "
        + $"{History} history • {MatchCorrections} match corrections • {MarkerConfigurations} skip marker configurations";
}

public sealed record PersonalImportResult(string FullBackupPath, string? PersonalBackupPath, string? PreviousDownloadDirectory)
{
    public string Message => PreviousDownloadDirectory is { } path
        ? $"Imported. Previous download folder: {path}. Choose a folder in Settings if needed."
        : "Personal library imported. Backup saved before changes.";
}

/// Personal Library → Export / Import across sources, settings, personal records and player state.
/// API keys, passwords, aria2/VLC overrides and download paths are never exported or applied.
public sealed partial class PersonalLibraryTransfer(AppStore store, EntertainmentStore entertainment, Func<PlayerPersonalState> player, Action<PlayerPersonalState> savePlayer)
{
    public const string Explanation =
        "Back up your sources, personal library, playback preferences, history, match corrections, and skip markers. "
        + "API keys, passwords, and machine credentials are excluded. Source URL query parameters are omitted; "
        + "sources that rely on them need reconnecting after import.";

    public List<EntertainmentSource> PortableSources() => store.Categories
        .Select(c => new EntertainmentSource(c.Id, c.Name, StripCredentials(c.RootUrlString)))
        .ToList();

    private static string StripCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "";

    public EntertainmentAppPreferences PortablePreferences()
    {
        var s = store.Settings;
        return new EntertainmentAppPreferences(
            s.Theme.ToString().ToLowerInvariant(), s.ConcurrentDownloads, s.ConnectionsPerFile, s.SplitCount, s.RetryCount, s.SpeedLimit,
            s.DownloadDirectory);
    }

    public byte[] Export() => entertainment.ExportArchive(PortableSources(), PortablePreferences(), player());

    public static PersonalImportPreview Preview(byte[] data) => new(EntertainmentStore.PreviewImport(data));

    [GeneratedRegex("^[0-9]+([KM])?$")]
    private static partial Regex SpeedLimitPattern();

    /// Validates, saves "Myra-before-import-{id}.json" (a full export) in backupDirectory, then applies the
    /// archive. On failure the sources, settings and personal data are restored and the error is rethrown.
    public PersonalImportResult Import(EntertainmentArchive raw, bool replace, string? backupDirectory = null)
    {
        var archive = EntertainmentStore.PreviewImport(JsonSerializer.SerializeToUtf8Bytes(raw, SwiftJson.Options));
        AppThemeMode theme = store.Settings.Theme;
        if (archive.AppPreferences is { } prefs)
        {
            if (prefs.ConcurrentDownloads is < 1 or > 20 || prefs.ConnectionsPerFile is < 1 or > 16 || prefs.SplitCount is < 1 or > 16
                || prefs.RetryCount is < 1 or > 20 || !TryTheme(prefs.Theme, out theme) || !SpeedLimitPattern().IsMatch(prefs.SpeedLimit ?? ""))
                throw new InvalidImportException("Invalid download or appearance preferences.");
        }

        var original = Export();
        var previousPersonal = entertainment.Personal.Clone();
        var previousPlayer = player().Clone();
        var existingPortable = PortableSources();
        var categories = store.Categories.Select(c => (c, c.Name, c.RootUrlString)).ToList();
        var settings = store.Settings.Snapshot();
        var previousTheme = store.Settings.Theme;

        var folder = backupDirectory ?? AppPaths.DataDirectory;
        var backup = Path.Combine(folder, $"Myra-before-import-{Guid.NewGuid().ToString().ToUpperInvariant()}.json");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(backup, original);

        string? personalBackup = null;
        try
        {
            personalBackup = entertainment.ApplyArchive(archive, replace);
            var importedIds = archive.Sources.Select(s => s.Id).ToHashSet();
            if (replace)
                foreach (var category in store.Categories.Where(c => !importedIds.Contains(c.Id)).ToList())
                    store.Categories.Remove(category);
            foreach (var source in archive.Sources)
            {
                if (store.Categories.FirstOrDefault(c => c.Id == source.Id) is { } category)
                {
                    category.Name = source.Name;
                    // A merge keeps a credential-bearing local URL when its portable form is unchanged.
                    var portable = existingPortable.FirstOrDefault(p => p.Id == category.Id)?.Url;
                    if (replace || portable != source.Url) category.RootUrlString = source.Url;
                }
                else
                {
                    store.Categories.Add(new Category { Id = source.Id, Name = source.Name, RootUrlString = source.Url });
                }
            }
            if (archive.AppPreferences is { } p)
            {
                store.Settings.Theme = theme;
                store.Settings.ConcurrentDownloads = p.ConcurrentDownloads;
                store.Settings.ConnectionsPerFile = p.ConnectionsPerFile;
                store.Settings.SplitCount = p.SplitCount;
                store.Settings.RetryCount = p.RetryCount;
                store.Settings.SpeedLimit = p.SpeedLimit;
            }
            if (archive.PlayerState is { } imported)
            {
                var state = player().Clone();
                state.Speed = imported.Speed;
                state.AudioLanguage = imported.AudioLanguage;
                state.SubtitleLanguage = imported.SubtitleLanguage;
                state.Autoplay = imported.Autoplay;
                state.AutomaticSkipping = imported.AutomaticSkipping;
                state.Markers = replace
                    ? new Dictionary<string, PlayerSkipMarkers>(imported.Markers)
                    : state.Markers.Concat(imported.Markers).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Last().Value);
                savePlayer(state);
            }
            store.Save();
        }
        catch
        {
            // Restore the previous state; the full backup file remains for manual recovery.
            foreach (var category in store.Categories.Where(c => categories.All(old => old.c.Id != c.Id)).ToList()) store.Categories.Remove(category);
            foreach (var (category, name, url) in categories)
            {
                category.Name = name;
                category.RootUrlString = url;
                if (!store.Categories.Contains(category)) store.Categories.Add(category);
            }
            store.Settings.Theme = previousTheme;
            store.Settings.ConcurrentDownloads = settings.ConcurrentDownloads;
            store.Settings.ConnectionsPerFile = settings.ConnectionsPerFile;
            store.Settings.SplitCount = settings.SplitCount;
            store.Settings.RetryCount = settings.RetryCount;
            store.Settings.SpeedLimit = settings.SpeedLimit;
            try
            {
                entertainment.ApplyArchive(new EntertainmentArchive { Personal = previousPersonal }, replace: true);
                savePlayer(previousPlayer);
            }
            catch (Exception recovery) when (recovery is InvalidImportException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidImportException($"Import recovery requires the preserved backup at {backup}: {recovery.Message}");
            }
            throw;
        }
        return new PersonalImportResult(backup, personalBackup, archive.AppPreferences?.PreviousDownloadDirectory);
    }

    private static bool TryTheme(string value, out AppThemeMode theme)
    {
        switch (value)
        {
            case "system":
                theme = AppThemeMode.System;
                return true;
            case "light":
                theme = AppThemeMode.Light;
                return true;
            case "dark":
                theme = AppThemeMode.Dark;
                return true;
            default:
                theme = AppThemeMode.System;
                return false;
        }
    }
}
