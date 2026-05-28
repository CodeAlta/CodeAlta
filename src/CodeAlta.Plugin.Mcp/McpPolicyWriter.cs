using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Plugin.Mcp;

internal sealed class McpPolicyWriter
{
    public async Task<McpPolicyMutationResult> SetServerEnabledAsync(
        string path,
        McpConfigScope scope,
        string serverKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        cancellationToken.ThrowIfCancellationRequested();

        var created = !File.Exists(path);
        var root = created
            ? new TomlTable()
            : TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) ?? new TomlTable();
        var serverTable = GetOrCreateServerPolicyTable(root, serverKey);
        serverTable["enabled"] = enabled;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, TomlSerializer.Serialize(root), cancellationToken).ConfigureAwait(false);
        return new McpPolicyMutationResult
        {
            Path = path,
            Scope = scope,
            Server = serverKey,
            Enabled = enabled,
            CreatedFile = created,
            Changed = true,
        };
    }

    public async Task<McpPolicyToolMutationResult> SetToolEnabledAsync(
        string path,
        McpConfigScope scope,
        string serverKey,
        string toolName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        cancellationToken.ThrowIfCancellationRequested();

        var created = !File.Exists(path);
        var root = created
            ? new TomlTable()
            : TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) ?? new TomlTable();
        var changed = false;
        var normalizedServer = serverKey.Trim();
        var normalizedTool = toolName.Trim();
        TomlArray disabledTools;
        var hasServerTable = TryGetServerPolicyTable(root, normalizedServer, out var existingServerTable);
        if (hasServerTable && existingServerTable is not null && existingServerTable.TryGetValue("disabled_tools", out var existingValue) && existingValue is TomlArray existingArray)
        {
            disabledTools = existingArray;
        }
        else
        {
            disabledTools = new TomlArray();
        }

        if (enabled)
        {
            for (var index = disabledTools.Count - 1; index >= 0; index--)
            {
                if (disabledTools[index] is string value && string.Equals(value, normalizedTool, StringComparison.Ordinal))
                {
                    disabledTools.RemoveAt(index);
                    changed = true;
                }
            }

            if (changed && existingServerTable is not null)
            {
                if (disabledTools.Count == 0)
                {
                    existingServerTable.Remove("disabled_tools");
                }
                else
                {
                    existingServerTable["disabled_tools"] = disabledTools;
                }
            }
        }
        else if (!disabledTools.OfType<string>().Contains(normalizedTool, StringComparer.Ordinal))
        {
            disabledTools.Add(normalizedTool);
            var serverTable = existingServerTable ?? GetOrCreateServerPolicyTable(root, normalizedServer);
            serverTable["disabled_tools"] = disabledTools;
            changed = true;
        }

        if (changed)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, TomlSerializer.Serialize(root), cancellationToken).ConfigureAwait(false);
        }

        return new McpPolicyToolMutationResult
        {
            Path = path,
            Scope = scope,
            Server = normalizedServer,
            Tool = normalizedTool,
            Enabled = enabled,
            DisabledTools = disabledTools.OfType<string>().ToArray(),
            CreatedFile = created && changed,
            Changed = changed,
        };
    }

    public static string GetGlobalPolicyPath(string? userHomeDirectory = null)
    {
        var mcpPath = McpConfigDiscovery.GetGlobalConfigPath(userHomeDirectory);
        return Path.Combine(Path.GetDirectoryName(mcpPath)!, "config.toml");
    }

    public static string GetProjectPolicyPath(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return Path.Combine(Path.GetFullPath(projectDirectory), ".alta", "config.toml");
    }

    private static TomlTable GetOrCreateServerPolicyTable(TomlTable root, string serverKey)
    {
        var plugins = GetOrCreateTable(root, "plugins");
        var mcp = GetOrCreateTable(plugins, "mcp");
        var servers = GetOrCreateTable(mcp, "servers");
        return GetOrCreateTable(servers, serverKey);
    }

    private static bool TryGetServerPolicyTable(TomlTable root, string serverKey, out TomlTable? table)
    {
        table = null;
        if (!root.TryGetValue("plugins", out var pluginsValue) || pluginsValue is not TomlTable plugins ||
            !plugins.TryGetValue("mcp", out var mcpValue) || mcpValue is not TomlTable mcp ||
            !mcp.TryGetValue("servers", out var serversValue) || serversValue is not TomlTable servers ||
            !servers.TryGetValue(serverKey, out var serverValue) || serverValue is not TomlTable server)
        {
            return false;
        }

        table = server;
        return true;
    }

    private static TomlTable GetOrCreateTable(TomlTable parent, string key)
    {
        if (parent.TryGetValue(key, out var value) && value is TomlTable table)
        {
            return table;
        }

        table = new TomlTable();
        parent[key] = table;
        return table;
    }
}

internal sealed record McpPolicyMutationResult
{
    public required string Path { get; init; }

    public required McpConfigScope Scope { get; init; }

    public required string Server { get; init; }

    public required bool Enabled { get; init; }

    public bool CreatedFile { get; init; }

    public bool Changed { get; init; }
}

internal sealed record McpPolicyToolMutationResult
{
    public required string Path { get; init; }

    public required McpConfigScope Scope { get; init; }

    public required string Server { get; init; }

    public required string Tool { get; init; }

    public required bool Enabled { get; init; }

    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    public bool CreatedFile { get; init; }

    public bool Changed { get; init; }
}
