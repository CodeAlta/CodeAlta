using CodeAlta.Catalog;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins;

namespace CodeAlta.Tui.App;

internal static class CodeAltaBuiltInPlugins
{
    public static IReadOnlyList<BuiltInPluginDefinition> All { get; } = CreateRegistry().GetDefinitions();

    private static BuiltInPluginRegistry CreateRegistry()
    {
        var registry = new BuiltInPluginRegistry();
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "git",
            DisplayName = "Git",
            Description = SR.T("Adds an issue prompt picker for GitHub, GitLab and Azure DevOps repositories and exposes their CLIs (gh, glab, az) when available."),
            EnabledByDefault = true,
            PluginType = typeof(GitPlugin),
            Factory = static () => new GitPlugin(GitTerminalContributions.CreatePromptEditorContributions),
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "mcp",
            DisplayName = "MCP",
            Description = SR.T("Connects CodeAlta to configured Model Context Protocol servers."),
            EnabledByDefault = true,
            PluginType = typeof(McpPlugin),
            Factory = static () => new McpPlugin(McpTerminalContributions.CreatePresentation),
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "statistics",
            DisplayName = SR.T("Statistics"),
            Description = SR.T("Projects transient per-turn and session statistics from normalized agent events."),
            EnabledByDefault = true,
            PluginType = typeof(StatisticsPlugin),
            Factory = static () => new StatisticsPlugin(StatisticsTerminalContributions.DecorateProjection),
        });
        return registry;
    }
}
