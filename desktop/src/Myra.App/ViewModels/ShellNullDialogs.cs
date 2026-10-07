using Avalonia.Controls;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.ViewModels;

/// Fallback for IAppDialogs until the real dialogs are wired in App. Every dialog closes at once.
/// The version chooser returns the first release so playback still starts.
public sealed class NullAppDialogs : IAppDialogs
{
    public Task ShowSettingsAsync(Window owner) => Task.CompletedTask;
    public Task ShowIndexManagementAsync(Window owner) => Task.CompletedTask;
    public Task ShowPersonalLibraryAsync(Window owner) => Task.CompletedTask;
    public Task ShowAboutAsync(Window owner) => Task.CompletedTask;
    public Task<bool> ShowCorrectMatchAsync(Window owner, EntertainmentTitle title) => Task.FromResult(false);
    public Task<string?> ShowOnlineSubtitlesAsync(Window owner, GlobalSearchResult media) => Task.FromResult<string?>(null);
    public Task ShowPlaybackPreferencesAsync(Window owner, PlayerViewModel player) => Task.CompletedTask;
    public Task ShowSkipMarkersAsync(Window owner, PlayerViewModel player) => Task.CompletedTask;

    public Task<GlobalSearchResult?> ChooseVersionAsync(Window owner, IReadOnlyList<GlobalSearchResult> versions) =>
        Task.FromResult(versions.FirstOrDefault());
}
