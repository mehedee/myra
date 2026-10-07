using Avalonia.Controls;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Services;

/// Secondary windows and dialogs. The main window calls these; it does not build them itself.
/// Implemented in AppDialogs.Implementation.cs. Each method returns when the dialog closes.
public interface IAppDialogs
{
    /// Settings: download folder, aria2, theme, indexing mode, and the OMDb, TMDB and OpenSubtitles credentials.
    Task ShowSettingsAsync(Window owner);

    /// Index Management: folder tree, schedules, selected refresh, full rescan, pause and cancel.
    Task ShowIndexManagementAsync(Window owner);

    /// Personal Library export and import with preview, merge or replace.
    Task ShowPersonalLibraryAsync(Window owner);

    /// About, with OMDb, TMDB and libVLC attribution.
    Task ShowAboutAsync(Window owner);

    /// Correct Match for one title. Returns true when the store accepted a correction.
    Task<bool> ShowCorrectMatchAsync(Window owner, EntertainmentTitle title);

    /// Find Online Subtitles. Returns the cached subtitle path, or null when cancelled.
    Task<string?> ShowOnlineSubtitlesAsync(Window owner, GlobalSearchResult media);

    /// Playback Preferences: audio and subtitle languages, speed, autoplay.
    Task ShowPlaybackPreferencesAsync(Window owner, PlayerViewModel player);

    /// Intro / Outro Markers editor for the current episode or series.
    Task ShowSkipMarkersAsync(Window owner, PlayerViewModel player);

    /// Version chooser. Returns the chosen release, or null when cancelled.
    Task<GlobalSearchResult?> ChooseVersionAsync(Window owner, IReadOnlyList<GlobalSearchResult> versions);
}
