using System.Runtime.InteropServices;

namespace Myra.Core;

/// Windows locations: %APPDATA%\Myra for durable data, %LOCALAPPDATA%\Myra for caches.
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
            var path = !string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(overridden, "Cache")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Myra", "Cache");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string StorePath => Path.Combine(DataDirectory, "Myra.json");
    public static string IndexPath => Path.Combine(DataDirectory, "LibraryIndex.sqlite");
    public static string PlayerStatePath => Path.Combine(DataDirectory, "PlayerPreferences.json");

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
