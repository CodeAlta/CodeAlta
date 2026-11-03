using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodeAlta.Plugin.Mobile;

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

    /// <summary>
    /// Runs a command in a console window of its own, for what it shows to the person (the account picker of Windows needs a window), and
    /// waits for its end, <paramref name="patience"/> at most.
    /// </summary>
    /// <returns>Its exit code; -1 when it did not end in time.</returns>
    Task<int> RunInWindowAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the mobile view through a Dev Tunnel of Microsoft (states: <c>not-installed</c>, <c>not-signed-in</c>, <c>serving</c>,
/// <c>failed</c>): a tunnel of the user's account, made once with anonymous access and kept
/// (so that its address, which phones keep, does not change), with the port of the view, hosted by <c>devtunnel host</c> while the plugin
/// runs. The phone needs nothing but a browser; the pairing token of the QR code is what lets it in, as behind Tailscale.
/// </summary>
internal sealed partial class MobileDevTunnel(IMobileDevTunnelCli cli, Action<string>? log = null)
{
    /// <summary>The page that installs the command line.</summary>
    internal const string InstallUrl = "https://learn.microsoft.com/azure/developer/dev-tunnels/get-started";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What a publication gave: the state, the tunnel to keep in the settings, the host process that serves it, and whether the tunnel
    /// was given the port (to take it off when the view moves to another one, even when the host did not start).
    /// </summary>
    internal sealed record Published(MobileProviderStatus Status, string? TunnelId, IMobileHostedProcess? Host, bool HasPort = false);

    /// <summary>
    /// Publishes <c>http://127.0.0.1:{port}</c>: checks the command line and the sign-in, makes the tunnel when there is none, gives it the
    /// port (a tunnel kept that is gone, or that this account cannot change, refuses it: a new one is made), and hosts it until the process
    /// of the outcome is disposed.
    /// </summary>
    /// <param name="port">The port of the plugin's server on the loopback address.</param>
    /// <param name="tunnelId">The tunnel the settings keep, or null.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    internal async Task<Published> PublishAsync(int port, string? tunnelId, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return new(new("not-installed", null, "Install the Dev Tunnels command line (devtunnel) on this computer.", InstallUrl), tunnelId, null);
        var user = await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false);
        if (!IsSignedIn(user)) return new(NotSignedIn, tunnelId, null);

        var made = false;
        if (tunnelId is null)
        {
            (tunnelId, var failure) = await CreateAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null) return new(failure, null, null);
            made = true;
        }

        var added = await AddPortAsync(tunnelId!, port, cancellationToken).ConfigureAwait(false);
        if (added is not null && !made)
        {
            // The tunnel kept may be gone, or one this account cannot change (made with another account): a new one is made, once.
            log?.Invoke($"Mobile: the Dev Tunnel {tunnelId} refused the port; a new tunnel is made.");
            (tunnelId, var failure) = await CreateAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null) return new(failure, null, null);
            added = await AddPortAsync(tunnelId!, port, cancellationToken).ConfigureAwait(false);
        }

        if (added is not null) return new(added, tunnelId, null);

        var host = cli.Start(["host", tunnelId!]);
        var url = await UrlAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (url.Url is null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            var output = url.Output.Trim();
            if (NeedsSignIn(output)) return new(NotSignedIn, tunnelId, null, HasPort: true);
            log?.Invoke("Mobile: devtunnel host said: " + output);
            return new(new("failed", null, "devtunnel host did not start" + (output.Length > 0 ? ": " + Summary(output) : ".")), tunnelId, null, HasPort: true);
        }

        return new(new("serving", url.Url, $"Dev Tunnels serves {url.Url}"), tunnelId, host, HasPort: true);
    }

    // Makes a tunnel with anonymous access: its id, or why there is none.
    private async Task<(string? TunnelId, MobileProviderStatus? Failure)> CreateAsync(CancellationToken cancellationToken)
    {
        var created = await cli.RunAsync(["create", "--allow-anonymous"], Patience, cancellationToken).ConfigureAwait(false);
        var tunnelId = created is { ExitCode: 0, TimedOut: false } ? TunnelIdIn(created.Output) : null;
        if (tunnelId is not null) return (tunnelId, null);
        return (null, Failed("devtunnel create", created));
    }

    // Gives the tunnel the port of the view over HTTP; a port it has already is fine. Null when it has it, otherwise why not.
    private async Task<MobileProviderStatus?> AddPortAsync(string tunnelId, int port, CancellationToken cancellationToken)
    {
        var added = await cli.RunAsync(["port", "create", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--protocol", "http"], Patience, cancellationToken).ConfigureAwait(false);
        if (added is { ExitCode: 0, TimedOut: false } || PortExists().IsMatch(added.Output)) return null;
        return Failed("devtunnel port create", added);
    }

    /// <summary>Stops the old port of a tunnel when the view moves to another one; what fails is left, the tunnel serves the new one.</summary>
    internal async Task RemovePortAsync(string tunnelId, int port, CancellationToken cancellationToken)
        => await cli.RunAsync(["port", "delete", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture)], Patience, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Signs in to Dev Tunnels. With GitHub, the command line opens the browser. With a Microsoft account on Windows, it always shows
    /// the account picker of Windows, even when the browser is asked, and that picker needs a window: the command line runs in a console of
    /// its own, which closes when the person signed in. It ends when the person signed in, or after ten minutes.
    /// </summary>
    /// <returns>Whether the person is signed in now.</returns>
    internal async Task<bool> SignInAsync(bool github, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return false;
        var patience = TimeSpan.FromMinutes(10);
        if (!github && OperatingSystem.IsWindows())
        {
            var code = await cli.RunInWindowAsync(["user", "login", "--entra"], patience, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"Mobile: devtunnel user login ended with {code}.");
        }
        else
        {
            string[] args = ["user", "login", github ? "--github" : "--entra", "--use-browser-auth"];
            var result = await cli.RunAsync(args, patience, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"Mobile: devtunnel user login ended with {result.ExitCode}: {result.Output.Trim()}");
        }

        return IsSignedIn(await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false));
    }

    private static MobileProviderStatus NotSignedIn { get; } = new("not-signed-in", null, "Sign in to Dev Tunnels with a Microsoft or GitHub account: CodeAlta opens the page of the account.");

    /// <summary>Whether a command of the command line failed because the sign-in is missing or no longer accepted.</summary>
    internal static bool NeedsSignIn(string output) => SignInRefused().IsMatch(output);

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

    private MobileProviderStatus Failed(string command, MobileCliResult result)
    {
        log?.Invoke($"Mobile: {command} ended with {result.ExitCode}: {result.Output.Trim()}");
        // A sign-in that the account no longer accepts (revoked, expired) while the command line still keeps it: the person signs in again.
        if (!result.TimedOut && NeedsSignIn(result.Output)) return NotSignedIn;
        return new("failed", null, result.TimedOut ? $"{command} did not answer." : $"{command} failed: {Summary(result.Output)}");
    }

    /// <summary>
    /// What a person reads of an error of the command line: its lines without the request id and the greeting it adds, the first two.
    /// </summary>
    internal static string Summary(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith("Request ID", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Welcome to dev tunnels", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("CLI version", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return lines.Length > 0 ? string.Join(" ", lines) : LastLine(output);
    }

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    [GeneratedRegex("already exists|conflict", RegexOptions.IgnoreCase)]
    private static partial Regex PortExists();

    [GeneratedRegex(@"login required|not logged in|unauthorized|\b401\b|token (?:is )?(?:expired|revoked|invalid)|sign in again", RegexOptions.IgnoreCase)]
    private static partial Regex SignInRefused();

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
    public Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Find() is { } path ? MobileCommandLine.RunAsync(path, args, patience, cancellationToken) : Task.FromResult(new MobileCliResult(-1, "devtunnel is not installed."));
    }

    /// <inheritdoc />
    public async Task<int> RunInWindowAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Find() is { } path ? await MobileCommandLine.RunInWindowAsync(path, args, patience, cancellationToken).ConfigureAwait(false) : -1;
    }

    /// <inheritdoc />
    public IMobileHostedProcess Start(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var path = Find() ?? throw new InvalidOperationException("devtunnel is not installed.");
        var process = Process.Start(MobileCommandLine.Info(path, args, redirected: true)) ?? throw new InvalidOperationException("devtunnel did not start.");
        return new Hosted(process);
    }

    // Looked for at each call: an install is seen without a restart. Beside the path, where winget, Homebrew and the install script put it.
    private static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return MobileCommandLine.Find("devtunnel", OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "devtunnel.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/opt/homebrew/bin/devtunnel", "/usr/local/bin/devtunnel", Path.Combine(home, "bin", "devtunnel")]
                : [Path.Combine(home, "bin", "devtunnel"), "/usr/local/bin/devtunnel", "/usr/bin/devtunnel"]);
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

