using CodeAlta.LiveTool;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Plugins;

namespace CodeAlta.Tui.App;

/// <summary>
/// The services the terminal application gives its plugins: the <c>alta</c> commands and the window
/// (dialogs, the selected session and the prompt). Both exist before the application does and are bound to
/// it once it runs, because plugins start first.
/// </summary>
internal sealed class CodeAltaPluginServices(PluginAltaServiceBridge alta, TerminalPluginUi ui, IPluginServices? inner = null) : IPluginServices
{
    private readonly IPluginServices _inner = inner ?? NoopPluginServices.Create();

    /// <summary>Creates the services with a new bridge and a new window service.</summary>
    public CodeAltaPluginServices()
        : this(new PluginAltaServiceBridge(), new TerminalPluginUi())
    {
    }

    /// <summary>Gets the bridge that the application binds to its <c>alta</c> dispatcher.</summary>
    public PluginAltaServiceBridge AltaBridge { get; } = alta ?? throw new ArgumentNullException(nameof(alta));

    /// <summary>Gets the window service that the application attaches itself to.</summary>
    public TerminalPluginUi TerminalUi { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    public XenoAtom.Logging.Logger Logger => _inner.Logger;

    public IPluginUiService Ui => TerminalUi;

    public IPluginStateStore State => _inner.State;

    public IPluginDatabase Database => _inner.Database;

    public IPluginWorkspaceService Workspace => _inner.Workspace;

    public IPluginSessionService Sessions => TerminalUi;

    public IPluginPromptService Prompts => TerminalUi;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta => AltaBridge;
}
