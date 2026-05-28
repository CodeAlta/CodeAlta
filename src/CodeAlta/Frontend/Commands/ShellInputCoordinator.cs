namespace CodeAlta.Frontend.Commands;

internal sealed class ShellInputCoordinator
{
    private readonly ShellInputRouter _router;
    private readonly Func<string?> _getPromptText;
    private readonly Func<bool> _isCurrentPromptEmpty;
    private readonly IShellCommandDispatcher _dispatcher;

    public ShellInputCoordinator(
        ShellInputRouter router,
        Func<string?> getPromptText,
        Func<bool> isCurrentPromptEmpty,
        IShellCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(isCurrentPromptEmpty);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _router = router;
        _getPromptText = getPromptText;
        _isCurrentPromptEmpty = isCurrentPromptEmpty;
        _dispatcher = dispatcher;
    }

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => HandleInputAsync(_getPromptText(), steer, cancellationToken);

    public Task AbortSelectedSessionAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new AbortSelectedSessionCommand(), cancellationToken);

    public Task CompactSelectedSessionAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new CompactSelectedSessionCommand(), cancellationToken);

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => DispatchAsync(new OpenHelpCommand(filterText), cancellationToken);

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new CloseCurrentTabCommand(), cancellationToken);

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => HandleInputAsync(rawInput, steer: false, cancellationToken);

    public async Task HandleInputAsync(
        string? rawInput,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var intent = _router.Route(rawInput, steer);
        if (intent is EmptyShellInputIntent && !_isCurrentPromptEmpty())
        {
            await _dispatcher.DispatchAsync(new SubmitPromptCommand(rawInput, steer), cancellationToken);
            return;
        }

        await ExecuteIntentAsync(intent, cancellationToken);
    }

    private async Task ExecuteIntentAsync(ShellInputIntent intent, CancellationToken cancellationToken)
    {
        ShellCommand? command = intent switch
        {
            EmptyShellInputIntent => null,
            SendPromptIntent send => new SubmitPromptCommand(send.PromptText, Steer: false),
            SteerPromptIntent steer => new SubmitPromptCommand(steer.PromptText, Steer: true),
            AbortSessionIntent => new AbortSelectedSessionCommand(),
            CompactSessionIntent => new CompactSelectedSessionCommand(),
            CloseTabIntent => new CloseCurrentTabCommand(),
            TabLeftIntent => new SelectRelativeTabCommand(-1),
            TabRightIntent => new SelectRelativeTabCommand(1),
            MessagePreviousIntent => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Previous),
            MessageNextIntent => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Next),
            MessageFirstIntent => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.First),
            MessageLastIntent => new ScrollSelectedSessionMessageCommand(SessionMessageScrollTarget.Last),
            OpenHelpIntent help => new OpenHelpCommand(help.FilterText),
            OpenCommandPaletteIntent => new OpenCommandPaletteCommand(),
            ExitAppIntent => new ExitAppCommand(),
            ToggleCommandBarMultiLineIntent => new ToggleCommandBarMultiLineCommand(),
            OpenFolderIntent openFolder => new OpenFolderCommand(openFolder.InitialPath),
            OpenAboutIntent => new OpenAboutCommand(),
            OpenModelProvidersIntent => new OpenModelProvidersCommand(),
            OpenModelsIntent => new OpenModelsCommand(),
            OpenApplicationLogsIntent => new OpenApplicationLogsCommand(),
            OpenFileEditorIntent => new OpenFileEditorCommand(),
            OpenSkillsIntent => new OpenSkillsCommand(),
            OpenPluginsIntent => new OpenPluginsCommand(),
            OpenMcpServersIntent => new OpenMcpServersCommand(),
            OpenWorkspaceSettingsIntent => new OpenWorkspaceSettingsCommand(),
            FocusSidebarIntent => new FocusSidebarCommand(),
            FocusPromptIntent => new FocusPromptCommand(),
            FocusModelProviderIntent => new FocusModelProviderCommand(),
            OpenSessionUsageIntent => new OpenSessionUsageCommand(),
            OpenSessionInfoIntent => new OpenSessionInfoCommand(),
            OpenExpandedPromptIntent => new OpenExpandedPromptCommand(),
            UnknownTextCommandIntent unknown => new ExecutePluginTextCommand(unknown.CommandName, unknown.Arguments),
            ClearQueueIntent => new ClearSelectedSessionQueueCommand(),
            _ => throw new InvalidOperationException($"Unsupported shell input intent: {intent.GetType().Name}"),
        };

        if (command is not null)
        {
            await _dispatcher.DispatchAsync(command, cancellationToken);
        }
    }

    private async Task DispatchAsync(ShellCommand command, CancellationToken cancellationToken)
        => await _dispatcher.DispatchAsync(command, cancellationToken);
}
