using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Myra.Core;

namespace Myra.App.Services;

/// Runtime window icon for the chosen family and variant. It sets Window.Icon on every open window
/// (title bar, taskbar, Alt+Tab) and on windows that open later. It never changes the executable.
/// Call <see cref="Apply"/> once at startup and again after the choice changes.
public static class MyraAppearance
{
    private static readonly Dictionary<string, Bitmap> Bitmaps = [];
    private static bool _hooked;
    private static WindowIcon? _icon;
    private static AppSettings? _settings;
    private static readonly List<WeakReference<Window>> Windows = [];

    /// Name of the image in use, for example "cinema-dark". Empty before the first Apply.
    public static string CurrentName { get; private set; } = "";

    /// The icon given to windows now.
    public static WindowIcon? CurrentIcon => _icon;

    /// True when the app currently shows the Dark theme.
    public static bool SystemIsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// The 256 px image for a name such as "orbit-glass", or null when the asset is missing.
    public static Bitmap? Image(string name)
    {
        lock (Bitmaps)
        {
            if (Bitmaps.TryGetValue(name, out var cached)) return cached;
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Myra/Assets/Icons/{name}.png"));
                return Bitmaps[name] = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException)
            {
                return null;
            }
        }
    }

    public static Bitmap? Image(MyraIconFamily family, MyraIconVariant variant, bool systemIsDark) =>
        Image(MyraIcons.AssetName(family, variant, systemIsDark));

    /// Applies the saved choice to all windows and keeps following the theme for Automatic.
    public static void Apply(AppSettings settings)
    {
        var family = MyraIcons.ParseFamily(settings.IconFamily);
        var variant = MyraIcons.ParseVariant(settings.IconVariant);
        _settings = settings;
        Hook();
        var name = MyraIcons.AssetName(family, variant, SystemIsDark);
        if (name != CurrentName || _icon is null)
        {
            if (Image(name) is { } bitmap)
            {
                _icon = new WindowIcon(bitmap);
                CurrentName = name;
            }
            else
            {
                return;
            }
        }
        foreach (var window in OpenWindows()) window.Icon = _icon;
    }

    /// Windows of the desktop lifetime plus windows seen loading (covers hosts without a lifetime).
    private static IEnumerable<Window> OpenWindows()
    {
        var all = new HashSet<Window>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows) all.Add(window);
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out var target) || !target.IsVisible);
            foreach (var reference in Windows)
                if (reference.TryGetTarget(out var window)) all.Add(window);
        }
        return all;
    }

    private static void Hook()
    {
        if (_hooked || Application.Current is not { } application) return;
        _hooked = true;
        // Windows opened later (dialogs, player, Settings) get the icon when they load.
        Control.LoadedEvent.AddClassHandler<Window>((window, _) =>
        {
            lock (Windows) Windows.Add(new WeakReference<Window>(window));
            if (_icon is not null) window.Icon = _icon;
        });
        application.ActualThemeVariantChanged += (_, _) =>
        {
            if (_settings is not null) Apply(_settings);
        };
    }
}
