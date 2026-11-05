using System.Net;

namespace CodeAlta.Desktop.Mcp;

/// <summary>This computer on a private network: the name clients reach it by, and its addresses there.</summary>
/// <param name="Name">Its name there (its MagicDNS name on a tailnet), or its first address when it has no name.</param>
/// <param name="Addresses">Its addresses there, IPv4 first.</param>
/// <param name="Network">The name of the network, for people.</param>
internal sealed record DesktopTailnetNode(string Name, IReadOnlyList<IPAddress> Addresses, string Network = "Tailscale");

/// <summary>
/// Whether the MCP server listens on the Tailscale network, and how it finds this computer there. The choice is the
/// user's; the server keeps it for the next starts through <paramref name="Remember"/>.
/// </summary>
/// <param name="Enabled">Whether the server listens on the tailnet when this computer is on one.</param>
/// <param name="Remember">Keeps the choice for the next starts.</param>
/// <param name="FindAsync">Finds this computer on its tailnet; null when Tailscale is not installed or not running.</param>
internal sealed record DesktopTailnetChoice(bool Enabled, Action<bool>? Remember, Func<CancellationToken, Task<DesktopTailnetNode?>> FindAsync);

/// <summary>
/// Asks the Tailscale network of this computer (<see cref="CodeAlta.Plugins.PrivateNetworks.TailscaleNetwork"/>) where the computer is on
/// its tailnet. Nothing is changed in Tailscale: the server listens on the addresses of the tailnet itself.
/// </summary>
internal static class DesktopTailnet
{
    /// <summary>Finds this computer on its tailnet; null when Tailscale is not installed, not running or does not answer.</summary>
    /// <param name="cancellationToken">Stops the search.</param>
    internal static async Task<DesktopTailnetNode?> FindAsync(CancellationToken cancellationToken)
        => Of(await new CodeAlta.Plugins.PrivateNetworks.TailscaleNetwork().FindThisComputerAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Reads the answer of <c>tailscale status --json</c>; null when Tailscale does not run or this computer has no address.</summary>
    /// <param name="json">What the command wrote.</param>
    internal static DesktopTailnetNode? Parse(string json) => Of(CodeAlta.Plugins.PrivateNetworks.TailscaleNetwork.Parse(json));

    private static DesktopTailnetNode? Of(CodeAlta.Plugins.Abstractions.PrivateNetworkPresence? presence)
        => presence is null ? null : new DesktopTailnetNode(presence.Name, presence.Addresses);
}
