using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>What the person asks: CodeAlta Server on this computer.</summary>
internal sealed record LocalServerRequest
{
    /// <summary>Gets the port the server listens on, on the loopback (2586 by default).</summary>
    public int Port { get; init; } = 2586;

    /// <summary>Gets the data folder of the server; null for <c>~/.altaserver</c>.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Gets the folder the tool is in or goes in; null for the global tools (<c>~/.dotnet/tools</c>) or the PATH.</summary>
    public string? ToolPath { get; init; }

    /// <summary>Gets a folder with the packages of CodeAlta Server; null for nuget.org.</summary>
    public string? PackageFolder { get; init; }

    /// <summary>Gets the name of the owner.</summary>
    public required string OwnerName { get; init; }

    /// <summary>Gets the name of the organization; null for the server's default.</summary>
    public string? OrganizationName { get; init; }
}

/// <summary>The steps of a server set up on this computer.</summary>
internal static class LocalServerSteps
{
    public const string Find = "find";
    public const string Install = "install";
    public const string Start = "start";
    public const string Claim = "claim";
}

/// <summary>How the desktop runs the commands of a setup on this computer; a test gives its own.</summary>
internal interface ILocalProcesses
{
    /// <summary>Runs a command and waits for it.</summary>
    Task<SshResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>Starts a command that goes on by itself, beyond this process.</summary>
    void StartDetached(string executable, IReadOnlyList<string> arguments);

    /// <summary>Gets whether a file exists.</summary>
    bool Exists(string path);

    /// <summary>Reads the lines of a small text file.</summary>
    IReadOnlyList<string> ReadLines(string path);
}

/// <summary>The processes of this computer.</summary>
internal sealed class SystemProcesses : ILocalProcesses
{
    /// <inheritdoc />
    public async Task<SshResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new SshResult(127, "", $"{executable} could not start: {exception.Message}");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw;
            }

            return new SshResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public void StartDetached(string executable, IReadOnlyList<string> arguments)
    {
        // No redirection: the server holds no handle of this process, and goes on when the desktop closes.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
    }

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public IReadOnlyList<string> ReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// Sets up CodeAlta Server on this computer in one go: finds <c>altaserver</c> (or installs it, a .NET tool), starts it
/// on the loopback, reads its one-time setup code here (it is given to this machine only), and claims it with that code
/// as its owner — what the person would otherwise copy from one window to another.
/// </summary>
internal sealed class LocalServerSetup(ILocalProcesses processes, string desktopVersion)
{
    /// <summary>The package of the tool.</summary>
    internal const string PackageId = "CodeAlta.Server";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs the setup; <paramref name="claim"/> makes this computer the owner with the address and the code, and returns the id of its link.</summary>
    /// <exception cref="SshSetupException">A step failed; its code says why.</exception>
    public async Task<string> RunAsync(LocalServerRequest request, Func<Uri, string, CancellationToken, Task<string>> claim, Action<SshSetupProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(progress);
        var exe = OperatingSystem.IsWindows() ? "altaserver.exe" : "altaserver";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tools = request.ToolPath ?? Path.Combine(home, ".dotnet", "tools");

        progress(new(LocalServerSteps.Find, "running"));
        var server = Resolve(tools, exe);
        var installed = server is not null;
        progress(new(LocalServerSteps.Find, "done", server));

        progress(new(LocalServerSteps.Install, "running"));
        if (!installed)
        {
            var version = request.PackageFolder is { Length: > 0 } folder ? PackageVersion(folder) : SshNodeInstaller.ReleaseVersion(desktopVersion);
            List<string> arguments = ["tool", "install", PackageId, "--version", version];
            arguments.AddRange(request.ToolPath is null ? ["-g"] : ["--tool-path", tools]);
            if (request.PackageFolder is { Length: > 0 } source) arguments.AddRange(["--add-source", source]);
            var install = await processes.RunAsync("dotnet", arguments, cancellationToken).ConfigureAwait(false);
            if (install.ExitCode == 127) throw new SshSetupException(LocalServerSteps.Install, "dotnet_missing", install.Error);
            server = Resolve(tools, exe);
            if (!install.Succeeded || server is null)
                throw new SshSetupException(LocalServerSteps.Install, "tool_install_failed", FirstLines(install.Error + install.Output));
            progress(new(LocalServerSteps.Install, "done", $"CodeAlta Server {version} · {tools}"));
        }
        else
        {
            progress(new(LocalServerSteps.Install, "done", "Installed already"));
        }

        if (server is null) throw new SshSetupException(LocalServerSteps.Install, "tool_install_failed", tools);
        progress(new(LocalServerSteps.Start, "running"));
        List<string> root = request.DataRoot is { Length: > 0 } data ? ["--data-root", data] : [];
        var status = await processes.RunAsync(server, ["status", .. root], cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            processes.StartDetached(server, ["run", "--port", request.Port.ToString(CultureInfo.InvariantCulture), .. root]);
            var deadline = DateTime.UtcNow + StartTimeout;
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                status = await processes.RunAsync(server, ["status", .. root], cancellationToken).ConfigureAwait(false);
                if (status.Succeeded && status.Output.Contains("Health:", StringComparison.Ordinal)) break;
                if (DateTime.UtcNow > deadline) throw new SshSetupException(LocalServerSteps.Start, "server_start_failed", FirstLines(status.Output + status.Error));
            }
        }

