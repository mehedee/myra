using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Myra.App.Views;

/// Version and attribution text shared by About and Settings.
public static class AboutAttribution
{
    /// macOS release this port matches. Used when the assembly carries no explicit version.
    public const string ParityVersion = "2.0.5";

    public const string TmdbNotice = "This product uses the TMDB API but is not endorsed or certified by TMDB.";
    public const string TmdbDetail = "Metadata and images for personal, noncommercial use.";
    public const string OmdbNotice = "Online movie information is provided by OMDb under CC BY-NC 4.0, for personal noncommercial use.";
    public const string VlcNotice = "Embedded playback uses libVLC from the VideoLAN project, licensed under the LGPL 2.1 or later. Source: https://code.videolan.org/videolan/vlc";
    public const string Aria2Notice = "Downloads are handled by aria2, licensed under the GPL 2 or later. Myra starts it as a separate program. Source: https://github.com/aria2/aria2";
    public const string SubtitleNotice = "Online subtitles come from OpenSubtitles.com. Only title and episode information is sent, never your media URL.";

    public static string Version
    {
        get
        {
            var informational = typeof(AboutAttribution).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational?.Split('+')[0];
            return string.IsNullOrWhiteSpace(version) || version is "1.0.0" or "1.0.0.0" ? ParityVersion : version;
        }
    }

    public static Bitmap? TmdbLogo()
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri("avares://Myra/Assets/TMDB.png")));
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// TMDB logo, the required notice and a link to The Movie Database.
    public static Control TmdbBlock()
    {
        var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = TmdbNotice, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        text.Children.Add(AppDialog.LinkButton("The Movie Database", "https://www.themoviedb.org"));
        text.Children.Add(AppDialog.Muted(TmdbDetail));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        if (TmdbLogo() is { } logo)
            row.Children.Add(new Image { Source = logo, Width = 120, Height = 51, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    public static IEnumerable<Control> Notices()
    {
        foreach (var notice in new[] { OmdbNotice, VlcNotice, Aria2Notice, SubtitleNotice })
            yield return AppDialog.Muted(notice);
    }
}

public sealed class AboutDialog : AppDialog
{
    public AboutDialog() : base("About Myra", 560)
    {
        var close = Action("Close", accent: true, isDefault: true, isCancel: true);
        close.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        try
        {
            header.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Myra/Assets/Myra.png"))), Width = 56, Height = 56 });
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or ArgumentException)
        {
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Title2("Myra"));
        titles.Children.Add(Muted("Version " + AboutAttribution.Version));
        header.Children.Add(titles);
        panel.Children.Add(header);
        panel.Children.Add(Muted("A personal downloader and player for h5ai, Apache and nginx directory listings."));

        panel.Children.Add(Heading("TMDB"));
        panel.Children.Add(AboutAttribution.TmdbBlock());
        panel.Children.Add(Heading("Open source and data notices"));
        foreach (var notice in AboutAttribution.Notices()) panel.Children.Add(notice);
        panel.Children.Add(new Border { Height = 4 });
        panel.Children.Add(ButtonRow(close));
        Content = panel;
    }
}
