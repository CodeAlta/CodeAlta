using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What the plugin knows of Tailscale on this computer.</summary>
/// <param name="State">
/// <c>pending</c>, <c>not-installed</c>, <c>not-running</c> (not signed in, or stopped), <c>serve-disabled</c> (Serve or HTTPS
/// certificates are not enabled on the tailnet), <c>port-taken</c> (the HTTPS port of the plugin serves something else),
/// <c>serving</c>, <c>failed</c> or <c>stopped</c>.
/// </param>
/// <param name="Url">The address of the mobile view on the tailnet, while it is served.</param>
/// <param name="Detail">A line for people.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
internal sealed record MobileTailscaleStatus(string State, string? Url, string Detail, string? FixUrl = null)
{
    internal static MobileTailscaleStatus Pending { get; } = new("pending", null, "Checking Tailscale…");

    internal static MobileTailscaleStatus Stopped { get; } = new("stopped", null, "The mobile view is not served.");
}

/// <summary>What a run of the Tailscale command line gave.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">What it wrote, its errors after its output.</param>
/// <param name="TimedOut">Whether it was stopped because it did not end in time: it waits for an answer then.</param>
internal sealed record MobileCliResult(int ExitCode, string Output, bool TimedOut = false);

/// <summary>Runs the Tailscale command line. The tests give their own.</summary>
internal interface IMobileTailscaleCli
{
    /// <summary>Gets whether the Tailscale command line is installed.</summary>
    bool IsInstalled { get; }

    /// <summary>Runs it and waits for its end, a few seconds at most.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What it gave.</returns>
    Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the mobile view on the tailnet with Tailscale Serve, on an HTTPS port of its own, and takes it off again. It
/// never changes what is served on the other ports, and takes off only the handler that points at the plugin's server.
/// </summary>
internal sealed class MobileTailscale(IMobileTailscaleCli cli)
{
    internal const string DownloadUrl = "https://tailscale.com/download";
    internal const string DnsUrl = "https://login.tailscale.com/admin/dns";

    /// <summary>Publishes <c>http://127.0.0.1:{localPort}</c> at <c>https://{node}:{httpsPort}</c>.</summary>
    /// <param name="localPort">The port of the plugin's server on the loopback address.</param>
    /// <param name="httpsPort">The HTTPS port of this computer on the tailnet.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>What Tailscale did.</returns>
    internal async Task<MobileTailscaleStatus> PublishAsync(int localPort, int httpsPort, CancellationToken cancellationToken)
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

    /// <summary>Takes the mobile view off the tailnet: the handler of its HTTPS port, when it points at the plugin's server.</summary>
    /// <param name="localPort">The port of the plugin's server on the loopback address.</param>
    /// <param name="httpsPort">The HTTPS port of this computer on the tailnet.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>What happened, for the log.</returns>
    internal async Task<string> UnpublishAsync(int localPort, int httpsPort, CancellationToken cancellationToken)
    {
        if (!cli.IsInstalled) return "Tailscale is not installed.";
        var (proxy, _, unread) = await ServedAsync(httpsPort, cancellationToken).ConfigureAwait(false);
        if (unread is not null) return unread.Detail;
        if (!string.Equals(proxy, Target(localPort), StringComparison.OrdinalIgnoreCase)) return $"Port {httpsPort} does not serve the mobile view: nothing to take off.";
        var result = await cli.RunAsync(["serve", HttpsOption(httpsPort), "off"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? "Tailscale no longer serves the mobile view." : $"tailscale serve off failed: {FirstLine(result.Output)}";
    }

    private static string Target(int localPort) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{localPort}");

    private static string HttpsOption(int httpsPort) => string.Create(CultureInfo.InvariantCulture, $"--https={httpsPort}");

    private static MobileTailscaleStatus Serving(string dnsName, int httpsPort)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"https://{dnsName}:{httpsPort}");
        return new("serving", url, $"Tailscale serves {url}");
    }

    // The name of this computer on the tailnet, or why there is none.
    private async Task<(string? DnsName, MobileTailscaleStatus? NotRunning)> NodeAsync(CancellationToken cancellationToken)
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
    private async Task<(string? Proxy, bool Used, MobileTailscaleStatus? Unread)> ServedAsync(int httpsPort, CancellationToken cancellationToken)
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
internal sealed class MobileTailscaleCli : IMobileTailscaleCli
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private readonly string? _path = Find();

    /// <inheritdoc />
    public bool IsInstalled => _path is not null;

    /// <inheritdoc />
    public async Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (_path is null) return new(-1, "Tailscale is not installed.");
        var info = new ProcessStartInfo(_path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("tailscale did not start.");
        // The streams are read to their end whatever happens: a command that waits (Serve not enabled on the tailnet waits
        // for the admin) is stopped after a while, and what it printed still says why.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
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

    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
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

        string[] usual = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"]
                : ["/usr/bin/tailscale", "/usr/local/bin/tailscale"];
        return usual.FirstOrDefault(File.Exists);
    }
}
