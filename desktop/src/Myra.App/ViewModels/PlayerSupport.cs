using Myra.Core;

namespace Myra.App.ViewModels;

/// Buffering counts only after a real two-second playback stall; libVLC's own buffering state is often stale.
public sealed class PlayerStallDetector
{
    private double? _lastTime;
    private double _lastProgressAt;

    public void Reset(double now)
    {
        _lastTime = null;
        _lastProgressAt = now;
    }

    public bool Update(PlayerState state, double time, double now, bool scrubbing)
    {
        var inactive = state is PlayerState.Paused or PlayerState.Stopped or PlayerState.Ended or PlayerState.Error or PlayerState.Idle;
        var moved = _lastTime is { } last ? Math.Abs(time - last) > 0.001 : time > 0;
        if (inactive || scrubbing || moved) _lastProgressAt = now;
        _lastTime = time;
        return !inactive && !scrubbing && now - _lastProgressAt >= 2;
    }
}

/// Fullscreen controls show on interaction and hide after two seconds of inactivity.
public sealed class PlayerControlsVisibility
{
    public bool Fullscreen { get; set; }
    public bool Hovering { get; set; }
    public bool MenuTracking { get; set; }
    public double LastInteraction { get; private set; } = double.NegativeInfinity;

    public void Interact(double now) => LastInteraction = now;

    public bool IsVisible(double now, bool scrubbing) =>
        !Fullscreen || Hovering || MenuTracking || scrubbing || now - LastInteraction < 2;
}

public sealed record PlayerChapter(int Id, string Name);

public enum PlayerDeinterlace
{
    Automatic,
    On,
    Off,
}

/// Seekbar position helper: the track is inset so the thumb stays inside the control.
public static class SeekBarGeometry
{
    public const double Inset = 9;

    public static double Fraction(double x, double width)
    {
        var track = Math.Max(1, width - Inset * 2);
        return Math.Min(1, Math.Max(0, (x - Inset) / track));
    }
}
