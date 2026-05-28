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
}

internal sealed record McpServerPolicyOptions
{
    public bool? Enabled { get; init; }

    public bool? Required { get; init; }

    public string? DirectExposure { get; init; }

    public IReadOnlyList<string> DirectTools { get; init; } = [];

    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    public int? StartupTimeoutMs { get; init; }

    public int? ToolTimeoutMs { get; init; }
}

internal sealed class McpPolicyLoader
{
    public McpPolicyOptions Load(string? globalConfigPath, string? projectConfigPath)
    {
        var policy = new McpPolicyOptions();
        policy = ApplyFile(policy, globalConfigPath);
        policy = ApplyFile(policy, projectConfigPath);
        return policy;
    }

    private static McpPolicyOptions ApplyFile(McpPolicyOptions policy, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return policy;
        }

        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
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
        };
        return policy;
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
