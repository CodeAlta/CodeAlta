using System.Globalization;
using System.Text.Json;

using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.PrivateNetworks;

/// <summary>What a run of a command line gave.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">What it wrote, its errors after its output.</param>
/// <param name="TimedOut">Whether it was stopped because it did not end in time: it waits for an answer then.</param>
internal sealed record CommandLineResult(int ExitCode, string Output, bool TimedOut = false);

/// <summary>Runs the Tailscale command line. The tests give their own.</summary>
internal interface ITailscaleCli
{
    /// <summary>Gets whether the Tailscale command line is installed.</summary>
    bool IsInstalled { get; }

    /// <summary>Runs it and waits for its end, a few seconds at most.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What it gave.</returns>
    Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the page on the tailnet with Tailscale Serve, on an HTTPS port of its own, and takes it off again. It
/// never changes what is served on the other ports, and takes off only the handler that points at the page's server. Its states:
/// <c>not-installed</c>, <c>not-running</c> (not signed in, or stopped), <c>serve-disabled</c> (Serve or HTTPS certificates are not
/// enabled on the tailnet), <c>port-taken</c> (the HTTPS port serves something else), <c>serving</c> and <c>failed</c>.
/// </summary>
internal sealed class TailscaleServe(ITailscaleCli cli)
{
    internal const string DownloadUrl = "https://tailscale.com/download";
    internal const string DnsUrl = "https://login.tailscale.com/admin/dns";

    /// <summary>Publishes <c>http://127.0.0.1:{localPort}</c> at <c>https://{node}:{httpsPort}</c>.</summary>
    /// <param name="localPort">The port of the page's server on the loopback address.</param>
    /// <param name="httpsPort">The HTTPS port of this computer on the tailnet.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>What Tailscale did.</returns>
    internal async Task<PrivateNetworkStatus> PublishAsync(int localPort, int httpsPort, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return new("not-installed", null, "Install it on this computer and on the phone, and sign in to the same tailnet on both.", DownloadUrl);
        var (dnsName, notRunning) = await NodeAsync(cancellationToken).ConfigureAwait(false);
        if (notRunning is not null) return notRunning;

        var target = Target(localPort);
        var (proxy, used, unread) = await ServedAsync(httpsPort, cancellationToken).ConfigureAwait(false);
        if (unread is not null) return unread;
        if (string.Equals(proxy, target, StringComparison.OrdinalIgnoreCase)) return Serving(dnsName!, httpsPort);
        if (used) return new("port-taken", null, $"Port {httpsPort} of this computer on the tailnet already serves something else.");

        var result = await cli.RunAsync(["serve", "--bg", HttpsOption(httpsPort), target], cancellationToken).ConfigureAwait(false);
        // Without Serve on the tailnet, the command prints the page that enables it and waits for it.
        if (result.Output.Contains("Serve is not enabled", StringComparison.OrdinalIgnoreCase))
            return new("serve-disabled", null, "An admin of the tailnet turns on Serve and HTTPS certificates once, then this computer can publish the page.", LinkIn(result.Output) ?? DnsUrl);
        if (result.TimedOut) return new("failed", null, "tailscale serve did not answer.");
        if (result.ExitCode != 0) return new("failed", null, $"tailscale serve failed: {FirstLine(result.Output)}");
        return Serving(dnsName!, httpsPort);
    }

    /// <summary>Takes the page off the tailnet: the handler of its HTTPS port, when it points at the page's server.</summary>
    /// <param name="localPort">The port of the page's server on the loopback address.</param>
    /// <param name="httpsPort">The HTTPS port of this computer on the tailnet.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>What happened, for the log.</returns>
    internal async Task<string> UnpublishAsync(int localPort, int httpsPort, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return "Tailscale is not installed.";
        var (proxy, _, unread) = await ServedAsync(httpsPort, cancellationToken).ConfigureAwait(false);
        if (unread is not null) return unread.Detail;
        if (!string.Equals(proxy, Target(localPort), StringComparison.OrdinalIgnoreCase)) return $"Port {httpsPort} does not serve the page: nothing to take off.";
        var result = await cli.RunAsync(["serve", HttpsOption(httpsPort), "off"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? "Tailscale no longer serves the page." : $"tailscale serve off failed: {FirstLine(result.Output)}";
    }

    private static string Target(int localPort) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{localPort}");

    private static string HttpsOption(int httpsPort) => string.Create(CultureInfo.InvariantCulture, $"--https={httpsPort}");

    private static PrivateNetworkStatus Serving(string dnsName, int httpsPort)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"https://{dnsName}:{httpsPort}");
        return new("serving", url, $"Tailscale serves {url}");
    }

