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
    private readonly ThreadCommandCoordinator _threadCommands;
    private readonly IShellThreadCommandService _threadCommandService;
    private readonly IShellDialogCommandService _dialogCommandService;
    private readonly IShellNavigationCommandService _navigationCommandService;
    private readonly IShellTabCommandService _tabCommandService;
    private readonly IShellStatusService _statusService;
    private readonly IPluginCommandService _pluginCommandService;

    public ShellCommandRegistryFactory(
        ThreadCommandCoordinator threadCommands,
        IShellThreadCommandService threadCommandService,
        IShellDialogCommandService dialogCommandService,
        IShellNavigationCommandService navigationCommandService,
        IShellTabCommandService tabCommandService,
        IShellStatusService statusService,
        IPluginCommandService pluginCommandService)
    {
        ArgumentNullException.ThrowIfNull(threadCommands);
        ArgumentNullException.ThrowIfNull(threadCommandService);
        ArgumentNullException.ThrowIfNull(dialogCommandService);
        ArgumentNullException.ThrowIfNull(navigationCommandService);
        ArgumentNullException.ThrowIfNull(tabCommandService);
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(pluginCommandService);
        _threadCommands = threadCommands;
        _threadCommandService = threadCommandService;
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
        registry.RegisterFactory("CodeAlta.Acp.Manage", static () => new OpenAcpManagementCommand());
        registry.RegisterFactory("CodeAlta.Providers.Manage", static () => new OpenModelProvidersCommand());
        registry.RegisterFactory("CodeAlta.Models.Browse", static () => new OpenModelsCommand());
        registry.RegisterFactory("CodeAlta.File.Edit", static () => new OpenFileEditorCommand());
        registry.RegisterFactory("CodeAlta.Skills.Manage", static () => new OpenSkillsCommand());
        registry.RegisterFactory("CodeAlta.Plugins.Manage", static () => new OpenPluginsCommand());
        registry.RegisterFactory("CodeAlta.Workspace.Settings", static () => new OpenWorkspaceSettingsCommand());
        registry.RegisterFactory("CodeAlta.Thread.SessionUsage", static () => new OpenSessionUsageCommand());
        registry.RegisterFactory("CodeAlta.Thread.Info", static () => new OpenThreadInfoCommand());
        registry.RegisterFactory("CodeAlta.Thread.ExpandPrompt", static () => new OpenExpandedPromptCommand());
        registry.RegisterFactory("CodeAlta.Thread.Send", static () => new SubmitPromptCommand(null, Steer: false));
        registry.RegisterFactory("CodeAlta.Thread.Steer", static () => new SubmitPromptCommand(null, Steer: true));
        registry.RegisterFactory("CodeAlta.Thread.Abort", static () => new AbortSelectedThreadCommand());
        registry.RegisterFactory("CodeAlta.Thread.Queue", static () => new ShowQueueStatusCommand());
        registry.RegisterFactory("CodeAlta.Thread.ClearQueue", static () => new ClearSelectedThreadQueueCommand());
        registry.RegisterFactory("CodeAlta.Thread.Compact", static () => new CompactSelectedThreadCommand());
        registry.RegisterFactory("CodeAlta.Thread.CloseTab", static () => new CloseCurrentTabCommand());
        registry.RegisterFactory("CodeAlta.Thread.TabLeft", static () => new SelectRelativeTabCommand(-1));
        registry.RegisterFactory("CodeAlta.Thread.TabRight", static () => new SelectRelativeTabCommand(1));
        registry.RegisterFactory("CodeAlta.Thread.MessagePrevious", static () => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Previous));
        registry.RegisterFactory("CodeAlta.Thread.MessageNext", static () => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Next));
        registry.RegisterFactory("CodeAlta.Thread.MessageFirst", static () => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.First));
        registry.RegisterFactory("CodeAlta.Thread.MessageLast", static () => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Last));

        PromptCommandHandlers.Register(registry, _threadCommands);
        ThreadCommandHandlers.Register(registry, _threadCommands, _threadCommandService, _statusService);
        NavigationCommandHandlers.Register(registry, _navigationCommandService);
        DialogCommandHandlers.Register(
            registry,
            presenter.ShowHelpDialogAsync,
            presenter.ShowCommandPalette,
            presenter.ShowOpenFolderDialog,
            _dialogCommandService);
        TabCommandHandlers.Register(registry, _tabCommandService);
        PluginCommandHandlers.Register(registry, _pluginCommandService, _threadCommands, _statusService);
        return registry;
    }
}
