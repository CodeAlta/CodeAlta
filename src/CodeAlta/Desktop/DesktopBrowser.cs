using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Browser;

namespace CodeAlta.Desktop;

/// <summary>What the window gives the browser plugin of a session: its sandbox, and the ports of CodeAlta it never reaches.</summary>
internal static class DesktopBrowser
{
    /// <summary>The sandbox of a session as its browser keeps to it: its folders, and a proxy of the network of its profile.</summary>
    /// <param name="reach">What the sandbox of the session reaches; null for a session that is not sandboxed.</param>
    internal static BrowserSandbox? Sandbox(SessionSandboxReach? reach)
        => reach is null
            ? null
            : new BrowserSandbox(reach.Profile, reach.Read, reach.Write, _ =>
            {
                var proxy = reach.StartProxy();
                return ValueTask.FromResult(new BrowserSandboxProxy(new Uri(proxy.Endpoint).Authority, proxy));
            })
            {
                Denied = reach.Denied,
                ReadOnly = reach.Protected,
                Fingerprint = reach.Fingerprint,
            };

    /// <summary>The ports of CodeAlta on this computer: the MCP servers of the window and of the developer instance.</summary>
    /// <param name="mcpPort">The port of the MCP server of this window; 0 for one the system picks.</param>
    internal static IReadOnlyList<int> ReservedPorts(int mcpPort) => mcpPort > 0 ? [2582, 2583, mcpPort] : [2582, 2583];
}
