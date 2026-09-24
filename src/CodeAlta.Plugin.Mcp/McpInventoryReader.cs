namespace CodeAlta.Plugin.Mcp;

/// <summary>A bounded configured MCP server descriptor; not evidence of a runtime connection.</summary>
/// <param name="Name">Configuration key.</param>
/// <param name="Scope">Scope supplying the effective definition.</param>
/// <param name="Transport">Configured transport.</param>
/// <param name="Enabled">Effective policy state, or unknown if policy could not be read.</param>
/// <param name="OverridesGlobal">Whether the project definition shadows a global definition.</param>
public sealed record McpInventoryServer(string Name, string Scope, string Transport, bool? Enabled, bool OverridesGlobal);

/// <summary>Read-only effective inventory and source read evidence, without paths or diagnostics.</summary>
/// <param name="Servers">Bounded effective definitions.</param>
/// <param name="Sources">Source scope and read state, without file paths.</param>
/// <param name="PolicyReadError">Whether configured enabled state is unknown.</param>
/// <param name="Omitted">Number of definitions excluded by inventory bounds.</param>
public sealed record McpInventory(IReadOnlyList<McpInventoryServer> Servers, IReadOnlyList<string> Sources, bool PolicyReadError, int Omitted);

/// <summary>Reads fixed global/project MCP configuration without plugin activation or writability probes.</summary>
public sealed class McpInventoryReader
{
    /// <summary>Reads the project overlay on global configuration. Callers must authorize the paths.</summary>
    /// <exception cref="ArgumentException">A supplied path is invalid.</exception>
    public McpInventory Read(string? projectDirectory, string? userHomeDirectory = null)
    {
        var config = new McpConfigDiscovery().Discover(new McpConfigPathOptions
        {
            ProjectDirectory = projectDirectory, UserHomeDirectory = userHomeDirectory, ProbeWritability = false,
        });
        var sources = config.Sources.Select(source => $"{source.Scope}: {(source.Exists ? source.IsValid ? "read" : "read_error" : "missing")}").ToArray();
        McpPolicyOptions? policy;
        try
        {
            policy = new McpPolicyLoader().LoadBoundedForInventory(McpPolicyWriter.GetGlobalPolicyPath(userHomeDirectory),
                projectDirectory is null ? null : McpPolicyWriter.GetProjectPolicyPath(projectDirectory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            policy = null;
        }
        var rows = new List<McpInventoryServer>();
        var omitted = 0;
        foreach (var server in config.EffectiveServers)
        {
            var name = server.Definition.Key;
            // Only opaque configuration identifiers, never a URL/path/command embedded as a key.
            if (rows.Count >= 64 || name.Length is < 1 or > 128 ||
                name.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
            { omitted++; continue; }
            McpServerPolicyOptions? serverPolicy = null;
            policy?.Servers.TryGetValue(name, out serverPolicy);
            rows.Add(new(name, server.Definition.SourceScope.ToString(), server.Definition.Transport.ToString(),
                policy is null ? null : policy.Enabled && (serverPolicy?.Enabled ?? true), server.OverridesGlobal));
        }
        return new(rows, sources, policy is null, omitted);
    }
}
