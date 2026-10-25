namespace CodeAlta.Plugin.Mcp;

public sealed partial class McpPlugin
{
    /// <summary>
    /// Gets the servers the policy marks required: <c>[plugins.mcp.servers.&lt;key&gt;] required = true</c>, in the user's or the
    /// project's <c>config.toml</c>.
    /// </summary>
    /// <param name="projectDirectory">The project whose configuration is read with the user's; null for the user's alone.</param>
    /// <returns>Their keys, in order.</returns>
    /// <exception cref="IOException">A configuration file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A configuration file cannot be read.</exception>
    /// <exception cref="Tomlyn.TomlException">A configuration file is not valid TOML.</exception>
    public IReadOnlyList<string> GetRequiredServers(string? projectDirectory)
        => RequiredServersOf(LoadPolicy(string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFullPath(projectDirectory)));

    /// <summary>
    /// Starts the servers the policy marks required (<see cref="GetRequiredServers"/>), each within its startup timeout, lists
    /// their tools, and closes them again: for a host that must not run with fewer servers than it was set up with. A required
    /// server that MCP or its own policy turns off, or that no configuration defines, fails too.
    /// </summary>
    /// <param name="projectDirectory">The project whose configuration is read with the user's; null for the user's alone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The required servers that could not start, by key; empty when every one started or none is required.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<IReadOnlyList<McpRequiredServerFailure>> CheckRequiredServersAsync(string? projectDirectory, CancellationToken cancellationToken)
    {
        var projectPath = string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFullPath(projectDirectory);
        McpPolicyOptions policy;
        try
        {
            policy = LoadPolicy(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            return [new McpRequiredServerFailure("config.toml", "policy_unreadable", "The MCP policy cannot be read: " + exception.GetBaseException().Message)];
        }

        var required = RequiredServersOf(policy);
        if (required.Count == 0) return [];
        if (!policy.Enabled)
        {
            return [.. required.Select(key => new McpRequiredServerFailure(key, "server_disabled", $"MCP server '{key}' is required, but MCP is turned off ([plugins.mcp] enabled = false)."))];
        }

        // One runtime for each server, closed once it answered: the sessions start their own when they activate it.
        var checks = required.Select(async key =>
        {
            await using var runtime = _connections.CreateRuntime(_home);
            var result = await runtime.TestServerAsync(new McpRuntimeRequest { ProjectDirectory = projectPath, Home = _home }, key, cancellationToken)
                .ConfigureAwait(false);
            return result.Diagnostics.FirstOrDefault() is { } diagnostic ? new McpRequiredServerFailure(key, diagnostic.Code, diagnostic.Message) : null;
        });
        var failures = await Task.WhenAll(checks).ConfigureAwait(false);
        return [.. failures.OfType<McpRequiredServerFailure>()];
    }

    private static IReadOnlyList<string> RequiredServersOf(McpPolicyOptions policy)
        => [.. policy.Servers.Where(static entry => entry.Value.Required == true).Select(static entry => entry.Key).Order(StringComparer.Ordinal)];
}
