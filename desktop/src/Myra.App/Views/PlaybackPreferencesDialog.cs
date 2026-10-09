using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// Audio and subtitle language, speed, autoplay and automatic skipping.
public sealed class PlaybackPreferencesDialog : AppDialog
{
    private static readonly float[] Speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    public PlaybackPreferencesDialog(AppServices services, PlayerViewModel player)
        : this(services.PlayerState, player.Speed, player.SetSpeed, autoplay => player.Autoplay = autoplay)
    {
    }

    /// The player is passed as three small hooks so the dialog can be checked without libVLC.
    public PlaybackPreferencesDialog(PlayerPersonalState state, float currentSpeed, Action<float> setSpeed, Action<bool> setAutoplay)
        : base("Playback preferences", 520)
    {
        (string Code, string Label)[] audio = [("default", "Source default"), .. PlayerLanguagePreference.Choices.Where(c => c.Code != "default")];
        (string Code, string Label)[] subtitles = [("off", "Disabled"), .. audio];

        var audioBox = Combo(audio.Select(c => c.Label), Math.Max(0, Array.FindIndex(audio, c => c.Code == state.AudioLanguage)));
        audioBox.SelectionChanged += (_, _) => state.AudioLanguage = audio[Math.Max(0, audioBox.SelectedIndex)].Code;
        var subtitleBox = Combo(subtitles.Select(c => c.Label), Math.Max(0, Array.FindIndex(subtitles, c => c.Code == state.SubtitleLanguage)));
        subtitleBox.SelectionChanged += (_, _) => state.SubtitleLanguage = subtitles[Math.Max(0, subtitleBox.SelectedIndex)].Code;

        var speedBox = Combo(Speeds.Select(s => s.ToString("0.##") + "×"), ClosestSpeed(currentSpeed));
        speedBox.SelectionChanged += (_, _) =>
        {
            var speed = Speeds[Math.Max(0, speedBox.SelectedIndex)];
            setSpeed(speed);
            state.Speed = speed;
        };

        var autoplay = new CheckBox { Content = "Automatically play the next video", IsChecked = state.Autoplay };
        autoplay.IsCheckedChanged += (_, _) =>
        {
            state.Autoplay = autoplay.IsChecked == true;
            setAutoplay(state.Autoplay);
        };
        var skipping = new CheckBox { Content = "Automatically skip configured intros / outros", IsChecked = state.AutomaticSkipping };
        skipping.IsCheckedChanged += (_, _) => state.AutomaticSkipping = skipping.IsChecked == true;

        var done = Action("Done", accent: true, isDefault: true, isCancel: true);
        done.Click += (_, _) => Close();
        Closed += (_, _) => Save(state);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(Title2("Playback preferences"));
        panel.Children.Add(Muted("Apply to future playback. Manual track choices remain active for the current video.", 13));
        panel.Children.Add(Row("Audio language", audioBox));
        panel.Children.Add(Row("Subtitle language", subtitleBox));
        panel.Children.Add(Row("Playback speed", speedBox));
        panel.Children.Add(autoplay);
        panel.Children.Add(skipping);
        panel.Children.Add(ButtonRow(done));
        Content = panel;
    }

    private static int ClosestSpeed(float speed) =>
        Array.IndexOf(Speeds, Speeds.MinBy(s => Math.Abs(s - speed)));

    private static ComboBox Combo(IEnumerable<string> items, int selected) =>
        new() { ItemsSource = items.ToList(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Grid Row(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    internal static void Save(PlayerPersonalState state)
    {
        try
        {
            state.Save();
        }
        catch (IOException)
        {
        }
    }
}
