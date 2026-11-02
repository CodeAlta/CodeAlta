using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What the plugin knows of the Dev Tunnel of the mobile view.</summary>
/// <param name="State">
/// <c>pending</c>, <c>not-installed</c>, <c>not-signed-in</c>, <c>signing-in</c>, <c>serving</c>, <c>failed</c>, <c>stopped</c>, or
/// <c>unused</c> (behind Tailscale).
/// </param>
/// <param name="Url">The address of the view, while the tunnel serves it.</param>
/// <param name="Detail">A line for people.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
internal sealed record MobileDevTunnelStatus(string State, string? Url, string Detail, string? FixUrl = null)
{
    internal static MobileDevTunnelStatus Pending { get; } = new("pending", null, "Opening the Dev Tunnel…");

    internal static MobileDevTunnelStatus Unused { get; } = new("unused", null, "Phones connect through Tailscale.");

    internal static MobileDevTunnelStatus Stopped { get; } = new("stopped", null, "The mobile view is not served.");
}

/// <summary>A command of the Dev Tunnels command line that runs until it is stopped: <c>devtunnel host</c>.</summary>
internal interface IMobileHostedProcess : IAsyncDisposable
{
    /// <summary>Gets the lines it writes, its errors with them; the reader completes when it exits.</summary>
    ChannelReader<string> Lines { get; }

    /// <summary>Gets whether it still runs.</summary>
    bool IsRunning { get; }
}

/// <summary>Runs the Dev Tunnels command line (<c>devtunnel</c>). The tests give their own.</summary>
internal interface IMobileDevTunnelCli
{
    /// <summary>Gets whether the command line is installed: looked for again each time, so that an install is seen without a restart.</summary>
    bool IsInstalled { get; }

    /// <summary>Runs a command and waits for its end, <paramref name="patience"/> at most.</summary>
    Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken);

    /// <summary>Starts a command that runs until it is disposed.</summary>
    IMobileHostedProcess Start(IReadOnlyList<string> args);
}

/// <summary>
/// Publishes the mobile view through a Dev Tunnel of Microsoft: a tunnel of the user's account, made once with anonymous access and kept
/// (so that its address, which phones keep, does not change), with the port of the view, hosted by <c>devtunnel host</c> while the plugin
/// runs. The phone needs nothing but a browser; the pairing token of the QR code is what lets it in, as behind Tailscale.
/// </summary>
internal sealed partial class MobileDevTunnel(IMobileDevTunnelCli cli)
{
    /// <summary>The page that installs the command line.</summary>
    internal const string InstallUrl = "https://learn.microsoft.com/azure/developer/dev-tunnels/get-started";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>What a publication gave: the state, the tunnel to keep in the settings, and the host process that serves it.</summary>
    internal sealed record Published(MobileDevTunnelStatus Status, string? TunnelId, IMobileHostedProcess? Host);

