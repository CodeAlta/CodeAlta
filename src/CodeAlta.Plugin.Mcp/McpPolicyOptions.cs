using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace CodeAlta.Plugin.Mcp;

internal sealed record McpPolicyOptions
{
    public bool Enabled { get; init; } = true;

    public bool ConnectOnStartup { get; init; } = true;

    public int StartupTimeoutMs { get; init; } = 30000;

    public int ToolTimeoutMs { get; init; } = 60000;

    public int MaxToolOutputChars { get; init; } = 120000;

    public bool DiscoverInPrompt { get; init; } = true;

    public int PromptMaxServers { get; init; } = 10;

    public int PromptMaxTools { get; init; } = 20;

    public string DirectExposure { get; init; } = "auto";

    public int DirectToolThreshold { get; init; } = 40;

    public string ConfigScopes { get; init; } = "auto";

    public string PreferredWriteScope { get; init; } = "project";

    public IReadOnlyDictionary<string, McpServerPolicyOptions> Servers { get; init; } = new Dictionary<string, McpServerPolicyOptions>(StringComparer.Ordinal);

    // The spaces of the servers that list some, by key without case, read from the global configuration only: a
    // project cannot widen a space. The rule is the host's (CodeAlta.Catalog.SpaceScope), which this plugin does not
    // reference: no list is every space, a list is only those spaces, the default space only when it is listed.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ServerSpaces { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public const string DefaultSpaceId = "default";

    // Whether the policy turns off a skill of a server.
    public bool IsSkillDisabled(string server, string uri, string name)
        => Servers.TryGetValue(server, out var serverPolicy) && serverPolicy.IsSkillDisabled(uri, name);

    // Whether a server is available in a space; a null space applies none (the user, outside any session).
    public bool IsServerInSpace(string key, string? spaceId)
    {
        if (spaceId is null || !ServerSpaces.TryGetValue(key, out var spaces) || spaces.Count == 0) return true;
        var space = string.IsNullOrWhiteSpace(spaceId) ? DefaultSpaceId : spaceId.Trim();
        return spaces.Contains(space, StringComparer.Ordinal);
    }
}

internal sealed record McpServerPolicyOptions
{
    public bool? Enabled { get; init; }

    public bool? Required { get; init; }

    public string? DirectExposure { get; init; }

    public IReadOnlyList<string> DirectTools { get; init; } = [];

    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    // The skills of the server the user turned off, by URI (what Settings writes) or by name (one a person may write).
    public IReadOnlyList<string> DisabledSkills { get; init; } = [];

    // Whether the policy turns a skill of the server off.
    public bool IsSkillDisabled(string uri, string name)
        => DisabledSkills.Contains(uri, StringComparer.Ordinal) || DisabledSkills.Contains(name, StringComparer.Ordinal);

    public int? StartupTimeoutMs { get; init; }

    public int? ToolTimeoutMs { get; init; }
}

internal sealed class McpPolicyLoader
{
    // The user's configuration as the host reads it (McpPluginOptions.GlobalConfigContent), or null to read its file.
    private readonly Func<string?>? _globalContent;

    public McpPolicyLoader()
        : this(globalContent: null)
    {
    }

    public McpPolicyLoader(Func<string?>? globalContent) => _globalContent = globalContent;

    public McpPolicyOptions Load(string? globalConfigPath, string? projectConfigPath)
        => LoadCore(globalConfigPath, projectConfigPath, bounded: false);

    // Inventory callers do not need writability or unrestricted policy text; existing plugin callers keep Load semantics.
    internal McpPolicyOptions LoadBoundedForInventory(string? globalConfigPath, string? projectConfigPath)
        => LoadCore(globalConfigPath, projectConfigPath, bounded: true);

    private McpPolicyOptions LoadCore(string? globalConfigPath, string? projectConfigPath, bool bounded)
    {
        var policy = new McpPolicyOptions();
        policy = _globalContent is { } globalContent
            ? Apply(policy, globalContent(), global: true)
            : ApplyFile(policy, globalConfigPath, bounded, global: true);
        policy = ApplyFile(policy, projectConfigPath, bounded, global: false);
        return policy;
    }