    // The name of this computer on the tailnet, or why there is none.
    private async Task<(string? DnsName, PrivateNetworkStatus? NotRunning)> NodeAsync(CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) return (null, new("failed", null, "tailscale status did not answer."));
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            var backend = Text(root, "BackendState");
            var dns = root.TryGetProperty("Self", out var self) ? Text(self, "DNSName")?.TrimEnd('.') : null;
            if (!string.Equals(backend, "Running", StringComparison.OrdinalIgnoreCase)) return (null, new("not-running", null, "Start Tailscale on this computer and sign in to your tailnet."));
            if (string.IsNullOrEmpty(dns)) return (null, new("serve-disabled", null, "This computer has no name on the tailnet: MagicDNS is off.", DnsUrl));
            return (dns, null);
        }
        catch (JsonException)
        {
            // It answers with a line when the service does not run.
            return (null, new("not-running", null, result.ExitCode == 0 ? "tailscale status did not answer in JSON." : "Tailscale is not running."));
        }
    }

    // What the HTTPS port serves now: the proxy of its root when it serves nothing else, and whether anything is configured on it.
    private async Task<(string? Proxy, bool Used, PrivateNetworkStatus? Unread)> ServedAsync(int httpsPort, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(["serve", "status", "--json"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) return (null, false, new("failed", null, $"tailscale serve status failed: {FirstLine(result.Output)}"));
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Output) ? "{}" : result.Output);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false, null);
            var port = httpsPort.ToString(CultureInfo.InvariantCulture);
            var used = root.TryGetProperty("TCP", out var tcp) && tcp.ValueKind == JsonValueKind.Object && tcp.TryGetProperty(port, out _);
            string? proxy = null;
            if (root.TryGetProperty("Web", out var web) && web.ValueKind == JsonValueKind.Object)
            {
                foreach (var site in web.EnumerateObject())
                {
                    if (!site.Name.EndsWith(":" + port, StringComparison.Ordinal)) continue;
                    used = true;
                    if (site.Value.ValueKind == JsonValueKind.Object && site.Value.TryGetProperty("Handlers", out var handlers) && handlers.ValueKind == JsonValueKind.Object
                        && handlers.EnumerateObject().Count() == 1 && handlers.TryGetProperty("/", out var handler) && handler.ValueKind == JsonValueKind.Object)
                        proxy = Text(handler, "Proxy");
                }
            }

            return (proxy, used, null);
        }
        catch (JsonException)
        {
            return (null, false, new("failed", null, "tailscale serve status did not answer in JSON."));
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? LinkIn(string output)
        => output.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(static word => word.StartsWith("https://", StringComparison.Ordinal));

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

/// <summary>The Tailscale command line of this computer, found on the path or where its installer puts it.</summary>
internal sealed class TailscaleCli : ITailscaleCli
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private readonly string? _path = PrivateNetworkCommandLine.Find("tailscale", OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]
        : OperatingSystem.IsMacOS()
            ? ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"]
            : ["/usr/bin/tailscale", "/usr/local/bin/tailscale"]);

    /// <inheritdoc />
    public bool IsInstalled => _path is not null;

    /// <inheritdoc />
    /// <remarks>A command that waits (Serve not enabled on the tailnet waits for the admin) is stopped after a while.</remarks>
    public Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        return _path is null ? Task.FromResult(new CommandLineResult(-1, "Tailscale is not installed.")) : PrivateNetworkCommandLine.RunAsync(_path, args, Patience, cancellationToken);
    }
}

