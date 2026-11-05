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
/// <c>altacli</c> has them, and the statistics of its sessions. The browser comes with its own part of the fleet.
/// </summary>
internal static class NodeBuiltInPlugins
{
    /// <summary>The id of the Statistics plugin.</summary>
    public const string StatisticsId = "statistics";

    /// <summary>The built-in plugins of a node whose sessions are in the state root of <paramref name="catalog"/>.</summary>
    /// <param name="catalog">The folders of the node.</param>
    /// <returns>The plugins: <see cref="All"/> and the Statistics plugin, which counts the sessions of the node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public static IReadOnlyList<BuiltInPluginDefinition> For(CodeAlta.Catalog.CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var journals = new CodeAlta.Agent.Runtime.FileSystemSessionJournalCatalog(new CodeAlta.Agent.Runtime.AgentRuntimePathLayout(catalog.StateRoot));
        return
        [
            .. All,
            new()
            {
                Id = StatisticsId, DisplayName = "Statistics",
                Description = "Keeps the statistics of the sessions of the node, which it publishes to the servers of its spaces.",
                PluginType = typeof(CodeAlta.Plugin.Statistics.StatisticsPlugin),
                Factory = () => CodeAlta.Plugin.Statistics.StatisticsPlugin.CreateForHost(journals),
                Frontends = PluginFrontends.All | PluginFrontends.Cli,
            },
        ];
    }

    /// <summary>The built-in plugins of a node that need nothing of its folders.</summary>
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
