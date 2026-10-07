using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Myra.Core;

/// Replaces the macOS Keychain. Secrets never enter Myra.json, the index or personal exports.
/// Read returns "" when a secret is absent. Saving an empty (whitespace) value deletes it.
public interface ISecretStore
{
    string Read(string name);
    void Save(string name, string value);
}

/// Stable secret names. They match the macOS Keychain service/account pairs in meaning.
public static class SecretNames
{
    public const string TmdbReadToken = "tmdb.read-token";
    public const string OmdbApiKey = "omdb.api-key";
    public const string OpenSubtitlesApiKey = "opensubtitles.api-key";
    public const string OpenSubtitlesUsername = "opensubtitles.username";
    public const string OpenSubtitlesPassword = "opensubtitles.password";
}

public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

public static partial class SecretStore
{
    /// DPAPI (CurrentUser) on Windows; a 0600 file store elsewhere.
    public static ISecretStore CreateDefault(string? directory = null)
    {
        var path = directory ?? AppPaths.SecretsDirectory;
        return OperatingSystem.IsWindows() ? new DpapiSecretStore(path) : new FileSecretStore(path);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$")]
    private static partial Regex NamePattern();

    /// Names become file names, so only a small safe alphabet is accepted.
    internal static string FileName(string name, string extension)
    {
        if (!NamePattern().IsMatch(name) || name.Contains("..")) throw new ArgumentException("Invalid secret name.", nameof(name));
        return name + extension;
    }

    public static SubtitleCredentials ReadSubtitleCredentials(this ISecretStore store) => new(
        store.Read(SecretNames.OpenSubtitlesApiKey),
        store.Read(SecretNames.OpenSubtitlesUsername),
        store.Read(SecretNames.OpenSubtitlesPassword));

    /// API key and username are trimmed; the password is stored exactly as typed.
    public static void SaveSubtitleCredentials(this ISecretStore store, SubtitleCredentials credentials)
    {
        store.Save(SecretNames.OpenSubtitlesApiKey, credentials.ApiKey.Trim());
        store.Save(SecretNames.OpenSubtitlesUsername, credentials.Username.Trim());
        store.Save(SecretNames.OpenSubtitlesPassword, credentials.Password);
    }
}

/// Linux fallback: one file per secret, directory 0700 and files 0600, written atomically.
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly Lock _lock = new();

    public FileSecretStore(string directory) => _directory = directory;

    public string Read(string name)
    {
        var path = Path.Combine(_directory, SecretStore.FileName(name, ".secret"));
        lock (_lock)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SecretStoreException("Myra could not read a saved credential.", error);
            }
        }
    }

    public void Save(string name, string value)
    {
        var path = Path.Combine(_directory, SecretStore.FileName(name, ".secret"));
        lock (_lock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    // File.Delete throws when the folder itself is missing; nothing to delete then.
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                CreatePrivateDirectory(_directory);
                var temporary = path + ".tmp";
                if (File.Exists(temporary)) File.Delete(temporary);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    var bytes = Encoding.UTF8.GetBytes(value);
                    stream.Write(bytes);
                }
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SecretStoreException("Myra could not save the credential.", error);
            }
        }
    }

    internal static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// Windows: DPAPI CurrentUser scope. Only the same Windows account can decrypt the files.
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = "Myra.secret.v1"u8.ToArray();
    private readonly string _directory;
    private readonly Lock _lock = new();

    public DpapiSecretStore(string directory) => _directory = directory;

    public string Read(string name)
    {
        var path = Path.Combine(_directory, SecretStore.FileName(name, ".dpapi"));
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path)) return "";
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new SecretStoreException("Myra could not read a saved credential.", error);
            }
        }
    }

    public void Save(string name, string value)
    {
        var path = Path.Combine(_directory, SecretStore.FileName(name, ".dpapi"));
        lock (_lock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    // File.Delete throws when the folder itself is missing; nothing to delete then.
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(_directory);
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, cipher);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new SecretStoreException("Myra could not save the credential.", error);
            }
        }
    }
}

/// Process-local store for tests and previews.
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string Read(string name)
    {
        lock (_values) return _values.GetValueOrDefault(name, "");
    }

    public void Save(string name, string value)
    {
        lock (_values)
        {
            if (string.IsNullOrWhiteSpace(value)) _values.Remove(name);
            else _values[name] = value;
        }
    }
}