/// <summary>
/// Tailscale: this computer on the tailnet (its MagicDNS name and its addresses there, which the fleet names in its pairing links), and
/// Tailscale Serve, which publishes a page of the loopback address on an HTTPS port of the tailnet. What a publisher published is taken
/// off with the ports it was published with.
/// </summary>
internal sealed class TailscaleNetwork(ITailscaleCli cli) : IPrivateNetworkProvider
{
    /// <summary>The id of the provider.</summary>
    internal const string ProviderId = "tailscale";

    /// <summary>The HTTPS port of the tailnet a page is published at when the publication names none.</summary>
    internal const int DefaultPublicPort = 443;

    /// <summary>Creates the provider with the Tailscale command line of this computer.</summary>
    public TailscaleNetwork()
        : this(new TailscaleCli())
    {
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Tailscale";

    /// <inheritdoc />
    public string Description => "The devices of your tailnet, signed in to the same Tailscale account.";

    /// <inheritdoc />
    public PrivateNetworkCapabilities Capabilities => PrivateNetworkCapabilities.Presence | PrivateNetworkCapabilities.Publish | PrivateNetworkCapabilities.PublicPort;

    /// <inheritdoc />
    /// <remarks>Nothing is changed in Tailscale: <c>tailscale status --json</c> is read.</remarks>
    public async ValueTask<PrivateNetworkPresence?> FindThisComputerAsync(CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return null;
        try
        {
            var result = await cli.RunAsync(["status", "--json"], cancellationToken).ConfigureAwait(false);
            return result.TimedOut ? null : Parse(result.Output);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads the answer of <c>tailscale status --json</c>; null when Tailscale does not run or this computer has no address.</summary>
    /// <param name="json">What the command wrote.</param>
    /// <returns>This computer on the tailnet: its MagicDNS name, or its IPv4 address when MagicDNS is off, and its addresses, IPv4 first.</returns>
    internal static PrivateNetworkPresence? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object) return null;
            if (!string.Equals(Text(root, "BackendState"), "Running", StringComparison.OrdinalIgnoreCase)) return null;
            System.Net.IPAddress[] addresses = self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? [.. ips.EnumerateArray().Select(static ip => ip.ValueKind == JsonValueKind.String && System.Net.IPAddress.TryParse(ip.GetString(), out var address) ? address : null)
                    .OfType<System.Net.IPAddress>().Distinct().OrderBy(static address => address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)]
                : [];
            if (addresses.Length == 0) return null;
            // Without MagicDNS the computer has no name on the tailnet: the other devices reach it by its address.
            var name = Text(self, "DNSName")?.TrimEnd('.');
            return new PrivateNetworkPresence(string.IsNullOrEmpty(name) ? addresses[0].ToString() : name, addresses);
        }
        catch (JsonException)
        {
            // It answers with a line when its service does not run.
            return null;
        }
    }

    /// <inheritdoc />
    public IPrivateNetworkPublisher CreatePublisher(IPrivateNetworkHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new Publisher(cli, host);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Tailscale Serve for one user: the page is published at an HTTPS port of the tailnet, and taken off with the ports it was published with.
    private sealed class Publisher(ITailscaleCli cli, IPrivateNetworkHost host) : IPrivateNetworkPublisher
    {
        // The ports Tailscale serves the page with now, as this publisher published them.
        private (int Port, int HttpsPort)? _published;

        public async Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(publication);
            var ports = (publication.LocalPort, publication.PublicPort ?? DefaultPublicPort);
            // What was published with other ports goes first.
            if (_published is { } published && published != ports) await UnpublishAsync(cancellationToken).ConfigureAwait(false);
            var status = await new TailscaleServe(cli).PublishAsync(ports.LocalPort, ports.Item2, cancellationToken).ConfigureAwait(false);
            if (status.State == "serving") _published = ports;
            return status;
        }

        public async Task UnpublishAsync(CancellationToken cancellationToken)
        {
            if (_published is not { } published) return;
            try
            {
                host.Log(await new TailscaleServe(cli).UnpublishAsync(published.Port, published.HttpsPort, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                host.Log($"Tailscale could not be asked to stop serving: {exception.Message}");
            }

            _published = null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
