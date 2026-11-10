using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.PrivateNetworks;

/// <summary>A command of the Dev Tunnels command line that runs until it is stopped: <c>devtunnel host</c>.</summary>
internal interface IHostedCommand : IAsyncDisposable
{
    /// <summary>Gets the lines it writes, its errors with them; the reader completes when it exits.</summary>
    ChannelReader<string> Lines { get; }

    /// <summary>Gets whether it still runs.</summary>
    bool IsRunning { get; }
}

/// <summary>Runs the Dev Tunnels command line (<c>devtunnel</c>). The tests give their own.</summary>
internal interface IDevTunnelCli
{
    /// <summary>Gets whether the command line is installed: looked for again each time, so that an install is seen without a restart.</summary>
    bool IsInstalled { get; }

    /// <summary>Runs a command and waits for its end, <paramref name="patience"/> at most.</summary>
    Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken);

    /// <summary>Starts a command that runs until it is disposed.</summary>
    IHostedCommand Start(IReadOnlyList<string> args);
}

/// <summary>
/// Publishes the page through a Dev Tunnel of Microsoft (states: <c>not-installed</c>, <c>not-signed-in</c>, <c>serving</c>,
/// <c>failed</c>): a tunnel of the user's account, made once and kept (so that its address, which phones keep, does not change), with
/// the port of the view, hosted by <c>devtunnel host</c> while its user runs. The tunnel lets in its own account alone, as a tunnel does
/// by default: the browser of the phone signs in with that GitHub account before it reaches the view, where the pairing
/// token of the QR code lets it in, as behind Tailscale. The account is a GitHub one: its sign-in is a page of the browser on every
/// system, on the computer as on the phone.
/// </summary>
internal sealed partial class DevTunnel(IDevTunnelCli cli, Action<string>? log = null)
{
    /// <summary>The page that installs the command line.</summary>
    internal const string InstallUrl = "https://learn.microsoft.com/azure/developer/dev-tunnels/get-started";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What a publication gave: the state, the tunnel its user keeps, the host process that serves it, and whether the tunnel
    /// was given the port (to take it off when the view moves to another one, even when the host did not start).
    /// </summary>
    internal sealed record Published(PrivateNetworkStatus Status, string? TunnelId, IHostedCommand? Host, bool HasPort = false);

