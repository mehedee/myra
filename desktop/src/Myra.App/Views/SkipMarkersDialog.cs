using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Intro / outro markers for the current episode or the whole series. Values are in seconds.
public sealed class SkipMarkersDialog : AppDialog
{
    private readonly PlayerPersonalState _state;
    private readonly Func<double> _position;
    private readonly GlobalSearchResult? _media;
    private readonly CheckBox _intro = new() { Content = "Intro" };
    private readonly CheckBox _outro = new() { Content = "Outro" };
    private readonly NumericUpDown _introStart = Seconds();
    private readonly NumericUpDown _introEnd = Seconds();
    private readonly NumericUpDown _outroStart = Seconds();
    private readonly NumericUpDown _outroEnd = Seconds();
    private readonly CheckBox _series = new() { Content = "Use for all episodes of this series" };
    private readonly CheckBox _automatic = new() { Content = "Automatically skip configured intros / outros" };
    private readonly TextBlock _error = Message();

    public SkipMarkersDialog(AppServices services, PlayerViewModel player)
        : this(services.PlayerState, player.Current, player.Duration, () => player.Position)
    {
    }

    /// The player is passed as plain values so the dialog can be checked without libVLC.
    public SkipMarkersDialog(PlayerPersonalState state, GlobalSearchResult? media, double duration, Func<double> position)
        : base("Intro / outro markers", 560)
    {
        _state = state;
        _position = position;
        _media = media;

        var current = _media is null ? new PlayerSkipMarkers() : PlayerMarkers.Current(_state, _media);
        var defaults = PlayerMarkers.EditorDefaults(current, duration);
        _intro.IsChecked = current.Intro is not null;
        _outro.IsChecked = current.Outro is not null;
        _introStart.Value = (decimal)defaults.Intro!.Start;
        _introEnd.Value = (decimal)defaults.Intro.End;
        _outroStart.Value = (decimal)defaults.Outro!.Start;
        _outroEnd.Value = (decimal)defaults.Outro.End;
        _automatic.IsChecked = _state.AutomaticSkipping;
        var isEpisode = _media is not null && MediaIdentity.Parse(_media.Entry.Name).Episode is not null;
        _series.IsVisible = isEpisode;
        _series.IsChecked = isEpisode && _media is not null
            && !_state.Markers.ContainsKey(PlayerMarkers.EpisodeKey(_media)) && _state.Markers.ContainsKey(PlayerMarkers.SeriesKey(_media));
        _error.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F85149"));

        void Sync()
        {
            _introStart.IsEnabled = _introEnd.IsEnabled = _intro.IsChecked == true;
            _outroStart.IsEnabled = _outroEnd.IsEnabled = _outro.IsChecked == true;
        }
        _intro.IsCheckedChanged += (_, _) => Sync();
        _outro.IsCheckedChanged += (_, _) => Sync();
        Sync();

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) =>
        {
            if (_media is not null) PlayerMarkers.Clear(_state, _media, _series.IsChecked == true);
            PlaybackPreferencesDialog.Save(_state);
            Close();
        };
        var cancel = Action("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close();
        var save = Action("Save", accent: true, isDefault: true);
        save.Click += (_, _) => SaveMarkers(duration);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        panel.Children.Add(Title2("Intro / outro markers"));
        panel.Children.Add(Muted($"Enter seconds. Episode overrides take priority over series markers. This video lasts {PlayerTime.Format(duration)}.", 13));
        panel.Children.Add(_intro);
        panel.Children.Add(RangeRow(_introStart, _introEnd));
        panel.Children.Add(_outro);
        panel.Children.Add(RangeRow(_outroStart, _outroEnd));
        panel.Children.Add(_series);
        panel.Children.Add(_automatic);
        panel.Children.Add(_error);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        buttons.Children.Add(clear);
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(cancel);
        Grid.SetColumn(save, 3);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static NumericUpDown Seconds() =>
        new() { Minimum = 0, Increment = 1, FormatString = "0.##", HorizontalAlignment = HorizontalAlignment.Stretch, ShowButtonSpinner = false };

    private Grid RangeRow(NumericUpDown start, NumericUpDown end)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(24, 0, 0, 0) };
        var startNow = new Button { Content = "Use current time" };
        var endNow = new Button { Content = "Use current time" };
        startNow.Click += (_, _) => start.Value = (decimal)Math.Round(_position(), 1);
        endNow.Click += (_, _) => end.Value = (decimal)Math.Round(_position(), 1);
        void Add(Control control, int column)
        {
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }
        Add(new TextBlock { Text = "Start", VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(start, 1);
        Add(startNow, 2);
        Add(new TextBlock { Text = "End", VerticalAlignment = VerticalAlignment.Center }, 3);
        Add(end, 4);
        Add(endNow, 5);
        return grid;
    }

    /// Returns null when the ranges fit the duration, or the reason they do not.
    public static string? Validate(PlayerSkipMarkers markers, double duration)
    {
        if (!(duration > 0)) return "Myra does not know this video's duration yet. Start playback, then set the markers.";
        foreach (var (name, range) in new[] { ("Intro", markers.Intro), ("Outro", markers.Outro) })
        {
            if (range is null) continue;
            if (!double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.Start < 0)
                return $"{name} start must be zero or later.";
            if (range.End <= range.Start) return $"{name} must end after it starts.";
            if (range.End > duration) return $"{name} must end within the video's duration ({PlayerTime.Format(duration)}).";
        }
        return null;
    }

    private void SaveMarkers(double duration)
    {
        if (_media is null)
        {
            Show(_error, "Nothing is playing.");
            return;
        }
        var markers = new PlayerSkipMarkers(
            _intro.IsChecked == true ? new PlayerSkipRange((double)(_introStart.Value ?? 0), (double)(_introEnd.Value ?? 0)) : null,
            _outro.IsChecked == true ? new PlayerSkipRange((double)(_outroStart.Value ?? 0), (double)(_outroEnd.Value ?? 0)) : null);
        _state.AutomaticSkipping = _automatic.IsChecked == true;
        if (Validate(markers, duration) is { } problem)
        {
            Show(_error, problem);
            return;
        }
        if (!PlayerMarkers.Set(_state, _media, markers, _series.IsChecked == true, duration))
        {
            Show(_error, "Each enabled range must start at zero or later and end after its start, within this video's duration.");
            return;
        }
        PlaybackPreferencesDialog.Save(_state);
        Close();
    }
}
