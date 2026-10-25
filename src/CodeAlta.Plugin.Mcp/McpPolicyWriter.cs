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
    private const string DisabledSkillsKey = "disabled_skills";

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
        var (changed, created, disabledTools) = SetListEntry(path, normalizedServer, DisabledToolsKey, normalizedTool, enabled);
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

    // Turns a skill of a server on or off: `disabled_skills` names it by its URI, which no other skill of the server has.
    public Task<McpPolicyMutationResult> SetSkillEnabledAsync(
        string path,
        McpConfigScope scope,
        string serverKey,
        string skillUri,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillUri);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedServer = serverKey.Trim();
        var (changed, created, _) = SetListEntry(path, normalizedServer, DisabledSkillsKey, skillUri.Trim(), enabled);
        return Task.FromResult(new McpPolicyMutationResult
        {
            Path = path,
            Scope = scope,
            Server = normalizedServer,
            Enabled = enabled,
            CreatedFile = created && changed,
            Changed = changed,
        });
    }

    // Adds an entry to a list of the policy of a server, to turn something off, or removes it, to turn it on again.
    private static (bool Changed, bool Created, IReadOnlyList<string> List) SetListEntry(string path, string serverKey, string key, string entry, bool enabled)
    {
        var created = false;
        IReadOnlyList<string> list = [];
        var changed = CodeAltaConfigStore.EditConfigFile(path, content =>
        {
            created = !File.Exists(path);
            var root = ParseTable(content);
            var current = TryGetServerPolicyTable(root, serverKey, out var existing) &&
                existing!.TryGetValue(key, out var value) && value is TomlArray array
                ? array.OfType<string>().ToArray()
                : [];
            list = enabled
                ? current.Where(item => !string.Equals(item, entry, StringComparison.Ordinal)).ToArray()
                : current.Contains(entry, StringComparer.Ordinal) ? current : [.. current, entry];
            if (list.Count == current.Length)
            {
                return content;
            }

            return EditServerPolicy(
                content,
                serverKey,
                (text, table) => ConfigTableSyntax.SetValue(text, table, key, list),
                server =>
                {
                    if (list.Count == 0)
                    {
                        server.Remove(key);
                    }
                    else
                    {
                        var items = new TomlArray();
                        foreach (var item in list) items.Add(item);
                        server[key] = items;
                    }
                });
        });

        return (changed, created, list);
    }

    public static string GetGlobalPolicyPath(McpHome home = default)
        => Path.Combine(home.Root, "config.toml");

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
