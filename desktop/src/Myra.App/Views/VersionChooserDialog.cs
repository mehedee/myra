using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Myra.App.Services;
using Myra.App.ViewModels;
using Myra.Core;

namespace Myra.App.Views;

/// One release in the version chooser.
public sealed record VersionChoice(GlobalSearchResult Media, string Quality, string Detail, string? Badge);

/// Choose a version. Closes with the chosen release, or null. Arrow keys move, Enter chooses, Esc cancels.
public sealed class VersionChooserDialog : AppDialog
{
    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly IReadOnlyList<VersionChoice> _choices;

    public VersionChooserDialog(AppServices services, IReadOnlyList<GlobalSearchResult> versions)
        : base("Choose a version", 640, 460, resizable: true)
    {
        var known = services.Entertainment.Catalogue
            .SelectMany(t => t.Versions)
            .GroupBy(v => v.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var preferred = versions
            .Select(v => known.GetValueOrDefault(v.Entry.Url.AbsoluteUri))
            .Where(v => v?.LastPlayed is not null)
            .MaxBy(v => v!.LastPlayed)?.Id;
        _choices = versions.Select(media =>
        {
            known.TryGetValue(media.Entry.Url.AbsoluteUri, out var version);
            var quality = MediaIdentity.Quality(media.Entry.Name);
            var detail = new List<string> { media.CategoryName };
            if (media.Entry.Size is { } size) detail.Add(FormatBytes(size));
            if (version is { ProgressSeconds: > 0 }) detail.Add("Resume at " + PlayerTime.Format(version.ProgressSeconds));
            var badge = media.Entry.Url.AbsoluteUri == preferred ? "Previously played" : null;
            return new VersionChoice(media, string.IsNullOrEmpty(quality) ? "Quality unknown" : quality, string.Join(" · ", detail), badge);
        }).ToList();

        _list.ItemsSource = _choices;
        _list.ItemTemplate = new FuncDataTemplate<VersionChoice>((choice, _) => Row(choice));
        _list.SelectedIndex = Math.Max(0, _choices.ToList().FindIndex(c => c.Badge is not null));
        _list.DoubleTapped += (_, _) => Choose();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Choose();
        };

        var cancel = Action("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close(null);
        var choose = Action("Play", accent: true, isDefault: true);
        choose.Click += (_, _) => Choose();

        var panel = new DockPanel { Margin = new Thickness(24) };
        var buttons = ButtonRow(cancel, choose);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Title2("Choose a version"));
        top.Children.Add(Muted("Quality and source labels are inferred from filenames. Different releases of the same title are grouped together."));
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_list);
        Content = panel;
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Focus the selected row so the arrow keys and Enter work at once.
            if (_list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is { } row) row.Focus();
            else _list.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static Control Row(VersionChoice? choice)
    {
        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(2, 6) };
        if (choice is null) return stack;
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = choice.Media.Entry.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        if (choice.Badge is { } badge)
            header.Children.Add(new TextBlock { Text = badge, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#4D9CF5")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = choice.Quality });
        stack.Children.Add(new TextBlock { Text = choice.Detail, FontSize = 12, Opacity = 0.65 });
        return stack;
    }

    private void Choose()
    {
        if (_list.SelectedItem is VersionChoice choice) Close(choice.Media);
    }
}