        var address = ListenAddress(status.Output) ?? new Uri($"http://127.0.0.1:{request.Port.ToString(CultureInfo.InvariantCulture)}");
        progress(new(LocalServerSteps.Start, "done", address.ToString().TrimEnd('/')));

        progress(new(LocalServerSteps.Claim, "running"));
        var code = await processes.RunAsync(server, ["setup-code", .. root], cancellationToken).ConfigureAwait(false);
        if (!code.Succeeded)
            throw new SshSetupException(LocalServerSteps.Claim, code.Error.Contains("already has an owner", StringComparison.OrdinalIgnoreCase) ? "server_owned" : "setup_code_failed",
                FirstLines(code.Error + code.Output));
        var setupCode = code.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var linkId = await claim(address, setupCode, cancellationToken).ConfigureAwait(false);
        progress(new(LocalServerSteps.Claim, "done", request.OrganizationName));
        return linkId;
    }

    // The executable of the tool: its launcher, or on Windows the program a .cmd launcher of a package per runtime starts
    // (started itself, so that no cmd.exe stands between the server and its stop).
    private string? Resolve(string tools, string exe)
    {
        var direct = Path.Combine(tools, exe);
        if (processes.Exists(direct)) return direct;
        if (!OperatingSystem.IsWindows()) return null;
        var shim = Path.Combine(tools, "altaserver.cmd");
        if (!processes.Exists(shim)) return null;
        foreach (var line in processes.ReadLines(shim))
        {
            var start = line.IndexOf("\"%~dp0", StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            var end = line.IndexOf('"', start + 1);
            if (end < 0) continue;
            var target = Path.GetFullPath(Path.Combine(tools, line[(start + "\"%~dp0".Length)..end]));
            if (target.StartsWith(Path.GetFullPath(tools), StringComparison.OrdinalIgnoreCase) && processes.Exists(target)) return target;
        }

        return shim;
    }

    /// <summary>The first loopback address <c>altaserver status</c> says it listens on.</summary>
    internal static Uri? ListenAddress(string status)
    {
        foreach (var line in status.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("Listens on ", StringComparison.Ordinal)) continue;
            foreach (var url in line["Listens on ".Length..].Split(',', StringSplitOptions.TrimEntries))
                if (Uri.TryCreate(url, UriKind.Absolute, out var address)) return address;
        }

        return null;
    }

    /// <summary>The newest version of the tool package in a folder.</summary>
    internal static string PackageVersion(string folder)
    {
        if (!Directory.Exists(folder)) throw new SshSetupException(LocalServerSteps.Install, "packages_missing", folder);
        var versions = Directory.GetFiles(folder, PackageId + ".*.nupkg").Select(static path => Path.GetFileNameWithoutExtension(path)[(PackageId.Length + 1)..])
            .Where(static version => version.Length > 0 && char.IsDigit(version[0])).ToArray();
        return versions.OrderByDescending(static version => System.Version.TryParse(version.Split('-', '+')[0], out var value) ? value : new Version(0, 0)).ThenByDescending(static v => v, StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new SshSetupException(LocalServerSteps.Install, "packages_missing", folder);
    }

    private static string FirstLines(string text)
        => string.Join('\n', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(5));
}