    private static McpPolicyOptions ApplyFile(McpPolicyOptions policy, string? path, bool bounded, bool global)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return policy;
        }

        return Apply(policy, bounded ? McpBoundedTextReader.Read(path) : File.ReadAllText(path), global);
    }

    private static McpPolicyOptions Apply(McpPolicyOptions policy, string? text, bool global)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return policy;
        }

        var model = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        if (!TryGetTable(model, "plugins", out var plugins) || !TryGetTable(plugins, "mcp", out var mcp))
        {
            return policy;
        }

        policy = policy with
        {
            Enabled = GetBool(mcp, "enabled") ?? policy.Enabled,
            ConnectOnStartup = GetBool(mcp, "connect_on_startup") ?? policy.ConnectOnStartup,
            StartupTimeoutMs = GetInt(mcp, "startup_timeout_ms") ?? policy.StartupTimeoutMs,
            ToolTimeoutMs = GetInt(mcp, "tool_timeout_ms") ?? policy.ToolTimeoutMs,
            MaxToolOutputChars = GetInt(mcp, "max_tool_output_chars") ?? policy.MaxToolOutputChars,
            DiscoverInPrompt = GetBool(mcp, "discover_in_prompt") ?? policy.DiscoverInPrompt,
            PromptMaxServers = GetInt(mcp, "prompt_max_servers") ?? policy.PromptMaxServers,
            PromptMaxTools = GetInt(mcp, "prompt_max_tools") ?? policy.PromptMaxTools,
            DirectExposure = GetString(mcp, "direct_exposure") ?? policy.DirectExposure,
            DirectToolThreshold = GetInt(mcp, "direct_tool_threshold") ?? policy.DirectToolThreshold,
            ConfigScopes = GetString(mcp, "config_scopes") ?? policy.ConfigScopes,
            PreferredWriteScope = GetString(mcp, "preferred_write_scope") ?? policy.PreferredWriteScope,
            Servers = MergeServers(policy.Servers, mcp),
            ServerSpaces = global ? ReadServerSpaces(mcp) : policy.ServerSpaces,
        };
        return policy;
    }

    // [plugins.mcp.servers.<key>] spaces = [...]: a list of strings, or one string; each identifier once, trimmed.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadServerSpaces(TomlTable mcp)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetTable(mcp, "servers", out var servers)) return result;
        foreach (var (key, value) in servers)
        {
            if (value is not TomlTable table || !table.TryGetValue("spaces", out var node)) continue;
            IEnumerable<string?> values = node switch
            {
                TomlArray array => array.OfType<string>(),
                string one => [one],
                _ => [],
            };
            var list = new List<string>();
            foreach (var text in values)
            {
                var id = text?.Trim();
                if (!string.IsNullOrEmpty(id) && !list.Contains(id, StringComparer.Ordinal)) list.Add(id);
            }

            if (list.Count > 0) result[key.Trim()] = list;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, McpServerPolicyOptions> MergeServers(IReadOnlyDictionary<string, McpServerPolicyOptions> current, TomlTable mcp)
    {
        var result = new Dictionary<string, McpServerPolicyOptions>(current, StringComparer.Ordinal);
        if (!TryGetTable(mcp, "servers", out var servers))
        {
            return result;
        }

        foreach (var (key, value) in servers)
        {
            if (value is not TomlTable table)
            {
                continue;
            }

            result.TryGetValue(key, out var existing);
            existing ??= new McpServerPolicyOptions();
            result[key] = existing with
            {
                Enabled = GetBool(table, "enabled") ?? existing.Enabled,
                Required = GetBool(table, "required") ?? existing.Required,
                DirectExposure = GetString(table, "direct_exposure") ?? existing.DirectExposure,
                DirectTools = GetStringArray(table, "direct_tools") ?? existing.DirectTools,
                AllowedTools = GetStringArray(table, "allowed_tools") ?? existing.AllowedTools,
                DisabledTools = GetStringArray(table, "disabled_tools") ?? existing.DisabledTools,
                DisabledSkills = GetStringArray(table, "disabled_skills") ?? existing.DisabledSkills,
                StartupTimeoutMs = GetInt(table, "startup_timeout_ms") ?? existing.StartupTimeoutMs,
                ToolTimeoutMs = GetInt(table, "tool_timeout_ms") ?? existing.ToolTimeoutMs,
            };
        }

        return result;
    }

    private static bool TryGetTable(TomlTable table, string key, out TomlTable value)
    {
        if (table.TryGetValue(key, out var node) && node is TomlTable nested)
        {
            value = nested;
            return true;
        }

        value = new TomlTable();
        return false;
    }

    private static string? GetString(TomlTable table, string key)
        => table.TryGetValue(key, out var value) ? value as string : null;

    private static bool? GetBool(TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is bool boolValue ? boolValue : null;

    private static int? GetInt(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            _ => null,
        };
    }

    private static IReadOnlyList<string>? GetStringArray(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not TomlArray array)
        {
            return null;
        }

        return array.OfType<string>().ToArray();
    }
}
