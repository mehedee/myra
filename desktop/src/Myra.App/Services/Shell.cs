using System.Diagnostics;
using Microsoft.Win32;

namespace Myra.App.Services;

/// Operating-system integration: Explorer, external VLC, folders.
public static class Shell
{
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
                else
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { NearestExisting(path) } });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { NearestExisting(path) }, UseShellExecute = false });
            }
        }
        catch (Exception)
        {
            // Nothing useful to report when the file manager cannot open.
        }
    }

    private static string NearestExisting(string path)
    {
        var current = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current)) current = Path.GetDirectoryName(current);
        return string.IsNullOrEmpty(current) ? Path.GetPathRoot(path) ?? "." : current;
    }

    public static string? FindVlc(string overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath.Trim('"'))) return overridePath.Trim('"');
        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
                if (key?.GetValue(null) is string registered && File.Exists(registered)) return registered;
                if (key?.GetValue("InstallDir") is string directory && File.Exists(Path.Combine(directory, "vlc.exe")))
                    return Path.Combine(directory, "vlc.exe");
            }
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var candidate = Path.Combine(Environment.GetFolderPath(folder), "VideoLAN", "VLC", "vlc.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, "vlc");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool OpenInVlc(string target, string overridePath)
    {
        var vlc = FindVlc(overridePath);
        if (vlc is null) return false;
        Process.Start(new ProcessStartInfo(vlc) { ArgumentList = { target }, UseShellExecute = false });
        return true;
    }
}
