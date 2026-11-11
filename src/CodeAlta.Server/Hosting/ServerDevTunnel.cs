using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Hosting;

internal sealed record DevTunnelCommandResult(int ExitCode, string Output);

internal interface IServerDevTunnelCommands
{
    string? Executable { get; }
    Task<DevTunnelCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan patience, Action<string>? output, bool capture, CancellationToken cancellationToken);
}

// Configuration is not a credential. The owner login stays in devtunnel's secure cache, and connect tokens are
// returned only on the explicit local token command or the signed, approved-device API.
internal sealed record ServerDevTunnelConfiguration(string TunnelId, int Port, string Executable, string? Address);

internal sealed class ServerDevTunnel(ServerPaths paths, IServerDevTunnelCommands commands, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly FilePathLocks _writes = new();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private string ConfigurationPath => Path.Combine(paths.DataRoot, "devtunnel.json");
    internal string StopPath => Path.Combine(paths.DataRoot, "devtunnel.stop");
    internal string HostLockPath => Path.Combine(paths.DataRoot, "devtunnel-host.lock");

    internal ServerDevTunnelConfiguration? Read()
    {
        try
        {
            return SafeFile.ReadAllText(ConfigurationPath) is { } text
                ? JsonSerializer.Deserialize(text, ServerDevTunnelJsonContext.Default.ServerDevTunnelConfiguration) : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new InvalidOperationException("The saved Dev Tunnel configuration does not read."); }
    }

    internal async Task<ServerDevTunnelConfiguration> ConfigureAsync(int port, Action<string> signInOutput, CancellationToken cancellationToken)
    {
        if (commands.Executable is not { } executable)
            throw new InvalidOperationException("Install devtunnel on the server's machine first: https://learn.microsoft.com/azure/developer/dev-tunnels/get-started");
        EnsureDirectory();
        // Only one configurator may create a tunnel for this root. This long lock is separate from the short state
        // writes: a running host can still report its address while a device-code login or a CLI call waits.
        using var configuring = await _writes.AcquireAsync(ConfigurationPath + ".configure", cancellationToken).ConfigureAwait(false);
        using var otherConfigurator = await LockFile.AcquireAsync(ConfigurationPath + ".configure.lock", cancellationToken).ConfigureAwait(false);
        var user = await RunAsync(["user", "show"], cancellationToken).ConfigureAwait(false);
        if (user.ExitCode != 0 || !user.Output.Contains("Logged in as", StringComparison.OrdinalIgnoreCase)
            || user.Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
        {
            // No local browser is needed. Output is streamed to the SSH setup, where the person reads the device code.
            var login = await commands.RunAsync(["user", "login", "--github", "-d"], TimeSpan.FromMinutes(10), signInOutput, capture: false, cancellationToken).ConfigureAwait(false);
            if (login.ExitCode != 0) throw LoginRequired();
            user = await RunAsync(["user", "show"], cancellationToken).ConfigureAwait(false);
            if (user.ExitCode != 0 || !user.Output.Contains("Logged in as", StringComparison.OrdinalIgnoreCase)) throw LoginRequired();
        }

        var saved = Read();
        if (saved is null)
        {
            var made = await RunAsync(["create", "--json", "--nologo"], cancellationToken).ConfigureAwait(false);
            var id = made.ExitCode == 0 ? TunnelId(made.Output) : null;
            if (id is null) throw new InvalidOperationException("devtunnel could not create a persistent private tunnel. Check the owner's login and tunnel quota.");
            saved = new(id, port, executable, null);
            saved = Write(saved); // Keep its id even if a later step fails: never replace an existing tunnel silently.
        }

        var added = await RunAsync(["port", "create", saved.TunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--protocol", "http"], cancellationToken).ConfigureAwait(false);
        if (added.ExitCode != 0 && !added.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("devtunnel could not publish the server's HTTP port. Check the saved tunnel and the owner's login.");
        var access = await RunAsync(["access", "list", saved.TunnelId, "--port-number", port.ToString(CultureInfo.InvariantCulture), "--json", "--nologo"], cancellationToken).ConfigureAwait(false);
        if (access.ExitCode != 0 || !AnonymousDenied(access.Output, _time.GetUtcNow()))
            throw new InvalidOperationException("The Dev Tunnel's access could not be proved private. Remove anonymous connect access on the tunnel and its port, then install again.");
        saved = saved with { Port = port, Executable = executable, Address = saved.Port == port ? saved.Address : null };
        return Write(saved);
    }

    internal async Task<FleetTunnelAccessDto?> IssueAsync(CancellationToken cancellationToken)
    {
        if (Read() is not { } saved) return null;
        if (saved.Address is null) throw new InvalidOperationException("The Dev Tunnel host has not reported its HTTPS address yet. Check the tunnel service and the owner's login.");
        var answer = await RunAsync(["token", saved.TunnelId, "--scopes", "connect", "--json", "--nologo"], cancellationToken).ConfigureAwait(false);
        if (answer.ExitCode != 0) throw LoginRequired();
        var token = Token(answer.Output);
        if (token is null) throw new InvalidOperationException("devtunnel did not return a connect token. Sign in again as the tunnel's owner.");
        var expires = _time.GetUtcNow() + TimeSpan.FromHours(24);
        try
        {
            var parts = token.Split('.');
            if (parts.Length == 3)
            {
                using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
                if (json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                {
                    var actual = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (actual < expires) expires = actual;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("The expiration of the Dev Tunnel token could not be read.");
        }

        // A small margin keeps network and clock skew from using the token at its last instant.
        expires -= TimeSpan.FromMinutes(1);
        if (expires <= _time.GetUtcNow()) throw LoginRequired();
        return new(saved.Address, token, expires);
    }

    internal async Task<int> HostAsync(Action<string> reportAddress, CancellationToken cancellationToken)
    {
        var saved = Read() ?? throw new InvalidOperationException("Run altaserver install --dev-tunnel first.");
        using var held = LockFile.Acquire(HostLockPath, TimeSpan.Zero, cancellationToken);
        var access = await RunAsync(["access", "list", saved.TunnelId, "--port-number", saved.Port.ToString(CultureInfo.InvariantCulture), "--json", "--nologo"], cancellationToken).ConfigureAwait(false);
        if (access.ExitCode != 0 || !AnonymousDenied(access.Output, _time.GetUtcNow()))
            throw new InvalidOperationException("The Dev Tunnel's access could not be proved private. Its host was not started; check the owner's login and remove anonymous connect access.");
        if (File.Exists(StopPath)) File.Delete(StopPath);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var host = commands.RunAsync(["host", saved.TunnelId], Timeout.InfiniteTimeSpan, line =>
        {
            foreach (Match match in Regex.Matches(line, @"https://[a-zA-Z0-9.-]+\.devtunnels\.ms(?::\d+)?/?", RegexOptions.CultureInvariant))
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
                    || !address.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
                    || !address.Host.Split('.')[0].EndsWith("-" + saved.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) continue;
                using var localWrite = _writes.Acquire(ConfigurationPath);
                using var write = LockFile.Acquire(ConfigurationPath + ".lock", Patience);
                if (Read() is not { } current || current.TunnelId != saved.TunnelId || current.Port != saved.Port) continue;
                WriteLocked(current with { Address = address.GetLeftPart(UriPartial.Authority) });
                reportAddress(address.GetLeftPart(UriPartial.Authority));
            }
        }, capture: false, stopping.Token);
        var stop = WatchStopAsync(stopping);
        try
        {
            await Task.WhenAny(host, stop).ConfigureAwait(false);
            await stopping.CancelAsync().ConfigureAwait(false);
            try { return (await host.ConfigureAwait(false)).ExitCode; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await stop.ConfigureAwait(false);
        }
    }

    private async Task WatchStopAsync(CancellationTokenSource stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                if (!File.Exists(StopPath)) continue;
                await stop.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private Task<DevTunnelCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => commands.RunAsync(arguments, Patience, null, capture: true, cancellationToken);

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(paths.DataRoot);
        else Directory.CreateDirectory(paths.DataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private ServerDevTunnelConfiguration Write(ServerDevTunnelConfiguration configuration)
    {
        using var local = _writes.Acquire(ConfigurationPath);
        using var other = LockFile.Acquire(ConfigurationPath + ".lock", Patience);
        // A host may have reported its first URL after Configure read its snapshot, during a CLI call.
        if (Read() is { } current && current.TunnelId == configuration.TunnelId && current.Port == configuration.Port)
            configuration = configuration with { Address = current.Address ?? configuration.Address };
        WriteLocked(configuration);
        return configuration;
    }

    private void WriteLocked(ServerDevTunnelConfiguration configuration)
        => SafeFile.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(configuration, ServerDevTunnelJsonContext.Default.ServerDevTunnelConfiguration));

    private static InvalidOperationException LoginRequired()
        => new("Dev Tunnels needs its owner's login on this machine. Run devtunnel user login --github -d as the service account, then try again.");

    internal static string? TunnelId(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement.TryGetProperty("tunnel", out var tunnel) ? tunnel : json.RootElement;
            var id = root.TryGetProperty("tunnelId", out var value) ? value.GetString() : null;
            var cluster = root.TryGetProperty("clusterId", out var region) ? region.GetString() : null;
            if (id is not { Length: > 0 and <= 160 } || !id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) return null;
            return !id.Contains('.', StringComparison.Ordinal) && !string.IsNullOrEmpty(cluster) && cluster.All(char.IsAsciiLetterOrDigit) ? id + "." + cluster : id;
        }
        catch (JsonException) { return null; }
    }

    internal static string? Token(string output)
    {
        var value = output.Trim();
        if (value.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(value);
                value = json.RootElement.TryGetProperty("token", out var token) ? token.GetString() ?? ""
                    : json.RootElement.TryGetProperty("accessToken", out var accessToken) ? accessToken.GetString() ?? ""
                    : json.RootElement.TryGetProperty("accessTokens", out var tokens) && tokens.TryGetProperty("connect", out var connect) ? connect.GetString() ?? "" : "";
            }
            catch (JsonException) { return null; }
        }

        return value is { Length: > 0 and <= 8192 } && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or '/' or '=') ? value : null;
    }

    internal static bool AnonymousDenied(string output, DateTimeOffset now)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            if (!json.RootElement.TryGetProperty("accessControlEntries", out var entries) || entries.ValueKind != JsonValueKind.Array) return false;
            var allowed = false;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "anonymous", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.TryGetProperty("isInverse", out var inverse) && inverse.ValueKind == JsonValueKind.True) continue;
                if (!entry.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array
                    || scopes.EnumerateArray().Any(static scope => scope.ValueKind != JsonValueKind.String)) return false;
                if (!scopes.EnumerateArray().Any(static scope => scope.GetString() == "connect")) continue;
                if (entry.TryGetProperty("expiration", out var expiration) && expiration.ValueKind == JsonValueKind.String
                    && expiration.TryGetDateTimeOffset(out var ends) && ends <= now) continue;
                if (entry.TryGetProperty("isDeny", out var deny) && deny.ValueKind == JsonValueKind.True)
                {
                    if (!entry.TryGetProperty("expiration", out expiration) || expiration.ValueKind == JsonValueKind.Null) return true;
                }
                else allowed = true;
            }

            return !allowed;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }
}

internal sealed class ServerDevTunnelCommands(string? executable = null) : IServerDevTunnelCommands
{
    public string? Executable { get; } = executable ?? Find();

    public async Task<DevTunnelCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan patience, Action<string>? output, bool capture, CancellationToken cancellationToken)
    {
        if (Executable is null) throw new InvalidOperationException("devtunnel is not installed for the service account.");
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("devtunnel did not start.");
        process.StandardInput.Close();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (patience != Timeout.InfiniteTimeSpan) waiting.CancelAfter(patience);
        using var kill = waiting.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var text = new StringBuilder();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(waiting.Token).ConfigureAwait(false) is { } line)
            {
                if (capture) lock (text) { if (text.Length < 65536) text.AppendLine(line); }
                output?.Invoke(line);
            }
        }

        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync(waiting.Token)).ConfigureAwait(false);
        return new(process.ExitCode, text.ToString());
    }

    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".devtunnel", "bin")))
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }
}

[JsonSerializable(typeof(ServerDevTunnelConfiguration))]
internal sealed partial class ServerDevTunnelJsonContext : JsonSerializerContext;
