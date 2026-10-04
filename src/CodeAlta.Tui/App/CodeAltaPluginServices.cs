using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tui.App;

internal sealed class CodeAltaPluginServices(IPluginAltaService alta, IPluginServices? inner = null) : IPluginServices
{
    private readonly IPluginServices _inner = inner ?? NoopPluginServices.Create();

    public XenoAtom.Logging.Logger Logger => _inner.Logger;

    public IPluginUiService Ui => _inner.Ui;

    public IPluginStateStore State => _inner.State;

    public IPluginWorkspaceService Workspace => _inner.Workspace;

    public IPluginSessionService Sessions => _inner.Sessions;

    public IPluginPromptService Prompts => _inner.Prompts;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = alta;
}
