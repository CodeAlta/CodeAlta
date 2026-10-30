namespace CodeAlta.Plugin.Mcp;

/// <summary>A tool of an MCP server as a session sees it.</summary>
/// <param name="Name">Its name on the server.</param>
/// <param name="Alias">The name a session calls it by (<c>mcp__server__tool</c>).</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="Description">What it does, or null.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Enabled">Whether the policy lets a session call it.</param>
/// <param name="Active">Whether the session activated it: its server as a whole, or this tool.</param>
public sealed record McpSessionToolView(string Name, string Alias, string? Title, string? Description, IReadOnlyList<McpCachedArgument> Arguments, bool Enabled, bool Active);

/// <summary>An MCP server of a session's project as the session sees it, with its tools as CodeAlta last listed them.</summary>
/// <param name="Server">The server in the configuration snapshot.</param>
/// <param name="Activated">Whether the session activated the server as a whole.</param>
/// <param name="InUse">Whether the session uses the server: activated as a whole, or with a tool activated.</param>
/// <param name="ActiveToolCount">The number of its tools the last run of the session was given, or null when no run listed them.</param>
/// <param name="ListedAt">When CodeAlta last listed its tools, or null when it never did for its definition now.</param>
/// <param name="Tools">Its tools, by name; empty when they were never listed.</param>
public sealed record McpSessionServerView(McpManagementServerSnapshot Server, bool Activated, bool InUse, int? ActiveToolCount, DateTimeOffset? ListedAt, IReadOnlyList<McpSessionToolView> Tools);

public sealed partial class McpPlugin
{
    // What the sessions activated, for a test.
    internal McpActivationState ActivationState => _activationState;

    /// <summary>
    /// Describes the servers of a configuration snapshot for one session of the host that runs this plugin: those the
    /// session activated, the tools it has and the tools it could activate, from the lists CodeAlta keeps of their
    /// tools. No server is started.
    /// </summary>
    /// <param name="snapshot">The configuration snapshot of the session's project.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for the project as a whole.</param>
    /// <returns>The servers in effect (enabled or disabled by policy), by key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<McpSessionServerView> DescribeSession(McpManagementSnapshot snapshot, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = McpActivationState.ResolveScopeKey(sessionId, snapshot.ProjectDirectory);
        return DescribeServers(snapshot, _configCache, _home, _activationState, scope);
    }

    /// <summary>
    /// Describes the servers of a configuration snapshot without a session, for a host that does not run this plugin:
    /// no server is activated, and their tools are the ones CodeAlta last listed. No server is started.
    /// </summary>
    /// <param name="snapshot">The configuration snapshot.</param>
    /// <param name="userHomeDirectory">The folder of the user that holds the global <c>.alta</c> folder, or null for the profile.</param>
    /// <returns>The servers in effect (enabled or disabled by policy), by key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<McpSessionServerView> DescribeServers(McpManagementSnapshot snapshot, string? userHomeDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return DescribeServers(snapshot, new McpConfigCache(), new McpHome(userHomeDirectory, null), null, null);
    }

    private static IReadOnlyList<McpSessionServerView> DescribeServers(McpManagementSnapshot snapshot, McpConfigCache configCache, McpHome home,
        McpActivationState? activation, string? scope)
    {
        // The definitions as they are now: a list of tools kept for another definition of a server is stale.
        var definitions = configCache.Discover(snapshot.ProjectDirectory, home).EffectiveServers
            .GroupBy(static server => server.Definition.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Definition, StringComparer.Ordinal);
        var cache = new McpToolListCache(McpToolListCache.FolderOf(home));
        var used = activation is null || scope is null ? [] : activation.GetUsedServers(scope).ToHashSet(StringComparer.Ordinal);
        var whole = activation is null || scope is null ? [] : activation.GetActiveServers(scope).ToHashSet(StringComparer.Ordinal);
        var counts = activation is null || scope is null ? new Dictionary<string, int>() : activation.GetToolCounts(scope);
        var views = new List<McpSessionServerView>();
        foreach (var server in snapshot.Servers.Where(static server => server.State is McpManagementServerState.Configured or McpManagementServerState.Disabled)
                     .OrderBy(static server => server.Key, StringComparer.OrdinalIgnoreCase))
        {
            var list = definitions.TryGetValue(server.Key, out var definition) ? cache.Read(server.Key, McpToolListCache.Fingerprint(definition)) : null;
            var serverEnabled = server.PolicyEnabled != false;
            var tools = (list?.Tools ?? [])
                // A tool only the view of its server calls (MCP Apps) is not the model's.
                .Where(static tool => McpApps.IsVisibleToModel(tool.Visibility))
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(tool =>
                {
                    var enabled = serverEnabled && ToolAllowed(server, tool.Name);
                    var active = enabled && activation is not null && scope is not null && activation.IsActive(scope, server.Key, tool.Name);
                    return new McpSessionToolView(tool.Name, tool.Alias, tool.Title, tool.Description, tool.Arguments, enabled, active);
                })
                .ToArray();
            views.Add(new McpSessionServerView(server, whole.Contains(server.Key), used.Contains(server.Key),
                counts.TryGetValue(server.Key, out var count) ? count : null, list?.ListedAt, tools));
        }

        return views;
    }

    private static bool ToolAllowed(McpManagementServerSnapshot server, string tool)
        => (server.AllowedTools.Count == 0 || server.AllowedTools.Contains(tool, StringComparer.Ordinal))
            && !server.DisabledTools.Contains(tool, StringComparer.Ordinal);
}
