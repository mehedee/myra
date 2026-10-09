using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Myra.App.Views;

/// Shared shell for the secondary dialogs: centred on the owner, Esc closes, Fluent styling.
public abstract class AppDialog : Window
{
    protected AppDialog(string title, double width, double height = double.NaN, bool resizable = false)
    {
        Title = title;
        Width = width;
        if (double.IsNaN(height)) SizeToContent = SizeToContent.Height;
        else Height = height;
        CanResize = resizable;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close();
        }
    }

    public static TextBlock Heading(string text) =>
        new() { Text = text, Classes = { "heading" }, Margin = new Thickness(0, 8, 0, 0) };

    public static TextBlock Muted(string text, double size = 12) =>
        new() { Text = text, Classes = { "muted" }, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Title2(string text) =>
        new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    public static Button Action(string text, bool accent = false, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel };
        if (accent) button.Classes.Add("accent");
        return button;
    }

    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.AddRange(buttons);
        return row;
    }

    public static TextBlock Message() => new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public static void Show(TextBlock block, string? text)
    {
        block.Text = text;
        block.IsVisible = !string.IsNullOrEmpty(text);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
        }
    }

    /// A text link that opens an https address in the default browser.
    public static Button LinkButton(string text, string url)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#4D9CF5")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => OpenLink(url);
        return button;
    }
}
