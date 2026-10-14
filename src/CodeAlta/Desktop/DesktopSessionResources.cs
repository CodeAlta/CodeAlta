using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop;

/// <summary>What a session can use: the resources of its space that are turned on, by kind. Names only, never a secret.</summary>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="McpServers">The MCP servers it can activate.</param>
/// <param name="Skills">The skills it is offered.</param>
/// <param name="Prompts">The agent prompts it can run with.</param>
/// <param name="Plugins">The plugins that add to it.</param>
internal sealed record SessionResources(string SpaceId, IReadOnlyList<string> McpServers, IReadOnlyList<string> Skills, IReadOnlyList<string> Prompts,
    IReadOnlyList<string> Plugins);

/// <summary>Reads what a session can use, for the panel that shows a session: the same filters as its runs.</summary>
internal sealed class DesktopSessionResources
{
    /// <summary>The most names one kind lists.</summary>
    internal const int MaximumNames = 128;

    private readonly CodeAltaHost _host;
    private readonly string? _home;

    /// <summary>Initializes a new instance of the <see cref="DesktopSessionResources"/> class.</summary>
    /// <param name="host">The host of the window.</param>
    /// <param name="home">The home folder the MCP servers of the user are read from; null for the one of the process.</param>
    internal DesktopSessionResources(CodeAltaHost host, string? home)
    {
        ArgumentNullException.ThrowIfNull(host);
        (_host, _home) = (host, home);
    }

    /// <summary>Reads what a session can use.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>Its resources; null when no such session is known.</returns>
    /// <exception cref="InvalidDataException">A configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">A configuration file cannot be read.</exception>
    internal async Task<SessionResources?> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var runtime = _host.RuntimeService;
        if (await runtime.GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } record) return null;
        var project = string.IsNullOrWhiteSpace(record.ProjectId) ? null
            : await _host.ProjectCatalog.GetByIdAsync(record.ProjectId, cancellationToken).ConfigureAwait(false);
        var space = await runtime.GetSpaceFilterAsync(record.SpaceId, project, cancellationToken).ConfigureAwait(false);
        var profile = _home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var skillQuery = SkillCatalogQuery.ForSession(new CodeAltaConfigStore(_host.CatalogOptions), _host.CatalogOptions.GlobalRoot, profile,
            project?.ProjectPath, otherProjectRoots: null, space) with { IncludeInvalid = false, IncludeShadowed = false, IncludeUntrusted = false, ModelVisibleOnly = true };
        var skills = (await _host.SkillCatalog.ListAsync(skillQuery, cancellationToken).ConfigureAwait(false)).Select(static skill => skill.Name);

        var prompts = AgentPromptCatalog.InSpace(new AgentPromptCatalog().ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            ProjectRoot = project?.ProjectPath,
            ProjectPromptResourcesTrusted = project is not null,
            UserCodeAltaRoot = _host.CatalogOptions.GlobalRoot,
            UserProfileRoot = profile,
        }), space).Select(static prompt => prompt.DisplayName);

        var mcp = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project?.ProjectPath, UserHomeDirectory = _home, ProbeWritability = false });
        var servers = mcp.Policy.Enabled
            ? mcp.Servers.Where(server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false
                && space.Allows(SpaceResourceKind.McpServer, server.Key)).Select(static server => server.Key)
            : [];

        var adapter = _host.PluginRuntime.Adapter;
        var plugins = _host.PluginRuntime.ActivePlugins
            .Where(plugin => plugin.Instance is not null && plugin.RuntimeContext.IsValid
                && adapter.IsAvailableInSpace(PluginContributionRegistry.PluginIdOf(plugin.Descriptor.RuntimeKey, plugin.SourcePackage?.PackageId), plugin.RuntimeContext.Scope, space.SpaceId)
                && plugin.RuntimeContext.AppliesToProject(project?.Id, project?.ProjectPath))
            .Select(static plugin => plugin.Descriptor.DisplayName ?? plugin.Descriptor.RuntimeKey);

        static IReadOnlyList<string> Names(IEnumerable<string> names)
            => [.. names.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Take(MaximumNames)];
        return new(space.SpaceId, Names(servers), Names(skills), Names(prompts), Names(plugins));
    }
}
