using System.Diagnostics;
using Avalonia.Controls;
using Myra.Core;

namespace Myra.App.ViewModels;

/// Windows that belong to the main window (Details, Pick, choosers, collections).
/// MainWindow implements it; tests can replace it.
public interface IShellWindows
{
    /// Owner for dialogs. Null before the main window exists.
    Window? OwnerWindow { get; }

    /// Details for one title. Returns the title to play when the user chose Play; the window is closed by then.
    Task<EntertainmentTitle?> ShowDetailsAsync(DetailViewModel details);

    /// Pick Something. Returns the title to play; the window is closed by then.
    Task<EntertainmentTitle?> ShowPickAsync(PickViewModel pick);

    /// Episode or version chooser for a Home title.
    Task<EntertainmentVersion?> ChooseTitleVersionAsync(DetailChooserViewModel chooser);

    /// Files and versions of a downloaded title.
    Task<EntertainmentVersion?> ChooseOfflineVersionAsync(OfflineChooserViewModel chooser);

    Task ShowCollectionsAsync(HomeCollectionsViewModel collections);

    Task<bool> ConfirmAsync(string message);
}

/// Opens web links in the default browser.
public static class ShellLinks
{
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false });
        }
        catch (Exception)
        {
            // No browser available; nothing useful to report.
        }
    }
}

/// Desktop notifications for followed series. Linux uses notify-send (libnotify) when it is installed.
/// Windows has no notification API without an extra package, so the caller shows an in-app toast instead.
public static class ShellNotifications
{
    private static bool? _notifySend;

    /// Returns true when the operating system showed the notification.
    public static bool Show(string title, string message)
    {
        if (!OperatingSystem.IsLinux()) return false;
        _notifySend ??= FindOnPath("notify-send");
        if (_notifySend != true) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("notify-send")
            {
                ArgumentList = { "--app-name=Myra", title, message },
                UseShellExecute = false,
            });
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, name)));
}
