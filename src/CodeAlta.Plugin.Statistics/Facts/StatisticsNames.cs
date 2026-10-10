using System.Collections.Frozen;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// The rule that names the bucket of a tool call, shared by the cards of the timeline and the facts of the history, and the
/// fixed list of kinds a tool belongs to.
/// </summary>
internal static class StatisticsToolBuckets
{
    private static readonly FrozenDictionary<string, ToolKind> KindsByName = new Dictionary<string, ToolKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = ToolKind.Files,
        ["write_file"] = ToolKind.Files,
        ["replace_in_file"] = ToolKind.Files,
        ["apply_patch"] = ToolKind.Files,
        ["delete_file_or_dir"] = ToolKind.Files,
        ["rename_file_or_dir"] = ToolKind.Files,
        ["list_dir"] = ToolKind.Files,
        ["view_image"] = ToolKind.Files,
        ["read"] = ToolKind.Files,
        ["write"] = ToolKind.Files,
        ["edit"] = ToolKind.Files,
        ["multiedit"] = ToolKind.Files,
        ["notebookedit"] = ToolKind.Files,
        ["ls"] = ToolKind.Files,
        ["grep"] = ToolKind.Search,
        ["glob"] = ToolKind.Search,
        ["toolsearch"] = ToolKind.Search,
        ["tool_search"] = ToolKind.Search,
        ["webget"] = ToolKind.Web,
        ["webfetch"] = ToolKind.Web,
        ["websearch"] = ToolKind.Web,
        ["alta"] = ToolKind.Alta,
        ["skill"] = ToolKind.Skill,
        ["codealta_skills_activate"] = ToolKind.Skill,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names the bucket of a tool call: <c>shell</c> for a command, <c>&lt;ActivityKind&gt;:&lt;name&gt;</c> for the others. This
    /// is the rule the statistics cards of the timeline have always used.
    /// </summary>
    /// <param name="kind">The activity kind.</param>
    /// <param name="name">The name of the tool.</param>
    /// <returns>The bucket.</returns>
    public static string Bucket(AgentActivityKind kind, string? name)
    {
        if (kind == AgentActivityKind.CommandExecution ||
            Contains(name, "shell") ||
            Contains(name, "command") ||
            Contains(name, "bash") ||
            Contains(name, "pwsh") ||
            Contains(name, "powershell"))
        {
            return "shell";
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? kind.ToString() : name.Trim();
        return FormattableString.Invariant($"{kind}:{normalizedName}");

        static bool Contains(string? text, string value)
            => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Tells the kind a tool belongs to, in the fixed list of the statistics.</summary>
    /// <param name="kind">The activity kind.</param>
    /// <param name="name">The name of the tool.</param>
    /// <returns>The kind; <see cref="ToolKind.Other"/> for a tool the list does not know.</returns>
    public static ToolKind KindOf(AgentActivityKind kind, string? name)
    {
        if (Bucket(kind, name) == "shell")
        {
            return ToolKind.Shell;
        }

        switch (kind)
        {
            case AgentActivityKind.FileChange:
                return ToolKind.Files;
            case AgentActivityKind.WebSearch:
                return ToolKind.Web;
            case AgentActivityKind.Skill:
                return ToolKind.Skill;
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return kind == AgentActivityKind.McpToolCall ? ToolKind.Mcp : ToolKind.Other;
        }

        if (TrySplitMcp(trimmed, out _, out var mcpTool))
        {
            // The alta tool reached through an MCP server of the same application is still the alta tool.
            return string.Equals(mcpTool, "alta", StringComparison.OrdinalIgnoreCase) ? ToolKind.Alta : ToolKind.Mcp;
        }

        if (KindsByName.TryGetValue(trimmed, out var known))
        {
            return known;
        }

        return kind == AgentActivityKind.McpToolCall ? ToolKind.Mcp : ToolKind.Other;
    }

    /// <summary>Splits the name of an MCP tool, <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    /// <param name="name">The name of the tool.</param>
    /// <param name="server">The server.</param>
    /// <param name="tool">The tool.</param>
    /// <returns><see langword="true"/> when the name is that of an MCP tool.</returns>
    public static bool TrySplitMcp(string? name, out string server, out string tool)
    {
        server = string.Empty;
        tool = string.Empty;
        if (name is null || !name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = name.IndexOf("__", 5, StringComparison.Ordinal);
        if (separator <= 5 || separator + 2 >= name.Length)
        {
            return false;
        }

        server = name[5..separator];
        tool = name[(separator + 2)..];
        return true;
    }
}

/// <summary>The names of the providers as the facts keep them.</summary>
internal static class StatisticsProviders
{
    /// <summary>Gets the key facts use for sessions whose provider is not known.</summary>
    public const string Unknown = "unknown";

    /// <summary>Folds the old names of the providers into the current ones: <c>codex_cli</c> into <c>codex</c>, <c>copilot_cli</c> into <c>copilot</c>.</summary>
    /// <param name="providerKey">The provider key as a session wrote it.</param>
    /// <returns>The key to count under; <see cref="Unknown"/> for none.</returns>
    public static string Fold(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return Unknown;
        }

        return providerKey switch
        {
            "codex_cli" => "codex",
            "copilot_cli" => "copilot",
            _ => providerKey,
        };
    }
}
