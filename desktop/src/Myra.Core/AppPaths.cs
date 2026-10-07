using System.Runtime.InteropServices;

namespace Myra.Core;

/// Durable data: %APPDATA%\Myra on Windows, ~/.config/Myra on Linux.
/// Caches: %LOCALAPPDATA%\Myra\Cache on Windows, ~/.cache/Myra on Linux.
/// MYRA_DATA_DIR overrides both, which keeps tests away from the personal store.
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("MYRA_DATA_DIR");
            var path = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Myra");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string CacheDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("MYRA_DATA_DIR");
            string path;
            if (!string.IsNullOrWhiteSpace(overridden))
                path = Path.Combine(overridden, "Cache");
            else if (OperatingSystem.IsWindows())
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Myra", "Cache");
            else
            {
                // XDG base directory rules: $XDG_CACHE_HOME, else ~/.cache.
                var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                var baseDirectory = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
                path = Path.Combine(baseDirectory, "Myra");
            }
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string StorePath => Path.Combine(DataDirectory, "Myra.json");
    public static string IndexPath => Path.Combine(DataDirectory, "LibraryIndex.sqlite");
    public static string PlayerStatePath => Path.Combine(DataDirectory, "PlayerPreferences.json");

    /// Watchlist, watched, collections, follows, history and match corrections (macOS-compatible JSON).
    public static string PersonalLibraryPath => Path.Combine(DataDirectory, "EntertainmentPersonal.json");

    /// Inherited Daily / Weekly / Manual-only folder refresh schedules.
    public static string IndexPoliciesPath => Path.Combine(DataDirectory, "IndexPolicies.json");

    /// Linux secret store fallback (0600). Windows uses DPAPI-protected files in the same folder.
    public static string SecretsDirectory => Path.Combine(DataDirectory, "Secrets");

    /// Downloaded OpenSubtitles files; app-generated names only, 30 days / 100 MB.
    public static string SubtitleCacheDirectory => Path.Combine(CacheDirectory, "Subtitles");

    public static string DefaultDownloadDirectory() => Path.Combine(DownloadsFolder(), "Myra");

    private static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            // FOLDERID_Downloads follows the user's redirected Downloads location.
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
