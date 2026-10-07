using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Myra.Core;

public sealed class Aria2Exception(string message) : Exception(message)
{
    public static Aria2Exception NotFound() => new(OperatingSystem.IsWindows()
        ? "aria2c.exe was not found. Install it with 'winget install aria2.aria2' or choose its path in Settings."
        : "aria2c was not found. Install aria2 (Arch: 'sudo pacman -S aria2', Debian/Ubuntu: 'sudo apt install aria2') or choose its path in Settings.");

    public static Aria2Exception LaunchFailed(string reason) => new($"aria2c could not start: {reason}");
}

/// JSON-RPC client for the private aria2 instance.
public sealed class Aria2RpcClient(Uri endpoint, string secret, HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        CallAsync("aria2.getVersion", [], cancellationToken);

    public async Task<string> AddAsync(Uri url, string destination, AppSettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        var options = new JsonObject
        {
            ["dir"] = Path.GetDirectoryName(destination),
            ["out"] = Path.GetFileName(destination),
            ["continue"] = "true",
            ["allow-overwrite"] = "false",
            ["auto-file-renaming"] = "false",
            ["max-connection-per-server"] = settings.ConnectionsPerFile.ToString(),
            ["split"] = settings.SplitCount.ToString(),
            ["max-tries"] = settings.RetryCount.ToString(),
            ["max-download-limit"] = settings.SpeedLimit,
        };
        var result = await CallAsync("aria2.addUri", [new JsonArray(url.AbsoluteUri), options], cancellationToken);
        return result.GetValue<string>();
    }

    public async Task<Aria2Status> StatusAsync(string gid, CancellationToken cancellationToken = default)
    {
        var keys = new JsonArray("gid", "status", "totalLength", "completedLength", "downloadSpeed", "errorMessage");
        var raw = (await CallAsync("aria2.tellStatus", [gid, keys], cancellationToken)).AsObject();
        static long Number(JsonNode? node) => long.TryParse(node?.GetValue<string>(), out var value) ? value : 0;
        return new Aria2Status(
            raw["gid"]?.GetValue<string>() ?? gid,
            raw["status"]?.GetValue<string>() ?? "",
            Number(raw["totalLength"]),
            Number(raw["completedLength"]),
            Number(raw["downloadSpeed"]),
            raw["errorMessage"]?.GetValue<string>());
    }

    public Task PauseAsync(string gid) => CallAsync("aria2.forcePause", [gid]);
    public Task ResumeAsync(string gid) => CallAsync("aria2.unpause", [gid]);
    public Task RemoveAsync(string gid) => CallAsync("aria2.forceRemove", [gid]);
    public Task ShutdownAsync() => CallAsync("aria2.shutdown", []);

    private async Task<JsonNode> CallAsync(string method, JsonNode?[] parameters, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = method,
            ["params"] = new JsonArray([JsonValue.Create("token:" + secret), .. parameters]),
        };
        // aria2's HTTP server cannot read chunked bodies, so send a fixed-length one.
        using var content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken)
                   ?? throw new Aria2Exception("aria2 returned an invalid response.");
        if (body["error"] is JsonObject error)
            throw new Aria2Exception("aria2 reported: " + (error["message"]?.GetValue<string>() ?? "unknown error"));
        if (!response.IsSuccessStatusCode || body["result"] is not { } result)
            throw new Aria2Exception("aria2 returned an invalid response.");
        return result;
    }
}

/// Owns the aria2c child process. It listens only on 127.0.0.1 with a random port and secret.
public sealed class Aria2Controller
{
    private Process? _process;
    private Aria2RpcClient? _client;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(AppSettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        var executable = DiscoverExecutable(settings.Aria2PathOverride);
        var port = Random.Shared.Next(31_000, 49_000);
        var secret = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in new[]
                 {
                     "--enable-rpc=true",
                     "--rpc-listen-all=false",
                     $"--rpc-listen-port={port}",
                     $"--rpc-secret={secret}",
                     $"--max-concurrent-downloads={settings.ConcurrentDownloads}",
                     "--continue=true",
                     "--auto-file-renaming=false",
                     "--allow-overwrite=false",
                     "--file-allocation=none",
                     "--summary-interval=0",
                     "--console-log-level=warn",
                     "--quiet=true",
                     // aria2 exits by itself if Myra crashes or is killed.
                     $"--stop-with-process={Environment.ProcessId}",
                 })
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw Aria2Exception.LaunchFailed("no process was created");
        }
        catch (Exception error) when (error is not Aria2Exception)
        {
            throw Aria2Exception.LaunchFailed(error.Message);
        }

        var client = new Aria2RpcClient(new Uri($"http://127.0.0.1:{port}/jsonrpc"), secret);
        _process = process;
        _client = client;
        Exception? last = null;
        for (var attempt = 0; attempt < 50 && !process.HasExited; attempt++)
        {
            try
            {
                await client.PingAsync(cancellationToken);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = error;
                await Task.Delay(100, cancellationToken);
            }
        }
        var exited = process.HasExited ? $"it exited with code {process.ExitCode}" : "the JSON-RPC service did not become ready";
        TerminateImmediately();
        throw Aria2Exception.LaunchFailed(last is null ? exited : $"{exited} ({last.Message})");
    }

    private Aria2RpcClient Client => _client ?? throw Aria2Exception.LaunchFailed("aria2 is not running");

    public Task<string> AddAsync(Uri url, string destination, AppSettingsSnapshot settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return Client.AddAsync(url, destination, settings);
    }

    public Task<Aria2Status> StatusAsync(string gid) => Client.StatusAsync(gid);
    public Task PauseAsync(string gid) => _client?.PauseAsync(gid) ?? Task.CompletedTask;
    public Task ResumeAsync(string gid) => Client.ResumeAsync(gid);
    public Task RemoveAsync(string gid) => _client?.RemoveAsync(gid) ?? Task.CompletedTask;

    public async Task ShutdownAsync()
    {
        try
        {
            if (_client is not null) await _client.ShutdownAsync();
            if (_process is not null) await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception)
        {
            // Fall through to a forced stop.
        }
        TerminateImmediately();
    }

    public void TerminateImmediately()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process?.Dispose();
        _process = null;
        _client = null;
    }

    public static string DiscoverExecutable(string overridePath)
    {
        foreach (var candidate in Candidates(overridePath))
            if (File.Exists(candidate)) return candidate;
        throw Aria2Exception.NotFound();
    }

    private static IEnumerable<string> Candidates(string overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath.Trim().Trim('"');
        var name = OperatingSystem.IsWindows() ? "aria2c.exe" : "aria2c";
        // A copy shipped next to Myra.exe wins, so the app works without any separate install.
        yield return Path.Combine(AppContext.BaseDirectory, "tools", name);
        yield return Path.Combine(AppContext.BaseDirectory, name);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(directory.Trim('"'), name);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", name);
            yield return Path.Combine(profile, "scoop", "shims", name);
            yield return Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? @"C:\ProgramData\chocolatey", "bin", name);
            // winget sometimes installs without creating a link.
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                foreach (var found in Directory.EnumerateFiles(packages, name, SearchOption.AllDirectories))
                    yield return found;
        }
        else
        {
            yield return "/opt/homebrew/bin/aria2c";
            yield return "/usr/local/bin/aria2c";
            yield return "/usr/bin/aria2c";
        }
    }
}
