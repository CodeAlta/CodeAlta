using CodeAlta.LiveTool;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Node.Plugins;

/// <summary>
/// What a node gives its plugins: the services that need no window. The <c>alta</c> commands, the private networks of the
/// machine, the files of the page of the phones, and the permission requests of its sessions, which only the built-in
/// Mobile plugin reads and answers (<see cref="PluginPermissionServiceBridge.ForPlugin"/>). The rest is what a host without
/// a user interface gives: no window, no tab, and no question to answer, since the agents of a node ask none.
/// </summary>
internal sealed class NodePluginServices(PluginAltaServiceBridge alta, IPluginAppFileService appFiles, IPluginPrivateNetworkService privateNetworks,
    PluginPermissionServiceBridge permissions) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();

    /// <summary>Gets the bridge to the <c>alta</c> commands, attached once the host runs.</summary>
    public PluginAltaServiceBridge AltaBridge { get; } = alta ?? throw new ArgumentNullException(nameof(alta));

    /// <summary>Gets the permission requests of the sessions, attached once the host runs.</summary>
    public PluginPermissionServiceBridge PermissionBridge { get; } = permissions ?? throw new ArgumentNullException(nameof(permissions));

    internal NodeMobileAlta PersonalAlta { get; } = new(alta);

    /// <inheritdoc />
    public Logger Logger => _inner.Logger;

    /// <inheritdoc />
    public IPluginUiService Ui => _inner.Ui;

    /// <inheritdoc />
    public IPluginStateStore State => _inner.State;

    /// <inheritdoc />
    public IPluginDatabase Database => _inner.Database;

    /// <inheritdoc />
    public IPluginWorkspaceService Workspace => _inner.Workspace;

    /// <inheritdoc />
    public IPluginSessionService Sessions => _inner.Sessions;

    /// <inheritdoc />
    public IPluginPromptService Prompts => _inner.Prompts;

    /// <inheritdoc />
    public IPluginAgentService Agents => _inner.Agents;

    /// <inheritdoc />
    public IPluginTaskService Tasks => _inner.Tasks;

    /// <inheritdoc />
    public IPluginAltaService Alta => PersonalAlta;

    /// <inheritdoc />
    public IPluginAppFileService AppFiles { get; } = appFiles ?? throw new ArgumentNullException(nameof(appFiles));

    /// <inheritdoc />
    public IPluginPrivateNetworkService PrivateNetworks { get; } = privateNetworks ?? throw new ArgumentNullException(nameof(privateNetworks));

    /// <inheritdoc />
    public IPluginPermissionService Permissions => PermissionBridge;
}
