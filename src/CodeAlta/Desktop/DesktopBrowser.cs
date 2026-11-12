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

    /// <summary>
    /// The ports of CodeAlta on this computer: the MCP servers of the normal and the developer instances, the one this
    /// window asked for and the one it listens on, which differs when that one was taken (an instance on another profile).
    /// </summary>
    /// <param name="mcpPort">The port the MCP server of this window asked for; 0 for one the system picks.</param>
    /// <param name="listeningPort">The port the MCP server of this window listens on now, null while it does not.</param>
    internal static IReadOnlyList<int> ReservedPorts(int mcpPort, int? listeningPort) => ReservedPorts(mcpPort, listeningPort, []);

    /// <summary>The ports of CodeAlta on this computer, with those the other instances of the user recorded (<see cref="CodeAlta.Core.IO.McpPortRecord"/>).</summary>
    /// <param name="mcpPort">The port the MCP server of this window asked for; 0 for one the system picks.</param>
    /// <param name="listeningPort">The port the MCP server of this window listens on now, null while it does not.</param>
    /// <param name="others">The ports the MCP servers of the instances of this user listen on, whatever their profile.</param>
    internal static IReadOnlyList<int> ReservedPorts(int mcpPort, int? listeningPort, IEnumerable<int> others)
    {
        List<int> ports = [Mcp.DesktopMcpEndpoint.DefaultPort, Mcp.DesktopMcpEndpoint.DeveloperPort];
        if (mcpPort > 0 && !ports.Contains(mcpPort)) ports.Add(mcpPort);
        if (listeningPort is { } listening && listening > 0 && !ports.Contains(listening)) ports.Add(listening);
        foreach (var port in others)
        {
            if (port > 0 && !ports.Contains(port)) ports.Add(port);
        }

        return ports;
    }
}