/// <summary>
/// The provider of Dev Tunnels: the server listens on the loopback address, and a Dev Tunnel of the user (made once, kept in the
/// settings so that its address does not change) publishes it at an HTTPS address any phone opens, hosted while the plugin runs.
/// </summary>
internal sealed class MobileDevTunnelProvider(IMobileDevTunnelCli cli, IMobileProviderHost host) : IMobileProvider
{
    /// <summary>The id of the provider in the settings.</summary>
    internal const string ProviderId = "devtunnel";

    // The host of the tunnel while it serves; the tunnel and the port it was given, taken off it when the view moves to another port; the
    // sign-in that runs.
    private IMobileHostedProcess? _host;
    private (string TunnelId, int Port)? _published;
    private CancellationTokenSource? _signIn;

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public MobileListen Listen(MobileSettings settings) => new(settings.Port);

    private MobileDevTunnel Tunnel => new(cli, host.Log);

    /// <inheritdoc />
    public async Task<MobileProviderStatus> PublishAsync(CancellationToken cancellationToken)
    {
        // Try again drops a sign-in that has not ended, and the host of before.
        await UnpublishAsync(cancellationToken).ConfigureAwait(false);
        var settings = host.Settings;
        if (_published is { } before && before.Port != settings.Port) await RemovePortAsync(before, cancellationToken).ConfigureAwait(false);
        MobileDevTunnel.Published published;
        try
        {
            published = await Tunnel.PublishAsync(settings.Port, settings.TunnelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            published = new(new MobileProviderStatus("failed", null, "Dev Tunnels could not be asked: " + exception.Message), settings.TunnelId, null);
        }

        // The tunnel made now is kept: its address, which phones keep, stays the same at the next start.
        if (published.TunnelId != settings.TunnelId) await host.SaveAsync(kept => kept with { TunnelId = published.TunnelId }).ConfigureAwait(false);
        host.Log($"Mobile: {published.Status.Detail}");
        if (published.HasPort) _published = (published.TunnelId!, settings.Port);
        if (published.Host is { } hosted)
        {
            _host = hosted;
            host.Run("mobile-tunnel", "Watches the Dev Tunnel of the mobile view.", ct => WatchAsync(hosted, ct));
        }

        return published.Status;
    }

    /// <inheritdoc />
    /// <remarks>The tunnel and its address stay in the account, for the next start.</remarks>
    public async Task UnpublishAsync(CancellationToken cancellationToken)
    {
        await CancelSignInAsync(Interlocked.Exchange(ref _signIn, null)).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _host, null) is { } hosted) await hosted.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MobileProviderStatus?> ActAsync(string action, CancellationToken cancellationToken)
    {
        if (action is not ("signin-github" or "signin-microsoft")) return null;
        var github = action == "signin-github";
        var run = new CancellationTokenSource();
        await CancelSignInAsync(Interlocked.Exchange(ref _signIn, run)).ConfigureAwait(false);
        host.Run("mobile-devtunnel-signin", "Signs in to Dev Tunnels.", async _ => await SignInAsync(github, run).ConfigureAwait(false));
        return new MobileProviderStatus("signing-in", null, !github && OperatingSystem.IsWindows()
            ? "Choose your account in the window of Windows that opens, then come back here."
            : "Sign in in the browser that opens, then come back here.") { Working = true };
    }

