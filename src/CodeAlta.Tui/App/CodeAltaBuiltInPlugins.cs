using CodeAlta.Catalog;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugin.CodexCli;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugin.Jira;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

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
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "mcp",
            DisplayName = "MCP",
            Description = SR.T("Connects CodeAlta to configured Model Context Protocol servers."),
            EnabledByDefault = true,
            PluginType = typeof(McpPlugin),
            Factory = static () => new McpPlugin(McpTerminalContributions.CreatePresentation),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = "jira",
            DisplayName = "Jira",
            Description = SR.T("The issues of a Jira project, for the projects whose configuration names one."),
            EnabledByDefault = true,
            PluginType = typeof(JiraPlugin),
            Factory = static () => new JiraPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
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
        registry.Add(new BuiltInPluginDefinition
        {
            Id = AcpPlugin.PluginId,
            DisplayName = "ACP",
            Description = SR.T("Runs the agents of the Agent Client Protocol (ACP) as model providers."),
            EnabledByDefault = true,
            PluginType = typeof(AcpPlugin),
            Factory = static () => new AcpPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        });
        registry.Add(new BuiltInPluginDefinition
        {
            Id = CodexCliPlugin.PluginId,
            DisplayName = "Codex CLI",
            Description = SR.T("Runs OpenAI's Codex app-server as a model provider."),
            EnabledByDefault = true,
            PluginType = typeof(CodexCliPlugin),
            Factory = static () => new CodexCliPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        });
        return registry;
    }
}
