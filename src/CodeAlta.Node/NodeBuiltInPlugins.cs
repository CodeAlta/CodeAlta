using CodeAlta.Plugin.Acp;
using CodeAlta.Plugin.CodexCli;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Jira;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node;

/// <summary>
/// The built-in plugins of a node: those that run without a window (Git, Jira, MCP, ACP, Codex CLI), as
/// <c>altacli</c> has them. The browser and the statistics come with their own parts of the fleet.
/// </summary>
internal static class NodeBuiltInPlugins
{
    public static IReadOnlyList<BuiltInPluginDefinition> All { get; } =
    [
        new()
        {
            Id = "git", DisplayName = "Git",
            Description = "Exposes the GitHub, GitLab and Azure DevOps CLIs (gh, glab, az) to sessions when they are available.",
            PluginType = typeof(GitPlugin), Factory = static () => new GitPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = "mcp", DisplayName = "MCP",
            Description = "Connects CodeAlta to configured Model Context Protocol servers.",
            // No window shows the views of MCP Apps, and the skills of servers, whose load always asks, are left out
            // until the node can route the question to a person.
            PluginType = typeof(McpPlugin), Factory = static () => new McpPlugin(new McpPluginOptions { ShowsApps = false, LoadsSkills = false }),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = "jira", DisplayName = "Jira",
            Description = "The issues of a Jira project, for the projects whose configuration names one.",
            PluginType = typeof(JiraPlugin), Factory = static () => new JiraPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = AcpPlugin.PluginId, DisplayName = "ACP",
            Description = "Runs the agents of the Agent Client Protocol (ACP) as model providers.",
            PluginType = typeof(AcpPlugin), Factory = static () => new AcpPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = CodexCliPlugin.PluginId, DisplayName = "Codex CLI",
            Description = "Runs OpenAI's Codex app-server as a model provider.",
            PluginType = typeof(CodexCliPlugin), Factory = static () => new CodexCliPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
    ];
}