    /// <summary>
    /// Publishes <c>http://127.0.0.1:{port}</c>: checks the command line and the sign-in, gives the port to the tunnel kept (one that is
    /// gone, or that this account cannot change, refuses it), or else to a new one, keeps anonymous visitors out of the port, and hosts the
    /// tunnel until the process of the outcome is disposed.
    /// </summary>
    /// <param name="port">The port of the page's server on the loopback address.</param>
    /// <param name="tunnelId">The tunnel its user kept, or null.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    internal async Task<Published> PublishAsync(int port, string? tunnelId, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return new(new("not-installed", null, "Install the Dev Tunnels command line (devtunnel) on this computer.", InstallUrl), tunnelId, null);
        var user = await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false);
        if (!IsSignedIn(user)) return new(NotSignedIn, tunnelId, null);

        var added = tunnelId is null ? null : await AddPortAsync(tunnelId, port, cancellationToken).ConfigureAwait(false);
        var made = tunnelId is null || added is not null;
        if (made)
        {
            // No tunnel yet, or the one kept refused the port (gone, or made with another account): a new one is made, once.
            if (tunnelId is not null) log?.Invoke($"the Dev Tunnel {tunnelId} refused the port; a new tunnel is made.");
            (tunnelId, var failure) = await CreateAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null) return new(failure, null, null);
            added = await AddPortAsync(tunnelId!, port, cancellationToken).ConfigureAwait(false);
        }

        if (added is not null) return new(added, tunnelId, null);
        // A tunnel made now lets in its account alone; a tunnel kept may let anyone in (made by a CodeAlta of before).
        if (!made && await DenyAnonymousAsync(tunnelId!, port, cancellationToken).ConfigureAwait(false) is { } open) return new(open, tunnelId, null, HasPort: true);

        var host = cli.Start(["host", tunnelId!]);
        var url = await UrlAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (url.Url is null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            var output = url.Output.Trim();
            if (NeedsSignIn(output)) return new(NotSignedIn, tunnelId, null, HasPort: true);
            log?.Invoke("devtunnel host said: " + output);
            return new(new("failed", null, "devtunnel host did not start" + (output.Length > 0 ? ": " + Summary(output) : ".")), tunnelId, null, HasPort: true);
        }

        return new(new("serving", url.Url, $"Dev Tunnels serves {url.Url} to the account signed in on this computer"), tunnelId, host, HasPort: true);
    }

    // Makes a tunnel, which lets in its account alone: its id, or why there is none.
    private async Task<(string? TunnelId, PrivateNetworkStatus? Failure)> CreateAsync(CancellationToken cancellationToken)
    {
        var created = await cli.RunAsync(["create"], Patience, cancellationToken).ConfigureAwait(false);
        var tunnelId = created is { ExitCode: 0, TimedOut: false } ? TunnelIdIn(created.Output) : null;
        if (tunnelId is not null) return (tunnelId, null);
        return (null, Failed("devtunnel create", created));
    }

    // Makes sure no one reaches the port of the view without the account of the tunnel: what the access of the port says, and when it may
    // let anyone in (an allow of the tunnel it inherits, of a CodeAlta of before), a deny of anonymous access on the port, which wins over
    // any allow, read again to be sure. Null once it is closed; otherwise why it is not served.
    private async Task<PrivateNetworkStatus?> DenyAnonymousAsync(string tunnelId, int port, CancellationToken cancellationToken)
    {
        var number = port.ToString(CultureInfo.InvariantCulture);
        string[] list = ["access", "list", tunnelId, "--port-number", number, "--json", "--nologo"];
        var listed = await cli.RunAsync(list, Patience, cancellationToken).ConfigureAwait(false);
        if (listed is { ExitCode: 0, TimedOut: false } && AnonymousIsDenied(listed.Output) == true) return null;
        log?.Invoke($"the port {number} of the Dev Tunnel {tunnelId} may let anyone in: {listed.Output.Trim()}");
        var denied = await cli.RunAsync(["access", "create", tunnelId, "--port-number", number, "--anonymous", "--deny", "--nologo"], Patience, cancellationToken).ConfigureAwait(false);
        const string Open = "The Dev Tunnel may let anyone in, so it does not serve the page.";
        if (denied is not { ExitCode: 0, TimedOut: false })
        {
            var failed = Failed("devtunnel access create", denied);
            return failed.State == "failed" ? failed with { Detail = Open + " " + failed.Detail } : failed;
        }

        var again = await cli.RunAsync(list, Patience, cancellationToken).ConfigureAwait(false);
        if (again is { ExitCode: 0, TimedOut: false } && AnonymousIsDenied(again.Output) == true) return null;
        log?.Invoke($"the port {number} of the Dev Tunnel {tunnelId} is still open after the deny: {again.Output.Trim()}");
        return new PrivateNetworkStatus("failed", null, Open + " devtunnel access create did not close it.");
    }

    /// <summary>
    /// Whether what <c>devtunnel access list --json</c> says keeps anonymous visitors out: true when no allow of anonymous access holds or
    /// a permanent deny of it does (a deny wins); false when one lets them in; null when it cannot be read. The JSON is the first value
    /// of the output: what the command line writes after it (its errors) is left.
    /// </summary>
    internal static bool? AnonymousIsDenied(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) return null;
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(output[start..]), new JsonReaderOptions { AllowTrailingCommas = true });
            using var document = JsonDocument.ParseValue(ref reader);
            if (!TryProperty(document.RootElement, "accessControlEntries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
            bool allowed = false, denied = false;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!IsAnonymousConnect(entry)) continue;
                if (!TryProperty(entry, "isDeny", out var deny) || deny.ValueKind != JsonValueKind.True) allowed = true;
                // A deny that ends lets the allows in again then: only a permanent one closes the port.
                else if (!TryProperty(entry, "expiration", out var ends) || ends.ValueKind == JsonValueKind.Null) denied = true;
            }

            return denied || !allowed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An entry of anonymous access to connect, permanent or not yet expired; an inverse one is about the users who signed in.
    private static bool IsAnonymousConnect(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (!TryProperty(entry, "type", out var type) || !string.Equals(type.GetString(), "Anonymous", StringComparison.OrdinalIgnoreCase)) return false;
        if (TryProperty(entry, "isInverse", out var inverse) && inverse.ValueKind == JsonValueKind.True) return false;
        if (TryProperty(entry, "expiration", out var expiration) && expiration.ValueKind == JsonValueKind.String
            && expiration.TryGetDateTimeOffset(out var ends) && ends <= DateTimeOffset.UtcNow) return false;
        return TryProperty(entry, "scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            && scopes.EnumerateArray().Any(static scope => string.Equals(scope.GetString(), "connect", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        return false;
    }

    // Gives the tunnel the port of the view over HTTP; a port it has already is fine. Null when it has it, otherwise why not.
    private async Task<PrivateNetworkStatus?> AddPortAsync(string tunnelId, int port, CancellationToken cancellationToken)
    {
        var added = await cli.RunAsync(["port", "create", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture), "--protocol", "http"], Patience, cancellationToken).ConfigureAwait(false);
        if (added is { ExitCode: 0, TimedOut: false } || PortExists().IsMatch(added.Output)) return null;
        return Failed("devtunnel port create", added);
    }

    /// <summary>Stops the old port of a tunnel when the view moves to another one; what fails is left, the tunnel serves the new one.</summary>
    internal async Task RemovePortAsync(string tunnelId, int port, CancellationToken cancellationToken)
        => await cli.RunAsync(["port", "delete", tunnelId, "-p", port.ToString(CultureInfo.InvariantCulture)], Patience, cancellationToken).ConfigureAwait(false);

    /// <summary>Signs in to Dev Tunnels with GitHub, in the browser. It ends when the person signed in, or after ten minutes.</summary>
    /// <returns>Whether the person is signed in with GitHub now.</returns>
    internal async Task<bool> SignInAsync(CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return false;
        var result = await cli.RunAsync(["user", "login", "--github", "--use-browser-auth"], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        log?.Invoke($"devtunnel user login ended with {result.ExitCode}: {result.Output.Trim()}");
        return IsSignedIn(await cli.RunAsync(["user", "show"], Patience, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The action that signs in to Dev Tunnels with GitHub.</summary>
    internal const string SignInAction = "signin-github";

    /// <summary>The state of a command line that is not signed in, with the button that signs it in.</summary>
    internal static PrivateNetworkStatus NotSignedIn { get; } = new("not-signed-in", null, "Sign in to Dev Tunnels with a GitHub account: CodeAlta opens its page in the browser. The devices sign in with the same account.")
    {
        Actions = [new PrivateNetworkAction(SignInAction, "Sign in with GitHub")],
    };

    /// <summary>Whether a command of the command line failed because the sign-in is missing or no longer accepted.</summary>
    internal static bool NeedsSignIn(string output) => SignInRefused().IsMatch(output);

    /// <summary>
    /// Whether <c>devtunnel user show</c> says a user is signed in with GitHub (<c>Logged in as someone using GitHub.</c>). A Microsoft
    /// account is not one: the person signs in with GitHub, and the tunnel of the other account, which this one cannot change, is replaced.
    /// </summary>
    internal static bool IsSignedIn(CommandLineResult result)
        => result is { ExitCode: 0, TimedOut: false } && result.Output.Contains("Logged in as", StringComparison.OrdinalIgnoreCase)
            && !result.Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) && result.Output.Contains("GitHub", StringComparison.OrdinalIgnoreCase);

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
    private static async Task<(string? Url, string Output)> UrlAsync(IHostedCommand host, int port, CancellationToken cancellationToken)
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

    private PrivateNetworkStatus Failed(string command, CommandLineResult result)
    {
        log?.Invoke($"{command} ended with {result.ExitCode}: {result.Output.Trim()}");
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
internal sealed class DevTunnelCli : IDevTunnelCli
{
    /// <inheritdoc />
    public bool IsInstalled => Find() is not null;

    /// <inheritdoc />
    public Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Find() is { } path ? PrivateNetworkCommandLine.RunAsync(path, args, patience, cancellationToken) : Task.FromResult(new CommandLineResult(-1, "devtunnel is not installed."));
    }

    /// <inheritdoc />
    public IHostedCommand Start(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var path = Find() ?? throw new InvalidOperationException("devtunnel is not installed.");
        var process = Process.Start(PrivateNetworkCommandLine.Info(path, args)) ?? throw new InvalidOperationException("devtunnel did not start.");
        return new Hosted(process);
    }

    // Looked for at each call: an install is seen without a restart. Beside the path, where winget, Homebrew and the install script put it.
    private static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return PrivateNetworkCommandLine.Find("devtunnel", OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "devtunnel.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/opt/homebrew/bin/devtunnel", "/usr/local/bin/devtunnel", Path.Combine(home, "bin", "devtunnel")]
                : [Path.Combine(home, "bin", "devtunnel"), "/usr/local/bin/devtunnel", "/usr/bin/devtunnel"]);
    }

    // The process of devtunnel host: its lines, out and err, go to one channel; disposing it ends the process.
    private sealed class Hosted : IHostedCommand
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
/// Dev Tunnels of Microsoft: a tunnel of the user (made once, kept by its user so that its address does not change) publishes a page of the
/// loopback address at an HTTPS address any device opens once signed in with the account of the tunnel, hosted while its user runs. It says
/// nothing of where this computer is: the devices reach the page through the tunnel only.
/// </summary>
internal sealed class DevTunnelNetwork(IDevTunnelCli cli) : IPrivateNetworkProvider
{
    /// <summary>The id of the provider.</summary>
    internal const string ProviderId = "devtunnel";

    /// <summary>The name its user keeps the tunnel under.</summary>
    internal const string TunnelKey = "devtunnel.tunnel";

    /// <summary>Creates the provider with the Dev Tunnels command line of this computer.</summary>
    public DevTunnelNetwork()
        : this(new DevTunnelCli())
    {
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Dev Tunnels";

    /// <inheritdoc />
    public string Description => "Any device signed in with the GitHub account of the tunnel, through a Dev Tunnel of Microsoft.";

    /// <inheritdoc />
    /// <remarks>
    /// The browser of a device is sent to the sign-in of GitHub, then comes back to the address it opened: the tunnel keeps the path
    /// and the query of that address (the <c>rd</c> of its redirection), and the pages of the sign-in lose its fragment.
    /// </remarks>
    public PrivateNetworkCapabilities Capabilities => PrivateNetworkCapabilities.Publish | PrivateNetworkCapabilities.BrowserSignIn;

    /// <inheritdoc />
    public IPrivateNetworkPublisher CreatePublisher(IPrivateNetworkHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new Publisher(cli, host);
    }

    // A tunnel for one user: its host while it serves; the tunnel and the port it was given, taken off it when the page moves to another
    // port; the sign-in that runs.
    private sealed class Publisher(IDevTunnelCli cli, IPrivateNetworkHost host) : IPrivateNetworkPublisher
    {
        private IHostedCommand? _host;
        private (string TunnelId, int Port)? _published;
        private CancellationTokenSource? _signIn;

        private DevTunnel Tunnel => new(cli, host.Log);

        public async Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(publication);
            // Try again drops a sign-in that has not ended, and the host of before.
            await UnpublishAsync(cancellationToken).ConfigureAwait(false);
            if (_published is { } before && before.Port != publication.LocalPort) await RemovePortAsync(before, cancellationToken).ConfigureAwait(false);
            var kept = await host.ReadStateAsync(TunnelKey, cancellationToken).ConfigureAwait(false);
            var published = await Tunnel.PublishAsync(publication.LocalPort, kept, cancellationToken).ConfigureAwait(false);

            // The tunnel made now is kept: its address, which the devices keep, stays the same at the next start.
            if (published.TunnelId != kept) await host.WriteStateAsync(TunnelKey, published.TunnelId, cancellationToken).ConfigureAwait(false);
            if (published.HasPort) _published = (published.TunnelId!, publication.LocalPort);
            if (published.Host is { } hosted)
            {
                _host = hosted;
                host.Run("devtunnel-host", "Watches the Dev Tunnel of the page.", ct => WatchAsync(hosted, ct));
            }

            return published.Status;
        }

        /// <remarks>The tunnel and its address stay in the account, for the next start.</remarks>
        public async Task UnpublishAsync(CancellationToken cancellationToken)
        {
            await CancelSignInAsync(Interlocked.Exchange(ref _signIn, null)).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _host, null) is { } hosted) await hosted.DisposeAsync().ConfigureAwait(false);
        }

        public async Task<PrivateNetworkStatus?> ActAsync(string action, CancellationToken cancellationToken)
        {
            if (action != DevTunnel.SignInAction) return null;
            var run = new CancellationTokenSource();
            await CancelSignInAsync(Interlocked.Exchange(ref _signIn, run)).ConfigureAwait(false);
            host.Run("devtunnel-signin", "Signs in to Dev Tunnels.", async _ => await SignInAsync(run).ConfigureAwait(false));
            return new PrivateNetworkStatus("signing-in", null, "Sign in with GitHub in the browser that opens, then come back here.") { Working = true };
        }

        // Stops a sign-in that runs; its task disposes it once it ended.
        private static async Task CancelSignInAsync(CancellationTokenSource? signIn)
        {
            if (signIn is null) return;
            try { await signIn.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        private async Task SignInAsync(CancellationTokenSource run)
        {
            bool signedIn;
            try
            {
                signedIn = await Tunnel.SignInAsync(run.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                signedIn = false;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                host.Log($"The sign-in of Dev Tunnels could not start: {exception.Message}");
                signedIn = false;
            }

            Interlocked.CompareExchange(ref _signIn, null, run);
            var canceled = run.IsCancellationRequested;
            run.Dispose();
            if (canceled) return;
            if (!signedIn)
            {
                await host.ReportAsync(DevTunnel.NotSignedIn with { Detail = "The sign-in did not end. Sign in again, or run devtunnel user login in a terminal." }).ConfigureAwait(false);
                return;
            }

            host.Log("Signed in to Dev Tunnels.");
            await host.RepublishAsync().ConfigureAwait(false);
        }

        // Reads what the host writes until it exits. A host that exits while it is the one of now (not stopped here) is said stopped.
        private async ValueTask WatchAsync(IHostedCommand hosted, CancellationToken cancellationToken)
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
            host.Log($"The Dev Tunnel stopped: {last}");
            await host.ReportAsync(new PrivateNetworkStatus("failed", null, "The Dev Tunnel stopped" + (last.Length > 0 ? ": " + last : "."))).ConfigureAwait(false);
        }

        // Takes off the tunnel the port the page left; what fails is left, the tunnel serves the new one.
        private async Task RemovePortAsync((string TunnelId, int Port) left, CancellationToken cancellationToken)
        {
            _published = null;
            try { await Tunnel.RemovePortAsync(left.TunnelId, left.Port, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                host.Log($"The old port of the Dev Tunnel could not be removed: {exception.Message}");
            }
        }

        public async ValueTask DisposeAsync() => await UnpublishAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
