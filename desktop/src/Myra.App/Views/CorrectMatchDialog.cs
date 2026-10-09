using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Myra.App.Services;
using Myra.Core;

namespace Myra.App.Views;

/// Correct Match / Grouping for one title. Closes with true when the store accepted a correction.
public sealed class CorrectMatchDialog : AppDialog
{
    private readonly EntertainmentStore _store;
    private readonly EntertainmentTitle _title;
    private readonly ComboBox _file = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Watermark = "Movie or series title" };
    private readonly TextBox _year = new() { Watermark = "Year (optional)" };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "Movie", "Series" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _providerId = new() { Watermark = "TMDB ID (optional)" };
    private readonly CheckBox _all = new() { Content = "Apply to every version in this title", IsChecked = true };
    private readonly TextBlock _error = Message();
    private readonly Button _save = Action("Save correction", accent: true, isDefault: true);
    private bool _saving;

    public CorrectMatchDialog(EntertainmentStore store, EntertainmentTitle title) : base("Correct Match", 560)
    {
        _store = store;
        _title = title;
        _error.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0883E"));
        _file.ItemsSource = title.Versions.Select(v => v.Media.Entry.Name).ToList();
        _file.SelectedIndex = title.Versions.Count > 0 ? 0 : -1;
        _name.Text = title.Name;
        _year.Text = title.Year ?? "";
        _kind.SelectedIndex = title.Kind == EntertainmentKind.Series ? 1 : 0;
        _providerId.Text = title.Metadata is { ProviderId: > 0 } metadata ? metadata.ProviderId.ToString() : "";
        _all.IsEnabled = title.Versions.Count > 1;
        if (title.Versions.Count <= 1) _all.IsChecked = true;

        var cancel = Action("Cancel", isCancel: true);
        cancel.Click += (_, _) => Close(false);
        _save.Click += async (_, _) => await SaveAsync();
        _name.TextChanged += (_, _) => _save.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text) && !_saving;
        _save.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        panel.Children.Add(Title2("Correct Match"));
        panel.Children.Add(Muted("A shared folder is not proof of identity. Correct a file's title and year to merge alternate versions, or give an incorrectly grouped file its own identity. TMDB ID is optional."));
        panel.Children.Add(Labeled("File", _file));
        panel.Children.Add(Labeled("Title", _name));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
        row.Children.Add(_year);
        Grid.SetColumn(_kind, 1);
        row.Children.Add(_kind);
        Grid.SetColumn(_providerId, 2);
        row.Children.Add(_providerId);
        panel.Children.Add(row);
        panel.Children.Add(_all);
        panel.Children.Add(_error);
        panel.Children.Add(ButtonRow(cancel, _save));
        Content = panel;
        Opened += (_, _) => _name.Focus();
    }

    private static StackPanel Labeled(string label, Control field)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 });
        stack.Children.Add(field);
        return stack;
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        int? providerId = null;
        var idText = (_providerId.Text ?? "").Trim();
        if (idText.Length > 0)
        {
            if (!int.TryParse(idText, out var parsed) || parsed <= 0)
            {
                Show(_error, "Enter a title, an optional four-digit year, and a positive TMDB ID.");
                return;
            }
            providerId = parsed;
        }
        IReadOnlyList<string> ids = _all.IsChecked == true || _file.SelectedIndex < 0
            ? _title.Versions.Select(v => v.Id).ToList()
            : [_title.Versions[_file.SelectedIndex].Id];
        _saving = true;
        _save.IsEnabled = false;
        Show(_error, null);
        _store.ClearError();
        try
        {
            var kind = _kind.SelectedIndex == 1 ? EntertainmentKind.Series : EntertainmentKind.Movie;
            var accepted = await _store.CorrectMatchAsync(ids, _name.Text ?? "", _year.Text, kind, providerId);
            if (accepted) Close(true);
            else Show(_error, _store.ErrorMessage ?? "The correction was not saved.");
        }
        finally
        {
            _saving = false;
            _save.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text);
        }
    }
}
