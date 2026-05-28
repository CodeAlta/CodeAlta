namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandSurfaceCoordinator
{
    private readonly Action _toggleCommandBarMultiLine;
    private readonly IShellCommandSurfacePresenter _presenter;
    private readonly IShellCommandDispatcher _shellCommandDispatcher;
    private readonly ShellCommandBindingProjector _bindingProjector;
    private readonly ShellInputCoordinator _shellInputCoordinator;

    public ShellCommandSurfaceCoordinator(
        IShellPromptInputService promptInputService,
        IShellCommandDispatcher shellCommandDispatcher,
        ShellCommandBindingProjector bindingProjector,
        IShellCommandSurfacePresenter presenter,
        Action toggleCommandBarMultiLine)
    {
        ArgumentNullException.ThrowIfNull(promptInputService);
        ArgumentNullException.ThrowIfNull(shellCommandDispatcher);
        ArgumentNullException.ThrowIfNull(bindingProjector);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(toggleCommandBarMultiLine);

        _toggleCommandBarMultiLine = toggleCommandBarMultiLine;
        _presenter = presenter;
        _shellCommandDispatcher = shellCommandDispatcher;
        _bindingProjector = bindingProjector;
        _shellInputCoordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            promptInputService.GetPromptText,
            promptInputService.IsCurrentPromptEmpty,
            _shellCommandDispatcher);
    }

    public IReadOnlyList<SessionWorkspaceCommandBinding> BuildWorkspaceCommandBindings()
        => _bindingProjector.BuildWorkspaceCommandBindings();

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => _shellInputCoordinator.HandleAcceptedPromptAsync(rawInput, cancellationToken);

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => _shellInputCoordinator.SubmitCurrentPromptAsync(steer, cancellationToken);

    public Task AbortSelectedSessionAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.AbortSelectedSessionAsync(cancellationToken);

    public Task CompactSelectedSessionAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.CompactSelectedSessionAsync(cancellationToken);

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.CloseCurrentTabAsync(cancellationToken);

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => DispatchShellCommandAsync(new OpenHelpCommand(filterText), cancellationToken);

    public void ShowCommandPalette()
        => _presenter.ShowCommandPalette();

    public Task ShowCommandPaletteAsync()
        => DispatchShellCommandAsync(new OpenCommandPaletteCommand());

    public Task ExitAppAsync()
        => DispatchShellCommandAsync(new ExitAppCommand());

    public Task FocusSidebarAsync()
        => DispatchShellCommandAsync(new FocusSidebarCommand());

    public Task FocusPromptAsync()
        => DispatchShellCommandAsync(new FocusPromptCommand());

    public Task FocusModelProviderAsync()
        => DispatchShellCommandAsync(new FocusModelProviderCommand());

    public void ToggleCommandBarMultiLine()
        => _toggleCommandBarMultiLine();

    public Task ShowOpenFolderDialogAsync(string? initialPath = null)
        => DispatchShellCommandAsync(new OpenFolderCommand(initialPath));

    public Task OpenModelProvidersAsync()
        => DispatchShellCommandAsync(new OpenModelProvidersCommand());

    public Task OpenAboutAsync()
        => DispatchShellCommandAsync(new OpenAboutCommand());

    public Task OpenApplicationLogsAsync()
        => DispatchShellCommandAsync(new OpenApplicationLogsCommand());

    public Task OpenFileEditorAsync()
        => DispatchShellCommandAsync(new OpenFileEditorCommand());

    public Task OpenSkillsAsync()
        => DispatchShellCommandAsync(new OpenSkillsCommand());

    public Task OpenPluginsAsync()
        => DispatchShellCommandAsync(new OpenPluginsCommand());

    public Task OpenMcpServersAsync()
        => DispatchShellCommandAsync(new OpenMcpServersCommand());

    public Task OpenWorkspaceSettingsAsync()
        => DispatchShellCommandAsync(new OpenWorkspaceSettingsCommand());

    private async Task DispatchShellCommandAsync(ShellCommand command, CancellationToken cancellationToken = default)
        => await _shellCommandDispatcher.DispatchAsync(command, cancellationToken);

    internal static string BuildUnknownCommandStatus(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        return $"Unknown command '/{commandName}'. Press F1 or type /help.";
    }
}
