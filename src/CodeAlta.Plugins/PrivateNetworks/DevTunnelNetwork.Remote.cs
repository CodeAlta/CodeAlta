using System.Globalization;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.PrivateNetworks;

internal sealed partial class DevTunnelNetwork
{
    /// <inheritdoc />
    public bool OwnsAddress(Uri address) => address.Scheme == Uri.UriSchemeHttps && address.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<PrivateNetworkRemoteResult?> CheckRemoteAsync(IPrivateNetworkRemoteHost host, Uri address, int localPort, bool publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(address);
        var number = localPort.ToString(CultureInfo.InvariantCulture);
        var instructions = $"On the server's machine, devtunnel host must forward to http://127.0.0.1:{number}. "
            + $"Use devtunnel port create <tunnel-id> -p {number} --protocol http, then devtunnel host <tunnel-id>. "
            + "Devices need the tunnel account's sign-in or a connect token in X-Tunnel-Authorization: tunnel <token>; "
            + "altanode cannot use a browser sign-in. Keep anonymous access disabled.";
        if (!await host.IsInstalledAsync("devtunnel", cancellationToken).ConfigureAwait(false))
            return new($"{instructions}\nInstall devtunnel there: {DevTunnel.InstallUrl}", "devtunnel_host_missing");
        var user = await host.RunAsync("devtunnel", ["user", "show"], null, cancellationToken).ConfigureAwait(false);
        if (user.ExitCode != 0 || !DevTunnel.IsSignedIn(new(user.ExitCode, user.Output, user.TimedOut)))
            return new($"{instructions}\nSign in there with devtunnel user login --github --use-device-code.", "devtunnel_host_missing");

        // Reading the tunnel on the server's machine distinguishes an unknown tunnel from one whose host/access is missing.
        // A web-forwarding name is <id>-<port>.<cluster>.devtunnels.ms (older names omit the cluster).
        var labels = address.Host[..^".devtunnels.ms".Length].Split('.');
        var dash = labels[0].LastIndexOf('-');
        if (dash > 0 && int.TryParse(labels[0].AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            var id = labels[0][..dash] + (labels.Length > 1 ? "." + labels[1] : "");
            var shown = await host.RunAsync("devtunnel", ["show", id, "--json", "--nologo"], null, cancellationToken).ConfigureAwait(false);
            if (shown.ExitCode != 0 || shown.TimedOut)
                return new($"{instructions}\nThe account on this machine could not read that tunnel.", "devtunnel_host_missing");
            try
            {
                using var json = JsonDocument.Parse(shown.Output);
                if (json.RootElement.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array
                    && !ports.EnumerateArray().Any(port => port.TryGetProperty("portNumber", out var value) && value.TryGetInt32(out var portNumber) && portNumber == localPort))
                    instructions += $"\nThe tunnel does not publish port {number}.";
            }
            catch (JsonException)
            {
                // An unreadable CLI response proves no publication: keep the instructions, do not change its access.
            }
        }

        return new(instructions, "devtunnel_host_missing");
    }
}