    /// <summary>
    /// Publishes <c>http://127.0.0.1:{port}</c>: checks the command line and the sign-in, makes the tunnel when there is none (or the one
    /// kept is gone), gives it the port, and hosts it until the process of the outcome is disposed.
    /// </summary>
    /// <param name="port">The port of the plugin's server on the loopback address.</param>
    /// <param name="tunnelId">The tunnel the settings keep, or null.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    internal async Task<Published> PublishAsync(int port, string? tunnelId, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return new(new("not-installed", null, "Install the Dev Tunnels command line (devtunnel) on this computer.", InstallUrl), tunnelId, null);
        var user = await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false);
        if (!IsSignedIn(user)) return new(NotSignedIn, tunnelId, null);

        if (tunnelId is null || await cli.RunAsync(["show", tunnelId], Patience, cancellationToken).ConfigureAwait(false) is not { ExitCode: 0, TimedOut: false })
        {
            var created = await cli.RunAsync(["create", "--allow-anonymous"], Patience, cancellationToken).ConfigureAwait(false);
            tunnelId = created is { ExitCode: 0, TimedOut: false } ? TunnelIdIn(created.Output) : null;
            if (tunnelId is null) return new(Failed("devtunnel create", created), null, null);
        }

        var portText = port.ToString(CultureInfo.InvariantCulture);
        var added = await cli.RunAsync(["port", "create", tunnelId, "-p", portText, "--protocol", "http"], Patience, cancellationToken).ConfigureAwait(false);
        // A port the tunnel has already is fine.
        if (added is not { ExitCode: 0, TimedOut: false } && !Regex.IsMatch(added.Output, "already exists|conflict", RegexOptions.IgnoreCase))
            return new(Failed("devtunnel port create", added), tunnelId, null);

        var host = cli.Start(["host", tunnelId]);
        var url = await UrlAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (url.Url is null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            var output = url.Output.Trim();
            if (Regex.IsMatch(output, "not logged in|login required|unauthorized", RegexOptions.IgnoreCase)) return new(NotSignedIn, tunnelId, null);
            return new(new("failed", null, "devtunnel host did not start" + (output.Length > 0 ? ": " + LastLine(output) : ".")), tunnelId, null);
        }

        return new(new("serving", url.Url, $"Dev Tunnels serves {url.Url}"), tunnelId, host);
    }

    /// <summary>Stops the old port of a tunnel when the view moves to another one; what fails is left, the tunnel serves the new one.</summary>
    internal async Task RemovePortAsync(string tunnelId, int port, CancellationToken cancellationToken)
        => await cli.RunAsync(["port", "delete", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture)], Patience, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Signs in to Dev Tunnels: the command line opens the browser of this computer on the page of the account (Microsoft or GitHub) and
    /// ends when the person signed in, or after a few minutes.
    /// </summary>
    /// <returns>Whether the person is signed in now.</returns>
    internal async Task<bool> SignInAsync(bool github, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return false;
        string[] args = github ? ["user", "login", "-g"] : ["user", "login"];
        await cli.RunAsync(args, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return IsSignedIn(await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false));
    }

    private static MobileDevTunnelStatus NotSignedIn { get; } = new("not-signed-in", null, "Sign in to Dev Tunnels with a Microsoft or GitHub account: CodeAlta opens the page of the account.");

    /// <summary>Whether <c>devtunnel user show</c> says a user is signed in.</summary>
    internal static bool IsSignedIn(MobileCliResult result)
        => result is { ExitCode: 0, TimedOut: false } && result.Output.Contains("Logged in as", StringComparison.OrdinalIgnoreCase)
            && !result.Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tunnel <c>devtunnel create</c> made, from its output (<c>Tunnel ID : name.region</c>).</summary>
    internal static string? TunnelIdIn(string output)
        => TunnelIdLine().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The address of the port in what <c>devtunnel host</c> writes (<c>Connect via browser: https://name-port.region.devtunnels.ms</c>).</summary>
    internal static string? UrlIn(string line, int port)
    {
        foreach (Match match in TunnelUrl().Matches(line))
        {
            var url = match.Value.TrimEnd('/', '.', ',');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains("-" + port.ToString(CultureInfo.InvariantCulture) + ".", StringComparison.Ordinal)
                && !uri.Host.Contains("-inspect", StringComparison.OrdinalIgnoreCase))
                return uri.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    // Reads what the host writes until it gives the address of the port and says it is ready, it exits, or the patience ends.
    private static async Task<(string? Url, string Output)> UrlAsync(IMobileHostedProcess host, int port, CancellationToken cancellationToken)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);
        var output = new System.Text.StringBuilder();
        string? url = null;
        try
        {
            await foreach (var line in host.Lines.ReadAllAsync(patience.Token).ConfigureAwait(false))
            {
                output.AppendLine(line);
                url ??= UrlIn(line, port);
                if (url is not null && line.Contains("Ready to accept connections", StringComparison.OrdinalIgnoreCase)) return (url, output.ToString());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The host did not say it was ready in time: it serves when it gave the address and still runs.
        }

        return (url is not null && host.IsRunning ? url : null, output.ToString());
    }

    private static MobileDevTunnelStatus Failed(string command, MobileCliResult result)
        => new("failed", null, result.TimedOut ? $"{command} did not answer." : $"{command} failed: {LastLine(result.Output)}");

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    [GeneratedRegex(@"Tunnel ID\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelIdLine();

    [GeneratedRegex(@"https://[^\s,]+\.devtunnels\.ms[^\s,]*", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelUrl();
}

/// <summary>The Dev Tunnels command line of this computer, found on the path or where its installers put it.</summary>
internal sealed class MobileDevTunnelCli : IMobileDevTunnelCli
{
    /// <inheritdoc />
    public bool IsInstalled => Find() is not null;

    /// <inheritdoc />
    public async Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Find() is not { } path) return new(-1, "devtunnel is not installed.");
        using var process = Process.Start(Info(path, args)) ?? throw new InvalidOperationException("devtunnel did not start.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(patience);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            if (cancellationToken.IsCancellationRequested) throw;
            timedOut = true;
        }

        var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
        return new(timedOut ? -1 : process.ExitCode, text, timedOut);
    }

    /// <inheritdoc />
    public IMobileHostedProcess Start(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var path = Find() ?? throw new InvalidOperationException("devtunnel is not installed.");
        var process = Process.Start(Info(path, args)) ?? throw new InvalidOperationException("devtunnel did not start.");
        return new Hosted(process);
    }

    private static ProcessStartInfo Info(string path, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A folder of the path that is no path.
            }
        }

        // Where winget, Homebrew and the install script put it, for a CodeAlta started before the path was changed.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] usual = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name)]
            : OperatingSystem.IsMacOS()
                ? ["/opt/homebrew/bin/devtunnel", "/usr/local/bin/devtunnel", Path.Combine(home, "bin", "devtunnel")]
                : [Path.Combine(home, "bin", "devtunnel"), "/usr/local/bin/devtunnel", "/usr/bin/devtunnel"];
        return usual.FirstOrDefault(File.Exists);
    }

    // The process of devtunnel host: its lines, out and err, go to one channel; disposing it ends the process.
    private sealed class Hosted : IMobileHostedProcess
    {
        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private int _open = 2;

        internal Hosted(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, line) => Write(line.Data);
            process.ErrorDataReceived += (_, line) => Write(line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public ChannelReader<string> Lines => _lines.Reader;

        public bool IsRunning
        {
            get
            {
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        private void Write(string? line)
        {
            // Null ends a stream: the channel completes when both have ended.
            if (line is null)
            {
                if (Interlocked.Decrement(ref _open) == 0) _lines.Writer.TryComplete();
                return;
            }

            _lines.Writer.TryWrite(line);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or it does not end in time: it is left to the system.
            }

            _process.Dispose();
            _lines.Writer.TryComplete();
        }
    }
}
