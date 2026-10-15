using CodeAlta.Catalog;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Plugin.Mcp;

// Edits the policy of a server in config.toml through the host's configuration store: under the lock its own saves
// take, so that a change saved at the same time by another page is not written over, and in the text of the file, so
// that its comments and its other settings stay as they are.
internal sealed class McpPolicyWriter
{
    private const string EnabledKey = "enabled";
    private const string DisabledToolsKey = "disabled_tools";

    public Task<McpPolicyMutationResult> SetServerEnabledAsync(
        string path,
        McpConfigScope scope,
        string serverKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        cancellationToken.ThrowIfCancellationRequested();

        var created = false;
        var changed = CodeAltaConfigStore.EditConfigFile(path, content =>
        {
            created = !File.Exists(path);
            return EditServerPolicy(
                content,
                serverKey,
                (text, table) => ConfigTableSyntax.SetValue(text, table, EnabledKey, enabled),
                server => server[EnabledKey] = enabled);
        });

        return Task.FromResult(new McpPolicyMutationResult
        {
            Path = path,
            Scope = scope,
            Server = serverKey,
            Enabled = enabled,
            CreatedFile = created && changed,
            Changed = changed,
        });
    }

    public Task<McpPolicyToolMutationResult> SetToolEnabledAsync(
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

        var normalizedServer = serverKey.Trim();
        var normalizedTool = toolName.Trim();
        var created = false;
        IReadOnlyList<string> disabledTools = [];
        var changed = CodeAltaConfigStore.EditConfigFile(path, content =>
        {
            created = !File.Exists(path);
            var root = ParseTable(content);
            var current = TryGetServerPolicyTable(root, normalizedServer, out var existing) &&
                existing!.TryGetValue(DisabledToolsKey, out var value) && value is TomlArray array
                ? array.OfType<string>().ToArray()
                : [];
            disabledTools = enabled
                ? current.Where(tool => !string.Equals(tool, normalizedTool, StringComparison.Ordinal)).ToArray()
                : current.Contains(normalizedTool, StringComparer.Ordinal) ? current : [.. current, normalizedTool];
            if (disabledTools.Count == current.Length)
            {
                return content;
            }

            return EditServerPolicy(
                content,
                normalizedServer,
                (text, table) => ConfigTableSyntax.SetValue(text, table, DisabledToolsKey, disabledTools),
                server =>
                {
                    if (disabledTools.Count == 0)
                    {
                        server.Remove(DisabledToolsKey);
                    }
                    else
                    {
                        var tools = new TomlArray();
                        foreach (var tool in disabledTools) tools.Add(tool);
                        server[DisabledToolsKey] = tools;
                    }
                });
        });

        return Task.FromResult(new McpPolicyToolMutationResult
        {
            Path = path,
            Scope = scope,
            Server = normalizedServer,
            Tool = normalizedTool,
            Enabled = enabled,
            DisabledTools = disabledTools,
            CreatedFile = created && changed,
            Changed = changed,
        });
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

    // A policy written with a [plugins.mcp.servers.<key>] header is edited in the text. One written as an inline table
    // or with dotted keys is not: the file is then written again from its table model, which keeps every setting but
    // not the comments.
    private static string EditServerPolicy(string content, string serverKey, Func<string, string[], string> editText, Action<TomlTable> editTable)
    {
        try
        {
            return editText(content, ["plugins", "mcp", "servers", serverKey]);
        }
        catch (InvalidDataException)
        {
            var root = ParseTable(content);
            editTable(GetOrCreateServerPolicyTable(root, serverKey));
            return TomlSerializer.Serialize(root);
        }
    }

    private static TomlTable ParseTable(string content)
    {
        try
        {
            return string.IsNullOrWhiteSpace(content) ? new TomlTable() : TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable();
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException("The configuration is not valid TOML.", exception);
        }
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
