using System.Globalization;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.PrivateNetworks;

internal sealed partial class TailscaleNetwork
{
    /// <inheritdoc />
    public bool OwnsAddress(Uri address) => address.Scheme == Uri.UriSchemeHttps && address.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<PrivateNetworkRemoteResult?> CheckRemoteAsync(IPrivateNetworkRemoteHost host, Uri address, int localPort, bool publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(address);
        var remote = new RemoteCli(host, await host.IsInstalledAsync("tailscale", cancellationToken).ConfigureAwait(false));
        var serve = new TailscaleServe(remote);
        var status = await serve.CheckAsync(localPort, address.Port, cancellationToken).ConfigureAwait(false);
        if (status.State == "serve-disabled") return Disabled(status);
        if (status.Url is { } url && !string.Equals(new Uri(url).Host, address.Host, StringComparison.OrdinalIgnoreCase))
            return new($"on its tailnet, this machine is {new Uri(url).Host}");
        switch (status.State)
        {
            case "not-serving": break;
            case "serving": return Published();
            case "port-taken": return new($"Tailscale Serve publishes something else on port {address.Port.ToString(CultureInfo.InvariantCulture)} of the machine");
            case "not-running": return new("Tailscale does not run on the machine, or is not signed in");
            default: return new(status.Detail);
        }

        var command = string.Create(CultureInfo.InvariantCulture,
            $"{(host.OperatingSystem == "Linux" && !host.IsRoot ? "sudo " : "")}tailscale serve --bg --https={address.Port} http://127.0.0.1:{localPort}");
        if (!publish)
            return new(command, await remote.ChangesAskPasswordAsync(cancellationToken).ConfigureAwait(false) ? "tailscale_serve_missing_sudo" : "tailscale_serve_missing");
        status = await serve.PublishAsync(localPort, address.Port, cancellationToken).ConfigureAwait(false);
        if (status.State == "serve-disabled") return Disabled(status);
        if (status.State != "serving")
            return remote.Last is { NeedsPassword: true } ? new($"sudo asks the password of {host.UserName} there.", "tailscale_sudo_password_needed")
                : remote.Last is { ElevationRefused: true } ? new(Lines(status.Detail), "sudo_refused")
                : new($"{Lines(status.Detail)}\n{command}", "tailscale_serve_failed");
        return Published() with { Recheck = true, Activity = "Tailscale Serve" };

        static PrivateNetworkRemoteResult Published() => new("Tailscale Serve publishes it on the machine: check that this computer is on the same tailnet");
        static PrivateNetworkRemoteResult Disabled(PrivateNetworkStatus status) => new(status.FixUrl ?? TailscaleServe.DnsUrl, "tailscale_serve_disabled");
    }

    private static string Lines(string text) => string.Join('\n', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(6));

    private sealed class RemoteCli(IPrivateNetworkRemoteHost host, bool installed) : ITailscaleCli
    {
        public bool IsInstalled => installed;

        public PrivateNetworkRemoteCommandResult? Last { get; private set; }

        public async Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Last = await host.RunAsync("tailscale", args, args is ["serve", not "status", ..] ? "Access denied" : null, cancellationToken).ConfigureAwait(false);
            return new(Last.ExitCode, Last.Output, Last.TimedOut);
        }

        public async Task<bool> ChangesAskPasswordAsync(CancellationToken cancellationToken)
        {
            if (host.OperatingSystem != "Linux" || !host.ElevationAsksPassword) return false;
            var prefs = await RunAsync(["debug", "prefs"], cancellationToken).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(prefs.Output);
                return !(json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("OperatorUser", out var user)
                    && user.ValueKind == JsonValueKind.String && string.Equals(user.GetString(), host.UserName, StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                return true;
            }
        }
    }
}
