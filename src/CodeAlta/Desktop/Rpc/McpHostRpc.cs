using CodeAlta.Desktop.Mcp;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the MCP server of the application: whether it runs, the address clients connect to, the
/// tools it offers, and the switch that turns it on and off.
/// </summary>
/// <remarks>
/// This is the server other applications connect to. The MCP servers CodeAlta connects to are another
/// service, <c>mcpServers</c>.
/// </remarks>
[NeoRpcService("mcpHost", Version = 1)]
internal sealed class McpHostService
{
    private readonly DesktopMcpServer? _server;
    private readonly Func<IReadOnlyList<DesktopMcpTool>>? _tools;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal McpHostService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="server">The MCP server of the application.</param>
    /// <param name="tools">The tools it offers.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal McpHostService(DesktopMcpServer server, Func<IReadOnlyList<DesktopMcpTool>> tools, string epoch)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_server, _tools, _epoch) = (server, tools, epoch);
    }

    /// <summary>Says what the server is doing.</summary>
    [NeoRpcMethod("status")]
    public McpHostResponse Status(McpHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Refuse(request.ExpectedEpoch) is { } refused ? Refused(refused) : Describe();
    }

    /// <summary>Turns the server on or off, now and for the next starts, and says what it is doing then.</summary>
    [NeoRpcMethod("setEnabled", TimeoutMilliseconds = 30_000)]
    public async Task<McpHostResponse> SetEnabledAsync(McpHostEnableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refused(refused);
        // The server finishes what it started whatever becomes of this call.
        await _server!.SetEnabledAsync(request.Enabled).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Describe();
    }

    private McpHostResponse Describe()
    {
        var status = _server!.Status;
        return new McpHostResponse("ok", status.State, status.Enabled, status.Url, status.Token, status.Error,
            status.State == DesktopMcpStatus.Running ? [.. _tools!().Select(static tool => tool.Name)] : []);
    }

    private static McpHostResponse Refused(string status) => new(status, DesktopMcpStatus.Stopped, false, null, null, null, []);

    private string? Refuse(string? expectedEpoch)
        => _server is null || _tools is null ? "unavailable"
            : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
}

/// <summary>A request about the MCP server of the application.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
internal sealed record McpHostRequest(string? ExpectedEpoch);

/// <summary>The request that turns the MCP server on or off.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
/// <param name="Enabled">Whether the server is to run.</param>
internal sealed record McpHostEnableRequest(string? ExpectedEpoch, bool Enabled);

/// <summary>What the MCP server of the application is doing.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> (this launch has no server) or <c>stale_epoch</c>.</param>
/// <param name="State"><c>running</c>, <c>stopped</c> (turned off) or <c>failed</c> (turned on, and not listening).</param>
/// <param name="Enabled">Whether the server is turned on.</param>
/// <param name="Url">The address clients connect to while it runs.</param>
/// <param name="Token">The access token clients send as a bearer token, when the server asks for one.</param>
/// <param name="Error">Why it is not listening, when it failed.</param>
/// <param name="Tools">The names of the tools it offers while it runs.</param>
internal sealed record McpHostResponse(string Status, string State, bool Enabled, string? Url, string? Token, string? Error, string[] Tools);