    // Stops a sign-in that runs; its task disposes it once it ended.
    private static async Task CancelSignInAsync(CancellationTokenSource? signIn)
    {
        if (signIn is null) return;
        try { await signIn.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private async Task SignInAsync(bool github, CancellationTokenSource run)
    {
        bool signedIn;
        try
        {
            signedIn = await Tunnel.SignInAsync(github, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            signedIn = false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            host.Log($"Mobile: the sign-in of Dev Tunnels could not start: {exception.Message}");
            signedIn = false;
        }

        Interlocked.CompareExchange(ref _signIn, null, run);
        var canceled = run.IsCancellationRequested;
        run.Dispose();
        if (canceled) return;
        if (!signedIn)
        {
            await host.ReportAsync(new MobileProviderStatus("not-signed-in", null, "The sign-in did not end. Sign in again, or run devtunnel user login in a terminal.")).ConfigureAwait(false);
            return;
        }

        host.Log("Mobile: signed in to Dev Tunnels.");
        await host.RepublishAsync().ConfigureAwait(false);
    }

    // Reads what the host writes until it exits. A host that exits while it is the one of now (not stopped here) is said stopped.
    private async ValueTask WatchAsync(IMobileHostedProcess hosted, CancellationToken cancellationToken)
    {
        var last = "";
        try
        {
            await foreach (var line in hosted.Lines.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _host, null, hosted) != hosted) return;
        await hosted.DisposeAsync().ConfigureAwait(false);
        host.Log($"Mobile: the Dev Tunnel stopped: {last}");
        await host.ReportAsync(new MobileProviderStatus("failed", null, "The Dev Tunnel stopped" + (last.Length > 0 ? ": " + last : "."))).ConfigureAwait(false);
    }

    // Takes off the tunnel the port the view left; what fails is left, the tunnel serves the new one.
    private async Task RemovePortAsync((string TunnelId, int Port) left, CancellationToken cancellationToken)
    {
        _published = null;
        try { await Tunnel.RemovePortAsync(left.TunnelId, left.Port, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            host.Log($"Mobile: the old port of the Dev Tunnel could not be removed: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await UnpublishAsync(CancellationToken.None).ConfigureAwait(false);
}
