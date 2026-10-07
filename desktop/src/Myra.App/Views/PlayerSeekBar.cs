using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Seekbar drawn like the macOS player: a full, high-contrast track, a 6 px progress bar and a visible thumb.
public sealed class PlayerSeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PlayerSeekBar, double>(nameof(Value));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<PlayerSeekBar, double>(nameof(Duration));

    static PlayerSeekBar()
    {
        AffectsRender<PlayerSeekBar>(ValueProperty, IsEnabledProperty);
        FocusableProperty.OverrideDefaultValue<PlayerSeekBar>(false);
    }

    /// Position as a fraction from 0 to 1.
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// Hover tooltips are off in fullscreen, as on macOS.
    public bool AllowTooltips { get; set; } = true;

    public event Action? ScrubStarted;
    public event Action<double>? ScrubChanged;
    public event Action<double>? ScrubCommitted;

    private bool _scrubbing;
    private double _scrubValue;

    private double Shown => _scrubbing ? _scrubValue : Math.Clamp(double.IsFinite(Value) ? Value : 0, 0, 1);

    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 200, 22);

    public override void Render(DrawingContext context)
    {
        var enabled = IsEnabled;
        var track = new Rect(SeekBarGeometry.Inset, Bounds.Height / 2 - 3, Math.Max(1, Bounds.Width - SeekBarGeometry.Inset * 2), 6);
        var accent = AccentBrush();
        // The chrome is always dark, so the unfilled track is light on dark.
        context.DrawRectangle(new SolidColorBrush(Colors.White, 0.30), new Pen(new SolidColorBrush(Colors.White, 0.55), 1), new RoundedRect(track, 3));
        var fraction = Shown;
        if (fraction > 0)
            context.DrawRectangle(enabled ? accent : new SolidColorBrush(Colors.Gray), null, new RoundedRect(new Rect(track.X, track.Y, track.Width * fraction, track.Height), 3));
        var centre = new Point(track.X + track.Width * fraction, track.Center.Y);
        context.DrawEllipse(new SolidColorBrush(enabled ? Colors.White : Colors.LightGray),
            new Pen(enabled ? accent : new SolidColorBrush(Colors.Gray), 2), centre, 7, 7);
    }

    private IBrush AccentBrush() =>
        this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var value) && value is Color color
            ? new SolidColorBrush(color)
            : new SolidColorBrush(Color.Parse("#3B82F6"));

    private double FractionAt(PointerEventArgs e) => SeekBarGeometry.Fraction(e.GetPosition(this).X, Bounds.Width);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _scrubbing = true;
        e.Pointer.Capture(this);
        ScrubStarted?.Invoke();
        Update(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scrubbing)
        {
            Update(e);
            return;
        }
        if (!AllowTooltips)
        {
            ToolTip.SetTip(this, null);
            return;
        }
        ToolTip.SetTip(this, IsEnabled ? "Seek to " + PlayerTime.Format(FractionAt(e) * Duration) : "This source is not seekable");
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing) return;
        Update(e);
        e.Pointer.Capture(null);
        Finish();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_scrubbing) Finish();
    }

    private void Update(PointerEventArgs e)
    {
        _scrubValue = FractionAt(e);
        InvalidateVisual();
        ScrubChanged?.Invoke(_scrubValue);
    }

    private void Finish()
    {
        _scrubbing = false;
        InvalidateVisual();
        ScrubCommitted?.Invoke(_scrubValue);
    }
}
