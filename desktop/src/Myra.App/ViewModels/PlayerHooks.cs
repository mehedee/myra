using Myra.Core;

namespace Myra.App.ViewModels;

// Optional player features whose logic lives elsewhere (Myra.Core and other windows).
// Assign an implementation to the matching PlayerViewModel property to switch the feature on.
// While the property is null, the player hides the control.

/// A "Skip Intro" or "Skip Outro" button shown while playback is inside a marker range.
public sealed record PlayerSkipPrompt(string Label, double Start, double End);

/// Gear button: opens playback preferences (audio and subtitle language, speed, autoplay).
/// While this hook is null, the player shows its own Autoplay toggle instead.
public interface IPlayerPreferencesHook
{
    void OpenPreferences(PlayerViewModel player);
}

/// Intro and outro markers: skip button, automatic skipping and the marker editor (scissors button).
public interface IPlayerSkipMarkersHook
{
    /// True when the player should jump over a marker as soon as playback enters it.
    bool AutomaticSkipping { get; }

    /// Returns the marker that contains the current position, or null. Called about four times a second.
    PlayerSkipPrompt? ActiveSkip(GlobalSearchResult media, double elapsed, double duration);

    void EditMarkers(PlayerViewModel player);
}

/// Episodes / Files button: opens the folder queue for the playing video.
public interface IPlayerQueueHook
{
    void OpenQueue(PlayerViewModel player);
}

/// Shows the version chooser when several releases of one title are available.
/// The UI must finish with PlayerViewModel.ChooseVersion or PlayerViewModel.CancelVersionChoice.
public interface IPlayerVersionChooser
{
    void Choose(PlayerViewModel player, IReadOnlyList<GlobalSearchResult> versions);
}
