using Avalonia.Controls;
using Myra.App.ViewModels;
using Myra.App.Views;
using Myra.Core;

namespace Myra.App.Services;

/// Builds each secondary window and shows it modally over its owner.
public sealed class AppDialogs(AppServices services) : IAppDialogs
{
    public Task ShowSettingsAsync(Window owner) =>
        new SettingsWindow(services.Store.Settings, services.PlayerState, services).ShowDialog(owner);

    public Task ShowIndexManagementAsync(Window owner) =>
        new IndexManagementWindow(services).ShowDialog(owner);

    public Task ShowPersonalLibraryAsync(Window owner) =>
        new PersonalLibraryDialog(services).ShowDialog(owner);

    public Task ShowAboutAsync(Window owner) =>
        new AboutDialog().ShowDialog(owner);

    public Task<bool> ShowCorrectMatchAsync(Window owner, EntertainmentTitle title) =>
        new CorrectMatchDialog(services.Entertainment, title).ShowDialog<bool>(owner);

    public Task<string?> ShowOnlineSubtitlesAsync(Window owner, GlobalSearchResult media) =>
        new OnlineSubtitlesDialog(services, media).ShowDialog<string?>(owner);

    public Task ShowPlaybackPreferencesAsync(Window owner, PlayerViewModel player) =>
        new PlaybackPreferencesDialog(services, player).ShowDialog(owner);

    public Task ShowSkipMarkersAsync(Window owner, PlayerViewModel player) =>
        new SkipMarkersDialog(services, player).ShowDialog(owner);

    public Task<GlobalSearchResult?> ChooseVersionAsync(Window owner, IReadOnlyList<GlobalSearchResult> versions) =>
        new VersionChooserDialog(services, versions).ShowDialog<GlobalSearchResult?>(owner);
}
