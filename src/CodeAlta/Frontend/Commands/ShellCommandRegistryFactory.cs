using CodeAlta.App;

namespace CodeAlta.Frontend.Commands;

internal interface IShellCommandSurfacePresenter
{
    Task ShowHelpDialogAsync(string? filterText = null);

    void ShowCommandPalette();

    void ShowOpenFolderDialog(string? initialPath = null);
}

internal sealed class ShellCommandRegistryFactory
{
    private readonly SessionCommandCoordinator _sessionCommands;
    private readonly IShellDialogCommandService _dialogCommandService;
    private readonly IShellNavigationCommandService _navigationCommandService;
    private readonly IShellTabCommandService _tabCommandService;
    private readonly IShellStatusService _statusService;
    private readonly IPluginCommandService _pluginCommandService;

    public ShellCommandRegistryFactory(
        SessionCommandCoordinator sessionCommands,
        IShellDialogCommandService dialogCommandService,
        IShellNavigationCommandService navigationCommandService,
        IShellTabCommandService tabCommandService,
        IShellStatusService statusService,
        IPluginCommandService pluginCommandService)
    {
        ArgumentNullException.ThrowIfNull(sessionCommands);
        ArgumentNullException.ThrowIfNull(dialogCommandService);
        ArgumentNullException.ThrowIfNull(navigationCommandService);
        ArgumentNullException.ThrowIfNull(tabCommandService);
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(pluginCommandService);
        _sessionCommands = sessionCommands;
        _dialogCommandService = dialogCommandService;
        _navigationCommandService = navigationCommandService;
        _tabCommandService = tabCommandService;
        _statusService = statusService;
        _pluginCommandService = pluginCommandService;
    }

    public ShellCommandRegistry Create(IShellCommandSurfacePresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        var registry = new ShellCommandRegistry();
        registry.RegisterFactory("CodeAlta.Shell.Help", static () => new OpenHelpCommand());
        registry.RegisterFactory("CodeAlta.Shell.ToggleCommandBarMultiLine", static () => new ToggleCommandBarMultiLineCommand());
        registry.RegisterFactory("CodeAlta.Project.OpenFolder", static () => new OpenFolderCommand());
        registry.RegisterFactory("CodeAlta.Shell.About", static () => new OpenAboutCommand());
        registry.RegisterFactory("CodeAlta.Providers.Manage", static () => new OpenModelProvidersCommand());
        registry.RegisterFactory("CodeAlta.Models.Browse", static () => new OpenModelsCommand());
        registry.RegisterFactory("CodeAlta.ApplicationLogs.Open", static () => new OpenApplicationLogsCommand());
        registry.RegisterFactory("CodeAlta.File.Edit", static () => new OpenFileEditorCommand());
        registry.RegisterFactory("CodeAlta.Skills.Manage", static () => new OpenSkillsCommand());
        registry.RegisterFactory("CodeAlta.Plugins.Manage", static () => new OpenPluginsCommand());
        registry.RegisterFactory("CodeAlta.Mcp.Manage", static () => new OpenMcpServersCommand());
        registry.RegisterFactory("CodeAlta.Workspace.Settings", static () => new OpenWorkspaceSettingsCommand());
        registry.RegisterFactory("CodeAlta.Session.SessionUsage", static () => new OpenSessionUsageCommand());
        registry.RegisterFactory("CodeAlta.Session.Info", static () => new OpenSessionInfoCommand());
        registry.RegisterFactory("CodeAlta.Session.ExpandPrompt", static () => new OpenExpandedPromptCommand());
        registry.RegisterFactory("CodeAlta.Session.Send", static () => new SubmitPromptCommand(null, Steer: false));
        registry.RegisterFactory("CodeAlta.Session.Steer", static () => new SubmitPromptCommand(null, Steer: true));
        registry.RegisterFactory("CodeAlta.Session.Abort", static () => new AbortSelectedSessionCommand());
        registry.RegisterFactory("CodeAlta.Session.ClearQueue", static () => new ClearSelectedSessionQueueCommand());
        registry.RegisterFactory("CodeAlta.Session.Compact", static () => new CompactSelectedSessionCommand());
        registry.RegisterFactory("CodeAlta.Session.CloseTab", static () => new CloseCurrentTabCommand());
        registry.RegisterFactory("CodeAlta.Session.TabLeft", static () => new SelectRelativeTabCommand(-1));
        registry.RegisterFactory("CodeAlta.Session.TabRight", static () => new SelectRelativeTabCommand(1));
        registry.RegisterFactory("CodeAlta.Session.MessagePrevious", static () => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Previous));
        registry.RegisterFactory("CodeAlta.Session.MessageNext", static () => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Next));
        registry.RegisterFactory("CodeAlta.Session.MessageFirst", static () => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.First));
        registry.RegisterFactory("CodeAlta.Session.MessageLast", static () => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Last));

        PromptCommandHandlers.Register(registry, _sessionCommands);
        SessionCommandHandlers.Register(registry, _sessionCommands);
        NavigationCommandHandlers.Register(registry, _navigationCommandService);
        DialogCommandHandlers.Register(
            registry,
            presenter.ShowHelpDialogAsync,
            presenter.ShowCommandPalette,
            presenter.ShowOpenFolderDialog,
            _dialogCommandService);
        TabCommandHandlers.Register(registry, _tabCommandService);
        PluginCommandHandlers.Register(registry, _pluginCommandService, _sessionCommands, _statusService);
        return registry;
    }
}
